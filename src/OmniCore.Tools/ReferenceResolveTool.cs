namespace OmniCore.Tools;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Tool Core `reference.resolve` (ADR-0033 §1): resuelve las referencias @… del composer al
/// contenido del objetivo (archivo/carpeta) con programa y frontera. Pasa por el pipeline de
/// tools y queda en el Effect Journal como lectura (ADR-0037 §3).
/// </summary>
public sealed class ReferenceResolveTool : ITool, IReferenceResolver
{
    private readonly ToolDescriptor _descriptor;

    private readonly IPathBoundaryValidator _boundary;

    public ReferenceResolveTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(
            new ToolId("reference.resolve"),
            "Resuelve una referencia @… (archivo/carpeta) dentro del workspace (read-only).",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"ref\":{\"type\":\"string\"}}}"),
            new string[] { "read" }, true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        // Declara la ruta resuelta pedida (ADR-0037 §1) en las claims; Security evalúa la resource.
        var reference = ArgsJson.Parse(call.NormalizedArgumentsJson).TryGetValue("ref", out var r) ? r : null;
        var claims = reference is null || reference!.Length == 0
            ? ResourceClaims.Empty()
            : new ResourceClaims(new string[] { reference!.TrimStart('@') }, new string[0], new NetworkGrant[0],
                null, new string[0]);
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.None,
            claims, ToolRisk.Low, null);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(IAuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var reference = ArgsJson.Parse(intent.Intent.NormalizedArgumentsJson).TryGetValue("ref", out var r) ? r : null;
        if (reference is null)
        {
            return System.Threading.Tasks.Task.FromResult(new ToolResult("Falta 'ref'", null, null, 0, false,
                EffectOutcome.None));
        }

        var resolved = Resolve(reference!, context.WorkspaceRoot);
        var summary = resolved.Kind + " " + resolved.TargetPath + ": " + resolved.Summary;
        return System.Threading.Tasks.Task.FromResult(new ToolResult(summary, resolved.Summary, null, 0, false,
            EffectOutcome.None));
    }

    public ResolvedReference Resolve(string reference, string workspaceRoot)
    {
        var clean = reference.TrimStart('@');
        var full = workspaceRoot.TrimEnd('/') + "/" + clean.Replace('\\', '/');
        if (!_boundary.IsWithin(full, workspaceRoot))
        {
            return new ResolvedReference("invalid", clean, "fuera del workspace");
        }

        if (File.Exists(full))
        {
            var text = File.ReadAllText(full);
            var preview = text.Length > 200 ? text.Substring(0, 200) + "…" : text;
            return new ResolvedReference("file", full, preview);
        }

        if (Directory.Exists(full))
        {
            return new ResolvedReference("folder", full, "carpeta");
        }

        return new ResolvedReference("missing", clean, "no encontrado");
    }
}