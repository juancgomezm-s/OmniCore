namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Contrato del Model Provider (ADR-0005 §2). El Agent Runtime solo ve esta interfaz (INV-011).
/// </summary>
public interface IModelProvider
{
    ProviderCapabilities Capabilities { get; }

    /// <summary>Stream de eventos del modelo; CompleteAsync es un helper que lo agrega.</summary>
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>Solicitud al modelo (ADR-0005 §2).</summary>
public sealed class ModelRequest
{
    public ModelSelection Model { get; }

    public IReadOnlyList<ModelMessage> Messages { get; }

    public string? Instructions { get; }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public ToolChoice ToolChoice { get; }

    public OutputConstraint? Output { get; }

    public ReasoningRequest? Reasoning { get; }

    public CacheHints? Cache { get; }

    public ProviderState? Continuation { get; }

    public ModelRequest(ModelSelection model, IReadOnlyList<ModelMessage> messages, string? instructions,
        IReadOnlyList<ToolDefinition> tools, ToolChoice toolChoice, OutputConstraint? output,
        ReasoningRequest? reasoning, CacheHints? cache, ProviderState? continuation)
    {
        Model = model;
        Messages = messages;
        Instructions = instructions;
        Tools = tools;
        ToolChoice = toolChoice;
        Output = output;
        Reasoning = reasoning;
        Cache = cache;
        Continuation = continuation;
    }
}

/// <summary>Definición de tool proyectada hacia el modelo.</summary>
public sealed class ToolDefinition
{
    public string Name { get; }

    public string Description { get; }

    public string InputSchemaJson { get; }

    public ToolDefinition(string name, string description, string inputSchemaJson)
    {
        Name = name;
        Description = description;
        InputSchemaJson = inputSchemaJson;
    }
}

/// <summary>Solicitud explícita de tool (spec §37).</summary>
public sealed class ToolChoice
{
    public string Mode { get; }

    public string? ToolName { get; }

    private ToolChoice(string mode, string? toolName)
    {
        Mode = mode;
        ToolName = toolName;
    }

    public static ToolChoice Auto() => new("auto", null);

    public static ToolChoice Exact(string toolName) => new("exact", toolName);

    public static ToolChoice None() => new("none", null);
}

/// <summary>Capacidades del provider (ADR-0031 §3: qué reporta de uso/costo/cuota).</summary>
public sealed class ProviderCapabilities
{
    public bool ReportsUsage { get; }

    public bool ReportsCost { get; }

    public bool ReportsQuota { get; }

    public ProviderCapabilities(bool reportsUsage, bool reportsCost, bool reportsQuota)
    {
        ReportsUsage = reportsUsage;
        ReportsCost = reportsCost;
        ReportsQuota = reportsQuota;
    }

    public static ProviderCapabilities Local() => new(true, false, false);
}

/// <summary>Evento de stream normalizado (ADR-0005 §2).</summary>
public abstract record ModelStreamEvent;

public sealed record ResponseStarted(int Index) : ModelStreamEvent;

public sealed record BlockStarted(int Index, string Kind) : ModelStreamEvent;

public sealed record TextDelta(int Index, string Text) : ModelStreamEvent;

public sealed record ReasoningDelta(int Index, string Text) : ModelStreamEvent;

public sealed record ToolArgumentsDelta(int Index, string PartialJson) : ModelStreamEvent;

public sealed record BlockCompleted(int Index, ContentBlock Block) : ModelStreamEvent;

public sealed record UsageUpdated(TokenUsage Usage) : ModelStreamEvent;

public sealed record ResponseCompleted(ModelResponse Response) : ModelStreamEvent;

public sealed record ResponseFailed(string ErrorType, string Message) : ModelStreamEvent;