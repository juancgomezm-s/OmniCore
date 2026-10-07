namespace OmniCore.Host;

using OmniCore.Domain;

/// <summary>Production turns behind the Host boundary; the TUI never composes providers or credentials.</summary>
public interface ITuiTurnHost
{
    Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken);
    Task<int> ExecuteActAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken) => ExecuteAsync(input, diagnostics, cancellationToken);
    Task<int> ResumeEscalationAsync(string interactionId, Action<string> diagnostics, CancellationToken cancellationToken) =>
        System.Threading.Tasks.Task.FromResult(1);
    bool HasEscalationForInteraction(string interactionId) => false;
    Task<int> ResumeQuotaAsync(string interactionId, Action<string> diagnostics, CancellationToken cancellationToken) =>
        System.Threading.Tasks.Task.FromResult(1);
    bool SetNextTurnReasoningBoost(ReasoningRequest request) => false;
}

public sealed class TuiTurnHost : ITuiTurnHost
{
    private readonly OmniCliRuntime _runtime;
    private readonly object _boostGate = new();
    private sealed record PendingTurnBoost(Guid Id, ReasoningRequest Request);
    private PendingTurnBoost? _nextTurnReasoningBoost;
    public TuiTurnHost(OmniCliRuntime runtime)
    {
        _runtime = runtime;
        _runtime.UseConsoleInput = false;
        _runtime.HasInteractionClient = true;
        _runtime.QuestionnaireInput = null;
    }
    public Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken)
    {
        var boost = PeekBoost();
        return _runtime.ConversationAsync(input, diagnostics, cancellationToken, boost?.Request, boost?.Id,
            boost is null ? null : CompleteBoost);
    }

    public Task<int> ExecuteActAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken)
    {
        var boost = PeekBoost();
        return _runtime.ActAsync(input, diagnostics, cancellationToken, boost?.Request, boost?.Id,
            boost is null ? null : CompleteBoost);
    }
    public Task<int> ResumeEscalationAsync(string interactionId, Action<string> diagnostics, CancellationToken cancellationToken) =>
        _runtime.ResumeEscalationAsync(new OmniCore.Domain.InteractionId(Guid.Parse(interactionId)), diagnostics, cancellationToken);
    public bool HasEscalationForInteraction(string interactionId) =>
        _runtime.HasEscalationForInteraction(new OmniCore.Domain.InteractionId(Guid.Parse(interactionId)));
    public Task<int> ResumeQuotaAsync(string interactionId, Action<string> diagnostics, CancellationToken cancellationToken) =>
        _runtime.ResumeQuotaAsync(new OmniCore.Domain.InteractionId(Guid.Parse(interactionId)), diagnostics, cancellationToken);

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

    private PendingTurnBoost? PeekBoost()
    {
        lock (_boostGate) return _nextTurnReasoningBoost;
    }

    private void CompleteBoost(Guid id)
    {
        lock (_boostGate)
            if (_nextTurnReasoningBoost?.Id == id) _nextTurnReasoningBoost = null;
    }
}
