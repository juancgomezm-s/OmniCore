using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Pruebas de integración HOST/TURN de la frontera de capacidad del modelo (ADR-0044 §5),
/// bloqueante de seguridad de M3: la política efectiva del modelo seleccionado se cablea en el
/// flujo REAL (ScriptedToolExecutor → ToolRuntime → ExplorerTurn), no solo en tests aislados
/// del boundary. Verifican que:
///  - un modelo sin UserModelPolicy efectiva (fallback ObserveOnly) NO escribe, aunque invoque
///    filesystem.patch directamente, y el archivo queda intacto;
///  - una política PatchOnly permite un patch válido con token y lectura previa;
///  - una tool oculta (fake.write = reemplazo) o inventada (filesystem.purge) se rechaza;
///  - el cambio de política (ObserveOnly → PatchOnly) cambia el comportamiento.
/// La categoría es un TECHO, nunca un permiso: el Permission Engine conserva la autoridad
/// (INV-018) y la frontera solo restringe.
/// </summary>
public sealed class M3PolicyBoundaryTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-boundary", Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// Política efectiva por categoría. ObserveOnly se construye SIN StoredModelPolicy: es el
    /// fallback de un modelo no clasificado (ADR-0044 §10.1), la base del requisito de entrega.
    /// </summary>
    private static EffectiveModelPolicy EffectiveFor(ModelPolicyCategory category)
    {
        var key = ModelPolicyKey.For("p", "m");
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
            PlanControl.ModelDriven, 8);
        StoredModelPolicy? stored = category == ModelPolicyCategory.ObserveOnly
            ? null
            : new StoredModelPolicy(key, 1, ModelPolicyPresets.For(category),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return EffectiveModelPolicy.Resolve(key, stored, harness);
    }

    // ---- Proveedores scriptados (modelo determinista) ----

    private static ModelResponse End() =>
        new ModelResponse(new ContentBlock[] { new TextBlock("ok") }, StopReason.EndTurn,
            new TokenUsage(2, 2, 0, 0, 0), null, new ProviderMetadata("", "", null));

    private static ModelResponse ToolCall(string name, string argsJson) =>
        new ModelResponse(new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "call-" + name, name, argsJson) },
            StopReason.ToolUse, new TokenUsage(2, 2, 0, 0, 0), null, new ProviderMetadata("", "", null));

    private static int ToolResults(ModelRequest request)
    {
        var n = 0;
        foreach (var m in request.Messages)
        {
            foreach (var b in m.Content)
            {
                if (m.Role == MessageRole.Tool && b is ToolResultBlock) n += 1;
            }
        }

        return n;
    }

    private static string PatchArgs(string path, string token, string oldText, string newText)
    {
        return "{\"path\":\"" + path + "\",\"expectedVersion\":\"" + token
            + "\",\"oldText\":\"" + oldText + "\",\"newText\":\"" + newText + "\"}";
    }

    // ================= 1. Modelo no clasificado → ObserveOnly → rechazo y archivo intacto =====

    [Fact]
    public void Unclassified_model_patch_is_rejected_and_file_intact()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var effective = EffectiveFor(ModelPolicyCategory.ObserveOnly);
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeMutationTools: true);
        var boundary = new ModelCapabilityBoundary(effective);
        var policy = ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow);
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, ws, boundary);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var fingerprint = new ExecutionFingerprint("m", "h", "t", "c", "o", "M2", effective.Fingerprint());
        var selection = new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null);
        var sink = new InMemoryAuditSink();
        var store = new InMemoryEventStore();
        var turn = new ExplorerTurn((req, tok) => ToolResults(req) == 0
                ? ToolCall("filesystem.patch", PatchArgs("doc.txt", VersionOf(original), "linea-dos", "linea-dos-X"))
                : End(),
            executor, hostTools.Catalog(), materializer, fingerprint, selection,
            store, EventCodecs.Create(), new FileArtifactStore(ws + "\\.omnicore-art"), sink,
            new RedactionPolicy(), boundary: boundary);

        var sessionId = SessionId.New();
        var result = turn.Ask("patchea el archivo", "sys {context}", sessionId, RunId.New(), "",
            CancellationToken.None);

        // El modelo invocó filesystem.patch pero la frontera ObserveOnly lo rechaza.
        Assert.True(result.ToolCalls.Count >= 1, "El turno intentó la tool");
        Assert.False(result.ToolCalls[0].Succeeded,
            "ObserveOnly rechaza el patch incluso invocado directamente. summary=" + result.ToolCalls[0].Summary);
        Assert.True(File.ReadAllText(ws + "\\doc.txt") == original, "El archivo no se toca");

        // El journal registra el rechazo; NUNCA un succeeded.
        var types = store.ReadFrom(sessionId, 1).Select(e => e.Type.ToString()).ToArray();
        Assert.True(types.Contains("toolcall.requested"), "requested presente: " + string.Join(",", types));
        Assert.True(types.Contains("toolcall.rejected"), "rejected presente: " + string.Join(",", types));
        Assert.False(types.Contains("toolcall.succeeded"), "nunca toolcall.succeeded");

        // ADR-0044 §8: el fingerprint lleva el hash de la política efectiva y se audita.
        Assert.Equal(effective.Fingerprint(), fingerprint.ModelPolicyHash);
        Assert.True(fingerprint.ModelPolicyHash is not null && fingerprint.ModelPolicyHash!.Length > 0,
            "El fingerprint registra el hash de la política (no vacío)");
        var policyAudits = sink.Records().Where(r => r.EventName == "turn.policy").ToArray();
        Assert.True(policyAudits.Length >= 1, "El turno registra la política aplicada en el audit");
        Assert.Equal(effective.Fingerprint(), policyAudits[0].Details["modelPolicyHash"]);
        RmDir(ws);
    }

    // ================= 2. PatchOnly → patch válido con token y lectura previa → éxito =========

    [Fact]
    public void PatchOnly_model_valid_patch_with_prior_read_succeeds()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var token = VersionOf(original);
        var effective = EffectiveFor(ModelPolicyCategory.PatchOnly);
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeMutationTools: true);
        var boundary = new ModelCapabilityBoundary(effective);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["filesystem.read"] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, ws, boundary);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var fingerprint = new ExecutionFingerprint("m", "h", "t", "c", "o", "M2", effective.Fingerprint());
        var selection = new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null);
        var sink = new InMemoryAuditSink();
        var store = new InMemoryEventStore();
        var turn = new ExplorerTurn((req, tok) =>
            {
                var n = ToolResults(req);
                if (n == 0)
                {
                    // 1. Lectura previa del archivo existente (ADR-0044 §3, PatchOnly).
                    return ToolCall("filesystem.read", "{\"path\":\"doc.txt\"}");
                }

                if (n == 1)
                {
                    // 2. Patch localizado con el token de versión vigente.
                    return ToolCall("filesystem.patch",
                        PatchArgs("doc.txt", token, "linea-dos", "linea-dos-B"));
                }

                return End();
            },
            executor, hostTools.Catalog(), materializer, fingerprint, selection,
            store, EventCodecs.Create(), new FileArtifactStore(ws + "\\.omnicore-art"), sink,
            new RedactionPolicy(), boundary: boundary);

        var sessionId = SessionId.New();
        var result = turn.Ask("lee y patchea doc.txt", "sys {context}", sessionId, RunId.New(), "",
            CancellationToken.None);

        Assert.True(result.ToolCalls.Count >= 2, "Lectura previa + patch (" + result.ToolCalls.Count + ")");
        Assert.True(result.ToolCalls[0].Succeeded, "filesystem.read ok. summary=" + result.ToolCalls[0].Summary);
        Assert.True(result.ToolCalls[1].Succeeded, "filesystem.patch ok. summary=" + result.ToolCalls[1].Summary);
        Assert.Equal("linea-uno\nlinea-dos-B\n", File.ReadAllText(ws + "\\doc.txt"));

        // El turno lo persiste como succeeded y audita la política.
        var types = store.ReadFrom(sessionId, 1).Select(e => e.Type.ToString()).ToArray();
        Assert.True(types.Contains("toolcall.succeeded"), "patch persisted as succeeded: " + string.Join(",", types));
        var policyAudits = sink.Records().Where(r => r.EventName == "turn.policy").ToArray();
        Assert.True(policyAudits.Length >= 1, "La política se audita también en el camino de éxito");
        Assert.Equal(effective.Fingerprint(), policyAudits[0].Details["modelPolicyHash"]);
        RmDir(ws);
    }

    // ============= 3. Tool oculta (reemplazo) e inventada → rechazo en el runtime =============

    [Fact]
    public void PatchOnly_rejects_hidden_write_and_invented_tool()
    {
        var ws = TempDir();
        var effective = EffectiveFor(ModelPolicyCategory.PatchOnly);
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeMutationTools: true);
        var boundary = new ModelCapabilityBoundary(effective);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["fake.write"] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, ws, boundary);

        // (a) Tool OCULTA: fake.write se clasifica como ReplaceFile (igual que filesystem.write).
        //     PatchOnly no la expone y la frontera la rechaza en el runtime aunque los permisos la
        //     permitirían: la categoría nunca concede una tool fuera de su techo (ADR-0044 §10.4).
        var hidden = new ValidatedToolCall(ToolCallId.New(), new ToolId("fake.write"), "pc-hidden", "{}");
        var hiddenOutcome = executor.ExecuteToolWithoutJournal(hidden, false, CancellationToken.None);
        Assert.False(hiddenOutcome.Succeeded, "PatchOnly no permite el reemplazo completo. summary=" + hiddenOutcome.Summary);
        Assert.Equal(ToolCallState.Rejected, hiddenOutcome.FinalState);
        var hiddenTypes = hiddenOutcome.Events.Select(e => e.Type().ToString()).ToArray();
        Assert.True(hiddenTypes.Contains("toolcall.requested"), "requested: " + string.Join(",", hiddenTypes));
        Assert.True(hiddenTypes.Contains("toolcall.rejected"), "rejected: " + string.Join(",", hiddenTypes));
        Assert.False(hiddenTypes.Contains("toolcall.succeeded"), "nunca succeeded");

        // (b) Tool INVENTADA en un Turn: filesystem.purge no existe → se rechaza en el flujo host.
        var store = new InMemoryEventStore();
        var sink = new InMemoryAuditSink();
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var fingerprint = new ExecutionFingerprint("m", "h", "t", "c", "o", "M2", effective.Fingerprint());
        var selection = new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null);
        var turn = new ExplorerTurn((req, tok) => ToolResults(req) == 0
                ? ToolCall("filesystem.purge", "{\"path\":\"doc.txt\"}")
                : End(),
            executor, hostTools.Catalog(), materializer, fingerprint, selection,
            store, EventCodecs.Create(), new FileArtifactStore(ws + "\\.omnicore-art"), sink,
            new RedactionPolicy(), boundary: boundary);
        var sessionId = SessionId.New();
        var result = turn.Ask("borra el archivo", "sys {context}", sessionId, RunId.New(), "",
            CancellationToken.None);
        Assert.True(result.ToolCalls.Count >= 1, "La tool inventada se intentó");
        Assert.False(result.ToolCalls[0].Succeeded, "La tool inventada se rechaza. summary=" + result.ToolCalls[0].Summary);
        var invTypes = store.ReadFrom(sessionId, 1).Select(e => e.Type.ToString()).ToArray();
        Assert.True(invTypes.Contains("toolcall.rejected"), "rejected: " + string.Join(",", invTypes));
        Assert.False(invTypes.Contains("toolcall.succeeded"), "nunca succeeded");
        RmDir(ws);
    }

    // ============= 4. El cambio de política (ObserveOnly → PatchOnly) cambia el comportamiento ==

    [Fact]
    public void Policy_change_flips_patch_from_reject_to_allow()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        var token = VersionOf(original);
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeMutationTools: true);
        var patchCall = new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.patch"), "pc-p",
            PatchArgs("doc.txt", token, "linea-dos", "linea-dos-C"));

        // ObserveOnly (modelo no clasificado): rechazo, archivo intacto.
        var obsBoundary = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.ObserveOnly));
        var obsExecutor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow), ws, obsBoundary);
        var obsOutcome = obsExecutor.ExecuteToolWithoutJournal(patchCall, false, CancellationToken.None);
        Assert.False(obsOutcome.Succeeded, "ObserveOnly rechaza el patch");
        Assert.Equal(ToolCallState.Rejected, obsOutcome.FinalState);
        Assert.True(File.ReadAllText(ws + "\\doc.txt") == original, "intacto tras ObserveOnly");

        // PatchOnly (política guardada para la misma clave): el MISMO patch aplica. Requiere la
        // lectura previa efectiva del archivo en el mismo Run (ADR-0044 §5): sin ella el patch se
        // rechaza con PRIOR_READ_REQUIRED aunque el token sea correcto.
        var patchBoundary = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyCategory.PatchOnly));
        var patchPolicy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["filesystem.read"] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        var patchExecutor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), patchPolicy,
            ws, patchBoundary);
        var readCall = new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.read"), "pc-pr",
            "{\"path\":\"doc.txt\"}");
        var readOutcome = patchExecutor.ExecuteToolWithoutJournal(readCall, false, CancellationToken.None);
        Assert.True(readOutcome.Succeeded, "PatchOnly permite la lectura previa. summary=" + readOutcome.Summary);
        var patchOutcome = patchExecutor.ExecuteToolWithoutJournal(patchCall, false, CancellationToken.None);
        Assert.True(patchOutcome.Succeeded, "Read previo + PatchOnly permiten el patch tras la recategorización. summary=" + patchOutcome.Summary);
        Assert.Equal("linea-uno\nlinea-dos-C\n", File.ReadAllText(ws + "\\doc.txt"));
        RmDir(ws);
    }
}