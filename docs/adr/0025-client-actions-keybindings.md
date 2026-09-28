# ADR-0025 — Client Actions, KeyBindingService y Command Palette

- **Estado:** Aceptada — rev. 2 (2026-09-24). Modelo ahora; combinaciones concretas y TUI después.
- **Ámbito:** **solo cliente**, nunca el Core. El modelo de acciones y bindings vive en `OmniCore.Client` (sin frameworks visuales, ADR-0030); la captura de teclas de Terminal.Gui y su traducción a `KeyChord` viven en `OmniCore.Cli`.
- **Relacionado:** ADR-0024 (Commands), ADR-0019 (interrupción y cancelación)
- **Diagrama:** [arquitectura §27](../architecture/arquitectura.md#27-commands-client-actions-y-keybindings)

## Decisión

### 1. Un solo sistema de acciones

```text
Keyboard Shortcut → Client Action
Command (texto)   → Client Action   (ClientCommand)
Palette           → Client Action | Command
Menú / click      → Client Action
Client Action     → IOmniClient (WireCommand / Query) | acción local de presentación
```

**Acciones iniciales** (rev. 2; las combinaciones de teclas concretas no se congelan, salvo interrupt y cancel):

| `ActionId` | Tipo | Efecto |
|---|---|---|
| `run.interrupt` | Engine | cancela la generación o acción en curso (primera interrupción, ADR-0019) |
| `run.cancel` | Engine | cancela el Run (segunda interrupción o explícita) |
| `sidebar.toggle` | Local | muestra u oculta el sidebar; en modo `Overlay` lo abre como overlay (ADR-0031 §4) |
| `palette.open` | Local | abre la Command Palette |
| `overlay.close` | Local | cierra el overlay superior; en un `InteractionRequest` equivale a su opción default (`Deny`) |
| `view.plan` / `view.tasks` / `view.context` | Local + Query | vistas lógica, técnica y de contexto |
| `model.select` | Local + Engine | selector de modelo o alias para el próximo Turn |
| `preferences.open` / `model.policies.open` | Local + Query | abre preferencias o el mantenimiento de políticas por modelo (ADR-0044) |
| `model.policy.inspect` / `model.policy.edit` / `model.policy.delete` | Local + Engine | inspecciona, reclasifica o elimina la preferencia exacta; eliminar hace que se vuelva a preguntar |
| `model.qualify` | Local + Engine | inicia una cualificación con consentimiento y presupuesto cuando corresponda (ADR-0007/0044) |
| `session.search` | Local + Query | búsqueda de sesiones |
| `agent.inspect` | Local + Query | `LaneInspector` de la Lane seleccionada (ADR-0032) |
| `diff.open` | Local + Query | `DiffPreview` del archivo seleccionado (ADR-0032) |
| `interaction.respond` | Engine | responde el `InteractionRequest` activo con `ChoiceResponse` o `QuestionnaireResponse` (ADR-0034/0045) |

- **`ClientAction`** es la unidad de comportamiento del cliente. Un `ClientCommand` es solo su superficie textual y un keybinding su superficie de teclado; no existen dos sistemas paralelos.
- **Acciones que tocan el Engine:** una acción como `run.cancel` emite el `WireCommand` correspondiente. Es la misma ruta que `/cancel`.

```csharp
public sealed record ClientActionDescriptor(
    ActionId Id,                 // "run.interrupt", "run.cancel", "palette.open", "view.plan", "view.tasks",
                                 // "model.select", "context.inspect", "session.search"
    string Title,
    ActionAvailability When,     // expresión de contexto (§2)
    ComponentSource Source);

public interface IClientActionHandler
{
    ValueTask ExecuteAsync(ActionInvocation invocation, ClientContext context, CancellationToken cancellationToken);
}
```

### 2. `KeyBindingService`

```csharp
public sealed record KeyBinding(
    IReadOnlyList<KeyChord> Sequence,        // soporta secuencias, p. ej. Ctrl+K Ctrl+P
    BindingTarget Target,                    // ActionId o CommandInvocation con argumentos predefinidos
    ContextExpression When,                  // p. ej. "focus == input && !runActive", "awaitingPermission"
    int Priority,
    BindingSource Source);                   // Default | Extension | User
```

- **Contextos** (claves booleanas que publica el cliente): `input`, `runActive`, `awaitingPermission`, `paletteOpen`, `view.plan`, `view.tasks`, `view.context`…
- **Resolución:**
  1. se toman los bindings cuya secuencia coincide y cuyo `When` es verdadero;
  2. gana la mayor `Priority`;
  3. a igual prioridad, `User > Extension > Default`.
  - Un conflicto no resuelto se reporta en `/keybindings`; nunca es silencioso.
- **Configuración:** `keybindings.yaml` en scope User (ADR-0039). Se puede deshabilitar un default con `"-run.cancel"`.
- **Acciones que no se pueden quedar sin binding:** `run.interrupt` y `run.cancel` (ADR-0019: la primera interrupción cancela la acción y la segunda el Run). Se pueden reasignar, pero no dejar sin tecla; si el usuario las deja sin binding, se restaura el default y se avisa.
- Las extensiones solo aportan bindings `Default` de su propia fuente. Nunca pueden reasignar las acciones de `Core`.

### 3. Command Palette

- **Qué lista:** las `ClientAction`s y los `Command`s disponibles en el contexto actual, filtrados por `When` y `RequiredCapabilities`.
- **Qué muestra:** búsqueda fuzzy, el binding de cada acción y la fuente o scope de los commands. Si un nombre es ambiguo, ofrece las dos opciones (ADR-0024 §4).
- **Implementación:** en la TUI futura. En M1 no existe palette; el modelo de acciones ya la soporta sin cambios.

## Clasificación

| Elemento | Categoría |
|---|---|
| `ClientAction` y `IClientActionHandler`; bindings por defecto de `run.interrupt` y `run.cancel`; los `ClientCommand`s de M1 resolviendo a acciones | **Necesario desde M1** (mínimo; lo usa la interrupción del CLI) |
| `KeyBindingService` configurable, contextos, `keybindings.yaml`, `/keybindings` | **Contract now / implementation later** (M10) |
| Command Palette y TUI | **Contract now / implementation later** (M10+) |
| Combinaciones concretas de teclas más allá de interrupt y cancel | **Fully deferable** |
