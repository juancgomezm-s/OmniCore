namespace OmniCore.Host;

using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using OmniCore.Abstractions;
using OmniCore.Models;

/// <summary>
/// Carga providers.yaml / models.yaml de M2 (ADR-0011 §2): el formato YAML se lee con
/// YamlDotNet (verificado en este runtime) y produce el ModelRegistry mínimo. Solo scope
/// User: un repo nunca redefine providers (ADR-0039 §4).
/// </summary>
public sealed class ConfigLoader
{
    /// <summary>Construye el registro mínimo a partir del YAML (uno por defecto si falta).</summary>
    public ModelRegistry BuildRegistry(string? providersYaml, string? modelsYaml)
    {
        var registry = new ModelRegistry();
        var configured = false;
        if (providersYaml is not null && providersYaml!.Trim().Length > 0)
        {
            configured |= ApplyProviders(registry, providersYaml!, modelsYaml);
        }

        if (!configured)
        {
            // Registro mínimo: un provider local y un modelo por defecto (ADR-0011 §2).
            registry.Add(DefaultLocalProvider());
            registry.AddModel(new ModelDefinition("local-worker", "local", 8192, 8192, 2048));
        }

        return registry;
    }

    private static bool ApplyProviders(ModelRegistry registry, string providersYaml, string? modelsYaml)
    {
        var des = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var root = des.Deserialize<Dictionary<string, object>>(providersYaml);
        if (root is null || !root.ContainsKey("providers"))
        {
            return false;
        }

        var container = (Dictionary<object, object>) Convert(root["providers"]!);
        foreach (var kv in container)
        {
            var id = kv.Key!.ToString()!;
            var values = (Dictionary<object, object>) Convert(kv.Value!);
            var baseUrl = Str(values, "baseUrl") ?? "";
            if (baseUrl.Length == 0)
            {
                baseUrl = "http://127.0.0.1:8080";
            }

            var auth = ParseAuth(values, id);
            var family = Str(values, "family") == "AnthropicMessages"
                ? OmniCore.Domain.ProviderFamily.AnthropicMessages
                : Str(values, "family") == "OpenAIResponses"
                    ? OmniCore.Domain.ProviderFamily.OpenAIResponses
                    : OmniCore.Domain.ProviderFamily.OpenAiChatCompatible;
            registry.Add(new ProviderDescriptor(id, family, baseUrl, auth, true, true, true)
            {
                TrustedCertificatePath = Str(values, "caCertificate"),
            });
        }

        if (modelsYaml is not null && modelsYaml!.Trim().Length > 0)
        {
            var mdes = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            var mroot = mdes.Deserialize<Dictionary<string, object>>(modelsYaml!);
            if (mroot is not null && mroot.ContainsKey("models"))
            {
                var container2 = (Dictionary<object, object>) Convert(mroot["models"]!);
                foreach (var kv in container2)
                {
                    var id = kv.Key!.ToString()!;
                    var values = (Dictionary<object, object>) Convert(kv.Value!);
                    var provider = Str(values, "provider");
                    if (provider is null) continue;
                    // context/maxOutput se respetan ahora (no se ignoran: el default es 8192).
                    var context = Long(values, "context", 8192);
                    var usable = Long(values, "recommendedUsableContext", context);
                    var maxOut = Long(values, "maxOutput", 2048);
                    registry.AddModel(new ModelDefinition(id, provider!, context, usable, maxOut,
                        Double(values, "parametersBillions")));
                }
            }
        }

        return true;
    }

    private static ProviderDescriptor DefaultLocalProvider() =>
        new ProviderDescriptor("local", OmniCore.Domain.ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1:8080", AuthConfig.None(), true, true, true);

    private static Dictionary<object, object> Convert(object value) =>
        value is Dictionary<object, object> dd ? dd : new Dictionary<object, object>();

    private static string? Str(Dictionary<object, object> map, string key)
    {
        foreach (var kv in map)
        {
            if (kv.Key!.ToString() == key || Normalize(kv.Key!.ToString()!) == Normalize(key))
            {
                return kv.Value is null ? null : kv.Value!.ToString();
            }
        }

        return null;
    }

    private static string Normalize(string s) => s.Replace("-", "").Replace("_", "")?.ToLowerInvariant() ?? "";

    /// <summary>
    /// Interpreta auth de un provider: `auth: none` → sin autenticación; `auth: "ref"` →
    /// ApiKey(ref); `authRef: "ref"` → ApiKey(ref); `auth: {apiKey: "ref"}` → ApiKey(ref).
    /// Cualquier otro mapa (p. ej. con secretToSelf) se traduce a ApiKey con un ref derivado.
    /// </summary>
    private static AuthConfig ParseAuth(Dictionary<object, object> values, string providerId)
    {
        var hasAuth = HasKey(values, "auth");
        var hasAuthRef = HasKey(values, "authRef");
        if (!hasAuth && !hasAuthRef)
        {
            return AuthConfig.None();
        }

        if (hasAuthRef)
        {
            return AuthConfig.ApiKey(Str(values, "authRef") ?? providerId);
        }

        var authVal = Raw(values, "auth");
        if (authVal is null)
        {
            return AuthConfig.None();
        }

        if (authVal! is string)
        {
            var s = authVal!.ToString()!;
            // "none" explícito → sin auth; si no, es la ref de la key.
            return s.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? AuthConfig.None()
                : AuthConfig.ApiKey(s);
        }

        if (authVal! is Boolean)
        {
            return (Boolean) authVal! ? AuthConfig.ApiKey(providerId) : AuthConfig.None();
        }

        // dict: {apiKey: ref} o {kind: none, ...}
        var authMap = (Dictionary<object, object>) Convert(authVal!);
        if (Raw(authMap, "apiKey") is not null)
        {
            return AuthConfig.ApiKey(Str(authMap, "apiKey") ?? providerId);
        }

        return AuthConfig.None();
    }

    private static bool HasKey(Dictionary<object, object> map, string key)
    {
        foreach (var kv in map)
        {
            if (kv.Key!.ToString() == key || Normalize(kv.Key!.ToString()!) == Normalize(key))
            {
                return true;
            }
        }

        return false;
    }

    private static object? Raw(Dictionary<object, object> map, string key)
    {
        foreach (var kv in map)
        {
            if (kv.Key!.ToString() == key || Normalize(kv.Key!.ToString()!) == Normalize(key))
            {
                return kv.Value;
            }
        }

        return null;
    }

    private static long Long(Dictionary<object, object> map, string key, long fallback)
    {
        var raw = Raw(map, key);
        if (raw is null) return fallback;
        if (raw! is long) return (long) raw!;
        if (raw! is int) return (int) raw!;
        var s = raw!.ToString()!;
        return long.TryParse(s.Trim(), out var n) ? n : fallback;
    }

    private static double? Double(Dictionary<object, object> map, string key)
    {
        var raw = Raw(map, key);
        return raw is not null && double.TryParse(raw.ToString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : null;
    }
}
