namespace OmniCore.Tests;

using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Sandbox;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

public sealed class PersistentProcessSessionManagerTests
{
    [Theory]
    [InlineData(PermissionDecision.Ask)]
    [InlineData(PermissionDecision.Deny)]
    public void Process_start_ask_or_deny_never_launches_before_authority(PermissionDecision decision)
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniProcessPermission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var run = RunId.New().ToString(); var lane = LaneId.New().ToString();
        var codecs = EventCodecs.Create(); var store = new InMemoryEventStore();
        var manager = new PersistentProcessSessionManager(session, store, codecs, Guid.NewGuid().ToString("N"),
            root, null, static () => true);
        var launcher = new CountingLauncher();
        var resolver = new SystemExecutableResolver();
        var tool = new ProcessStartTool(manager, launcher, SandboxStrength.Strong,
            new PathBoundaryValidator(), resolver);
        var events = new List<DomainEventPayload>();
        var runtime = new ToolRuntime(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("process.start", decision),
            payload => { events.Add(payload); return VoidBox.Instance; }, null, resolver);
        try
        {
            var arguments = JsonSerializer.Serialize(new
            {
                executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
                argv = new[] { "--version" }, cwd = ".", timeoutSeconds = 30,
                readPaths = new[] { "allowed.txt" }, writePaths = new[] { "allowed.txt" },
            });
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.start"), "permission-fixture", arguments);
            var outcome = runtime.Run(call, new ToolPreparationContext(root, DateTimeOffset.UtcNow),
                new ToolExecutionContext(root, null, events.Add, isInteractive: false,
                    mutationRunId: run, mutationLaneId: lane), false, TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded);
            Assert.Equal(EffectOutcome.None, outcome.Effect);
            Assert.Equal(0, launcher.StartCount);
            if (decision == PermissionDecision.Ask)
                Assert.Contains(events, payload => payload is InteractionRequested { Kind: InteractionKind.Permission });
            else
                Assert.DoesNotContain(events, payload => payload is InteractionRequested);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void Host_process_ownership_metadata_does_not_rewrite_legacy_permission_fingerprints()
    {
        var call = ToolCallId.New();
        var legacy = new ToolIntent(call, new ToolId("process.send_input"), "{\"handle\":\"0123456789abcdef0123456789abcdef\"}",
            EffectClass.NonIdempotent, new ResourceClaims([], [], [],
                new ProcessClaim("process.handle:0123456789abcdef0123456789abcdef", [], "External"), []), ToolRisk.Critical, null);
        var bound = new ToolIntent(call, legacy.ToolId, legacy.NormalizedArgumentsJson, legacy.Effect,
            new ResourceClaims([], [], [], new ProcessClaim("process.handle:0123456789abcdef0123456789abcdef", [],
                "External", OwnerSessionId: "session", OwnerRunId: "run", OwnerLaneId: "lane", OwnerLeaseId: "lease"), []),
            legacy.Risk, null);
        Assert.Equal(ToolRuntime.IntentFingerprint(legacy), ToolRuntime.IntentFingerprint(bound));
    }

    [Fact]
    public async Task Ambiguous_launcher_failure_is_durable_unknown_not_effect_none()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniProcessLaunchUnknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var run = RunId.New().ToString(); var lane = LaneId.New().ToString();
        var store = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        var manager = new PersistentProcessSessionManager(session, store, codecs, Guid.NewGuid().ToString("N"),
            root, null, static () => true);
        try
        {
            var started = await manager.StartAsync(run, lane, Spec(root, TimeSpan.FromSeconds(10)),
                new AmbiguousLauncher(), null, TestContext.Current.CancellationToken);
            Assert.Equal("unknown", started.State);
            Assert.NotNull(started.Handle);
            var latest = store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<ProcessSessionStateRecorded>().Last();
            Assert.Equal("unknown", latest.State);
            Assert.Equal("launch_outcome_unresolved:IOException", latest.Diagnostic);
            var recovered = new PersistentProcessSessionManager(session, store, codecs, Guid.NewGuid().ToString("N"),
                root, null, static () => true);
            recovered.RestoreUnknown(new Dictionary<string, ProcessSessionStateRecorded>(StringComparer.Ordinal)
                { [latest.HandleId] = latest });
            _ = recovered.Read(latest.HandleId, run, lane, 0, 8, out _, out var highWater,
                out var gap, out var recoveredState, out _);
            Assert.Equal(0, highWater);
            Assert.True(gap, "Recovery cannot claim the prior live output buffer was empty.");
            Assert.Equal("unknown", recoveredState);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Partial_stdin_failure_is_recorded_as_unknown_effect()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniProcessInputUnknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var run = RunId.New().ToString(); var lane = LaneId.New().ToString();
        var store = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        var manager = new PersistentProcessSessionManager(session, store, codecs, Guid.NewGuid().ToString("N"),
            root, null, static () => true);
        var control = new FakePersistentControl { FailInputAfterPartialWrite = true };
        try
        {
            var started = await manager.StartAsync(run, lane, Spec(root, TimeSpan.FromSeconds(30)),
                new FakePersistentLauncher(control), null, TestContext.Current.CancellationToken);
            var handle = Assert.IsType<string>(started.Handle);
            await Assert.ThrowsAsync<ProcessInputOutcomeUnknownException>(() => manager.SendAsync(handle, run, lane,
                "finish", false, null, null, TestContext.Current.CancellationToken).AsTask());
            var state = manager.Read(handle, run, lane, 0, 8, out _, out _, out _, out var observedState, out _);
            Assert.Equal("unknown", observedState);
            Assert.Empty(state);
            var latest = store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<ProcessSessionStateRecorded>().Last();
            Assert.Equal("unknown", latest.State);
            Assert.Equal("process_input_outcome_unresolved:IOException", latest.Diagnostic);
            Assert.Equal("fin", Encoding.UTF8.GetString(control.Input.ToArray()));
        }
        finally
        {
            await manager.StopAllAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Process_session_binds_scope_pages_output_and_waits_for_stop_or_timeout()
    {
        var session = SessionId.New();
        var run = RunId.New().ToString();
        var lane = LaneId.New().ToString();
        var root = Path.Combine(Path.GetTempPath(), "OmniProcessSession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var ownsLease = true;
        var leaseGeneration = Guid.NewGuid().ToString("N");
        var manager = new PersistentProcessSessionManager(session, store, codecs, leaseGeneration, root, null, () => ownsLease);
        var control = new FakePersistentControl();
        var launcher = new FakePersistentLauncher(control);
        try
        {
            var started = await manager.StartAsync(run, lane, Spec(root, TimeSpan.FromSeconds(30)), launcher,
                null, TestContext.Current.CancellationToken);
            Assert.Equal("running", started.State);
            var handle = Assert.IsType<string>(started.Handle);
            Assert.NotNull(manager.ControlClaim(handle, run, lane));
            Assert.Null(manager.ControlClaim(handle, run, RunId.New().ToString()));
            Assert.Throws<KeyNotFoundException>(() => manager.Read(handle, run, LaneId.New().ToString(), 0, 4,
                out _, out _, out _, out _, out _));

            control.Output.WriteChunk(Encoding.UTF8.GetBytes("one\n"));
            await WaitUntilAsync(() => Read(manager, handle, run, lane, 0).HighWater >= 1);
            var first = manager.Read(handle, run, lane, 0, 1, out var firstCursor, out _, out var firstGap, out _, out _);
            Assert.False(firstGap);
            Assert.Equal("one\n", Assert.Single(first).Text);

            control.Output.WriteChunk(Encoding.UTF8.GetBytes("two\n"));
            await WaitUntilAsync(() => Read(manager, handle, run, lane, firstCursor).HighWater > firstCursor);
            var second = manager.Read(handle, run, lane, firstCursor, 1, out var secondCursor, out _, out var secondGap, out _, out _);
            Assert.False(secondGap);
            Assert.Equal("two\n", Assert.Single(second).Text);
            Assert.True(secondCursor > firstCursor);

            var delayedUtf8Read = manager.ReadAsync(handle, run, lane, secondCursor, 8,
                TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            _ = Task.Run(async () =>
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
                control.Output.WriteChunk([0xC3]);
                await Task.Delay(25, TestContext.Current.CancellationToken);
                control.Output.WriteChunk([0xB1, (byte)'\n']);
            }, TestContext.Current.CancellationToken);
            var delayedUtf8 = await delayedUtf8Read;
            Assert.Contains(delayedUtf8.Output, chunk => chunk.Text == "ñ\n");
            var safeCursor = delayedUtf8.HighWater;

            var splitSecret = "m9-split-secret-" + Guid.NewGuid().ToString("N");
            SecretRedactorRegistry.Install(SecretRedactor.Shared);
            SecretRedactorRegistry.Register(splitSecret);
            var splitAt = splitSecret.Length / 2;
            control.Output.WriteChunk(Encoding.UTF8.GetBytes("prefix-" + splitSecret[..splitAt]));
            control.Output.WriteChunk(Encoding.UTF8.GetBytes(splitSecret[splitAt..] + "-suffix\u001b]0;untrusted-title"));
            control.Output.WriteChunk(Encoding.UTF8.GetBytes("\u0007tail\n"));
            await WaitUntilAsync(() => Read(manager, handle, run, lane).HighWater >= safeCursor + 3);
            var sanitized = manager.Read(handle, run, lane, safeCursor, 8, out _, out _, out _, out _, out _);
            var sanitizedText = string.Concat(sanitized.Select(chunk => chunk.Text));
            Assert.DoesNotContain(splitSecret, sanitizedText, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", sanitizedText, StringComparison.Ordinal);
            Assert.DoesNotContain("untrusted-title", sanitizedText, StringComparison.Ordinal);
            Assert.Contains("tail", sanitizedText, StringComparison.Ordinal);

            for (var index = 0; index < 40; index++)
                control.Output.WriteChunk(Encoding.UTF8.GetBytes(new string('x', 8 * 1024)));
            await WaitUntilAsync(() => Read(manager, handle, run, lane, 0).HighWater > secondCursor + 80);
            var truncated = manager.Read(handle, run, lane, 0, 1, out _, out _, out var gap, out _, out _);
            Assert.True(gap);
            Assert.True(Assert.Single(truncated).Sequence > 1);

            await manager.SendAsync(handle, run, lane, "finish\n", eof: true, null, null,
                TestContext.Current.CancellationToken);
            Assert.Equal("finish\n", Encoding.UTF8.GetString(control.Input.ToArray()));
            Assert.True(control.InputClosed);

            ownsLease = false;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await manager.SendAsync(handle, run, lane,
                "blocked", false, null, null, TestContext.Current.CancellationToken));
            ownsLease = true;
            Assert.Equal("stopped", await manager.StopAsync(handle, run, lane, null, TestContext.Current.CancellationToken));
            var final = manager.Read(handle, run, lane, secondCursor, 8, out _, out _, out _, out var state, out _);
            Assert.Equal("stopped", state);
            Assert.True(final.Count > 0);
            Assert.True(control.Terminated);

            var timeoutControl = new FakePersistentControl();
            var timeoutStarted = await manager.StartAsync(run, lane, Spec(root, TimeSpan.FromMilliseconds(30)),
                new FakePersistentLauncher(timeoutControl), null, TestContext.Current.CancellationToken);
            var timeoutHandle = Assert.IsType<string>(timeoutStarted.Handle);
            await WaitUntilAsync(() => Read(manager, timeoutHandle, run, lane).State == "timed_out");
            Assert.True(timeoutControl.Terminated);

            var recyclableHandles = new List<string>();
            for (var index = 0; index < 34; index++)
            {
                var process = new FakePersistentControl();
                var startedProcess = await manager.StartAsync(run, lane, Spec(root, TimeSpan.FromSeconds(30)),
                    new FakePersistentLauncher(process), null, TestContext.Current.CancellationToken);
                Assert.Equal("running", startedProcess.State);
                recyclableHandles.Add(startedProcess.Handle!);
                Assert.Equal("stopped", await manager.StopAsync(startedProcess.Handle!, run, lane, null,
                    TestContext.Current.CancellationToken));
            }
            var evictedTerminalCount = recyclableHandles.Count(handle =>
            {
                try { _ = manager.Read(handle, run, lane, 0, 4, out _, out _, out _, out _, out _); return false; }
                catch (KeyNotFoundException) { return true; }
            });
            Assert.True(evictedTerminalCount > 0, "Confirmed terminal handles should be recycled once the session cap is reached.");
        }
        finally
        {
            ownsLease = true;
            await manager.StopAllAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Process_read_holds_only_sensitive_suffixes_between_calls_and_flushes_terminal_sequences()
    {
        var session = SessionId.New(); var run = RunId.New().ToString(); var lane = LaneId.New().ToString();
        var root = Path.Combine(Path.GetTempPath(), "OmniProcessIncrementalRedaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manager = new PersistentProcessSessionManager(session, new InMemoryEventStore(), EventCodecs.Create(),
            Guid.NewGuid().ToString("N"), root, null, static () => true);
        var control = new FakePersistentControl();
        try
        {
            var started = await manager.StartAsync(run, lane, Spec(root, TimeSpan.FromSeconds(30)),
                new FakePersistentLauncher(control), null, TestContext.Current.CancellationToken);
            var handle = Assert.IsType<string>(started.Handle);

            SecretRedactorRegistry.Install(SecretRedactor.Shared);
            control.Output.WriteChunk(Encoding.UTF8.GetBytes("READY> "));
            var promptRead = await manager.ReadAsync(handle, run, lane, 0, 8,
                TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            var prompt = promptRead.Output;
            var promptCursor = promptRead.Last;
            Assert.Equal("READY> ", string.Concat(prompt.Select(chunk => chunk.Text)));
            Assert.True(promptCursor > 0, "A plain interactive prompt without a newline must be delivered immediately.");

            var secret = "m9-cross-call-secret-" + Guid.NewGuid().ToString("N");
            SecretRedactorRegistry.Register(secret);
            var split = secret.Length / 2;
            control.Output.WriteChunk(Encoding.UTF8.GetBytes("ordinary-prefix:" + secret[..split]));
            var withheld = await manager.ReadAsync(handle, run, lane, promptCursor, 8,
                TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            var firstResponse = string.Concat(withheld.Output.Select(chunk => chunk.Text));
            Assert.Equal("ordinary-prefix:", firstResponse);
            Assert.DoesNotContain(secret[..split], firstResponse, StringComparison.Ordinal);
            Assert.Equal(withheld.HighWater, withheld.Last);
            Assert.True(withheld.Last > promptCursor);

            control.Output.WriteChunk(Encoding.UTF8.GetBytes(secret[split..] + "\n"));
            var afterSecretRead = await manager.ReadAsync(handle, run, lane, withheld.Last, 8,
                TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            var afterSecret = afterSecretRead.Output;
            var afterSecretCursor = afterSecretRead.Last;
            var allResponses = string.Concat(prompt.Select(chunk => chunk.Text)) + firstResponse
                + string.Concat(afterSecret.Select(chunk => chunk.Text));
            Assert.DoesNotContain(secret, allResponses, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", allResponses, StringComparison.Ordinal);
            Assert.True(afterSecretCursor > promptCursor);

            control.Output.WriteChunk(Encoding.UTF8.GetBytes("\u001b]0;private-title"));
            var beforeOscTerminator = await manager.ReadAsync(handle, run, lane, afterSecretCursor, 8,
                TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            Assert.Empty(beforeOscTerminator.Output);
            Assert.Equal(afterSecretCursor, beforeOscTerminator.Last);
            control.Output.WriteChunk(Encoding.UTF8.GetBytes("\u0007prompt> "));
            var afterOscRead = await manager.ReadAsync(handle, run, lane, afterSecretCursor, 8,
                TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            var afterOsc = afterOscRead.Output;
            var oscText = string.Concat(afterOsc.Select(chunk => chunk.Text));
            Assert.DoesNotContain("private-title", oscText, StringComparison.Ordinal);
            Assert.Contains("prompt> ", oscText, StringComparison.Ordinal);
        }
        finally
        {
            await manager.StopAllAsync();
            CleanupOwnedTree(root);
        }
    }

    [Fact]
    public async Task Stopped_process_proposal_survives_host_reopen_without_relaunching_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniProcessProposal-" + Guid.NewGuid().ToString("N"));
        var artifactRoot = Path.Combine(Path.GetTempPath(), "OmniProcessArtifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await Git(root, "init", "--quiet");
            await Git(root, "config", "user.name", "OmniCore test");
            await Git(root, "config", "user.email", "test@localhost");
            var target = Path.Combine(root, "allowed.txt");
            await File.WriteAllTextAsync(target, "before", TestContext.Current.CancellationToken);
            await Git(root, "add", "allowed.txt");
            await Git(root, "commit", "--quiet", "-m", "baseline");

            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New(); var run = RunId.New().ToString(); var lane = LaneId.New().ToString();
            var call = ToolCallId.New(); var turn = TurnId.New().ToString();
            var claims = new ResourceClaims(["allowed.txt"], ["allowed.txt"], [],
                new ProcessClaim("fixture", [], "PrivateProcessWriter", Isolation: ProcessIsolation.PrivateProcessWorkspace), []);
            var stage = ExtensionWriteWorkspace.Create(root, Path.Combine(Path.GetTempPath(), "OmniProcessStages"), claims);
            var control = new FakePersistentControl();
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, "fixture", [], stage.Root,
                new Dictionary<string, string>(), [new SandboxAllowedPath(stage.Root, SandboxPathAccess.Read | SandboxPathAccess.Write)],
                new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(30)) { Duplex = true };
            var sessionManager = new PersistentProcessSessionManager(session, store, codecs, Guid.NewGuid().ToString("N"),
                root, new FileArtifactStore(artifactRoot), static () => true);
            var started = await sessionManager.StartAsync(run, lane, launch,
                new FakePersistentLauncher(control, spec => File.WriteAllText(Path.Combine(spec.WorkingDirectory!, "allowed.txt"), "after")),
                null, TestContext.Current.CancellationToken, stage, root, call, new ToolId("process.start"), turn, claims);
            Assert.Equal("running", started.State);
            var handle = Assert.IsType<string>(started.Handle);
            Assert.Equal("stopped", await sessionManager.StopAsync(handle, run, lane, null, TestContext.Current.CancellationToken));
            var (artifact, preview) = sessionManager.CreatePrivateProcessProposal(handle, run, lane, ToolCallId.New(),
                turn, root, new FileArtifactStore(artifactRoot));
            Assert.NotNull(artifact);
            Assert.NotNull(preview);
            using var previewDocument = JsonDocument.Parse(preview);
            var proposalId = previewDocument.RootElement.GetProperty("proposalId").GetString()!;
            Assert.Equal(artifact!.Hash.Value, previewDocument.RootElement.GetProperty("artifactHash").GetString());

            var latest = new Dictionary<string, ProcessSessionStateRecorded>(StringComparer.Ordinal);
            foreach (var envelope in store.ReadFrom(session, 1))
                if (codecs.Decode(envelope) is ProcessSessionStateRecorded evidence) latest[evidence.HandleId] = evidence;
            var reopenedArtifacts = new FileArtifactStore(artifactRoot);
            var reopened = new PersistentProcessSessionManager(session, store, codecs, Guid.NewGuid().ToString("N"),
                root, reopenedArtifacts, static () => true);
            reopened.RestoreUnknown(latest);
            var json = ((IProtectedArtifactStore)reopenedArtifacts).GetProtectedText(artifact,
                ExtensionTool.ProposalPurpose(root, proposalId));
            var proposal = JsonSerializer.Deserialize(json, ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal);
            Assert.NotNull(proposal);
            Assert.True(reopened.ValidateCurrentProcessBatchProposal(proposal!, root));
            var output = reopened.Read(handle, run, lane, 0, 8, out _, out _, out _, out var state, out _);
            Assert.Equal("stopped", state);
            Assert.Empty(output);
            Assert.False(File.Exists(target) && File.ReadAllText(target) == "after",
                "The private process proposal must remain unapplied after reopen.");
        }
        finally
        {
            CleanupOwnedTree(root);
            CleanupOwnedTree(artifactRoot);
        }
    }

    private static void CleanupOwnedTree(string root)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                if ((attributes & FileAttributes.Directory) != 0
                    && (attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(path);
            }
        }
        Directory.Delete(root, recursive: true);
    }

    private static SandboxLaunchSpec Spec(string root, TimeSpan timeout) => new(SandboxStrength.None,
        "fixture", [], root, new Dictionary<string, string>(), [], new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), timeout)
    { Duplex = true };

    private static (long HighWater, string State) Read(PersistentProcessSessionManager manager, string handle,
        string run, string lane, long after = 0)
    {
        _ = manager.Read(handle, run, lane, after, 1, out _, out var highWater, out _, out var state, out _);
        return (highWater, state);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static async Task Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardError = true,
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git fixture.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException("git fixture failed: " + await error + await output);
    }

    private sealed class FakePersistentLauncher(FakePersistentControl control,
        Action<SandboxLaunchSpec>? onStart = null) : ISandboxProcessLauncher
    {
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onStart?.Invoke(launch);
            return ValueTask.FromResult<ISandboxProcessControl>(control);
        }
    }

    private sealed class CountingLauncher : ISandboxProcessLauncher
    {
        public int StartCount { get; private set; }
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
        {
            StartCount++;
            throw new InvalidOperationException("A denied or pending process must never launch.");
        }
    }

    private sealed class AmbiguousLauncher : ISandboxProcessLauncher
    {
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken) =>
            throw new IOException("fixture launch outcome is ambiguous");
    }

    private sealed class FakePersistentControl : ISandboxPersistentProcessControl
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PartialFailureStream _partialFailureInput;
        internal readonly ChannelStream Output = new();
        internal readonly ChannelStream Error = new();
        internal readonly MemoryStream Input = new();
        public FakePersistentControl() => _partialFailureInput = new PartialFailureStream(Input);
        internal bool FailInputAfterPartialWrite { get; set; }
        public Stream StandardInput => FailInputAfterPartialWrite ? _partialFailureInput : Input;
        public Stream StandardOutput => Output;
        public Stream StandardError => Error;
        public bool IsTerminal => false;
        internal bool InputClosed { get; private set; }
        internal bool Terminated { get; private set; }
        public ValueTask<int> WaitForExitCodeAsync(CancellationToken cancellationToken) => new(_exit.Task.WaitAsync(cancellationToken));
        public ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken) =>
            new(WaitForExitCodeAsync(cancellationToken).AsTask().ContinueWith(task => new SandboxProcessOutput(task.Result, []),
                cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        public async ValueTask TerminateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Terminated = true;
            Output.Complete(); Error.Complete();
            _exit.TrySetResult(143);
            await ValueTask.CompletedTask;
        }
        public ValueTask CloseInputAsync() { InputClosed = true; return ValueTask.CompletedTask; }
        public void ResizeTerminal(short columns, short rows) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() { Output.Complete(); Error.Complete(); return ValueTask.CompletedTask; }
    }

    private sealed class PartialFailureStream(Stream destination) : Stream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partial = Math.Max(1, buffer.Length / 2);
            destination.Write(buffer.Span[..partial]);
            throw new IOException("fixture pipe failed after a partial input write");
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            { SingleReader = true, SingleWriter = false });
        private byte[]? _current;
        private int _offset;
        private int _readCount;
        internal int ReadCount => Volatile.Read(ref _readCount);
        public void WriteChunk(byte[] bytes) => _chunks.Writer.TryWrite(bytes);
        public void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset >= _current.Length)
            {
                _current = null; _offset = 0;
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_chunks.Reader.TryRead(out _current)) continue;
                Interlocked.Increment(ref _readCount);
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
