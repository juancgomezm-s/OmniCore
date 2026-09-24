# ADR-0033 — Render de conversación y composer con referencias estructuradas

- **Estado:** Aceptada (2026-09-24)
- **Relacionado:** ADR-0030 (cliente), ADR-0024 (commands), ADR-0029 (procedencia), ADR-0014 (permisos de lectura)
- **Spec:** §60 (`SendInput`), INV-013 (tools sin UI)
- **Diagrama:** [arquitectura §34](../architecture/arquitectura.md#34-conversación-composer-e-interacciones)

## Decisión

### 1. Conversación = bloques semánticos, no log de eventos

`ActivityProjector` (en `ClientProjection`) convierte `WireEvent`s en `ConversationBlock`s:

| Bloque | Origen | Ejemplo (Normal) |
|---|---|---|
| `UserMessageBlock` | `SendInput` propio | texto + chips de referencias |
| `AssistantMessageBlock` | `TextBlock`s del modelo (streaming) | markdown |
| `ActivityBlock` | ciclo de vida de ToolCall (ADR-0004) + `ToolPresentation` | `● Searching repository` → `✓ Found 4 relevant files` |
| `DelegationBlock` | Lane de subagente creada o terminada | `◆ coder (Qwen 27B) editing ContextMaterializer.cs` |
| `PlanChangeBlock` | `PlanRevised` y transiciones relevantes | `✓ Plan 3/5: Fix AuthenticationService` |
| `ValidationBlock` | gates y tests | `● Running tests` → `✓ 143 passed` / `✗ 2 failed` |
| `InteractionBlock` | `InteractionRequested` / resuelto (ADR-0034) | `! Permission requested: dotnet test` → `✓ Allowed for Run` |
| `NoticeBlock` | `ProgressStalled`, escalaciones, errores tipados | `! Sin progreso en P3 tras 6 turns — replanificando` |

- **Un solo bloque por ToolCall:** un `ActivityBlock` **se actualiza en su lugar** (Running → Succeeded/Failed); no genera una línea por evento.
- **Verbosidad** (`Normal | Verbose | Trace`):
  - `Normal`: bloques resumidos;
  - `Verbose`: agrega argumentos normalizados, duración, tokens del Turn y `ArtifactRef`s;
  - `Trace`: agrega los eventos crudos intercalados.

  El Engine emite **los mismos eventos** en los tres niveles; solo cambia el renderer.

### 2. `ToolPresentation`: metadata sin lógica de UI

Para respetar INV-013 (tools sin rendering), `ToolDescriptor` declara **datos** de presentación, no código:

```csharp
public sealed record ToolPresentation(
    ActivityCategory Category,         // Search | Read | Edit | Execute | Test | Build | Plan | Delegate | Network | Other
    string RunningLabel,               // "Searching {query}"
    string SucceededLabel,             // "Found {resultCount} files"
    string FailedLabel,                // "Search failed"
    IReadOnlyList<string> SummaryFields); // campos del ToolIntent o ToolResult que se pueden mostrar (ya redactados)
```

- **Quién interpreta:** el cliente interpola las etiquetas y elige glyph y rol a partir de `Category` y del estado. Las etiquetas son **claves de localización** con argumentos, no frases (ADR-0040).
- **Tools sin `ToolPresentation`:** se muestran con una etiqueta genérica (`● tool.id`).

### 3. Composer

- **Qué acepta:** texto normal, `/commands` (ADR-0024) y `@references`.
- **Autocompletado:** popovers al escribir `/` (catálogo de `ListCommands` + commands del cliente) y `@`: `@file`, `@folder`, `@task`, `@lane`, `@artifact` y `@skill`. Consultan `QueryAsync(Complete { kind, prefix })` al Host.
- **Referencias estructurales:** el texto **no** se envía literal. `ComposerParser` produce partes tipadas:

```csharp
public sealed record SendInput(IReadOnlyList<InputPart> Parts, InputOrigin Origin);   // WireCommand; congelado en M1

public abstract record InputPart;
public sealed record TextPart(string Text) : InputPart;
public sealed record ReferencePart(ReferenceKind Kind, string Target, string DisplayText) : InputPart;
// ReferenceKind: File | Folder | Task | Lane | Artifact | Skill
```

- **Resolución** (revisión integral: vía la tool interna `reference.resolve`, que sigue el pipeline completo y queda en el Effect Journal; ADR-0037 §3):
  1. valida que la referencia exista;
  2. la somete al **Permission Engine**, porque leer `@file` es una lectura como cualquier otra (ADR-0014);
  3. la materializa como `ContextItem`s con procedencia (`Category = File/Task/…`, ADR-0029) y pasa a la conversación del modelo una marca estable en lugar del texto `@…`.

  Una referencia no resoluble se devuelve al cliente como error del input; nunca se envía como texto ambiguo.
- **Texto que empieza por `/`** solo se trata como command si el parser lo reconoce. En otro caso el cliente pregunta si enviarlo como texto; el Engine nunca lo interpreta (ADR-0024).

## Clasificación

| Elemento | Categoría |
|---|---|
| `SendInput { Parts }` con `TextPart`/`ReferencePart` (forma del protocolo), `ConversationBlock`s básicos y `ActivityProjector` para el plain renderer | **Necesario desde M1** (M1 solo usa `TextPart`) |
| `ToolPresentation` (M2, con las primeras tools reales), `ReferenceResolver` para `@file`/`@folder` (M2) y `@task`/`@lane`/`@artifact`/`@skill` (M6/M8), autocompletado con popovers (track TUI, después de M3; el plain renderer acepta `/` y `@` sin popovers desde M2) | **Contract now / implementation later** |
| Render de markdown avanzado e imágenes | **Deferable** |
