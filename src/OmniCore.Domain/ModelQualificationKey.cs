namespace OmniCore.Domain;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

/// <summary>
/// Identidad exacta de una configuración de modelo para cualificación empírica (ADR-0007 §5).
/// Es más específica que la ModelPolicyKey (ADR-0044 §2): además de los mismos campos de
/// configuración incluye ToolCallFormat y ToolMode, porque la cualificación mide el
/// comportamiento concreto de esa combinación. Un cambio en cualquier campo relevante produce
/// una clave nueva y exige una cualificación nueva (ADR-0007 §4).
///
/// Serialización canónica: JSON con orden fijo de campos (JsonSerializer con propiedad
/// explicitada + JsonWriter), UTF-8, sin dependencias de cultura ni de plataforma. Null vs
/// cadena vacía se distingue explícito en el JSON (null vs "").
///
/// Adapters: la clave de cualificación NO colapsa dos órdenes de adapters sin evidencia de
/// conmutatividad. La aplicación de adapters (LoRA, fine-tunes) puede depender del orden en
/// que se aplican, y el hash debe reflejar la configuración concreta. Se preserva el orden y
/// la multiplicidad como opción conservadora (ADR-0007 §5). Los adapters null o entradas
/// null/vacías se rechazan de forma explícita en vez de omitirse en silencio.
///
/// Clave canónica: SHA-256 de la serialización canónica, en hexadecimal minúsculo (64 caracteres).
/// </summary>
public sealed class ModelQualificationKey
{
    public string ProviderId { get; }

    public string ModelId { get; }

    public string? ModelRevision { get; }

    public string? Quantization { get; }

    public IReadOnlyList<string> Adapters { get; }

    public string? Backend { get; }

    public string? BackendBuild { get; }

    public string? ChatTemplateHash { get; }

    public string AdapterProfile { get; }

    public ToolCallFormat ToolCallFormat { get; }

    public ToolMode ToolMode { get; }

    public string PromptProfileVersion { get; }

    public string? Endpoint { get; }

    public string? Protocol { get; }

    public string? RuntimeBuild { get; }

    public ModelQualificationKey(string providerId, string modelId, string? modelRevision, string? quantization,
        IReadOnlyList<string> adapters, string? backend, string? backendBuild, string? chatTemplateHash,
        string adapterProfile, ToolCallFormat toolCallFormat, ToolMode toolMode, string promptProfileVersion,
        string? endpoint = null, string? protocol = null, string? runtimeBuild = null)
    {
        if (providerId is null || providerId!.Length == 0)
        {
            throw new ArgumentException("ProviderId es obligatorio", nameof(providerId));
        }

        if (modelId is null || modelId!.Length == 0)
        {
            throw new ArgumentException("ModelId es obligatorio", nameof(modelId));
        }

        if (adapterProfile is null || adapterProfile!.Length == 0)
        {
            throw new ArgumentException("AdapterProfile es obligatorio", nameof(adapterProfile));
        }

        if (promptProfileVersion is null || promptProfileVersion!.Length == 0)
        {
            throw new ArgumentException("PromptProfileVersion es obligatorio", nameof(promptProfileVersion));
        }

        if (adapters is null)
        {
            throw new ArgumentNullException(nameof(adapters), "Adapters es obligatorio (Array.Empty<string>() si no hay adapters)");
        }

        var adapterSnapshot = new List<string>(adapters.Count);
        foreach (var a in adapters)
        {
            if (a is null || a!.Length == 0)
            {
                throw new ArgumentException("Adapters no puede contener entradas null o vacías", nameof(adapters));
            }

            adapterSnapshot.Add(a);
        }

        Adapters = adapterSnapshot.ToImmutableArray();
        ProviderId = providerId;
        ModelId = modelId;
        ModelRevision = modelRevision;
        Quantization = quantization;
        Backend = backend;
        BackendBuild = backendBuild;
        ChatTemplateHash = chatTemplateHash;
        AdapterProfile = adapterProfile;
        ToolCallFormat = toolCallFormat;
        ToolMode = toolMode;
        PromptProfileVersion = promptProfileVersion;
        Endpoint = endpoint;
        Protocol = protocol;
        RuntimeBuild = runtimeBuild;
    }

    /// <summary>Clave para una configuración local sin metadatos finos.</summary>
    public static ModelQualificationKey For(string providerId, string modelId,
        ToolCallFormat toolCallFormat, ToolMode toolMode, string? endpoint = null,
        string? protocol = null, string? runtimeBuild = null)
        => new(providerId, modelId, null, null, Array.Empty<string>(), null, null, null,
            "default", toolCallFormat, toolMode, "v1", endpoint, protocol, runtimeBuild);

