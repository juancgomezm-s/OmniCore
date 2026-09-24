# ADR-0005 — Model Runtime provider-native

- **Estado:** Aceptada — rev. 2 (2026-09-24)
- **Rev. 1:** "un único provider OpenAI-compatible como base". Queda **reemplazada**: ningún protocolo concreto (`/v1/chat/completions` incluido) es la abstracción fundamental de OmniCore.
- **Spec:** §17–§19, INV-010, INV-011
- **Relacionado:** ADR-0011 (conexión, credenciales, servidor local), ADR-0001 (artifacts), ADR-0018 (secretos)
- **Diagrama:** [arquitectura §9](../architecture/arquitectura.md#9-model-provider-architecture)

## Decisión

### 1. Tres familias de providers nativos

```text
Protocolo nativo del provider → Provider Adapter → OmniCore Model Representation → Agent Runtime
```

| Adapter | Protocolo | Cubre |
|---|---|---|
| `OpenAIResponsesProvider` | Responses API nativa | Modelos OpenAI con API key. Perfil `codex` para la suscripción ChatGPT (ADR-0011 §3.4) |
| `AnthropicMessagesProvider` | Messages API nativa | Anthropic. **Nunca** a través de un adapter OpenAI-compatible |
| `OpenAiChatCompatibleProvider` | `/v1/chat/completions` | ik_llama / llama.cpp, OpenRouter cuando corresponde, y otros endpoints realmente compatibles |

- Las diferencias dentro de una familia se declaran como compat flags (ADR-0011 §8, modelo de Pi), nunca con `if (provider == ...)`.
- El Agent Runtime **no sabe** con qué provider habla.

### 2. Contrato neutral

```csharp
public interface IModelProvider
{
    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}
// CompleteAsync es un helper que agrega el stream; no forma parte del contrato de cada adapter.

public sealed record ModelRequest
{
    public required ModelSelection Model { get; init; }
    public required IReadOnlyList<ModelMessage> Messages { get; init; }  // Role + ContentBlocks
    public string? Instructions { get; init; }                            // system; cada adapter decide cómo enviarlo
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];       // proyección del ToolPlan
    public ToolChoice ToolChoice { get; init; }
    public OutputConstraint? Output { get; init; }                        // JsonSchema | Grammar (ADR-0007)
    public ReasoningRequest? Reasoning { get; init; }                     // effort / budget
    public CacheHints? Cache { get; init; }                               // breakpoints lógicos (ADR-0011 §9)
    public ProviderState? Continuation { get; init; }                     // estado opaco del Turn anterior, si aplica
}

public sealed record ModelMessage(MessageRole Role, IReadOnlyList<ContentBlock> Content);

public abstract record ContentBlock;
public sealed record TextBlock(string Text) : ContentBlock;
public sealed record ToolCallBlock(ToolCallId Id, string? ProviderCallId, string ToolName, JsonElement Arguments) : ContentBlock;
public sealed record ToolResultBlock(ToolCallId Id, IReadOnlyList<ContentBlock> Content, bool IsError) : ContentBlock;
public sealed record ReasoningBlock(string? VisibleText, ReasoningVisibility Visibility, ProviderOpaque? Opaque) : ContentBlock;
public sealed record CitationBlock(string Text, SourceRef Source) : ContentBlock;
public sealed record MediaBlock(string MediaType, ArtifactRef Content) : ContentBlock;              // imagen/documento (futuro)
public sealed record ProviderOpaqueBlock(ProviderOpaque Opaque) : ContentBlock;

public sealed record ProviderOpaque(
    ProviderFamily Family,         // OpenAIResponses | AnthropicMessages | OpenAiChatCompatible
    string ModelId,
    string Kind,                   // p. ej. "anthropic.thinking_signature", "openai.encrypted_reasoning"
    ArtifactRef Payload,           // bytes/JSON tal como llegaron; Sensitivity = Sensitive
    ReplayPolicy Replay);          // SameModel | SameFamily | Never

public sealed record ModelResponse
{
    public required IReadOnlyList<ContentBlock> Content { get; init; }
    public required StopReason StopReason { get; init; }
    public required TokenUsage Usage { get; init; }         // input, output, cacheRead, cacheWrite, reasoning
    public ProviderState? State { get; init; }              // continuación opaca, p. ej. response id
    public ProviderMetadata Metadata { get; init; }         // request id, modelo resuelto, service tier
}

public enum StopReason { EndTurn, ToolUse, MaxOutputTokens, StopSequence, ContextOverflow, ContentFilter, Refusal, Cancelled, Error }

public abstract record ModelStreamEvent;
// ResponseStarted, BlockStarted(index, kind), TextDelta, ReasoningDelta, ToolArgumentsDelta(index, json),
// BlockCompleted(index, ContentBlock), UsageUpdated, ResponseCompleted(ModelResponse), ResponseFailed(ModelError)
```

Estos tipos viven en **Domain**: son datos persistidos en eventos y artifacts. `IModelProvider` vive en **Abstractions**.

**`ProviderCapabilities.UsageReporting`** (agregado el 2026-09-24, ADR-0031 §3) declara qué informa el provider:

- `Usage` (tokens);
- `Cost` (`Reported` si lo informa el provider, `Estimated` si se calcula con el precio declarado);
- `Quota` (créditos o cuota restantes).

El cliente nunca muestra un dato que el provider no declara: una cuota no informada se muestra como `—`.

### 3. Qué se normaliza y qué no

| Información | ¿Se normaliza? | ¿Se persiste para resume? | ¿Entra al Context Engine? | ¿Es sensible? |
|---|---|---|---|---|
| Texto | Sí (`TextBlock`) | Sí | Sí | No |
| Tool calls | Sí (`ToolCallBlock` + mapeo `ProviderCallId`) | Sí | Sí | Los argumentos pasan por el redactor |
| Resultados de tools | Sí (`ToolResultBlock`) | Sí (artifact si es grande) | Sí, con presupuesto | Pasan por el redactor |
| Razonamiento visible o resumen | Sí (`ReasoningBlock.VisibleText`) | Sí | Según `ContextPolicy`; por defecto solo en el Turn en curso | No |
| Firma de thinking, `redacted_thinking`, reasoning cifrado | **No** (`ProviderOpaque`) | **Sí**, porque se necesita para continuar | Solo se reenvía intacto al mismo destino según `ReplayPolicy`; el Context Engine solo cuenta sus tokens | **Sí** |
| Estado de conversación (`previous_response_id`, items cifrados con `store:false`) | **No** (`ProviderState`) | Sí | No; el adapter lo usa directamente | Sí |
| Usage | Sí (`TokenUsage`) | Sí | No; va a métricas | No |
| Metadata (request id, modelo resuelto, headers de rate limit) | Parcial | Request id y modelo sí | No | Los headers de auth nunca se guardan |

### 4. Reglas de continuidad

1. Un `ProviderOpaque` se reenvía **sin modificar** solo si el destino cumple su `ReplayPolicy`. Ejemplo: Anthropic exige devolver los bloques de thinking con su firma dentro del mismo Turn con tool use.
2. Si el siguiente Turn va a otro modelo o familia (escalación o cambio de modelo), el Context Engine proyecta el bloque según una regla de degradación portada de Pi (`transform-messages`): el razonamiento visible se convierte en texto marcado o se omite, y lo opaco se descarta.
3. El adapter es el único componente que interpreta `ProviderOpaque` y `ProviderState`. El Core los trata como bytes con metadata.
4. Todo lo opaco es `Sensitivity = Sensitive` (ADR-0001 §9): no va a logs ni telemetría, ni se muestra en `/context`.

## Propiedad de los tipos de modelo (revisión integral, 2026-09-24)

Resuelve la superposición de tipos detectada en la revisión (modelos F17):

| Tipo | Es dueño de | No debe contener |
|---|---|---|
| `DeclaredCapabilities` (ADR-0007; reemplaza el `ModelDescriptor` de la spec §17) | hechos del modelo: contexto, output máximo, modalidades, soporte de tools, esfuerzos de razonamiento, `UsageReporting` | políticas |
| `ProviderDescriptor` | familia, endpoint, auth y **compat flags** (ADR-0011 §8) | datos por modelo |
| `EffectiveModelProfile` (ADR-0007) | hechos + traits resueltos **por `ModelQualificationKey`** | decisiones por Turn |
| `HarnessPolicy` | `ToolCallFormat`, `ToolMode`, `MaxVisibleTools`, `Guidance`, `PlanControl`… | hechos |
| `EffectiveExecutionProfile` (spec §20) | intersección **por Turn**: `EffectiveModelProfile` ∩ requisitos de la Task ∩ `AgentProfile` ∩ permisos ∩ disponibilidad | — |
| `ModelSelection` | valores resueltos para **una** request: modelo, esfuerzo, `ToolMode`, presupuesto de contexto (`long`) | — |
| `ModelRequest.Reasoning` | se deriva de `ModelSelection.Effort`; no es una segunda fuente | — |

## Clasificación

| Elemento | Categoría |
|---|---|
| Tipos de contenido, `ModelResponse`, `StopReason`, `TokenUsage`, `ProviderOpaque`/`ProviderState` como contratos de Domain | **Necesario antes de M2** (ya forman parte de los eventos de Turn; M1 los usa en simulación) |
| `OpenAiChatCompatibleProvider` (M2); `OpenAIResponsesProvider` y `AnthropicMessagesProvider` (M5) | **Contract now / implementation later** |
| Media, citations y documentos | **Deferable** (el tipo existe; el soporte no) |
