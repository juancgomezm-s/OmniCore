# ADR-0045 — Cuestionarios estructurados y respuestas humanas

- **Estado:** Aceptada (2026-09-27)
- **Complementa y prevalece sobre:** ADR-0003 y ADR-0034 para `InteractionKind.Question`
- **Relacionado:** ADR-0001, ADR-0013, ADR-0018, ADR-0019, ADR-0030, ADR-0033, ADR-0035, ADR-0036 y ADR-0040
- **Roadmap:** contrato, tool y modo plain en M3; frame TUI en M4

## Contexto

ADR-0034 incluyó `InteractionKind.Question`, `WaitingForInput` y overlays, pero su respuesta
original contiene un único `OptionId`. Ese contrato sirve para permisos y confirmaciones simples,
pero no representa:

- varias preguntas en un mismo frame;
- selección única o múltiple por pregunta;
- respuesta de texto libre;
- una opción `Otro` con texto escrito por el usuario;
- validaciones por pregunta;
- cancelación y reanudación de un cuestionario durable.

Las preguntas son datos propuestos por el modelo. El modelo no crea eventos ni decide si una
respuesta es válida: el Host valida el esquema, publica la interacción y valida la respuesta.

## Decisión

### 1. Una familia tipada de prompts y respuestas

`InteractionRequest` conserva el envelope común de ADR-0034 y reemplaza `Options` como forma única
por un payload discriminado:

```csharp
public abstract record InteractionPrompt;

public sealed record ChoicePrompt(
    IReadOnlyList<InteractionOption> Options,
    string DefaultOptionId) : InteractionPrompt;

public sealed record QuestionnairePrompt(
    string Title,
    string? Description,
    IReadOnlyList<QuestionField> Questions,
    LocalizedText SubmitLabel,
    LocalizedText CancelLabel) : InteractionPrompt;

public sealed record InteractionRequest(
    InteractionId Id,
    InteractionKind Kind,
    InteractionSubject Subject,
    InteractionPrompt Prompt,
    DateTimeOffset? ExpiresAt,
    LaneId? Lane,
    TaskId? Task,
    PlanItemId? PlanItem,
    int QueuePosition,
    int QueueLength);
```

Permisos, aprobaciones y conflictos continúan con `ChoicePrompt`. `InteractionKind.Question` usa
`QuestionnairePrompt`, aunque contenga una sola pregunta.

La respuesta también es una unión discriminada:

```csharp
public abstract record InteractionResponse;

public sealed record ChoiceResponse(string OptionId) : InteractionResponse;

public sealed record QuestionnaireResponse(
    IReadOnlyList<QuestionAnswer> Answers,
    bool Cancelled) : InteractionResponse;

public sealed record RespondToInteraction(
    InteractionId Id,
    InteractionResponse Response);               // WireCommand
```

El antiguo `RespondToInteraction { InteractionId, OptionId }` se interpreta como
`ChoiceResponse` mediante compatibilidad/upcasting de protocolo. No se reutiliza para cuestionarios.

### 2. Tipos de pregunta

```csharp
public enum QuestionKind
{
    SingleChoice,
    MultipleChoice,
    FreeText
}

public sealed record QuestionField(
    string Id,
    string Prompt,
    string? HelpText,
    QuestionKind Kind,
    IReadOnlyList<QuestionOption> Options,
    OtherInput? Other,
    bool Required,
    int? MinSelections,
    int? MaxSelections,
    int? MaxTextLength);

public sealed record QuestionOption(
    string Id,
    string Label,
    string? Description);

public sealed record OtherInput(
    string OptionId,
    string Label,
    string? Placeholder,
    bool TextRequired,
    int MaxTextLength);

public sealed record QuestionAnswer(
    string QuestionId,
    IReadOnlyList<string> SelectedOptionIds,
    string? Text,
    string? OtherText);
```

`Otro` no se detecta comparando texto traducido. `OtherInput` tiene un id estable y forma parte del
schema. En `MultipleChoice`, puede coexistir con otras opciones. En `SingleChoice`, seleccionarlo
desmarca cualquier otra opción.

