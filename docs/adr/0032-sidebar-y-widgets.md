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
  1. `core.session` conserva su slot fijo arriba;
  2. entre los demás, los que están en `Attention` suben temporalmente;
  3. el resto se ordena por `priority` configurada o `DefaultPriority`; los empates se resuelven por orden de registro.

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

La presentación tiene ubicación explícita en el [track Panel lateral funcional](../architecture/arquitectura.md#241-track-tui--panel-lateral-funcional):

| Presentación | Ubicación y dependencia |
|---|---|
| Contexto, consumo y diagnóstico básico de recuperación | TUI-P1, sobre las proyecciones ya construidas en M4/M5.5; no reabre esos hitos |
| Archivos atribuibles a la sesión y DiffPreview | TUI-P2; aislamiento y agrupación por worktree/Lane dependen de M7 |
| Configuración por widget, pestañas y adaptación a altura | TUI-P3, ADR-0031/0039 |
| AgentsWidget e inspector operativo | M6; no basta el contrato pre-M6 para representar un scheduler funcionando |
| MemoryWidget de sesión | M8, junto al servicio Session Memory de ADR-0028 |
| DiagnosticsWidget de commands/skills/extensions y widgets de extensiones | M8, ADR-0023/0029 |
| Memoria Project/Workspace/Global y diagnóstico de keybindings | M10, ADR-0028/0029 |

TUI-P1 usa `Query("sessionSidebar")` para el Plan canónico del Run activo y `sessionObservability` para mediciones separadas. La consulta del Plan selecciona los eventos por `SessionId`/`RunId` del envelope, no por su posición entre dos `run.created`. Al cambiar de sesión se invalidan ambos snapshots; al reabrir se reconstruyen del journal. El título usa el objetivo del primer Run como fallback estable mientras no exista un título canónico. Los contenedores se derivan con `PlanProjection`; el denominador de progreso cuenta hojas, no también sus contenedores. Un replay inválido aparece como **no disponible**, no como un Plan vacío saludable.

El panel muestra capacidad declarada, presupuesto efectivo y ocupación de la última solicitud separados del consumo acumulado. `≈` identifica estimaciones y `—` datos no disponibles; cache/reasoning se muestran como desglose y no vuelven a sumarse. El detalle se despliega en una superficie desplazable sin llamadas a proveedores. Hasta TUI-P2 no se afirma «sin archivos modificados» sin una proyección que lo respalde.

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
