using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Security;
using OmniCore.Sandbox;
using OmniCore.Tools;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Pipeline-unit coverage only: the launcher is counted/faked here. These tests prove the
/// ToolRuntime preflight and one-call resume contract, not platform sandbox or child-process UI.
/// </summary>
public sealed class WeakSandboxConsentPreflightTests
{
    [Fact]
    public void Autonomous_authorized_process_falls_back_without_a_per_call_question()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "omni-auto-consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var launcher = new CountingLauncher(StrongSandboxPreflightResult.UnavailableBeforeLaunch, false);
        var events = new List<DomainEventPayload>();
        var policy = new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Act)
            .WithProfile(PermissionProfile.Autonomous);
        var runtime = new ToolRuntime(new FakeCatalog().Add(new ProcessExecTool(launcher,
            new PathBoundaryValidator(), SandboxStrength.Strong)), policy,
            payload => { events.Add(payload); return VoidBox.Instance; }, null, new FixedResolver());
        var context = new ToolExecutionContext(workspace, readRegistry: null, emitEvent: events.Add,
            allowDeferredWeakSandboxConsent: true);
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe") : "/bin/echo";
        var call = new ValidatedToolCall(ToolCallId.New(), new("process.exec"), "auto-consent",
            JsonSerializer.Serialize(new { executable, argv = new[] { "fixture" }, cwd = ".", timeoutSeconds = 5 }));
        var outcome = runtime.Run(call, new(workspace, DateTimeOffset.UtcNow), context, false,
            TestContext.Current.CancellationToken);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, launcher.WeakProcessesStarted);
        Assert.DoesNotContain(events, item => item is InteractionRequested);
        Assert.False(context.WeakSandboxConsentForThisCall);
        Directory.Delete(workspace);
    }

    [Fact]
    public void Scripted_executor_persists_weak_interaction_with_exact_execution_scope()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "omni-weak-consent-envelope-", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        using var store = new SqliteEventStore(Path.Combine(workspace, "journal.db"));
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.OpenInSession(store, session, workspace);
        var stream = new EventStream(store, codecs, session);
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "weak-envelope-fixture",
            JsonSerializer.Serialize(new
            {
                executable = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe")
                    : "/bin/echo",
                argv = new[] { "fixture" }, cwd = ".", timeoutSeconds = 5,
            }));
        var launcher = new CountingLauncher(StrongSandboxPreflightResult.UnavailableBeforeLaunch,
            strongLaunchUnknown: false);
        var catalog = new FakeCatalog().Add(new ProcessExecTool(launcher, new PathBoundaryValidator()));
        var executor = new ScriptedToolExecutor(catalog,
            ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Allow), workspace,
            boundary: null, audit: null, interactionResponder: null, isInteractive: false,
            allowDeferredWeakSandboxConsent: true);
        var turn = TurnId.New();
        var execution = ExecutionId.New();
        // ToolCallId is deliberately absent from the ambient caller scope. The executor must
        // bind the interaction envelope to the validated call it is currently emitting.
        var scope = new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn, null, execution);

        using (ExecutionScope.Begin(scope))
        {
            var outcome = executor.ExecuteTool(call, userApprovesAsk: false, CancellationToken.None, stream);
            Assert.False(outcome.Succeeded);
            Assert.Equal(ToolCallState.Authorized, outcome.FinalState);
            Assert.Null(outcome.Effect);
        }

        Assert.Empty(launcher.Attempts);
        Assert.Equal(0, launcher.WeakProcessesStarted);
        var persisted = Assert.Single(store.ReadFrom(session, 1), evt =>
            evt.RunId == run.RunId && codecs.Decode(evt) is InteractionRequested request
                && request.Kind == InteractionKind.WeakSandboxConsent);
        Assert.Equal(session, persisted.SessionId);
        Assert.Equal(run.RunId, persisted.RunId);
        Assert.Equal(run.RootTask, persisted.TaskId);
        Assert.Equal(run.RootLane, persisted.LaneId);
        Assert.Equal(turn, persisted.TurnId);
        Assert.Equal(call.ToolCallId, persisted.ToolCallId);
        Assert.Equal(execution, persisted.ExecutionId);
        Assert.Equal("OmniCore.Engine.EventStream", persisted.Source);

        store.Close();
        SqliteConnection.ClearPool((SqliteConnection)store.Connection);
        Directory.Delete(workspace, recursive: true);
    }

    [Fact]
    public void Unavailable_preflight_pends_before_started_without_invoking_the_launcher()
    {
        using var fixture = PendingFixture.Create();

        Assert.False(fixture.Initial.Succeeded);
        Assert.Equal(ToolCallState.Authorized, fixture.Initial.FinalState);
        Assert.Equal(EffectOutcome.None, fixture.Initial.Effect);
        Assert.NotNull(fixture.Initial.PendingInteractionId);
        Assert.Equal(1, fixture.Launcher.PreflightChecks);
        Assert.Empty(fixture.Launcher.Attempts);
        Assert.Equal(0, fixture.Launcher.WeakProcessesStarted);

        var interactionEvent = Assert.Single(fixture.Events, item =>
            item is InteractionRequested { Kind: InteractionKind.WeakSandboxConsent });
        var interaction = Assert.IsType<InteractionRequested>(interactionEvent);
        Assert.Equal(fixture.Initial.PendingInteractionId, interaction.InteractionId);
        using var options = JsonDocument.Parse(interaction.OptionsJson);
        Assert.Equal(new[] { "consent_once", "deny" }, options.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()).ToArray());
        Assert.Equal("deny", interaction.DefaultOptionId);

        var requested = Assert.Single(fixture.Events, item => item is ToolCallRequested);
        var prepared = Assert.Single(fixture.Events, item => item is ToolCallPrepared);
        var authorized = Assert.Single(fixture.Events, item => item is ToolCallAuthorized);
        Assert.True(IndexOf(fixture.Events, requested) < IndexOf(fixture.Events, prepared));
        Assert.True(IndexOf(fixture.Events, prepared) < IndexOf(fixture.Events, authorized));
        Assert.True(IndexOf(fixture.Events, authorized) < IndexOf(fixture.Events, interactionEvent));
        Assert.Equal(ToolCallState.Authorized, FoldToolCall(fixture.Events, fixture.Call.ToolCallId));
        Assert.DoesNotContain(fixture.Events, item => item is ToolCallStarted or ToolCallSucceeded
            or ToolCallEffectUnknown or PermissionGranted);
    }

    [Fact]
    public void Explicit_deny_resumes_the_exact_call_without_starting_a_process()
    {
        using var fixture = PendingFixture.Create();
        fixture.RecordChoice("deny");

        var outcome = fixture.Resume("deny");

        Assert.False(outcome.Succeeded);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.Equal(EffectOutcome.None, outcome.Effect);
        Assert.Equal(0, fixture.Launcher.WeakProcessesStarted);
        Assert.Empty(fixture.Launcher.Attempts);
        Assert.Single(fixture.Events, item => item is PermissionDenied denied
            && denied.ToolCallId == fixture.Call.ToolCallId);
        Assert.DoesNotContain(fixture.Events, item => item is ToolCallStarted or ToolCallSucceeded
            or ToolCallEffectUnknown or PermissionGranted);
        Assert.Single(fixture.Events, item => item is InteractionResolved resolved
            && resolved.InteractionId == fixture.Interaction!.InteractionId
            && resolved.OptionId == "deny" && resolved.Cause == InteractionCause.User);
        Assert.Equal(ToolCallState.Rejected, FoldToolCall(fixture.Events, fixture.Call.ToolCallId));
    }

    [Fact]
    public void Consent_once_retries_only_the_same_call_as_weak_and_starts_one_process()
    {
        using var fixture = PendingFixture.Create();
        fixture.RecordChoice("consent_once");

        var outcome = fixture.Resume("consent_once");

        Assert.True(outcome.Succeeded, outcome.Summary);
        Assert.Equal(ToolCallState.Succeeded, outcome.FinalState);
        Assert.Equal(EffectOutcome.Applied, outcome.Effect);
        Assert.Equal(1, fixture.Launcher.WeakProcessesStarted);
        Assert.Equal(new[] { SandboxStrength.Strong, SandboxStrength.Weak },
            fixture.Launcher.Attempts.Select(attempt => attempt.Strength).ToArray());
        Assert.Single(fixture.Events, item => item is ToolCallStarted started
            && started.ToolCallId == fixture.Call.ToolCallId);
        Assert.Single(fixture.Events, item => item is ToolCallSucceeded succeeded
            && succeeded.ToolCallId == fixture.Call.ToolCallId);
        Assert.Single(fixture.Events, item => item is ToolCallRequested requested
            && requested.ToolCallId == fixture.Call.ToolCallId);
        Assert.Single(fixture.Events, item => item is InteractionRequested
            { Kind: InteractionKind.WeakSandboxConsent });
        Assert.DoesNotContain(fixture.Events, item => item is PermissionGranted);
        Assert.Equal(ToolCallState.Succeeded, FoldToolCall(fixture.Events, fixture.Call.ToolCallId));
    }

    [Fact]
    public void Resume_rejects_changed_arguments_or_resolved_process_claim_before_launch()
    {
        using (var changedArguments = PendingFixture.Create())
        {
            changedArguments.RecordChoice("consent_once");
            var tampered = new ValidatedToolCall(changedArguments.Call.ToolCallId,
                changedArguments.Call.ToolId, changedArguments.Call.ProviderCallId,
                changedArguments.Call.NormalizedArgumentsJson.Replace("\"timeoutSeconds\":5",
                    "\"timeoutSeconds\":6", StringComparison.Ordinal));

            var outcome = changedArguments.Resume("consent_once", tampered);

            Assert.False(outcome.Succeeded);
            Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
            Assert.Equal(EffectOutcome.None, outcome.Effect);
            Assert.Empty(changedArguments.Launcher.Attempts);
            Assert.DoesNotContain(changedArguments.Events, item => item is ToolCallStarted);
            Assert.Equal(ToolCallState.Rejected,
                FoldToolCall(changedArguments.Events, changedArguments.Call.ToolCallId));
        }

        using (var changedResolvedClaim = PendingFixture.Create(changeResolverOnResume: true))
        {
            changedResolvedClaim.RecordChoice("consent_once");

            var changed = changedResolvedClaim.Resume("consent_once");

            Assert.False(changed.Succeeded);
            Assert.Equal(ToolCallState.Rejected, changed.FinalState);
            Assert.Equal(EffectOutcome.None, changed.Effect);
            Assert.Empty(changedResolvedClaim.Launcher.Attempts);
            Assert.DoesNotContain(changedResolvedClaim.Events, item => item is ToolCallStarted);
            Assert.Single(changedResolvedClaim.Events, item => item is PermissionDenied denied
                && denied.ToolCallId == changedResolvedClaim.Call.ToolCallId);
            Assert.Equal(ToolCallState.Rejected,
                FoldToolCall(changedResolvedClaim.Events, changedResolvedClaim.Call.ToolCallId));
        }
    }

    [Fact]
    public void Resume_rechecks_current_permission_policy_and_does_not_treat_consent_as_a_grant()
    {
        using var fixture = PendingFixture.Create();
        fixture.RecordChoice("consent_once");
        fixture.Policy.Decision = PermissionDecision.Deny;

        var outcome = fixture.Resume("consent_once");

        Assert.False(outcome.Succeeded);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.Equal(EffectOutcome.None, outcome.Effect);
        Assert.Empty(fixture.Launcher.Attempts);
        Assert.DoesNotContain(fixture.Events, item => item is ToolCallStarted or PermissionGranted);
        Assert.Single(fixture.Events, item => item is PermissionDenied denied
            && denied.ToolCallId == fixture.Call.ToolCallId);
        Assert.Equal(ToolCallState.Rejected, FoldToolCall(fixture.Events, fixture.Call.ToolCallId));
    }

    [Fact]
    public void Unknown_or_unproven_strong_launch_never_opens_weak_consent_or_falls_back()
    {
        using var unknown = PendingFixture.Create(preflightResult: StrongSandboxPreflightResult.Unknown,
            strongLaunchUnknown: true);

        var unknownOutcome = unknown.Initial;

        Assert.False(unknownOutcome.Succeeded);
        Assert.Equal(ToolCallState.EffectUnknown, unknownOutcome.FinalState);
        Assert.Equal(EffectOutcome.Unknown, unknownOutcome.Effect);
        Assert.Equal(1, unknown.Launcher.PreflightChecks);
        Assert.Equal(new[] { SandboxStrength.Strong },
            unknown.Launcher.Attempts.Select(attempt => attempt.Strength).ToArray());
        Assert.Equal(0, unknown.Launcher.WeakProcessesStarted);
        Assert.DoesNotContain(unknown.Events, item => item is InteractionRequested
            { Kind: InteractionKind.WeakSandboxConsent });
        Assert.Single(unknown.Events, item => item is ToolCallEffectUnknown effect
            && effect.ToolCallId == unknown.Call.ToolCallId);
        Assert.Equal(ToolCallState.EffectUnknown, FoldToolCall(unknown.Events, unknown.Call.ToolCallId));

    }

    private static ToolCallState FoldToolCall(IEnumerable<DomainEventPayload> events, ToolCallId expectedCallId)
    {
        var state = ToolCallState.Requested;
        foreach (var payload in events.Where(IsToolCallLifecycleEvent))
        {
            Assert.Equal(expectedCallId, ToolCallIdOf(payload));
            state = StateMachines.ApplyToolCall(state, payload);
        }
        return state;
    }

    private static bool IsToolCallLifecycleEvent(DomainEventPayload payload) => payload is
        ToolCallRequested or ToolCallPrepared or ToolCallRejected or PermissionEvaluated or PermissionRequested
        or PermissionGranted or PermissionDenied or ToolCallAuthorized or ToolCallStarted or ToolCallSucceeded
        or ToolCallFailed or ToolCallEffectUnknown or ToolCallCancelled or ToolCallReconciled;

    private static ToolCallId ToolCallIdOf(DomainEventPayload payload) => payload switch
    {
        ToolCallRequested value => value.ToolCallId,
        ToolCallPrepared value => value.ToolCallId,
        ToolCallRejected value => value.ToolCallId,
        PermissionEvaluated value => value.ToolCallId,
        PermissionRequested value => value.ToolCallId,
        PermissionGranted value => value.ToolCallId,
        PermissionDenied value => value.ToolCallId,
        ToolCallAuthorized value => value.ToolCallId,
        ToolCallStarted value => value.ToolCallId,
        ToolCallSucceeded value => value.ToolCallId,
        ToolCallFailed value => value.ToolCallId,
        ToolCallEffectUnknown value => value.ToolCallId,
        ToolCallCancelled value => value.ToolCallId,
        ToolCallReconciled value => value.ToolCallId,
        _ => throw new ArgumentOutOfRangeException(nameof(payload)),
    };

    private static int IndexOf(IReadOnlyList<DomainEventPayload> events, DomainEventPayload payload)
    {
        for (var index = 0; index < events.Count; index++)
            if (ReferenceEquals(events[index], payload)) return index;
        return -1;
    }

    private sealed class PendingFixture : IDisposable
    {
        private PendingFixture(string workspace, CountingLauncher launcher, MutablePermissionPolicy policy,
            IExecutableResolver resolver, bool changeResolverOnResume)
        {
            Workspace = workspace;
            Launcher = launcher;
            Policy = policy;
            Resolver = resolver;
            ChangeResolverOnResume = changeResolverOnResume;
            Events = new List<DomainEventPayload>();
            var tool = new ProcessExecTool(launcher, new PathBoundaryValidator(), SandboxStrength.Strong);
            Runtime = new ToolRuntime(new FakeCatalog().Add(tool), policy, payload =>
            {
                Events.Add(payload);
                return VoidBox.Instance;
            }, null, resolver);
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe")
                : "/bin/echo";
            Call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "fixture-call",
                JsonSerializer.Serialize(new
                {
                    executable,
                    argv = new[] { "fixture" },
                    cwd = ".",
                    timeoutSeconds = 5,
                }));
            Preparation = new ToolPreparationContext(workspace, DateTimeOffset.UnixEpoch);
            InitialContext = new ToolExecutionContext(workspace, readRegistry: null, emitEvent: Events.Add,
                allowDeferredWeakSandboxConsent: true);
            Initial = Runtime.Run(Call, Preparation, InitialContext, userApprovesAsk: false,
                CancellationToken.None);
            Interaction = Events.OfType<InteractionRequested>().SingleOrDefault(item =>
                item.Kind == InteractionKind.WeakSandboxConsent);
            PreparedArguments = Events.OfType<ToolCallPrepared>().Single().ToolIntentJson;
            IntentFingerprint = Interaction is null ? null : ReadFingerprint(Interaction);
        }

        public string Workspace { get; }
        public CountingLauncher Launcher { get; }
        public MutablePermissionPolicy Policy { get; }
        public IExecutableResolver Resolver { get; }
        public bool ChangeResolverOnResume { get; }
        public List<DomainEventPayload> Events { get; }
        public ToolRuntime Runtime { get; }
        public ValidatedToolCall Call { get; }
        public ToolPreparationContext Preparation { get; }
        public ToolExecutionContext InitialContext { get; }
        public ToolRuntime.Outcome Initial { get; }
        public InteractionRequested? Interaction { get; }
        public string PreparedArguments { get; }
        public string? IntentFingerprint { get; }

        public static PendingFixture Create(StrongSandboxPreflightResult preflightResult =
            StrongSandboxPreflightResult.UnavailableBeforeLaunch, bool strongLaunchUnknown = false,
            bool changeResolverOnResume = false)
        {
            var workspace = Path.Combine(Path.GetTempPath(), "omni-weak-consent-pipeline-",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var launcher = new CountingLauncher(preflightResult, strongLaunchUnknown);
            var policy = new MutablePermissionPolicy();
            IExecutableResolver resolver = changeResolverOnResume
                ? new SwitchingResolver()
                : new FixedResolver();
            return new PendingFixture(workspace, launcher, policy, resolver, changeResolverOnResume);
        }

        public void RecordChoice(string optionId) => Events.Add(new InteractionResolved(
            (Interaction ?? throw new InvalidOperationException("No pending consent in this fixture.")).InteractionId,
            optionId, InteractionCause.User));

        public ToolRuntime.Outcome Resume(string optionId, ValidatedToolCall? call = null,
            string? expectedFingerprint = null, string? expectedArguments = null)
        {
            if (ChangeResolverOnResume && Resolver is SwitchingResolver switching)
                switching.UseSecondPath = true;
            return Runtime.ResumeWeakSandboxConsent(call ?? Call, Preparation,
                new ToolExecutionContext(Workspace, readRegistry: null, emitEvent: Events.Add,
                    allowDeferredWeakSandboxConsent: true,
                    weakSandboxConsentForThisCall: optionId == "consent_once"),
                CancellationToken.None,
                (Interaction ?? throw new InvalidOperationException("No pending consent in this fixture.")).InteractionId,
                optionId, expectedArguments ?? PreparedArguments,
                expectedFingerprint ?? IntentFingerprint
                    ?? throw new InvalidOperationException("No preflight fingerprint."));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Workspace)) Directory.Delete(Workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string ReadFingerprint(InteractionRequested request)
        {
            using var document = JsonDocument.Parse(request.SubjectJson);
            return document.RootElement.GetProperty("intentFingerprint").GetString()
                ?? throw new InvalidOperationException("Preflight request did not carry an intent fingerprint.");
        }
    }

    private sealed class MutablePermissionPolicy : IPermissionPolicy
    {
        private readonly ScriptedPermissionPolicy _allow =
            ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Allow);
        private readonly ScriptedPermissionPolicy _deny =
            ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Deny);

        public PermissionDecision Decision { get; set; } = PermissionDecision.Allow;
        private ScriptedPermissionPolicy Current => Decision == PermissionDecision.Allow ? _allow : _deny;
        public PermissionDecisionRecord Evaluate(ToolIntent intent) => Current.Evaluate(intent);
        public AuthorizedToolIntent Authorize(ToolIntent intent) => Current.Authorize(intent);
        public AuthorizedToolIntent AuthorizeApproved(ToolIntent intent, GrantId? approvedGrant) =>
            Current.AuthorizeApproved(intent, approvedGrant);
    }

    private sealed class FixedResolver : IExecutableResolver
    {
        public ExecutableResolution Resolve(string executable, string workspaceRoot) =>
            new(executable, executable);
    }

    private sealed class SwitchingResolver : IExecutableResolver
    {
        public bool UseSecondPath { get; set; }
        public ExecutableResolution Resolve(string executable, string workspaceRoot) =>
            new(executable, UseSecondPath ? Path.Combine(workspaceRoot, "different-process.exe") : executable);
    }

    private sealed class CountingLauncher : ISandboxProcessLauncher, IStrongSandboxPreflight
    {
        private readonly StrongSandboxPreflightResult _preflightResult;
        private readonly bool _strongLaunchUnknown;

        public CountingLauncher(StrongSandboxPreflightResult preflightResult, bool strongLaunchUnknown)
        {
            _preflightResult = preflightResult;
            _strongLaunchUnknown = strongLaunchUnknown;
        }

        public int PreflightChecks { get; private set; }
        public int WeakProcessesStarted { get; private set; }
        public List<(SandboxStrength Strength, string Executable, string[] Arguments)> Attempts { get; } = [];

        public StrongSandboxPreflightResult CheckStrongAvailability(SandboxLaunchSpec launch)
        {
            PreflightChecks++;
            return _preflightResult;
        }

        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add((launch.RequestedStrength, launch.Executable, launch.Arguments.ToArray()));
            if (launch.RequestedStrength == SandboxStrength.Strong)
            {
                if (_strongLaunchUnknown)
                    throw new StrongSandboxLaunchOutcomeUnknownException("test: outcome unknown",
                        new IOException("test launcher uncertainty"));
                throw new StrongSandboxUnavailableBeforeLaunchException("test: prelaunch unavailable");
            }
            WeakProcessesStarted++;
            return ValueTask.FromResult<ISandboxProcessControl>(new SuccessfulProcess());
        }
    }

    private sealed class SuccessfulProcess : ISandboxProcessControl
    {
        public ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SandboxProcessOutput(0, Array.Empty<SandboxOutputChunk>()));
        public ValueTask TerminateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
