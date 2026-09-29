# ADR-0036 — Máquinas de estado canónicas y eventos por transición

- **Estado:** Aceptada (2026-09-24)
- **Resuelve:** B2, B3, B4, B5, B7, H7, M4 y M5 de la revisión integral
- **Spec:** §7, §9, §10, §12; INV-021
- **Relacionado:** ADR-0001, ADR-0004, ADR-0016, ADR-0035
- **Diagrama:** [arquitectura §36](../architecture/arquitectura.md#36-máquinas-de-estado)

## Principio

**Cada transición de un estado canónico tiene exactamente un evento.** Los estados que no son canónicos se **derivan** de otros eventos; nunca se persisten por separado y nunca dependen de heartbeats, que se pueden coalescer.

## 1. Run

| Desde | Hacia | Evento |
|---|---|---|
| — | `Created` | `RunCreated` |
| `Created` | `Running` | `RunStarted` |
| `Running` | `AwaitingInput` | `RunAwaitingInput` (la Lane raíz espera al usuario) |
| `AwaitingInput` | `Running` | `UserInputReceived` (ADR-0035) |
| `Running` | `Validating` | `RunValidationStarted` |
| `Validating` | `Running` | `RunValidationRejected { gates, missing }` |
| `Validating` | `Completed` | `RunCompleted { outcome: Completed \| CompletedWithIssues \| Planned }` |
| `Running`, `Validating`, `AwaitingInput` | `Failed` | `RunFailed { cause }` |
| cualquier estado no terminal | `Cancelled` | `RunCancelled` |

`Preparing` desaparece; la preparación ocurre dentro de `Running`. El cambio de modo se registra como `RunModeChanged` y no es un cambio de estado.

`UserInputReceived` con el Run en `Running` no es una transición: es un mensaje que se añade a la conversación y lo ve el próximo Turn de la Lane raíz (ADR-0035 §1). `RunCompleted { outcome: CompletedWithIssues }` lleva al estado `CompletedWithIssues`.

**Validación (M1, 2026-09-29):** `EventStream` valida cada evento contra estas tablas antes de persistirlo (`CanonicalStateTracker`); una transición inválida, o un evento sobre una entidad que no existe, lanza `InvalidStateTransitionException` y no se escribe. Las proyecciones calculan el estado con las mismas máquinas y fallan igual ante un journal inválido.

## 2. Task

| Desde | Hacia | Evento |
|---|---|---|
| — | `Pending` | `TaskCreated` |
| `Pending` | `Ready` | `TaskReady` (dependencias requeridas `Completed`) |
| `Ready` | `Running` | `TaskStarted` (su primera Lane arranca) |
| `Running` | `Blocked` | `TaskBlocked { reason }` |
| `Blocked` | `Running` | `TaskUnblocked` |
| `Blocked` | `Ready` | `TaskUnblocked { requeue: true }` (sin Lane activa) |
| `Running`, `Blocked` | `Completed` | `TaskCompleted` |
| `Running`, `Blocked` | `Failed` | `TaskFailed` (**solo** al agotar la `RecoveryPolicy`) |
| `Pending`, `Ready`, `Blocked` | `Skipped` | `TaskSkipped { reason }` |
| cualquier estado no terminal | `Cancelled` | `TaskCancelled` |

- **Reintentos:** una Task permanece `Running` mientras su `RecoveryPolicy` reintenta con Lanes nuevas.
- **Dependencias:** una dependencia es `{ TaskId, Required }`, con `Required = true` por defecto.

## 3. Lane: ciclo de vida canónico + actividad derivada

**Canónico:**

| Desde | Hacia | Evento |
|---|---|---|
| — | `Queued` | `LaneCreated` |
| `Queued` | `Provisioning` | `LaneProvisioning` (worktree, proceso externo) |
| `Queued`, `Provisioning` | `Running` | `LaneStarted` |
| `Running` | `Blocked` | `LaneBlocked { reason }` |
| `Blocked` | `Running` | `LaneUnblocked` |
| `Running` | `Completed` | `LaneCompleted { agentResult }` |
| `Running`, `Blocked`, `Provisioning` | `Failed` | `LaneFailed { cause }` |
| cualquier estado no terminal | `Cancelled` | `LaneCancelled` |

**Actividad derivada** (proyección y UI, sin eventos propios):

| Actividad | Se deriva de |
|---|---|
| `WaitingForModel` | `TurnStarted` sin `ModelCompleted` |
| `WaitingForTool` | ToolCall en `Started` |
| `WaitingForPermission` | `InteractionRequested` de tipo `Permission` sin resolver (ADR-0034) |
| `WaitingForInput` | `RunAwaitingInput` con esta Lane en foreground |
| `WaitingForSubtask` | Task hija en `Running` |
| `Validating` | gates de la Task en curso |
| `Stalled` | `ProgressStalled` sin resolver (ADR-0016 §9); no es terminal |

Los heartbeats **no** se persisten salvo el último de cada Lane al cerrarla. Son telemetría.

**Lanes externas** (ADR-0012):

- el efecto de la Lane se registra como **efecto a nivel de Lane**: `LaneEffectStarted` y `LaneEffectReconciled`, con la misma semántica que ADR-0004 §4;
- no hay Turns de OmniCore;
- `Provisioning` cubre la creación del worktree y el arranque del proceso.

## 4. PlanItem

| Desde | Hacia | Evento | Causa típica |
|---|---|---|---|
| — | `Pending` | `PlanItemAdded` | modelo, usuario, runtime |
| `Pending` | `Ready` | `PlanItemReady` | reconciler (R6) |
| `Pending`, `Ready` | `InProgress` | `PlanItemStarted` | reconciler (R1) o mutación `Start` |
| `InProgress` | `Blocked` | `PlanItemBlocked` | R3 o `Block` |
| `Blocked` | `InProgress` | `PlanItemUnblocked` | R4 o `Unblock` |
| `InProgress` | `Completed` | `PlanItemCompleted` | R2 o `Complete` validado |
| `InProgress`, `Blocked` | `Failed` | `PlanItemFailed` | R5 o `Fail` |
| `Pending`, `Ready`, `Blocked` | `Skipped` | `PlanItemSkipped` | `Skip` (requerido → `Ask`) |
| cualquier estado no terminal | `Cancelled` | `PlanItemCancelled` | `Cancel` (requerido → `Ask`) |
| `Failed` | `Ready` | `PlanItemReopened` | `Revise` |

- **Mutaciones completas:** `Start`, `Complete`, `Block`, `Unblock`, `Fail`, `Cancel`, `Add`, `Split`, `Reorder`, `Skip` y `Revise`. **El modelo puede proponer `Cancel` o `Skip` de un item requerido**, pero siempre pasa por `Ask` (impactos `RequiredSkip` y `RequiredCancel`, ADR-0016 §6).
- **Otros eventos:** `PlanItemUpdated` solo registra cambios de texto o metadata, sin cambio de estado. `PlanItemReordered`, `PlanItemLinked` y `PlanItemUnlinked` quedan como en ADR-0016.
- **Jerarquía:** un item con hijos es un **contenedor derivado**. Su estado se calcula de sus hijos: `Completed` si todos los hijos requeridos son terminales aceptables, `Failed` si falló uno requerido, `InProgress` si hay alguno en curso. **Solo las hojas** cuentan para `PlanCompletionGate` y para las reglas R1–R7.
- **Defaults:**
  - `PlanItem.Required = true`, y bajarlo es un impacto `Moderate`;
  - `PlanItemLink.Required = true`, salvo `Role = Supports`, que es `false` por defecto.

## 5. ToolCall

Complementa ADR-0004 §2:

- **Cancelación antes de ejecutar:** `ToolCallCancelled` es válido desde `Requested`, `Prepared`, `AwaitingPermission` y `Authorized` (por interrupt, `TurnAbandoned`, `CancelLane`/`CancelRun`, o una revalidación fallida tras un resume).
- **Cancelación durante la ejecución:** una ToolCall `Started` cancelada termina en `ToolCallFailed { EffectOutcome }`, nunca en `Cancelled`.
- **Permisos:**
  - **`PermissionEvaluated { decision, layers }`** se emite para **toda** ToolCall. Es la traza de la decisión y la base del `PermissionDecisionRecord` (ADR-0037).
  - `PermissionRequested` se emite **solo** cuando la decisión es `Ask`, y abre un `InteractionRequest`.
  - `PermissionGranted` y `PermissionDenied` cierran el `Ask`.

## 6. Turn

`TurnStarted` → `ModelCompleted` → (`TurnCompleted` | `TurnInterrupted` | `TurnAbandoned`).

- **Reparaciones:** los reintentos de reparación de tool calls (ADR-0007) ocurren dentro del mismo Turn y no abren uno nuevo.

## 7. Watchdog: señales de progreso y conteo

Esto precisa ADR-0016 §9:

- **Señales de progreso:**
  - transición de Task, Lane o PlanItem;
  - ToolCall con efecto `Applied`;
  - `ToolCallSucceeded` sobre un recurso **no visto antes en la Lane** (la exploración cuenta como progreso);
  - resultado de validación.
- **Qué Turns se cuentan:** solo los de las Lanes vinculadas al item; si el item no tiene vínculos, los de la Lane raíz.
- **Umbral por defecto:** 6 Turns sin señal. En M1 es una constante; desde M2 sale de `HarnessPolicy.StallThresholdTurns`.

## Clasificación

| Elemento | Categoría |
|---|---|
| Todas las tablas de este ADR | **Necesario desde M1**: son el núcleo testeable de M1 |
