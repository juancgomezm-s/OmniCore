using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ModeAuthorityContractTests
{
    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";

    private static WireEnvelope Command(string payload) => WireEnvelope.Command(Ids.NewV7(), payload);

    [Fact]
    public void Adaptive_allowlist_is_frozen_and_expiry_uses_durable_UTC_grant_time()
    {
        var allowed = new List<RunMode> { RunMode.Plan, RunMode.Act };
        var granted = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var authorization = new ModeSwitchAuthorization(Guid.NewGuid(), 3, 1, "objective-sha256", 1,
            allowed, new ModeSwitchLimits(0, 0, 2, 4, 30, 0.25m), granted);
        allowed.Add(RunMode.Orchestrate);

        Assert.Equal(new[] { RunMode.Plan, RunMode.Act }, authorization.AllowedModes);
        Assert.False(authorization.IsExpiredAt(granted.AddSeconds(29)));
        Assert.True(authorization.IsExpiredAt(granted.AddSeconds(30)));
        Assert.Throws<ArgumentException>(() => authorization.IsExpiredAt(granted.ToOffset(TimeSpan.FromHours(1))));

        var widened = authorization with { AllowedModes = allowed };
        allowed.Clear();
        Assert.Equal(new[] { RunMode.Plan, RunMode.Act, RunMode.Orchestrate }, widened.AllowedModes);
    }

    [Fact]
    public void Authority_rejects_unknown_enums_and_modes_outside_the_authorized_set()
    {
        var limits = new ModeSwitchLimits(1, 1, 2, 4, 30, 1m);
        var authorization = new ModeSwitchAuthorization(Guid.NewGuid(), 2, 1, "digest", 1,
            new[] { RunMode.Plan }, limits, DateTimeOffset.UtcNow);
        var authority = new RunModeAuthority(new RunId(Guid.NewGuid()), 2, RunMode.Act,
            ExecutionStrategy.Direct, ProductEffort.UltraCode, false, true, 1, "digest", 1, authorization);

        Assert.Throws<ArgumentException>(authority.Validate);
        Assert.Throws<ArgumentException>(() => (authority with { Strategy = (ExecutionStrategy)999 }).Validate());
        Assert.Throws<ArgumentException>(() => (authority with { ProductEffort = (ProductEffort)999 }).Validate());
    }

    [Fact]
    public void Authority_query_distinguishes_stored_selection_from_current_effective_authority()
    {
        var now = new DateTimeOffset(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);
        var digest = RunModeAuthority.ObjectiveDigestFor("effective authority fixture");
        var authorization = new ModeSwitchAuthorization(Guid.NewGuid(), 2, 1, digest, 1,
            new[] { RunMode.Plan, RunMode.Act }, new ModeSwitchLimits(1, 1, 2, 4, 30, 1m),
            now.AddSeconds(-30));
        var authority = new RunModeAuthority(new RunId(Guid.NewGuid()), 2, RunMode.Plan,
            ExecutionStrategy.Direct, ProductEffort.UltraCode, false, true, 1, digest, 1, authorization);

        using var expired = System.Text.Json.JsonDocument.Parse(OmniServer.ModeAuthorityJson(authority, now));
        Assert.Single(expired.RootElement.EnumerateObject(), property => property.Name == "effectiveAutoModeSwitch");
        Assert.True(expired.RootElement.GetProperty("autoModeSwitch").GetBoolean());
        Assert.False(expired.RootElement.GetProperty("effectiveAutoModeSwitch").GetBoolean());
        Assert.True(expired.RootElement.GetProperty("authorization").GetProperty("expired").GetBoolean());

        var current = authority with
        {
            Authorization = authorization with { GrantedAtUtc = now.AddSeconds(-29) },
        };
        using var live = System.Text.Json.JsonDocument.Parse(OmniServer.ModeAuthorityJson(current, now));
        Assert.True(live.RootElement.GetProperty("autoModeSwitch").GetBoolean());
        Assert.True(live.RootElement.GetProperty("effectiveAutoModeSwitch").GetBoolean());
        Assert.False(live.RootElement.GetProperty("authorization").GetProperty("expired").GetBoolean());
    }

    [Fact]
    public void Explicit_explore_start_remains_plan_even_when_the_session_default_is_act()
    {
        var server = OmniHost.CreateInMemoryServer();
        var ack = server.Send(Command(Payload(JsonObj.Field("cmd", "explore.start"),
            JsonObj.Field("objective", "inspect the fixture"))), TestContext.Current.CancellationToken);

        Assert.Equal("ok", ack.Status);
        Assert.Equal(RunMode.Plan, server.CurrentRunMode());
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var projection = RunProjection.Replay(session, run, server.AcquireCodecs(),
            server.AcquireStore().ReadFrom(session, 1));
        Assert.Equal(RunMode.Plan, projection.Mode);
        Assert.Equal(ProductEffort.Standard, projection.ModeAuthority?.ProductEffort);
        Assert.False(projection.ModeAuthority?.AutoModeSwitch);
    }

    [Fact]
    public void Unimplemented_policy_transition_origin_is_rejected_before_journal_append()
    {
                var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
            JsonObj.Field("text", "no implicit policy transitions"))), TestContext.Current.CancellationToken).Status);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var before = server.AcquireStore().CurrentSequence(session);
        var transition = new RunModeTransitionAuthorized(run, RunMode.Plan, RunMode.Plan,
            "automatic policy transition", "UltraCodePolicy", Ids.NewV7(), 2, 1,
            RunModeAuthority.ObjectiveDigestFor("no implicit policy transitions"), 1, null);

        Assert.Throws<InvalidStateTransitionException>(() =>
            new EventStream(server.AcquireStore(), server.AcquireCodecs(), session).Append(transition));
        Assert.Equal(before, server.AcquireStore().CurrentSequence(session));
    }

    [Fact]
    public void Objective_digest_binds_the_redacted_durable_objective_used_by_replay()
    {
        const string original = "new session objective";
        var durable = new PiiRedactor().Redact(original);

        Assert.NotEqual(original, durable);
        Assert.Equal(RunModeAuthority.ObjectiveDigestFor(durable),
            RunModeAuthority.ObjectiveDigestFor(original));
    }

    [Fact]
    public void Revoking_run_override_restores_captured_user_default_without_rereading_user_store()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
            JsonObj.Field("text", "reasoning baseline replay"))), TestContext.Current.CancellationToken).Status);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var stream = new EventStream(server.AcquireStore(), server.AcquireCodecs(), session);
        var captured = new ReasoningRequest("low", null);
        stream.AppendBatch(new DomainEventPayload[]
        {
            new RunReasoningPreferenceSelected(run, 1, captured, "UserDefault", "RunCreated", 4),
            new RunReasoningPreferenceSelected(run, 2, new ReasoningRequest("high", null), "User",
                Ids.NewV7().ToString()),
            new RunReasoningPreferenceRevoked(run, 3, Ids.NewV7().ToString()),
        }, DurabilityClass.Barrier);

        var projection = RunProjection.Replay(session, run, server.AcquireCodecs(),
            server.AcquireStore().ReadFrom(session, 1));
        Assert.NotNull(projection.ReasoningSelection);
        Assert.False(projection.ReasoningSelection.HasSelection);
        Assert.True(projection.ReasoningSelection.HasCapturedUserDefault);
        Assert.Equal(captured, projection.ReasoningSelection.CapturedUserDefault);
        Assert.Equal(4, projection.ReasoningSelection.CapturedUserPreferenceRevision);
        var tracker = CanonicalStateTracker.Replay(server.AcquireCodecs(), server.AcquireStore().ReadFrom(session, 1));
        Assert.Contains(tracker.Snapshot(), line => line.StartsWith("run_reasoning:" + run + "=", StringComparison.Ordinal)
            && line.Contains("captured=True:low:capturedRevision=4", StringComparison.Ordinal));
    }

    [Fact]
    public void Preference_store_is_user_scoped_revisioned_and_conflict_checked()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-mode-pref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "user.db");
        try
        {
            using (var store = new RunModePreferenceStore(database))
            {
                Assert.Equal(new RunModePreference(RunMode.Act, 0), store.Read());
                Assert.Equal(new RunModePreference(RunMode.Orchestrate, 1), store.Set(RunMode.Orchestrate, 0));
                Assert.Throws<RunModePreferenceConflictException>(() => store.Set(RunMode.Act, 0));
            }
            using var reopened = new RunModePreferenceStore(database);
            Assert.Equal(new RunModePreference(RunMode.Orchestrate, 1), reopened.Read());
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Only_trusted_user_action_can_change_active_mode_and_replay_keeps_authority()
    {
        var server = OmniHost.CreateInMemoryServer();
        var start = server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
            JsonObj.Field("text", "mode authority test"), JsonObj.Field("mode", "plan"))),
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", start.Status);
        Assert.Equal(RunMode.Act, server.CurrentRunMode());
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var before = server.AcquireStore().CurrentSequence(session);

        var untrusted = server.Send(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
            JsonObj.Field("mode", "act"), JsonObj.Field("effort", "standard"))),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, untrusted.Outcome?.Kind);
        Assert.Equal(before, server.AcquireStore().CurrentSequence(session));

        var command = Command(Payload(JsonObj.Field("cmd", "run.mode.select"), JsonObj.Field("mode", "act"),
            JsonObj.Field("effort", "standard")));
        var selected = server.SendUserAction(command, TestContext.Current.CancellationToken);
        Assert.Equal("ok", selected.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);
        Assert.True(selected.FirstSeq.HasValue);
        Assert.True(selected.LastSeq.HasValue);
        Assert.True(selected.LastSeq.Value >= selected.FirstSeq!.Value);

        var events = server.AcquireStore().ReadFrom(session, 1);
        var projection = RunProjection.Replay(session, run, server.AcquireCodecs(), events);
        Assert.Equal(RunMode.Act, projection.Mode);
        Assert.Equal(ProductEffort.Standard, projection.ModeAuthority?.ProductEffort);
        Assert.True(projection.ModeAuthority?.ModePinned);
        Assert.Contains(events, evt => server.AcquireCodecs().Decode(evt) is RunModeTransitionAuthorized transition
            && transition.CommandId == command.MessageId && transition.Origin == "User");

        var client = new ClientProjection();
        var state = ClientState.Empty();
        foreach (var wire in server.SubscribeSince(0)) state = client.Apply(state, wire);
        Assert.Equal("act", state.StatusLine.Mode);
        Assert.Equal("standard", state.StatusLine.ProductEffort);
    }

    [Fact]
    public void UltraCode_requires_explicit_finite_limits_and_safe_boundary_defers_a_live_step()
    {
        var server = OmniHost.CreateInMemoryServer();
        var start = server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
            JsonObj.Field("text", "adaptive mode test"))), TestContext.Current.CancellationToken);
        Assert.Equal("ok", start.Status);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var authority = server.CurrentModeAuthority();
        Assert.NotNull(authority);

        var missingCaps = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
            JsonObj.Field("mode", "plan"), JsonObj.Field("effort", "ultracode"), JsonObj.FieldBool("adaptive", true))),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, missingCaps.Outcome?.Kind);
        Assert.Equal(authority, server.CurrentModeAuthority());

        var task = Assert.IsType<TaskId>(RunProjection.Replay(session, run, server.AcquireCodecs(),
            server.AcquireStore().ReadFrom(session, 1)).RootTask);
        var lane = Assert.IsType<LaneId>(server.LastLaneId());
        var turn = new TurnId(Guid.NewGuid());
        using (ExecutionScope.Begin(new ExecutionScopeState(run, task, lane, turn)))
        {
            var stream = new EventStream(server.AcquireStore(), server.AcquireCodecs(), session);
            stream.Append(new TurnStarted(turn, lane));
            stream.Append(new ModelStepStarted(turn, 0, "fixture-model", 8192, "direct", null, null, null));
        }

        var deferred = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
            JsonObj.Field("mode", "plan"), JsonObj.Field("effort", "ultracode"), JsonObj.FieldBool("adaptive", true),
            JsonObj.Field("allowedModes", "plan,act,orq"), JsonObj.FieldRaw("maxAgents", "1"),
            JsonObj.FieldRaw("maxDepth", "1"), JsonObj.FieldRaw("maxTurns", "3"),
            JsonObj.FieldRaw("maxToolCalls", "8"), JsonObj.FieldRaw("maxElapsedSeconds", "120"),
            JsonObj.FieldRaw("maxSpendUsd", "2.50"))), TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, deferred.Outcome?.Kind);
        Assert.Equal("ModelStepActive", deferred.Outcome?.Reason);
        Assert.Equal(authority, server.CurrentModeAuthority());
    }

    [Fact]
    public void Mode_downgrade_waits_for_tool_lifecycle_after_model_step_completed()
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
            JsonObj.Field("text", "safe boundary test"))), TestContext.Current.CancellationToken).Status);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var projection = RunProjection.Replay(session, run, server.AcquireCodecs(),
            server.AcquireStore().ReadFrom(session, 1));
        var task = Assert.IsType<TaskId>(projection.RootTask);
        var lane = Assert.IsType<LaneId>(server.LastLaneId());
        var turn = new TurnId(Guid.NewGuid());
        var call = ToolCallId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(run, task, lane, turn)))
        {
            var stream = new EventStream(server.AcquireStore(), server.AcquireCodecs(), session);
            stream.Append(new TurnStarted(turn, lane));
            stream.Append(new ModelStepStarted(turn, 0, "fixture-model", 8192, "direct", null, null, null));
            stream.Append(new ModelStepCompleted(turn, 0, new TokenUsage(1, 1, 0, 0, 0),
                StopReason.ToolUse, null, "2026-10-07", null));
            stream.Append(new ToolCallRequested(call, "provider-call", "filesystem.patch", "{}"));
            stream.Append(new ToolCallPrepared(call, "{}"));
            stream.Append(new ToolCallAuthorized(call));
            stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent, null));
        }

        var before = server.AcquireStore().CurrentSequence(session);
        var deferred = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
            JsonObj.Field("mode", "plan"), JsonObj.Field("effort", "standard"))),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, deferred.Outcome?.Kind);
        Assert.Equal("ToolCallActive", deferred.Outcome?.Reason);
        Assert.Equal(before, server.AcquireStore().CurrentSequence(session));

        using (ExecutionScope.Begin(new ExecutionScopeState(run, task, lane, turn)))
            new EventStream(server.AcquireStore(), server.AcquireCodecs(), session)
                .Append(new ToolCallSucceeded(call, "{}"));
        var accepted = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
            JsonObj.Field("mode", "plan"), JsonObj.Field("effort", "standard"))),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, accepted.Outcome?.Kind);
        Assert.Equal(RunMode.Plan, server.CurrentRunMode());
    }

    [Fact]
    public void UltraCode_authority_replays_finite_caps_and_user_revocation_is_durable()
    {
        var server = OmniHost.CreateInMemoryServer();
        var start = server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
            JsonObj.Field("text", "adaptive authority lifecycle"))), TestContext.Current.CancellationToken);
        Assert.Equal("ok", start.Status);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());

        var selected = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
            JsonObj.Field("mode", "act"), JsonObj.Field("effort", "ultracode"),
            JsonObj.FieldBool("adaptive", true), JsonObj.Field("allowedModes", "plan,act"),
            JsonObj.FieldRaw("maxAgents", "2"), JsonObj.FieldRaw("maxDepth", "3"),
            JsonObj.FieldRaw("maxTurns", "4"), JsonObj.FieldRaw("maxToolCalls", "12"),
            JsonObj.FieldRaw("maxElapsedSeconds", "90"), JsonObj.FieldRaw("maxSpendUsd", "1.25"))),
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", selected.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);

        var selectedEvents = server.AcquireStore().ReadFrom(session, 1);
        var selectedProjection = RunProjection.Replay(session, run, server.AcquireCodecs(), selectedEvents);
        var granted = Assert.IsType<RunModeAuthority>(selectedProjection.ModeAuthority);
        Assert.Equal(RunMode.Act, granted.Mode);
        Assert.Equal(ProductEffort.UltraCode, granted.ProductEffort);
        Assert.False(granted.ModePinned);
        Assert.True(granted.AutoModeSwitch);
        Assert.Equal(new[] { RunMode.Plan, RunMode.Act }, granted.Authorization?.AllowedModes);
        Assert.Equal(new ModeSwitchLimits(2, 3, 4, 12, 90, 1.25m), granted.Authorization?.Limits);

        var revoked = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.revoke"))),
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", revoked.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, revoked.Outcome?.Kind);

        var replayed = RunProjection.Replay(session, run, server.AcquireCodecs(),
            server.AcquireStore().ReadFrom(session, 1));
        var current = Assert.IsType<RunModeAuthority>(replayed.ModeAuthority);
        Assert.Equal(RunMode.Act, current.Mode);
        Assert.Equal(ProductEffort.Standard, current.ProductEffort);
        Assert.True(current.ModePinned);
        Assert.False(current.AutoModeSwitch);
        Assert.Null(current.Authorization);
        Assert.Contains(server.AcquireStore().ReadFrom(session, 1), evt =>
            server.AcquireCodecs().Decode(evt) is RunModeAuthorityRevoked item
            && item.AuthorizationId == granted.Authorization?.AuthorizationId);
    }
}
