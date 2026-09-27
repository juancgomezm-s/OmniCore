namespace OmniCore.Domain;

/// <summary>
/// Contrato neutral del Model Runtime (ADR-0005 §2). Estos tipos viven en Domain porque se
/// persisten en eventos y artifacts. El Agent Runtime no conoce providers concretos (INV-011).
/// </summary>

/// <summary>Familia de protocolo nativo del provider.</summary>
public enum ProviderFamily
{
    OpenAIResponses,
    AnthropicMessages,
    OpenAiChatCompatible,
}

/// <summary>Rol de un mensaje de la conversación hacia el modelo.</summary>
public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>Razón por la que el modelo terminó la generación.</summary>
public enum StopReason
{
    EndTurn,
    ToolUse,
    MaxOutputTokens,
    StopSequence,
    ContextOverflow,
    ContentFilter,
    Refusal,
    Cancelled,
    Error,
}

/// <summary>Visibilidad del razonamiento del modelo.</summary>
public enum ReasoningVisibility
{
    Full,
    Summarized,
    Omitted,
}

/// <summary>Política de replay de un bloque opaco (ADR-0005 §4).</summary>
public enum ReplayPolicy
{
    SameModel,
    SameFamily,
    Never,
}

/// <summary>Bloque de contenido abstracto (ADR-0005 §2).</summary>
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolCallBlock(
    ToolCallId Id,
    string? ProviderCallId,
    string ToolName,
    string ArgumentsJson) : ContentBlock;

public sealed record ToolResultBlock(ToolCallId Id, IReadOnlyList<ContentBlock> Content, bool IsError)
    : ContentBlock;

public sealed record ReasoningBlock(string? VisibleText, ReasoningVisibility Visibility, ArtifactRef? OpaquePayload)
    : ContentBlock;

public sealed record CitationBlock(string Text, string SourceRef) : ContentBlock;

/// <summary>Estado opaco del provider: se reenvía intacto solo a destinos compatibles (ADR-0005 §4).</summary>
public sealed record ProviderOpaque(
    ProviderFamily Family,
    string ModelId,
    string Kind,
    ArtifactRef Payload,
    ReplayPolicy Replay) { }

/// <summary>Bloque de contenido opaco embebido en una respuesta.</summary>
public sealed record ProviderOpaqueBlock(ProviderOpaque Opaque) : ContentBlock;

/// <summary>Uso de tokens normalizado (ADR-0011 §6).</summary>
public sealed record TokenUsage(
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning) { }

/// <summary>Metadata del provider (request id, modelo resuelto; nunca headers de auth).</summary>
public sealed record ProviderMetadata(string RequestId, string Model, string? ServiceTier) { }

/// <summary>Estado de conversación opaco para continuar (p. ej. response id).</summary>
public sealed record ProviderState(string Kind, string PayloadJson) { }

/// <summary>Respuesta completa normalizada del modelo.</summary>
public sealed record ModelResponse(
    IReadOnlyList<ContentBlock> Content,
    StopReason StopReason,
    TokenUsage Usage,
    ProviderState? State,
    ProviderMetadata Metadata) { }

/// <summary>Mensaje hacia el modelo.</summary>
public sealed record ModelMessage(MessageRole Role, IReadOnlyList<ContentBlock> Content) { }

/// <summary>Restricción de salida (structured output, ADR-0007 §3).</summary>
public sealed record OutputConstraint(string Kind, string SchemaJson) { }

/// <summary>Formato de tool calls soportado (ADR-0007 §3).</summary>
public enum ToolCallFormat
{
    Native,
    Grammar,
    PromptedJson,
}

/// <summary>Nivel de guía de herramientas.</summary>
public enum GuidanceLevel
{
    Off,
    DomainOnly,
    Full,
}

/// <summary>Cuánto controla el runtime el plan (ADR-0016 §8, ADR-0007 §3).</summary>
public enum PlanControl
{
    RuntimeDriven,
    Assisted,
    ModelDriven,
}

/// <summary>Solicitud de razonamiento (effort / budget).</summary>
public sealed record ReasoningRequest(string Kind, int? BudgetTokens) { }

/// <summary>Hints de caché (ADR-0011 §9).</summary>
public sealed record CacheHints(int MaxBreakpoints, string Mode) { }