    /// <summary>JSON canónico determinista (orden fijo de campos; null explícito; adapters en orden de entrada).</summary>
    public string CanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("providerId", ProviderId);
            writer.WriteString("modelId", ModelId);
            if (ModelRevision is null)
            {
                writer.WriteNull("modelRevision");
            }
            else
            {
                writer.WriteString("modelRevision", ModelRevision);
            }

            if (Quantization is null)
            {
                writer.WriteNull("quantization");
            }
            else
            {
                writer.WriteString("quantization", Quantization);
            }

            writer.WriteStartArray("adapters");
            foreach (var adapter in Adapters)
            {
                writer.WriteStringValue(adapter);
            }

            writer.WriteEndArray();
            if (Backend is null)
            {
                writer.WriteNull("backend");
            }
            else
            {
                writer.WriteString("backend", Backend);
            }

            if (BackendBuild is null)
            {
                writer.WriteNull("backendBuild");
            }
            else
            {
                writer.WriteString("backendBuild", BackendBuild);
            }

            if (ChatTemplateHash is null)
            {
                writer.WriteNull("chatTemplateHash");
            }
            else
            {
                writer.WriteString("chatTemplateHash", ChatTemplateHash);
            }

            writer.WriteString("adapterProfile", AdapterProfile);
            writer.WriteString("toolCallFormat", ToolCallFormat.ToString());
            writer.WriteString("toolMode", ToolMode.ToString());
            writer.WriteString("promptProfileVersion", PromptProfileVersion);
            // Preserve byte-for-byte legacy JSON when all route-specific fields are absent.
            if (Endpoint is not null || Protocol is not null || RuntimeBuild is not null)
            {
                if (Endpoint is null) writer.WriteNull("endpoint"); else writer.WriteString("endpoint", Endpoint);
                if (Protocol is null) writer.WriteNull("protocol"); else writer.WriteString("protocol", Protocol);
                if (RuntimeBuild is null) writer.WriteNull("runtimeBuild"); else writer.WriteString("runtimeBuild", RuntimeBuild);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Hash canónico: SHA-256 de la serialización canónica, hexadecimal minúsculo (64 chars).</summary>
    public string QualificationKeyHash() => Hex(Sha256(Encoding.UTF8.GetBytes(CanonicalJson())));

    public override string ToString() => ProviderId + "/" + ModelId
        + (Quantization is null ? "" : ":" + Quantization)
        + (Adapters.Count == 0 ? "" : ":adapters=" + Adapters.Count)
        + ":" + ToolCallFormat + "/" + ToolMode + ":" + PromptProfileVersion;

    public override bool Equals(object? other)
    {
        if (other is not ModelQualificationKey k)
        {
            return false;
        }

        return Ordinal(ProviderId, k.ProviderId) && Ordinal(ModelId, k.ModelId)
            && OrdinalNull(ModelRevision, k.ModelRevision) && OrdinalNull(Quantization, k.Quantization)
            && Adapters.SequenceEqual(k.Adapters) && OrdinalNull(Backend, k.Backend)
            && OrdinalNull(BackendBuild, k.BackendBuild) && OrdinalNull(ChatTemplateHash, k.ChatTemplateHash)
            && Ordinal(AdapterProfile, k.AdapterProfile)
            && ToolCallFormat == k.ToolCallFormat
            && ToolMode == k.ToolMode
            && Ordinal(PromptProfileVersion, k.PromptProfileVersion)
            && OrdinalNull(Endpoint, k.Endpoint)
            && OrdinalNull(Protocol, k.Protocol)
            && OrdinalNull(RuntimeBuild, k.RuntimeBuild);
    }

    public override int GetHashCode()
    {
        var hash = 17;
        hash = hash * 31 + ProviderId.GetHashCode(StringComparison.Ordinal);
        hash = hash * 31 + ModelId.GetHashCode(StringComparison.Ordinal);
        hash = hash * 31 + (ModelRevision?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + (Quantization?.GetHashCode(StringComparison.Ordinal) ?? 0);
        foreach (var a in Adapters)
        {
            hash = hash * 31 + a.GetHashCode(StringComparison.Ordinal);
        }

        hash = hash * 31 + (Backend?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + (BackendBuild?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + (ChatTemplateHash?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + AdapterProfile.GetHashCode(StringComparison.Ordinal);
        hash = hash * 31 + (int)ToolCallFormat;
        hash = hash * 31 + (int)ToolMode;
        hash = hash * 31 + PromptProfileVersion.GetHashCode(StringComparison.Ordinal);
        hash = hash * 31 + (Endpoint?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + (Protocol?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = hash * 31 + (RuntimeBuild?.GetHashCode(StringComparison.Ordinal) ?? 0);
        return hash;
    }

    private static bool Ordinal(string a, string b) => a.Equals(b, StringComparison.Ordinal);

    private static bool OrdinalNull(string? a, string? b) =>
        a is null ? b is null : b is not null && Ordinal(a!, b!);

    internal static byte[] Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(bytes);
    }

    internal static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}
