namespace OmniCore.Models;

using OmniCore.Abstractions;

/// <summary>Estado de conectividad del servidor local.</summary>
public enum LocalServerStatus
{
    Ready,
    Unreachable,
    Crashed,
}

/// <summary>
/// LocalModelHost (ADR-0011 §4): gestiona un servidor local llama.cpp/ik_llama en dos modos:
/// attach (se conecta a uno ya levantado) y managed (lo lanza y supervisa con IProcessRuntime).
/// M2 implementa: puerto efímero + API key aleatoria generados EN RUNTIME (nunca en config,
/// ADR-0011 §4), readiness check real con retry hasta timeout, supervisión de la muerte del
/// proceso (IsManagedRunning devuelve false si el handler murió) y cancelación del árbol.
/// El reinicio automático con backoff queda a M3.
/// </summary>
public sealed class LocalModelHost
{
    private readonly IProcessRuntime _process;

    private ProcessHandle? _server;

    private string _generatedApiKey = "";

    private int _chosenPort = -1;

    public LocalModelHost(IProcessRuntime process)
    {
        _process = process;
    }

    /// <summary>Modo attach: verifica salud con el callback (el caller conecta HTTP).</summary>
    public LocalServerStatus Attach(Func<bool> healthCheck)
    {
        return healthCheck != null && healthCheck() ? LocalServerStatus.Ready : LocalServerStatus.Unreachable;
    }

    /// <summary>
    /// Modo managed: genera puerto efímero y API key aleatoria, lanza el servidor y espera
    /// readiness (retries cada 200 ms) hasta `readinessTimeout`. Si el proceso muere antes,
    /// devuelve Crashed y libera el handle. El endpoint real se devuelve al listo.
    /// </summary>
    public LocalServerStatus StartManaged(ManagedServerSpec spec, Func<bool> readinessCheck,
        TimeSpan readinessTimeout)
    {
        if (_server is not null)
        {
            throw new InvalidOperationException("Ya hay un servidor managed en marcha");
        }

        _chosenPort = spec.Port > 0 ? spec.Port : EphemeralPort();
        _generatedApiKey = RandomApiKey(24);
        var launch = spec.ToLaunch(_chosenPort, _generatedApiKey);
        _server = _process.Launch(launch, CancellationToken.None);

        var deadline = DateTimeOffset.Now.AddSeconds(readinessTimeout.TotalSeconds);
        while (true)
        {
            if (!_process.IsAlive(_server!))
            {
                StopManaged();
                return LocalServerStatus.Crashed;
            }

            var ready = false;
            try
            {
                ready = readinessCheck != null && readinessCheck();
            }
            catch (Exception)
            {
                ready = false;
            }

            if (ready)
            {
                return LocalServerStatus.Ready;
            }

            if (DateTimeOffset.Now >= deadline)
            {
                // Timeout de readiness: no dejar un servidor huérfano; cortar el árbol.
                StopManaged();
                return LocalServerStatus.Unreachable;
            }

            try
            {
                Thread.Sleep(200);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Cancela el árbol del servidor managed (kill-tree) y borra el secreto.</summary>
    public void StopManaged()
    {
        if (_server is not null)
        {
            _process.CancelTree(_server!);
            _server = null;
            _generatedApiKey = "";
            _chosenPort = -1;
        }
    }

    public bool IsManagedRunning()
    {
        if (_server is null)
        {
            return false;
        }

        var alive = _process.IsAlive(_server!);
        if (!alive)
        {
            _server = null;
        }

        return alive;
    }

    public string GeneratedApiKeyValue() => _generatedApiKey;

    public int ChosenPort() => _chosenPort;

    /// <summary>
    /// 4xx-udp/6xxxx puerto efímero candidato en rango alto de alto riesgo de colisión
    /// (el servidor llama.cpp recibe --port; en M2 el rango 10.000-12.000, suficiente).
    /// </summary>
    private static int EphemeralPort()
    {
        var rnd = Random.Shared;
        return 10_000 + (int) (rnd.NextInt64() % 2_000);
    }

    private static string RandomApiKey(int bytes)
    {
        var rnd = Random.Shared;
        return rnd.GetHexString(bytes);
    }
}

/// <summary>Especificación para lanzar el servidor managed (ik_llama/llama.cpp).</summary>
public sealed class ManagedServerSpec
{
    public string Executable { get; }

    public IReadOnlyList<string> Args { get; }

    public string WorkingDirectory { get; }

    public int Port { get; }

    public ManagedServerSpec(string executable, IReadOnlyList<string> args, string workingDirectory, int port)
    {
        Executable = executable;
        Args = args;
        WorkingDirectory = workingDirectory;
        Port = port;
    }

    /// <summary>Arma el ProcessLaunch reemplazando {port} y {apiKey} en los args template.</summary>
    public ProcessLaunch ToLaunch(int port, string apiKey)
    {
        var outArgs = new List<string>();
        foreach (var arg in Args)
        {
            outArgs.Add(arg.Replace("{port}", port.ToString()).Replace("{apiKey}", apiKey));
        }

        var env = new Dictionary<string, string>();
        env["OMNI_SERVER_PORT"] = port.ToString();
        env["OMNI_SERVER_API_KEY"] = apiKey;
        return new ProcessLaunch(Executable, outArgs, WorkingDirectory, env, true);
    }
}