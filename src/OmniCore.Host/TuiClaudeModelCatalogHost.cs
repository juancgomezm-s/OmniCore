namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Models;
using OmniCore.Abstractions;

/// <summary>Explicit, cancellable model discovery for an existing Claude account. No secrets cross this boundary.</summary>
public interface ITuiClaudeModelCatalogHost
{
    Task<TuiClaudeModelCatalogOutcome> DiscoverModelsAsync(string providerId, CancellationToken cancellationToken);
}

public sealed record TuiClaudeModelCatalogOutcome(bool Success, int AvailableModels = 0,
    int RegisteredModels = 0, bool Truncated = false, string? ErrorKey = null);

public sealed partial class TuiClaudeAccountHost : ITuiClaudeModelCatalogHost
{
    public async Task<TuiClaudeModelCatalogOutcome> DiscoverModelsAsync(string providerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var providers = File.ReadAllText(Path.Combine(_paths.ConfigDirectory, "providers.yaml"));
            var provider = new ConfigLoader().Load(providers, "models: {}\n").Registry.Provider(providerId);
            if (provider is not { Family: ProviderFamily.AnthropicMessages, Auth.Kind: AuthKind.OAuth })
                return new(false, ErrorKey: "notConfigured");
            var session = _sessionFactory(providerId);
            if (session is null) return new(false, ErrorKey: "notConfigured");
            var bearer = await session.Auth.GetAsync(token).ConfigureAwait(false);
            if (bearer is null) return new(false, ErrorKey: "invalid");
            var catalog = new AnthropicModelCatalog(session.HttpFactory);
            var (models, truncated, _) = await catalog.ListOAuthAsync(bearer, provider.BaseUrl,
                session.Identity.UserAgentTemplate.Replace("{version}",
                    typeof(OmniHost).Assembly.GetName().Version?.ToString() ?? "0.0.0"), token).ConfigureAwait(false);
            void ValidateSession()
            {
                token.ThrowIfCancellationRequested();
                var current = session.Credentials.Load(session.SecretRef, token);
                if (current is null || current.AccessToken != bearer.AccessToken || session.Refresh.IsDead(current.RefreshToken))
                    throw new ProviderConnectionException("changed", "La cuenta cambió durante el descubrimiento.");
            }
            var added = ProviderConnectionRegistration.RegisterAnthropicModels(_paths.ConfigDirectory, providerId,
                models, provider.BaseUrl, truncated, AuthKind.OAuth, providers, ValidateSession);
            return new(true, models.Count, added, truncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, ErrorKey: "unreachable"); }
        catch (ProviderConnectionException error) { return new(false, ErrorKey: error.Kind); }
        catch (Exception) { return new(false, ErrorKey: "failed"); }
    }
}
