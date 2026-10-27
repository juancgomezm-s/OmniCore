namespace OmniCore.Host;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Sandbox;
using OmniCore.Extensions;
using Task = System.Threading.Tasks.Task;

internal sealed record ProcessOutputChunk(long Sequence, string Stream, string Text);
internal sealed record ProcessSessionSummary(string Handle, string State, string RunId, string LaneId,
    long LastOutputSequence, int? ExitCode, string? ProposalId, string SourceToolId, bool OutputLost,
    bool CanRead, bool CanInput, bool CanStop);
public sealed class PersistentProcessSessionQuota
{
    private readonly object gate = new();
    private int handles, active;
    internal Lease? TryAcquire()
    {
        lock (gate)
        {
            if (handles >= 32 || active >= 8) return null;
            handles++; active++;
            return new Lease(this, active: true);
        }
    }
    internal Lease? TryRecoverHandle()
    {
        lock (gate)
        {
            if (handles >= 32) return null;
            handles++;
            return new Lease(this, active: false);
        }
    }
    private void Release(bool releaseHandle, bool releaseActive)
    {
        lock (gate)
        {
            if (releaseHandle) handles--;
            if (releaseActive) active--;
        }
    }
    internal sealed class Lease(PersistentProcessSessionQuota owner, bool active) : IDisposable
    {
        private int released;
        private int activeOwned = active ? 1 : 0;
        internal void ReleaseActive()
        {
            if (Interlocked.Exchange(ref activeOwned, 0) == 1) owner.Release(false, true);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            var releaseActive = Interlocked.Exchange(ref activeOwned, 0) == 1;
            owner.Release(true, releaseActive);
        }
    }
}
internal sealed class ProcessInputOutcomeUnknownException(string reason, Exception innerException)
    : IOException(reason, innerException);

