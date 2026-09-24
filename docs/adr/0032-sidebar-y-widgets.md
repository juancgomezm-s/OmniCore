# ADR-0032 — SidebarHost y contrato de widgets

- **Estado:** Aceptada (2026-09-24)
- **Relacionado:** ADR-0030 (cliente), ADR-0031 (responsive, roles), ADR-0016 (Plan), ADR-0023 (extensiones)
- **Diagrama:** [arquitectura §33](../architecture/arquitectura.md#33-sidebar-y-widgets)

## Decisión

### 1. Un host de widgets, no un panel de plan

`SidebarHost` es un contenedor de **widgets registrables** e independientes. Nada en el host sabe qué es un Plan o una Lane.

```csharp
// OmniCore.Client: sin tipos de framework visual
public interface ISidebarWidget
{
    WidgetId Id { get; }                         // "core.session", "core.plan", "core.agents", "core.files"
    string Title { get; }
    ThemeRole Accent { get; }
    int DefaultPriority { get; }                 // mayor = más arriba
    WidgetRelevance Evaluate(ClientState state); // None | Low | Normal | Attention
    WidgetModel Build(ClientState state, WidgetSize size);   // Compact | Normal | Expanded
}

public abstract record WidgetModel;              // modelos declarativos: ListModel, TreeModel, KeyValueModel,
                                                 // ProgressModel, StatusBadgeModel, con ThemeRole por fila
```

- **Renderers:** `OmniCore.Cli` tiene un renderer por tipo de `WidgetModel`, no por widget. Así un widget nuevo, incluido el de una extensión, no necesita código de UI propio.
- **Configuración** (scope User en `settings.yaml`, con override en el `settings.yaml` local del Workspace; ADR-0039 §2):

```text
sidebar.visible = true | false
sidebar.mode    = auto | stacked | tabbed | overlay
widgets.<id>.visible  = true | false | auto     // auto: visible si Evaluate ≥ Low
widgets.<id>.expanded = true | false
widgets.<id>.priority = <int>
```

- **Orden de los widgets:**
  1. los que están en `Attention` suben temporalmente arriba;
  2. el resto se ordena por `priority` configurada o `DefaultPriority`;
  3. los empates se resuelven por orden de registro.

### 2. Widgets iniciales

| Widget | Relevancia `auto` | Contenido |
|---|---|---|
| **SessionHeaderWidget** (`core.session`) | siempre visible; fijo arriba | `SESSION` + título; opcionalmente el tiempo transcurrido y el `RunMode`. El título puede generarlo el MetaModelService, pero su representación es del cliente |
| **PlanWidget** (`core.plan`) | hay Plan con más de un item, o algún item `Blocked`/`Failed` (`Attention`) | El **Plan lógico**, no el TaskGraph. `PLAN 2/5 · rev.3`; filas con glyph + rol (✓ Success, → Active, ○ Muted, ◐/! Attention para Blocked, ✗ Error para Failed) |
| **AgentsWidget** (`core.agents`) | existen Lanes de subagentes (profundidad > 0), o alguna Lane está en `WaitingForPermission` (`Attention`) | `SUBCODERS 3`; por Lane: glyph de estado, perfil, modelo, turn y operación actual (`patching AuthService.cs`) o espera (`waiting for T193`, `WAITING FOR PERMISSION`) |
| **ChangedFilesWidget** (`core.files`) | hay archivos cambiados en la sesión | `FILES 3`; `M AuthService.cs +18 -7`, `A TokenPolicy.cs +41`; los cambios pendientes en worktrees de Lanes aparecen agrupados por Lane (ADR-0021) |

- **Interacción:**
  - seleccionar una Lane ejecuta `agent.inspect`, que abre el `LaneInspector` (overlay; ADR-0034) con Task, Lane, modelo, estado, operación actual, uso de contexto, tokens, tiempo y artifacts;
  - seleccionar un archivo ejecuta `diff.open`, que abre `DiffPreview`.
- **Transcript:** el transcript de una Lane **nunca** se vuelca automáticamente en la conversación principal (spec §75). Solo se ve desde el inspector.
- **Datos:** vienen de read models del Host (`SessionSummary`, `PlanView`, `LaneSummary`, `ChangedFiles`), actualizados por eventos.

### 3. Widgets futuros

`ContextWidget` (desglose de ADR-0029), `MemoryWidget` (ADR-0028), `DiagnosticsWidget` y **widgets de extensiones**:

- el manifest los declara en `provides.sidebarWidgets` (ADR-0023);
- solo pueden devolver `WidgetModel`s declarativos, no dibujar;
- solo ven datos **redactados** según su `TrustLevel`;
- no pueden ocupar el slot fijo de `core.session`.

## Clasificación

| Elemento | Categoría |
|---|---|
| `ISidebarWidget`, `WidgetModel` (tipos declarativos), `WidgetRelevance` y configuración | **Contract now** (M1 no tiene TUI; el `PlainRenderer` reutiliza `PlanWidget`/`AgentsWidget` como modelos para `/plan` y `/tasks`) |
| Session, Plan, Files y diff (track TUI, después de M3), Agents e inspector (M6), widgets de extensión (M8) | **Contract now / implementation later** |
| Widgets con dibujo libre | **Fuera de alcance** (no se permitirán) |
