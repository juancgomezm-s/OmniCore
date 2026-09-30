using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Sandbox;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Deterministas de los códigos de error tipados de tool (spec §71, M3): cada código bien
/// conocido lo fija SU productor (la tool o el ToolRuntime, nunca un parseo del texto visible
/// al modelo), el summary mantiene su texto estable y el código viaja aparte en el
/// <c>ToolResult</c> y en los eventos de fallo <c>ToolCallRejected</c>/<c>ToolCallFailed</c>
/// (v2, campo opcional). Cubre: validación del tipo, un productor por código, el round-trip
/// del código por el journal real (SQLite cerrado y reabierto, sobre el JSON persistido), el
/// decode de eventos v1 sin el campo (upcaster identidad → ErrorCode null) y los productores
/// de fallo que quedaban: Prepare que lanza, rechazo de Prepare sin código (normalizado),
/// interrupción de una llamada Started (RunControlService) y cierre por recuperación de una
/// lectura interrumpida (RunResumeService) — todos con el código persistido en el journal.
/// </summary>
public sealed class ToolErrorCodeTests
{
    // ---------------------------------------------------------------------- helpers

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-errorcodes", Guid.NewGuid().ToString("N"));
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

    private static string EightLineFile()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 8; i++) sb.Append("linea-").Append(i).Append('\n');
        return sb.ToString();
    }

    private static EffectiveModelPolicy EffectiveFor(ModelPolicyCategory category)
    {
        var key = ModelPolicyKey.For("p", "m");
        return EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, ModelPolicyPresets.For(category),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
                PlanControl.ModelDriven, 8));
    }

    private static FileMutationPolicy Mutation(FileMutationMode mode, int maxFiles, int maxLines, double ratio,
        bool requirePriorRead = false) =>
        new(mode, DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny, maxFiles, maxLines, ratio,
            requirePriorRead, requireExpectedVersionToken: true, requirePostEditValidation: false,
            allowParallelMutations: false);

    /// <summary>Política Custom con techo completo de mutación, para variar solo la política de mutación.</summary>
    private static EffectiveModelPolicy CustomEffective(FileMutationPolicy mutation)
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
            new ModelToolPolicy(ToolMode.Direct, 12, false, caps), mutation, "test", null);
        return EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, user, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 12, GuidanceLevel.Full, 3,
                PlanControl.ModelDriven, 8));
    }

    private static ScriptedToolExecutor Pipeline(string ws, ModelCapabilityBoundary? boundary = null)
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

    private static ValidatedToolCall ReadCall(string path) =>
        new(ToolCallId.New(), new ToolId("filesystem.read"), "pc-read", "{\"path\":\"" + path + "\"}");

    private static ValidatedToolCall PatchCall(string path, string expectedVersion, string oldText, string newText) =>
        new(ToolCallId.New(), new ToolId("filesystem.patch"), "pc-patch",
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["path"] = path,
                ["expectedVersion"] = expectedVersion,
                ["oldText"] = oldText,
                ["newText"] = newText,
            }));

    private static ValidatedToolCall WriteCall(string path, string content, string? expectedVersion = null) =>
        new(ToolCallId.New(), new ToolId("filesystem.write"), "pc-write",
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["path"] = path,
                ["content"] = content,
                ["expectedVersion"] = expectedVersion,
            }));

    private static ToolCallRejected RejectedOf(IReadOnlyList<DomainEventPayload> events) =>
        Assert.Single(events.OfType<ToolCallRejected>());

    private static ToolCallFailed FailedOf(IReadOnlyList<DomainEventPayload> events) =>
        Assert.Single(events.OfType<ToolCallFailed>());

    // ------------------------------------------------------- 1. Validación del tipo

    [Fact]
    public void Code_value_rejects_malformed_codes()
    {
        Assert.Equal("STALE_WRITE", ToolErrorCode.Of("STALE_WRITE").Value);
        Assert.Equal(ToolErrorCode.StaleWrite, ToolErrorCode.Of("STALE_WRITE"));
        Assert.Equal(ToolErrorCode.StaleWrite, new ToolErrorCode("STALE_WRITE"));
        Assert.Throws<ArgumentException>(() => ToolErrorCode.Of(""));
        Assert.Throws<ArgumentException>(() => ToolErrorCode.Of(null!));
        Assert.Throws<ArgumentException>(() => ToolErrorCode.Of("stale_write")); // minúsculas
        Assert.Throws<ArgumentException>(() => ToolErrorCode.Of("1BAD")); // no empieza por letra
        Assert.Throws<ArgumentException>(() => ToolErrorCode.Of("BAD-CODE")); // guión
        Assert.Throws<ArgumentException>(() => ToolErrorCode.Of(new string('X', 65))); // > 64
    }

    [Fact]
    public void Well_known_codes_are_screaming_snake_case()
    {
        foreach (var code in new[]
                 {
                     ToolErrorCode.ToolFailure, ToolErrorCode.InvalidArguments, ToolErrorCode.PermissionDenied,
                     ToolErrorCode.ProcessFailure, ToolErrorCode.Cancellation, ToolErrorCode.CapabilityRefused,
                     ToolErrorCode.UnknownEffect, ToolErrorCode.UnknownTool, ToolErrorCode.ArtifactMissing,
                     ToolErrorCode.ArtifactCorrupted, ToolErrorCode.StaleWrite, ToolErrorCode.PriorReadRequired,
                     ToolErrorCode.LimitExceeded, ToolErrorCode.MutationRefused,
                 })
        {
            Assert.Matches("^[A-Z][A-Z0-9_]{0,63}$", code.Value);
        }
    }

    // --------------------------------------- 2. Un productor por código (Rejected)

    [Fact]
    public void Unknown_tool_yields_rejected_with_unknown_tool_code()
    {
        var events = new List<DomainEventPayload>();
        var runtime = new ToolRuntime(FakeCatalog.Default(),
            ScriptedPermissionPolicy.WithTool("nope.tool", PermissionDecision.Allow),
            payload => { events.Add(payload); return VoidBox.Instance; });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("nope.tool"), "p1", "{}");

        var outcome = runtime.Run(call, new ToolPreparationContext("ws", DateTimeOffset.Now),
            new ToolExecutionContext("ws"), false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        var rejected = RejectedOf(events);
        Assert.Equal(ToolErrorCode.UnknownTool, rejected.ErrorCode);
        Assert.Contains("nope.tool", rejected.Reason);
    }

    [Fact]
    public void Invalid_schema_arguments_yield_rejected_with_invalid_arguments_code()
    {
        var ws = TempDir();
        try
        {
            var executor = Pipeline(ws);
            // Falta oldText/newText: el schema de filesystem.patch lo exige antes de Prepare.
            var outcome = executor.ExecuteToolWithoutJournal(
                new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.patch"), "p1",
                    "{\"path\":\"doc.txt\",\"expectedVersion\":\"" + VersionOf("x") + "\"}"),
                true, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var rejected = RejectedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.InvalidArguments, rejected.ErrorCode);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Secret_path_read_yields_rejected_with_permission_denied_code()
    {
        var ws = TempDir();
        try
        {
            var executor = Pipeline(ws);
            var outcome = executor.ExecuteToolWithoutJournal(ReadCall(".env"), true, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var rejected = RejectedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.PermissionDenied, rejected.ErrorCode);
            Assert.Contains("secretos", rejected.Reason); // texto estable, sin el código dentro
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Missing_executable_yields_rejected_with_process_failure_code()
    {
        var ws = TempDir();
        try
        {
            var events = new List<DomainEventPayload>();
            var tool = new ProcessExecTool(new NeverLaunchingLauncher(), new PathBoundaryValidator(),
                SandboxStrength.Weak);
            var runtime = new ToolRuntime(new FakeCatalog().Add(tool),
                ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Allow),
                payload => { events.Add(payload); return VoidBox.Instance; }, null, new SystemExecutableResolver());
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    executable = "this-executable-does-not-exist-omnicore",
                    argv = Array.Empty<string>(),
                    cwd = ".",
                    timeoutSeconds = 5,
                }));

            var outcome = runtime.Run(call, new ToolPreparationContext(ws, DateTimeOffset.Now),
                new ToolExecutionContext(ws), false, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var rejected = RejectedOf(events);
            Assert.Equal(ToolErrorCode.ProcessFailure, rejected.ErrorCode);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Capability_boundary_yields_rejected_with_capability_refused_code()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            // ObserveOnly: la frontera corta filesystem.patch aunque la policy lo permita.
            var executor = Pipeline(ws, new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.ObserveOnly)));
            var outcome = executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", VersionOf(original), "linea-dos", "linea-dos-C"),
                true, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var rejected = RejectedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.CapabilityRefused, rejected.ErrorCode);
            Assert.Equal(original, File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Patch_only_boundary_rejects_write_intent_with_capability_refused_code()
    {
        var ws = TempDir();
        try
        {
            // PatchOnly no expone filesystem.write: rechazo de frontera (otro productor del código).
            var executor = Pipeline(ws, new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.PatchOnly)));
            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("nuevo.txt", "contenido\n"),
                true, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var rejected = RejectedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.CapabilityRefused, rejected.ErrorCode);
            Assert.False(File.Exists(Path.Combine(ws, "nuevo.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    // ---------------------------------------- 3. Un productor por código (Failed)

    [Fact]
    public void Nonexistent_file_read_yields_failed_with_invalid_arguments_code()
    {
        var ws = TempDir();
        try
        {
            var executor = Pipeline(ws);
            var outcome = executor.ExecuteToolWithoutJournal(ReadCall("missing.txt"), true,
                CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.InvalidArguments, failed.ErrorCode);
            Assert.Equal("Archivo no encontrado: missing.txt", failed.Cause); // texto byte-idéntico
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Weak_sandbox_consent_denied_yields_failed_with_permission_denied_code()
    {
        var ws = TempDir();
        try
        {
            var events = new List<DomainEventPayload>();
            var tool = new ProcessExecTool(new StrongUnavailableLauncher(), new PathBoundaryValidator(),
                SandboxStrength.Strong);
            var runtime = new ToolRuntime(new FakeCatalog().Add(tool),
                ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Allow),
                payload => { events.Add(payload); return VoidBox.Instance; }, null, new SystemExecutableResolver());
            var executable = OperatingSystem.IsWindows() ? "dotnet" : "/bin/echo";
            var argv = OperatingSystem.IsWindows() ? new[] { "--version" } : new[] { "x" };
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    executable,
                    argv,
                    cwd = ".",
                    timeoutSeconds = 5,
                }));
            // Sin cliente interactivo: el consentimiento Weak se deniega → PermissionDenied.
            var execContext = new ToolExecutionContext(ws, null, payload => events.Add(payload), null, null,
                false, new WeakSandboxConsentState());

            var outcome = runtime.Run(call, new ToolPreparationContext(ws, DateTimeOffset.Now),
                execContext, false, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(events);
            Assert.Equal(ToolErrorCode.PermissionDenied, failed.ErrorCode);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Cancelled_process_yields_failed_with_cancellation_code()
    {
        var ws = TempDir();
        try
        {
            var events = new List<DomainEventPayload>();
            var tool = new ProcessExecTool(new NeverLaunchingLauncher(), new PathBoundaryValidator(),
                SandboxStrength.Weak);
            var runtime = new ToolRuntime(new FakeCatalog().Add(tool),
                ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Allow),
                payload => { events.Add(payload); return VoidBox.Instance; }, null, new SystemExecutableResolver());
            var executable = OperatingSystem.IsWindows() ? "dotnet" : "/bin/echo";
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    executable,
                    argv = new[] { "--version" },
                    cwd = ".",
                    timeoutSeconds = 5,
                }));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var outcome = runtime.Run(call, new ToolPreparationContext(ws, DateTimeOffset.Now),
                new ToolExecutionContext(ws), false, cancelled.Token);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(events);
            Assert.Equal(ToolErrorCode.Cancellation, failed.ErrorCode);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Tool_throw_yields_failed_with_tool_failure_code()
    {
        var ws = TempDir();
        try
        {
            var events = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(new FakeCatalog().Add(new ExplodingTool()),
                ScriptedPermissionPolicy.WithTool("boom.none", PermissionDecision.Allow),
                payload => { events.Add(payload); return VoidBox.Instance; });

            var outcome = runtime.Run(
                new ValidatedToolCall(ToolCallId.New(), new ToolId("boom.none"), "p1", "{}"),
                new ToolPreparationContext(ws, DateTimeOffset.Now), new ToolExecutionContext(ws),
                false, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(events);
            Assert.Equal(ToolErrorCode.ToolFailure, failed.ErrorCode);
            Assert.Contains("InvalidOperationException", failed.Cause);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Unknown_effect_outcome_yields_failed_with_unknown_effect_code()
    {
        var ws = TempDir();
        try
        {
            var events = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(new FakeCatalog().Add(new UnknownOutcomeTool()),
                ScriptedPermissionPolicy.WithTool("mystery.read", PermissionDecision.Allow),
                payload => { events.Add(payload); return VoidBox.Instance; });

            var outcome = runtime.Run(
                new ValidatedToolCall(ToolCallId.New(), new ToolId("mystery.read"), "p1", "{}"),
                new ToolPreparationContext(ws, DateTimeOffset.Now), new ToolExecutionContext(ws),
                false, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(events);
            Assert.Equal(ToolErrorCode.UnknownEffect, failed.ErrorCode);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Stale_version_yields_failed_with_stale_write_code()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            var executor = Pipeline(ws, new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.FullAgent)));
            var read = executor.ExecuteToolWithoutJournal(ReadCall("doc.txt"), true, CancellationToken.None);
            Assert.True(read.Succeeded, read.Summary ?? "read falló");

            // Modificación EXTERNA: el token que devolvió esa lectura queda obsoleto, así que el
            // parche con el token de la lectura es STALE_WRITE (uno fabricado daría
            // PRIOR_READ_REQUIRED, cortado antes por la defensa de lectura previa).
            var external = original + "linea-extra-externa\n";
            File.WriteAllText(Path.Combine(ws, "doc.txt"), external);

            var outcome = executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", VersionOf(original), "linea-dos", "linea-dos-C"),
                true, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.StaleWrite, failed.ErrorCode);
            Assert.StartsWith("STALE_WRITE:", failed.Cause); // texto estable, byte-idéntico
            Assert.Equal(external, File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Missing_prior_read_yields_failed_with_prior_read_required_code()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            var executor = Pipeline(ws, new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.FullAgent)));
            var outcome = executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", VersionOf(original), "linea-dos", "linea-dos-C"),
                true, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.PriorReadRequired, failed.ErrorCode);
            Assert.StartsWith("PRIOR_READ_REQUIRED:", failed.Cause);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Turn_file_limit_yields_failed_with_limit_exceeded_code()
    {
        var ws = TempDir();
        try
        {
            // MaxFilesPerTurn = 1: el segundo archivo del mismo Turn se rechaza ANTES de escribir.
            var boundary = new ModelCapabilityBoundary(
                CustomEffective(Mutation(FileMutationMode.Full, maxFiles: 1, maxLines: 100000, ratio: 1.0)));
            var executor = Pipeline(ws, boundary);

            var first = executor.ExecuteToolWithoutJournal(WriteCall("a.txt", "a\n"), true, CancellationToken.None);
            Assert.True(first.Succeeded, first.Summary ?? "first");

            var outcome = executor.ExecuteToolWithoutJournal(WriteCall("b.txt", "b\n"), true,
                CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.LimitExceeded, failed.ErrorCode);
            Assert.StartsWith("LIMIT_EXCEEDED:", failed.Cause);
            Assert.False(File.Exists(Path.Combine(ws, "b.txt")), "el rechazo ocurre ANTES de escribir");
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Scoped_coder_replace_of_existing_file_yields_failed_with_mutation_refused_code()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        try
        {
            // ScopedCoder es create-only: pasa la frontera como CreateFile pero el archivo existe,
            // así que el ledger de la tool rechaza el reemplazo con MUTATION_REFUSED.
            var executor = Pipeline(ws, new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.ScopedCoder)));
            var read = executor.ExecuteToolWithoutJournal(ReadCall("doc.txt"), true, CancellationToken.None);
            Assert.True(read.Succeeded, read.Summary ?? "read falló");

            var outcome = executor.ExecuteToolWithoutJournal(
                WriteCall("doc.txt", "reemplazo completo\n", VersionOf(original)),
                true, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.MutationRefused, failed.ErrorCode);
            Assert.StartsWith("MUTATION_REFUSED:", failed.Cause);
            Assert.Equal(original, File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(ws);
        }
    }

    // --------------------------------------------------- 4. artifact.read (CAS)

    [Fact]
    public void Unreferenced_artifact_yields_failed_with_permission_denied_code()
    {
        var ws = TempDir();
        try
        {
            var outcome = RunArtifactRead(ws, new FakeArtifactStore(), referenced: false);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.PermissionDenied, failed.ErrorCode);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Missing_artifact_yields_failed_with_artifact_missing_code()
    {
        var ws = TempDir();
        try
        {
            var outcome = RunArtifactRead(ws, new FakeArtifactStore { OnGetText = _ => null }, referenced: true);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.ArtifactMissing, failed.ErrorCode);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Corrupted_artifact_yields_failed_with_artifact_corrupted_code()
    {
        var ws = TempDir();
        try
        {
            var store = new FakeArtifactStore
            {
                OnGetText = _ => throw new InvalidDataException("hash mismatch"),
            };
            var outcome = RunArtifactRead(ws, store, referenced: true);

            Assert.False(outcome.Succeeded);
            var failed = FailedOf(outcome.Events);
            Assert.Equal(ToolErrorCode.ArtifactCorrupted, failed.ErrorCode);
        }
        finally
        {
            RmDir(ws);
        }
    }

    private static ToolOutcome RunArtifactRead(string ws, IArtifactStore store, bool referenced)
    {
        var tool = new ArtifactReadTool(store, _ => referenced);
        var executor = ScriptedToolExecutor.WithWorkspace(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("artifact.read", PermissionDecision.Allow), ws);
        return executor.ExecuteToolWithoutJournal(
            new ValidatedToolCall(ToolCallId.New(), new ToolId("artifact.read"), "p1",
                "{\"hash\":\"" + new string('a', 64) + "\"}"),
            true, CancellationToken.None);
    }

    // ------------------------------------------ 5. Round-trip por el journal real

    [Fact]
    public void Codes_round_trip_through_the_journal()
    {
        var ws = TempDir();
        var original = EightLineFile();
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        var storePath = Path.Combine(ws, "journal.db");
        try
        {
            var sessionId = SessionId.New();
            var runId = RunId.New();
            var codecs = EventCodecs.Create();
            var store = new SqliteEventStore(storePath);
            var stream = new EventStream(store, codecs, sessionId);
            try
            {
                stream.Append(new RunCreated(runId, sessionId, "corregir un test", RunMode.Act,
                    ExecutionStrategy.Direct, FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null),
                    TaskId.New(), DateTimeOffset.Now));
                stream.Append(new RunStarted(runId));

                var executor = Pipeline(ws, new ModelCapabilityBoundary(
                    EffectiveFor(ModelPolicyCategory.FullAgent)));

                // 1) Lectura previa (satisface PRIOR_READ_REQUIRED) + modificación externa +
                //    parche con el token ya obsoleto → fallo STALE_WRITE con código.
                var read = executor.ExecuteTool(ReadCall("doc.txt"), true, CancellationToken.None, stream);
                foreach (var evt in read.Events) stream.Append(evt);
                var external = original + "linea-extra-externa\n";
                File.WriteAllText(Path.Combine(ws, "doc.txt"), external);
                var stale = executor.ExecuteTool(
                    PatchCall("doc.txt", VersionOf(original), "linea-dos", "linea-dos-C"),
                    true, CancellationToken.None, stream);
                foreach (var evt in stale.Events) stream.Append(evt);
                Assert.False(stale.Succeeded);

                // 2) Rechazo UNKNOWN_TOOL con código, vía el mismo stream.
                var rejected = executor.ExecuteTool(
                    new ValidatedToolCall(ToolCallId.New(), new ToolId("nope.tool"), "p1", "{}"),
                    true, CancellationToken.None, stream);
                foreach (var evt in rejected.Events) stream.Append(evt);
            }
            finally
            {
                store.Close();
            }

            // Reabrir el journal: el código persistió como campo tipado junto al texto estable.
            var reopened = new SqliteEventStore(storePath);
            try
            {
                var envelopes = reopened.ReadFrom(sessionId, 1).ToList();
                var failedEnvelope = Assert.Single(envelopes, e => e.Type.ToString() == "toolcall.failed");
                Assert.Equal(2, failedEnvelope.SchemaVersion);
                Assert.Contains("\"ErrorCode\":{\"Value\":\"STALE_WRITE\"}", failedEnvelope.PayloadJson);
                Assert.Contains("STALE_WRITE:", failedEnvelope.PayloadJson); // el texto viaja aparte

                var decoded = envelopes.Select(codecs.Decode).ToList();
                var failed = Assert.Single(decoded.OfType<ToolCallFailed>());
                Assert.Equal(ToolErrorCode.StaleWrite, failed.ErrorCode);
                Assert.StartsWith("STALE_WRITE:", failed.Cause);

                var rejection = Assert.Single(decoded.OfType<ToolCallRejected>());
                Assert.Equal(ToolErrorCode.UnknownTool, rejection.ErrorCode);
            }
            finally
            {
                reopened.Close();
            }
        }
        finally
        {
            RmDir(ws);
        }
    }

    // ---------------------- 5b. Fallos restantes: Prepare, cancel y recuperación

    [Fact]
    public void Prepare_throw_yields_rejected_with_tool_failure_code()
    {
        var ws = TempDir();
        try
        {
            var events = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(new FakeCatalog().Add(new PrepareThrowsTool()),
                ScriptedPermissionPolicy.WithTool("prepare.boom", PermissionDecision.Allow),
                payload => { events.Add(payload); return VoidBox.Instance; });

            var outcome = runtime.Run(
                new ValidatedToolCall(ToolCallId.New(), new ToolId("prepare.boom"), "p1", "{}"),
                new ToolPreparationContext(ws, DateTimeOffset.Now), new ToolExecutionContext(ws),
                false, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var rejected = RejectedOf(events);
            Assert.Equal(ToolErrorCode.ToolFailure, rejected.ErrorCode);
            Assert.StartsWith("prepare falló:", rejected.Reason); // texto estable, sin el código dentro
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Codeless_preparation_rejection_is_normalized_to_tool_failure_code()
    {
        var ws = TempDir();
        try
        {
            // Una tool que rechaza en Prepare sin fijar código (p. ej. código v1): el runtime no
            // persiste un rechazo huérfano — lo clasifica TOOL_FAILURE (spec §71, invariante:
            // todo evento de rechazo persistido lleva código).
            var events = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(new FakeCatalog().Add(new CodelessRejectingTool()),
                ScriptedPermissionPolicy.WithTool("codeless.read", PermissionDecision.Allow),
                payload => { events.Add(payload); return VoidBox.Instance; });

            var outcome = runtime.Run(
                new ValidatedToolCall(ToolCallId.New(), new ToolId("codeless.read"), "p1", "{}"),
                new ToolPreparationContext(ws, DateTimeOffset.Now), new ToolExecutionContext(ws),
                false, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            var rejected = RejectedOf(events);
            Assert.Equal(ToolErrorCode.ToolFailure, rejected.ErrorCode);
            Assert.Equal("motivo de rechazo de la tool", rejected.Reason); // el texto viaja intacto
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Interrupt_closing_a_started_call_persists_cancellation_code()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, codecs, session);
        var started = ToolCallId.New();
        stream.Append(new TurnStarted(TurnId.New(), run.RootLane));
        stream.AppendBatch(new DomainEventPayload[] {
            new ToolCallRequested(started, "pc", "fake.read", "{}"),
            new ToolCallPrepared(started, "{}"),
            new PermissionEvaluated(started, PermissionDecision.Allow, "[]", null),
            new ToolCallAuthorized(started),
            new ToolCallStarted(started, EffectClass.None, null),
        }, DurabilityClass.Standard);

        new RunControlService(store, codecs).Interrupt(session, run.RunId);

        // El evento PERSISTIDO (decode del journal) lleva el código de interrupción, no solo Cause.
        var journal = store.ReadFrom(session, 1);
        var failed = journal.Select(codecs.Decode).OfType<ToolCallFailed>().Single();
        Assert.Equal(started, failed.ToolCallId);
        Assert.Equal(ToolErrorCode.Cancellation, failed.ErrorCode);
        Assert.Equal(EffectOutcome.Unknown, failed.EffectOutcome);
        CanonicalStateTracker.Replay(codecs, journal); // Started → Failed es una transición válida
    }

    [Fact]
    public void Recovery_close_of_an_interrupted_read_persists_cancellation_code()
    {
        var ws = TempDir();
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var call = ToolCallId.New();
            var stream = new EventStream(store, codecs, session);
            // Crash justo tras el Barrier: la lectura quedó Started sin outcome.
            stream.AppendBatch(new DomainEventPayload[] {
                new ToolCallRequested(call, "pc", "filesystem.read", "{}"),
                new ToolCallPrepared(call, "{}"),
                new PermissionEvaluated(call, PermissionDecision.Allow, "[]", null),
                new ToolCallAuthorized(call),
                new ToolCallStarted(call, EffectClass.None, null),
            }, DurabilityClass.Barrier);

            var resumed = new RunResumeService(store, codecs, null, ws).Resume(session, run.RunId);
            Assert.Equal(1, resumed);

            var journal = store.ReadFrom(session, 1);
            var failed = journal.Select(codecs.Decode).OfType<ToolCallFailed>().Single();
            Assert.Equal(call, failed.ToolCallId);
            // El crash INTERRUMPIÓ la ejecución (spec §71 CANCELLATION); con efecto None no hay
            // efecto parcial que reconciliar, así que no es UNKNOWN_EFFECT.
            Assert.Equal(ToolErrorCode.Cancellation, failed.ErrorCode);
            Assert.Equal(EffectOutcome.None, failed.EffectOutcome);
            CanonicalStateTracker.Replay(codecs, journal); // Started → Failed válida tras recovery
        }
        finally
        {
            RmDir(ws);
        }
    }

    /// <summary>
    /// Sweep de las rutas principales de fallo (spec §71): cada una deja en el JOURNAL su evento
    /// de fallo/rechazo con SU código — unknown tool, schema inválido, permiso denegado,
    /// prepare que lanza, excepción de tool, interrupción de una Started y cierre por
    /// recuperación de una lectura interrumpida. Regla: ningún evento de fallo sin código.
    /// </summary>
    [Fact]
    public void Main_failure_paths_persist_their_expected_code()
    {
        var ws = TempDir();
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var stream = new EventStream(store, codecs, session);
            var executor = Pipeline(ws);

            void Persist(ToolOutcome outcome)
            {
                foreach (var evt in outcome.Events)
                {
                    stream.Append(evt);
                }
            }

            ValidatedToolCall Call(ToolCallId id, string tool, string argsJson) =>
                new(id, new ToolId(tool), "pc", argsJson);

            // 1) unknown tool → UNKNOWN_TOOL.
            var unknown = ToolCallId.New();
            Persist(executor.ExecuteTool(Call(unknown, "nope.tool", "{}"), true,
                CancellationToken.None, stream));

            // 2) schema inválido (filesystem.patch sin oldText/newText) → INVALID_ARGUMENTS.
            var schema = ToolCallId.New();
            Persist(executor.ExecuteTool(
                Call(schema, "filesystem.patch", "{\"path\":\"doc.txt\"}"),
                true, CancellationToken.None, stream));

            // 3) permiso denegado (ruta de secretos en Prepare) → PERMISSION_DENIED.
            var denied = ToolCallId.New();
            Persist(executor.ExecuteTool(
                Call(denied, "filesystem.read", "{\"path\":\".env\"}"),
                true, CancellationToken.None, stream));

            // 4) prepare que lanza → TOOL_FAILURE (productor del ToolRuntime, no de la tool).
            var prepare = ToolCallId.New();
            var prepareEvents = new List<DomainEventPayload>();
            new ToolRuntime(new FakeCatalog().Add(new PrepareThrowsTool()),
                ScriptedPermissionPolicy.WithTool("prepare.boom", PermissionDecision.Allow),
                payload => { prepareEvents.Add(payload); return VoidBox.Instance; })
                .Run(Call(prepare, "prepare.boom", "{}"),
                    new ToolPreparationContext(ws, DateTimeOffset.Now), new ToolExecutionContext(ws),
                    false, CancellationToken.None);
            foreach (var evt in prepareEvents)
            {
                stream.Append(evt);
            }

            // 5) excepción de tool (ExecuteAsync lanza) → TOOL_FAILURE.
            var boom = ToolCallId.New();
            var boomEvents = new List<DomainEventPayload>();
            new ToolRuntime(new FakeCatalog().Add(new ExplodingTool()),
                ScriptedPermissionPolicy.WithTool("boom.none", PermissionDecision.Allow),
                payload => { boomEvents.Add(payload); return VoidBox.Instance; })
                .Run(Call(boom, "boom.none", "{}"),
                    new ToolPreparationContext(ws, DateTimeOffset.Now), new ToolExecutionContext(ws),
                    false, CancellationToken.None);
            foreach (var evt in boomEvents)
            {
                stream.Append(evt);
            }

            // 6) interrupción de una llamada Started → CANCELLATION (RunControlService).
            var cancelled = ToolCallId.New();
            stream.AppendBatch(new DomainEventPayload[] {
                new ToolCallRequested(cancelled, "pc", "fake.read", "{}"),
                new ToolCallPrepared(cancelled, "{}"),
                new PermissionEvaluated(cancelled, PermissionDecision.Allow, "[]", null),
                new ToolCallAuthorized(cancelled),
                new ToolCallStarted(cancelled, EffectClass.None, null),
            }, DurabilityClass.Standard);
            new RunControlService(store, codecs).Interrupt(session, run.RunId);

            // 7) cierre por recuperación de una lectura interrumpida → CANCELLATION
            //    (RunResumeService, en su propia sesión: el crash dejó Started sin outcome).
            var crashSession = SessionId.New();
            var crashRun = TestRun.Open(store, crashSession);
            var recovered = ToolCallId.New();
            var crashStream = new EventStream(store, codecs, crashSession);
            crashStream.AppendBatch(new DomainEventPayload[] {
                new ToolCallRequested(recovered, "pc", "filesystem.read", "{}"),
                new ToolCallPrepared(recovered, "{}"),
                new PermissionEvaluated(recovered, PermissionDecision.Allow, "[]", null),
                new ToolCallAuthorized(recovered),
                new ToolCallStarted(recovered, EffectClass.None, null),
            }, DurabilityClass.Barrier);
            Assert.Equal(1, new RunResumeService(store, codecs, null, ws).Resume(crashSession,
                crashRun.RunId));

            // Asertos sobre el journal persistido (decode), no sobre los objetos en memoria.
            var persisted = store.ReadFrom(session, 1).Select(codecs.Decode).ToList();
            ToolCallRejected Rejected(ToolCallId id) =>
                Assert.Single(persisted.OfType<ToolCallRejected>(), r => r.ToolCallId.Equals(id));
            ToolCallFailed Failed(ToolCallId id) =>
                Assert.Single(persisted.OfType<ToolCallFailed>(), f => f.ToolCallId.Equals(id));

            Assert.Equal(ToolErrorCode.UnknownTool, Rejected(unknown).ErrorCode);
            Assert.Equal(ToolErrorCode.InvalidArguments, Rejected(schema).ErrorCode);
            Assert.Equal(ToolErrorCode.PermissionDenied, Rejected(denied).ErrorCode);
            Assert.Equal(ToolErrorCode.ToolFailure, Rejected(prepare).ErrorCode);
            Assert.Equal(ToolErrorCode.ToolFailure, Failed(boom).ErrorCode);
            Assert.Equal(ToolErrorCode.Cancellation, Failed(cancelled).ErrorCode);
            Assert.Equal(EffectOutcome.Unknown, Failed(cancelled).EffectOutcome);

            var crashPersisted = store.ReadFrom(crashSession, 1).Select(codecs.Decode).ToList();
            var recoveredFailed = Assert.Single(crashPersisted.OfType<ToolCallFailed>(),
                f => f.ToolCallId.Equals(recovered));
            Assert.Equal(ToolErrorCode.Cancellation, recoveredFailed.ErrorCode);
            Assert.Equal(EffectOutcome.None, recoveredFailed.EffectOutcome);

            // Invariante del sweep: ningún fallo/rechazo persistido queda sin código.
            foreach (var failure in persisted.OfType<ToolCallFailed>()) Assert.NotNull(failure.ErrorCode);
            foreach (var rejection in persisted.OfType<ToolCallRejected>()) Assert.NotNull(rejection.ErrorCode);
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(crashSession, 1));
        }
        finally
        {
            RmDir(ws);
        }
    }

    // ------------------------------------- 6. Journals v1: campo ausente → null

    [Fact]
    public void A_v1_toolcall_failed_still_decodes_with_a_null_code()
    {
        var call = ToolCallId.New();
        var v1 = DomainEvent.Stored(EventId.New(), SessionId.New(), 1, EventType.Of("toolcall.failed"), 1,
            DateTimeOffset.UtcNow, null, null, null, null, null, null, null, call, Array.Empty<ArtifactRef>(),
            "{\"ToolCallId\":{\"Value\":\"" + call.Value + "\"},\"Cause\":\"tool failed\",\"EffectOutcome\":0}");

        var decoded = Assert.IsType<ToolCallFailed>(EventCodecs.Create().Decode(v1));
        Assert.Equal(call, decoded.ToolCallId);
        Assert.Equal("tool failed", decoded.Cause);
        Assert.Null(decoded.ErrorCode);
    }

    [Fact]
    public void A_v1_toolcall_rejected_still_decodes_with_a_null_code()
    {
        var call = ToolCallId.New();
        var v1 = DomainEvent.Stored(EventId.New(), SessionId.New(), 1, EventType.Of("toolcall.rejected"), 1,
            DateTimeOffset.UtcNow, null, null, null, null, null, null, null, call, Array.Empty<ArtifactRef>(),
            "{\"ToolCallId\":{\"Value\":\"" + call.Value + "\"},\"Reason\":\"invalid arguments\"}");

        var decoded = Assert.IsType<ToolCallRejected>(EventCodecs.Create().Decode(v1));
        Assert.Equal(call, decoded.ToolCallId);
        Assert.Equal("invalid arguments", decoded.Reason);
        Assert.Null(decoded.ErrorCode);
    }

    // ---------------------------------------------------------------------- fakes

    private sealed class NeverLaunchingLauncher : ISandboxProcessLauncher
    {
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "nunca debería lanzar en estos tests");
    }

    private sealed class StrongUnavailableLauncher : ISandboxProcessLauncher
    {
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch,
            CancellationToken cancellationToken)
        {
            if (launch.RequestedStrength == SandboxStrength.Strong)
            {
                throw new NotSupportedException("test: Strong unavailable");
            }

            throw new InvalidOperationException("el consentimiento debe denegarse antes de lanzar");
        }
    }

    /// <summary>Tool cuyo Prepare lanza: el ToolRuntime tipa el rechazo TOOL_FAILURE.</summary>
    private sealed class PrepareThrowsTool : ITool
    {
        private readonly ToolDescriptor _descriptor = new(new ToolId("prepare.boom"),
            "Prepare lanza siempre (test)", new InputSchema("{}"), new string[0], true, false,
            ToolRisk.Low, ComponentSource.Core(), ToolProtection.None, EffectClass.None);

        public ToolDescriptor Descriptor => _descriptor;

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            throw new InvalidOperationException("prepare boom");

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken) => throw new InvalidOperationException("nunca llega a ejecutar");
    }

    /// <summary>Tool que rechaza en Prepare SIN fijar código: el runtime lo normaliza TOOL_FAILURE.</summary>
    private sealed class CodelessRejectingTool : ITool
    {
        private readonly ToolDescriptor _descriptor = new(new ToolId("codeless.read"),
            "Rechaza en Prepare sin código (test)", new InputSchema("{}"), new string[0], true, false,
            ToolRisk.Low, ComponentSource.Core(), ToolProtection.None, EffectClass.None);

        public ToolDescriptor Descriptor => _descriptor;

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            new PreparationRejected("motivo de rechazo de la tool", null);

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken) => throw new InvalidOperationException("nunca llega a ejecutar");
    }

    /// <summary>Tool EffectClass.None que lanza en ExecuteAsync: el runtime lo tipa TOOL_FAILURE.</summary>
    private sealed class ExplodingTool : ITool
    {
        private readonly ToolDescriptor _descriptor = new(new ToolId("boom.none"),
            "Lanza siempre (test)", new InputSchema("{}"), new string[0], true, false, ToolRisk.Low,
            ComponentSource.Core(), ToolProtection.None, EffectClass.None);

        public ToolDescriptor Descriptor => _descriptor;

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    /// <summary>Tool que devuelve éxito con EffectOutcome.Unknown: el runtime lo tipa UNKNOWN_EFFECT.</summary>
    private sealed class UnknownOutcomeTool : ITool
    {
        private readonly ToolDescriptor _descriptor = new(new ToolId("mystery.read"),
            "Exito con efecto desconocido (test)", new InputSchema("{}"), new string[0], true, false,
            ToolRisk.Low, ComponentSource.Core(), ToolProtection.None, EffectClass.None);

        public ToolDescriptor Descriptor => _descriptor;

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(
            new ToolResult("hecho", "hecho", null, 0, false, EffectOutcome.Unknown));
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        public Func<ContentHash, string?> OnGetText { get; set; } = _ => "contenido";

        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity) =>
            throw new NotSupportedException("solo lectura en estos tests");

        public string? GetText(ContentHash hash) => OnGetText(hash);

        public bool Verify(ContentHash hash, long expectedSize) => false;
    }
}
