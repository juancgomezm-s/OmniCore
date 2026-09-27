namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>
/// OmniServer: sesión de protocolo in-process que expone el runtime detrás de IOmniClient
/// (ADR-0019 §1). Traduce los comandos wire a llamadas del Engine y acumula los eventos wire.
/// En M1 atiende <c>sim</c> y la query <c>state</c>.
/// </summary>
public sealed class OmniServer : IOmniClient
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly IAuditSink _audit;

    private readonly SimulationEngine _engine;

    private readonly List<WireEnvelope> _events = new();

    private SessionId? _lastSessionId;

    private RunId? _lastRunId;

    private ContextSnapshot? _lastSnapshot;

    private readonly string? _stateFile;

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit)
    {
        _store = store;
        _codecs = codecs;
        _audit = audit;
        _engine = BuildEngine(store, codecs, audit);
        _stateFile = null;
        LoadLastSession();
    }

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit, string stateFile)
    {
        _store = store;
        _codecs = codecs;
        _audit = audit;
        _engine = BuildEngine(store, codecs, audit);
        _stateFile = stateFile;
        LoadLastSession();
    }

    /// <summary>Composición común: tools Core + defaults por modo (modo Act del sim).</summary>
    internal static SimulationEngine BuildEngine(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit) =>
        new SimulationEngine(store, codecs, audit, ScriptedToolExecutor.WithCoreTools(
            HostTools.Default().Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act)));

    private void LoadLastSession()
    {
        if (_stateFile is null || !File.Exists(_stateFile!))
        {
            return;
        }

        var text = File.ReadAllText(_stateFile!);
        var sep = text.IndexOf('\n');
        if (sep > 0)
        {
            _lastSessionId = SessionId.Parse(text.Substring(0, sep));
            _lastRunId = RunId.Parse(text.Substring(sep + 1));
        }
    }

    private void SaveLastSession()
    {
        if (_stateFile is null || _lastSessionId is null || _lastRunId is null)
        {
            return;
        }

        File.WriteAllText(_stateFile!, _lastSessionId!.ToString() + "\n" + _lastRunId!.ToString());
    }

    public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
    {
        if (command.MessageType != MessageTypes.Command)
        {
            return CommandAck.Fail(command.MessageId, "esperaba un command, recibí " + command.MessageType);
        }

        var fields = JsonObj.Parse(command.PayloadJson);
        var queryName = fields.TryGetValue("query", out var q) ? q : null;
        if (queryName is not null)
        {
            return CommandAck.Ok(command.MessageId);
        }

        var commandName = fields.TryGetValue("cmd", out var c) ? c : null;
        if (commandName == "sim")
        {
            return RunSim(command, fields);
        }

        if (commandName == "sim.resume")
        {
            return ResumeSim(command);
        }

        return CommandAck.Fail(command.MessageId, "comando desconocido en M1");
    }

    public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => _events.ToArray();

    public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
    {
        if (name == "state")
        {
            var run = _lastSnapshot is null ? "none" : "M2:" + _lastSnapshot!.TokenCount.ToString();
            return new SessionQueryResult("state", "{\"runtime\":\"omnicore\",\"milestone\":\"M2\","
                + JsonObj.Field("runState", run) + "}");
        }

        if (name == "context")
        {
            if (_lastSnapshot is null)
            {
                return new SessionQueryResult("context", "{\"snapshot\":null}");
            }

            var items = new List<string>();
            foreach (ContextItem item in _lastSnapshot!.Items)
            {
                items.Add("{\"id\":" + JsonObj.Field("id", item.Id)
                    + ",\"kind\":" + JsonObj.Field("kind", item.Kind.ToString())
                    + ",\"tokens\":" + JsonObj.Field("tokens", item.EstimatedTokens.ToString())
                    + ",\"content\":" + JsonObj.Field("content", Redact(item.Content, 80)) + "}");
            }

            return new SessionQueryResult("context",
                "{\"snapshot\":{" + JsonObj.Field("tokens", _lastSnapshot!.TokenCount.ToString())
                + ",\"fingerprint\":" + JsonObj.Field("fingerprint", _lastSnapshot!.Fingerprint.Hash())
                + ",\"items\":[" + string.Join(",", items.ToArray()) + "]}}");
        }

        return null;
    }

    /// <summary>
    /// Redacción de PII para diagnóstico (/context y /permissions son supervisors del runtime;
    /// nunca vuelcan contenido completo del workspace, solo cabeceras, ADR-0018).
    /// </summary>
    private static string Redact(string content, int max)
    {
        var safe = content is null ? "" : content!;
        if (safe.Length <= max)
        {
            return safe;
        }

        return safe.Substring(0, max) + "…(" + safe.Length + ")";
    }

    private CommandAck RunSim(WireEnvelope command, Dictionary<string, string> fields)
    {
        try
        {
            var scenarioName = fields.TryGetValue("scenario", out var s) ? s : null;
            var scenario = "with-tool-crash" == scenarioName
                ? Scenarios.WithToolCrash()
                : Scenarios.MultiItemPlan();
            var result = _engine.Execute(scenario, CancellationToken.None);
            _lastSessionId = result.SessionId;
            _lastRunId = result.RunId;
            SaveLastSession();
            AuditRun(result);
            var snapshot = MaterializeSnapshot(result);
            _lastSnapshot = snapshot;
            _events.Add(WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.FieldRaw("type", "\"sim.events\"")
                + "," + JsonObj.Field("exitCode", result.ExitCode == 0 ? "0" : "1")
                + "," + JsonObj.Field("run", result.Run.State.ToString())
                + "," + JsonObj.Field("ctxTokens", snapshot is null ? "0" : snapshot.TokenCount.ToString()) + "}"));
            return result.ExitCode == 0 ? CommandAck.Ok(command.MessageId)
                : CommandAck.Fail(command.MessageId, "sim falló: " + string.Join("; ", result.Diagnostics));
        }
        catch (Exception ex)
        {
            var detail = ex is OmniCore.Infrastructure.EventParseException parse
                ? "Evento " + parse.EventType + ": " + (parse.Detail ?? "?")
                : (ex.Message ?? "exception");
            var stack = ex.StackTrace is null ? "" : string.Join("; ", ex.StackTrace);
            return CommandAck.FailWithCause(command.MessageId, detail, stack);
        }
    }

    /// <summary>
    /// Materializa un ContextSnapshot del run (ADR-0042 §2): WorkingState + conversación, con
    /// conteo de tokens. Es el post-proceso que el Context Engine hace en el Host (el Engine
    /// permanece desacoplado).
    /// </summary>
    private ContextSnapshot? MaterializeSnapshot(SimulationEngine.RunResult result)
    {
        if (result.WorkingState is null)
        {
            return null;
        }

        var counter = new FakeTokenCounter();
        var contributors = new IContextContributor[] {
            new WorkingStateContributor(OmniCore.Engine.WorkingStateProjector.Render(result.WorkingState)),
        };
        var materializer = new ContextMaterializer(counter, contributors);
        var fingerprint = new ExecutionFingerprint(
            "qwen38-27b-local", "harness-v1", "core-tools", "ctx-v1", "none", "M2");
        var request = new MaterializeRequest(result.SessionId, result.RunId, null, null, null,
            _store.CurrentSequence(result.SessionId), fingerprint);
        return materializer.Materialize(request, CancellationToken.None);
    }

    /// <summary>
    /// Alimenta el audit sink con los eventos de permisos del run (ADR-0043 §1, INV-012):
    /// el Engine no escribe auditoría directamente; un consumidor lee los eventos canónicos
    /// y registra los PermissionGranted/Denied como metadata redactada.
    /// </summary>
    private void AuditRun(SimulationEngine.RunResult result)
    {
        var events = _store.ReadFrom(result.SessionId, 1);
        foreach (var evt in events)
        {
            if (evt.Type.ToString() == "toolcall.permission_evaluated"
                || evt.Type.ToString() == "toolcall.permission_granted"
                || evt.Type.ToString() == "toolcall.permission_denied")
            {
                _audit.Record(new AuditRecord(
                    evt.Type.ToString(),
                    null,
                    result.SessionId,
                    result.RunId,
                    evt.Timestamp,
                    "session:" + result.SessionId + ":" + evt.Sequence,
                    new Dictionary<string, string>()), CancellationToken.None);
            }
        }
    }

    private CommandAck ResumeSim(WireEnvelope command)
    {
        if (_lastSessionId is null || _lastRunId is null)
        {
            return CommandAck.Fail(command.MessageId, "no hay un run previo para reanudar");
        }

        try
        {
            var stream = new EventStream(_store, _codecs, _lastSessionId!);
            var reconciled = _engine.Resume(_lastSessionId!, _lastRunId!, stream);
            _events.Add(WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.FieldRaw("type", "\"sim.resumed\"")
                + "," + JsonObj.Field("reconciled", reconciled == 0 ? "0" : "1") + "}"));
            return CommandAck.Ok(command.MessageId);
        }
        catch (Exception ex)
        {
            return CommandAck.FailWithCause(command.MessageId, ex.Message ?? "exception",
                ex.StackTrace is null ? "" : string.Join("; ", ex.StackTrace));
        }
    }
}

