using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ReasoningPreferenceHostTests
{
    private static WireEnvelope Command(string json) => WireEnvelope.Command(Ids.NewV7(), json);

    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";

    [Fact]
    public void Trusted_user_default_is_captured_once_and_run_override_revoke_restores_that_snapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-reasoning-host-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(workspace);
        try
        {
            var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(),
                userSpendReader: new UserWorkspaceSpendReader(data, workspace));

            var setPayload = Payload(JsonObj.Field("cmd", "reasoning.default.set"), JsonObj.Field("kind", "high"),
                JsonObj.Field("expectedRevision", "0"));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected,
                server.Send(Command(setPayload), TestContext.Current.CancellationToken).Outcome?.Kind);
            using (var emptyPreference = new ReasoningPreferenceStore(Path.Combine(data, "user.db")))
                Assert.Equal(new UserReasoningPreference(false, null, 0), emptyPreference.Read());

            Assert.Equal("ok", server.SendUserAction(Command(setPayload), TestContext.Current.CancellationToken).Status);
            Assert.Equal("ok", server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
                JsonObj.Field("text", "captured reasoning default"))), TestContext.Current.CancellationToken).Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var created = RunProjection.Replay(session, run, EventCodecs.Create(),
                server.AcquireStore().ReadFrom(session, 1)).ReasoningSelection;
            Assert.NotNull(created);
            Assert.True(created.HasSelection);
            Assert.Equal("high", created.Request?.Kind);
            Assert.Equal("UserDefault", created.Source);
            Assert.Equal(1, created.CapturedUserPreferenceRevision);

            using (var userStore = new ReasoningPreferenceStore(Path.Combine(data, "user.db")))
                Assert.Equal(new UserReasoningPreference(true, new ReasoningRequest("low", null), 2),
                    userStore.Set(new ReasoningRequest("low", null), 1));

            var before = server.AcquireStore().CurrentSequence(session);
            var runOverride = Payload(JsonObj.Field("cmd", "run.reasoning.select"), JsonObj.Field("kind", "budget"),
                JsonObj.Field("budgetTokens", "4096"), JsonObj.Field("expectedRevision", "1"));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected,
                server.Send(Command(runOverride), TestContext.Current.CancellationToken).Outcome?.Kind);
            Assert.Equal(before, server.AcquireStore().CurrentSequence(session));
            Assert.Equal("ok", server.SendUserAction(Command(runOverride), TestContext.Current.CancellationToken).Status);

            var revoke = Payload(JsonObj.Field("cmd", "run.reasoning.revoke"),
                JsonObj.Field("expectedRevision", "2"));
            Assert.Equal("ok", server.SendUserAction(Command(revoke), TestContext.Current.CancellationToken).Status);
            var restored = server.CurrentRunReasoningSelection();
            Assert.NotNull(restored);
            Assert.False(restored.HasSelection);
            Assert.True(restored.HasCapturedUserDefault);
            Assert.Equal(new ReasoningRequest("high", null), restored.CapturedUserDefault);
            Assert.Equal(1, restored.CapturedUserPreferenceRevision);
            using (var updatedPreference = new ReasoningPreferenceStore(Path.Combine(data, "user.db")))
                Assert.Equal(new ReasoningRequest("low", null), updatedPreference.Read().Request);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
