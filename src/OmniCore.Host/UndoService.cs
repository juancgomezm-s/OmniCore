namespace OmniCore.Host;

using System.Security.Cryptography;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>Restores only journal-attributed, hash-verified filesystem preimages. Every attempt
/// appends its own barriered lifecycle; a partial or interrupted attempt is reconciled as Unknown.</summary>
public sealed class UndoService : IDisposable
{
    private const long MaxImageBytes = 2L * 1024 * 1024;
    private const long MaxBatchBytes = 8L * 1024 * 1024;
    private const int MaxFiles = 64;
    private readonly string _root;
    private readonly string _rootIdentity;
    private readonly IEventStore _events;
    private readonly IEventCodecRegistry _codecs;
    private readonly IArtifactStore _artifacts;
    private readonly ScriptedPermissionPolicy _permissions;
    private readonly WorkspaceSnapshotStore _snapshots;
    private readonly IWorkspaceWriteLeaseProvider _leases;
    private readonly IWorkspaceMutationHistoryStore? _mutationHistory;

    public UndoService(string workspaceRoot, string durableRootIdentity, IEventStore events,
        IEventCodecRegistry codecs, IArtifactStore artifacts, ScriptedPermissionPolicy permissions,
        IWorkspaceWriteLeaseProvider? leases = null, IWorkspaceMutationHistoryStore? mutationHistory = null)
    {
        _root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot);
        if (!WorkspaceRootIdentity.Verify(_root, durableRootIdentity)) throw new InvalidOperationException("Workspace root identity is not verified.");
        _rootIdentity = durableRootIdentity;
        _events = events;
        _codecs = codecs;
        _artifacts = artifacts;
        _permissions = permissions;
        _leases = leases ?? HostWorkspaceWriteLeases.Shared;
        _mutationHistory = mutationHistory;
        _snapshots = new WorkspaceSnapshotStore(_root, events, codecs, artifacts, _rootIdentity);
    }

    public UndoPlan Plan(SessionId sessionId, ToolCallId? effectId = null) => _snapshots.BuildPlan(sessionId, effectId);

    internal ToolCallId? PendingEffect(SessionId sessionId) => FindUnfinishedUndo(_events.ReadFrom(sessionId, 1))?.OriginalEffect;

    internal string? LastOutcome(SessionId sessionId, ToolCallId effectId) => _events.ReadFrom(sessionId, 1)
        .Select(_codecs.Decode).OfType<UndoCompleted>().LastOrDefault(item => item.OriginalEffect == effectId)?.Outcome.ToString();

    /// <summary>Read-only review of attributed effects against the current workspace. Never recovers or writes.</summary>
    public UndoPlan Preview(SessionId sessionId, ToolCallId? effectId = null)
    {
        var plan = Plan(sessionId, effectId);
        return plan with
        {
            Entries = plan.Entries.Select(entry =>
        {
            if (entry.State != "Ready") return entry;
            try
            {
                var current = Hash(ReadCurrent(entry.RelativePath));
                return entry with
                {
                    State = current == entry.ExpectedCurrentHash ? "Ready"
                    : current == entry.ResultingHash ? "AlreadyRestored" : "Conflict"
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { return entry with { State = "Conflict" }; }
        }).ToArray()
        };
    }

    public UndoResult Undo(SessionId sessionId, ToolCallId? effectId, CancellationToken cancellationToken,
        bool confirmedByUser = false, CommandId? commandIdOverride = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = _leases.Acquire(_root, cancellationToken);
        using var historyFence = _mutationHistory?.AcquireFence(_root, cancellationToken);
        _mutationHistory?.RecoverPending(_root, cancellationToken);
        if (!_snapshots.VerifySession(sessionId))
            return new(UndoOutcome.Unsupported, effectId, [], "The source session belongs to another or unverifiable workspace.");
        var events = _events.ReadFrom(sessionId, 1);
        var pending = FindUnfinishedUndo(events);
        if (pending is not null)
        {
            if (effectId is not null && effectId != pending.OriginalEffect)
                return new(UndoOutcome.Unsupported, effectId, [],
                    "A different undo is pending recovery; select its recorded effect first.");
            EnsureRootIdentity();
            var classified = pending.Files.Select(file => ClassifyPending(file)).ToArray();
            var recoveryOutcome = classified.Length > 0
                && classified.All(file => file.Outcome == ReconciliationOutcome.Applied)
                ? UndoOutcome.Applied : UndoOutcome.Unknown;
            if (!AppendTerminal(sessionId, pending.CommandId, pending.Attribution, pending.UndoId,
                    pending.OriginalEffect, recoveryOutcome, classified))
                return new(UndoOutcome.Unknown, pending.OriginalEffect, classified,
                    "An interrupted undo was classified, but its recovery record was not durably confirmed.");
            return new(recoveryOutcome, pending.OriginalEffect, classified,
                "A prior undo was interrupted; its targets were classified and no retry was attempted.");
        }

        var plan = _snapshots.BuildPlan(sessionId, effectId);
        var entries = plan.Entries.Where(entry => entry.State == "Ready").ToArray();
        if (entries.Length == 0)
            return new(UndoOutcome.Unsupported, effectId, [], "No safely attributable reversible filesystem effect is available.");
        var grouped = entries.GroupBy(entry => entry.OriginalEffect).ToArray();
        if (effectId is null && grouped.Length > 1)
            return new(UndoOutcome.Unsupported, null, [], "Select one attributed effect per undo command.");
        if (grouped.Length != 1) return new(UndoOutcome.Unsupported, effectId, [], "Undo selection is ambiguous.");
        entries = grouped[0].OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
        if (entries.Length > MaxFiles || entries.Sum(entry => (long)(entry.RestoreBytes?.LongLength ?? 0)) > MaxBatchBytes
            || entries.Any(entry => (entry.RestoreBytes?.LongLength ?? 0) > MaxImageBytes))
            return new(UndoOutcome.Unsupported, entries[0].OriginalEffect, [], "Undo image limits were exceeded.");

        var claims = new ResourceClaims([], entries.Select(entry => _snapshots.ResolvePath(entry.RelativePath)).ToArray(), [], null, []);
        var intent = new ToolIntent(ToolCallId.New(), new ToolId("workspace.undo"), "{}", EffectClass.NonIdempotent,
            claims, ToolRisk.High, null);
        AuthorizedToolIntent authorized;
        try { authorized = confirmedByUser ? _permissions.AuthorizeApproved(intent, null) : _permissions.Authorize(intent); }
        catch (PermissionDeniedException) { return new(UndoOutcome.Unsupported, entries[0].OriginalEffect, [], "WorkspaceWrite authorization is required."); }
        if (authorized.Decision.Final != PermissionDecision.Allow)
            return new(UndoOutcome.Unsupported, entries[0].OriginalEffect, [], "WorkspaceWrite authorization is required.");

        var outcomes = new List<UndoFileState>(entries.Length);
        long total = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = checked(total + (entry.RestoreBytes?.LongLength ?? 0));
            if (total > MaxBatchBytes || (entry.RestoreBytes?.LongLength ?? 0) > MaxImageBytes)
                return new(UndoOutcome.Unsupported, entry.OriginalEffect, [], "Undo image limits were exceeded.");
            try
            {
                var hash = Hash(ReadCurrent(entry.RelativePath));
                if (hash == entry.ResultingHash) outcomes.Add(new(entry.RelativePath, ReconciliationOutcome.NotApplied));
                else if (hash == entry.ExpectedCurrentHash) outcomes.Add(new(entry.RelativePath, ReconciliationOutcome.Applied));
                else outcomes.Add(new(entry.RelativePath, ReconciliationOutcome.Conflict));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.Security.SecurityException or ArgumentException)
            { outcomes.Add(new(entry.RelativePath, ReconciliationOutcome.Conflict)); }
        }
        if (outcomes.All(item => item.Outcome == ReconciliationOutcome.NotApplied))
            return new(UndoOutcome.NoOp, entries[0].OriginalEffect, outcomes,
                "Every target already matches the recorded preimage.");
        if (outcomes.Any(item => item.Outcome == ReconciliationOutcome.Conflict))
            return new(UndoOutcome.Conflict, entries[0].OriginalEffect, outcomes,
                "A target differs from both recorded images; no files were changed.");

        var pendingEntries = entries.Where((_, index) => outcomes[index].Outcome == ReconciliationOutcome.Applied).ToArray();
        using var historyPublication = _mutationHistory is null ? null
            : (_artifacts as IArtifactPublicationLease
                ?? throw new InvalidOperationException("Artifact publication exclusion is required for durable undo history."))
                .AcquirePublicationLease(cancellationToken);
        var undoReceipts = PrepareUndoReceipts(entries, pendingEntries, cancellationToken);

        var undoId = Guid.NewGuid().ToString("N");
        var commandId = commandIdOverride ?? CommandId.New();
        var sourceAttribution = entries[0].Attribution!;
        var undoAttribution = sourceAttribution with { UndoExecutionId = sourceAttribution.UndoExecutionId };
        try
        {
            EnsureRootIdentity();
            using var executionScope = ExecutionScope.Begin(new ExecutionScopeState(sourceAttribution.SourceRunId,
                sourceAttribution.SourceTaskId, sourceAttribution.SourceLaneId, sourceAttribution.SourceTurnId,
                entries[0].OriginalEffect, undoAttribution.UndoExecutionId));
            using var causationScope = CausationScope.Begin(new CommandCausation(commandId));
            new EventStream(_events, _codecs, sessionId).Append(new UndoStarted(commandId, undoId,
                entries[0].OriginalEffect, undoAttribution, entries.Select((entry, index) => new UndoFileIntent(entry.RelativePath,
                    outcomes[index].Outcome == ReconciliationOutcome.NotApplied ? entry.ResultingHash! : entry.ExpectedCurrentHash!,
                    entry.ResultingHash!, entry.RestoreBytes is null
                        ? Path.Combine(Path.GetDirectoryName(entry.RelativePath) ?? "", ".omni-undo-" + undoId + "-" + Array.IndexOf(entries, entry).ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace('\\', '/')
                        : null)).ToArray(), _rootIdentity), DurabilityClass.Barrier);
        }
        catch (Exception)
        { return new(UndoOutcome.Unknown, entries[0].OriginalEffect, [], "The durable undo barrier failed; no file write was started."); }

        var touched = false;
        try
        {
            foreach (var entry in pendingEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = ReadCurrent(entry.RelativePath);
                if (Hash(now) != entry.ExpectedCurrentHash) throw new UndoConflictException();
                touched = true;
                EnsureRootIdentity();
                var intentFile = Array.IndexOf(entries, entry);
                RestoreOne(entry.RelativePath, now, entry.RestoreBytes,
                    entry.RestoreBytes is null ? Path.Combine(Path.GetDirectoryName(entry.RelativePath) ?? "",
                        ".omni-undo-" + undoId + "-" + intentFile.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace('\\', '/') : null);
            }
            var completed = entries.Select(entry => new UndoFileState(entry.RelativePath, ReconciliationOutcome.Applied)).ToArray();
            foreach (var entry in entries)
                if (Hash(ReadCurrent(entry.RelativePath)) != entry.ResultingHash) throw new IOException("Undo postimage verification failed.");
            CompleteUndoReceipts(undoReceipts, entries, cancellationToken);
            if (!AppendTerminal(sessionId, commandId, undoAttribution, undoId, entries[0].OriginalEffect, UndoOutcome.Applied, completed))
                return new(UndoOutcome.Unknown, entries[0].OriginalEffect, completed,
                    "Files match their restored images, but the terminal journal barrier was not confirmed.");
            return new(UndoOutcome.Applied, entries[0].OriginalEffect, completed,
                "Recorded OmniCore filesystem effects were restored.");
        }
        catch (UndoConflictException)
        {
            var classified = entries.Select(entry => Classify(entry)).ToArray();
            var outcome = touched ? UndoOutcome.Unknown : UndoOutcome.Conflict;
            AppendTerminal(sessionId, commandId, undoAttribution, undoId, entries[0].OriginalEffect, outcome, classified);
            return new(outcome, entries[0].OriginalEffect, classified,
                touched ? "Undo stopped after a write began; reconcile before another attempt." : "A target changed before the first write.");
        }
        catch (Exception) when (touched)
        {
            var classified = entries.Select(entry => Classify(entry)).ToArray();
            AppendTerminal(sessionId, commandId, undoAttribution, undoId, entries[0].OriginalEffect, UndoOutcome.Unknown, classified);
            return new(UndoOutcome.Unknown, entries[0].OriginalEffect, classified,
                "Undo was interrupted after a write began; recovery classified the targets and did not retry.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            var classified = entries.Select(entry => Classify(entry)).ToArray();
            AppendTerminal(sessionId, commandId, undoAttribution, undoId, entries[0].OriginalEffect, UndoOutcome.Conflict, classified);
            return new(UndoOutcome.Conflict, entries[0].OriginalEffect, classified,
                "Undo could not safely begin; no file was written.");
        }
    }

    private IReadOnlyList<WorkspaceMutationReceipt> PrepareUndoReceipts(UndoPlanEntry[] entries,
        UndoPlanEntry[] pendingEntries, CancellationToken cancellationToken)
    {
        if (_mutationHistory is null) return [];
        var pendingPaths = pendingEntries.Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);
        var receipts = new List<WorkspaceMutationReceipt>(pendingEntries.Length);
        var toolCall = ToolCallId.New();
        var scope = ExecutionScope.Current;
        var run = scope?.RunId?.ToString() ?? "workspace-undo";
        var turn = scope?.TurnId?.ToString() ?? "workspace-undo";
        foreach (var entry in entries.Where(entry => pendingPaths.Contains(entry.RelativePath)))
        {
            var current = ReadCurrent(entry.RelativePath);
            var before = entry.RestoreBytes;
            var currentRef = current is null ? null : FilesystemPreimage.CaptureHistoryImage(_artifacts, current);
            var restoredRef = before is null ? null : FilesystemPreimage.CaptureHistoryImage(_artifacts, before, postimage: true);
            var oldText = current is null ? string.Empty : FileVersion.Decode(current).Text;
            var newText = before is null ? string.Empty : FileVersion.Decode(before).Text;
            FileVersion.ChangedLines(oldText, newText, out var deleted, out var inserted);
            receipts.Add(new WorkspaceMutationReceipt(_mutationHistory.GetWorkspaceId(_root),
                _mutationHistory.NormalizeRelativePath(_root, entry.RelativePath), toolCall, run, turn, "workspace.undo",
                Hash(current) == FilesystemReconciliationMetadata.AbsentPreHash ? null : Hash(current),
                Hash(before) == FilesystemReconciliationMetadata.AbsentPreHash ? null : Hash(before),
                currentRef, restoredRef, deleted, inserted, WorkspaceMutationReceiptState.Prepared));
        }
        _mutationHistory.Prepare(receipts, cancellationToken);
        return receipts;
    }

    private void CompleteUndoReceipts(IReadOnlyList<WorkspaceMutationReceipt> receipts,
        UndoPlanEntry[] entries, CancellationToken cancellationToken)
    {
        if (_mutationHistory is null || receipts.Count == 0) return;
        var states = new List<WorkspaceFileHistoryEntry>(receipts.Count);
        var finalized = new List<WorkspaceMutationReceipt>(receipts.Count);
        foreach (var receipt in receipts)
        {
            var entry = entries.Single(item => string.Equals(item.RelativePath, receipt.RelativePath, StringComparison.Ordinal));
            var previous = _mutationHistory.ReadFile(_root, entry.RelativePath);
            var restored = entry.RestoreBytes;
            states.Add(restored is null
                ? new WorkspaceFileHistoryEntry(receipt.WorkspaceId, receipt.RelativePath,
                    WorkspaceFileHistoryState.Tombstone, receipt.ExpectedPreHash,
                    receipt.PreimageRef ?? previous?.ImageRef, checked((previous?.Revision ?? 0) + 1), receipt.ToolCallId)
                : new WorkspaceFileHistoryEntry(receipt.WorkspaceId, receipt.RelativePath,
                    WorkspaceFileHistoryState.Present, receipt.ExpectedPostHash, receipt.PostimageRef,
                    checked((previous?.Revision ?? 0) + 1), receipt.ToolCallId));
            finalized.Add(receipt with { State = WorkspaceMutationReceiptState.Applied });
        }
        _mutationHistory.Complete(finalized, states, cancellationToken);
    }

    public void Dispose()
    {
        if (_mutationHistory is IDisposable disposable) disposable.Dispose();
    }

    private byte[]? ReadCurrent(string relative)
    {
        var path = _snapshots.ResolvePath(relative);
        VerifyAncestors(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((attributes & FileAttributes.Directory) != 0)
            throw new InvalidOperationException("A directory occupies an undo target.");
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
            throw new InvalidOperationException("Undo target topology is unsupported.");
        if (new FileInfo(path).Length > MaxImageBytes) throw new InvalidOperationException("Undo target exceeds its size bound.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxImageBytes) throw new InvalidOperationException("Undo target exceeds its size bound.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = input.Read(chunk, 0, chunk.Length);
            if (read == 0) break;
            if (buffer.Length + read > MaxImageBytes) throw new InvalidOperationException("Undo target exceeds its size bound.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        _ = FileVersion.Decode(bytes); // Reject invalid/non-round-trippable content.
        return bytes;
    }

    private void RestoreOne(string relative, byte[]? current, byte[]? before, string? quarantineRelative)
    {
        var path = _snapshots.ResolvePath(relative);
        VerifyAncestors(path);
        if (before is null)
        {
            if (!File.Exists(path) || Directory.Exists(path)) throw new UndoConflictException();
            var quarantine = _snapshots.ResolvePath(quarantineRelative ?? throw new InvalidOperationException());
            File.Move(path, quarantine, overwrite: false);
            var moved = ReadCurrent(quarantineRelative!);
            if (Hash(moved) != Hash(current))
            {
                try { File.Move(quarantine, path, overwrite: false); } catch (IOException) { }
                throw new UndoConflictException();
            }
            File.Delete(quarantine);
            return;
        }
        if (File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length > MaxImageBytes) throw new UndoConflictException();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            if (Hash(memory.ToArray()) != Hash(current)) throw new UndoConflictException();
            stream.Position = 0; stream.SetLength(0); stream.Write(before); stream.Flush(flushToDisk: true);
        }
        else
        {
            // Never replace an external file that appears after validation.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(before); stream.Flush(flushToDisk: true);
        }
    }

    private void VerifyAncestors(string target)
    {
        for (var path = Path.GetDirectoryName(target); path is not null; path = Path.GetDirectoryName(path))
        {
            if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Undo path ancestor is missing or linked.");
            if (PathEquals(path, _root)) return;
        }
        throw new InvalidOperationException("Undo target is outside the verified root.");
    }

    private UndoFileState Classify(UndoPlanEntry entry)
    {
        try
        {
            var hash = Hash(ReadCurrent(entry.RelativePath));
            return new(entry.RelativePath, hash == entry.ResultingHash ? ReconciliationOutcome.Applied
                : hash == entry.ExpectedCurrentHash ? ReconciliationOutcome.NotApplied : ReconciliationOutcome.Conflict);
        }
        catch (Exception) { return new(entry.RelativePath, ReconciliationOutcome.Conflict); }
    }

    private UndoFileState ClassifyPending(UndoFileIntent file)
    {
        try
        {
            EnsureRootIdentity();
            var hash = Hash(ReadCurrent(file.RelativePath));
            if (hash == file.ExpectedRestoredHash)
            {
                if (file.QuarantinePath is not null)
                {
                    var owned = _snapshots.ResolvePath(file.QuarantinePath);
                    FileAttributes attributes;
                    var quarantineExists = true;
                    try { attributes = File.GetAttributes(owned); }
                    catch (FileNotFoundException) { attributes = default; quarantineExists = false; }
                    catch (DirectoryNotFoundException) { attributes = default; quarantineExists = false; }
                    if (quarantineExists)
                    {
                        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                            || Hash(ReadCurrent(file.QuarantinePath)) != file.ExpectedBeforeUndoHash)
                            return new(file.RelativePath, ReconciliationOutcome.Conflict);
                        // Recovery is classification only. Preserve the journal-owned quarantine
                        // bytes until a future explicit cleanup lifecycle is authorized.
                    }
                }
                return new(file.RelativePath, ReconciliationOutcome.Applied);
            }
            if (hash == file.ExpectedBeforeUndoHash) return new(file.RelativePath, ReconciliationOutcome.NotApplied);
            if (file.QuarantinePath is not null && hash == FilesystemReconciliationMetadata.AbsentPreHash)
            {
                var owned = _snapshots.ResolvePath(file.QuarantinePath);
                if (File.Exists(owned) && Hash(ReadCurrent(file.QuarantinePath)) == file.ExpectedBeforeUndoHash)
                {
                    // The leaf is absent, but the original postimage is still preserved in the
                    // durable quarantine. Do not silently discard it during recovery.
                    return new(file.RelativePath, ReconciliationOutcome.Conflict);
                }
            }
            return new(file.RelativePath, ReconciliationOutcome.Conflict);
        }
        catch (Exception) { return new(file.RelativePath, ReconciliationOutcome.Conflict); }
    }

    private sealed record PendingUndo(CommandId CommandId, UndoAttribution Attribution, string UndoId,
        ToolCallId OriginalEffect, UndoFileIntent[] Files);

    private PendingUndo? FindUnfinishedUndo(IReadOnlyList<DomainEvent> events)
    {
        var started = new Dictionary<string, (UndoStarted Payload, DomainEvent Envelope)>(StringComparer.Ordinal);
        var completed = new Dictionary<string, (UndoCompleted Payload, DomainEvent Envelope)>(StringComparer.Ordinal);
        foreach (var envelope in events)
        {
            switch (_codecs.Decode(envelope))
            {
                case UndoStarted value: started[value.UndoId] = (value, envelope); break;
                case UndoCompleted value: completed[value.UndoId] = (value, envelope); break;
            }
        }
        foreach (var terminal in completed)
        {
            if (!started.TryGetValue(terminal.Key, out var begin)) throw new InvalidOperationException("Undo terminal has no matching start.");
            if (terminal.Value.Payload.CommandId != begin.Payload.CommandId
                || terminal.Value.Payload.OriginalEffect != begin.Payload.OriginalEffect
                || terminal.Value.Payload.Attribution != begin.Payload.Attribution
                || terminal.Value.Payload.WorkspaceIdentity != begin.Payload.WorkspaceIdentity
                || !terminal.Value.Payload.Files.Select(file => file.RelativePath).SequenceEqual(
                    begin.Payload.Files.Select(file => file.RelativePath), StringComparer.Ordinal)
                || terminal.Value.Envelope.Causation is not CommandCausation cause || cause.CommandId != begin.Payload.CommandId
                || terminal.Value.Envelope.RunId != begin.Envelope.RunId || terminal.Value.Envelope.TaskId != begin.Envelope.TaskId
                || terminal.Value.Envelope.LaneId != begin.Envelope.LaneId || terminal.Value.Envelope.TurnId != begin.Envelope.TurnId
                || terminal.Value.Envelope.ExecutionId != begin.Payload.Attribution.UndoExecutionId)
                throw new InvalidOperationException("Undo lifecycle attribution is inconsistent.");
        }
        var item = started.Where(pair => !completed.ContainsKey(pair.Key)).OrderBy(pair => pair.Value.Envelope.Sequence).FirstOrDefault();
        if (item.Value.Payload is null) return null;
        if (!string.Equals(item.Value.Payload.WorkspaceIdentity, _rootIdentity, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Pending undo belongs to another workspace root.");
        if (item.Value.Payload.Files is null || item.Value.Payload.Files.Count is < 1 or > MaxFiles
            || item.Value.Payload.Files.Any(file => !FilesystemReconciliationMetadata.IsSaneRelativePath(file.RelativePath)
                || file.QuarantinePath is not null && !FilesystemReconciliationMetadata.IsSaneRelativePath(file.QuarantinePath)))
            throw new InvalidOperationException("Pending undo has invalid targets.");
        var start = item.Value.Payload;
        var pendingEnvelope = item.Value.Envelope;
        var attribution = start.Attribution;
        var sourceEnvelope = attribution is null ? null : events.SingleOrDefault(evt => evt.EventId == attribution.SourceEventId);
        var sourcePayload = sourceEnvelope is null ? null : _codecs.Decode(sourceEnvelope);
        if (attribution is null || attribution.SourceEventId == default
            || attribution.UndoExecutionId == default
            || sourcePayload is not ToolCallStarted sourceEffect || sourceEffect.ToolCallId != start.OriginalEffect
            || sourceEnvelope!.RunId != attribution.SourceRunId || sourceEnvelope.TaskId != attribution.SourceTaskId
            || sourceEnvelope.LaneId != attribution.SourceLaneId || sourceEnvelope.TurnId != attribution.SourceTurnId
            || sourceEnvelope.ExecutionId != attribution.SourceExecutionId
            || pendingEnvelope.Causation is not CommandCausation startCause || startCause.CommandId != start.CommandId
            || pendingEnvelope.RunId != attribution.SourceRunId || pendingEnvelope.TaskId != attribution.SourceTaskId
            || pendingEnvelope.LaneId != attribution.SourceLaneId || pendingEnvelope.TurnId != attribution.SourceTurnId
            || pendingEnvelope.ExecutionId != attribution.UndoExecutionId)
            throw new InvalidOperationException("Pending undo attribution is inconsistent.");
        return new(start.CommandId, start.Attribution, item.Key, start.OriginalEffect, start.Files.ToArray());
    }

    private bool AppendTerminal(SessionId sessionId, CommandId commandId, UndoAttribution attribution,
        string undoId, ToolCallId effect, UndoOutcome outcome,
        IReadOnlyList<UndoFileState> files)
    {
        try
        {
            EnsureRootIdentity();
            using var executionScope = ExecutionScope.Begin(new ExecutionScopeState(attribution.SourceRunId,
                attribution.SourceTaskId, attribution.SourceLaneId, attribution.SourceTurnId,
                effect, attribution.UndoExecutionId));
            using var causationScope = CausationScope.Begin(new CommandCausation(commandId));
            new EventStream(_events, _codecs, sessionId).Append(new UndoCompleted(commandId, undoId, effect,
                attribution, outcome, files, _rootIdentity), DurabilityClass.Barrier);
            return true;
        }
        catch (Exception) { return false; /* failed terminal barriers remain discoverable as pending Unknown */ }
    }

    private void EnsureRootIdentity()
    {
        if (!WorkspaceRootIdentity.Verify(_root, _rootIdentity))
            throw new InvalidOperationException("Verified workspace root identity changed.");
    }

    private static string? Hash(byte[]? bytes) => bytes is null ? FilesystemReconciliationMetadata.AbsentPreHash : FileVersion.VersionToken(bytes);
    private static bool PathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private sealed class UndoConflictException : Exception;
}

public sealed record UndoResult(UndoOutcome Outcome, ToolCallId? OriginalEffect,
    IReadOnlyList<UndoFileState> Files, string Summary);
