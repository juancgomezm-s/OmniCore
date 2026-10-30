namespace OmniCore.Tests;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Sandbox;
using OmniCore.Security;
using OmniCore.Tools;

using Xunit;

using Task = System.Threading.Tasks.Task;

public sealed class LanguageServerToolTests
{
    [Fact]
    public async Task LspStdioFramesSupportUtf8NotificationsAndReadMethods()
    {
        await using var process = StartFixture("normal");
        using var protocol = new OwnedLspProtocol(process, CancellationToken.None);
        var initialize = await protocol.RequestAsync("initialize", new JsonObject { ["processId"] = 1 }, CancellationToken.None);
        Assert.Equal("fixture", initialize.GetProperty("serverInfo").GetProperty("name").GetString());
        await protocol.NotifyAsync("initialized", new JsonObject(), CancellationToken.None);
        var uri = new Uri(Path.Combine(Path.GetTempPath(), "workspace-café", "source.cs")).AbsoluteUri;
        await protocol.NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = uri,
                ["languageId"] = "csharp",
                ["version"] = 1,
                ["text"] = "class Café { string s = \"🍊\"; }"
            }
        }, CancellationToken.None);

        var diagnostic = await protocol.RequestAsync("textDocument/diagnostic",
            new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, CancellationToken.None);
        Assert.Contains("diagnóstico café 🧪", diagnostic.GetProperty("items")[0].GetProperty("message").GetString());
        var definition = await protocol.RequestAsync("textDocument/definition", new JsonObject
        { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = 0, ["character"] = 8 } }, CancellationToken.None);
        Assert.Equal(uri, definition[0].GetProperty("uri").GetString());
        var symbols = await protocol.RequestAsync("textDocument/documentSymbol",
            new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, CancellationToken.None);
        Assert.Equal("símbolo", symbols[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task LspTimeoutAndDisallowedWorkspaceMutationFailClosed()
    {
        await using (var process = StartFixture("timeout"))
        using (var protocol = new OwnedLspProtocol(process, CancellationToken.None, TimeSpan.FromMilliseconds(250)))
        {
            _ = await protocol.RequestAsync("initialize", new JsonObject { ["processId"] = 1 }, CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                protocol.RequestAsync("test/timeout", new JsonObject(), CancellationToken.None));
        }

        await using (var process = StartFixture("malicious"))
        using (var protocol = new OwnedLspProtocol(process, CancellationToken.None, TimeSpan.FromSeconds(2)))
            await Assert.ThrowsAsync<IOException>(() => protocol.RequestAsync("initialize", new JsonObject { ["processId"] = 1 }, CancellationToken.None));
    }

    [Fact]
    public void UriRemappingRejectsEscapesAndMapsSnapshotLocations()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "omni-lsp-workspace");
        var snapshot = Path.Combine(Path.GetTempPath(), "omni-lsp-snapshot");
        var inside = new JsonObject { ["uri"] = new Uri(Path.Combine(snapshot, "src", "file.cs")).AbsoluteUri };
        LanguageServerTool.RemapUris(inside, snapshot, workspace);
        Assert.Equal(new Uri(Path.Combine(workspace, "src", "file.cs")).AbsoluteUri, inside["uri"]!.GetValue<string>());
        var outside = new JsonObject { ["targetUri"] = new Uri(Path.Combine(Path.GetTempPath(), "elsewhere.cs")).AbsoluteUri };
        Assert.Throws<InvalidDataException>(() => LanguageServerTool.RemapUris(outside, snapshot, workspace));
    }

    [Fact]
    public void Lsp_io_diagnostics_are_bounded_and_do_not_echo_exception_text()
    {
        Assert.Equal("process_exited", LanguageServerTool.SafeIoDiagnostic("language_server_exited"));
        Assert.Equal("io_failure", LanguageServerTool.SafeIoDiagnostic("/private/path/server.dll: permission denied"));
    }

    [Fact]
    public void LanguageServerConfigurationIsUserTypedAndRejectsSecretArguments()
    {
        var loaded = new ConfigLoader().Load(null, null,
            "languageServers:\n  csharp:\n    command: dotnet\n    args: [server.dll]\n    languageId: csharp\n    extensions: [.cs]\n");
        Assert.Equal("dotnet", loaded.Settings!.LanguageServers!["csharp"].Command);
        Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(null, null,
            "languageServers:\n  csharp:\n    command: dotnet\n    args: [--api-key, secret]\n    languageId: csharp\n    extensions: [.cs]\n"));
        Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(null, null,
            "languageServers:\n  csharp:\n    command: dotnet\n    environment: {TOKEN: secret}\n    languageId: csharp\n    extensions: [.cs]\n"));
        Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(null, null,
            "languageServers:\n  first:\n    command: dotnet\n    languageId: csharp\n    extensions: [.cs]\n  second:\n    command: dotnet\n    languageId: csharp\n    extensions: [.cs]\n"));
    }

    [Fact]
    public void Host_explorer_composes_one_lsp_tool_pair_for_multiple_servers()
    {
        var servers = LanguageServerDefinition.FromSettings(new UserSettingsYaml
        {
            LanguageServers = new Dictionary<string, LanguageServerSettingsYaml>(StringComparer.Ordinal)
            {
                ["csharp"] = new() { Command = "dotnet", Args = ["server.dll"], LanguageId = "csharp", Extensions = [".cs"] },
                ["fsharp"] = new() { Command = "dotnet", Args = ["server.dll"], LanguageId = "fsharp", Extensions = [".fs"] },
            }
        });
        var session = SessionId.New();
        var manager = new PersistentProcessSessionManager(session, new InMemoryEventStore(), EventCodecs.Create(),
            Guid.NewGuid().ToString("N"), Path.GetTempPath(), new FileArtifactStore(Path.GetTempPath()), () => true);
        var host = HostTools.ExplorerWithLanguageServersForTests(manager, servers,
            new StrongUnavailableFixtureLauncher(), _ => true, () => true);
        Assert.NotNull(host.Catalog().Find(new ToolId("code.navigate")));
        Assert.NotNull(host.Catalog().Find(new ToolId("code.diagnostics")));
        Assert.Equal(1, host.Catalog().RegisteredTools().Count(tool => tool.Descriptor.Id.ToString() == "code.navigate"));
        Assert.Equal(1, host.Catalog().RegisteredTools().Count(tool => tool.Descriptor.Id.ToString() == "code.diagnostics"));
    }

    [Fact]
    public async Task Host_lsp_tools_use_fixture_through_the_owned_strong_process_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniLspHost" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "source.cs"), "class Café { }\n");
            File.WriteAllText(Path.Combine(workspace, "outside.txt"), "class Outside { }\n");
            RunGit(workspace, "init");
            RunGit(workspace, "add", "source.cs", "outside.txt");
            var fixture = Path.Combine(AppContext.BaseDirectory, "lsp-fixture", "OmniCore.LanguageServerFixture.dll");
            Assert.True(File.Exists(fixture));
            var fixtureRuntimeConfig = Path.ChangeExtension(fixture, ".runtimeconfig.json");
            var fixtureDeps = Path.ChangeExtension(fixture, ".deps.json");
            Assert.True(File.Exists(fixtureRuntimeConfig));
            Assert.True(File.Exists(fixtureDeps));
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var definitions = LanguageServerDefinition.FromSettings(new UserSettingsYaml
            {
                LanguageServers = new Dictionary<string, LanguageServerSettingsYaml>(StringComparer.Ordinal)
                {
                    ["csharp"] = new() { Command = dotnet, Args = ["exec", "--runtimeconfig", fixtureRuntimeConfig,
                        "--depsfile", fixtureDeps, fixture, "normal"], LanguageId = "csharp", Extensions = [".cs"] },
                    ["outside"] = new() { Command = dotnet, Args = ["exec", "--runtimeconfig", fixtureRuntimeConfig,
                        "--depsfile", fixtureDeps, fixture, "outside"], LanguageId = "plaintext", Extensions = [".txt"] },
                }
            });
            var session = SessionId.New();
            var eventStore = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var openedRun = TestRun.Open(eventStore, session, "LSP fixture", RunMode.Plan);
            var artifacts = new FileArtifactStore(Path.Combine(data, "artifacts"));
            var manager = new PersistentProcessSessionManager(session, eventStore, codecs,
                Guid.NewGuid().ToString("N"), workspace, artifacts, () => true);
            var run = openedRun.RunId.ToString();
            var lane = openedRun.RootLane.ToString();
            var turn = TurnId.New();
            await manager.PrepareScopeAsync(run, lane);
            var launcher = OmniHost.CreateProcessSandboxLauncher(SystemProcessRuntime.Instance());
            var trusted = true;
            var configCurrent = true;
            Action? afterServerStart = null;
            Action? beforeSourceRecheck = null;
            var host = HostTools.ExplorerWithLanguageServersForTests(manager, definitions, launcher, _ => trusted,
                () => configCurrent, () => Interlocked.Exchange(ref afterServerStart, null)?.Invoke(),
                () => Interlocked.Exchange(ref beforeSourceRecheck, null)?.Invoke());
            var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
            { ["code.navigate"] = PermissionDecision.Allow });
            var runtime = new ToolRuntime(host.Catalog(), policy,
                _ => VoidBox.Instance);
            var context = new ToolExecutionContext(workspace, null,
                emitEvent: _ => { }, artifacts: artifacts, mutationRunId: run, mutationLaneId: lane,
                mutationTurnId: turn.ToString());
            using var executionScope = ExecutionScope.Begin(new ExecutionScopeState(openedRun.RunId,
                openedRun.RootTask, openedRun.RootLane, turn));

            ToolRuntime.Outcome Call(string path, string operation, CancellationToken token = default) => runtime.Run(
                new ValidatedToolCall(ToolCallId.New(), new ToolId("code.navigate"), "fixture-" + operation,
                    JsonSerializer.Serialize(new { path, operation, line = 0, character = 6 })),
                new ToolPreparationContext(workspace, DateTimeOffset.UtcNow), context, userApprovesAsk: false, token);

            var testToken = TestContext.Current.CancellationToken;
            var definition = Call("source.cs", "definition", testToken);
            if (!definition.Succeeded)
            {
                Assert.Contains("StrongSandboxUnavailableBeforeLaunchException", definition.Summary, StringComparison.Ordinal);
                return; // Strong is unavailable on this runner; the Host failed closed without Weak fallback.
            }
            using var definitionResult = JsonDocument.Parse(definition.Preview!);
            Assert.Contains("source.cs", definitionResult.RootElement.GetProperty("result")[0].GetProperty("uri").GetString());

            var references = Call("source.cs", "references", testToken);
            Assert.True(references.Succeeded, references.Summary);
            using var referencesResult = JsonDocument.Parse(references.Preview!);
            Assert.Contains("source.cs", referencesResult.RootElement.GetProperty("result")[0].GetProperty("uri").GetString());

            var outside = Call("outside.txt", "definition", testToken);
            Assert.False(outside.Succeeded);
            Assert.Contains("InvalidDataException", outside.Summary, StringComparison.Ordinal);

            beforeSourceRecheck = () => File.WriteAllText(Path.Combine(workspace, "source.cs"), "class Changed { }\n");
            var staleSource = Call("source.cs", "definition", testToken);
            Assert.False(staleSource.Succeeded);
            Assert.Contains("document_changed_during_request", staleSource.Summary, StringComparison.Ordinal);

            beforeSourceRecheck = () => configCurrent = false;
            var staleConfig = Call("source.cs", "definition", testToken);
            Assert.False(staleConfig.Succeeded);
            Assert.Contains("configuration_changed", staleConfig.Summary, StringComparison.Ordinal);
            configCurrent = true;

            beforeSourceRecheck = () => trusted = false;
            var staleTrust = Call("source.cs", "definition", testToken);
            Assert.False(staleTrust.Succeeded);
            Assert.Contains("trust_revoked", staleTrust.Summary, StringComparison.Ordinal);
            trusted = true;

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testToken);
            afterServerStart = cancellation.Cancel;
            var cancelled = Call("source.cs", "definition", cancellation.Token);
            Assert.False(cancelled.Succeeded);
            Assert.Contains("cancelled", cancelled.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(manager.Snapshot(run), process => process.State is "running" or "starting" or "stopping");
            Assert.Empty(Directory.GetFiles(workspace, "*.lsp-*", SearchOption.AllDirectories));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class StrongUnavailableFixtureLauncher : ISandboxProcessLauncher, IStrongSandboxPreflight
    {
        public StrongSandboxPreflightResult CheckStrongAvailability(SandboxLaunchSpec launch) =>
            StrongSandboxPreflightResult.UnavailableBeforeLaunch;

        public ValueTask<ISandboxProcessControl> StartAsync(
            SandboxLaunchSpec launch, CancellationToken cancellationToken) =>
            throw new StrongSandboxUnavailableBeforeLaunchException("fixture: Strong unavailable before launch");
    }

    private static FixtureTransport StartFixture(string mode)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "lsp-fixture", "OmniCore.LanguageServerFixture.dll");
        Assert.True(File.Exists(fixture), "The LSP fixture app must be copied beside the tests.");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add(mode);
        var searchPath = Environment.GetEnvironmentVariable("PATH");
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        start.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(searchPath)) start.Environment["PATH"] = searchPath;
        if (!string.IsNullOrWhiteSpace(dotnetRoot)) start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the LSP fixture.");
        return new FixtureTransport(process);
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git fixture command.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "git " + string.Join(" ", args) + " failed: " + output + error);
    }

    private sealed class FixtureTransport(Process process) : ILspProcessTransport, IAsyncDisposable
    {
        public async Task SendAsync(string text, CancellationToken token)
        {
            await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(text), token);
            await process.StandardInput.BaseStream.FlushAsync(token);
        }

        public async Task<LspTransportRead> ReadAsync(TimeSpan wait, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(wait);
            try
            {
                var stream = process.StandardOutput.BaseStream;
                var header = new List<byte>();
                var window = new Queue<byte>();
                while (header.Count < 8192)
                {
                    var single = new byte[1];
                    if (await stream.ReadAsync(single, deadline.Token) == 0) return new LspTransportRead("", "exited");
                    header.Add(single[0]); window.Enqueue(single[0]); if (window.Count > 4) window.Dequeue();
                    if (window.SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var text = Encoding.ASCII.GetString(header.ToArray());
                var line = text.Split("\r\n", StringSplitOptions.None).Single(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
                var bytes = new byte[length]; var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(offset), deadline.Token);
                    if (read == 0) return new LspTransportRead("", "exited");
                    offset += read;
                }
                return new LspTransportRead(text + Encoding.UTF8.GetString(bytes), "running");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return new LspTransportRead("", process.HasExited ? "exited" : "running"); }
        }

        public async ValueTask DisposeAsync()
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
            process.Dispose();
        }
    }
}
