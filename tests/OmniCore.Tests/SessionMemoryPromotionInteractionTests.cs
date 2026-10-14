namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Memory;
using OmniCore.Protocol;

public sealed class SessionMemoryPromotionInteractionTests
{
    [Fact]
    public void Memory_commands_and_interaction_respond_share_the_durable_host_decision()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-promotion-interaction-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data"); var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var server = OmniHost.CreateInMemoryServer();
        var paths = OmniHost.CreatePlatformPaths(data);
        server.ConfigurePlatformPaths(() => paths); server.ConfigureWorkspaceRoot(workspace);
        try
        {
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"start memory interaction fixture\"}"),
                CancellationToken.None).Status);
            var session = server.LastSessionId()!;
            var memoryStore = new SessionMemoryStore(OmniHost.WorkspaceDataDirectory(paths, workspace));
            var scope = MemoryScope.ForSession(session);
            memoryStore.Add(new MemoryRecord("trusted-release", scope, MemoryKind.Decision,
                "The release deployment uses the blue production region.", .95, .95, MemorySource.UserStated,
                DateTimeOffset.UtcNow.AddMinutes(-1), null, MemoryExpirationKind.WithScope, ["release"], [], null),
                CancellationToken.None);
            memoryStore.Add(new MemoryRecord("trusted-canary", scope, MemoryKind.Decision,
                "The emergency recovery path uses the blue fallback cluster.", .95, .95, MemorySource.UserStated,
                DateTimeOffset.UtcNow.AddMinutes(-1), null, MemoryExpirationKind.WithScope, ["recovery"], [], null),
                CancellationToken.None);
            IReadOnlyList<DomainEventPayload> Journal() => server.AcquireStore().ReadFrom(session, 1)
                .Select(server.AcquireCodecs().Decode).ToArray();
            void Emit(DomainEventPayload payload) => new EventStream(server.AcquireStore(), server.AcquireCodecs(), session)
                .Append(payload, DurabilityClass.Barrier);
            var policy = new SessionMemoryPolicy(session, memoryStore, Journal, Emit);

            var promoted = policy.Propose("The release deployment uses the green production region.",
                MemoryKind.Decision, MemoryCandidateOrigin.Model);
            Assert.Equal("ask", promoted.Decision);
            var request = Assert.Single(Journal().OfType<InteractionRequested>(), item => item.Kind == InteractionKind.MemoryPromotion);
            var statePath = Path.Combine(root, "last-session.txt");
            File.WriteAllText(statePath, session + "\n" + server.LastRunId());
            var reopened = new OmniServer(server.AcquireStore(), server.AcquireCodecs(), new InMemoryAuditSink(), statePath);
            reopened.ConfigurePlatformPaths(() => paths); reopened.ConfigureWorkspaceRoot(workspace);
            Assert.Single(Journal().OfType<InteractionRequested>(), item => item.Kind == InteractionKind.MemoryPromotion);
            var arbitrarySupersedes = reopened.SendUserAction(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"memory.session.promote\",\"candidateId\":\"" + promoted.Id
                    + "\",\"supersedesId\":\"attacker-selected\",\"expectedSessionId\":\"" + session + "\"}"),
                CancellationToken.None);
            Assert.Equal("error", arbitrarySupersedes.Status);
            Assert.Contains(Journal(), item => item is InteractionRequested pending
                && pending.InteractionId == request.InteractionId);

            var memoryAction = reopened.SendUserAction(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"memory.session.promote\",\"candidateId\":\"" + promoted.Id
                    + "\",\"expectedSessionId\":\"" + session + "\"}"), CancellationToken.None);
            Assert.Equal("ok", memoryAction.Status);
            Assert.Contains(Journal(), item => item is MemoryPromoted itemPromoted
                && itemPromoted.CandidateId == promoted.Id && itemPromoted.Supersedes == "trusted-release");
            Assert.Contains(Journal(), item => item is InteractionResolved resolved
                && resolved.InteractionId == request.InteractionId && resolved.OptionId == "promote");

            var rejected = policy.Propose("The emergency recovery path uses the green fallback cluster.",
                MemoryKind.Decision, MemoryCandidateOrigin.Model);
            Assert.Equal("ask", rejected.Decision);
            var rejectionRequest = Assert.Single(Journal().OfType<InteractionRequested>(), item =>
                item.Kind == InteractionKind.MemoryPromotion
                && item.InteractionId != request.InteractionId);
            var response = reopened.RespondToInteraction(rejectionRequest.InteractionId, "reject");
            Assert.Equal("ok", response.Status);
            Assert.Contains(Journal(), item => item is MemoryRejected itemRejected
                && itemRejected.CandidateId == rejected.Id);
            Assert.Contains(Journal(), item => item is InteractionResolved resolved
                && resolved.InteractionId == rejectionRequest.InteractionId && resolved.OptionId == "reject");
            using var query = JsonDocument.Parse(server.Query("memory.session", CancellationToken.None)!.Json);
            Assert.Equal(3, query.RootElement.GetProperty("records").GetArrayLength());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