/// <summary>Escenarios de simulación incluidos para los tests deterministas y <c>omni sim</c>.</summary>
public sealed class Scenarios
{
    /// <summary>Plan de varios items con TaskGraph N:M, el caso central del criterio de M1 (ADR-0041).</summary>
    public static SimulationScenario MultiItemPlan()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("fake.read", "read"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "multi-item-plan",
            RunMode.Act,
            "Corregir el test de autenticación",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Inspeccionar auth", new string[0]),
                new SimulatedPlanMutation("P2", "Aplicar fix", new string[] { "P1" }),
                new SimulatedPlanMutation("P3", "Validar", new string[] { "P2" }),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Explorar flujo de auth"),
                new SimulatedTask("T2",
                    new SimulatedLink[] { new SimulatedLink("P2", "implements") },
                    new string[] { "T1" }, "Aplicar el fix"),
                new SimulatedTask("T3",
                    new SimulatedLink[] { new SimulatedLink("P3", "verifies") },
                    new string[] { "T2" }, "Validar el fix"),
            },
            turns,
            AuthPermissions(),
            "Completed",
            new Dictionary<string, string>());
    }

    /// <summary>Permisos del escenario por defecto: escritura Allow, resto Allow (perfil autónomo simulado).</summary>
    public static Dictionary<string, PermissionDecision> AuthPermissions()
    {
        var p = new Dictionary<string, PermissionDecision>();
        p["fake.read"] = PermissionDecision.Allow;
        p["fake.test"] = PermissionDecision.Allow;
        p["fake.write"] = PermissionDecision.Allow;
        return p;
    }

    /// <summary>Escenario con una tool call real en los turns (ejercita ToolRuntime completo).</summary>
    public static SimulationScenario WithTools()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("fake.write", "applied"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "with-tools",
            RunMode.Act,
            "Escribir un archivo",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Escribir", new string[0]),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Escribir objetivo"),
            },
            turns,
            AuthPermissions(),
            "Completed",
            new Dictionary<string, string>());
    }

    /// <summary>Escenario con crash inyectado tras el Started de fake.write (ADR-0041 §2).</summary>
    public static SimulationScenario WithToolCrash()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("fake.write", "applied"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "with-tool-crash",
            RunMode.Act,
            "Escribir un archivo",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Escribir", new string[0]),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Escribir objetivo"),
            },
            turns,
            AuthPermissions(),
            "Running",
            new Dictionary<string, string>(),
            "fake.write");
    }

    /// <summary>WorkingState renderizado de ejemplo para `omni explain` sin LLM (M2).</summary>
    public static string SampleWorkingState()
    {
        var rootItem = OmniCore.Domain.PlanItemId.New();
        var items = new OmniCore.Domain.PlanItem[] {
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "Explorar el repositorio",
                OmniCore.Domain.PlanItemState.InProgress, 1, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "Resumir estructura",
                OmniCore.Domain.PlanItemState.Ready, 2, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
        };
        _ = rootItem;
        var plan = OmniCore.Engine.PlanProjection.FromItems(OmniCore.Domain.PlanId.New(),
            OmniCore.Domain.RunId.New(), items);
        var run = OmniCore.Engine.RunProjection.ForSample(
            OmniCore.Domain.SessionId.New(), OmniCore.Domain.RunId.New(), "Explícame este repositorio");
        return OmniCore.Engine.WorkingStateProjector.Render(
            OmniCore.Engine.WorkingStateProjector.Project(run, plan));
    }
}