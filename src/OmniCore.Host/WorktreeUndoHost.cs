namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>Authoritative CLI composition for F4. It derives journal/CAS locations from the
/// verified workspace root and selects only a session whose durable root identity matches it.</summary>
public static class WorktreeUndoHost
{
    /// <summary>CLI-safe scalar projection; keeps Domain's typed undo contracts behind Host.</summary>
    public static WorktreeUndoPlanView? PlanForCli(string workspacePath, string? effectId = null)
    {
        var effect = effectId is null ? null : ToolCallId.Parse(effectId);
        using var composition = Open(workspacePath, effect);
        var plan = composition?.Service.Plan(composition.Session, effect);
        return plan is null ? null : new WorktreeUndoPlanView(plan.SessionId.ToString(),
            plan.Entries.Select(entry => new WorktreeUndoPlanEntryView(entry.OriginalEffect.ToString(),
                entry.State, entry.RelativePath)).ToArray());
    }

    /// <summary>CLI-safe scalar projection; no Domain enum leaks into the CLI assembly.</summary>
    public static WorktreeUndoResultView UndoForCli(string workspacePath, string effectId,
        CancellationToken cancellationToken)
    {
        var result = Undo(workspacePath, ToolCallId.Parse(effectId), cancellationToken);
        return new WorktreeUndoResultView(result.Outcome.ToString(), result.Files.Select(file =>
            new WorktreeUndoFileStateView(file.Outcome.ToString(), file.RelativePath)).ToArray());
    }

    public static UndoPlan? Plan(string workspacePath, ToolCallId? effectId = null)
    {
        using var composition = Open(workspacePath, effectId);
        return composition?.Service.Plan(composition.Session, effectId);
    }

    public static UndoResult Undo(string workspacePath, ToolCallId effectId,
        CancellationToken cancellationToken)
    {
        using var composition = Open(workspacePath, effectId);
        if (composition is null)
            return new(UndoOutcome.Unsupported, effectId, [], "No verified OmniCore session exists for this workspace.");
        return composition.Service.Undo(composition.Session, effectId, cancellationToken);
    }

    private static Composition? Open(string workspacePath, ToolCallId? effectId)
    {
        var root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspacePath);
        var canonical = ProjectIdentity.CanonicalWorkspacePath(root);
        var workspaceId = WorkspaceId.Of(canonical);
        var paths = OmniHost.CreatePlatformPaths();
        var workspaceData = OmniHost.WorkspaceDataDirectory(paths, root);
        var journalPath = Path.Combine(workspaceData, "journal.db");
        if (!File.Exists(journalPath)) return null;
        var store = new SqliteEventStore(journalPath);
        try
        {
            var codecs = EventCodecs.Create();
            var sessions = ((IWorkspaceJournalReader)store).ReadEvents(EventType.Of("session.created"))
                .OrderByDescending(item => item.Timestamp).Select(item => (Envelope: item,
                    Payload: codecs.Decode(item) as SessionCreated)).Where(item => item.Payload is not null
                    && item.Payload.WorkspaceId == workspaceId.ToString())
                .Where(item => effectId is null || store.ReadFrom(item.Payload!.SessionId, 1).Any(evt =>
                    codecs.Decode(evt) is ToolCallStarted start && start.ToolCallId == effectId)).ToArray();
            if (effectId is not null && sessions.Length != 1)
            {
                store.Close();
                return null;
            }
            foreach (var candidate in sessions)
            {
                var events = store.ReadFrom(candidate.Payload!.SessionId, 1);
                var established = events.Select(codecs.Decode).OfType<WorkspaceRootEstablished>()
                    .SingleOrDefault(item => item.SessionId == candidate.Payload.SessionId);
                if (established is null || !PathEquals(established.CanonicalRoot, canonical)
                    || !WorkspaceRootIdentity.Verify(root, established.DurableIdentity)) continue;

                var trusted = new WorkspaceTrustStore(paths).IsTrusted(root);
                var project = WorkspaceConfigurationLoader.Load(root, trusted);
                var policy = OmniHost.CreateProjectRestrictionPolicy(RunMode.Act,
                    trusted ? project.Settings?.PermissionRestrictions : null, UserPermissionsLoader.Load(paths));
                policy.WithGrantStore(OmniHost.CreatePermissionGrantStore(root), workspaceId, null);
                var artifacts = OmniHost.CreateArtifactStore(workspaceData);
                var service = new UndoService(root, established.DurableIdentity, store, codecs, artifacts, policy);
                return new Composition(store, candidate.Payload.SessionId, service);
            }
            store.Close();
            return null;
        }
        catch
        {
            store.Close();
            throw;
        }
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class Composition(SqliteEventStore store, SessionId session, UndoService service) : IDisposable
    {
        public SessionId Session { get; } = session;
        public UndoService Service { get; } = service;
        public void Dispose() => store.Close();
    }
}

public sealed record WorktreeUndoPlanView(string SessionId, IReadOnlyList<WorktreeUndoPlanEntryView> Entries);
public sealed record WorktreeUndoPlanEntryView(string OriginalEffect, string State, string RelativePath);
public sealed record WorktreeUndoResultView(string Outcome, IReadOnlyList<WorktreeUndoFileStateView> Files);
public sealed record WorktreeUndoFileStateView(string Outcome, string RelativePath);
