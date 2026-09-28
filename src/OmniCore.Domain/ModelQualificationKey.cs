namespace OmniCore.Domain;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Identidad exacta de una configuración de modelo para cualificación empírica (ADR-0007 §5).
/// Es más específica que la ModelPolicyKey (ADR-0044 §2): además de los mismos campos de
/// configuración incluye ToolCallFormat y ToolMode, porque la cualificación mide el
/// comportamiento concreto de esa combinación. Un cambio en cualquier campo relevante produce
/// una clave nueva y exige una cualificación nueva (ADR-0007 §4).
///
/// Serialización canónica: JSON con orden fijo de campos, UTF-8, sin dependencias de cultura
/// ni de plataforma. Los adapters se canonizan como conjunto ordenado (deduplicado, orden
/// ordinal) porque semánticamente es un conjunto: el orden de entrada no importa.
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

    public ModelQualificationKey(string providerId, string modelId, string? modelRevision, string? quantization,
        IReadOnlyList<string> adapters, string? backend, string? backendBuild, string? chatTemplateHash,
        string adapterProfile, ToolCallFormat toolCallFormat, ToolMode toolMode, string promptProfileVersion)
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

        ProviderId = providerId;
        ModelId = modelId;
        ModelRevision = modelRevision;
        Quantization = quantization;
        Adapters = CanonicalAdapters(adapters);
        Backend = backend;
        BackendBuild = backendBuild;
        ChatTemplateHash = chatTemplateHash;
        AdapterProfile = adapterProfile;
        ToolCallFormat = toolCallFormat;
        ToolMode = toolMode;
        PromptProfileVersion = promptProfileVersion;
    }

    /// <summary>Clave para una configuración local sin metadatos finos.</summary>
    public static ModelQualificationKey For(string providerId, string modelId,
        ToolCallFormat toolCallFormat, ToolMode toolMode)
        => new(providerId, modelId, null, null, Array.Empty<string>(), null, null, null,
            "default", toolCallFormat, toolMode, "v1");

    /// <summary>JSON canónico determinista (orden fijo de campos; null explícito; adapters ordenados).</summary>
    public string CanonicalJson()
    {
        var adapters = new StringBuilder();
        adapters.Append('[');
        for (var i = 0; i < Adapters.Count; i++)
        {
            if (i > 0)
            {
                adapters.Append(',');
            }

            adapters.Append('"').Append(Escape(Adapters[i])).Append('"');
        }

        adapters.Append(']');

        return "{\"providerId\":\"" + Escape(ProviderId) + "\""
            + ",\"modelId\":\"" + Escape(ModelId) + "\""
            + ",\"modelRevision\":" + Nullable(ModelRevision)
            + ",\"quantization\":" + Nullable(Quantization)
            + ",\"adapters\":" + adapters
            + ",\"backend\":" + Nullable(Backend)
            + ",\"backendBuild\":" + Nullable(BackendBuild)
            + ",\"chatTemplateHash\":" + Nullable(ChatTemplateHash)
            + ",\"adapterProfile\":\"" + Escape(AdapterProfile) + "\""
            + ",\"toolCallFormat\":\"" + ToolCallFormat + "\""
            + ",\"toolMode\":\"" + ToolMode + "\""
            + ",\"promptProfileVersion\":\"" + Escape(PromptProfileVersion) + "\"}";
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
            && Ordinal(PromptProfileVersion, k.PromptProfileVersion);
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
        return hash;
    }

    private static IReadOnlyList<string> CanonicalAdapters(IReadOnlyList<string> adapters)
    {
        var copy = new List<string>();
        foreach (var a in adapters)
        {
            if (a is not null && a!.Length > 0)
            {
                copy.Add(a);
            }
        }

        copy.Sort(StringComparer.Ordinal);
        copy = copy.Distinct(StringComparer.Ordinal).ToList();
        return copy;
    }

    private static string Nullable(string? value) =>
        value is null ? "null" : "\"" + Escape(value) + "\"";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

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
