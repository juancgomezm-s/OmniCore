using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Tests de validación de argumentos por InputSchema (M2 EPIC-018): el pipeline valida
/// TODAS las tools contra su schema declarado ANTES de Prepare (ADR-0014 §1, INV-001) y
/// una tool jamás se ejecuta con argumentos inválidos: el rechazo es tipado
/// (INVALID_ARGUMENTS: detalle, spec §71) y alimenta el repair loop.
/// </summary>
public sealed class ToolSchemaValidationTests
{
    /// <summary>Tool de grabación: cuenta Prepare y Execute para probar que NUNCA se llaman.</summary>
    private sealed class RecordingTool : ITool
    {
        private int _prepareCalls;
        private int _executeCalls;

        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public int ExecuteCalls => Volatile.Read(ref _executeCalls);
        public ToolDescriptor Descriptor { get; }

        public RecordingTool(string name, string schema)
        {
            Descriptor = new ToolDescriptor(new ToolId(name), "rec", new InputSchema(schema),
                Array.Empty<string>(), true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);
        }

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
        {
            Interlocked.Increment(ref _prepareCalls);
            return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));
        }

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCalls);
            return System.Threading.Tasks.Task.FromResult(ToolResult.Ok("ok", null, 0, false, EffectOutcome.None));
        }
    }

    private sealed class EventSink
    {
        public readonly List<DomainEventPayload> Events = new();

        public VoidBox Emit(DomainEventPayload payload)
        {
            Events.Add(payload);
            return VoidBox.Instance;
        }

        public int Count(string eventName)
        {
            var n = 0;
            foreach (var e in Events)
            {
                if (e.Type().ToString() == eventName)
                {
                    n += 1;
                }
            }

            return n;
        }
    }

    /// <summary>Schema con superficie declarada: path requerido, tipos y strict-by-default.</summary>
    private const string StrictSchema =
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"},"
        + "\"flag\":{\"type\":\"boolean\"}},\"required\":[\"path\"]}";

    private static (ToolRuntime.Outcome Outcome, RecordingTool Tool, EventSink Sink) Run(string argsJson,
        string? schema = null)
    {
        var sink = new EventSink();
        var tool = new RecordingTool("recorded", schema ?? StrictSchema);
        var runtime = ToolRuntime.For(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("recorded", PermissionDecision.Allow), sink.Emit);
        var outcome = runtime.Run(
            new ValidatedToolCall(ToolCallId.New(), new ToolId("recorded"), "pc", argsJson),
            new ToolPreparationContext("sim", DateTimeOffset.UtcNow),
            new ToolExecutionContext("sim"), false, TestContext.Current.CancellationToken);
        return (outcome, tool, sink);
    }

    [Theory]
    [InlineData("{}", "campo requerido faltante: 'path'")]
    [InlineData("{\"path\":1}", "se esperaba string")]
    [InlineData("{\"path\":\"a\",\"count\":1.5}", "se esperaba integer")]
    [InlineData("{\"path\":\"a\",\"count\":\"3\"}", "se esperaba integer")]
    [InlineData("{\"path\":\"a\",\"flag\":\"true\"}", "se esperaba boolean")]
    [InlineData("{\"path\":\"a\",\"extra\":1}", "campo desconocido: 'extra'")]
    [InlineData("[1,2]", "los argumentos deben ser un objeto JSON")]
    [InlineData("\"texto\"", "los argumentos deben ser un objeto JSON")]
    [InlineData("{no-json", "los argumentos no son JSON válido")]
    public void Invalid_arguments_are_rejected_before_prepare_and_execute(string argsJson, string expectedDetail)
    {
        var (outcome, tool, sink) = Run(argsJson);

        Assert.False(outcome.Succeeded, "summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.StartsWith("INVALID_ARGUMENTS: ", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains(expectedDetail, outcome.Summary, StringComparison.Ordinal);

        // La tool NUNCA se ejecuta: ni Prepare ni Execute ven los argumentos inválidos.
        Assert.Equal(0, tool.PrepareCalls);
        Assert.Equal(0, tool.ExecuteCalls);
        Assert.True(sink.Count("toolcall.rejected") == 1, "El rechazo queda en el journal");
        Assert.True(sink.Count("toolcall.prepared") == 0);
        Assert.True(sink.Count("toolcall.permission_evaluated") == 0);
    }

    [Fact]
    public void Valid_arguments_reach_prepare_and_execute()
    {
        var (outcome, tool, sink) = Run("{\"path\":\"a\",\"count\":3,\"flag\":true}");

        Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Succeeded, outcome.FinalState);
        Assert.Equal(1, tool.PrepareCalls);
        Assert.Equal(1, tool.ExecuteCalls);
        Assert.True(sink.Count("toolcall.succeeded") == 1);
    }

    [Fact]
    public void Permissive_empty_schema_accepts_any_object()
    {
        // El schema "{}" (FakeTool y tools de test) no declara superficie: la validación de
        // schema no puede cerrarles la puerta; la tool hace su propia validación semántica.
        var (outcome, tool, _) = Run("{\"anything\":1,\"more\":[1,2]}", schema: "{}");

        Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
        Assert.Equal(1, tool.ExecuteCalls);
    }

    [Fact]
    public void Required_fields_apply_even_without_declared_properties()
    {
        // required sin properties: la presencia se exige, los tipos los valida la tool.
        var (outcome, _, _) = Run("{}", schema: "{\"required\":[\"path\"]}");

        Assert.False(outcome.Succeeded);
        Assert.Equal(ToolCallState.Rejected, outcome.FinalState);
        Assert.StartsWith("INVALID_ARGUMENTS: ", outcome.Summary, StringComparison.Ordinal);

        var (present, _, _) = Run("{\"path\":42}", schema: "{\"required\":[\"path\"]}");
        Assert.True(present.Succeeded, "summary=" + present.Summary);
    }

    [Fact]
    public void Additional_properties_true_accepts_undeclared_fields()
    {
        var schema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},"
            + "\"required\":[\"path\"],\"additionalProperties\":true}";

        var (outcome, tool, _) = Run("{\"path\":\"a\",\"extra\":true}", schema: schema);

        Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
        Assert.Equal(1, tool.ExecuteCalls);
    }

    [Fact]
    public void Schema_rejection_replays_to_a_terminal_rejected_state()
    {
        // El ciclo durable acepta Requested → Rejected por schema (ADR-0036 §5): los eventos
        // emitidos llevan la ToolCall a un estado terminal consistente con el outcome.
        var (outcome, _, sink) = Run("{}");

        var state = ToolCallState.Requested;
        foreach (var evt in sink.Events)
        {
            state = StateMachines.ApplyToolCall(state, evt);
        }

        Assert.Equal(outcome.FinalState, state);
        Assert.Equal(ToolCallState.Rejected, state);
    }

    [Fact]
    public void FakeTool_with_extra_arguments_still_runs()
    {
        // FakeTool declara "{}": argumentos con campos extra deben seguir ejecutándose
        // (las tools de simulación no declaran superficie).
        var sink = new EventSink();
        var runtime = ToolRuntime.For(FakeCatalog.Default(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow), sink.Emit);

        var outcome = runtime.Run(
            new ValidatedToolCall(ToolCallId.New(), new ToolId("fake.write"), "pc",
                "{\"path\":\"doc.txt\",\"unexpected\":\"x\"}"),
            new ToolPreparationContext("sim", DateTimeOffset.UtcNow), new ToolExecutionContext("sim"),
            true, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
        Assert.True(sink.Count("toolcall.succeeded") == 1);
    }

    [Fact]
    public void Every_builtin_tool_declares_a_parseable_object_schema()
    {
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(),
            includeSimulationTools: false, includeMutationTools: true);

        var definitions = new List<ToolDefinition>(hostTools.Catalog().Definitions());
        Assert.NotEmpty(definitions);
        Assert.Contains(definitions, d => d.Name == "filesystem.read");
        Assert.Contains(definitions, d => d.Name == "filesystem.write");
        Assert.Contains(definitions, d => d.Name == "search.text");

        foreach (var definition in definitions)
        {
            using var doc = JsonDocument.Parse(definition.InputSchemaJson);
            Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object,
                "El schema de '" + definition.Name + "' debe ser un objeto JSON");
        }

        // Las tools de simulación también: schema "{}" mínimo.
        foreach (var definition in FakeCatalog.Default().Definitions())
        {
            using var doc = JsonDocument.Parse(definition.InputSchemaJson);
            Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object,
                "El schema de '" + definition.Name + "' debe ser un objeto JSON");
        }
    }

    [Fact]
    public void Write_tool_schema_accepts_null_expected_version_but_not_other_types()
    {
        // filesystem.write acepta expectedVersion: null (crear archivo nuevo, ADR-0044 §5);
        // el schema lo declara como ["string","null"] y cualquier otro tipo se rechaza.
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(),
            includeSimulationTools: false, includeMutationTools: true);
        var write = hostTools.Catalog().Find(new ToolId("filesystem.write"));
        Assert.NotNull(write);

        Assert.Null(ToolSchemaValidator.Validate("{\"path\":\"a.txt\",\"content\":\"c\"}", write!.Descriptor.InputSchema));
        Assert.Null(ToolSchemaValidator.Validate(
            "{\"path\":\"a.txt\",\"content\":\"c\",\"expectedVersion\":null}", write.Descriptor.InputSchema));
        Assert.Null(ToolSchemaValidator.Validate(
            "{\"path\":\"a.txt\",\"content\":\"c\",\"expectedVersion\":\"sha\"}", write.Descriptor.InputSchema));
        Assert.NotNull(ToolSchemaValidator.Validate(
            "{\"path\":\"a.txt\",\"content\":\"c\",\"expectedVersion\":123}", write.Descriptor.InputSchema));
    }

    [Fact]
    public void Search_tool_schema_requires_pattern_and_rejects_unknown_fields()
    {
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(),
            includeSimulationTools: false, includeMutationTools: true);
        var search = hostTools.Catalog().Find(new ToolId("search.text"));
        Assert.NotNull(search);
        var schema = search!.Descriptor.InputSchema;

        Assert.Null(ToolSchemaValidator.Validate("{\"pattern\":\"needle\"}", schema));
        Assert.Null(ToolSchemaValidator.Validate(
            "{\"pattern\":\"needle\",\"regex\":true,\"path\":\"src\",\"glob\":\"*.cs\",\"maxResults\":10,"
            + "\"caseSensitive\":false}", schema));
        Assert.NotNull(ToolSchemaValidator.Validate("{}", schema));
        Assert.NotNull(ToolSchemaValidator.Validate("{\"regex\":true}", schema));
        Assert.NotNull(ToolSchemaValidator.Validate("{\"pattern\":\"needle\",\"surprise\":1}", schema));
        Assert.NotNull(ToolSchemaValidator.Validate("{\"pattern\":\"needle\",\"maxResults\":\"10\"}", schema));
    }
}
