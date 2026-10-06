namespace OmniCore.Host;

/// <summary>Production turns behind the Host boundary; the TUI never composes providers or credentials.</summary>
public interface ITuiTurnHost
{
    Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken);
    Task<int> ExecuteActAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken) => ExecuteAsync(input, diagnostics, cancellationToken);
}

public sealed class TuiTurnHost : ITuiTurnHost
{
    private readonly OmniCliRuntime _runtime;
    public TuiTurnHost(OmniCliRuntime runtime)
    {
        _runtime = runtime;
        _runtime.UseConsoleInput = false;
        _runtime.QuestionnaireInput = null;
    }
    public Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken) =>
        _runtime.ConversationAsync(input, diagnostics, cancellationToken);
    public Task<int> ExecuteActAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken) =>
        _runtime.ActAsync(input, diagnostics, cancellationToken);
}
