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

### Estado de implementación (A9, 2026-10-09)

`ClientActionDescriptor`, `ClientActionInvocation`, `IClientActionHandler` y su implementación por defecto (`ClientActionHandler`) existen en `OmniCore.Client`, con las acciones `help.show`, `app.exit`, `view.plan`, `view.tasks`, `view.events`, `context.inspect`, `tools.inspect`, `permissions.list`, `run.interrupt` y `run.cancel`. Las dos últimas emiten el `WireCommand` correspondiente, la misma ruta que cualquier otra superficie. Los bindings por defecto de `run.interrupt` y `run.cancel` **no** se han cableado: hoy la interrupción es la del CLI/TUI (cancelar el Turn en curso) y `KeyBindingService`, `keybindings.yaml` y la palette siguen en M10 como se clasificó.

## Clasificación

### Corte M10-A — motor construido, captura TUI pendiente (2026-10-26)

`OmniCore.Client/Input/` incorpora KeyChord, condiciones booleanas acotadas,
KeyBindingService, estado de secuencias y dispatcher hacia IClientActionHandler.
Es independiente de Terminal.Gui y compila para net8/net10. El Host lee DTOs YAML
tipados mediante generador estático; un adaptador CLI consume sólo contratos Host
y Client. El schema y ejemplo User están incluidos. No se añade una dependencia
CLI → Abstractions ni se cambia el grafo.

La resolución usa Priority y después User > Extension > Default; un empate entre
targets distintos es Conflict y no se despacha. Las secuencias completas resuelven
inmediatamente; un prefijo de una secuencia más larga no retrasa interrupt/cancel,
y la secuencia sombreada recibe diagnóstico. Timeout, contexto diferente o una
secuencia fallida descartan el prefijo; la última tecla se reconsidera sola para
no perder una interrupción.

El registro comprueba cobertura de interrupt/cancel en los contextos publicados.
Una eliminación, condición imposible o colisión que los deje inaccesibles restaura
los defaults y lo diagnostica. Se admiten reasignaciones válidas, incluidas las
superficies command que resuelven a la misma acción. La fuente de extensión queda
limitada al ownership de su registro; aliases de commands Core no permiten eludirlo.

Este corte **no intercepta todavía el teclado de la TUI**, no activa una paleta
global ni `/keybindings`. El adaptador de captura, la presentación localizada de
diagnósticos y la integración de esos consumidores quedan para M10-B, coordinados
con los cambios de TuiApp en M9. El ejemplo `docs/examples/keybindings.yaml` no
activaba atajos nuevos por sí solo en M10-A. Ver el acta M10-A y la integración siguiente.

### Corte M10-B — captura TUI y configuración User conectadas (2026-10-26)

`TerminalKeyChord` traduce únicamente en CLI los eventos de Terminal.Gui; el motor
portable conserva su independencia. La TUI carga `keybindings.yaml` User al abrir,
publica los contextos reales de foco, Run, interacción, overlay y vistas, y despacha
mediante `KeyBindingDispatcher` y las acciones ya utilizadas por los comandos.
Los atajos conservan el borrador y las secuencias caducan tras un segundo; cambiar
foco, Session o Run descarta el prefijo. F2/F3/F4 pasan por el motor. Los demás
gestos de edición/navegación existentes siguen siendo locales al renderer.

`/keybindings` inspecciona teclas, destinos, fuente, condición y prioridad, además
de diagnósticos con archivo/línea/columna en es/en/fr/it. Su botón Recargar y
`/keybindings reload` vuelven a leer User sin escribir el archivo. Los conflictos
abren esta inspección y no ejecutan ninguna candidata. Los errores se anuncian
también en la ayuda del chat. La ayuda de F2/F3/F4 refleja sus reasignaciones.

