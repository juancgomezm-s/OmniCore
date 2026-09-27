namespace OmniCore.Abstractions;

/// <summary>
/// Presentación declarativa de una tool para la UI (ADR-0033 §2): claves de LocalizedText y
/// verbosidad, nunca frases. La UI la interpreta con los recursos es/en (ADR-0040).
/// </summary>
public sealed class ToolPresentation
{
    public string TitleKey { get; }

    public string RunningKey { get; }

    public string DoneKey { get; }

    public string[] ArgsKeys { get; }

    public ToolPresentation(string titleKey, string runningKey, string doneKey, string[] argsKeys)
    {
        TitleKey = titleKey;
        RunningKey = runningKey;
        DoneKey = doneKey;
        ArgsKeys = argsKeys;
    }

    public static ToolPresentation Simple(string toolId) =>
        new("tool." + toolId + ".title", "tool." + toolId + ".running", "tool." + toolId + ".done", new string[0]);
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