/// <summary>Host-owned persistent processes. Handles are random capabilities scoped to one Session/Run/Lane.</summary>
public sealed class PersistentProcessSessionManager(SessionId sessionId, IEventStore store, IEventCodecRegistry codecs,
    string hostLeaseGeneration, string workspaceRoot, IArtifactStore? artifacts, Func<bool> ownsHostLease,
    PersistentProcessSessionQuota? sessionQuota = null, Action? trimSessionProcessHandles = null,
    bool enforceChildControlProcessScope = false)
{
    private const int RetainedBytes = 256 * 1_024;
    private const int MaximumTotalBytes = 32 * 1_048_576;
    private const int MaximumInputBytes = 16_384;
    private const int MaximumHandles = 32;
    private const int MaximumActiveProcesses = 8;
    private const int MaximumPendingSanitizerChars = 8_192;
    private readonly ConcurrentDictionary<string, ProcessRecord> _processes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PersistentProcessSessionQuota.Lease> _recoveredCapacity = new(StringComparer.Ordinal);
    private readonly EventStream _events = new(store, codecs, sessionId);
    private readonly object _capacityGate = new();
    private readonly Action? _trimSessionProcessHandles = trimSessionProcessHandles;
    private bool _closing;

    private sealed class ProcessRecord(string id, string run, string lane, string lease,
        ISandboxPersistentProcessControl? control, DateTimeOffset started, TimeSpan timeout,
        ExtensionWriteWorkspace? staging, string workspaceRoot, ToolCallId sourceCall, ToolId sourceTool,
        string turn, ResourceClaims? writerClaims, ProcessClaim? sourceProcessClaim)
    {
        internal readonly string Id = id, Run = run, Lane = lane;
        internal string Lease = lease;
        internal ISandboxPersistentProcessControl? Control = control;
        internal ISandboxProcessControl? UncertainControl;
        internal readonly DateTimeOffset Started = started;
        internal readonly TimeSpan Timeout = timeout;
        internal readonly ExtensionWriteWorkspace? Staging = staging;
        internal readonly string WorkspaceRoot = workspaceRoot;
        internal readonly ToolCallId SourceCall = sourceCall;
        internal readonly ToolId SourceTool = sourceTool;
        internal readonly string Turn = turn;
        internal readonly ResourceClaims? WriterClaims = writerClaims;
        internal readonly ProcessClaim? SourceProcessClaim = sourceProcessClaim;
        internal IReadOnlyList<ExtensionWorkspaceChange>? Changes;
        internal ExtensionWriteBatchProposal? Proposal;
        internal ArtifactRef? ProposalArtifact;
        internal readonly object Gate = new();
        internal readonly SemaphoreSlim InputGate = new(1, 1);
        internal readonly SemaphoreSlim OutputChanged = new(0, 1);
        internal readonly Queue<(long Sequence, string Stream, string Text, int Bytes)> Chunks = new();
        internal long LastSequence;
        internal int Retained, Total;
        internal int? ExitCode;
        internal bool OutputLostOnRecovery;
        internal string State = control is null ? "starting" : "running";
        internal string? Diagnostic;
        internal bool TimedOut;
        internal Task Pumps = Task.CompletedTask;
        internal Task Monitor = Task.CompletedTask;
        internal readonly TaskCompletionSource StartReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PersistentProcessSessionQuota.Lease? CapacityLease;
    }

    internal async Task<(string? Handle, string State, string? Error)> StartAsync(
        string run, string lane, SandboxLaunchSpec spec, ISandboxProcessLauncher launcher,
        Action<DomainEventPayload>? emit, CancellationToken token, ExtensionWriteWorkspace? staging = null,
        string workspaceRoot = "", ToolCallId? sourceCall = null, ToolId? sourceTool = null,
        string turn = "", ResourceClaims? writerClaims = null, ProcessClaim? sourceProcessClaim = null)
    {
        EnsureHostLease();
        token.ThrowIfCancellationRequested();
        if (run.Length == 0 || lane.Length == 0 || !spec.Duplex
            || spec.Arguments.Count > 256 || spec.Arguments.Any(argument => argument.Length > 8192))
            return (null, "failed", "process.start.invalid_request");
        sourceCall ??= new ToolCallId(Guid.Empty);
        sourceTool ??= new ToolId("process.start");
        var capacityLease = sessionQuota?.TryAcquire();
        if (sessionQuota is not null && capacityLease is null)
        {
            _trimSessionProcessHandles?.Invoke();
            capacityLease = sessionQuota.TryAcquire();
        }
        if (sessionQuota is not null && capacityLease is null)
            return (null, "failed", "process.start.session_capacity_exceeded");
        var id = Guid.NewGuid().ToString("N");
        var record = new ProcessRecord(id, run, lane, hostLeaseGeneration, null, DateTimeOffset.UtcNow,
            spec.Timeout ?? TimeSpan.FromMinutes(10), staging, workspaceRoot, sourceCall, sourceTool, turn,
            writerClaims, sourceProcessClaim)
        { CapacityLease = capacityLease };
        lock (_capacityGate)
        {
            RecycleTerminalProcesses();
            if (_closing || _processes.Count + _recovered.Count >= MaximumHandles
                || _processes.Values.Count(process => process.State is "starting" or "running" or "stopping" or "unknown") >= MaximumActiveProcesses)
            {
                capacityLease?.Dispose();
                return (null, "failed", "process.start.capacity_exceeded");
            }
            if (!_processes.TryAdd(id, record)) { capacityLease?.Dispose(); return (null, "failed", "process.handle_collision"); }
        }
        if (!Record(record, emit, "starting"))
        {
            record.StartReady.TrySetResult();
            _processes.TryRemove(id, out _);
            capacityLease?.Dispose();
            DisposeStaging(staging);
            return (null, "failed", "process.start.lifecycle_reservation_failed");
        }
        ISandboxProcessControl? raw = null;
        try
        {
            raw = await launcher.StartAsync(spec, token).ConfigureAwait(false);
            if (raw is not ISandboxPersistentProcessControl control)
                throw new InvalidOperationException("process.persistent_transport_unavailable");
            if (spec.Terminal && !control.IsTerminal)
                throw new InvalidOperationException("process.terminal_unavailable");
            lock (record.Gate) { record.Control = control; record.State = "running"; }
            if (!Record(record, emit, "running"))
            {
                lock (record.Gate) { record.State = "unknown"; record.Diagnostic = "running_state_persistence_failed"; }
                var cleaned = await CleanupUncertainControlAsync(control).ConfigureAwait(false);
                lock (record.Gate)
                {
                    record.Control = null;
                    if (!cleaned) record.UncertainControl = control;
                }
                if (cleaned) DisposeStaging(staging);
                Record(record, emit, "unknown");
                return (id, "unknown", "process.start.lifecycle_persistence_failed");
            }
            record.Pumps = Task.WhenAll(PumpAsync(record, control.StandardOutput, "stdout", emit),
                PumpAsync(record, control.StandardError, "stderr", emit));
            record.Monitor = MonitorAsync(record, emit);
            return (id, "running", null);
        }
        catch (StrongSandboxUnavailableBeforeLaunchException ex) when (raw is null)
        {
            DisposeStaging(staging);
            lock (record.Gate) { record.State = "failed"; record.Diagnostic = ex.GetType().Name; }
            Record(record, emit, "failed");
            record.CapacityLease?.ReleaseActive();
            return (id, "failed", "process.start." + ex.GetType().Name);
        }
        catch (ProcessLaunchUnavailableBeforeStartException ex) when (raw is null)
        {
            DisposeStaging(staging);
            lock (record.Gate) { record.State = "failed"; record.Diagnostic = ex.GetType().Name; }
            Record(record, emit, "failed");
            record.CapacityLease?.ReleaseActive();
            return (id, "failed", "process.start." + ex.GetType().Name);
        }
        catch (StrongSandboxLaunchOutcomeUnknownException ex)
        {
            var cleaned = raw is null || await CleanupUncertainControlAsync(raw).ConfigureAwait(false);
            if (cleaned) DisposeStaging(staging);
            lock (record.Gate)
            {
                record.Control = null;
                if (!cleaned) record.UncertainControl = raw;
                record.State = "unknown";
                // Preserve only stable, non-sensitive failure metadata. In particular, do not
                // persist the exception message: launcher messages may contain absolute paths.
                var cause = ex.InnerException;
                record.Diagnostic = "strong_launch_outcome_unknown:" + ex.GetType().Name
                    + (cause is null ? string.Empty : ":" + cause.GetType().Name + ":0x" + cause.HResult.ToString("X8", CultureInfo.InvariantCulture));
            }
            Record(record, emit, "unknown");
            return (id, "unknown", "process.start.outcome_unknown");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var cleaned = raw is null || await CleanupUncertainControlAsync(raw).ConfigureAwait(false);
            if (cleaned) DisposeStaging(staging);
            const string state = "unknown";
            lock (record.Gate)
            {
                record.Control = null;
                if (!cleaned) record.UncertainControl = raw;
                record.State = state;
                record.Diagnostic = "launch_outcome_unresolved:" + ex.GetType().Name;
            }
            Record(record, emit, state);
            return (id, state, "process.start." + ex.GetType().Name);
        }
        finally { record.StartReady.TrySetResult(); }
    }

    internal void RecycleTerminalProcesses(bool force = false)
    {
        if (!force && _processes.Count + _recovered.Count < MaximumHandles) return;
        foreach (var pair in _processes)
        {
            var process = pair.Value;
            lock (process.Gate)
            {
                if (process.State is not ("exited" or "stopped" or "cancelled" or "failed" or "timed_out" or "output_limit")
                    || process.Proposal is not null || !process.Monitor.IsCompleted)
                    continue;
                if (_processes.TryRemove(pair.Key, out _))
                {
                    process.CapacityLease?.Dispose();
                    process.OutputChanged.Dispose();
                    process.InputGate.Dispose();
                }
            }
            if (!force && _processes.Count + _recovered.Count < MaximumHandles) return;
        }
    }

    private static async Task<bool> CleanupUncertainControlAsync(ISandboxProcessControl control)
    {
        try { await control.TerminateAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return false; }
        try { await control.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return false; }
        return true;
    }

    internal static void DisposeStaging(ExtensionWriteWorkspace? staging)
    {
        if (staging is null) return;
        try { staging.Dispose(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
    }

    private static bool PathEquals(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), comparison);
    }

    private readonly ConcurrentDictionary<string, (string Run, string Lane, string SourceTool, long Last, int? Exit, bool Lost)> _recovered = new(StringComparer.Ordinal);

    internal void RestoreUnknown(IReadOnlyDictionary<string, ProcessSessionStateRecorded> latest)
    {
        EnsureHostLease();
        foreach (var evidence in latest.Values)
        {
            evidence.Validate();
            if (evidence.ProposalId is { } proposalId)
            {
                var recoveredCapacity = sessionQuota?.TryRecoverHandle();
                if (sessionQuota is not null && recoveredCapacity is null)
                    throw new InvalidDataException("Recovered process handles exceed the Session capacity.");
                if (artifacts is not IProtectedArtifactStore protectedStore)
                    throw new InvalidDataException("Durable process proposal requires a protected artifact store.");
                var artifactHash = evidence.ProposalArtifactHash
                    ?? throw new InvalidDataException("Durable process proposal hash is missing.");
                var artifactSize = evidence.ProposalArtifactSize
                    ?? throw new InvalidDataException("Durable process proposal size is missing.");
                var sourceCall = evidence.SourceCallId
                    ?? throw new InvalidDataException("Durable process source call is missing.");
                var turn = evidence.TurnId
                    ?? throw new InvalidDataException("Durable process source turn is missing.");
                var root = evidence.WorkspaceRoot
                    ?? throw new InvalidDataException("Durable process workspace root is missing.");
                if (!IsProcessWriterTool(evidence.SourceToolId) || !PathEquals(root, workspaceRoot))
                    throw new InvalidDataException("Durable process proposal reference is incomplete or belongs to another workspace.");
                var artifact = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(artifactHash), artifactSize,
                    "application/json", ArtifactKind.Patch, Sensitivity.Sensitive);
                var json = protectedStore.GetProtectedText(artifact, ExtensionTool.ProposalPurpose(workspaceRoot, proposalId));
                if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
                    throw new InvalidDataException("Durable process proposal exceeds the protected batch limit.");
                var proposal = JsonSerializer.Deserialize(json, ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal)
                    ?? throw new InvalidDataException("Durable process proposal is empty.");
                if (proposal.ProposalId != proposalId || proposal.ProcessHandleId != evidence.HandleId
                    || proposal.Origin != "process" || proposal.ToolId != evidence.SourceToolId
                    || proposal.MutationRunId != evidence.RunId || proposal.MutationLaneId != evidence.LaneId
                    || proposal.MutationTurnId != turn || proposal.ToolCallId != sourceCall
                    || !PathEquals(proposal.WorkspaceRoot, workspaceRoot) || proposal.Changes is not { Count: > 0 and <= 64 })
                    throw new InvalidDataException("Durable process proposal does not match its lifecycle receipt.");
                var record = new ProcessRecord(evidence.HandleId, evidence.RunId, evidence.LaneId, evidence.LeaseId,
                    null, DateTimeOffset.MinValue, TimeSpan.Zero, null, workspaceRoot,
                    new ToolCallId(Guid.Parse(sourceCall)), new ToolId(evidence.SourceToolId), turn,
                    new ResourceClaims([], proposal.Changes.Select(change => change.Path).ToArray(), [], null, []), null)
                { Lease = hostLeaseGeneration, State = evidence.State, ExitCode = evidence.ExitCode, LastSequence = evidence.LastOutputSequence,
                    OutputLostOnRecovery = evidence.LastOutputSequence > 0, CapacityLease = recoveredCapacity,
                    Proposal = proposal, ProposalArtifact = artifact };
                if (!_processes.TryAdd(record.Id, record)) { recoveredCapacity?.Dispose(); throw new InvalidDataException("Duplicate durable process handle."); }
                if (_processes.Count + _recovered.Count > MaximumHandles)
                    throw new InvalidDataException("Durable process handles exceed the session retention limit.");
                if (!Record(record, null, evidence.State))
                {
                    _processes.TryRemove(record.Id, out _);
                    recoveredCapacity?.Dispose();
                    throw new InvalidDataException("Recovered process proposal lease could not be persisted.");
                }
                continue;
            }
            if (evidence.State is "exited" or "stopped" or "cancelled" or "failed" or "timed_out" or "output_limit")
                continue;
            if (_processes.Count + _recovered.Count >= MaximumHandles)
                throw new InvalidDataException("Unresolved durable processes exceed the session retention limit.");
            var unresolvedCapacity = sessionQuota?.TryRecoverHandle();
            if (sessionQuota is not null && unresolvedCapacity is null)
                throw new InvalidDataException("Recovered process handles exceed the Session capacity.");
            // Live output is intentionally not journaled; after ownership recovery even a zero
            // durable cursor cannot prove that the old process emitted no bytes before it died.
            _recovered[evidence.HandleId] = (evidence.RunId, evidence.LaneId,
                evidence.SourceToolId ?? "process.start", evidence.LastOutputSequence, evidence.ExitCode, Lost: true);
            if (unresolvedCapacity is not null) _recoveredCapacity[evidence.HandleId] = unresolvedCapacity;
            if (evidence.State is "running" or "starting" or "stopping" or "unknown")
            {
                if (evidence.State != "unknown" || evidence.Diagnostic != "host_restarted_with_process_unresolved")
                    _events.Append(evidence with { State = "unknown", Diagnostic = "host_restarted_with_process_unresolved" }, DurabilityClass.Barrier);
            }
        }
    }

    internal bool UsesWorkspaceRoot(string root) => PathEquals(workspaceRoot, root);

    /// <summary>Read-only live summary; never restores ownership or writes recovery events.</summary>
    internal IReadOnlyList<ProcessSessionSummary> Snapshot(string run)
    {
        var result = new List<ProcessSessionSummary>();
        foreach (var process in _processes.Values.Where(item => item.Run == run
                     && item.SourceTool.ToString() != HumanShellProcessTool.ProcessOwnerSource))
        {
            lock (process.Gate)
                result.Add(new(process.Id, process.State, process.Run, process.Lane, process.LastSequence,
                    process.ExitCode, process.Proposal?.ProposalId, process.SourceTool.ToString(),
                    process.OutputLostOnRecovery, process.Lease == hostLeaseGeneration,
                    process.Lease == hostLeaseGeneration && process.State == "running",
                    process.Lease == hostLeaseGeneration && process.State is "running" or "stopping"));
        }
        foreach (var pair in _recovered.Where(item => item.Value.Run == run
                     && item.Value.SourceTool != HumanShellProcessTool.ProcessOwnerSource))
            result.Add(new(pair.Key, "unknown", pair.Value.Run, pair.Value.Lane, pair.Value.Last,
                pair.Value.Exit, null, pair.Value.SourceTool, pair.Value.Lost, false, false, false));
        return result.OrderBy(item => item.Handle, StringComparer.Ordinal).ToArray();
    }

    internal bool HasRootLaneControlCandidate(string run, string lane) =>
        _processes.Values.Any(process => process.Run == run && process.Lane == lane
            && process.SourceTool.ToString() != HumanShellProcessTool.ProcessOwnerSource
            && process.Lease == hostLeaseGeneration
            && process.State is "running" or "exited" or "stopped" or "timed_out" or "output_limit")
        || _recovered.Values.Any(item => item.Run == run && item.Lane == lane
            && item.SourceTool != HumanShellProcessTool.ProcessOwnerSource);

    internal bool CanControlOwnedProcess(string handle, string run, string lane, string toolName)
    {
        if (toolName is not ("process.read" or "process.send_input" or "process.stop")
            || !ownsHostLease() || !_processes.TryGetValue(handle, out var process)
            || process.SourceTool.ToString() == HumanShellProcessTool.ProcessOwnerSource
            || process.Run != run || process.Lane != lane || process.Lease != hostLeaseGeneration) return false;
        lock (process.Gate)
        {
            return toolName switch
            {
                "process.read" => process.State is "running" or "exited" or "stopped" or "cancelled"
                    or "failed" or "timed_out" or "output_limit" or "unknown",
                "process.send_input" => process.State == "running" && process.Control is not null,
                "process.stop" => process.State is "running" or "exited" or "stopped" or "timed_out"
                    or "output_limit" or "unknown",
                _ => false,
            };
        }
    }

    /// <summary>Process ownership keeps its Run addressable until every process and retained
    /// publication has a durable terminal disposition. This is a passive query.</summary>
    internal bool HasUnresolvedForRun(string run)
    {
        foreach (var process in _processes.Values.Where(item => item.Run == run))
        {
            lock (process.Gate)
                if (process.State is "starting" or "running" or "stopping" or "unknown" or "output_limit"
                    || process.Proposal is not null) return true;
        }
        return _recovered.Values.Any(item => item.Run == run);
    }

    /// <summary>
    /// Passive readback only: it cannot publish, stop, input to, or otherwise advance a process.
    /// The returned cursor covers only delivered chunks; reconnects must call this explicitly.
    /// </summary>
    internal IReadOnlyList<ProcessOutputChunk> Read(string handle, string run, string lane, long after, int maximumChunks,
        out long last, out long highWater, out bool gap, out string state, out int? exitCode)
    {
        if (_recovered.TryGetValue(handle, out var recovered))
        {
            if (recovered.SourceTool == HumanShellProcessTool.ProcessOwnerSource
                || recovered.Run != run || recovered.Lane != lane)
                throw new KeyNotFoundException("process.handle_unavailable");
            highWater = recovered.Last;
            if (after > highWater) throw new ArgumentOutOfRangeException(nameof(after));
            last = after; state = "unknown"; exitCode = recovered.Exit; gap = recovered.Lost || after < highWater;
            return Array.Empty<ProcessOutputChunk>();
        }
        if (!_processes.TryGetValue(handle, out var process) || process.Run != run || process.Lane != lane
            || process.SourceTool.ToString() == HumanShellProcessTool.ProcessOwnerSource)
            throw new KeyNotFoundException("process.handle_unavailable");
        if (process.Lease != hostLeaseGeneration && process.OutputLostOnRecovery)
        {
            lock (process.Gate)
            {
                highWater = process.LastSequence;
                if (after > highWater) throw new ArgumentOutOfRangeException(nameof(after));
                last = after; state = process.State; exitCode = process.ExitCode; gap = after < highWater;
                return Array.Empty<ProcessOutputChunk>();
            }
        }
        if (process.Lease != hostLeaseGeneration) throw new KeyNotFoundException("process.handle_unavailable");
        lock (process.Gate)
        {
            highWater = process.LastSequence;
            if (after > highWater) throw new ArgumentOutOfRangeException(nameof(after));
            last = after; state = process.State; exitCode = process.ExitCode;
            gap = process.OutputLostOnRecovery && after < highWater
                || process.Chunks.Count > 0 && after < process.Chunks.Peek().Sequence - 1;
            var selected = process.Chunks.Where(chunk => chunk.Sequence > after).Take(maximumChunks).ToArray();
            if (selected.Length > 0) last = selected[^1].Sequence;
            // Sanitize contiguous available text per stream as one unit. This catches known
            // secrets and VT sequences split across adjacent pipe reads before returning them.
            return selected.GroupBy(chunk => chunk.Stream, StringComparer.Ordinal)
                .Select(group => new ProcessOutputChunk(group.Max(chunk => chunk.Sequence), group.Key,
                    SafeOutput(string.Concat(group.Select(chunk => chunk.Text)))))
                .OrderBy(chunk => chunk.Sequence).ToArray();
        }
    }

    /// <summary>Private read capability for the native human shell action. Agent process tools,
    /// passive process queries, and process-control claims cannot address these handles.</summary>
    internal IReadOnlyList<ProcessOutputChunk> ReadHumanShell(string handle, string run, string lane,
        long after, int maximumChunks, out long last, out long highWater, out bool gap,
        out string state, out int? exitCode)
    {
        if (_recovered.TryGetValue(handle, out var recovered))
        {
            if (recovered.SourceTool != HumanShellProcessTool.ProcessOwnerSource
                || recovered.Run != run || recovered.Lane != lane)
                throw new KeyNotFoundException("process.handle_unavailable");
            if (maximumChunks is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumChunks));
            highWater = recovered.Last;
            if (after > highWater) throw new ArgumentOutOfRangeException(nameof(after));
            last = after;
            state = "unknown";
            exitCode = recovered.Exit;
            gap = true; // Recovered output is never replayed and no process is relaunched.
            return Array.Empty<ProcessOutputChunk>();
        }
        if (!_processes.TryGetValue(handle, out var process)
            || process.SourceTool.ToString() != HumanShellProcessTool.ProcessOwnerSource
            || process.Run != run || process.Lane != lane)
            throw new KeyNotFoundException("process.handle_unavailable");
        return ReadCore(process, after, maximumChunks, out last, out highWater, out gap, out state, out exitCode);
    }

    private IReadOnlyList<ProcessOutputChunk> ReadCore(ProcessRecord process, long after, int maximumChunks,
        out long last, out long highWater, out bool gap, out string state, out int? exitCode)
    {
        if (maximumChunks is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumChunks));
        if (process.Lease != hostLeaseGeneration && process.OutputLostOnRecovery)
        {
            lock (process.Gate)
            {
                highWater = process.LastSequence;
                if (after > highWater) throw new ArgumentOutOfRangeException(nameof(after));
                last = after; state = process.State; exitCode = process.ExitCode; gap = after < highWater;
                return Array.Empty<ProcessOutputChunk>();
            }
        }
        if (process.Lease != hostLeaseGeneration) throw new KeyNotFoundException("process.handle_unavailable");
        lock (process.Gate)
        {
            highWater = process.LastSequence;
            if (after > highWater) throw new ArgumentOutOfRangeException(nameof(after));
            last = after; state = process.State; exitCode = process.ExitCode;
            gap = process.OutputLostOnRecovery && after < highWater
                || process.Chunks.Count > 0 && after < process.Chunks.Peek().Sequence - 1;
            var selected = process.Chunks.Where(chunk => chunk.Sequence > after).Take(maximumChunks).ToArray();
            if (selected.Length > 0) last = selected[^1].Sequence;
            return selected.GroupBy(chunk => chunk.Stream, StringComparer.Ordinal)
                .Select(group => new ProcessOutputChunk(group.Max(chunk => chunk.Sequence), group.Key,
                    SafeOutput(string.Concat(group.Select(chunk => chunk.Text))))).OrderBy(chunk => chunk.Sequence).ToArray();
        }
    }

    internal async Task<(IReadOnlyList<ProcessOutputChunk> Output, long Last, long HighWater, bool Gap,
        string State, int? ExitCode)> ReadAsync(string handle, string run, string lane, long after,
        int maximumChunks, TimeSpan wait, CancellationToken token, bool humanShell = false)
    {
        if (wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(wait));
        IReadOnlyList<ProcessOutputChunk> ReadForOwner(long cursor, int count, out long last,
            out long highWater, out bool gap, out string state, out int? exitCode) => humanShell
            ? ReadHumanShell(handle, run, lane, cursor, count, out last, out highWater, out gap, out state, out exitCode)
            : Read(handle, run, lane, cursor, count, out last, out highWater, out gap, out state, out exitCode);
        if (_recovered.ContainsKey(handle))
        {
            var recoveredOutput = ReadForOwner(after, maximumChunks, out var recoveredLast,
                out var recoveredHighWater, out var recoveredGap, out var recoveredState, out var recoveredExit);
            return (recoveredOutput, recoveredLast, recoveredHighWater, recoveredGap, recoveredState, recoveredExit);
        }
        if (!_processes.TryGetValue(handle, out var process) || process.Run != run || process.Lane != lane)
            throw new KeyNotFoundException("process.handle_unavailable");
        if (process.Lease != hostLeaseGeneration)
        {
            var lostOutput = ReadForOwner(after, maximumChunks, out var lostLast,
                out var lostHighWater, out var lostGap, out var lostState, out var lostExit);
            return (lostOutput, lostLast, lostHighWater, lostGap, lostState, lostExit);
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            lock (process.Gate)
            {
                while (process.OutputChanged.Wait(0)) { }
                var output = ReadForOwner(after, maximumChunks, out var last,
                    out var highWater, out var gap, out var state, out var exitCode);
                if (output.Count > 0 || state is not ("starting" or "running" or "stopping")
                    || wait == TimeSpan.Zero)
                    return (output, last, highWater, gap, state, exitCode);
            }

            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0) return ReadCurrent();
            var signaled = await process.OutputChanged.WaitAsync(TimeSpan.FromSeconds(
                (double)remaining / Stopwatch.Frequency), token).ConfigureAwait(false);
            if (!signaled) return ReadCurrent();
        }

        (IReadOnlyList<ProcessOutputChunk>, long, long, bool, string, int?) ReadCurrent()
        {
            var output = ReadForOwner(after, maximumChunks, out var last,
                out var highWater, out var gap, out var state, out var exitCode);
            return (output, last, highWater, gap, state, exitCode);
        }
    }

    private static void SignalOutputChanged(ProcessRecord process)
    {
        if (process.OutputChanged.CurrentCount == 0) process.OutputChanged.Release();
    }

    internal bool IsTrustedPrivateWriterIntent(ToolIntent intent)
    {
        try
        {
            if (intent.ToolId.ToString() != "process.stop") return false;
            using var document = JsonDocument.Parse(intent.NormalizedArgumentsJson);
            var handle = document.RootElement.GetProperty("handle").GetString();
            return handle is not null && _processes.TryGetValue(handle, out var process)
                && process.Staging is not null && process.WriterClaims is not null
                && intent.Claims.Process?.Isolation == ProcessIsolation.PrivateProcessWorkspace
                && intent.Claims.Process.OwnerSessionId == sessionId.ToString()
                && intent.Claims.Process.OwnerRunId == process.Run && intent.Claims.Process.OwnerLaneId == process.Lane
                && intent.Claims.Process.OwnerLeaseId == process.Lease
                && process.State is ("running" or "exited" or "stopped")
                && process.WriterClaims.Writes.Count > 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    internal ProcessClaim? ControlClaim(string handle, string run, string lane)
    {
        EnsureHostLease();
        if (!_processes.TryGetValue(handle, out var process) || process.Run != run || process.Lane != lane
            || process.SourceTool.ToString() == HumanShellProcessTool.ProcessOwnerSource
            || process.Lease != hostLeaseGeneration
            || process.State is not ("running" or "exited" or "stopped" or "stopping" or "unknown")
            || process.State == "unknown" && process.Control is null && process.UncertainControl is null) return null;
        var privateWriter = process.Staging is not null && process.WriterClaims is { Writes.Count: > 0 };
        if (enforceChildControlProcessScope && process.SourceProcessClaim is null) return null;
        var claim = enforceChildControlProcessScope
            ? process.SourceProcessClaim!
            : new ProcessClaim("process.handle:" + handle, [], privateWriter ? "PrivateProcessWriter" : "External",
                WorkingDirectory: process.WorkspaceRoot,
                Isolation: privateWriter ? ProcessIsolation.PrivateProcessWorkspace : ProcessIsolation.None);
        return claim with
        {
            OwnerSessionId = sessionId.ToString(), OwnerRunId = process.Run, OwnerLaneId = process.Lane,
            OwnerLeaseId = process.Lease,
        };
    }

    internal bool TryBindProcessControlIntent(ToolIntent intent, ToolExecutionContext execution, out ToolIntent bound)
    {
        bound = intent;
        try
        {
            if (intent.ToolId.ToString() is not ("process.send_input" or "process.stop")) return false;
            using var document = JsonDocument.Parse(intent.NormalizedArgumentsJson);
            var handle = document.RootElement.GetProperty("handle").GetString();
            if (handle is null || ControlClaim(handle, execution.MutationRunId, execution.MutationLaneId) is not { } claim)
                return false;
            var claims = new ResourceClaims(intent.Claims.Reads, intent.Claims.Writes, intent.Claims.Network,
                claim, intent.Claims.Secrets, intent.Claims.WorktreeDisposition, intent.Claims.WorktreeRetention);
            bound = new ToolIntent(intent.ToolCallId, intent.ToolId, intent.NormalizedArgumentsJson,
                intent.Effect, claims, intent.Risk, intent.Reconciliation,
                intent.EffectiveMutationOperation, intent.RequiresPhysicalCreateCapability,
                intent.EffectiveMutationHistoryRevision, intent.EffectiveMutationProposal);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    internal bool IsTrustedPrivateProcessControl(ToolIntent intent)
    {
        try
        {
            if (intent.ToolId.ToString() is not ("process.send_input" or "process.stop")) return false;
            using var document = JsonDocument.Parse(intent.NormalizedArgumentsJson);
            var handle = document.RootElement.GetProperty("handle").GetString();
            return handle is not null && _processes.TryGetValue(handle, out var process)
                && process.Staging is not null && process.WriterClaims is { Writes.Count: > 0 }
                && intent.Claims.Process is { Isolation: ProcessIsolation.PrivateProcessWorkspace, NetworkRequired: false }
                && intent.Claims.Process.OwnerSessionId == sessionId.ToString()
                && intent.Claims.Process.OwnerRunId == process.Run && intent.Claims.Process.OwnerLaneId == process.Lane
                && intent.Claims.Process.OwnerLeaseId == process.Lease
                && process.Lease == hostLeaseGeneration
                && process.State is ("running" or "exited" or "stopped");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    internal bool ValidateCurrentProcessBatchProposal(ExtensionWriteBatchProposal proposal, string workspaceRoot)
    {
        if (proposal.Origin != "process" || proposal.ProcessHandleId is not { } id
            || !IsProcessWriterTool(proposal.ToolId) || proposal.Isolation != ProcessIsolation.PrivateProcessWorkspace.ToString()
            || !PathEquals(proposal.WorkspaceRoot, workspaceRoot)
            || !string.IsNullOrEmpty(proposal.ExtensionId) || !string.IsNullOrEmpty(proposal.ExtensionVersion)
            || !string.IsNullOrEmpty(proposal.PackageSha256) || !string.IsNullOrEmpty(proposal.ManifestSha256)) return false;
        if (!_processes.TryGetValue(id, out var process)) return false;
        if (process.WriterClaims is null
            || process.State is not ("exited" or "stopped") || proposal.ToolCallId != process.SourceCall.ToString()
            || proposal.MutationRunId != process.Run || proposal.MutationLaneId != process.Lane
            || !PathEquals(process.WorkspaceRoot, workspaceRoot)) return false;
        lock (process.Gate)
        {
            if (process.Proposal is null || process.ProposalArtifact is null) return false;
            var incoming = JsonSerializer.Serialize(proposal, ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal);
            var expected = JsonSerializer.Serialize(process.Proposal, ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal);
            if (!string.Equals(incoming, expected, StringComparison.Ordinal)) return false;
            var patterns = process.WriterClaims.Writes;
            return proposal.Changes is { Count: > 0 and <= 64 }
                && proposal.Changes.All(change => patterns.Any(pattern => ExtensionWriteWorkspace.IsPathWithinPattern(pattern, change.Path)));
        }
    }

    internal bool MarkProcessBatchProposalApplied(ExtensionWriteBatchProposal proposal, string workspaceRoot)
    {
        if (proposal.Origin != "process" || proposal.ProcessHandleId is not { } handle
            || !_processes.TryGetValue(handle, out var process) || !PathEquals(workspaceRoot, process.WorkspaceRoot))
            return false;
        lock (process.Gate)
        {
            if (process.Lease != hostLeaseGeneration || process.State is not ("exited" or "stopped")
                || process.Proposal is null || process.ProposalArtifact is null)
                return false;
            var completed = JsonSerializer.Serialize(proposal, ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal);
            var retained = JsonSerializer.Serialize(process.Proposal, ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal);
            if (!string.Equals(completed, retained, StringComparison.Ordinal)) return false;

            // The protected proposal remains retained until the workspace history is durable.
            // This final barrier removes it from the outstanding-process set only after the
            // publisher has verified all postimages and committed the mutation history.
            var priorProposal = process.Proposal;
            var priorArtifact = process.ProposalArtifact;
            process.Proposal = null;
            process.ProposalArtifact = null;
            if (Record(process, null, process.State)) return true;
            process.Proposal = priorProposal;
            process.ProposalArtifact = priorArtifact;
            return false;
        }
    }

    internal (ArtifactRef? Artifact, string? Preview) CreatePrivateProcessProposal(string handle, string run, string lane,
        ToolCallId readCall, string turn, string workspaceRoot, IArtifactStore? artifacts)
    {
        var process = Owned(handle, run, lane);
        lock (process.Gate)
        {
            if (process.Proposal is not null && process.ProposalArtifact is { } saved)
                return (saved, BuildPreview(process.Proposal, saved));
            if (process.Staging is null || process.WriterClaims is null || process.Changes is not { Count: > 0 }
                || process.State is not ("stopped" or "exited")) return (null, null);
            if (!PathEquals(process.WorkspaceRoot, workspaceRoot))
                throw new InvalidOperationException("process.proposal.protected_store_unavailable");
            if (artifacts is not IProtectedArtifactStore protectedStore)
                throw new InvalidOperationException("process.proposal.protected_store_unavailable");
            var proposalId = Guid.NewGuid().ToString("N");
            var changes = new List<ExtensionWriteBatchChange>(process.Changes.Count);
            long total = 0;
            foreach (var change in process.Changes)
            {
                var full = Path.GetFullPath(change.RelativePath, workspaceRoot);
                var current = File.Exists(full) ? File.ReadAllBytes(full) : null;
                var currentHash = current is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(current)).ToLowerInvariant();
                if (currentHash != change.BeforeSha256)
                    throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Process proposal preimage changed after the staged run.");
                total = checked(total + (change.AfterBytes?.LongLength ?? 0));
                if (total > 8 * 1024 * 1024) throw new ToolPreflightException(ToolErrorCode.LimitExceeded, "Process proposal exceeds the safe batch size.");
                changes.Add(new ExtensionWriteBatchChange(change.RelativePath, change.BeforeSha256, change.AfterSha256, change.AfterBytes));
            }
            var proposal = new ExtensionWriteBatchProposal(1, proposalId, Path.GetFullPath(workspaceRoot),
                process.SourceCall.ToString(), process.SourceTool.ToString(), "", "", "", "",
                ProcessIsolation.PrivateProcessWorkspace.ToString(), process.Run, process.Turn, process.Lane,
                changes, "process", process.Id);
            var json = JsonSerializer.Serialize(proposal, ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal);
            if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
                throw new ToolPreflightException(ToolErrorCode.LimitExceeded, "Process proposal exceeds the protected batch limit.");
            var artifact = protectedStore.PutProtectedText(json, ExtensionTool.ProposalPurpose(workspaceRoot, proposalId),
                "application/json", ArtifactKind.Patch);
            process.Proposal = proposal;
            process.ProposalArtifact = artifact;
            if (!Record(process, null, process.State))
                throw new IOException("Protected process proposal could not be durably referenced.");
            return (artifact, BuildPreview(proposal, artifact));
        }
    }

    private static string BuildPreview(ExtensionWriteBatchProposal proposal, ArtifactRef artifact)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("proposalId", proposal.ProposalId);
            writer.WriteString("artifactHash", artifact.Hash.Value);
            writer.WriteNumber("artifactSize", artifact.Size);
            writer.WriteStartArray("files"); foreach (var change in proposal.Changes) writer.WriteStringValue(change.Path);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal async ValueTask SendAsync(string handle, string run, string lane, string input, bool eof,
        short? columns, short? rows, CancellationToken token)
    {
        EnsureHostLease();
        var process = Owned(handle, run, lane);
        // Terminal protocols use control bytes (CR/LF, TAB, Ctrl+C); output remains inert text.
        int inputBytes;
        try { inputBytes = new UTF8Encoding(false, true).GetByteCount(input); }
        catch (EncoderFallbackException) { throw new InvalidDataException("process.input.invalid_utf8"); }
        if (inputBytes > MaximumInputBytes || input.Contains('\0'))
            throw new InvalidDataException("process.input.invalid");
        token.ThrowIfCancellationRequested();
        EnsureHostLease();
        if (process.Lease != hostLeaseGeneration || process.State != "running" || process.Control is null)
            throw new InvalidOperationException("process.handle_not_running");
        var control = process.Control;
        await process.InputGate.WaitAsync(token).ConfigureAwait(false);
        var effectMayHaveStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            EnsureHostLease();
            if (process.Lease != hostLeaseGeneration || process.State != "running" || process.Control is null)
                throw new InvalidOperationException("process.handle_not_running");
            if (!ReferenceEquals(process.Control, control))
                throw new InvalidOperationException("process.handle_not_running");
            if (columns is not null || rows is not null)
            {
                if (!control.IsTerminal || columns is not (> 0 and <= 300) || rows is not (> 0 and <= 200))
                    throw new InvalidOperationException("process.terminal_resize_invalid");
                effectMayHaveStarted = true;
                control.ResizeTerminal(columns.Value, rows.Value);
            }
            if (input.Length > 0)
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(input);
                effectMayHaveStarted = true;
                await control.StandardInput.WriteAsync(bytes, token).ConfigureAwait(false);
                await control.StandardInput.FlushAsync(token).ConfigureAwait(false);
            }
            if (eof)
            {
                effectMayHaveStarted = true;
                await control.CloseInputAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (effectMayHaveStarted && ex is not OutOfMemoryException and not StackOverflowException)
        {
            lock (process.Gate)
            {
                process.State = "unknown";
                process.Diagnostic = "process_input_outcome_unresolved:" + ex.GetType().Name;
                SignalOutputChanged(process);
            }
            Record(process, null, "unknown");
            throw new ProcessInputOutcomeUnknownException("process.input.outcome_unknown", ex);
        }
        finally { process.InputGate.Release(); }
    }

    internal async Task<string> StopAsync(string handle, string run, string lane, Action<DomainEventPayload>? emit,
        CancellationToken token)
    {
        EnsureHostLease();
        var process = Owned(handle, run, lane);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.StartReady.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "process_start_unresolved"; }
            Record(process, emit, "unknown");
            return "unknown";
        }
        ISandboxPersistentProcessControl? control;
        ISandboxProcessControl? uncertainControl;
        string state;
        lock (process.Gate) { control = process.Control; uncertainControl = process.UncertainControl; state = process.State; }
        if (control is null)
        {
            if (uncertainControl is null) return state;
            var cleaned = await CleanupUncertainControlAsync(uncertainControl).ConfigureAwait(false);
            try { await process.Monitor.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                lock (process.Gate) process.Diagnostic = "process_monitor_cleanup_unresolved:" + ex.GetType().Name;
                cleaned = false;
            }
            lock (process.Gate)
            {
                if (cleaned) process.UncertainControl = null;
                process.State = "unknown";
                process.Diagnostic = cleaned ? "process_start_effect_unresolved" : "process_control_cleanup_unresolved";
            }
            if (cleaned) DisposeStaging(process.Staging);
            Record(process, emit, "unknown");
            return "unknown";
        }
        var terminal = state is "exited" or "stopped" or "cancelled" or "failed" or "timed_out" or "output_limit";
        if (!terminal)
        {
            lock (process.Gate) process.State = "stopping";
            if (!Record(process, emit, "stopping")) return "unknown";
            try { await control.TerminateAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
                or OperationCanceledException)
            { lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "process_stop_failed:" + ex.GetType().Name; } }
        }
        try { await process.Monitor.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var cleaned = await CleanupUncertainControlAsync(control).ConfigureAwait(false);
            if (cleaned) DisposeStaging(process.Staging);
            lock (process.Gate)
            {
                process.Control = null;
                if (!cleaned) process.UncertainControl = control;
            }
            lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "process_exit_unobserved:" + ex.GetType().Name; }
            Record(process, emit, "unknown");
        }
        if (process.State == "stopping")
        {
            lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "process_terminal_state_missing"; }
            Record(process, emit, "unknown");
        }
        return process.State;
    }

    internal async Task StopRunAsync(string run, Action<DomainEventPayload>? emit)
    {
        foreach (var process in _processes.Values.Where(process => process.Run == run
            && process.State is "starting" or "running" or "stopping" or "unknown" or "output_limit" or "timed_out"))
        {
            try { await StopAsync(process.Id, process.Run, process.Lane, emit, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "run_terminal_cleanup_failed:" + ex.GetType().Name; }
                Record(process, emit, "unknown");
            }
        }
    }

    internal async Task PrepareScopeAsync(string run, string lane)
    {
        EnsureHostLease();
        foreach (var process in _processes.Values.Where(process => process.Run != run || process.Lane != lane)
            .Where(process => process.State is "running" or "starting" or "stopping" or "unknown" or "output_limit" or "timed_out"))
        {
            try { await StopAsync(process.Id, process.Run, process.Lane, null, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "scope_transition_cleanup_failed:" + ex.GetType().Name; }
                Record(process, null, "unknown");
            }
        }
    }

    internal async Task StopAllAsync(Action<DomainEventPayload>? emit = null)
    {
        lock (_capacityGate) _closing = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var process in _processes.Values)
        {
            if (process.State == "starting")
            {
                try { await process.StartReady.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "shutdown_start_unresolved"; }
                    Record(process, emit, "unknown");
                    continue;
                }
            }
            var monitorPending = !process.Monitor.IsCompleted;
            if (!monitorPending && process.State is not ("running" or "stopping" or "unknown" or "output_limit" or "timed_out")) continue;
            try { await StopAsync(process.Id, process.Run, process.Lane, emit, deadline.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "host_shutdown_cleanup_failed:" + ex.GetType().Name; }
                Record(process, emit, "unknown");
            }
        }
    }

    internal void ValidateHandle(string handle, string run, string lane) => _ = Owned(handle, run, lane);

    internal async Task<string> WaitForTerminalAsync(string handle, string run, string lane, CancellationToken token)
    {
        var process = Owned(handle, run, lane);
        await process.StartReady.Task.WaitAsync(token).ConfigureAwait(false);
        var limit = process.Timeout + TimeSpan.FromSeconds(31);
        try { await process.Monitor.WaitAsync(limit, token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "process_terminal_wait_unresolved"; }
            Record(process, null, "unknown");
        }
        return process.State;
    }

    internal string? GetTerminalDiagnostic(string handle, string run, string lane)
    {
        var process = Owned(handle, run, lane);
        lock (process.Gate) return process.Diagnostic;
    }

    private ProcessRecord Owned(string handle, string run, string lane)
    {
        EnsureHostLease();
        if (string.IsNullOrWhiteSpace(handle) || handle.Length != 32 || !_processes.TryGetValue(handle, out var process)
            || process.Lease != hostLeaseGeneration || run != process.Run || lane != process.Lane)
            throw new KeyNotFoundException("process.handle_unavailable");
        return process;
    }

    private void EnsureHostLease()
    {
        if (!ownsHostLease()) throw new UnauthorizedAccessException("process.host_lease_unavailable");
    }

    private static string SafeOutput(string value)
    {
        var safe = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '\u001b')
            {
                if (++index >= value.Length) break;
                var introducer = value[index];
                if (introducer == '[')
                {
                    while (index + 1 < value.Length && !(value[index + 1] is >= '@' and <= '~')) index++;
                    if (index + 1 < value.Length) index++;
                }
                else if (introducer is ']' or 'P' or '^' or '_')
                {
                    while (index + 1 < value.Length && value[index + 1] != '\a')
                    {
                        if (value[index + 1] == '\u001b' && index + 2 < value.Length && value[index + 2] == '\\')
                        { index += 2; break; }
                        index++;
                    }
                    if (index + 1 < value.Length && value[index + 1] == '\a') index++;
                }
                continue;
            }
            if ((char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
                safe.Append('\uFFFD');
            else safe.Append(ch);
        }
        return OmniCore.Abstractions.SecretRedactorRegistry.Current?.Redact(safe.ToString()) ?? safe.ToString();
    }

    private sealed class StreamOutputSanitizer
    {
        private readonly StringBuilder _pending = new();

        internal string Append(string value, bool endOfStream = false)
        {
            _pending.Append(value);
            if (_pending.Length > MaximumPendingSanitizerChars)
            {
                _pending.Clear();
                return "[REDACTED: process output exceeded the incremental sanitizer buffer]";
            }

            var combined = _pending.ToString();
            combined = SecretRedactorRegistry.Current?.Redact(combined) ?? new PiiRedactor().Redact(combined);
            if (endOfStream)
            {
                if (SecretRedactorRegistry.Current is { } secrets)
                {
                    var secretPrefixLength = secrets.PotentialSecretPrefixSuffixLength(combined);
                    if (secretPrefixLength > 0)
                        combined = combined[..^secretPrefixLength] + OmniCore.Security.SecretRedactor.Marker;
                }
                var credentialSuffixStart = PiiRedactor.PotentialCredentialSuffixStart(combined);
                if (credentialSuffixStart < combined.Length)
                    combined = combined[..credentialSuffixStart] + OmniCore.Security.SecretRedactor.Marker;
                _pending.Clear();
                return SafeOutput(combined);
            }

            var holdStart = combined.Length;
            if (SecretRedactorRegistry.Current is { } streamSecrets)
                holdStart = Math.Min(holdStart, combined.Length - streamSecrets.PotentialSecretPrefixSuffixLength(combined));
            holdStart = Math.Min(holdStart, PiiRedactor.PotentialCredentialSuffixStart(combined));
            holdStart = Math.Min(holdStart, IncompleteTerminalSequenceStart(combined));
            if (holdStart <= 0) return string.Empty;

            var safe = combined[..holdStart];
            _pending.Clear();
            _pending.Append(combined.AsSpan(holdStart));
            return SafeOutput(safe);
        }

        private static int IncompleteTerminalSequenceStart(string text)
        {
            for (var escape = 0; escape < text.Length; escape++)
            {
                if (text[escape] != '\u001b') continue;
                if (escape + 1 >= text.Length) return escape;
                var kind = text[escape + 1];
                if (kind == '[')
                {
                    var end = escape + 2;
                    while (end < text.Length && !(text[end] is >= '@' and <= '~')) end++;
                    if (end == text.Length) return escape;
                    escape = end;
                    continue;
                }
                if (kind is ']' or 'P' or '^' or '_')
                {
                    var end = escape + 2;
                    var terminated = false;
                    while (end < text.Length)
                    {
                        if (kind == ']' && text[end] == '\a') { terminated = true; end++; break; }
                        if (text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\')
                        { terminated = true; end += 2; break; }
                        end++;
                    }
                    if (!terminated) return escape;
                    escape = end - 1;
                    continue;
                }
                if (escape + 2 >= text.Length) return escape;
                escape++;
            }
            return text.Length;
        }
    }

    private static void EnqueueSanitizedOutput(ProcessRecord process, string streamName, string text,
        Action<DomainEventPayload>? emit)
    {
        if (text.Length == 0) return;
        var bytes = Encoding.UTF8.GetByteCount(text);
        lock (process.Gate)
        {
            var sequence = ++process.LastSequence;
            process.Chunks.Enqueue((sequence, streamName, text, bytes));
            process.Retained += bytes;
            while (process.Retained > RetainedBytes && process.Chunks.TryDequeue(out var removed))
                process.Retained -= removed.Bytes;
            SignalOutputChanged(process);
        }
    }

    private async Task PumpAsync(ProcessRecord process, Stream stream, string streamName, Action<DomainEventPayload>? emit)
    {
        var buffer = new byte[4096];
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var sanitizer = new StreamOutputSanitizer();
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                decoder.Convert(buffer, 0, count, chars, 0, chars.Length, count == 0,
                    out var bytesUsed, out var charsUsed, out _);
                if (bytesUsed != count) throw new InvalidDataException("process.output.decoder_overflow");
                if (count == 0 && charsUsed == 0)
                {
                    EnqueueSanitizedOutput(process, streamName,
                        sanitizer.Append(string.Empty, endOfStream: true), emit);
                    break;
                }
                var text = new string(chars, 0, charsUsed);
                var exceedsLimit = false;
                lock (process.Gate)
                {
                    process.Total = checked(process.Total + count);
                    if (process.Total > MaximumTotalBytes)
                    {
                        process.State = "output_limit";
                        process.Diagnostic = "output_limit_exceeded";
                        SignalOutputChanged(process);
                        exceedsLimit = true;
                    }
                }
                if (!exceedsLimit)
                    EnqueueSanitizedOutput(process, streamName, sanitizer.Append(text), emit);
                if (exceedsLimit)
                {
                    if (process.Control is { } control)
                    {
                        try { await control.TerminateAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                        { lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "output_limit_stop_failed"; } }
                    }
                    EnqueueSanitizedOutput(process, streamName,
                        sanitizer.Append(text, endOfStream: true), emit);
                    break;
                }
                if (count == 0)
                {
                    EnqueueSanitizedOutput(process, streamName, sanitizer.Append(string.Empty, endOfStream: true), emit);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (sanitizer.Append(string.Empty, endOfStream: true) is { Length: > 0 })
                EnqueueSanitizedOutput(process, streamName, "[REDACTED: process output withheld after stream error]", emit);
            lock (process.Gate)
            {
                process.State = "unknown";
                process.Diagnostic = "output_read_failed:" + ex.GetType().Name;
                SignalOutputChanged(process);
            }
            if (process.Control is { } control)
            {
                var cleaned = await CleanupUncertainControlAsync(control).ConfigureAwait(false);
                if (cleaned) DisposeStaging(process.Staging);
                lock (process.Gate)
                {
                    process.Control = null;
                    if (!cleaned) process.UncertainControl = control;
                }
            }
            Record(process, emit, "unknown");
        }
    }

    private async Task MonitorAsync(ProcessRecord process, Action<DomainEventPayload>? emit)
    {
        try
        {
            var control = process.Control ?? throw new InvalidOperationException("process.control_missing");
            var wait = control.WaitForExitCodeAsync(CancellationToken.None).AsTask();
            var timedOut = await Task.WhenAny(wait, Task.Delay(process.Timeout)).ConfigureAwait(false) != wait;
            if (timedOut)
            {
                // Keep a nonterminal state visible until the process tree has actually exited.
                // A timeout is an observed cause, not proof that cleanup already completed.
                lock (process.Gate) { process.TimedOut = true; process.State = "stopping"; }
                Record(process, emit, "stopping");
                await control.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            }
            var code = await wait.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await process.Pumps.ConfigureAwait(false);
            lock (process.Gate)
            {
                process.ExitCode = code;
                if (process.TimedOut) process.State = "timed_out";
                else if (process.State == "running") process.State = "exited";
                else if (process.State == "stopping") process.State = "stopped";
                SignalOutputChanged(process);
            }
            if (process.Staging is not null)
            {
                try { process.Changes = process.Staging.CaptureChanges(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    lock (process.Gate) { process.State = "unknown"; process.Diagnostic = "private_workspace_capture_failed"; }
                }
            }
            // The Strong control owns ACL grants on the private staging tree. Revoke those
            // grants while every granted path still exists, then remove the private copy.
            await control.DisposeAsync().ConfigureAwait(false);
            DisposeStaging(process.Staging);
            Record(process, emit, process.State);
            if (process.State is "exited" or "stopped" or "cancelled" or "failed" or "timed_out" or "output_limit")
                process.CapacityLease?.ReleaseActive();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var cleaned = true;
            if (process.Control is { } control) cleaned = await CleanupUncertainControlAsync(control).ConfigureAwait(false);
            if (cleaned) DisposeStaging(process.Staging);
            lock (process.Gate)
            {
                if (process.Control is { } currentControl)
                {
                    process.Control = null;
                    if (!cleaned) process.UncertainControl = currentControl;
                }
            }
            lock (process.Gate)
            {
                process.State = "unknown";
                process.Diagnostic = "process_exit_unobserved:" + ex.GetType().Name
                    + ":0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);
            }
            Record(process, emit, "unknown");
        }
    }

    internal static bool IsProcessWriterTool(string? toolId) =>
        toolId is "process.start" or "process.exec" or "shell.exec";

    private bool Record(ProcessRecord process, Action<DomainEventPayload>? emit, string state)
    {
        try
        {
            EnsureHostLease();
            lock (process.Gate)
            {
                process.State = state;
                var evidence = new ProcessSessionStateRecorded(process.Id, state, process.Run, process.Lane,
                    process.Lease, process.LastSequence, process.ExitCode, process.Proposal?.ProposalId,
                    process.Diagnostic, process.ProposalArtifact?.Hash.Value, process.ProposalArtifact?.Size,
                    process.SourceCall.ToString(), process.SourceTool.ToString(),
                    process.Turn, process.WorkspaceRoot);
                _events.Append(evidence, DurabilityClass.Barrier);
                SignalOutputChanged(process);
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            lock (process.Gate)
            {
                process.State = "unknown";
                process.Diagnostic = "process_lifecycle_persistence_failed";
            }
            return false;
        }
    }
}

internal sealed class ProcessStartTool(PersistentProcessSessionManager manager, ISandboxProcessLauncher launcher,
    SandboxStrength strength, IPathBoundaryValidator paths, IExecutableResolver executables) : ITool,
    IStagedExtensionBatchProducer, IHostResolvedProcessExecutable
{
    private readonly ToolDescriptor _descriptor = ProcessSessionToolJson.Descriptor("process.start", ToolRisk.Critical,
        "Starts a scoped process and returns an opaque handle; use process.read, process.send_input or process.stop. Writer paths run in a private copy and publish as one batch.",
        "{\"type\":\"object\",\"properties\":{\"executable\":{\"type\":\"string\"},\"argv\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"cwd\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\"},\"terminal\":{\"type\":\"boolean\"},\"readPaths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":64},\"writePaths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":64}},\"required\":[\"executable\",\"argv\",\"cwd\",\"timeoutSeconds\"],\"additionalProperties\":false}");
    public ToolDescriptor Descriptor => _descriptor;
    public bool IsHostResolvedProcessExecutable(ToolIntent intent) => intent.ToolId.Equals(Descriptor.Id)
        && intent.Claims.Process is { } process && Path.IsPathFullyQualified(process.Executable)
        && (process.Isolation == ProcessIsolation.None
            || process.Isolation == ProcessIsolation.PrivateProcessWorkspace);

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.NormalizedArgumentsJson); var root = doc.RootElement;
            var executable = root.GetProperty("executable").GetString(); var cwd = root.GetProperty("cwd").GetString();
            var timeout = root.GetProperty("timeoutSeconds").GetInt32();
            if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(cwd) || timeout is < 1 or > 3600
                || root.GetProperty("argv").ValueKind != JsonValueKind.Array) return new PreparationRejected("Invalid process request", null, ToolErrorCode.InvalidArguments);
            var args = root.GetProperty("argv").EnumerateArray().Select(value => value.GetString()).ToArray();
            if (args.Any(value => value is null || value.Length > 8192) || args.Length > 256) return new PreparationRejected("Invalid process arguments", null, ToolErrorCode.InvalidArguments);
            var path = Path.GetFullPath(cwd, context.WorkspaceRoot);
            if (!paths.IsWithin(path, context.WorkspaceRoot) || !Directory.Exists(path))
                return new PreparationRejected("Process cwd must be an existing directory inside the workspace", null, ToolErrorCode.CapabilityRefused);
            var resolvedExecutable = executables.Resolve(executable, context.WorkspaceRoot).ResolvedPath;
            var reads = ProcessSessionToolJson.ReadPatterns(root, "readPaths");
            var writes = ProcessSessionToolJson.ReadPatterns(root, "writePaths");
            if (reads.Length > 64 || writes.Length > 64
                || reads.Any(pattern => !ExtensionWriteWorkspace.IsValidPattern(pattern))
                || writes.Any(pattern => !ExtensionWriteWorkspace.IsValidPattern(pattern)))
                return new PreparationRejected("Invalid process path scope", null, ToolErrorCode.InvalidArguments);
            if (paths.IsWithin(resolvedExecutable, context.WorkspaceRoot))
            {
                var relativeExecutable = Path.GetRelativePath(context.WorkspaceRoot, resolvedExecutable)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (new RedactionPolicy().IsSecretPath(relativeExecutable))
                    return new PreparationRejected("Process executable is in a protected path", null, ToolErrorCode.CapabilityRefused);
                if (!reads.Any(pattern => ExtensionWriteWorkspace.IsPathWithinPattern(pattern, relativeExecutable)))
                    reads = [.. reads, relativeExecutable];
            }
            if (reads.Length > 64)
                return new PreparationRejected("Process read scope exceeds the safe limit", null, ToolErrorCode.LimitExceeded);
            var isolation = reads.Length + writes.Length > 0 ? ProcessIsolation.PrivateProcessWorkspace : ProcessIsolation.None;
            var claims = new ResourceClaims(reads, writes, [], new ProcessClaim(resolvedExecutable, args!, "External", false, path, isolation), []);
            return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.NonIdempotent, claims, ToolRisk.Critical, null));
        }
        catch (ExecutableNotFoundException)
        { return new PreparationRejected("Process executable could not be resolved", null, ToolErrorCode.InvalidArguments); }
        catch (ExecutableRequiresShellException)
        { return new PreparationRejected("Batch scripts require the explicit shell tool", null, ToolErrorCode.CapabilityRefused); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or InvalidDataException)
        { return new PreparationRejected("Invalid process request", null, ToolErrorCode.InvalidArguments); }
    }

    public bool IsTrustedPrivateWorkspaceWriter(ToolIntent intent) =>
        intent.ToolId.ToString() == "process.start"
        && intent.Claims.Process?.Isolation == ProcessIsolation.PrivateProcessWorkspace
        && intent.Claims.Writes.Count is > 0 and <= 64;

    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context, CancellationToken token)
    {
        var run = context.MutationRunId; var lane = context.MutationLaneId;
        ExtensionWriteWorkspace? staging = null;
        var managerOwnsStaging = false;
        try
        {
            using var doc = JsonDocument.Parse(intent.Intent.NormalizedArgumentsJson); var root = doc.RootElement;
            var executable = intent.Intent.Claims.Process!.Executable;
            var args = root.GetProperty("argv").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var cwd = Path.GetFullPath(root.GetProperty("cwd").GetString()!, context.WorkspaceRoot);
            var timeout = TimeSpan.FromSeconds(root.GetProperty("timeoutSeconds").GetInt32());
            var terminal = root.TryGetProperty("terminal", out var terminalValue) && terminalValue.ValueKind == JsonValueKind.True;
            var stagedProcess = intent.Intent.Claims.Process?.Isolation == ProcessIsolation.PrivateProcessWorkspace;
            var writer = stagedProcess && intent.Intent.Claims.Writes.Count > 0;
            if (stagedProcess)
                staging = ExtensionWriteWorkspace.Create(context.WorkspaceRoot,
                    Path.Combine(Path.GetTempPath(), "OmniCore", "process-workspaces"), intent.Intent.Claims);
            if (staging is not null && paths.IsWithin(executable, context.WorkspaceRoot))
            {
                var relativeExecutable = Path.GetRelativePath(context.WorkspaceRoot, executable);
                executable = Path.GetFullPath(relativeExecutable, staging.Root);
                if (!File.Exists(executable)) throw new FileNotFoundException("Resolved process executable is not present in its authorized private copy.", executable);
            }
            var launchCwd = cwd;
            IReadOnlyList<SandboxAllowedPath> allowedPaths;
            if (staging is not null)
            {
                var relativeCwd = Path.GetRelativePath(context.WorkspaceRoot, cwd);
                launchCwd = relativeCwd == "." ? staging.Root : Path.GetFullPath(relativeCwd, staging.Root);
                if (!paths.IsWithin(launchCwd, staging.Root)) throw new InvalidDataException("Process working directory escapes its private copy.");
                Directory.CreateDirectory(launchCwd);
                allowedPaths = [new SandboxAllowedPath(staging.Root, SandboxPathAccess.Read
                    | (writer ? SandboxPathAccess.Write : SandboxPathAccess.None))];
            }
            else allowedPaths = [new SandboxAllowedPath(cwd, SandboxPathAccess.Read)];
            var launch = new SandboxLaunchSpec(writer ? SandboxStrength.Strong : strength, executable, args, launchCwd, new Dictionary<string, string>(),
                allowedPaths, new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), timeout)
                { Duplex = true, Terminal = terminal };
            var started = await manager.StartAsync(run, lane, launch, launcher, context.EmitEvent, token,
                staging, context.WorkspaceRoot, intent.Intent.ToolCallId, intent.Intent.ToolId,
                context.MutationTurnId, writer ? intent.Intent.Claims : null,
                intent.Intent.Claims.Process).ConfigureAwait(false);
            managerOwnsStaging = started.Handle is not null;
            if (started.Handle is null) return ToolResult.Error(ToolErrorCode.ToolFailure, started.Error ?? "process.start.failed");
            var summary = ProcessSessionToolJson.StartSummary(started.Handle, started.State, run, lane, terminal);
            if (started.State == "unknown")
                return new ToolResult(summary, null, null, 0, true, EffectOutcome.Unknown, true, ToolErrorCode.UnknownEffect);
            if (started.State != "running")
                return new ToolResult(summary, null, null, 0, true, EffectOutcome.None, true, ToolErrorCode.ToolFailure);
            return new ToolResult(summary, null, null, 0, false, EffectOutcome.Applied);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        { return ToolResult.Error(ToolErrorCode.ToolFailure, "process.start." + ex.GetType().Name); }
        finally { if (!managerOwnsStaging) PersistentProcessSessionManager.DisposeStaging(staging); }
    }
}

