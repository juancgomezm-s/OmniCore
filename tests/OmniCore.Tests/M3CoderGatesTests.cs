using System.Diagnostics;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class M3CoderGatesTests
{
    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-m3-gates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Remove(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception) { }
    }

    private static string Version(string content) => FilesystemPatchTool.VersionToken(
        System.Text.Encoding.UTF8.GetBytes(content));

    // Líneas estables para que un patch de una línea no supere el MaxRewriteRatio por defecto
    // (0.25, ADR-0044 §5), como en el resto de fixtures PatchOnly.
    private const string Padding = "// 2\n// 3\n// 4\n// 5\n// 6\n// 7\n// 8\n";

    private static string Patch(string path, string version, string oldText, string newText) =>
        "{\"path\":" + System.Text.Json.JsonSerializer.Serialize(path) + ",\"expectedVersion\":"
        + System.Text.Json.JsonSerializer.Serialize(version) + ",\"oldText\":"
        + System.Text.Json.JsonSerializer.Serialize(oldText) + ",\"newText\":"
        + System.Text.Json.JsonSerializer.Serialize(newText) + "}";

    private static ModelResponse End() => new(new ContentBlock[] { new TextBlock("listo") },
        StopReason.EndTurn, new TokenUsage(2, 2, 0, 0, 0), null, new ProviderMetadata("", "", null));

    private static ModelResponse Call(string name, string arguments) => new(
        new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "scripted-" + name, name, arguments) },
        StopReason.ToolUse, new TokenUsage(2, 2, 0, 0, 0), null, new ProviderMetadata("", "", null));

    private static int ToolResultCount(ModelRequest request) => request.Messages
        .Where(message => message.Role == MessageRole.Tool)
        .SelectMany(message => message.Content).OfType<ToolResultBlock>().Count();

    private static void InitializeGit(string workspace)
    {
        Run("git", ["init", "--quiet"], workspace);
        Run("git", ["config", "core.autocrlf", "false"], workspace);
        Run("git", ["-c", "user.name=OmniCore tests", "-c", "user.email=tests@example.invalid",
            "add", "Program.cs"], workspace);
        Run("git", ["-c", "user.name=OmniCore tests", "-c", "user.email=tests@example.invalid",
            "commit", "--quiet", "-m", "fixture"], workspace);
    }

    private static void Run(string executable, IReadOnlyList<string> args, string cwd)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("No se inició " + executable);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(executable + " terminó " + process.ExitCode);
    }

    private sealed record RunContext(OmniServer Server, SqliteEventStore Store, SessionId Session,
        RunId Run, LaneId Lane, TaskId Task, string Workspace, string Data);

    private static RunContext StartRun(string root)
    {
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(workspace);
        var store = new SqliteEventStore(Path.Combine(data, "journal.db"));
        var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(),
            Path.Combine(data, "lastsession.txt"));
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act") + ","
            + JsonObj.Field("objective", "corrige este test") + "," + JsonObj.Field("workspace", workspace) + "}"),
            CancellationToken.None);
        Assert.Equal("ok", ack.Status);
        var events = store.ReadFrom(server.LastSessionId()!, 1);
        var run = RunProjection.Replay(server.LastSessionId()!, server.LastRunId()!, EventCodecs.Create(), events);
        return new RunContext(server, store, server.LastSessionId()!, server.LastRunId()!,
            server.LastLaneId()!, run.RootTask!, workspace, data);
    }

    private static EffectiveModelPolicy PatchOnlyPolicy()
    {
        var key = ModelPolicyKey.For("scripted", "coder");
        var stored = new StoredModelPolicy(key, 1, ModelPolicyPresets.PatchOnly(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
            PlanControl.ModelDriven, 8);
        return EffectiveModelPolicy.Resolve(key, stored, harness);
    }

    private static ExplorerTurn CreateTurn(RunContext context,
        Func<ModelRequest, CancellationToken, ModelResponse> complete, out ModelCapabilityBoundary boundary)
    {
        var catalog = OmniHost.CreateActTools().Catalog();
        boundary = new ModelCapabilityBoundary(PatchOnlyPolicy(), ModelCapabilityBoundary.CoreTools,
            new FileReadRegistry(context.Workspace));
        var executor = OmniHost.CreateActExecutor(catalog, context.Workspace, boundary);
        return new ExplorerTurn(complete, executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("scripted-coder", "harness", "core-tools-1", "fake", "none", "M3",
                PatchOnlyPolicy().Fingerprint()),
            new ModelSelection(new ModelIdValue("scripted-coder"), 8192, ToolMode.Direct, null),
            context.Server.AcquireStore(), context.Server.AcquireCodecs(),
            new FileArtifactStore(Path.Combine(context.Data, "artifacts")), new InMemoryAuditSink(),
            new RedactionPolicy(), boundary: boundary);
    }

    private static bool Complete(RunContext context,
        Func<EventStream, IReadOnlyList<ExternalCompletionGateResult>>? external = null)
    {
        var events = context.Store.ReadFrom(context.Session, 1);
        var codecs = context.Server.AcquireCodecs();
        var run = RunProjection.Replay(context.Session, context.Run, codecs, events);
        var stream = new EventStream(context.Store, codecs, context.Session);
        return new RunCoupon(run, TaskGraphProjection.Replay(codecs, events), PlanProjection.Replay(codecs, events))
            .CheckCompletionAndGate(new PlanService(), new ProgressReconciler(), context.Store, codecs,
                context.Session, stream, external is null ? null : () => external(stream));
    }

    private ConfiguredCompletionGates GateRunner(RunContext context, WorkspaceGatesYaml gates,
        IArtifactStore? artifacts = null) => new(gates, context.Workspace, context.Run, context.Lane,
            context.Task, null, new InMemoryAuditSink(), artifacts ?? new FileArtifactStore(
                Path.Combine(context.Data, "gate-artifacts")), _ => "consent_once", interactive: true);

    [Fact]
    public void Trusted_workspace_gate_configuration_is_typed_argv_and_rejects_shell_strings()
    {
        var root = TempDir();
        try
        {
            var config = Path.Combine(root, ".omnicore");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "settings.yaml"),
                "gates:\n  build: [dotnet, --version]\n  test: [git, diff, --check]\n  acceptance: true\n");
            var loaded = WorkspaceConfigurationLoader.Load(root, trusted: true);
            Assert.Equal(new[] { "dotnet", "--version" }, loaded.Settings!.Gates!.Build);
            Assert.Equal(new[] { "git", "diff", "--check" }, loaded.Settings.Gates.Test);
            Assert.True(loaded.Settings.Gates.Acceptance);

            File.WriteAllText(Path.Combine(config, "settings.yaml"), "gates:\n  test: dotnet test\n");
            Assert.Throws<ConfigValidationException>(() => WorkspaceConfigurationLoader.Load(root, trusted: true));
        }
        finally { Remove(root); }
    }

    [Fact]
    public void Scripted_coder_reads_patches_then_build_gate_passes_and_run_completes()
    {
        if (OperatingSystem.IsMacOS()) return;
        var root = TempDir();
        var context = StartRun(root);
        var original = "class Program { BROKEN }\n" + Padding;
        var firstPatch = "class Program { static void Main() { } }\n" + Padding;
        File.WriteAllText(Path.Combine(context.Workspace, "Program.cs"), original);
        try
        {
            var turn = CreateTurn(context, (request, _) => ToolResultCount(request) switch
            {
                0 => Call("filesystem.read", "{\"path\":\"Program.cs\"}"),
                1 => Call("filesystem.patch", Patch("Program.cs", Version(original), "BROKEN", "static void Main() { }")),
                _ => End(),
            }, out _);
            var first = turn.Ask("corrige el test", "Coder instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None);
            Assert.Contains(first.ToolCalls, call => call.ToolName == "filesystem.read" && call.Succeeded);
            Assert.Contains(first.ToolCalls, call => call.ToolName == "filesystem.patch" && call.Succeeded);
            Assert.Equal(firstPatch, File.ReadAllText(Path.Combine(context.Workspace, "Program.cs")));

            var gates = new WorkspaceGatesYaml { Build = ["dotnet", "--version"] };
            var runner = GateRunner(context, gates);
            var completed = Complete(context, stream => runner.Run(stream, CancellationToken.None));
            Assert.True(completed, string.Join("; ", context.Store.ReadFrom(context.Session, 1)
                .Select(context.Server.AcquireCodecs().Decode).OfType<RunValidationRejected>()
                .SelectMany(rejection => rejection.Missing)));

            var decoded = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode).ToArray();
            Assert.Contains(decoded.OfType<ToolCallRequested>(), call => call.ToolName == "process.exec"
                && call.ProviderCallId.StartsWith("completion-gate:build:", StringComparison.Ordinal));
            Assert.Contains(decoded.OfType<PermissionEvaluated>(), permission => permission.Decision == PermissionDecision.Allow);
            Assert.Contains(decoded.OfType<RunCompleted>(), completed => completed.RunId.Equals(context.Run));
            Assert.DoesNotContain(decoded.OfType<ToolCallRequested>(), call => call.ToolName == "shell.exec");
        }
        finally { context.Store.Close(); Remove(root); }
    }

    [Fact]
    public void Failing_test_gate_returns_output_feedback_to_model_then_fixed_workspace_completes()
    {
        if (OperatingSystem.IsMacOS()) return;
        var root = TempDir();
        var context = StartRun(root);
        var original = "class Program { BROKEN }  \n" + Padding;
        var rejectedPatch = "class Program { STILL_BROKEN }  \n" + Padding;
        var fixedContent = "class Program { static void Main() { } }\n" + Padding;
        File.WriteAllText(Path.Combine(context.Workspace, "Program.cs"), original);
        try { Run("git", ["--version"], context.Workspace); }
        catch (Exception) { context.Store.Close(); Remove(root); return; }
        InitializeGit(context.Workspace);
        var sawGateFeedback = false;
        try
        {
            var turn = CreateTurn(context, (request, _) =>
            {
                var count = ToolResultCount(request);
                var userText = string.Join("\n", request.Messages.Where(message => message.Role == MessageRole.User)
                    .SelectMany(message => message.Content).OfType<TextBlock>().Select(block => block.Text));
                if (userText.Contains("trailing whitespace", StringComparison.OrdinalIgnoreCase)) sawGateFeedback = true;
                return count switch
                {
                    0 => Call("filesystem.read", "{\"path\":\"Program.cs\"}"),
                    1 => Call("filesystem.patch", Patch("Program.cs", Version(original), "BROKEN", "STILL_BROKEN")),
                    2 => End(),
                    3 => Call("filesystem.read", "{\"path\":\"Program.cs\"}"),
                    4 => Call("filesystem.patch", Patch("Program.cs", Version(rejectedPatch), "STILL_BROKEN }  ",
                        "static void Main() { } }")),
                    _ => End(),
                };
            }, out _);

            var first = turn.Ask("corrige el test", "Coder instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, first.StopReason);
            var failing = GateRunner(context, new WorkspaceGatesYaml
            {
                Test = ["git", "diff", "--check"],
            });
            Assert.False(Complete(context, stream => failing.Run(stream, CancellationToken.None)));
            var rejected = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode)
                .OfType<RunValidationRejected>().Last();
            Assert.Contains("test", rejected.Gates);
            Assert.Contains(rejected.Missing, missing => missing.Contains("artifact:", StringComparison.Ordinal));
            var outputArtifact = Assert.Single(rejected.OutputArtifacts!);
            var output = new FileArtifactStore(Path.Combine(context.Data, "gate-artifacts"))
                .GetText(outputArtifact.Hash);
            Assert.Contains("trailing whitespace", output, StringComparison.OrdinalIgnoreCase);
            var feedback = "Runtime completion validation failed. Fix the following feedback, then propose completion again:\n"
                + string.Join("\n", rejected.Missing) + "\nGate output:\n" + output;
            var repaired = turn.Ask(feedback, "Coder instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None);
            Assert.Equal(fixedContent, File.ReadAllText(Path.Combine(context.Workspace, "Program.cs")));
            Assert.Contains(repaired.ToolCalls, call => call.ToolName == "filesystem.patch" && call.Succeeded);
            var passing = GateRunner(context, new WorkspaceGatesYaml
            {
                Test = ["git", "diff", "--check"],
            });
            var completed = Complete(context, stream => passing.Run(stream, CancellationToken.None));
            Assert.True(completed, string.Join("; ", context.Store.ReadFrom(context.Session, 1)
                .Select(context.Server.AcquireCodecs().Decode).OfType<RunValidationRejected>()
                .SelectMany(rejection => rejection.Missing)));
            Assert.True(sawGateFeedback, "el siguiente Turn del modelo recibió la salida del gate fallido");
            Assert.Contains(context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode)
                .OfType<RunCompleted>(), completed => completed.RunId.Equals(context.Run));
        }
        finally { context.Store.Close(); Remove(root); }
    }

    [Fact]
    public void Acceptance_gate_without_tty_returns_input_required_and_with_scripted_answer_completes()
    {
        var root = TempDir();
        var context = StartRun(root);
        try
        {
            var turn = CreateTurn(context, (_, _) => End(), out _);
            var output = new List<string>();
            var runtime = OmniCliRuntime.Create(context.Workspace);
            var status = runtime.RunActLoop(turn, output.Add, "corrige este test", "Coder instructions",
                context.Session, context.Run, context.Lane, "", new WorkspaceGatesYaml { Acceptance = true },
                null, context.Server, new FileArtifactStore(Path.Combine(context.Data, "artifacts")),
                new InMemoryAuditSink(), null, interactive: false, "es", CancellationToken.None);
            Assert.Equal(3, status);
            Assert.Contains(output, line => line.Contains("InputRequired", StringComparison.Ordinal));
            var pending = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode)
                .OfType<InteractionRequested>().Last(request => request.Kind == InteractionKind.AcceptanceConfirmation);
            var runState = RunProjection.Replay(context.Session, context.Run, context.Server.AcquireCodecs(),
                context.Store.ReadFrom(context.Session, 1)).State;
            Assert.Equal(RunState.AwaitingInput, runState);
            Assert.NotNull(pending);
        }
        finally { context.Store.Close(); Remove(root); }

        root = TempDir();
        context = StartRun(root);
        try
        {
            var turn = CreateTurn(context, (_, _) => End(), out _);
            var runtime = OmniCliRuntime.Create(context.Workspace);
            var status = runtime.RunActLoop(turn, _ => { }, "corrige este test", "Coder instructions",
                context.Session, context.Run, context.Lane, "", new WorkspaceGatesYaml { Acceptance = true },
                null, context.Server, new FileArtifactStore(Path.Combine(context.Data, "artifacts")),
                new InMemoryAuditSink(), null, interactive: true, "es", CancellationToken.None,
                acceptanceResponder: _ => "accept");
            Assert.Equal(0, status);
            var events = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode).ToArray();
            Assert.Contains(events.OfType<InteractionResolved>(), resolved => resolved.OptionId == "accept");
            Assert.Contains(events.OfType<RunCompleted>(), completed => completed.RunId.Equals(context.Run));
        }
        finally { context.Store.Close(); Remove(root); }

        root = TempDir();
        context = StartRun(root);
        try
        {
            var turn = CreateTurn(context, (_, _) => End(), out _);
            var output = new List<string>();
            var runtime = OmniCliRuntime.Create(context.Workspace);
            runtime.Localize = new Localization("es").Resolve;
            var status = runtime.RunActLoop(turn, output.Add, "corrige este test", "Coder instructions",
                context.Session, context.Run, context.Lane, "", null, null, context.Server,
                new FileArtifactStore(Path.Combine(context.Data, "artifacts")), new InMemoryAuditSink(), null,
                interactive: false, "es", CancellationToken.None);
            Assert.Equal(0, status);
            Assert.Contains(output, line => line.Contains("no se configuró gate Build/Test", StringComparison.Ordinal));
            var events = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode).ToArray();
            Assert.DoesNotContain(events.OfType<InteractionRequested>(), request =>
                request.Kind == InteractionKind.AcceptanceConfirmation);
            Assert.Contains(events.OfType<RunCompleted>(), completed => completed.RunId.Equals(context.Run));
        }
        finally { context.Store.Close(); Remove(root); }
    }
}