Título, preguntas y opciones son contenido del modelo y se muestran tal cual (ADR-0040). Solo el
chrome del cliente —Enviar, Cancelar, requerido y mensajes de validación— usa `LocalizedText`.

### 3. Tool del modelo

El modelo solicita información mediante la tool interna `user.ask`:

```text
user.ask({ title, description?, questions[] })
```

- `EffectClass = None`; no toca filesystem, procesos ni red.
- Pasa por el pipeline normal de ToolCalls para durabilidad y observabilidad.
- El runtime convierte una invocación válida en `InteractionRequested`; el modelo nunca emite el
  evento directamente ni puede responder su propia pregunta.
- Solo puede existir una interacción activa por Lane. Las demás usan la cola global de ADR-0034.
- La tool call cuenta contra `MaxToolCalls` y el cuestionario contra un límite específico para
  evitar loops de preguntas.

Límites iniciales por request:

| Recurso | Máximo |
|---|---:|
| Preguntas | 5 |
| Opciones por pregunta | 8 |
| Caracteres del prompt | 500 |
| Caracteres por etiqueta | 120 |
| Texto libre u `OtherText` | 2,000 |

Estos límites son configurables y el Host los valida. El modelo recibe un error de tool tipado si
el cuestionario es inválido.

`user.ask` no sirve para solicitar API keys, passwords, tokens ni credenciales. Esos valores usan
`ISecretProvider`/`ICredentialStore` (ADR-0018). La UI advierte y el Host rechaza prompts que se
declaren como credenciales.

### 4. Validación en el Host

Antes de publicar:

- ids de preguntas y opciones únicos y no vacíos;
- entre 1 y 5 preguntas;
- `SingleChoice`/`MultipleChoice` con opciones o `OtherInput`;
- `FreeText` sin opciones;
- `MinSelections` y `MaxSelections` coherentes con el tipo y el número de opciones;
- textos y payload dentro de límites;
- contenido redactado antes de journal, logs o artifacts.

Al responder:

- cada `QuestionId` debe pertenecer a la solicitud vigente y aparecer una sola vez;
- cada option id debe pertenecer a su pregunta y no repetirse;
- `SingleChoice` acepta exactamente una selección cuando es requerida;
- `MultipleChoice` respeta mínimos y máximos;
- `OtherText` solo se acepta si se seleccionó `Other.OptionId` y respeta `TextRequired`/longitud;
- preguntas requeridas no pueden omitirse;
- una respuesta tardía, duplicada o para otra interacción se rechaza;
- el cliente no puede cambiar el schema ni enviar preguntas nuevas.

El botón Submit permanece deshabilitado mientras la validación local falle, pero la validación del
Host es la autoridad.

### 5. Frame y experiencia de usuario

`QuestionnaireOverlayModel` es un modelo declarativo de `OmniCore.Client`. Terminal.Gui solo lo
renderiza en `OmniCore.Cli`.

```text
┌ Necesito algunos datos ──────────────────────────────┐
│ 1. ¿Qué enfoque prefieres?                           │
│    ( ) Mantener compatibilidad                       │
│    (•) Simplificar API                               │
│    ( ) Otro: [____________________________________]  │
│                                                     │
│ 2. ¿Qué validaciones ejecuto?                        │
│    [x] Unitarias   [ ] Integración   [x] Lint        │
│    [ ] Otro: [____________________________________]  │
│                                                     │
│                         [Enviar] [Cancelar]           │
└─────────────────────────────────────────────────────┘
```

- radio buttons para `SingleChoice`;
- checkboxes para `MultipleChoice`;
- editor para `FreeText`;
- seleccionar `Otro` activa y enfoca su editor;
- Tab/Shift+Tab navegan, Space selecciona, Enter envía cuando es válido y Esc cancela;
- el frame es desplazable y responsive;
- foco, selección, requerido y errores siempre tienen señal textual; el color no es la única señal;
- la conversación y el sidebar continúan actualizándose detrás del overlay.

