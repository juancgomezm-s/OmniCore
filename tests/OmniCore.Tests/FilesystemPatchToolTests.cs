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

    private static string VersionOf(string content) => FilesystemPatchTool.VersionToken(content);

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
        Assert.Equal(FilesystemPatchTool.VersionToken("abc"), FilesystemPatchTool.VersionToken("abc"));
        Assert.NotEqual(FilesystemPatchTool.VersionToken("abc"), FilesystemPatchTool.VersionToken("abd"));
    }
}
