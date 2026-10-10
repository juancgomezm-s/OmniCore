# ADR-0024 — Commands como subsistema formal

- **Estado:** Aceptada (2026-09-24)
- **Relacionado:** ADR-0019 (CLI → IOmniClient), ADR-0022 (scopes), ADR-0023 (extensiones y confianza), ADR-0025 (Client Actions)
- **Spec:** §60, §64, INV-016
- **Diagrama:** [arquitectura §27](../architecture/arquitectura.md#27-commands-client-actions-y-keybindings)

## Decisión

### 1. El Engine nunca interpreta `/`

- El cliente transforma el texto en operaciones estructuradas (`CommandInvocation`).
- El Engine recibe `WireCommand`s tipados o `SendInput { InputPart[] }` (ADR-0033). Un `TextPart` que empieza por `/` es **texto literal** para el modelo; el Engine nunca lo parsea.
- *Test:* un `SendInput("/cancel")` no cancela nada.

### 2. Tipos de command

| Kind | Quién lo ejecuta | Resultado | Ejemplos |
|---|---|---|---|
| `ClientCommand` | el cliente | una `ClientAction` (ADR-0025); puede hacer `QueryAsync` de read models | `/plan`, `/tasks`, `/context`, `/keybindings`, `/help`, `/exit` |
| `ServerCommand` (antes llamado `EngineCommand`) | el Engine, vía `WireCommand` | `CreateSession`, `StartRun`, `CancelRun`, `RespondToInteraction` (ADR-0034), `ResumeRun`, `Compact`, `SetDefaultMode`… | `/cancel`, `/resume`, `/compact`, `/mode act` (cambia el modo del **próximo** Run, ADR-0035 §4) |
| `PromptCommand` | el `CommandService` del Host: expande una plantilla con argumentos | `SendInput` o `StartRun` con `Origin = PromptCommand(id, version)` | `/review <file>` definido en `.omnicore/commands/review.md` |
| `WorkflowCommand` | el `CommandService` del Host | `StartRun` con `WorkflowRef` (TaskGraph o plantilla de plan) + argumentos | `/fix-tests`, `/orq-auth` |
| `ExtensionCommand` | el handler de una extensión (fuera de proceso, ADR-0023) | cualquiera de los anteriores, **devuelto como dato** para que lo ejecute el cliente o el Host | `/prowin-compile` |

**Un handler de extensión nunca ejecuta directamente un `ServerCommand`.** Devuelve una intención (por ejemplo, "iniciar Run con X"), y el Host la somete a las mismas validaciones y permisos que cualquier command.

### 3. Contratos

```csharp
public sealed record CommandDescriptor(
    CommandId Id,                           // canónico y calificado: "core:plan", "project:review", "ext.acme.progress:prowin-compile"
    string Name,                            // nombre corto: "plan"
    IReadOnlyList<string> Aliases,
    CommandKind Kind,
    string Description,
    IReadOnlyList<CommandArgument> Arguments,
    IReadOnlyList<CapabilityRequirement> RequiredCapabilities,   // p. ej. requiere Run activo, requiere Engine
    CommandAvailability Availability,       // modos, estados de Run y contexto de UI en que aplica
    ComponentSource Source);                // Kind, Scope, Trust, OwnerId (ADR-0023)

public sealed record CommandArgument(
    string Name, ArgumentType Type, bool Required, string? Description,
    IReadOnlyList<string>? Choices, bool Variadic, IArgumentCompleter? Completer);

public interface ICommandHandler
{
    ValueTask<CommandOutcome> HandleAsync(CommandInvocation invocation, CommandContext context, CancellationToken cancellationToken);
}

public sealed record CommandInvocation(CommandId Id, ParsedArguments Arguments, InvocationOrigin Origin); // Typed | Palette | KeyBinding | Api

public abstract record CommandOutcome;   // ClientActionRequested | ServerCommandRequested | PromptExpanded | WorkflowRequested | CommandFailed
```

**Dos registries, un catálogo:**

- `ClientCommandRegistry`, en el cliente: commands de UI.
- `CommandRegistry`, en el Host: `ServerCommand`, `PromptCommand`, `WorkflowCommand` y `ExtensionCommand`.
- El cliente obtiene el catálogo del Host con `QueryAsync(ListCommands)` y lo combina con el suyo. Así OmniCoder y el CLI ven los mismos commands de servidor.

### 4. Precedencia y conflictos

1. **Nombres reservados de `Core`** (`plan`, `tasks`, `mode`, `cancel`, `interrupt`, `resume`, `permissions`, `context`, `help`, `exit`, `commands`…): nadie puede registrarlos. Un intento de hacerlo se rechaza con diagnóstico (`CommandRegistrationRejected`) y aparece en `/commands`.
2. **Id canónico:** siempre es único por construcción (`<source>:<name>`) y siempre invocable, por ejemplo `/project:review`.
3. **Nombre corto** (entre fuentes no reservadas):
   1. Un **alias explícito del usuario** (config de scope User) gana.
   2. Si no hay alias, gana la **especificidad del scope** (`Session > Workspace > Project > User > BuiltIn`), **con una restricción de confianza**: una fuente de menor `TrustLevel` no puede ocultar a una de mayor. En ese caso, el nombre corto queda **ambiguo**.
   3. **Mismo scope y misma confianza:** el nombre también es ambiguo.
   - Un nombre corto ambiguo no se resuelve en silencio: la invocación pide desambiguar (palette o lista), y `/commands` muestra el conflicto.
4. **Aliases:** siguen las mismas reglas, y un alias nunca oculta un nombre canónico ni un nombre corto no ambiguo.
5. **Capacidades:** un command cuya `RequiredCapabilities` no se cumple (por ejemplo, un `ServerCommand` sin Run activo) aparece deshabilitado, con la razón visible.

### Estado de implementación (A9, 2026-10-09)

`ClientCommandRegistry` existe en `OmniCore.Client` con los ClientCommands de M1: `/help`, `/exit`, `/plan`, `/tasks`, `/events`, `/context`, `/tools`, `/permissions`, `/interrupt` y `/cancel`. Cada uno resuelve a una `ClientActionInvocation` (ADR-0025) que ejecuta `ClientActionHandler`; la TUI y el CLI plano usan el mismo registro y el mismo handler, y solo aportan lo suyo (la TUI, cerrar y detener su Turn; el CLI, el listado de permisos). Antes `/help`, `/exit`, `/tasks`, `/events` y `/permissions` caían en el Host y terminaban en `command.prompt_not_found`. Todos los nombres del registro están reservados en el Host (un test lo verifica), de modo que ninguna otra fuente puede registrarlos. Los commands propios de la TUI (`/mode`, `/agents`, `/agent`, `/models`…) siguen resueltos en `TuiApp`: moverlos al registro es trabajo pendiente. `CommandDescriptor` completo, `ListCommands` con precedencia entre fuentes y la palette siguen como se declaró arriba.

## Clasificación

| Elemento | Categoría |
|---|---|
| Regla "el Engine nunca interpreta `/`", `CommandInvocation`, `ClientCommandRegistry` mínimo con los commands de M1 (`/plan`, `/tasks`, `/events`, `/exit`), mapeo a `WireCommand` | **Necesario desde M1** |
| `CommandDescriptor` completo, `CommandRegistry` del Host, `ListCommands`, precedencia; `PromptCommand` (M2), `WorkflowCommand` (M6), `ExtensionCommand` (M8) | **Contract now / implementation later** |
| Completers avanzados de argumentos | **Deferable** |
