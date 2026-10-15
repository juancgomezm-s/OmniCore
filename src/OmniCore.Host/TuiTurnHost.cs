namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Protocol;

/// <summary>Opaque attachment handle exposed to the TUI without leaking journal/domain types across the Host boundary.</summary>
public sealed class TuiAttachmentReference
{
    internal ArtifactRef Artifact { get; }
    public string? FileName => Artifact.FileName;
    public long Size => Artifact.Size;

    internal TuiAttachmentReference(ArtifactRef artifact) => Artifact = artifact;
}

/// <summary>Production turns behind the Host boundary; the TUI never composes providers or credentials.</summary>
public interface ITuiTurnHost
{
    UsageSnapshot? CurrentUsage() => null;
    Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken);
    TuiAttachmentReference ImportAttachment(byte[] bytes, string fileName) => throw new NotSupportedException("Attachments are not supported by this turn host.");
    TuiAttachmentReference ImportAttachmentFromPath(string path) => throw new NotSupportedException("Path attachments are not supported by this turn host.");
    Task<int> ExecuteAsync(string input, IReadOnlyList<TuiAttachmentReference> attachments, Action<string> diagnostics,
        CancellationToken cancellationToken) => attachments.Count == 0
        ? ExecuteAsync(input, diagnostics, cancellationToken)
        : System.Threading.Tasks.Task.FromResult(1);
    Task<int> ExecuteDelegationAsync(string delegationId, Action<string> diagnostics, CancellationToken cancellationToken) =>
        System.Threading.Tasks.Task.FromResult(1);
    Task<int> ExecuteWorkflowAsync(WorkflowRequestDto request, string commandId, Action<string> diagnostics,
        CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(1);
    Task<int> ExecuteExtensionCommandAsync(string prompt, string toolName, Action<string> diagnostics,
        CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(1);
    Task<int> ExecuteActAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken) => ExecuteAsync(input, diagnostics, cancellationToken);
    Task<int> ResumeEscalationAsync(string interactionId, Action<string> diagnostics, CancellationToken cancellationToken) =>
        System.Threading.Tasks.Task.FromResult(1);
    bool HasEscalationForInteraction(string interactionId) => false;
    Task<int> ResumeQuotaAsync(string interactionId, Action<string> diagnostics, CancellationToken cancellationToken) =>
        System.Threading.Tasks.Task.FromResult(1);
    bool SetNextTurnReasoningBoost(string kind, int? budgetTokens) => false;
}

