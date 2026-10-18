using System.Diagnostics;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Extensions;
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
        // The fixture contains no symlinks. Git's Windows symlink resolver queries
        // the Volume Mount Manager, which is unavailable in an AppContainer.
        if (OperatingSystem.IsWindows()) Run("git", ["config", "core.symlinks", "false"], workspace);
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

    private static RunContext StartRun(string root, bool fullAgentSession = false)
    {
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(workspace);
        var store = new SqliteEventStore(Path.Combine(data, "journal.db"));
        var platformPaths = fullAgentSession ? OmniHost.CreatePlatformPaths(Path.Combine(data, "platform")) : null;
        if (platformPaths is not null)
        {
            var securityDefault = new SessionSecurityDefaultConfiguration(platformPaths);
            var current = securityDefault.Read();
            securityDefault.Set(current.Revision, ModelPolicyCategory.FullAgent, CancellationToken.None);
        }
        var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(),
            Path.Combine(data, "lastsession.txt"),
            platformPathsFactory: platformPaths is null ? null : () => platformPaths);
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act") + ","
            + JsonObj.Field("objective", "corrige este test") + "," + JsonObj.Field("workspace", workspace) + "}"),
            CancellationToken.None);
        Assert.Equal("ok", ack.Status);
        var events = store.ReadFrom(server.LastSessionId()!, 1);
        var run = RunProjection.Replay(server.LastSessionId()!, server.LastRunId()!, EventCodecs.Create(), events);
        return new RunContext(server, store, server.LastSessionId()!, server.LastRunId()!,
            server.LastLaneId()!, run.RootTask!, workspace, data);
    }

    private static EffectiveModelPolicy PatchOnlyPolicy(bool requirePostEditValidation = true)
    {
        var key = ModelPolicyKey.For("scripted", "coder");
        var preset = ModelPolicyPresets.PatchOnly();
        var policy = new UserModelPolicy(preset.Category, preset.ToolPolicy,
            new FileMutationPolicy(preset.MutationPolicy.Mode, preset.MutationPolicy.Delete,
                preset.MutationPolicy.MoveOrRename, preset.MutationPolicy.MaxFilesPerTurn,
                preset.MutationPolicy.MaxChangedLinesPerTurn, preset.MutationPolicy.MaxRewriteRatio,
                preset.MutationPolicy.RequirePriorRead, preset.MutationPolicy.RequireExpectedVersionToken,
                requirePostEditValidation, preset.MutationPolicy.AllowParallelMutations), "test", null);
        var stored = new StoredModelPolicy(key, 1, policy,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
            PlanControl.ModelDriven, 8);
        return EffectiveModelPolicy.Resolve(key, stored, harness);
    }

    private static ExplorerTurn CreateTurn(RunContext context,
        Func<ModelRequest, CancellationToken, ModelResponse> complete, out ModelCapabilityBoundary boundary,
        bool requirePostEditValidation = true)
    {
        var policy = PatchOnlyPolicy(requirePostEditValidation);
        var catalog = OmniHost.CreateActTools().Catalog();
        boundary = new ModelCapabilityBoundary(policy, ModelCapabilityBoundary.CoreTools,
            new FileReadRegistry(context.Workspace));
        var executor = OmniHost.CreateActExecutor(catalog, context.Workspace, boundary);
        return new ExplorerTurn(complete, executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("scripted-coder", "harness", "core-tools-1", "fake", "none", "M3",
                policy.Fingerprint()),
            new ModelSelection(new ModelIdValue("scripted-coder"), 8192, ToolMode.Direct, null),
            context.Server.AcquireStore(), context.Server.AcquireCodecs(),
            new FileArtifactStore(Path.Combine(context.Data, "artifacts")), new InMemoryAuditSink(),
            new RedactionPolicy(), boundary: boundary);
    }

    private static bool Complete(RunContext context,
        Func<EventStream, IReadOnlyList<ExternalCompletionGateResult>>? external = null,
        MutationLedger? mutationLedger = null)
    {
        var result = context.Server.CheckRunCompletionAndGate(context.Session, context.Run, external, mutationLedger);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome!.Kind);
        return Assert.IsType<bool>(result.Completed);
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
            }, out var boundary);
            var first = turn.Ask("corrige el test", "Coder instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None);
            Assert.Contains(first.ToolCalls, call => call.ToolName == "filesystem.read" && call.Succeeded);
            Assert.Contains(first.ToolCalls, call => call.ToolName == "filesystem.patch" && call.Succeeded);
            Assert.Equal(firstPatch, File.ReadAllText(Path.Combine(context.Workspace, "Program.cs")));

            var gates = new WorkspaceGatesYaml { Build = ["dotnet", "--version"] };
            var runner = GateRunner(context, gates);
            var sourceTurn = Assert.Single(context.Store.ReadFrom(context.Session, 1)
                .Select(context.Server.AcquireCodecs().Decode).OfType<TurnStarted>()).TurnId;
            var sibling = LaneId.New();
            var siblingTurn = TurnId.New();
            var profile = Assert.Single(context.Store.ReadFrom(context.Session, 1)
                .Select(context.Server.AcquireCodecs().Decode).OfType<LaneCreated>()).AgentProfile;
            using (ExecutionScope.Begin(new ExecutionScopeState(context.Run, context.Task, sibling)))
                new EventStream(context.Store, context.Server.AcquireCodecs(), context.Session).AppendBatch(
                    [new LaneCreated(sibling, context.Task, profile), new LaneStarted(sibling),
                        new TurnStarted(siblingTurn, sibling), new TurnCompleted(siblingTurn), new LaneCompleted(sibling, null)],
                    DurabilityClass.Standard);
            bool completed;
            using (ExecutionScope.Begin(new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New())))
                completed = Complete(context, stream => runner.Run(stream, CancellationToken.None),
                    boundary.ReadRegistry().Ledger);
            Assert.True(completed, string.Join("; ", context.Store.ReadFrom(context.Session, 1)
                .Select(context.Server.AcquireCodecs().Decode).OfType<RunValidationRejected>()
                .SelectMany(rejection => rejection.Missing)));

            var decoded = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode).ToArray();
            Assert.Contains(decoded.OfType<ToolCallRequested>(), call => call.ToolName == "process.exec"
                && call.ProviderCallId.StartsWith("completion-gate:build:", StringComparison.Ordinal));
            Assert.Contains(decoded.OfType<PermissionEvaluated>(), permission => permission.Decision == PermissionDecision.Allow);
            Assert.Contains(decoded.OfType<RunCompleted>(), completed => completed.RunId.Equals(context.Run));
            Assert.DoesNotContain(decoded.OfType<ToolCallRequested>(), call => call.ToolName == "shell.exec");
            var gateCall = Assert.Single(decoded.OfType<ToolCallRequested>(), call => call.ToolName == "process.exec");
            var gateEvents = context.Store.ReadFrom(context.Session, 1)
                .Where(evt => evt.ToolCallId == gateCall.ToolCallId).ToArray();
            Assert.NotEmpty(gateEvents);
            Assert.All(gateEvents, evt =>
            {
                Assert.Equal(context.Run, evt.RunId);
                Assert.Equal(context.Task, evt.TaskId);
                Assert.Equal(context.Lane, evt.LaneId);
                Assert.Equal(sourceTurn, evt.TurnId);
            });
        }
        finally { context.Store.Close(); Remove(root); }
    }

    [Fact]
    public void Latest_open_root_turn_cannot_borrow_a_previous_turn_for_process_gates()
    {
        var root = TempDir();
        var context = StartRun(root);
        try
        {
            var turn = CreateTurn(context, (_, _) => End(), out var boundary, requirePostEditValidation: false);
            Assert.Equal(StopReason.EndTurn, turn.Ask("explica", "instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None).StopReason);
            using (ExecutionScope.Begin(new ExecutionScopeState(context.Run, context.Task, context.Lane)))
                new EventStream(context.Store, context.Server.AcquireCodecs(), context.Session)
                    .Append(new TurnStarted(TurnId.New(), context.Lane));
            var runner = GateRunner(context, new WorkspaceGatesYaml { Build = ["dotnet", "--version"] });
            Assert.False(Complete(context, stream => runner.Run(stream, CancellationToken.None),
                boundary.ReadRegistry().Ledger));
            Assert.DoesNotContain(context.Store.ReadFrom(context.Session, 1)
                .Select(context.Server.AcquireCodecs().Decode).OfType<ToolCallRequested>(), call => call.ToolName == "process.exec");
        }
        finally { context.Store.Close(); Remove(root); }
    }

    [Fact]
    public void Configured_process_gate_without_a_durable_turn_does_not_execute_or_invent_one()
    {
        var root = TempDir();
        var context = StartRun(root);
        try
        {
            var runner = GateRunner(context, new WorkspaceGatesYaml { Build = ["dotnet", "--version"] });
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(context.Run, context.Task, context.Lane));
            var result = Assert.Single(runner.Run(new EventStream(context.Store,
                context.Server.AcquireCodecs(), context.Session), CancellationToken.None));
            Assert.False(result.Passed);
            var payloads = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode).ToArray();
            Assert.DoesNotContain(payloads, payload => payload is ToolCallRequested or ToolCallStarted or TurnStarted);
        }
        finally { context.Store.Close(); Remove(root); }
    }

    [Fact]
    public void Pending_edit_without_build_or_test_gate_rejects_completion_with_localized_guidance()
    {
        var root = TempDir();
        var context = StartRun(root);
        var original = "class Program { BROKEN }\n" + Padding;
        File.WriteAllText(Path.Combine(context.Workspace, "Program.cs"), original);
        try
        {
            var turn = CreateTurn(context, (request, _) => ToolResultCount(request) switch
            {
                0 => Call("filesystem.read", "{\"path\":\"Program.cs\"}"),
                1 => Call("filesystem.patch", Patch("Program.cs", Version(original), "BROKEN", "static void Main() { }")),
                _ => End(),
            }, out var boundary);
            var result = turn.Ask("corrige", "Coder instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None);
            Assert.Contains(result.ToolCalls, call => call.ToolName == "filesystem.patch" && call.Succeeded);

            Assert.False(Complete(context, mutationLedger: boundary.ReadRegistry().Ledger));
            var rejected = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode)
                .OfType<RunValidationRejected>().Last();
            Assert.Contains("post-edit-validation", rejected.Gates);
            Assert.Contains(rejected.Missing, missing => missing == "coder.postEditValidation.required");
            var localized = new Localization("es").ResolveWire(
                rejected.Missing.Single(missing => missing == "coder.postEditValidation.required"));
            Assert.Contains("ediciones pendientes de validar", localized, StringComparison.Ordinal);
            Assert.Contains(".omnicore/settings.yaml", localized, StringComparison.Ordinal);
            Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(context.Session, context.Run,
                context.Server.AcquireCodecs(), context.Store.ReadFrom(context.Session, 1)).State);
            Assert.NotEmpty(PlanProjection.Replay(context.Server.AcquireCodecs(), context.Store.ReadFrom(context.Session, 1)).Items());
        }
        finally { context.Store.Close(); Remove(root); }
    }

    [Fact]
    public void Edit_after_successful_test_gate_requires_another_gate_before_completion()
    {
        var root = TempDir();
        var context = StartRun(root);
        var original = "class Program { BROKEN }\n" + Padding;
        var secondOriginal = "class Other { OLD }\n" + Padding;
        File.WriteAllText(Path.Combine(context.Workspace, "Program.cs"), original);
        File.WriteAllText(Path.Combine(context.Workspace, "Other.cs"), secondOriginal);
        try
        {
            var responseStep = 0;
            var turn = CreateTurn(context, (_, _) => responseStep++ switch
            {
                0 => Call("filesystem.read", "{\"path\":\"Program.cs\"}"),
                1 => Call("filesystem.patch", Patch("Program.cs", Version(original), "BROKEN", "static void Main() { }")),
                2 => End(),
                3 => Call("filesystem.read", "{\"path\":\"Other.cs\"}"),
                4 => Call("filesystem.patch", Patch("Other.cs", Version(secondOriginal), "OLD", "NEW")),
                _ => End(),
            }, out var boundary);
            _ = turn.Ask("corrige", "Coder instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None);

            // El gate Test pasa, pero Acceptance mantiene el Run abierto para permitir otra edición.
            Assert.False(Complete(context, _ => [
                new ExternalCompletionGateResult("test", true, "ok"),
                new ExternalCompletionGateResult("acceptance", false, "pendiente"),
            ], boundary.ReadRegistry().Ledger));
            Assert.Empty(boundary.ReadRegistry().Ledger.PendingValidations());

            var next = turn.Ask("edita el otro archivo", "Coder instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None);
            Assert.Contains(next.ToolCalls, call => call.ToolName == "filesystem.patch" && call.Succeeded);
            Assert.Equal(secondOriginal.Replace("OLD", "NEW", StringComparison.Ordinal),
                File.ReadAllText(Path.Combine(context.Workspace, "Other.cs")));

            Assert.False(Complete(context, mutationLedger: boundary.ReadRegistry().Ledger));
            var rejected = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode)
                .OfType<RunValidationRejected>().Last();
            Assert.Contains("post-edit-validation", rejected.Gates);
            _ = turn.Ask("ejecuta validación", "Coder instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None);

            Assert.True(Complete(context, _ => [new ExternalCompletionGateResult("test", true, "ok")],
                boundary.ReadRegistry().Ledger));
            Assert.Equal(RunState.Completed, RunProjection.Replay(context.Session, context.Run,
                context.Server.AcquireCodecs(), context.Store.ReadFrom(context.Session, 1)).State);
        }
        finally { context.Store.Close(); Remove(root); }
    }

    [Fact]
    public void Disabled_post_edit_validation_does_not_block_completion_without_a_gate()
    {
        var root = TempDir();
        var context = StartRun(root);
        var original = "class Program { BROKEN }\n" + Padding;
        File.WriteAllText(Path.Combine(context.Workspace, "Program.cs"), original);
        try
        {
            var turn = CreateTurn(context, (request, _) => ToolResultCount(request) switch
            {
                0 => Call("filesystem.read", "{\"path\":\"Program.cs\"}"),
                1 => Call("filesystem.patch", Patch("Program.cs", Version(original), "BROKEN", "static void Main() { }")),
                _ => End(),
            }, out var boundary, requirePostEditValidation: false);
            var result = turn.Ask("corrige", "Coder instructions", context.Session, context.Run,
                context.Lane, "", CancellationToken.None);
            Assert.Contains(result.ToolCalls, call => call.ToolName == "filesystem.patch" && call.Succeeded);
            Assert.Empty(boundary.ReadRegistry().Ledger.PendingValidations());
            Assert.True(Complete(context, mutationLedger: boundary.ReadRegistry().Ledger));
            Assert.Equal(RunState.Completed, RunProjection.Replay(context.Session, context.Run,
                context.Server.AcquireCodecs(), context.Store.ReadFrom(context.Session, 1)).State);
        }
        finally { context.Store.Close(); Remove(root); }
    }

    [Fact]
    public void Configured_test_gate_reworks_in_act_loop_when_skill_validator_activation_is_unavailable()
    {
        if (OperatingSystem.IsMacOS()) return;
        var root = TempDir();
        var context = StartRun(root, fullAgentSession: true);
        var initialPolicy = context.Store.ReadFrom(context.Session, 1)
            .Select(context.Server.AcquireCodecs().Decode).OfType<SessionSecurityPolicySet>().Single();
        Assert.Equal(ModelPolicyCategory.FullAgent, initialPolicy.Category);
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
            var modelCall = 0;
            ModelResponse CompleteModel(ModelRequest request, CancellationToken _)
            {
                var userText = string.Join("\n", request.Messages.Where(message => message.Role == MessageRole.User)
                    .SelectMany(message => message.Content).OfType<TextBlock>().Select(block => block.Text));
                if (userText.Contains("trailing whitespace", StringComparison.OrdinalIgnoreCase)) sawGateFeedback = true;
                return modelCall++ switch
                {
                    0 or 3 => Call("filesystem.read", "{\"path\":\"Program.cs\"}"),
                    1 => Call("filesystem.patch", Patch("Program.cs", Version(original), "BROKEN", "STILL_BROKEN")),
                    2 => End(),
                    4 => Call("filesystem.patch", Patch("Program.cs", Version(rejectedPatch), "STILL_BROKEN }  ",
                        "static void Main() { } }")),
                    _ => End(),
                };
            }
            var turn = CreateTurn(context, CompleteModel, out _);
            var gateConfiguration = new WorkspaceGatesYaml
            {
                Test = ["git", "diff", "--check"],
            };
            var gateArtifacts = new FileArtifactStore(Path.Combine(context.Data, "gate-artifacts"));
            var diagnostics = new List<string>();
            var runtime = OmniCliRuntime.Create(context.Workspace);
            var exit = runtime.RunActLoop(turn, diagnostics.Add, "corrige el test", "Coder instructions",
                context.Session, context.Run, context.Lane, "", gateConfiguration, null, context.Server,
                gateArtifacts, new InMemoryAuditSink(), interaction =>
                    interaction.Kind == InteractionKind.WeakSandboxConsent ? "consent_once" : "allow_once",
                interactive: true, locale: "en",
                cancellationToken: TestContext.Current.CancellationToken,
                subsequentTurnFactory: attempt => CreateTurn(context, CompleteModel, out _));

            var payloads = context.Store.ReadFrom(context.Session, 1).Select(context.Server.AcquireCodecs().Decode)
                .ToArray();
            var rejections = payloads.OfType<RunValidationRejected>().ToArray();
            var rejectionKinds = string.Join(" | ", rejections.Select(item => string.Join(',', item.Gates))
                .Distinct(StringComparer.Ordinal));
            Assert.True(rejections.Length == 1,
                $"validation rejection count={rejections.Length}; gate sets={rejectionKinds}; "
                + $"missing counts={string.Join(',', rejections.Select(item => item.Missing.Count))}; "
                + $"tool failures={string.Join(',', payloads.OfType<ToolCallFailed>().Select(item => item.ErrorCode?.Value ?? "none"))}; "
                + $"patch calls={payloads.OfType<ToolCallRequested>().Count(item => item.ToolName == "filesystem.patch")}; "
                + $"completed={payloads.OfType<RunCompleted>().Any(item => item.RunId.Equals(context.Run))}; "
                + $"model requests={modelCall}.");
            var rejected = Assert.Single(rejections);
            Assert.Contains("test", rejected.Gates);
            Assert.Contains(rejected.Missing, missing => missing.Contains("artifact:", StringComparison.Ordinal));
            var outputArtifact = Assert.Single(rejected.OutputArtifacts!);
            var output = gateArtifacts.GetText(outputArtifact.Hash);
            Assert.NotNull(output);
            Assert.True(output.Contains("trailing whitespace", StringComparison.OrdinalIgnoreCase),
                "Expected the real whitespace diagnostic from the test gate. Workspace: "
                + context.Workspace + ". Actual output:\n" + output);
            Assert.Equal(0, exit);
            Assert.Equal(fixedContent, File.ReadAllText(Path.Combine(context.Workspace, "Program.cs")));
            Assert.True(sawGateFeedback, "el siguiente Turn del modelo recibió la salida del gate fallido");
            Assert.Equal(6, modelCall);
            Assert.Contains(payloads.OfType<ToolCallSucceeded>(), succeeded => payloads.OfType<ToolCallRequested>()
                .Any(requested => requested.ToolCallId.Equals(succeeded.ToolCallId)
                    && requested.ToolName == "filesystem.patch"));
            Assert.Contains(payloads.OfType<RunCompleted>(), completed => completed.RunId.Equals(context.Run));
        }
        finally { context.Store.Close(); Remove(root); }
    }

    [Fact]
    public void Skill_validator_pass_does_not_replace_required_build_or_test_validation()
    {
        if (OperatingSystem.IsMacOS()) return;
        var root = TempDir();
        var context = StartRun(root, fullAgentSession: true);
        var original = "class Program { BROKEN }\n" + Padding;
        var verified = "class Program { VERIFIED }\n" + Padding;
        File.WriteAllText(Path.Combine(context.Workspace, "Program.cs"), original);
        var skill = new DiscoveredSkill(context.Workspace,
            new SkillManifest { Id = "fixture.post-edit-validator", Version = "1", BudgetTokens = 64 },
            ScopeLevel.Session, TrustLevel.Trusted, new string('a', 64), SkillLifecycleState.Loaded, null);
        const string rule = "{\"id\":\"verified-output\",\"kind\":\"file-contains\",\"path\":\"Program.cs\",\"text\":\"VERIFIED\"}";
        var resource = new LoadedSkillResource(SkillResourceKind.Validator, "validators/output.json", rule,
            new string('b', 64));
        var validator = new SkillCompletionValidator([new LoadedSkill(skill, "fixture instructions",
            new string('c', 64), [resource], new string('d', 64))]);
        try
        {
            var modelCall = 0;
            var subsequentTurnCreated = false;
            ModelResponse CompleteModel(ModelRequest _, CancellationToken __)
            {
                return modelCall++ switch
                {
                    0 => Call("filesystem.read", "{\"path\":\"Program.cs\"}"),
                    1 => Call("filesystem.patch", Patch("Program.cs", Version(original), "BROKEN", "VERIFIED")),
                    2 => End(),
                    _ => End(),
                };
            }

            var turn = CreateTurn(context, CompleteModel, out _);
            var diagnostics = new List<string>();
            var runtime = OmniCliRuntime.Create(context.Workspace);
            var exit = runtime.RunActLoop(turn, diagnostics.Add, "corrige y valida", "Coder instructions",
                context.Session, context.Run, context.Lane, "", null, null, context.Server,
                new FileArtifactStore(Path.Combine(context.Data, "artifacts")), new InMemoryAuditSink(),
                interaction => interaction.Kind == InteractionKind.WeakSandboxConsent ? "consent_once" : "allow_once",
                interactive: true, locale: "en", cancellationToken: TestContext.Current.CancellationToken,
                skillValidators: validator,
                subsequentTurnFactory: attempt =>
                {
                    subsequentTurnCreated = true;
                    return CreateTurn(context, CompleteModel, out _);
                });

            var events = context.Store.ReadFrom(context.Session, 1)
                .Select(context.Server.AcquireCodecs().Decode).ToArray();
            var rejected = events.OfType<RunValidationRejected>().ToArray();
            var rejection = Assert.Single(rejected);
            Assert.Equal(new[] { "post-edit-validation" }, rejection.Gates);
            Assert.Contains("coder.postEditValidation.required", rejection.Missing);
            Assert.Equal(1, exit);
            Assert.Equal(3, modelCall);
            Assert.False(subsequentTurnCreated);
            Assert.Contains(diagnostics, line => line.Contains("The skill validation passed.", StringComparison.Ordinal));
            Assert.Equal(verified, File.ReadAllText(Path.Combine(context.Workspace, "Program.cs")));
            Assert.Contains(events.OfType<ToolCallSucceeded>(), succeeded => events.OfType<ToolCallRequested>()
                .Any(requested => requested.ToolCallId.Equals(succeeded.ToolCallId)
                    && requested.ToolName == "filesystem.patch"));
            Assert.DoesNotContain(events.OfType<RunCompleted>(), completed => completed.RunId.Equals(context.Run));
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