internal sealed class ProcessReadTool(PersistentProcessSessionManager manager) : ITool
{
    // Deliberately not a batch producer. Reading output never commits the private workspace.
    private readonly ToolDescriptor _descriptor = ProcessSessionToolJson.Descriptor("process.read", ToolRisk.Medium,
        "Reads bounded sequenced output from an owned process handle; an optional bounded passive wait returns when new output or a terminal state arrives.", "{\"type\":\"object\",\"properties\":{\"handle\":{\"type\":\"string\"},\"afterSequence\":{\"type\":\"integer\"},\"maxChunks\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":128},\"waitMs\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10000}},\"required\":[\"handle\",\"afterSequence\"],\"additionalProperties\":false}");
    public ToolDescriptor Descriptor => _descriptor;
    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            using var document = JsonDocument.Parse(call.NormalizedArgumentsJson);
            var root = document.RootElement;
            var handle = root.GetProperty("handle").GetString();
            if (string.IsNullOrWhiteSpace(handle)) return new PreparationRejected("Invalid process handle", null, ToolErrorCode.InvalidArguments);
            return ProcessSessionToolJson.Prepare(call, Descriptor, EffectClass.None);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new PreparationRejected("Invalid process request", null, ToolErrorCode.InvalidArguments); }
    }
    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context, CancellationToken token)
    {
        try
        {
            using var doc = JsonDocument.Parse(intent.Intent.NormalizedArgumentsJson); var root = doc.RootElement;
            var after = root.GetProperty("afterSequence").GetInt64(); var count = root.TryGetProperty("maxChunks", out var max) ? max.GetInt32() : 32;
            if (after < 0 || count is < 1 or > 128) return Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments, "process.read.bounds"));
            var waitMs = root.TryGetProperty("waitMs", out var wait) ? wait.GetInt32() : 0;
            if (waitMs is < 0 or > 10_000)
                return Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments, "process.read.bounds"));
            return ReadAsync();

            async Task<ToolResult> ReadAsync()
            {
                var result = await manager.ReadAsync(root.GetProperty("handle").GetString()!, context.MutationRunId,
                    context.MutationLaneId, after, count, TimeSpan.FromMilliseconds(waitMs), token).ConfigureAwait(false);
                var preview = ProcessSessionToolJson.ReadPreview(result.State, result.ExitCode, after,
                    result.Last, result.HighWater, result.Gap, result.Output, null);
                return new ToolResult("Process output read.", preview, null, result.Output.Count, false, EffectOutcome.Applied);
            }
        }
        catch (KeyNotFoundException) { return Task.FromResult(ToolResult.Error(ToolErrorCode.PermissionDenied, "process.handle_unavailable")); }
        catch (ArgumentOutOfRangeException) { return Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments, "process.read.cursor_ahead")); }
        catch (ToolPreflightException ex) { return Task.FromResult(ToolResult.Error(ex.ErrorCode, ex.Message)); }
    }
}

