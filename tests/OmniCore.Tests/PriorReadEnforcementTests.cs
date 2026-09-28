using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Tests deterministas de la lectura previa obligatoria de filesystem.patch (ADR-0044 §5),
/// cableados en el pipeline REAL (ScriptedToolExecutor → ToolRuntime → ReadFileTool /
/// FilesystemPatchTool): un patch a un archivo existente exige que el modelo haya leído ESA
/// ruta/versión con éxito en el MISMO Run.
///
///  - patch sin lectura previa       → PRIOR_READ_REQUIRED, archivo intacto;
///  - read exitoso + patch           → éxito;
///  - read de otra ruta / read fallido → rechazo (identidad de path/version);
///  - read + modificación externa    → STALE_WRITE (se conserva la defensa del token);
///  - aislamiento entre Runs         → un token de un Run anterior no habilita el patch.
///
/// No dependen de un LLM vivo: el modelo es un provider scriptado determinista.
/// </summary>
public sealed class PriorReadEnforcementTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-priorread", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RmDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception)
        {
        }
    }

    private static string VersionOf(string content) =>
        FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes(content));

    private static EffectiveModelPolicy PatchOnlyPolicy()
    {
        var key = ModelPolicyKey.For("p", "m");
        return EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, ModelPolicyPresets.For(ModelPolicyCategory.PatchOnly),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
                PlanControl.ModelDriven, 8));
    }

    // ---- Ejecutor real del pipeline con frontera de capacidad (política del modelo) ----

    private static ScriptedToolExecutor PatchPipeline(string wsDir, ModelCapabilityBoundary boundary)
    {
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService());
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["filesystem.read"] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        return ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, wsDir, boundary);
    }

    private static ValidatedToolCall ReadCall(string path) =>
        new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.read"), "pc-read",
            "{\"path\":\"" + path + "\"}");

    private static ValidatedToolCall PatchCall(string path, string expectedVersion, string oldText, string newText)
    {
        var args = "{\"path\":\"" + path + "\",\"expectedVersion\":\"" + expectedVersion
            + "\",\"oldText\":\"" + oldText + "\",\"newText\":\"" + newText + "\"}";
        return new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.patch"),
            "pc-patch", args);
    }

    // ============ 1. Patch sin lectura previa => rechazo PRIOR_READ_REQUIRED, archivo intacto ======

    [Fact]
    public void Patch_without_prior_read_is_rejected_and_file_intact()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var boundary = new ModelCapabilityBoundary(PatchOnlyPolicy());
        var executor = PatchPipeline(ws, boundary);

        // El modelo emite el hash correcto directamente, SIN haber leído el archivo en este Run.
        var outcome = executor.ExecuteTool(
            PatchCall("doc.txt", VersionOf(original), "linea-dos", "linea-dos-X"), false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "Un patch sin lectura previa debe rechazarse. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Failed, outcome.FinalState);
        Assert.Contains("PRIOR_READ_REQUIRED", outcome.Summary);
        Assert.Equal(original, File.ReadAllText(ws + "\\doc.txt"));
        RmDir(ws);
    }

    // ============ 2. Read exitoso + patch con ese token => éxito ============

    [Fact]
    public void Successful_read_then_patch_with_that_token_succeeds()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var boundary = new ModelCapabilityBoundary(PatchOnlyPolicy());
        var executor = PatchPipeline(ws, boundary);

        // 1. Lectura previa efectiva del archivo (registra doc.txt -> token real).
        var readOutcome = executor.ExecuteTool(ReadCall("doc.txt"), false, CancellationToken.None);
        Assert.True(readOutcome.Succeeded, "La lectura previa debe aplicar. summary=" + readOutcome.Summary);

        // 2. Patch con ESE token (el que la lectura expone): debe aplicar.
        var patchOutcome = executor.ExecuteTool(
            PatchCall("doc.txt", VersionOf(original), "linea-dos", "linea-dos-B"), false, CancellationToken.None);
        Assert.True(patchOutcome.Succeeded, "Lee + patch con ese token = éxito. summary=" + patchOutcome.Summary);
        Assert.Equal("linea-uno\nlinea-dos-B\n", File.ReadAllText(ws + "\\doc.txt"));
        RmDir(ws);
    }

    // ============ 3. Read de otra ruta / read fallido => rechazo ============

    [Fact]
    public void Patch_with_token_from_other_path_is_rejected()
    {
        var ws = TempDir();
        // a.txt y b.txt tienen EXACTAMENTE el mismo contenido → token idéntico (SHAR-256 del contenido).
        var shared = "mismo-contenido\n";
        File.WriteAllText(ws + "\\a.txt", shared);
        File.WriteAllText(ws + "\\b.txt", shared);
        var boundary = new ModelCapabilityBoundary(PatchOnlyPolicy());
        var executor = PatchPipeline(ws, boundary);

        // Se lee SOLO a.txt → el registro tiene a.txt, NO b.txt.
        var readOutcome = executor.ExecuteTool(ReadCall("a.txt"), false, CancellationToken.None);
        Assert.True(readOutcome.Succeeded, "read a.txt ok. summary=" + readOutcome.Summary);

        // Se intenta parchear b.txt con el mismo token (hash válido y vigente para b.txt):
        // la identidad es de path/version, y b.txt NO se leyó en este Run → rechazo.
        var patchOutcome = executor.ExecuteTool(
            PatchCall("b.txt", VersionOf(shared), "mismo-contenido", "otro-contenido"), false, CancellationToken.None);
        Assert.False(patchOutcome.Succeeded, "El token de otra ruta no habilita el patch. summary=" + patchOutcome.Summary);
        Assert.Contains("PRIOR_READ_REQUIRED", patchOutcome.Summary);
        Assert.Equal(shared, File.ReadAllText(ws + "\\b.txt"));
        RmDir(ws);
    }

    [Fact]
    public void Failed_read_does_not_enable_patch()
    {
        var ws = TempDir();
        var original = "linea-uno\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var boundary = new ModelCapabilityBoundary(PatchOnlyPolicy());
        var executor = PatchPipeline(ws, boundary);

        // Lectura FALLIDA (el archivo no existe): no registra ninguna ruta.
        var failedRead = executor.ExecuteTool(ReadCall("no-existe.txt"), false, CancellationToken.None);
        Assert.False(failedRead.Succeeded, "read de archivo inexistente falla. summary=" + failedRead.Summary);

        // Un patch del archivo real con su token vigente NO se habilita por el read fallido.
        var patchOutcome = executor.ExecuteTool(
            PatchCall("doc.txt", VersionOf(original), "linea-uno", "linea-uno-Z"), false, CancellationToken.None);
        Assert.False(patchOutcome.Succeeded, "Un read fallido no habilita el patch. summary=" + patchOutcome.Summary);
        Assert.Contains("PRIOR_READ_REQUIRED", patchOutcome.Summary);
        Assert.Equal(original, File.ReadAllText(ws + "\\doc.txt"));
        RmDir(ws);
    }

    // ============ 4. Read + modificación externa => STALE_WRITE (se conserva) ============

    [Fact]
    public void Read_then_external_modification_still_rejects_stale_token()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var boundary = new ModelCapabilityBoundary(PatchOnlyPolicy());
        var executor = PatchPipeline(ws, boundary);

        // 1. Lectura previa efectiva (doc.txt -> token de 'original').
        var readOutcome = executor.ExecuteTool(ReadCall("doc.txt"), false, CancellationToken.None);
        Assert.True(readOutcome.Succeeded, "read ok. summary=" + readOutcome.Summary);

        // 2. Modificación EXTERNA fuera del control del modelo: el contenido cambia.
        var external = "linea-uno\nlinea-dos\nlinea-extra-externa\n";
        File.WriteAllText(ws + "\\doc.txt", external);

        // 3. El modelo parchea con el token de la lectura previa (ya obsoleto): STALE_WRITE.
        var patchOutcome = executor.ExecuteTool(
            PatchCall("doc.txt", VersionOf(original), "linea-dos", "linea-dos-B"), false, CancellationToken.None);
        Assert.False(patchOutcome.Succeeded, "El token obsoleto tras modificación externa se rechaza. summary=" + patchOutcome.Summary);
        Assert.Contains("STALE_WRITE", patchOutcome.Summary);
        Assert.Equal(external, File.ReadAllText(ws + "\\doc.txt"));
        RmDir(ws);
    }

    // ============ 5. Aislamiento entre Runs: el token de un Run anterior no habilita ============

    [Fact]
    public void Prior_read_does_not_leak_across_runs()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(ws + "\\doc.txt", original);

        // Run 1: el modelo lee doc.txt (token T) en SU registro por-Run.
        var boundaryRun1 = new ModelCapabilityBoundary(PatchOnlyPolicy());
        var run1 = PatchPipeline(ws, boundaryRun1);
        var readOutcome = run1.ExecuteTool(ReadCall("doc.txt"), false, CancellationToken.None);
        Assert.True(readOutcome.Succeeded, "Run1: read ok. summary=" + readOutcome.Summary);
        Assert.True(boundaryRun1.ReadRegistry().Size() == 1, "Run1 registró doc.txt");

        // Run 2: INSTANCIA nueva de frontera/registro (nunca estado global). El contenido de
        // doc.txt NO cambió, así que el token T SEGUIRÍA pasando la verificación de contenido
        // (STALE_WRITE no lo rechazaría); es el aislamiento por-Run lo que lo corta.
        var boundaryRun2 = new ModelCapabilityBoundary(PatchOnlyPolicy());
        var run2 = PatchPipeline(ws, boundaryRun2);
        Assert.True(boundaryRun2.ReadRegistry().Size() == 0, "Run2 empieza sin lecturas");

        var patchOutcome = run2.ExecuteTool(
            PatchCall("doc.txt", VersionOf(original), "linea-dos", "linea-dos-C"), false, CancellationToken.None);
        Assert.False(patchOutcome.Succeeded, "El token de un Run anterior no habilita el patch. summary=" + patchOutcome.Summary);
        Assert.Contains("PRIOR_READ_REQUIRED", patchOutcome.Summary);
        Assert.Equal(original, File.ReadAllText(ws + "\\doc.txt"));
        RmDir(ws);
    }

    // ============ 6. Read de bytes inválidos (encoding): error + sin registro + patch rechazado =====

    [Fact]
    public void Read_invalid_encoding_does_not_record_and_does_not_enable_patch()
    {
        var ws = TempDir();
        // Bytes UTF-8 inválidos (0xC3 seguido de 0x28 no es una secuencia válida):
        // FileVersion.Decode los rechaza con UnsupportedEncodingException.
        var raw = new byte[] { 0x68, 0x6F, 0x6C, 0xC3, 0x28 };
        File.WriteAllBytes(ws + "\\doc.txt", raw);
        var boundary = new ModelCapabilityBoundary(PatchOnlyPolicy());
        var executor = PatchPipeline(ws, boundary);

        // 1. Read devuelve ToolResult ERROR (encoding inválido) y NO debe registrar la ruta:
        //    la lectura no fue efectiva, el modelo nunca vio contenido ni el token.
        var readOutcome = executor.ExecuteTool(ReadCall("doc.txt"), false, CancellationToken.None);
        Assert.False(readOutcome.Succeeded, "read de bytes inválidos debe fallar. summary=" + readOutcome.Summary);
        Assert.Equal(0, boundary.ReadRegistry().Size());
        Assert.False(boundary.ReadRegistry().HasRead("doc.txt"));

        // 2. Patch con el hash REAL de los bytes vigentes (pasaría la verificación STALE_WRITE si
        //    la lectura estuviera registrada): debe rechazarse por PRIOR_READ_REQUIRED y el
        //    archivo debe quedar intacto.
        var patchOutcome = executor.ExecuteTool(
            PatchCall("doc.txt", FilesystemPatchTool.VersionToken(raw), "hol", "hol-camb"),
            false, CancellationToken.None);
        Assert.False(patchOutcome.Succeeded, "El read fallido por encoding no habilita el patch. summary=" + patchOutcome.Summary);
        Assert.Contains("PRIOR_READ_REQUIRED", patchOutcome.Summary);
        var after = File.ReadAllBytes(ws + "\\doc.txt");
        Assert.Equal(raw.Length, after.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            Assert.Equal(raw[i], after[i]);
        }

        RmDir(ws);
    }

    // ============ 7. Read válido: el token del preview (lo que ve el modelo) habilita el patch ====

    [Fact]
    public void Valid_read_exposed_token_is_visible_and_enables_patch()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var boundary = new ModelCapabilityBoundary(PatchOnlyPolicy());
        var executor = PatchPipeline(ws, boundary);

        // 1. Read válido: registra la ruta y el marcador [version:…] llega visible en el preview
        //    (lo que el modelo realmente observa).
        var readOutcome = executor.ExecuteTool(ReadCall("doc.txt"), false, CancellationToken.None);
        Assert.True(readOutcome.Succeeded, "read válido ok. summary=" + readOutcome.Summary);
        Assert.True(boundary.ReadRegistry().HasRead("doc.txt"));
        var exposed = ExtractTokenFromReadPreview(readOutcome.Preview!);
        Assert.NotNull(exposed);
        Assert.Equal(FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes(original)), exposed);

        // 2. Patch con EXACTAMENTE el token que el read expuso: debe aplicar.
        var patchOutcome = executor.ExecuteTool(
            PatchCall("doc.txt", exposed!, "linea-dos", "linea-dos-B"), false, CancellationToken.None);
        Assert.True(patchOutcome.Succeeded, "Read válido + token visible = patch permitido. summary=" + patchOutcome.Summary);
        Assert.Equal("linea-uno\nlinea-dos-B\n", File.ReadAllText(ws + "\\doc.txt"));
        RmDir(ws);
    }

    /// <summary>Extrae el token de versión del marcador [version:…] tal como lo expone ReadFileTool.</summary>
    private static string? ExtractTokenFromReadPreview(string preview)
    {
        const string start = "[version:";
        var idx = preview.LastIndexOf(start, StringComparison.Ordinal);
        if (idx < 0) return null;
        var end = preview.IndexOf(']', idx + start.Length);
        if (end < 0) return null;
        return preview.Substring(idx + start.Length, end - idx - start.Length);
    }

}