Cerrar el frame equivale a **Cancelar**, no a escoger una opción ni a `Deny`. La cancelación se
devuelve al modelo como resultado estructurado para que pueda continuar con una alternativa segura
o bloquear la Task explicando qué información faltó.

En plain con TTY se muestra el mismo formulario secuencialmente. Las opciones múltiples aceptan
ids separados y después preguntan por `Otro` cuando corresponda.

### 6. Sin cliente interactivo

La regla `Ask → Deny` de ADR-0003 sigue vigente para permisos y operaciones de riesgo. No aplica a
`InteractionKind.Question`:

- la interacción queda durable y la Lane deriva `WaitingForInput`;
- una invocación síncrona sin TTY devuelve `InputRequired`, sin inventar respuesta; el Run permanece
  activo y la Lane sigue esperando;
- `--json` emite `interaction.requested` con el schema y el resultado `InputRequired`;
- al reconectar o hacer resume, la interacción pendiente se vuelve a publicar;
- ningún timeout selecciona automáticamente una respuesta. Si existe expiración, produce
  `InteractionExpired` y una respuesta de tool `QuestionExpired`.

### 7. Durabilidad, privacidad y respuesta al modelo

- `InteractionRequested` y `InteractionResolved` siguen siendo canónicos.
- El schema completo y la respuesta se guardan como artifacts content-addressed referenciados por
  los eventos; el journal no duplica texto libre voluminoso.
- Los eventos incluyen tipo de respuesta, hash del artifact, estado (`Submitted | Cancelled |
  Expired`) y causa (`User | Timeout | NoClient` cuando aplique).
- Todo texto pasa por `RedactionPolicy` antes de journal, artifact, audit y contexto.
- La respuesta válida vuelve al mismo Turn como `ToolResult` estructurado de `user.ask`; si se
  reanuda después de un crash, no se vuelve a preguntar ni a inferir.
- El siguiente request al modelo contiene ids, etiquetas seleccionadas y textos permitidos; no una
  concatenación ambigua.

### 8. Estados y concurrencia

- Una `Question` no resuelta deriva `LaneActivity.WaitingForInput`; no crea un nuevo estado canónico.
- La cola global muestra una interacción por vez. Un cuestionario es una sola interacción aunque
  contenga varias preguntas.
- `run.interrupt` puede interrumpir generación, pero no descarta el formulario pendiente.
- `run.cancel` cancela el Run y resuelve la interacción como cancelada por el runtime.
- Mientras espera, la Lane no consume Turns ni tokens.

### 9. Criterios de aceptación

1. Un cuestionario puede contener una o varias preguntas.
2. `SingleChoice`, `MultipleChoice`, `FreeText` y `Otro + texto` funcionan end-to-end.
3. `Otro` se identifica por schema y no por su texto localizado.
4. Multi-select acepta varias opciones y `Otro` simultáneamente.
5. El Host rechaza ids desconocidos, duplicados, omisiones requeridas y límites inválidos.
6. Cancelar devuelve un resultado estructurado al modelo sin inventar respuesta.
7. Un modelo no puede responder su propia interacción.
8. Sin TTY se obtiene `InputRequired`, nunca `Deny` ni una opción automática.
9. La interacción sobrevive a restart y se resuelve exactamente una vez.
10. La respuesta se entrega al modelo una sola vez y queda correlacionada con la ToolCall original.
11. Texto sensible no aparece en journal, logs, audit ni artifacts sin redacción.
12. TUI y plain producen la misma `QuestionnaireResponse` para las mismas elecciones.
13. Snapshots cubren teclado, scroll, errores, localización es/en y terminal estrecha.

## Clasificación

| Elemento | Categoría |
|---|---|
| DTOs tipados, schema v2/upcaster, `user.ask`, validación Host, durabilidad, resume y plain TTY | **M3** |
| `QuestionnaireOverlayModel` y frame TUI con radio/checkbox/texto/`Otro` | **M4 — track TUI v0** |
| Formularios dinámicos de extensiones | **M8+**, sujetos al mismo schema y límites |
| Aprobación o respuesta desde dispositivos remotos | **Deferable** |