La primera interrupción de un Turn local cancela su generación mediante el token
que ya posee la TUI. La segunda o `run.cancel` explícito alcanzan además la acción
canónica; la cancelación no retorna prematuramente por existir un Turn local.
Sin Turn local, ambas superficies utilizan los comandos tipados existentes. Una
interacción pendiente conserva su opción predeterminada/denegar con Esc; los
atajos de navegación no la sustituyen. Esc y Meta no se ofrecen como reemplazos
capturables: se diagnostican antes de comprobar/restaurar cobertura protegida.

Los DTOs de keybindings comparten `OmniYamlStaticContext` con el resto del Host:
el generador estático fijado no generaba el contexto anterior al añadir uno
independiente. La prueba de arranque completo detectó esa regresión de M10-A.
Las posiciones usan la numeración de YamlDotNet sin sumar otra unidad.

La paleta global y el descubrimiento de contribuciones de extensiones en la TUI
continúan pendientes. No se declara M10 cerrado. Evidencia y coordinación con M9:
[acta M10-B](../validation/m10-keybindings-tui-20261026.md).

| Elemento | Categoría |
|---|---|
| `ClientAction` y `IClientActionHandler`; bindings por defecto de `run.interrupt` y `run.cancel`; los `ClientCommand`s de M1 resolviendo a acciones | **Necesario desde M1** (mínimo; lo usa la interrupción del CLI) |
| `KeyBindingService` configurable, contextos, `keybindings.yaml`, `/keybindings` | **Implementado en TUI, cortes M10-A/B** |
| Command Palette global | **Implementada en TUI, corte M10-C** |
| Combinaciones concretas de teclas más allá de interrupt y cancel | **Fully deferable** |

### Corte M10-C — paleta global y keybindings (2026-10-26)

La paleta TUI se abre desde el chat con Ctrl+P, acción canónica `palette.open`,
reasignable en User; `/palette`, `/paleta` y `/tavolozza` conservan los aliases
humanos y se reservan frente a extensiones. `paletteOpen` ahora identifica esta
paleta global; el selector de modelos conserva su identidad y usa `overlayOpen`.

`CommandPalette` en Client construye una proyección pura de las acciones existentes
y el catálogo Host. La búsqueda fuzzy ignora mayúsculas/diacríticos y mantiene IDs,
argumentos y fuentes literales. Se muestran nombre, atajo efectivo, fuente y la
razón de indisponibilidad. Los atajos se obtienen resolviendo el motor en el contexto
de origen, no enumerando bindings que hayan quedado ocultos o en conflicto.

El catálogo añade `CanonicalEnabled` opcional: captura `IsEnabled` del handler
antes de aplicar precedencia del nombre corto. `Enabled` conserva su semántica
anterior. Esto permite ofrecer ambas opciones de un nombre ambiguo con su ID
calificado, sin habilitar comandos cuyo handler no cumple disponibilidad. No se
cambian revisiones, autoridad ni admisión nativa de M9.

Buscar, seleccionar filas y abrir la paleta sólo consultan; Enter despacha una
acción tipada o un `CommandInvocation` con origen Palette e ID calificado. TUI
reutiliza el handler de acciones y el mismo consumidor de resultados Host que los
comandos escritos. Se comprueban de nuevo Session/Run, disponibilidad, identidad,
fingerprint y argumentos declarados antes del despacho; el Host revalida autoridad.
Los argumentos se analizan como argv, sin shell ni interpolación. El borrador y
adjuntos no se sustituyen ni se envían al elegir una acción.

La paleta se abre sólo cuando no existe un overlay ni una interacción pendiente.
No descarta formularios o aprobaciones; Esc vuelve al composer, y en una interacción
sigue aplicando su opción predeterminada/denegar. Las contribuciones de atajos de
extensiones y la asignación directa de teclas a comandos Host siguen siendo un
corte posterior; los comandos Host ya se pueden elegir/ejecutar por la paleta.
Acta: [M10-C](../validation/m10-global-command-palette-20261026.md). No cierra M10.