internal sealed class ProcessSendInputTool(PersistentProcessSessionManager manager) : ITool, IHostScopedPrivateProcessControl
{
    private readonly ToolDescriptor _descriptor = ProcessSessionToolJson.Descriptor("process.send_input", ToolRisk.Critical,
        "Sends bounded UTF-8 input or EOF to an owned process. Input may cause external effects.",
        "{\"type\":\"object\",\"properties\":{\"handle\":{\"type\":\"string\"},\"input\":{\"type\":\"string\",\"maxLength\":16384},\"eof\":{\"type\":\"boolean\"},\"columns\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":300},\"rows\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"handle\",\"input\"],\"additionalProperties\":false}");
    public ToolDescriptor Descriptor => _descriptor;
    public bool IsTrustedPrivateProcessControl(ToolIntent intent) => manager.IsTrustedPrivateProcessControl(intent);
    public bool TryBindProcessControlIntent(ToolIntent intent, ToolExecutionContext execution, out ToolIntent boundIntent) =>
        manager.TryBindProcessControlIntent(intent, execution, out boundIntent);
    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            using var document = JsonDocument.Parse(call.NormalizedArgumentsJson);
            var handle = document.RootElement.GetProperty("handle").GetString();
            if (string.IsNullOrWhiteSpace(handle)) return new PreparationRejected("Invalid process handle", null, ToolErrorCode.InvalidArguments);
            var root = document.RootElement;
            var input = root.GetProperty("input").GetString();
            var hasColumns = root.TryGetProperty("columns", out var columns);
            var hasRows = root.TryGetProperty("rows", out var rows);
            if (input is null || input.Contains('\0') || new UTF8Encoding(false, true).GetByteCount(input) > 16_384
                || root.TryGetProperty("eof", out var eof) && eof.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || hasColumns != hasRows
                || hasColumns && (!columns.TryGetInt32(out var columnCount) || columnCount is < 1 or > 300
                    || !rows.TryGetInt32(out var rowCount) || rowCount is < 1 or > 200))
                return new PreparationRejected("Invalid process input or terminal bounds", null, ToolErrorCode.InvalidArguments);
            return ProcessSessionToolJson.Prepare(call, Descriptor, EffectClass.NonIdempotent);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new PreparationRejected("Invalid process request", null, ToolErrorCode.InvalidArguments); }
    }
    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context, CancellationToken token)
    {
        try
        {
            using var doc = JsonDocument.Parse(intent.Intent.NormalizedArgumentsJson); var root = doc.RootElement;
            var handle = root.GetProperty("handle").GetString()!; var input = root.GetProperty("input").GetString()!;
            short? columns = root.TryGetProperty("columns", out var c) ? checked((short)c.GetInt32()) : null;
            short? rows = root.TryGetProperty("rows", out var r) ? checked((short)r.GetInt32()) : null;
            await manager.SendAsync(handle, context.MutationRunId, context.MutationLaneId, input,
                root.TryGetProperty("eof", out var eof) && eof.ValueKind == JsonValueKind.True, columns, rows,
                token).ConfigureAwait(false);
            return new ToolResult("Input accepted.", null, null, 0, false, EffectOutcome.Applied);
        }
        catch (KeyNotFoundException) { return ToolResult.Error(ToolErrorCode.PermissionDenied, "process.handle_unavailable"); }
        catch (ProcessInputOutcomeUnknownException ex)
        { return new ToolResult(ex.Message, null, null, 0, false, EffectOutcome.Unknown, false, ToolErrorCode.UnknownEffect); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or OverflowException)
        { return ToolResult.Error(ToolErrorCode.InvalidArguments, "process.input." + ex.GetType().Name); }
    }
}

