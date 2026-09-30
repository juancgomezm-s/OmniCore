using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Deterministas de filesystem.write y de la política de mutación por Turn (M3 EPIC-021,
/// ADR-0044 §5): creación/reemplazo atómico con token de versión, STALE_WRITE idéntico al de
/// filesystem.patch, exposición create-only según el modo (frontera + tool), límites
/// MaxFilesPerTurn / MaxChangedLinesPerTurn / MaxRewriteRatio con rechazo tipado
/// (LIMIT_EXCEEDED/MUTATION_REFUSED) ANTES de escribir, RequirePriorRead/RequirePostEditValidation,
/// crash entre el Barrier (ToolCallStarted con metadatos canónicos, pre=absent para creaciones)
/// y la publicación reconciliado Applied/NotApplied sin duplicar, y replay limpio del journal.
///
/// Todo cableado en el pipeline REAL (ScriptedToolExecutor → ToolRuntime → tools), sin LLM.
/// </summary>
public sealed class FilesystemWriteToolTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-write", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RmDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string VersionOf(string content) => VersionOfBytes(Encoding.UTF8.GetBytes(content));

    private static string VersionOfBytes(byte[] bytes) => FilesystemPatchTool.VersionToken(bytes);

    /// <summary>Archivo de 8 líneas: cambiar 1 línea = ratio 0.125 (permitido incluso en PatchOnly 0.25).</summary>
    private static string EightLineFile()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 8; i++)
        {
            sb.Append("linea-").Append(i).Append('\n');
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- helpers de pipeline

    private static EffectiveModelPolicy EffectiveFor(ModelPolicyCategory category)
    {
        var key = ModelPolicyKey.For("p", "m");
        return EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, ModelPolicyPresets.For(category),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
                PlanControl.ModelDriven, 8));
    }

    /// <summary>Política Custom con techo completo de mutación (read/patch/create/replace) y
    /// política de mutación explícita: para probar que cada valor de ADR-0044 cambia el comportamiento.</summary>
    private static EffectiveModelPolicy CustomEffective(FileMutationPolicy mutation, ToolMode mode = ToolMode.Direct)
    {
        var caps = new HashSet<ModelToolCapability>
        {
            ModelToolCapability.WorkspaceRead,
            ModelToolCapability.PatchExisting,
            ModelToolCapability.CreateFile,
            ModelToolCapability.ReplaceFile,
        };
        var key = ModelPolicyKey.For("p", "m");
        var user = new UserModelPolicy(ModelPolicyCategory.Custom,
            new ModelToolPolicy(mode, 12, false, caps), mutation, "test", null);
        return EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, user, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new HarnessPolicy(ToolCallFormat.Native, mode, 12, GuidanceLevel.Full, 3,
                PlanControl.ModelDriven, 8));
    }

    private static FileMutationPolicy Mutation(FileMutationMode mode, int maxFiles, int maxLines,
        double ratio, bool requirePriorRead = false, bool requirePostEditValidation = false) =>
        new(mode, DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny, maxFiles, maxLines, ratio,
            requirePriorRead, requireExpectedVersionToken: true, requirePostEditValidation,
            allowParallelMutations: false);

    /// <summary>Pipeline real con tools de mutación: sin frontera (semántica M2) o con la frontera
    /// de capacidad del modelo (ADR-0044 §5), que viaja junto al registro por-Run.</summary>
    private static ScriptedToolExecutor Pipeline(string ws, ModelCapabilityBoundary? boundary)
    {
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeMutationTools: true);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["filesystem.read"] = PermissionDecision.Allow,
            ["filesystem.write"] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        return ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, ws, boundary);
    }

    private static string WriteArgs(string path, string content, string? expectedVersion = null)
    {
        var map = new Dictionary<string, string?>
        {
            ["path"] = path,
            ["content"] = content,
            ["expectedVersion"] = expectedVersion,
        };
        return JsonSerializer.Serialize(map);
    }

    private static string PatchArgs(string path, string expectedVersion, string oldText, string newText)
    {
        var map = new Dictionary<string, string?>
        {
            ["path"] = path,
            ["expectedVersion"] = expectedVersion,
            ["oldText"] = oldText,
            ["newText"] = newText,
        };
        return JsonSerializer.Serialize(map);
    }

    private static ValidatedToolCall WriteCall(string path, string content, string? expectedVersion = null) =>
        WriteCall(ToolCallId.New(), path, content, expectedVersion);

    private static ValidatedToolCall WriteCall(ToolCallId id, string path, string content,
        string? expectedVersion = null) =>
        new(id, new ToolId("filesystem.write"), "pc-write", WriteArgs(path, content, expectedVersion));

    private static ValidatedToolCall ReadCall(string path) =>
        new(ToolCallId.New(), new ToolId("filesystem.read"), "pc-read",
            "{\"path\":\"" + path + "\"}");

    private static ValidatedToolCall PatchCall(string path, string expectedVersion, string oldText,
        string newText) =>
        new(ToolCallId.New(), new ToolId("filesystem.patch"), "pc-patch",
            PatchArgs(path, expectedVersion, oldText, newText));

    // ==================================================================== 1. Creación

    [Fact]
    public void Create_new_file_writes_content_utf8_no_bom_and_returns_version_token()
    {
        var ws = TempDir();
        try
        {
            var content = "hola\nmundo\n";
            var executor = Pipeline(ws, null);
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("nuevo.txt", content), true,
                TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.Equal(ToolCallState.Succeeded, outcome.FinalState);
            Assert.Equal(EffectOutcome.Applied, outcome.Effect);
            Assert.True(File.Exists(Path.Combine(ws, "nuevo.txt")), "el archivo debe existir");
            Assert.Equal(content, File.ReadAllText(Path.Combine(ws, "nuevo.txt")));
            // UTF-8 sin BOM (encoding de creación, ADR-0044 §5): el primer byte es contenido, no EF BB BF.
            var bytes = File.ReadAllBytes(Path.Combine(ws, "nuevo.txt"));
            Assert.Equal((byte)'h', bytes[0]);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "la creación no debe emitir BOM");
            Assert.Contains("[version:" + VersionOf(content) + "]", outcome.Summary);
            Assert.Contains("Archivo creado", outcome.Summary);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Create_builds_missing_parent_directories()
    {
        var ws = TempDir();
        try
        {
            var executor = Pipeline(ws, null);
            var outcome = executor.ExecuteToolWithoutJournal(
                WriteCall("src/nuevo/Modulo.cs", "class M {}\n"), true,
                TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.True(File.Exists(Path.Combine(ws, "src/nuevo/Modulo.cs")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Create_empty_file_is_allowed()
    {
        var ws = TempDir();
        try
        {
            var executor = Pipeline(ws, null);
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("vacio.txt", ""), true,
                TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.Equal(0, new FileInfo(Path.Combine(ws, "vacio.txt")).Length);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Write_to_directory_path_is_rejected()
    {
        var ws = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(ws, "carpeta"));
            var executor = Pipeline(ws, null);
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("carpeta", "x"), true,
                TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded);
            Assert.Contains("directorio", outcome.Summary);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Create_when_destination_appears_concurrently_is_refused_without_overwriting()
    {
        var ws = TempDir();
        try
        {
            var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(),
                includeMutationTools: true);
            // La costura de test simula la carrera TOCTOU: el archivo aparece DESPUÉS de escribir
            // el temporal y ANTES de publicar. Con overwrite:false el Move falla y NO se pisa.
            var writeTool = (FilesystemWriteTool)hostTools.Catalog().Find(new ToolId("filesystem.write"))!;
            writeTool.TestFailureHook = (_, full) => File.WriteAllText(full, "concurrente");
            var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
                ScriptedPermissionPolicy.WithTool("filesystem.write", PermissionDecision.Allow), ws);
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("carrera.txt", "nuestro"), true,
                TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded, "el write concurrente no debe publicarse");
            Assert.Equal("concurrente", File.ReadAllText(Path.Combine(ws, "carrera.txt")));
            Assert.False(Directory.EnumerateFiles(ws, "*.tmp-*").Any(), "sin temporales huérfanos");
        }
        finally
        {
            RmDir(ws);
        }
    }

    // ==================================================================== 2. Reemplazo

    [Fact]
    public void Replace_with_correct_token_replaces_content_and_preserves_encoding()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        var updated = "reescrito\ncompleto\n";
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original, Encoding.Unicode); // UTF-16 LE con BOM
        try
        {
            var token = VersionOfBytes(File.ReadAllBytes(Path.Combine(ws, "doc.txt")));
            var executor = Pipeline(ws, null);
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("doc.txt", updated, token), true,
                TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            var bytes = File.ReadAllBytes(Path.Combine(ws, "doc.txt"));
            // BOM UTF-16 LE conservado + contenido re-codificado en el mismo encoding.
            var expected = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(updated)).ToArray();
            Assert.Equal(expected, bytes);
            Assert.Contains("[version:" + VersionOfBytes(bytes) + "]", outcome.Summary);
            Assert.Contains("Archivo reemplazado", outcome.Summary);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Replace_with_wrong_token_is_stale_write_and_file_stays_unchanged()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            var wrongToken = VersionOf("otra cosa");
            var executor = Pipeline(ws, null);
            var outcome = executor.ExecuteToolWithoutJournal(
                WriteCall("doc.txt", "nuevo contenido\n", wrongToken), true,
                TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded);
            Assert.Equal(ToolCallState.Failed, outcome.FinalState);
            Assert.StartsWith("STALE_WRITE:", outcome.Summary);
            // Mismo formato que filesystem.patch: el token vigente viaja [version:…] para releer.
            Assert.Contains("[version:" + VersionOf(original) + "]", outcome.Summary);
            Assert.Equal(original, File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Replace_without_token_is_rejected()
    {
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), "x\n");
        try
        {
            var executor = Pipeline(ws, null);
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("doc.txt", "y\n"), true,
                TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded);
            Assert.Contains("expectedVersion", outcome.Summary);
            Assert.Equal("x\n", File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Replace_with_identical_content_is_rejected_as_noop()
    {
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), "igual\n");
        try
        {
            var token = VersionOf("igual\n");
            var executor = Pipeline(ws, null);
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("doc.txt", "igual\n", token), true,
                TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded);
            Assert.Contains("idéntico", outcome.Summary);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Replace_emptying_existing_file_is_rejected()
    {
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), "contenido\n");
        try
        {
            var token = VersionOf("contenido\n");
            var executor = Pipeline(ws, null);
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("doc.txt", "", token), true,
                TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded);
            Assert.Contains("vaciar", outcome.Summary);
            Assert.Equal("contenido\n", File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    // ==================================================================== 3. Modo de mutación (frontera + tool)

    [Fact]
    public void Write_visibility_follows_the_mutation_mode()
    {
        Assert.False(new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.ObserveOnly))
            .IsToolVisible("filesystem.write"));
        Assert.False(new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.PatchOnly))
            .IsToolVisible("filesystem.write"), "PatchOnly no expone filesystem.write");
        Assert.True(new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.ScopedCoder))
            .IsToolVisible("filesystem.write"), "ScopedCoder expone filesystem.write create-only");
        Assert.True(new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.FullAgent))
            .IsToolVisible("filesystem.write"));
    }

    [Fact]
    public void PatchOnly_rejects_write_intent_at_the_capability_boundary()
    {
        var ws = TempDir();
        try
        {
            var executor = Pipeline(ws, new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.PatchOnly)));
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("nuevo.txt", "contenido\n"), true,
                TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded);
            Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
            Assert.Contains("filesystem.patch", outcome.Summary);
            Assert.False(File.Exists(Path.Combine(ws, "nuevo.txt")));
            Assert.Contains(outcome.Events, e => e is ToolCallRejected);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void ScopedCoder_write_is_create_only_replacing_existing_is_refused()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            var boundary = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.ScopedCoder));
            var executor = Pipeline(ws, boundary);

            // Lectura previa (exigida por ScopedCoder): satisfecha para que el flujo llegue al
            // rechazo de MODO, no al de lectura previa.
            var read = executor.ExecuteToolWithoutJournal(ReadCall("doc.txt"), true,
                TestContext.Current.CancellationToken);
            Assert.True(read.Succeeded, "summary=" + read.Summary);

            // La frontera pasa el intento como CreateFile (create-only); el archivo EXISTE, así
            // que la tool lo rechaza: MUTATION_REFUSED tipado y archivo intacto.
            var token = VersionOf(original);
            var outcome = executor.ExecuteToolWithoutJournal(
                WriteCall("doc.txt", "reemplazo completo\n", token), true,
                TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.Equal(ToolCallState.Failed, outcome.FinalState);
            Assert.StartsWith("MUTATION_REFUSED:", outcome.Summary);
            Assert.Equal(original, File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void ScopedCoder_write_creates_new_file()
    {
        var ws = TempDir();
        try
        {
            var boundary = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.ScopedCoder));
            var executor = Pipeline(ws, boundary);
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("nuevo.txt", "creado\n"), true,
                TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.Equal("creado\n", File.ReadAllText(Path.Combine(ws, "nuevo.txt")));
            Assert.Equal(1, boundary.ReadRegistry().Ledger.FilesTouchedThisTurn);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void FullAgent_write_replaces_existing_file_with_prior_read()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            var boundary = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.FullAgent));
            var executor = Pipeline(ws, boundary);

            var read = executor.ExecuteToolWithoutJournal(ReadCall("doc.txt"), true,
                TestContext.Current.CancellationToken);
            Assert.True(read.Succeeded, "summary=" + read.Summary);
            var token = VersionOf(original);

            var outcome = executor.ExecuteToolWithoutJournal(
                WriteCall("doc.txt", "reescritura\ncompleta\n", token), true,
                TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.Equal("reescritura\ncompleta\n", File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Ledger_refuses_replace_and_create_in_modes_that_do_not_allow_them()
    {
        // Defensa en profundidad a nivel de contabilidad (sin frontera): las mismas reglas de
        // modo que la frontera, aplicables cuando solo el registry viaja al contexto.
        var patchOnly = Mutation(FileMutationMode.PatchExisting, 2, 200, 0.25);
        var registry = new FileReadRegistry();
        registry.Ledger.Bind(patchOnly);

        Assert.StartsWith("MUTATION_REFUSED:",
            registry.Ledger.RefuseMutation(ModelToolCapability.ReplaceFile, "a.txt", true, 1, 1, 8));
        Assert.StartsWith("MUTATION_REFUSED:",
            registry.Ledger.RefuseMutation(ModelToolCapability.CreateFile, "a.txt", false, 0, 1, 0));
        Assert.Null(registry.Ledger.RefuseMutation(ModelToolCapability.PatchExisting, "a.txt", true, 1, 1, 8));

        var scoped = Mutation(FileMutationMode.PatchAndCreate, 5, 600, 0.50);
        var registry2 = new FileReadRegistry();
        registry2.Ledger.Bind(scoped);
        Assert.Null(registry2.Ledger.RefuseMutation(ModelToolCapability.CreateFile, "a.txt", false, 0, 1, 0));
        Assert.StartsWith("MUTATION_REFUSED:",
            registry2.Ledger.RefuseMutation(ModelToolCapability.ReplaceFile, "a.txt", true, 1, 1, 8));

        // Sin política vinculada esta capa no restringe (manda el techo de la frontera).
        var registry3 = new FileReadRegistry();
        Assert.Null(registry3.Ledger.RefuseMutation(ModelToolCapability.ReplaceFile, "a.txt", true, 8, 8, 8));
    }

    // ==================================================================== 4. Lectura previa (RequirePriorRead)

    [Fact]
    public void Replace_without_prior_read_is_refused_and_file_stays_unchanged()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            var boundary = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.FullAgent));
            var executor = Pipeline(ws, boundary);
            var token = VersionOf(original);

            // Token correcto PERO sin lectura previa en este Run → PRIOR_READ_REQUIRED.
            var outcome = executor.ExecuteToolWithoutJournal(
                WriteCall("doc.txt", "reemplazo\n", token), true, TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.StartsWith("PRIOR_READ_REQUIRED:", outcome.Summary);
            Assert.Equal(original, File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void RequirePriorRead_false_allows_replace_without_prior_read()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            // El MISMO escenario, con la política que desactiva la exigencia → el valor cambia el
            // comportamiento (ADR-0044 §5, RequirePriorRead).
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, 10, 1000, 1.0, requirePriorRead: false)));
            var executor = Pipeline(ws, boundary);
            var token = VersionOf(original);

            var outcome = executor.ExecuteToolWithoutJournal(
                WriteCall("doc.txt", "reemplazo sin lectura\n", token), true,
                TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.Equal("reemplazo sin lectura\n", File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void RequirePriorRead_false_allows_patch_without_prior_read()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, 10, 1000, 1.0, requirePriorRead: false)));
            var executor = Pipeline(ws, boundary);
            var token = VersionOf(original);

            var outcome = executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", token, "linea-8", "linea-8-X"), true, TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.Contains("linea-8-X", File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    // ==================================================================== 5. Límites por Turn (ADR-0044 §5)

    [Fact]
    public void MaxFilesPerTurn_refuses_third_file_and_resets_next_turn()
    {
        var ws = TempDir();
        try
        {
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, maxFiles: 2, maxLines: 100000, ratio: 1.0)));
            var executor = Pipeline(ws, boundary);

            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("a.txt", "a\n"), true,
                TestContext.Current.CancellationToken).Succeeded);
            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("b.txt", "b\n"), true,
                TestContext.Current.CancellationToken).Succeeded);
            var ledger = boundary.ReadRegistry().Ledger;
            Assert.Equal(2, ledger.FilesTouchedThisTurn);

            var third = executor.ExecuteToolWithoutJournal(WriteCall("c.txt", "c\n"), true,
                TestContext.Current.CancellationToken);
            Assert.False(third.Succeeded);
            Assert.StartsWith("LIMIT_EXCEEDED:", third.Summary);
            Assert.Contains("por Turn", third.Summary);
            Assert.False(File.Exists(Path.Combine(ws, "c.txt")), "el rechazo ocurre ANTES de escribir");

            // Un nuevo Turn reinicia el presupuesto por Turn, no los totales por Run.
            boundary.BeginTurn();
            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("c.txt", "c\n"), true,
                TestContext.Current.CancellationToken).Succeeded);
            Assert.Equal(1, ledger.FilesTouchedThisTurn);
            Assert.Equal(3, ledger.FilesTouchedThisRun);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void MaxFilesPerTurn_value_changes_behaviour()
    {
        var ws = TempDir();
        try
        {
            // Misma secuencia con un valor mayor: las tres creaciones pasan.
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, maxFiles: 3, maxLines: 100000, ratio: 1.0)));
            var executor = Pipeline(ws, boundary);

            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("a.txt", "a\n"), true,
                TestContext.Current.CancellationToken).Succeeded);
            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("b.txt", "b\n"), true,
                TestContext.Current.CancellationToken).Succeeded);
            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("c.txt", "c\n"), true,
                TestContext.Current.CancellationToken).Succeeded);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void MaxChangedLinesPerTurn_accumulates_across_write_and_patch_and_resets_per_turn()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            // Presupuesto de 10 líneas por Turn: creación de 6 líneas + dos patches de 2 líneas.
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, maxFiles: 100, maxLines: 10, ratio: 1.0)));
            var executor = Pipeline(ws, boundary);
            var ledger = boundary.ReadRegistry().Ledger;

            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("nuevo.txt", "l1\nl2\nl3\nl4\nl5\nl6\n"),
                true, TestContext.Current.CancellationToken).Succeeded);
            Assert.Equal(6, ledger.ChangedLinesThisTurn);

            var token = VersionOf(original);
            Assert.True(executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", token, "linea-8", "linea-8-X"), true,
                TestContext.Current.CancellationToken).Succeeded, "6+2 <= 10");
            Assert.Equal(8, ledger.ChangedLinesThisTurn);

            // Tercera mutación: 8+2 = 10 == limite, cabe justo.
            Assert.True(executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", VersionOf(File.ReadAllText(Path.Combine(ws, "doc.txt"))),
                    "linea-7", "linea-7-X"), true, TestContext.Current.CancellationToken).Succeeded);

            // Cuarta: 10+2 > 10 → rechazo tipado ANTES de escribir; sin gasto de presupuesto.
            var fourth = executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", VersionOf(File.ReadAllText(Path.Combine(ws, "doc.txt"))),
                    "linea-6", "linea-6-X"), true, TestContext.Current.CancellationToken);
            Assert.False(fourth.Succeeded);
            Assert.StartsWith("LIMIT_EXCEEDED:", fourth.Summary);
            Assert.Contains("líneas", fourth.Summary);
            Assert.DoesNotContain("linea-6-X", File.ReadAllText(Path.Combine(ws, "doc.txt")));

            // Nuevo Turn: el presupuesto de líneas se reinicia (los totales por Run no).
            var runLines = ledger.ChangedLinesThisRun;
            boundary.BeginTurn();
            Assert.True(executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", VersionOf(File.ReadAllText(Path.Combine(ws, "doc.txt"))),
                    "linea-6", "linea-6-X"), true, TestContext.Current.CancellationToken).Succeeded);
            Assert.Equal(2, ledger.ChangedLinesThisTurn);
            Assert.Equal(runLines + 2, ledger.ChangedLinesThisRun);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void MaxChangedLinesPerTurn_value_changes_behaviour()
    {
        var ws = TempDir();
        try
        {
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, maxFiles: 100, maxLines: 20, ratio: 1.0)));
            var executor = Pipeline(ws, boundary);

            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("nuevo.txt", "l1\nl2\nl3\nl4\nl5\nl6\n"),
                true, TestContext.Current.CancellationToken).Succeeded);
            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("otro.txt", "l1\nl2\nl3\nl4\nl5\nl6\n"),
                true, TestContext.Current.CancellationToken).Succeeded, "12 <= 20 con un valor más alto");
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void MaxRewriteRatio_refuses_rewriting_too_much_without_spending_budget()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            // Reescritura de 3 de 8 líneas (0.375) con ratio 0.25 → LIMIT_EXCEEDED tipado.
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, maxFiles: 100, maxLines: 100000, ratio: 0.25)));
            var executor = Pipeline(ws, boundary);
            var ledger = boundary.ReadRegistry().Ledger;
            var token = VersionOf(original);

            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("doc.txt",
                "nueva-1\nnueva-2\nnueva-3\nlinea-4\nlinea-5\nlinea-6\nlinea-7\nlinea-8\n", token), true,
                TestContext.Current.CancellationToken);

            Assert.False(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.StartsWith("LIMIT_EXCEEDED:", outcome.Summary);
            Assert.Contains("reescribir", outcome.Summary);
            Assert.Equal(original, File.ReadAllText(Path.Combine(ws, "doc.txt"))); // archivo intacto
            // Un rechazo no gasta presupuesto: la contabilidad queda en cero.
            Assert.Equal(0, ledger.ChangedLinesThisTurn);
            Assert.Equal(0, ledger.FilesTouchedThisTurn);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void MaxRewriteRatio_value_changes_behaviour()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            // La MISMA reescritura de 3/8 con ratio 0.5 pasa.
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, maxFiles: 100, maxLines: 100000, ratio: 0.5)));
            var executor = Pipeline(ws, boundary);
            var token = VersionOf(original);

            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("doc.txt",
                "nueva-1\nnueva-2\nnueva-3\nlinea-4\nlinea-5\nlinea-6\nlinea-7\nlinea-8\n", token), true,
                TestContext.Current.CancellationToken);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            Assert.Contains("nueva-1", File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void MaxRewriteRatio_counts_rewrite_not_insertion()
    {
        var ws = TempDir();
        var original = "linea-1\nlinea-2\nlinea-3\nlinea-4\n";
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            // Ratio 0: solo se permite lo que NO reescribe líneas existentes. Un patch que INSERTA
            // (0 borradas) pasa; un patch que EDITA una línea (1/4 = 0.25 > 0) se rechaza.
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, maxFiles: 100, maxLines: 100000, ratio: 0.0)));
            var executor = Pipeline(ws, boundary);
            var token = VersionOf(original);

            var insert = executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", token, "linea-2", "linea-2\ninsertada"), true,
                TestContext.Current.CancellationToken);
            Assert.True(insert.Succeeded, "una inserción no reescribe contenido previo. summary=" + insert.Summary);

            var edit = executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", VersionOf(File.ReadAllText(Path.Combine(ws, "doc.txt"))),
                    "linea-3", "linea-3-EDITADA"), true, TestContext.Current.CancellationToken);
            Assert.False(edit.Succeeded);
            Assert.StartsWith("LIMIT_EXCEEDED:", edit.Summary);
        }
        finally
        {
            RmDir(ws);
        }
    }

    // ==================================================================== 6. Validación post-edición

    [Fact]
    public void RequirePostEditValidation_records_pending_validation_for_each_mutation()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            var boundary = new ModelCapabilityBoundary(CustomEffective(
                Mutation(FileMutationMode.Full, 100, 100000, 1.0, requirePriorRead: false,
                    requirePostEditValidation: true)));
            var executor = Pipeline(ws, boundary);
            var ledger = boundary.ReadRegistry().Ledger;

            var writeCallId = ToolCallId.New();
            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall(writeCallId, "nuevo.txt", "x\n"), true,
                TestContext.Current.CancellationToken).Succeeded);
            Assert.True(executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", VersionOf(original), "linea-8", "linea-8-X"), true,
                TestContext.Current.CancellationToken).Succeeded);

            var pending = ledger.PendingValidations();
            Assert.Equal(2, pending.Count);
            Assert.Contains(pending, p => p.Path == "nuevo.txt" && p.ToolCallId == writeCallId);
            Assert.Contains(pending, p => p.Path == "doc.txt");

            // El consumidor (Coder/gates) las retira: quedan registradas exactamente una vez.
            var taken = ledger.TakePendingValidations();
            Assert.Equal(2, taken.Count);
            Assert.Empty(ledger.PendingValidations());
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void RequirePostEditValidation_false_records_nothing()
    {
        var ws = TempDir();
        try
        {
            var boundary = new ModelCapabilityBoundary(CustomEffective(
                Mutation(FileMutationMode.Full, 100, 100000, 1.0, requirePostEditValidation: false)));
            var executor = Pipeline(ws, boundary);
            var ledger = boundary.ReadRegistry().Ledger;

            Assert.True(executor.ExecuteToolWithoutJournal(WriteCall("nuevo.txt", "x\n"), true,
                TestContext.Current.CancellationToken).Succeeded);
            Assert.Empty(ledger.PendingValidations());
        }
        finally
        {
            RmDir(ws);
        }
    }

    // ==================================================================== 7. Semántica de líneas del diff

    [Fact]
    public void FileVersion_changed_lines_semantics()
    {
        FileVersion.ChangedLines("a\nb\nc\n", "a\nb\nc\n", out var d0, out var i0);
        Assert.Equal((0, 0), (d0, i0)); // sin cambios

        FileVersion.ChangedLines("a\nb\nc\n", "a\nB\nc\n", out var d1, out var i1);
        Assert.Equal((1, 1), (d1, i1)); // una línea editada

        FileVersion.ChangedLines("a\nb\n", "a\nb\nc\nd\n", out var d2, out var i2);
        Assert.Equal((0, 2), (d2, i2)); // inserción pura

        FileVersion.ChangedLines("a\nb\nc\n", "a\n", out var d3, out var i3);
        Assert.Equal((2, 0), (d3, i3)); // borrado puro

        FileVersion.ChangedLines("a\nb\nc\n", "c\nb\na\n", out var d4, out var i4);
        Assert.Equal((0, 0), (d4, i4)); // líneas movidas: sobreviven (no se reescribió contenido)

        FileVersion.ChangedLines("a\na\nb\n", "a\nb\n", out var d5, out var i5);
        Assert.Equal((1, 0), (d5, i5)); // una repetición menos

        Assert.Equal(0, FileVersion.CountLines(""));
        Assert.Equal(1, FileVersion.CountLines("solo"));
        Assert.Equal(2, FileVersion.CountLines("una\ndos"));
        Assert.Equal(1, FileVersion.CountLines("una\n")); // el salto final termina la línea: 1
        Assert.Equal(1, FileVersion.CountLines("\n")); // una línea vacía
    }

    // ==================================================================== 8. Journal: metadatos + replay limpio

    [Fact]
    public void Write_pipeline_persists_canonical_reconciliation_metadata_and_replays_clean()
    {
        var root = TempDir();
        var ws = Path.Combine(root, "ws");
        Directory.CreateDirectory(ws);
        var storePath = Path.Combine(root, "journal.db");
        try
        {
            var store = new SqliteEventStore(storePath);
            var codecs = EventCodecs.Create();
            var sessionId = SessionId.New();
            var runId = RunId.New();
            var stream = new EventStream(store, codecs, sessionId);
            stream.Append(new RunCreated(runId, sessionId, "escribir archivos", RunMode.Act,
                ExecutionStrategy.Direct, FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null),
                TaskId.New(), DateTimeOffset.Now));
            stream.Append(new RunStarted(runId));

            var executor = Pipeline(ws, null);
            var content = "linea-1\nlinea-2\n";

            // El Engine persiste la cola (post-Started) cuando la tool termina: lo replicamos.
            void Persist(ToolOutcome o)
            {
                stream.AppendBatch(o.Events, DurabilityClass.Standard);
            }

            // 1) Creación vía filesystem.write en el pipeline real con journal (Barrier en Started).
            var createId = ToolCallId.New();
            var create = executor.ExecuteTool(WriteCall(createId, "nuevo.txt", content), true,
                TestContext.Current.CancellationToken, stream);
            Assert.True(create.Succeeded, "summary=" + create.Summary);
            Persist(create);

            // 2) Reemplazo de un archivo existente (token correcto, sin frontera = M2).
            File.WriteAllText(Path.Combine(ws, "doc.txt"), "original\n");
            var replaceId = ToolCallId.New();
            var replace = executor.ExecuteTool(WriteCall(replaceId, "doc.txt", "reemplazado\n",
                VersionOf("original\n")), true, TestContext.Current.CancellationToken, stream);
            Assert.True(replace.Succeeded, "summary=" + replace.Summary);
            Persist(replace);

            // 3) Un write FALLADO (STALE_WRITE) también viaja por el journal.
            var staleId = ToolCallId.New();
            var stale = executor.ExecuteTool(WriteCall(staleId, "doc.txt", "x\n", VersionOf("otro\n")), true,
                TestContext.Current.CancellationToken, stream);
            Assert.False(stale.Succeeded);
            Persist(stale);

            // Metadatos canónicos en el ToolCallStarted de la CREACIÓN: pre = centinela absent.
            var events = store.ReadFrom(sessionId, 1);
            var tracker = CanonicalStateTracker.Replay(codecs, events); // replay limpio: no lanza
            Assert.Equal(ToolCallState.Succeeded, tracker.ToolCall(createId));
            Assert.Equal(ToolCallState.Succeeded, tracker.ToolCall(replaceId));
            Assert.Equal(ToolCallState.Failed, tracker.ToolCall(staleId));

            foreach (var evt in events)
            {
                if (codecs.Decode(evt) is ToolCallStarted started && started.ToolCallId == createId)
                {
                    var meta = FilesystemReconciliationMetadata.Parse(started.ReconciliationJson);
                    Assert.False(meta is null, "metadatos canónicos de la creación");
                    Assert.Equal("nuevo.txt", meta!.Path);
                    Assert.Equal(FilesystemReconciliationMetadata.AbsentPreHash, meta.ExpectedPreHash);
                    Assert.Equal(VersionOf(content), meta.ExpectedPostHash);
                }

                if (codecs.Decode(evt) is ToolCallStarted started2 && started2.ToolCallId == replaceId)
                {
                    var meta = FilesystemReconciliationMetadata.Parse(started2.ReconciliationJson);
                    Assert.False(meta is null);
                    Assert.Equal(VersionOf("original\n"), meta!.ExpectedPreHash);
                    Assert.Equal(VersionOf("reemplazado\n"), meta.ExpectedPostHash);
                }
            }

            store.Close();
        }
        finally
        {
            RmDir(root);
        }
    }

    // ==================================================================== 9. Crash entre Barrier y publicación → reconciliación

    /// <summary>Escribe en un SqliteEventStore el run + la cadena durable de una ToolCall
    /// Started-sin-outcome de filesystem.write (crash) con commit Barrier.</summary>
    private static void WriteCrashJournal(string storePath, SessionId sessionId, RunId runId, ToolCallId callId,
        string path, string argsJson, string? startedJson)
    {
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(storePath);
        try
        {
            var stream = new EventStream(store, codecs, sessionId);
            stream.Append(new RunCreated(runId, sessionId, "escribir un archivo", RunMode.Act,
                ExecutionStrategy.Direct, FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null),
                TaskId.New(), DateTimeOffset.Now));
            stream.Append(new RunStarted(runId));
            stream.Append(new ToolCallRequested(callId, "pc-1", "filesystem.write", argsJson));
            stream.Append(new ToolCallPrepared(callId, argsJson));
            stream.Append(new PermissionEvaluated(callId, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(callId));
            stream.Append(new ToolCallStarted(callId, EffectClass.NonIdempotent, startedJson), DurabilityClass.Barrier);
        }
        finally
        {
            store.Close();
        }
    }

    private static List<ToolCallReconciled> Reconciled(SqliteEventStore store, SessionId sessionId)
    {
        var codecs = EventCodecs.Create();
        var found = new List<ToolCallReconciled>();
        foreach (var evt in store.ReadFrom(sessionId, 1))
        {
            if (codecs.Decode(evt) is ToolCallReconciled r)
            {
                found.Add(r);
            }
        }

        return found;
    }

    private static int CountEvents(SqliteEventStore store, SessionId sessionId, string type)
    {
        var n = 0;
        foreach (var evt in store.ReadFrom(sessionId, 1))
        {
            if (evt.Type.ToString() == type)
            {
                n += 1;
            }
        }

        return n;
    }

    /// <summary>Escenario de crash entre el Barrier y la publicación de un filesystem.write, con
    /// clasificación esperada e idempotencia de reanudar dos veces (SQLite cerrado/reabierto).</summary>
    private static void RunCrashClassification(string caseName, string content, string argsJson,
        string? startedJson, Action<string> arrangeWorkspace, ReconciliationOutcome expected)
    {
        var root = TempDir();
        var ws = Path.Combine(root, "ws");
        Directory.CreateDirectory(ws);
        var storePath = Path.Combine(root, "journal.db");
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var callId = ToolCallId.New();
        try
        {
            WriteCrashJournal(storePath, sessionId, runId, callId, "nuevo.txt", argsJson, startedJson);
            arrangeWorkspace(ws);

            var store = new SqliteEventStore(storePath);
            var engine = new SimulationEngine(store, EventCodecs.Create(), new InMemoryAuditSink(),
                ScriptedToolExecutor.Default(), new FilesystemReconciler(new PathBoundaryValidator()), ws);
            var stream2 = new EventStream(store, EventCodecs.Create(), sessionId);
            Assert.Equal(1, engine.Resume(sessionId, runId, stream2, ws));

            var results = Reconciled(store, sessionId);
            Assert.Single(results);
            Assert.Equal(callId, results[0].ToolCallId);
            Assert.Equal(expected, results[0].Outcome);
            Assert.Equal(1, CountEvents(store, sessionId, "toolcall.effect_unknown"));
            Assert.Equal(0, CountEvents(store, sessionId, "toolcall.succeeded"));
            store.Close();

            // Idempotencia: reanudar de nuevo no duplica eventos ni reconcilia nada nuevo.
            var store2 = new SqliteEventStore(storePath);
            var engine2 = new SimulationEngine(store2, EventCodecs.Create(), new InMemoryAuditSink(),
                ScriptedToolExecutor.Default(), new FilesystemReconciler(new PathBoundaryValidator()), ws);
            var stream3 = new EventStream(store2, EventCodecs.Create(), sessionId);
            Assert.Equal(0, engine2.Resume(sessionId, runId, stream3, ws));
            Assert.Equal(1, CountEvents(store2, sessionId, "toolcall.reconciled"));
            store2.Close();
        }
        finally
        {
            RmDir(root);
        }
    }

    [Fact]
    public void Crash_between_barrier_and_create_write_reconciles_not_applied_without_duplicate()
    {
        var content = "archivo nuevo\n";
        var post = VersionOf(content);
        var args = WriteArgs("nuevo.txt", content);
        var started = FilesystemReconciliationMetadata.Encode("nuevo.txt",
            FilesystemReconciliationMetadata.AbsentPreHash, post);

        // El archivo NO existe: el create no llegó a aplicarse → NotApplied (reintentable).
        RunCrashClassification("create-not-applied", content, args, started, _ => { },
            ReconciliationOutcome.NotApplied);
    }

    [Fact]
    public void Crash_after_create_write_applied_reconciles_applied_without_duplicate()
    {
        var content = "archivo nuevo\n";
        var post = VersionOf(content);
        var args = WriteArgs("nuevo.txt", content);
        var started = FilesystemReconciliationMetadata.Encode("nuevo.txt",
            FilesystemReconciliationMetadata.AbsentPreHash, post);

        // El archivo existe con el post-hash: la publicación llegó a completarse → Applied,
        // y el resume NO lo re-escribe (solo clasifica, ADR-0004 §4).
        RunCrashClassification("create-applied", content, args, started,
            ws => File.WriteAllText(Path.Combine(ws, "nuevo.txt"), content),
            ReconciliationOutcome.Applied);
    }

    [Fact]
    public void Crash_between_barrier_and_replace_write_reconciles_applied_or_not_applied()
    {
        var original = "linea-uno\nlinea-dos\n";
        var updated = "reescritura completa\n";
        var args = WriteArgs("doc.txt", updated, VersionOf(original));
        var started = FilesystemReconciliationMetadata.Encode("doc.txt", VersionOf(original), VersionOf(updated));

        // El archivo quedó en el contenido nuevo → Applied (sin re-ejecutar).
        RunCrashClassification("replace-applied", updated, args, started,
            ws => File.WriteAllText(Path.Combine(ws, "doc.txt"), updated),
            ReconciliationOutcome.Applied);

        // El archivo sigue en su estado previo (pre-hash) → NotApplied.
        RunCrashClassification("replace-not-applied", updated, args, started,
            ws => File.WriteAllText(Path.Combine(ws, "doc.txt"), original),
            ReconciliationOutcome.NotApplied);
    }

    [Fact]
    public void Crash_of_write_without_metadata_fails_closed_unresolvable()
    {
        var content = "archivo nuevo\n";
        var args = WriteArgs("nuevo.txt", content);
        // Sin metadatos en el Started: falla cerrado (nunca Applied).
        RunCrashClassification("no-meta", content, args, null, _ => { },
            ReconciliationOutcome.Unresolvable);
    }
}
