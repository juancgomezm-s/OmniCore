namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

public sealed partial class OmniServer
{
    private UndoService CurrentUndoService(OmniCore.Abstractions.IWorkspaceMutationHistoryStore? history = null)
    {
        if (_workspaceRoot is not { } root || _lastSessionId is not { } session || _artifacts is null)
            throw new InvalidOperationException("undo.unavailable");
        var established = _store.ReadFrom(session, 1).Select(_codecs.Decode).OfType<WorkspaceRootEstablished>().SingleOrDefault()
            ?? throw new InvalidOperationException("undo.unavailable");
        var paths = _platformPathsFactory();
        var trusted = new WorkspaceTrustStore(paths).IsTrusted(root);
        var restrictions = WorkspaceConfigurationLoader.Load(root, trusted).Settings?.PermissionRestrictions;
        // This is an explicit human compensation, not model dispatch. It reuses the CLI undo
        // permission layers; confirmation lifts Ask once and never lifts a current Deny.
        var policy = OmniHost.CreateProjectRestrictionPolicy(RunMode.Act, trusted ? restrictions : null,
            UserPermissionsLoader.Load(paths));
        policy.WithGrantStore(new OmniCore.Security.FilePermissionGrantStore(OmniHost.WorkspaceDataDirectory(paths, root), _audit),
            WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root)), _lastRunId);
        return new UndoService(root, established.DurableIdentity, _store, _codecs, _artifacts, policy,
            mutationHistory: history ?? _mutationHistory);
    }

    private UndoReview ReadUndoReview(string? effect, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_lastSessionId is not { } session) return new(null, "", [], Error: "undo.unavailable");
        var service = CurrentUndoService();
        var pending = service.PendingEffect(session);
        var plan = service.Preview(session, effect is null ? null : ToolCallId.Parse(effect));
        var previewBudget = 48 * 1024;
        FileDiffSnapshot? PreviewDiff(UndoPlanEntry entry)
        {
            if (effect is null || entry.State is not ("Ready" or "AlreadyRestored")) return null;
            try
            {
                var before = entry.PostimageBytes is null ? "" : OmniCliRuntime.RedactSensitive(OmniCore.Tools.FileVersion.Decode(entry.PostimageBytes).Text);
                var after = entry.RestoreBytes is null ? "" : OmniCliRuntime.RedactSensitive(OmniCore.Tools.FileVersion.Decode(entry.RestoreBytes).Text);
                if (before.Length > 4096 || after.Length > 4096 || before.Length + after.Length > previewBudget) return null;
                previewBudget -= before.Length + after.Length;
                return new(session.ToString(), entry.OriginalEffect.ToString(), entry.RelativePath, true, null, before, after);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or DecoderFallbackException)
            { return null; }
        }
        var files = plan.Entries.OrderByDescending(item => item.Sequence).ThenBy(item => item.RelativePath, StringComparer.Ordinal)
            .Take(256).Select(item => new UndoReviewFile(item.OriginalEffect.ToString(), item.RelativePath, item.State,
                item.RestoreBytes is null ? "remove" : item.PostimageBytes is null ? "recreate" : "restore",
                item.RestoreBytes?.LongLength ?? 0, item.ExpectedCurrentHash, item.ResultingHash) { Diff = PreviewDiff(item) }).ToArray();
        var sequence = _store.ReadFrom(session, 1).LastOrDefault()?.Sequence ?? 0;
        var identity = session + "\n" + _lastRunId + "\n" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"
            + System.Text.Json.JsonSerializer.Serialize(files, UndoReviewFilesJson.Default.UndoReviewFileArray);
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new(session.ToString(), revision, files, Outcome: effect is null ? null : service.LastOutcome(session, ToolCallId.Parse(effect)))
        { RunId = _lastRunId?.ToString(), PendingEffectId = pending?.ToString(), RecoveryRequired = pending is not null && effect == pending.ToString() };
    }

    private SessionQueryResult QueryUndo(string name, CancellationToken token)
    {
        lock (_modeAuthorityMutationGate)
        {
            try
            {
                var effect = name == UndoProtocol.Query ? null : name[(UndoProtocol.Query.Length + 1)..];
                if (effect is not null && !Guid.TryParseExact(effect, "D", out _))
                    return new(name, UndoProtocol.Encode(new(null, "", [], Error: "undo.unavailable")));
                return new(name, UndoProtocol.Encode(ReadUndoReview(effect, token)));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException
                or UnauthorizedAccessException or SqliteException)
            { return new(name, UndoProtocol.Encode(new(null, "", [], Error: "undo.unavailable"))); }
        }
    }

    private CommandAck ApplyUndo(WireEnvelope command, IReadOnlyDictionary<string, string> fields,
        bool trustedUserAction, CancellationToken token)
    {
        CommandAck Reject(string error) => new(command.MessageId, "error", error, RuntimeCommandOutcome.Rejected());
        if (!trustedUserAction || fields.GetValueOrDefault("confirmed") != "true") return Reject("undo.confirm_required");
        if (_lastSessionId is not { } session || fields.GetValueOrDefault("sessionId") != session.ToString())
            return Reject("undo.stale");
        if (!CanMutateSessionPreference(session)) return Reject("undo.busy");
        if (!Guid.TryParseExact(fields.GetValueOrDefault("effectId"), "D", out _)) return Reject("undo.unavailable");
        try
        {
            var effect = fields["effectId"];
            var current = ReadUndoReview(effect, token);
            if (current.Revision != fields.GetValueOrDefault("revision")
                || !current.RecoveryRequired && (current.Files.Count == 0
                    || current.Files.Any(file => file.State is not ("Ready" or "AlreadyRestored")))) return Reject("undo.stale");
            using var ownedHistory = _mutationHistory is null ? new SqliteWorkspaceMutationHistoryStore(
                OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), _workspaceRoot!)) : null;
            var service = CurrentUndoService(ownedHistory);
            var result = service.Undo(session, ToolCallId.Parse(effect), token,
                confirmedByUser: true, commandIdOverride: new CommandId(Guid.Parse(command.MessageId)));
            _workspaceGitCache = null;
            return result.Outcome is UndoOutcome.Applied or UndoOutcome.NoOp
                || current.RecoveryRequired && result.Outcome == UndoOutcome.Unknown && service.PendingEffect(session) is null
                ? new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted())
                : Reject(result.Outcome == UndoOutcome.Conflict ? "undo.stale"
                    : result.Outcome == UndoOutcome.Unknown ? "undo.unknown" : "undo.denied");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException
            or UnauthorizedAccessException or SqliteException)
        { return Reject("undo.unknown"); }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(UndoReviewFile[]))]
internal partial class UndoReviewFilesJson : System.Text.Json.Serialization.JsonSerializerContext;
