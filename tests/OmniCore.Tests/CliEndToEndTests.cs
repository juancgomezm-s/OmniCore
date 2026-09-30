using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Cli;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>Smoke tests through the real CLI dispatcher, persistent Host and provider HTTP adapter.</summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CliEndToEndTests
{
    [Fact]
    public async Task M1_to_M4_cli_surface_runs_in_isolated_directories()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), "omnicore-cli-e2e-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);

        var previousDirectory = Environment.CurrentDirectory;
        var priorData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var priorConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var priorLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        var priorModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        OmniCliRuntime? previousRuntime = null;
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable("OMNI_MODEL", null);

            await using var provider = new ScriptedHttpProvider();
            File.WriteAllText(Path.Combine(config, "providers.yaml"),
                $"providers:\n  scripted:\n    family: OpenAiChatCompatible\n    baseUrl: {provider.BaseUrl}\n    auth: none\n");
            File.WriteAllText(Path.Combine(config, "models.yaml"),
                "models:\n  scripted-model:\n    provider: scripted\n    context: 8192\n    maxOutput: 2048\n");
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
            previousRuntime = CliApp.UseRuntimeForTests(OmniCliRuntime.Create(workspace));

            // M1: default and JSON renderers, all checked-in scenario files, and crash/recovery.
            var sim = await Run("sim");
            Assert.Equal(0, sim.Code);
            Assert.Contains("Run iniciado:", sim.Output);
            AssertNoLeaks(sim.Output);

            var simJson = await Run("sim", "--json");
            Assert.Equal(0, simJson.Code);
            Assert.Contains("\"type\"", simJson.Output);
            Assert.Contains("\"outcome\"", simJson.Output);
            AssertNoLeaks(simJson.Output, machineReadable: true);

            var scenarios = Path.Combine(repositoryRoot, "docs", "sim");
            foreach (var file in Directory.GetFiles(scenarios, "*.yaml").OrderBy(path => path, StringComparer.Ordinal))
            {
                var result = await Run("sim", file, "--json");
                // crash-resume.yaml models a successful interrupted state; the explicit --crash
                // flag below is the command-level interrupted exit path.
                const int expectedExit = 0;
                Assert.True(result.Code == expectedExit,
                    $"{Path.GetFileName(file)} expected exit={expectedExit}, actual={result.Code}: {result.Output}");
                Assert.Contains("\"outcome\"", result.Output);
                AssertNoLeaks(result.Output, machineReadable: true);
            }

            var crash = await Run("sim", "--crash", "--json");
            Assert.Equal(1, crash.Code);
            Assert.Contains("Interrupted", crash.Output);
            AssertNoLeaks(crash.Output, machineReadable: true);
            var resumed = await Run("sim", "--resume", "--json");
            Assert.Equal(0, resumed.Code);
            Assert.Contains("sim.resumed", resumed.Output);
            Assert.Contains("\"outcome\"", resumed.Output);
            AssertNoLeaks(resumed.Output, machineReadable: true);

            // M2: actual HTTP Chat Completions provider, plus diagnostics and typed client commands.
            var doctor = await Run("doctor");
            Assert.Equal(0, doctor.Code);
            Assert.Contains("Estado: modelo configurado", doctor.Output);
            AssertNoLeaks(doctor.Output);
            var doctorJournal = await Run("doctor", "--verify-journal");
            Assert.Equal(0, doctorJournal.Code);
            Assert.Contains("verify-journal:", doctorJournal.Output);
            AssertNoLeaks(doctorJournal.Output);

            var ask = await Run("ask", "explica el estado");
            Assert.Equal(0, ask.Code);
            Assert.Contains("respuesta-scripted", ask.Output);
            AssertNoLeaks(ask.Output);

            var explain = await Run("explain", "estado del plan");
            Assert.Equal(0, explain.Code);
            Assert.Contains("Pregunta: estado del plan", explain.Output);
            AssertNoLeaks(explain.Output);

            var context = await Run("/context");
            Assert.Equal(0, context.Code);
            Assert.Contains("Snapshot de contexto", context.Output);
            AssertNoLeaks(context.Output);

            var tools = await Run("/tools");
            Assert.Equal(0, tools.Code);
            Assert.Contains("Tools efectivas", tools.Output);
            AssertNoLeaks(tools.Output);

            var typedExplain = await Run("/explain");
            Assert.Equal(0, typedExplain.Code);
            Assert.Contains("respuesta-scripted", typedExplain.Output);
            AssertNoLeaks(typedExplain.Output);

            // M3: exercise the real Act path and verify the scripted provider response is rendered.
            var act = await Run("act", "implementa el objetivo");
            Assert.Equal(0, act.Code);
            Assert.Contains("respuesta-scripted", act.Output);
            AssertNoLeaks(act.Output);

            var policyList = await Run("model", "list");
            Assert.Equal(0, policyList.Code);
            Assert.Contains("scripted-model", policyList.Output);
            AssertNoLeaks(policyList.Output);

            var policyShowMissing = await Run("model", "policy", "show", "scripted-model");
            Assert.Equal(1, policyShowMissing.Code);
            AssertNoLeaks(policyShowMissing.Output);

            var policySet = await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "e2e");
            Assert.Equal(0, policySet.Code);
            Assert.Contains("PatchOnly", policySet.Output);
            AssertNoLeaks(policySet.Output);

            var policyShow = await Run("model", "policy", "show", "scripted-model");
            Assert.Equal(0, policyShow.Code);
            Assert.Contains("PatchOnly", policyShow.Output);
            AssertNoLeaks(policyShow.Output);

            var select = await Run("model", "select", "scripted-model");
            Assert.Equal(0, select.Code);
            Assert.Contains("scripted-model", select.Output);
            AssertNoLeaks(select.Output);

            var policyHistory = await Run("model", "policy", "history", "scripted-model");
            Assert.Equal(0, policyHistory.Code);
            Assert.Contains("rev=", policyHistory.Output);
            AssertNoLeaks(policyHistory.Output);

            var policyDelete = await Run("model", "policy", "delete", "scripted-model");
            Assert.Equal(0, policyDelete.Code);
            Assert.Contains("eliminada", policyDelete.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(policyDelete.Output);

            // M4: trust, permission grants, interaction resolution, integrity and maintenance.
            var trust = await Run("trust");
            Assert.Equal(0, trust.Code);
            Assert.Contains("confiable", trust.Output);
            AssertNoLeaks(trust.Output);
            var untrust = await Run("trust", "revoke");
            Assert.Equal(0, untrust.Code);
            Assert.Contains("no confiable", untrust.Output);
            AssertNoLeaks(untrust.Output);

            var paths = OmniHost.CreatePlatformPaths();
            var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
                ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspace)));
            var workspaceData = OmniHost.WorkspaceDataDirectory(paths, workspace);
            var grantStore = new FilePermissionGrantStore(workspaceData, new FileAuditSink(data));
            var grant = new PermissionGrantRecord(GrantId.New(), "filesystem.read", new string('a', 64),
                GrantLifetime.Workspace, workspaceId, null, DateTimeOffset.UtcNow);
            grantStore.Add(grant, CancellationToken.None);

            var permissions = await Run("permissions", "list");
            Assert.Equal(0, permissions.Code);
            Assert.Contains(grant.Id.ToString(), permissions.Output);
            AssertNoLeaks(permissions.Output);
            var revoke = await Run("permissions", "revoke", grant.Id.ToString());
            Assert.Equal(0, revoke.Code);
            Assert.Contains("revocado", revoke.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(revoke.Output);
            var revokeMissing = await Run("permissions", "revoke", Guid.NewGuid().ToString());
            Assert.Equal(1, revokeMissing.Code);
            AssertNoLeaks(revokeMissing.Output);

            var resolve = await Run("resolve");
            Assert.Equal(1, resolve.Code);
            Assert.Contains("No hay", resolve.Output);
            AssertNoLeaks(resolve.Output);
            var resolveUnknown = await Run("resolve", "missing-interaction", "allow_once");
            Assert.Equal(1, resolveUnknown.Code);
            AssertNoLeaks(resolveUnknown.Output);

            // An explicit bad path is a stable user-facing failure mode, independent of internal storage layout.
            var verifyOk = await Run("verify-journal");
            Assert.True(verifyOk.Code == 0, $"verify-journal exited {verifyOk.Code}: {verifyOk.Output}");
            Assert.Contains("— OK", verifyOk.Output);
            AssertNoLeaks(verifyOk.Output);

            var verify = await Run("verify-journal", Path.Combine(root, "missing-journal.db"));
            Assert.Equal(1, verify.Code);
            Assert.Contains("JournalUnreadable", verify.Output);
            AssertNoLeaks(verify.Output);

            var sessionPurge = await Run("session", "purge", "not-a-guid");
            Assert.Equal(2, sessionPurge.Code);
            Assert.Contains("no válido", sessionPurge.Output);
            AssertNoLeaks(sessionPurge.Output);

            var gc = await Run("gc", "--dry-run");
            Assert.Equal(0, gc.Code);
            Assert.Contains("GC", gc.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(gc.Output);

            var auditPurge = await Run("audit", "purge", "--dry-run", "--before", "2026-01-01");
            Assert.Equal(0, auditPurge.Code);
            Assert.Contains("auditoría", auditPurge.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(auditPurge.Output);

            // Headless only: must return without initializing an interactive terminal driver.
            var tui = await Run("tui", "--sim");
            Assert.True(tui.Code == 0, $"tui --sim exited {tui.Code}: {tui.Output}");
            Assert.Contains("TUI (sim)", tui.Output);
            Assert.Contains("OmniCore", tui.Output);
            AssertNoLeaks(tui.Output);

            var journal = Path.Combine(workspaceData, "journal.db");
            string sessionId;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = journal }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT session_id FROM events ORDER BY seq LIMIT 1";
                sessionId = Convert.ToString(command.ExecuteScalar()) ?? "";
            }
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            var purge = await Run("session", "purge", sessionId);
            Assert.Equal(0, purge.Code);
            Assert.Contains("sesión", purge.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(purge.Output);

            Environment.SetEnvironmentVariable("OMNI_LOCALE", "en");
            var englishDoctor = await Run("doctor");
            Assert.Equal(0, englishDoctor.Code);
            Assert.Contains("Available models:", englishDoctor.Output);
            AssertNoLeaks(englishDoctor.Output);

            var help = await Run("--help");
            Assert.Equal(0, help.Code);
            Assert.Contains("Uso:", help.Output);
            AssertNoLeaks(help.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", priorData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", priorConfig);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", priorLocale);
            Environment.SetEnvironmentVariable("OMNI_MODEL", priorModel);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
            if (previousRuntime is not null) CliApp.UseRuntimeForTests(previousRuntime);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private static async Task<(int Code, string Output)> Run(params string[] args)
    {
        var prior = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var code = await CliApp.RunAsync(args);
            return (code, output.ToString());
        }
        finally { Console.SetOut(prior); }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs", "sim")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró docs/sim.");
    }

    private static void AssertNoLeaks(string output, bool machineReadable = false)
    {
        Assert.False(Regex.IsMatch(output, @"(?m)^\s*at [A-Za-z0-9_.+`]+\("), "Se filtró una traza: " + output);
        Assert.DoesNotContain("frames:", output, StringComparison.OrdinalIgnoreCase);
        if (machineReadable) return; // JSON conserva identificadores de eventos, que no son texto localizado.
        var localizationKey = Regex.Match(output, @"\b[a-z][a-z0-9]*(?:\.[a-z][a-z0-9]*)*\.[a-z][a-z0-9]*_[a-z0-9_]+\b",
            RegexOptions.IgnoreCase);
        Assert.False(localizationKey.Success, "Se filtró una clave de localización: " + localizationKey.Value);
    }

    /// <summary>A tiny real HTTP server returning deterministic OpenAI-compatible SSE responses.</summary>
    private sealed class ScriptedHttpProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;

        public ScriptedHttpProvider()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = HandleAsync(client, _stop.Token);
            }
        }

        private static async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                var body = new char[contentLength];
                var read = 0;
                while (read < body.Length)
                {
                    var count = await reader.ReadAsync(body.AsMemory(read), cancellationToken);
                    if (count == 0) break;
                    read += count;
                }

                var eventText = "data: {\"choices\":[{\"delta\":{\"content\":\"respuesta-scripted\"},\"finish_reason\":null}]}\n\n"
                    + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: [DONE]\n\n";
                var bytes = Encoding.UTF8.GetBytes(eventText);
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n"
                    + "Cache-Control: no-cache\r\nConnection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _serve; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
