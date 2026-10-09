namespace OmniCore.Tools;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Tool de simulación de M1 (ADR-0041 §2): implementa el pipeline real (ITool) sin I/O.
/// El escenario declara su clase de efecto; ExecuteAsync devuelve un ToolResult fijo.
/// El efecto se registra a través del ReconciliationSpec cuando la clase es Reconcilable.
/// </summary>
public sealed class FakeTool : ITool
{
    private readonly ToolDescriptor _descriptor;

    private readonly EffectClass _effect;

    public FakeTool(string toolId, string description, EffectClass effect, bool readOnly)
    {
        _effect = effect;
        _descriptor = new ToolDescriptor(
            new ToolId(toolId),
            description,
            new InputSchema("{}"),
            new string[0],
            readOnly,
            false,
            ToolRisk.Low,
            ComponentSource.Core(),
            ToolProtection.None,
            _effect);
    }

    public static FakeTool Read(string id) => new(id, "Simulated read (M1)", EffectClass.None, true);

    public static FakeTool Write(string id) => new(id, "Simulated write (M1)", EffectClass.Reconcilable, false);

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        var reconciliation = _effect == EffectClass.Reconcilable
            ? new ReconciliationSpec("pre", "post", call.ToolCallId.ToString())
            : null;
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, _effect,
            ResourceClaims.Empty(), ToolRisk.Low, reconciliation);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var result = new ToolResult("ok (" + intent.Intent.ToolId + ")", "ok", null, 2, false,
            EffectOutcome.Applied);
        return System.Threading.Tasks.Task.FromResult(result);
    }
}

/// <summary>Registration rejection diagnostic required by ADR-0027 §§1 and 5.</summary>
public sealed class ToolRegistrationRejected : InvalidOperationException
{
    public ToolId ToolId { get; }

    public ToolRegistrationRejected(ToolId toolId, string message)
        : base("ToolRegistrationRejected: " + toolId + ": " + message)
    {
        ToolId = toolId;
    }
}

/// <summary>Simulation tool catalog (ADR-0041 §2: FakeTools).</summary>
public sealed class FakeCatalog
{
    private readonly Dictionary<ToolId, ITool> _tools = new();

    /// <summary>Adds a tool only when its canonical id is not already registered (ADR-0027).</summary>
    /// <exception cref="ToolRegistrationRejected">The id is already registered.</exception>
    public FakeCatalog Add(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var descriptor = tool.Descriptor;
        if (_tools.ContainsKey(descriptor.Id))
        {
            throw new ToolRegistrationRejected(descriptor.Id,
                "A tool with this canonical id is already registered; replacement is not supported.");
        }

        _tools.Add(descriptor.Id, tool);
        return this;
    }

    public static FakeCatalog Default() => new FakeCatalog()
        .Add(FakeTool.Read("fake.read"))
        .Add(FakeTool.Read("fake.test"))
        .Add(FakeTool.Write("fake.write"));

    public ITool? Find(ToolId id) => _tools.TryGetValue(id, out var tool) ? tool : null;

    public bool Contains(ToolId id) => _tools.TryGetValue(id, out var _) != false;

    /// <summary>Registered tools for Host composition; callers cannot mutate the catalog through this view.</summary>
    public IReadOnlyList<ITool> RegisteredTools() => _tools.Values.ToArray();

    /// <summary>Todas las tools del catálogo como ToolDefinition (para BuildBody del provider).</summary>
    public IReadOnlyList<ToolDefinition> Definitions()
    {
        var defs = new List<ToolDefinition>();
        foreach (var kv in _tools)
        {
            var d = kv.Value.Descriptor;
            defs.Add(new ToolDefinition(d.Id.ToString(), d.Description, d.InputSchema.ToString()));
        }

        return defs;
    }
}
