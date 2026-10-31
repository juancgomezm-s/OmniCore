namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;

public sealed partial class OmniCliRuntime
{
    /// <summary>Human-only administrative action. No provider, model tool, imports or scripts are invoked.</summary>
    public ProjectInitializationPlan InitializeProject(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _turnExecutionActive, 1, 0) != 0)
            throw new InvalidOperationException("project.initialize.busy");
        try
        {
            if (!_activeDelegations.IsEmpty) throw new InvalidOperationException("project.initialize.busy");
            var paths = OmniHost.CreatePlatformPaths();
            var trust = new WorkspaceTrustStore(paths);
            if (!trust.IsTrusted(_workspaceRoot)) throw new InvalidOperationException("project.initialize.trust_required");
            var server = Server();
            if (!server.EnsureWriterOwnershipForCommand()) throw new InvalidOperationException("project.initialize.busy");
            using var workspaceLease = HostWorkspaceWriteLeases.Shared.Acquire(_workspaceRoot, cancellationToken);
            EnsureNoActiveInitializationTurns(server);
            var initializer = new ProjectInitializer(root => new WorkspaceTrustStore(OmniHost.CreatePlatformPaths()).IsTrusted(root));
            var plan = initializer.Preview(_workspaceRoot, cancellationToken);
            EnsureNoActiveInitializationTurns(server);
            initializer.Apply(plan, cancellationToken);
            return plan;
        }
        finally { Volatile.Write(ref _turnExecutionActive, 0); }
    }

    private void EnsureNoActiveInitializationTurns(OmniServer server)
    {
        var workspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(ProjectIdentity.ResolvePhysicalWorkspaceRoot(_workspaceRoot))).ToString();
        var sessions = server.AcquireStore() is IWorkspaceJournalReader reader
            ? reader.ReadEvents(EventType.Of("session.created")).Select(server.AcquireCodecs().Decode).OfType<SessionCreated>()
                .Where(created => created.WorkspaceId == workspace).Select(created => created.SessionId).ToArray()
            : server.LastSessionId() is { } selected ? [selected] : Array.Empty<SessionId>();
        foreach (var session in sessions)
        {
            var payloads = server.AcquireStore().ReadFrom(session, 1).Select(server.AcquireCodecs().Decode).ToArray();
            var completed = payloads.Select(payload => payload switch
            {
                TurnCompleted turn => turn.TurnId,
                TurnAbandoned turn => turn.TurnId,
                _ => null,
            }).Where(turn => turn is not null).ToHashSet();
            if (payloads.OfType<TurnStarted>().Any(turn => !completed.Contains(turn.TurnId)))
                throw new InvalidOperationException("project.initialize.busy");
        }
    }
}
