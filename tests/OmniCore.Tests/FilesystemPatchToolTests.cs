using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Tests de filesystem.patch (M3, ADR-0044 §5): parche localizado con token de versión
/// obligatorio. Éxito, rechazo STALE_WRITE, ruta fuera del workspace, sin creación, oldText
/// ambiguo/ausente, y la frontera de capacidad PatchOnly/ObserveOnly aplicada antes del efecto.
/// </summary>
public sealed class FilesystemPatchToolTests
{
    private static string TestCwd => Path.GetFullPath(".");

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-patch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ScriptedToolExecutor PatchExecutor(string wsDir)
    {
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService());
        return ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow), wsDir);
    }

    private static ValidatedToolCall PatchCall(string wsDir, string relPath, string expectedVersion,
        string oldText, string newText)
    {
        var args = "{\"path\":\"" + relPath + "\",\"expectedVersion\":\"" + expectedVersion +
            "\",\"oldText\":" + JsonEscape(oldText) + ",\"newText\":" + JsonEscape(newText) + "}";
        return new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.patch"),
            "pc-" + Guid.NewGuid().ToString("N").Substring(0, 6), args);
    }

    private static string JsonEscape(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    private static string VersionOf(string content) => FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// Extrae el token de versión del marcador [version:<token>] tal como lo expone
    /// ReadFileTool en su Preview. Es lo que hace el modelo/CLI antes de un patch.
    /// </summary>
    private static string? ExtractTokenFromReadPreview(string preview)
    {
        const string start = "[version:";
        var idx = preview.LastIndexOf(start, StringComparison.Ordinal);
        if (idx < 0) return null;
        var end = preview.IndexOf(']', idx + start.Length);
        if (end < 0) return null;
        return preview.Substring(idx + start.Length, end - idx - start.Length);
    }

    [Fact]
    public async System.Threading.Tasks.Task Patch_applies_localized_change_and_preserves_unrelated_content()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);
        var original = "linea1\nlinea2\nlinea3\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var version = VersionOf(original);

        var outcome = executor.ExecuteTool(
            PatchCall(ws, "doc.txt", version, "linea2", "LINEA2-CAMBIADA"), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El patch con token vigente se aplica. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Succeeded, outcome.FinalState);
        var after = File.ReadAllText(ws + "\\doc.txt");
        Assert.True(after == "linea1\nLINEA2-CAMBIADA\nlinea3\n",
            "Solo el rango del parche cambia; el contenido no relacionado se conserva");
    }

    [Fact]
    public async System.Threading.Tasks.Task Patch_rejects_stale_version_before_mutating()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);
        File.WriteAllText(ws + "\\doc.txt", "contenido original");
        var staleVersion = VersionOf("otro contenido viejo");

        var outcome = executor.ExecuteTool(
            PatchCall(ws, "doc.txt", staleVersion, "contenido original", "nuevo"), false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "Un token obsoleto se rechaza (STALE_WRITE). summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        // No se mutó nada.
        Assert.Equal("contenido original", File.ReadAllText(ws + "\\doc.txt"));
        Assert.Contains("STALE_WRITE", outcome.Summary);
    }

    [Fact]
    public async System.Threading.Tasks.Task Patch_rejects_path_outside_workspace()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);
        // Se crea el archivo FUERA del workspace (en el temp root, no dentro de ws) y se intenta
        // alcanzarlo con un path de escape via traversal.
        var outsideFile = Path.Combine(Path.GetTempPath(), "omnicore-m3-patch-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outsideFile, "contenido fuera");
        try
        {
            var relativeEscape = Path.GetRelativePath(ws, outsideFile).Replace('\\', '/');
            var outcome = executor.ExecuteTool(
                PatchCall(ws, relativeEscape, VersionOf("contenido fuera"), "contenido fuera", "nuevo"),
                false, CancellationToken.None);

            Assert.False(outcome.Succeeded, "Un path que escapa del workspace se rechaza. summary=" + outcome.Summary);
            Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        }
        finally
        {
            if (File.Exists(outsideFile)) File.Delete(outsideFile);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Patch_rejects_when_file_does_not_exist()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);

        var outcome = executor.ExecuteTool(
            PatchCall(ws, "no-existe.txt", VersionOf("x"), "a", "b"), false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "Un patch no crea archivos: se rechaza si no existe. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.False(File.Exists(ws + "\\no-existe.txt"), "No se crea el archivo");
    }

    [Fact]
    public async System.Threading.Tasks.Task Patch_rejects_ambiguous_oldText()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);
        var content = "abc\nabc\nabc\n";
        File.WriteAllText(ws + "\\doc.txt", content);
        var version = VersionOf(content);

        var outcome = executor.ExecuteTool(
            PatchCall(ws, "doc.txt", version, "abc", "xyz"), false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "oldText ambiguo (varias ocurrencias) se rechaza. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.True(File.ReadAllText(ws + "\\doc.txt") == content, "El archivo no cambia ante ambigüedad");
    }

    [Fact]
    public async System.Threading.Tasks.Task Patch_rejects_when_oldText_not_found()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);
        var content = "un contenido distinto";
        File.WriteAllText(ws + "\\doc.txt", content);
        var version = VersionOf(content);

        var outcome = executor.ExecuteTool(
            PatchCall(ws, "doc.txt", version, "texto que no aparece", "nuevo"), false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "oldText ausente se rechaza. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
    }

    [Fact]
    public async System.Threading.Tasks.Task Patch_requires_expected_version_token()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);
        File.WriteAllText(ws + "\\doc.txt", "contenido");

        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.patch"), "pc-notoken",
            "{\"path\":\"doc.txt\",\"oldText\":\"contenido\",\"newText\":\"nuevo\"}");
        var outcome = executor.ExecuteTool(call, false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "Sin expectedVersion el patch se rechaza (token obligatorio). summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.Contains("expectedVersion", outcome.Summary);
    }

    [Fact]
    public async System.Threading.Tasks.Task Patch_handles_multiline_text_with_json_escapes()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);
        var content = "inicio\nbloque antiguo\nfin\n";
        File.WriteAllText(ws + "\\doc.txt", content);
        var version = VersionOf(content);

        var outcome = executor.ExecuteTool(
            PatchCall(ws, "doc.txt", version, "bloque antiguo", "bloque\nnuevo\ndos lineas"), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El parser JSON real soporta oldText/newText con saltos de línea. summary=" + outcome.Summary);
        Assert.Equal("inicio\nbloque\nnuevo\ndos lineas\nfin\n", File.ReadAllText(ws + "\\doc.txt"));
    }

    // ---- Frontera de capacidad PatchOnly / ObserveOnly (ADR-0044 §5) ----

    private static ModelCapabilityBoundary BoundaryFor(OmniCore.Domain.UserModelPolicy policy)
    {
        var key = ModelPolicyKey.For("p", "m");
        var effective = EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, policy, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 16, GuidanceLevel.Full, 3,
                PlanControl.ModelDriven, 8));
        return new ModelCapabilityBoundary(effective);
    }

    [Fact]
    public async System.Threading.Tasks.Task ObserveOnly_boundary_blocks_patch_before_permissions()
    {
        var ws = TempDir();
        File.WriteAllText(ws + "\\doc.txt", "contenido");
        var version = VersionOf("contenido");
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService());
        var sink = new List<DomainEventPayload>();
        var boundary = BoundaryFor(ModelPolicyPresets.ObserveOnly());
        var policy = ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow);
        var runtime = ToolRuntime.For(hostTools.Catalog(), policy, payload => { sink.Add(payload); return VoidBox.Instance; }, boundary);

        var call = PatchCall(ws, "doc.txt", version, "contenido", "nuevo");
        var prepContext = new ToolPreparationContext(ws, DateTimeOffset.Now);
        var execContext = new ToolExecutionContext(ws);
        var outcome = runtime.Run(call, prepContext, execContext, false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "ObserveOnly no permite patch: la frontera corta antes de permisos");
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.True(File.ReadAllText(ws + "\\doc.txt") == "contenido", "El archivo no se toca");
    }

    [Fact]
    public async System.Threading.Tasks.Task PatchOnly_boundary_allows_patch()
    {
        var ws = TempDir();
        var original = "linea1\nlinea2\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var version = VersionOf(original);
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService());
        var sink = new List<DomainEventPayload>();
        var boundary = BoundaryFor(ModelPolicyPresets.PatchOnly());
        var policy = ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow);
        var runtime = ToolRuntime.For(hostTools.Catalog(), policy, payload => { sink.Add(payload); return VoidBox.Instance; }, boundary);

        var call = PatchCall(ws, "doc.txt", version, "linea2", "linea2-b");
        var prepContext = new ToolPreparationContext(ws, DateTimeOffset.Now);
        var execContext = new ToolExecutionContext(ws);
        var outcome = runtime.Run(call, prepContext, execContext, false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "PatchOnly permite el patch localizado. summary=" + outcome.Summary);
        Assert.Equal("linea1\nlinea2-b\n", File.ReadAllText(ws + "\\doc.txt"));
    }

    [Fact]
    public void VersionToken_is_deterministic_and_content_sensitive()
    {
        Assert.Equal(FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes("abc")),
                    FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes("abc")));
        Assert.NotEqual(FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes("abc")),
                       FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes("abd")));
    }

    [Fact]
    public void Patch_rejects_when_oldText_is_entire_file_content()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);
        var content = "linea1\nlinea2\nlinea3\n";
        File.WriteAllText(ws + "\\doc.txt", content);
        var version = VersionOf(content);

        // oldText == contenido completo: se rechaza (no se permite sustituir el archivo entero).
        var outcome = executor.ExecuteTool(
            PatchCall(ws, "doc.txt", version, content, "otro contenido"), false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "oldText igual al contenido completo debe rechazarse. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.Equal(content, File.ReadAllText(ws + "\\doc.txt"));
    }

    [Fact]
    public void Patch_rejects_when_newText_is_empty()
    {
        var ws = TempDir();
        var executor = PatchExecutor(ws);
        var content = "linea1\nlinea2\n";
        File.WriteAllText(ws + "\\doc.txt", content);
        var version = VersionOf(content);

        // newText vacío: se rechaza (no se permite vaciar el archivo).
        var outcome = executor.ExecuteTool(
            PatchCall(ws, "doc.txt", version, "linea1", ""), false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "newText vacío debe rechazarse. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.Equal(content, File.ReadAllText(ws + "\\doc.txt"));
    }

    [Fact]
    public void EndToEnd_read_extract_version_patch_succeeds_then_stale_fails()
    {
        var ws = TempDir();
        var initial = "linea1\nlinea2\nlinea3\n";
        File.WriteAllText(ws + "\\doc.txt", initial);

        // Executor que permite leer Y parchear (ambas con Allow) para simular el flujo real:
        // leer → extraer token → parchear → token obsoleto falla.
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService());
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["filesystem.read"] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, ws);

        // 1. Leer el archivo con la herramienta real (ReadFileTool) vía executor y extraer el
        //    token de la salida (tal como lo haría el modelo). El token vive en el Preview.
        var readCall = new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.read"),
            "pc-read", "{\"path\":\"doc.txt\"}");
        var readOutcome = executor.ExecuteTool(readCall, false, CancellationToken.None);
        Assert.True(readOutcome.Succeeded, "La lectura debe aplicar. summary=" + readOutcome.Summary);
        var version = ExtractTokenFromReadPreview(readOutcome.Preview!);
        Assert.NotNull(version);

        // 2. Usar ese token en el patch: debe pasar.
        var firstPatch = executor.ExecuteTool(
            PatchCall(ws, "doc.txt", version!, "linea2", "linea2-cambiada"), false, CancellationToken.None);
        Assert.True(firstPatch.Succeeded, "El patch con el token vigente debe aplicar. summary=" + firstPatch.Summary);
        Assert.Equal("linea1\nlinea2-cambiada\nlinea3\n", File.ReadAllText(ws + "\\doc.txt"));

        // 3. El archivo ya cambió: el MISMO token viejo ahora falla (STALE_WRITE) sin mutar.
        var currentBefore = File.ReadAllText(ws + "\\doc.txt");
        var stalePatch = executor.ExecuteTool(
            PatchCall(ws, "doc.txt", version!, "linea3", "linea3-cambiada"), false, CancellationToken.None);
        Assert.False(stalePatch.Succeeded, "El token viejo debe rechazarse. summary=" + stalePatch.Summary);
        Assert.Equal(ToolCallState.Rejected, stalePatch.FinalState);
        Assert.Equal(currentBefore, File.ReadAllText(ws + "\\doc.txt"));
    }
}
