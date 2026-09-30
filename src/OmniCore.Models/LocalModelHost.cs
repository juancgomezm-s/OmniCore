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
            _generatedApiKey = "";
            _chosenPort = -1;
        }

        return alive;
    }

    public string GeneratedApiKeyValue() => _generatedApiKey;

    public int ChosenPort() => _chosenPort;

    /// <summary>
    /// Puerto efímero real: reserva un socket (TcpListener) para obtener un puerto libre, lo
    /// libera y lo devuelve. La ventana entre el release y el bind del servidor puede colisionar,
    /// pero elimina el "número aleatorio que puede chocar" del enfoque anterior (P2-13).
    /// </summary>
    private static int EphemeralPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>API key aleatoria criptográfica (RandomNumberGenerator, no Random.Shared).</summary>
    private static string RandomApiKey(int bytes) =>
        System.Security.Cryptography.RandomNumberGenerator.GetHexString(bytes * 2);
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

    /// <summary>Arma ProcessLaunch; la API key solo se entrega por el entorno del proceso.</summary>
    public ProcessLaunch ToLaunch(int port, string apiKey)
    {
        var outArgs = new List<string>();
        for (var i = 0; i < Args.Count; i++)
        {
            var arg = Args[i];
            if (arg == "--api-key")
            {
                if (i + 1 < Args.Count) i++; // La API key siempre viaja solo en el entorno del proceso.
                continue;
            }
            if (arg.StartsWith("--api-key=", StringComparison.Ordinal) || arg.Contains("{apiKey}", StringComparison.Ordinal))
            {
                // Omite tanto el placeholder como formas --api-key={apiKey}; el servidor lee env.
                continue;
            }
            outArgs.Add(arg.Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var env = new Dictionary<string, string>();
        env["OMNI_SERVER_PORT"] = port.ToString();
        env["OMNI_SERVER_API_KEY"] = apiKey;
        return new ProcessLaunch(Executable, outArgs, WorkingDirectory, env, true);
    }
}