namespace OmniCore.Tools;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Tools de lectura reales de M2 (ADR-0038 §2: sin APIs de plataforma fuera de las
/// abstracciones; aquí el acceso a filesystem se hace a través de la raíz del workspace
/// validada, sin salidas por path traversal). Frontera de paths congeb aplicada por Security.
/// </summary>
public sealed class ReadFileTool : ITool
{
    private readonly ToolDescriptor _descriptor;

    private readonly IPathBoundaryValidator _boundary;

    public ReadFileTool(IPathBoundaryValidator boundary)
    {
        _boundary = boundary;
        _descriptor = new ToolDescriptor(
            new ToolId("filesystem.read"),
            "Lee el contenido de un archivo dentro del workspace (read-only).",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"),
            new string[] { "read" }, true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        // Prepare es puro: declara las claims de lectura de la ruta concreta pedida (ADR-0014 §3),
        // para que Security evalúe la resource path real (no vacío). La lectura ocurre en Execute.
        var path = ExtractPath(call.NormalizedArgumentsJson);

        // ADR-0018 §4: rutas de secretos (.env, PEM/SSH, credenciales) se REJECT en Prepare:
        // nunca se llega a ejecutar la tool (el journal termina en ToolCallRejected, sin
        // toolcall.succeeded). P0-2.
        if (path is not null && path!.Length > 0 && new OmniCore.Domain.RedactionPolicy().IsSecretPath(path!))
        {
            return new PreparationRejected(
                "Acceso denegado: la ruta contiene secretos (.env, claves, credenciales) y está protegida (ADR-0018)",
                null);
        }

        var claims = path is null || path!.Length == 0
            ? ResourceClaims.Empty()
            : new ResourceClaims(new string[] { path! }, new string[0], new NetworkGrant[0], null, new string[0]);
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.None,
            claims, ToolRisk.Low, null);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(IAuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var path = ExtractPath(intent.Intent.NormalizedArgumentsJson);
        if (path is null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Falta 'path' en los argumentos"));
        }

        var full = JoinPath(context.WorkspaceRoot, path);
        if (!_boundary.IsWithin(full, context.WorkspaceRoot))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Ruta fuera del workspace"));
        }

        if (!File.Exists(full))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("Archivo no encontrado: " + path));
        }

        var content = File.ReadAllText(full);
        // Redacción obligatoria del contenido antes de exponerlo al modelo (ADR-0018 §4).
        content = new OmniCore.Domain.RedactionPolicy().Redact(content);
        var truncated = content.Length > 8000 ? content.Substring(0, 8000) : content;
        var externalized = content.Length > 8000;
        return System.Threading.Tasks.Task.FromResult(new ToolResult(
            externalized ? "Contenido truncado (externalizado)" : "ok (" + path + ")", truncated, null,
            content.Length, externalized, EffectOutcome.None));
    }

    private static string? ExtractPath(string argsJson)
    {
        var map = ArgsJson.Parse(argsJson);
        return map.TryGetValue("path", out var p) ? p : null;
    }

    private static string JoinPath(string root, string relative)
    {
        if (relative is null) return root;
        var norm = relative.Replace('\\', '/');
        return root.TrimEnd('/') + "/" + norm;
    }
}

/// <summary>Mini parser de argumentos JSON para herramientas (objeto plano de strings).</summary>
internal sealed class ArgsJson
{
    public static Dictionary<string, string> Parse(string json)
    {
        var map = new Dictionary<string, string>();
        if (json is null || json.Length == 0) return map;
        var body = json.Trim();
        if (body.Length >= 2 && body[0] == '{') body = body.Substring(1, body.Length - 2);
        var i = 0;
        while (i < body.Length)
        {
            var colon = body.IndexOf(':', i);
            if (colon < 0) break;
            var key = body.Substring(i, colon - i).Trim().Trim('"');
            var after = colon + 1;
            while (after < body.Length && body[after] == ' ') after += 1;
            if (after >= body.Length) break;
            if (body[after] == '"')
            {
                var end = body.IndexOf('"', after + 1);
                if (end < 0) break;
                map[key] = body.Substring(after + 1, end - after - 1);
                i = body.IndexOf(',', end) < 0 ? body.Length : body.IndexOf(',', end) + 1;
            }
            else
            {
                var comma = body.IndexOf(',', after);
                map[key] = comma < 0 ? body.Substring(after).Trim() : body.Substring(after, comma - after).Trim();
                i = comma < 0 ? body.Length : comma + 1;
            }
        }

        return map;
    }
}