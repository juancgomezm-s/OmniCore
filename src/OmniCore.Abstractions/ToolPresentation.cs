namespace OmniCore.Abstractions;

/// <summary>Categoría de actividad de una tool: elige glyph y rol en el cliente (ADR-0033 §2).</summary>
public enum ActivityCategory
{
    Search,
    Read,
    Edit,
    Execute,
    Test,
    Build,
    Plan,
    Delegate,
    Network,
    Other,
}

/// <summary>
/// Presentación declarativa de una tool para la UI (ADR-0033 §2, INV-013): datos, nunca código ni frases.
/// Las etiquetas son claves de localización (ADR-0040) con argumentos; el cliente las interpola con los
/// campos de <see cref="SummaryFields"/> tomados de los argumentos de la llamada (ya redactados).
/// </summary>
public sealed record ToolPresentation(ActivityCategory Category, string RunningKey, string SucceededKey,
    string FailedKey, IReadOnlyList<string> SummaryFields)
{
    /// <summary>Convención de claves: <c>tool.&lt;id&gt;.running|succeeded|failed</c>.</summary>
    public static ToolPresentation For(string toolId, ActivityCategory category, params string[] summaryFields) =>
        new(category, "tool." + toolId + ".running", "tool." + toolId + ".succeeded", "tool." + toolId + ".failed",
            summaryFields);

    private static readonly IReadOnlyDictionary<string, ToolPresentation> Core =
        new Dictionary<string, ToolPresentation>(StringComparer.Ordinal)
        {
            ["filesystem.read"] = For("filesystem.read", ActivityCategory.Read, "path"),
            ["filesystem.list"] = For("filesystem.list", ActivityCategory.Read, "path"),
            ["search.text"] = For("search.text", ActivityCategory.Search, "pattern"),
            ["filesystem.write"] = For("filesystem.write", ActivityCategory.Edit, "path"),
            ["filesystem.patch"] = For("filesystem.patch", ActivityCategory.Edit, "path"),
            ["process.exec"] = For("process.exec", ActivityCategory.Execute, "executable"),
            ["dyn.core.verify_integration"] = For("dyn.core.verify_integration", ActivityCategory.Test, "executable"),
            ["plan.propose"] = For("plan.propose", ActivityCategory.Plan),
            ["mode.propose"] = For("mode.propose", ActivityCategory.Plan),
            ["reference.resolve"] = For("reference.resolve", ActivityCategory.Read),
            ["artifact.read"] = For("artifact.read", ActivityCategory.Read),
            ["user.ask"] = For("user.ask", ActivityCategory.Other),
            ["core.agents.mailbox.receive"] = For("core.agents.mailbox.receive", ActivityCategory.Delegate),
        };

    /// <summary>Presentación de una tool Core; null para las demás (el cliente usa la etiqueta genérica).</summary>
    public static ToolPresentation? Of(string toolId) => Core.TryGetValue(toolId, out var value) ? value : null;

    /// <summary>Ids de las tools Core con presentación propia.</summary>
    public static IReadOnlyCollection<string> CoreToolIds => Core.Keys.ToArray();
}

/// <summary>Referencia resuelta del composer (@file, @folder; ADR-0033 §1).</summary>
public sealed class ResolvedReference
{
    public string Kind { get; }

    public string TargetPath { get; }

    public string Summary { get; }

    public ResolvedReference(string kind, string targetPath, string summary)
    {
        Kind = kind;
        TargetPath = targetPath;
        Summary = summary;
    }
}

/// <summary>Resolver de referencias @… (ADR-0033 §1). Pasa por el pipeline de tools como tool Core.</summary>
public interface IReferenceResolver
{
    ResolvedReference Resolve(string reference, string workspaceRoot);
}