internal sealed class ProcessStopTool(PersistentProcessSessionManager manager) : ITool, IStagedExtensionBatchProducer, IHostScopedPrivateProcessControl
{
    private readonly ToolDescriptor _descriptor = ProcessSessionToolJson.Descriptor("process.stop", ToolRisk.Critical,
        "Stops an owned process and its sandboxed descendants, then waits for a verifiable terminal state.",
        "{\"type\":\"object\",\"properties\":{\"handle\":{\"type\":\"string\"}},\"required\":[\"handle\"],\"additionalProperties\":false}");
    public ToolDescriptor Descriptor => _descriptor;
    public bool TryBindProcessControlIntent(ToolIntent intent, ToolExecutionContext execution, out ToolIntent boundIntent) =>
        manager.TryBindProcessControlIntent(intent, execution, out boundIntent);
    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            using var document = JsonDocument.Parse(call.NormalizedArgumentsJson);
            var handle = document.RootElement.GetProperty("handle").GetString();
            if (string.IsNullOrWhiteSpace(handle)) return new PreparationRejected("Invalid process handle", null, ToolErrorCode.InvalidArguments);
            return ProcessSessionToolJson.Prepare(call, Descriptor, EffectClass.NonIdempotent);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new PreparationRejected("Invalid process request", null, ToolErrorCode.InvalidArguments); }
    }
    public bool IsTrustedPrivateWorkspaceWriter(ToolIntent intent) => manager.IsTrustedPrivateWriterIntent(intent);
    public bool IsTrustedPrivateProcessControl(ToolIntent intent) => manager.IsTrustedPrivateProcessControl(intent);
    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context, CancellationToken token)
    {
        try
        {
            using var doc = JsonDocument.Parse(intent.Intent.NormalizedArgumentsJson);
            var state = await manager.StopAsync(doc.RootElement.GetProperty("handle").GetString()!,
                context.MutationRunId, context.MutationLaneId, context.EmitEvent, token).ConfigureAwait(false);
            if (state is "unknown" or "failed" or "timed_out" or "output_limit")
                return new ToolResult(ProcessSessionToolJson.StopSummary(state, false), null, null, 0, false, EffectOutcome.Unknown);
            var (artifact, proposalPreview) = manager.CreatePrivateProcessProposal(
                doc.RootElement.GetProperty("handle").GetString()!, context.MutationRunId, context.MutationLaneId,
                intent.Intent.ToolCallId, context.MutationTurnId, context.WorkspaceRoot, context.Artifacts);
            var preview = proposalPreview is null
                ? ProcessSessionToolJson.NoPublicationPreview(state)
                : ProcessSessionToolJson.WithPublicationState(proposalPreview, state == "exited" ? "staged" : state);
            return new ToolResult(ProcessSessionToolJson.StopSummary(state, artifact is not null), preview,
                artifact, artifact is not null ? 1 : 0, artifact is not null, EffectOutcome.Applied);
        }
        catch (KeyNotFoundException) { return ToolResult.Error(ToolErrorCode.PermissionDenied, "process.handle_unavailable"); }
    }
}

