using System.Diagnostics;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

// Control companion to AutomaticEscalationMissingCredentialRegressionTests: same hermetic
// real-child fixture, but escalation mode ask. Ask mode must surface the escalation request
// without approving it or resolving the missing credential, and must exit 0.
public sealed class AskEscalationMissingCredentialControlTests
{
    [Fact]
    public async Task Ask_escalation_requests_without_automatic_approval_or_credential_resolution()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "src", "OmniCore.Cli", "OmniCore.Cli.csproj")))
            repo = repo.Parent ?? throw new InvalidOperationException("Repository root unavailable");
        var cli = Path.Combine(repo.FullName, "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
        Assert.True(File.Exists(cli), "Build CLI before running this control");
        Assert.True(File.Exists(Path.ChangeExtension(cli, ".runtimeconfig.json")));
        var root = Path.Combine(Path.GetTempPath(), "omnicore-missing-auth-ask-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        foreach (var path in new[] { root, workspace, data, config }) Directory.CreateDirectory(path);
        using var child = new Process();
        var started = false;
        try
        {
            File.WriteAllText(Path.Combine(config, "providers.yaml"), """
                providers:
                  local: { family: OpenAiChatCompatible, baseUrl: 'http://127.0.0.1:1/v1', auth: none }
                  paid: { family: OpenAiChatCompatible, baseUrl: 'http://127.0.0.1:1/v1', authRef: synthetic-missing }
                """);
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  worker-model: { provider: local, context: 4, recommendedUsableContext: 4, maxOutput: 1, aliases: [worker] }
                  paid-model: { provider: paid, context: 200000, aliases: [paid], inputPricePerMillionUsd: 1, outputPricePerMillionUsd: 1 }
                routing:
                  exploration: [worker]
                  escalation: { mode: ask, chain: [worker, paid] }
                """);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workspace
            };
            start.ArgumentList.Add(cli);
            start.ArgumentList.Add("ask");
            start.ArgumentList.Add("probe");
            start.Environment.Clear();
            foreach (var name in new[] { "PATH", "SystemRoot", "windir", "DOTNET_ROOT" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            foreach (var name in new[] { "TEMP", "TMP", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "DOTNET_CLI_HOME" })
                start.Environment[name] = root;
            start.Environment["OMNICORE_DATA_DIR"] = data;
            start.Environment["OMNICORE_CONFIG_DIR"] = config;
            child.StartInfo = start;
            started = child.Start();
            Assert.True(started);
            var output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                child.WaitForExit();
                throw new TimeoutException("Owned isolated CLI did not exit within 30 seconds");
            }
            await Task.WhenAll(output, error);
            // Ask mode surfaces the request but never acts; the run exits successfully.
            Assert.Equal(0, child.ExitCode);
            var journal = Assert.Single(Directory.GetFiles(data, "journal.db", SearchOption.AllDirectories));
            var session = SessionId.Parse(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(journal)!, "lastsession.txt"))[0]);
            var store = new SqliteEventStore(journal);
            DomainEventPayload[] events;
            try { events = store.ReadFrom(session, 1).Select(EventCodecs.Create().Decode).ToArray(); }
            finally { store.Close(); }
            // These positives distinguish the intended path from unrelated startup failures.
            var abandoned = Assert.Single(events.OfType<TurnAbandoned>());
            Assert.Contains("ContextOverflow", abandoned.Reason);
            var requested = Assert.Single(events.OfType<ModelEscalationRequested>());
            Assert.Equal("worker-model", requested.FromModel);
            Assert.Equal("paid-model", requested.ToModel);
            Assert.Equal(EscalationCause.ContextLimit, requested.Cause);
            Assert.Equal(Assert.Single(events.OfType<RunCreated>()).RunId, requested.RunId);
            // Ask mode must not approve or complete an escalation on its own.
            Assert.DoesNotContain(events, e => e is ModelEscalationApproved);
            Assert.DoesNotContain(events, e => e is ModelEscalationCompleted);
        }
        finally
        {
            // Only the process created by this test; never remove its root while still alive.
            if (started && !child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
