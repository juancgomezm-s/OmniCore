using System.Text.Json;
using OmniCore.Host;

// Offline inspection only. Never returns tokens, account identifiers or raw configuration.
try
{
    var paths = OmniHost.CreatePlatformPaths();
    var configuration = OmniHost.LoadUserConfiguration(paths);
    var auth = OmniHost.CreateChatGptAuth(paths).Status(CancellationToken.None);
    var models = new[] { "gpt-6-luna", "qwen38-27b-abl-v9-best-q4kxl" }.Select(id =>
    {
        var model = configuration.Registry.Model(id);
        var provider = model is null ? null : configuration.Registry.Provider(model.ProviderId);
        var endpoint = provider is not null && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path) : null;
        return new
        {
            Model = id, Present = model is not null, Provider = provider?.Id,
            Family = provider?.Family.ToString(), provider?.Profile,
            Billing = provider?.BillingMode.ToString(), Endpoint = endpoint,
            LocalHost = provider?.LocalHost?.Mode.ToString(),
            provider?.SupportsNativeToolCalls, model?.MaxOutputTokens,
            model?.ContextWindow,
        };
    }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Utc = DateTimeOffset.UtcNow,
        SubscriptionPresent = auth.LoggedIn,
        SubscriptionExpired = auth.Expired,
        SubscriptionValidForMinutes = auth.ExpiresAt is { } expires
            ? (double?)Math.Floor((expires - DateTimeOffset.UtcNow).TotalMinutes) : null,
        Models = models,
        ExternalCalls = 0,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { PreflightFailed = true, ErrorType = error.GetType().Name }));
    return 1;
}
