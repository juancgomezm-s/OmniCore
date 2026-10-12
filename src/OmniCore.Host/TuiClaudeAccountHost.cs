namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

/// <summary>Local status and explicit OAuth actions for configured Claude providers.</summary>
public interface ITuiClaudeAccountHost
{
    IReadOnlyList<TuiClaudeAccountRow> List(CancellationToken cancellationToken);
    Task<TuiClaudeLoginOutcome> LoginAsync(string providerId, Action<TuiClaudeLoginProgress> progress,
        CancellationToken cancellationToken);
    void Logout(string providerId, CancellationToken cancellationToken);
}

/// <summary>No token material crosses this account-menu boundary.</summary>
public sealed record TuiClaudeAccountRow(string ProviderId, bool Configured, bool SignedIn, bool Expired, bool Dead,
    string? AccountHint, string? SubscriptionType, string? RateLimitTier,
    DateTimeOffset? ExpiresAt, string? UnavailableReasonKey)
{
    public bool CanLogin => Configured;
    public bool CanLogout => Configured && SignedIn;
}

public sealed record TuiClaudeLoginProgress(string Phase, string? MessageKey = null);
public sealed record TuiClaudeLoginOutcome(bool Success, string? ErrorKey = null,
    string? AccountHint = null, string? SubscriptionType = null);

/// <summary>
/// Presents only the OAuth identity declared by a User-scope Anthropic provider. Status inspection
/// is local; it deliberately does not call the auth provider's token-refresh path.
/// </summary>
public sealed class TuiClaudeAccountHost : ITuiClaudeAccountHost
{
    private readonly IPlatformPaths _paths;
    private readonly Func<string, OmniHost.ClaudeOAuthSession?> _sessionFactory;
    private readonly Func<DateTimeOffset> _utcNow;

    // Keep the renderer's binary boundary limited to Host/Client/Protocol.
    public static ITuiClaudeAccountHost Create() => new TuiClaudeAccountHost();

    public TuiClaudeAccountHost(IPlatformPaths? paths = null,
        Func<string, OmniHost.ClaudeOAuthSession?>? sessionFactory = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _paths = paths ?? OmniHost.CreatePlatformPaths();
        _sessionFactory = sessionFactory ?? (id => OmniHost.CreateClaudeOAuthSession(_paths, id));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<TuiClaudeAccountRow> List(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        var providers = loaded.Registry.Providers()
            .Where(provider => provider.Family == ProviderFamily.AnthropicMessages
                && provider.Auth.Kind == AuthKind.OAuth)
            .OrderBy(provider => provider.Id, StringComparer.Ordinal)
            .ToArray();
        var rows = new List<TuiClaudeAccountRow>(providers.Length);
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var session = _sessionFactory(provider.Id);
                if (session is null)
                {
                    rows.Add(new(provider.Id, false, false, false, false, null, null, null, null,
                        "claude.account.identityUnavailable"));
                    continue;
                }

                var status = session.Auth.InspectAsync(cancellationToken).GetAwaiter().GetResult();
                var expired = status.ExpiresAt is { } expiry && expiry <= _utcNow();
                rows.Add(new(provider.Id, true, status.SignedIn, expired, status.Dead, MaskIdentity(status.EmailAddress),
                    status.SubscriptionType, status.RateLimitTier, status.ExpiresAt, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                rows.Add(new(provider.Id, false, false, false, false, null, null, null, null,
                    "claude.account.statusUnavailable"));
            }
        }
        return rows;
    }

    public async Task<TuiClaudeLoginOutcome> LoginAsync(string providerId,
        Action<TuiClaudeLoginProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        var session = _sessionFactory(providerId);
        if (session is null)
            return new(false, "claude.account.identityUnavailable");

        var login = session.CreateLogin(value => progress(new(value.Phase.ToString(), value.MessageKey)));
        try
        {
            var result = await login.LoginAsync(session.SecretRef, new ClaudeOAuthLoginOptions(), cancellationToken)
                .ConfigureAwait(false);
            return new(result.Success, result.Failure is { } failure ? FailureKey(failure) : null,
                MaskIdentity(result.EmailAddress), result.SubscriptionType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(false, "claude.oauth.failed.protocol");
        }
    }

    public void Logout(string providerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        cancellationToken.ThrowIfCancellationRequested();
        var session = _sessionFactory(providerId);
        if (session is null) throw new InvalidOperationException("claude.account.identityUnavailable");
        session.Auth.SignOut(cancellationToken);
    }

    private static string? MaskIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var at = value.IndexOf('@');
        return at > 0 ? value[0] + "***@***" : value[0] + "***";
    }

    private static string FailureKey(ClaudeOAuthLoginFailure failure) => failure switch
    {
        ClaudeOAuthLoginFailure.Cancelled => "claude.oauth.failed.cancelled",
        ClaudeOAuthLoginFailure.TimedOut => "claude.oauth.failed.timeout",
        ClaudeOAuthLoginFailure.TransportUnavailable => "claude.oauth.failed.transport",
        ClaudeOAuthLoginFailure.BrowserUnavailable => "claude.oauth.failed.browser",
        ClaudeOAuthLoginFailure.CallbackRejected => "claude.oauth.failed.callback",
        ClaudeOAuthLoginFailure.ExchangeRejected => "claude.oauth.failed.exchange",
        ClaudeOAuthLoginFailure.Network => "claude.oauth.failed.network",
        ClaudeOAuthLoginFailure.Protocol => "claude.oauth.failed.protocol",
        ClaudeOAuthLoginFailure.Storage => "claude.oauth.failed.storage",
        _ => "claude.oauth.failed.protocol",
    };
}