public sealed class TuiTurnHost : ITuiTurnHost
{
    private readonly OmniCliRuntime _runtime;
    public UsageSnapshot? CurrentUsage() => _runtime.CurrentUsage();
    public TuiAttachmentReference ImportAttachment(byte[] bytes, string fileName) => new(_runtime.ImportTuiAttachment(bytes, fileName));
    public TuiAttachmentReference ImportAttachmentFromPath(string path) => new(_runtime.ImportTuiAttachmentFromPath(path));
    private readonly object _boostGate = new();
    private sealed record PendingTurnBoost(Guid Id, ReasoningRequest Request);
    private PendingTurnBoost? _nextTurnReasoningBoost;
    private Guid? _reservedBoostId;
    private TaskCompletionSource<bool>? _boostDecision;
    public TuiTurnHost(OmniCliRuntime runtime)
    {
        _runtime = runtime;
        _runtime.UseConsoleInput = false;
        _runtime.HasInteractionClient = true;
        _runtime.QuestionnaireInput = null;
    }
    public Task<int> ExecuteDelegationAsync(string delegationId, Action<string> diagnostics, CancellationToken token) =>
        _runtime.DelegationAsync(delegationId, diagnostics, token);
    public Task<int> ExecuteWorkflowAsync(WorkflowRequestDto request, string commandId, Action<string> diagnostics,
        CancellationToken token) => _runtime.ExecuteWorkflowAsync(request, commandId, diagnostics, token);
    public Task<int> ExecuteExtensionCommandAsync(string prompt, string toolName, Action<string> diagnostics,
        CancellationToken token) => _runtime.ExecuteExtensionCommandAsync(prompt, toolName, diagnostics, token);
    public async Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken)
        => await ExecuteAsync(input, Array.Empty<TuiAttachmentReference>(), diagnostics, cancellationToken).ConfigureAwait(false);

    public async Task<int> ExecuteAsync(string input, IReadOnlyList<TuiAttachmentReference> attachments,
        Action<string> diagnostics, CancellationToken cancellationToken)
    {
        var boost = await ReserveBoostAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _runtime.ConversationAsync(input, diagnostics, cancellationToken, boost?.Request, boost?.Id,
                boost is null ? null : CompleteBoost, attachments.Select(item => item.Artifact).ToArray()).ConfigureAwait(false);
        }
        finally { ReleaseBoostReservation(boost); }
    }

    public async Task<int> ExecuteActAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken)
    {
        var boost = await ReserveBoostAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _runtime.ActAsync(input, diagnostics, cancellationToken, boost?.Request, boost?.Id,
                boost is null ? null : CompleteBoost).ConfigureAwait(false);
        }
        finally { ReleaseBoostReservation(boost); }
    }
    public Task<int> ResumeEscalationAsync(string interactionId, Action<string> diagnostics, CancellationToken cancellationToken) =>
        _runtime.ResumeEscalationAsync(new OmniCore.Domain.InteractionId(Guid.Parse(interactionId)), diagnostics, cancellationToken);
    public bool HasEscalationForInteraction(string interactionId) =>
        _runtime.HasEscalationForInteraction(new OmniCore.Domain.InteractionId(Guid.Parse(interactionId)));
    public Task<int> ResumeQuotaAsync(string interactionId, Action<string> diagnostics, CancellationToken cancellationToken) =>
        _runtime.ResumeQuotaAsync(new OmniCore.Domain.InteractionId(Guid.Parse(interactionId)), diagnostics, cancellationToken);

    public bool SetNextTurnReasoningBoost(string kind, int? budgetTokens) =>
        SetNextTurnReasoningBoost(new ReasoningRequest(kind, budgetTokens));

    /// <summary>Host-internal compatibility overload; the renderer uses protocol-neutral scalar fields.</summary>
    public bool SetNextTurnReasoningBoost(ReasoningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Kind)
            || (request.Kind == "budget" ? request.BudgetTokens is null or < 1024 : request.BudgetTokens is not null))
            throw new ArgumentException("A reasoning boost must be a valid provider-neutral request.", nameof(request));
        lock (_boostGate)
        {
            if (_nextTurnReasoningBoost is not null) return false;
            _nextTurnReasoningBoost = new PendingTurnBoost(Guid.NewGuid(), request);
            return true;
        }
    }

    private async Task<PendingTurnBoost?> ReserveBoostAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            System.Threading.Tasks.Task? waitForDecision = null;
            lock (_boostGate)
            {
                if (_nextTurnReasoningBoost is null) return null;
                if (_reservedBoostId is null)
                {
                    _reservedBoostId = _nextTurnReasoningBoost.Id;
                    _boostDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _nextTurnReasoningBoost;
                }

                waitForDecision = _boostDecision?.Task
                    ?? throw new InvalidOperationException("A reserved reasoning boost has no completion signal.");
            }

            await waitForDecision.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void CompleteBoost(Guid id)
    {
        TaskCompletionSource<bool>? decision = null;
        lock (_boostGate)
        {
            if (_nextTurnReasoningBoost?.Id != id || _reservedBoostId != id) return;
            _nextTurnReasoningBoost = null;
            _reservedBoostId = null;
            decision = _boostDecision;
            _boostDecision = null;
        }

        decision?.TrySetResult(true);
    }

    private void ReleaseBoostReservation(PendingTurnBoost? boost)
    {
        if (boost is null) return;
        TaskCompletionSource<bool>? decision = null;
        lock (_boostGate)
        {
            if (_reservedBoostId != boost.Id) return;
            _reservedBoostId = null;
            decision = _boostDecision;
            _boostDecision = null;
        }

        decision?.TrySetResult(true);
    }
}
