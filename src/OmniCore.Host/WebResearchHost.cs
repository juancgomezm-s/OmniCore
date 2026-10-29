namespace OmniCore.Host;

using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Infrastructure;

/// <summary>User-only opt-in configuration. Loading it does not resolve or create any credential.</summary>
public sealed class WebResearchConfiguration
{
    public bool Enabled { get; set; }
    public string Preset { get; set; } = "normal";
    public WebResearchLimits? Limits { get; set; }
    public string? PrimaryProvider { get; set; }
    public string? FallbackProvider { get; set; }
    public string[] FallbackErrors { get; set; } = ["provider_unavailable", "rate_limited", "credentials_unavailable"];
    public int Retries { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
    public Dictionary<string, string> CredentialEnvironmentVariables { get; set; } = [];
}

internal static class WebResearchHost
{
    internal static WebResearchContextGuard ContextGuard(string workspaceData, WebResearchController? controller)
    {
        if (controller is not null) return new(controller.Store);
        var existing = Path.Combine(workspaceData, "web-research.db");
        // Removing configuration cannot authorize the replay of already journalled web evidence.
        return new(File.Exists(existing) ? new SqliteWebResearchStore(existing) : null);
    }

    internal static WebResearchController? Create(IPlatformPaths paths, string workspaceData, string runId, string question)
    {
        var path = Path.Combine(paths.ConfigDirectory, "web-research.json");
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024) throw new InvalidDataException("Web research configuration exceeds its bound.");
        var configuration = JsonSerializer.Deserialize(File.ReadAllText(path), WebResearchConfigurationJson.Default.WebResearchConfiguration)
            ?? throw new InvalidDataException("Web research configuration is invalid.");
        if (!configuration.Enabled) return null;
        var limits = configuration.Limits ?? configuration.Preset switch
        {
            "point" => WebResearchLimits.Point, "normal" => WebResearchLimits.Normal, "broad" => WebResearchLimits.Broad,
            _ => throw new InvalidDataException("Unknown web research preset."),
        };
        limits.Validate();
        if (configuration.AllowedOrigins is null || configuration.CredentialEnvironmentVariables is null || configuration.FallbackErrors is null
            || configuration.AllowedOrigins.Length > 32 || configuration.CredentialEnvironmentVariables.Count > 3
            || configuration.Retries is < 0 or > 2 || configuration.FallbackErrors.Length > 8)
            throw new InvalidDataException("Web research configuration exceeds bounded limits.");
        IWebSearchProvider? Provider(string? id)
        {
            if (id is null) return null;
            string? Key()
            {
                if (!configuration.CredentialEnvironmentVariables.TryGetValue(id, out var variable)) return null;
                if (variable.Length is < 1 or > 128 || variable.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                    throw new InvalidDataException("Credential environment reference is invalid.");
                return Environment.GetEnvironmentVariable(variable);
            }
            return id switch
            {
                "brave" => new BraveWebSearchProvider(Key), "tavily" => new TavilyWebSearchProvider(Key), "exa" => new ExaWebSearchProvider(Key),
                _ => throw new InvalidDataException("Unknown configured web search provider."),
            };
        }
        return new(new SqliteWebResearchStore(Path.Combine(workspaceData, "web-research.db")), runId,
            [new("q1", question)], limits, new WebReadTransport(), configuration.AllowedOrigins,
            Provider(configuration.PrimaryProvider), Provider(configuration.FallbackProvider), configuration.FallbackErrors, configuration.Retries);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WebResearchConfiguration))]
internal partial class WebResearchConfigurationJson : JsonSerializerContext;