internal static class ProcessSessionToolJson
{
    internal static string StartSummary(string handle, string state, string run, string lane, bool terminal)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("handle", handle); writer.WriteString("state", state);
            writer.WriteString("session", "current"); writer.WriteString("run", run);
            writer.WriteString("lane", lane); writer.WriteBoolean("terminal", terminal); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static string StopSummary(string state, bool publication)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        { writer.WriteStartObject(); writer.WriteString("state", state); writer.WriteBoolean("publication", publication); writer.WriteEndObject(); }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static string NoPublicationPreview(string state)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        { writer.WriteStartObject(); writer.WriteString("state", state); writer.WriteString("publicationState", "none"); writer.WriteEndObject(); }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static ToolDescriptor Descriptor(string id, ToolRisk risk, string description, string schema) =>
        new(new ToolId(id), description, new InputSchema(schema), ["process", "session"], false, true, risk,
            ComponentSource.Core(), ToolProtection.Protected, EffectClass.NonIdempotent);

    internal static ToolPreparation Prepare(ValidatedToolCall call, ToolDescriptor descriptor, EffectClass effect,
        ProcessClaim? processClaim = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.NormalizedArgumentsJson); var root = doc.RootElement;
            var handle = root.GetProperty("handle").GetString();
            if (root.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(handle) || handle.Length != 32
                || handle.Any(character => !Uri.IsHexDigit(character))) return new PreparationRejected("Invalid process handle", null, ToolErrorCode.InvalidArguments);
            var claims = new ResourceClaims([], [], [], processClaim ?? (descriptor.Id.ToString() is "process.read"
                ? null : new ProcessClaim("process.handle:" + handle, [], "External")), []);
            return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, effect, claims, descriptor.Risk, null));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new PreparationRejected("Invalid process request", null, ToolErrorCode.InvalidArguments); }
    }

    internal static string[] ReadPatterns(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Process path scope must be an array.");
        var items = value.EnumerateArray().Select(item => item.GetString()).ToArray();
        if (items.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 512))
            throw new InvalidDataException("Process path scope is invalid.");
        return items!;
    }

    internal static string ReadPreview(string state, int? exitCode, long after, long deliveredThrough,
        long highWater, bool gap, IReadOnlyList<ProcessOutputChunk> output, string? proposal)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("state", state);
            if (exitCode is { } code) writer.WriteNumber("exitCode", code); else writer.WriteNull("exitCode");
            writer.WriteNumber("afterSequence", after); writer.WriteNumber("deliveredThrough", deliveredThrough);
            writer.WriteNumber("highWater", highWater); writer.WriteBoolean("gap", gap);
            writer.WriteStartArray("output");
            foreach (var chunk in output)
            {
                writer.WriteStartObject(); writer.WriteNumber("sequence", chunk.Sequence);
                writer.WriteString("stream", chunk.Stream); writer.WriteString("text", chunk.Text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (proposal is not null)
            {
                using var doc = JsonDocument.Parse(proposal);
                foreach (var property in doc.RootElement.EnumerateObject())
                { writer.WritePropertyName(property.Name); property.Value.WriteTo(writer); }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static string WithPublicationState(string proposalPreview, string state)
    {
        using var source = JsonDocument.Parse(proposalPreview);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in source.RootElement.EnumerateObject())
            { writer.WritePropertyName(property.Name); property.Value.WriteTo(writer); }
            writer.WriteString("publicationState", state);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
