namespace OmniCore.Host;

/// <summary>Account operations for the TUI. Status and progress never contain stored tokens.</summary>
public interface ITuiAccountHost
{
    ChatGptSessionStatus Status();
    Task<ChatGptSessionStatus> LoginAsync(bool deviceCode, Action<string> progress, CancellationToken cancellationToken);
    void Logout();
    Task<IReadOnlyList<AvailableChatGptModel>> ListModelsAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<AvailableChatGptModel>>(new NotSupportedException("Model discovery unavailable."));
}

public sealed class TuiAccountHost : ITuiAccountHost
{
    private readonly ChatGptSubscriptionAuthProvider _auth;
    public TuiAccountHost() => _auth = OmniHost.CreateChatGptAuth(OmniHost.CreatePlatformPaths());
    public ChatGptSessionStatus Status() => _auth.Status(CancellationToken.None);
    public Task<ChatGptSessionStatus> LoginAsync(bool deviceCode, Action<string> progress, CancellationToken cancellationToken) =>
        deviceCode
            ? _auth.LoginWithDeviceCodeAsync((url, code) => progress(url + "\n" + code), cancellationToken)
            : _auth.LoginWithBrowserAsync(progress, cancellationToken);
    public void Logout() => _auth.Logout(CancellationToken.None);
    public Task<IReadOnlyList<AvailableChatGptModel>> ListModelsAsync(CancellationToken cancellationToken) =>
        new ChatGptModelCatalog(_auth).ListAsync(cancellationToken);
    public Task<IReadOnlyList<AvailableChatGptModel>> ListModelsAsync(CancellationToken cancellationToken, Action<string>? diagnostics) =>
        new ChatGptModelCatalog(_auth, diagnostics: diagnostics).ListAsync(cancellationToken);
}
