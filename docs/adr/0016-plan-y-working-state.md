# ADR-0016 — Plan/Todo como estado canónico, WorkingState y ProgressReconciler

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §5, §7, §9, §24–§28, §50, §64
- **Diagramas:** [arquitectura §14–§17](../architecture/arquitectura.md#14-run--plan-y-run--taskgraph)

> **Revisión integral (2026-09-24):**
> - las transiciones completas de `PlanItem` (con un evento por transición, mutaciones `Unblock`, `Fail` y `Cancel`, jerarquía padre/hijo y defaults de `Required`) están en **ADR-0036 §4**, que prevalece sobre §2 y §4 de este ADR;
> - la creación del plan, la fase de planificación por modo y el paso PLAN → ACT en el mismo Run con aprobación están en **ADR-0035 §3–§4**.

## Contexto

En otros agentes, el seguimiento del plan depende de que el modelo reescriba una lista textual, y tras 3–4 pasos deja de mantenerla. En OmniCore el plan es **control ejecutivo del runtime**, no una feature del CLI.

## Decisión

### 1. Plan ≠ TaskGraph

| | `Plan` | `TaskGraph` |
|---|---|---|
| Qué es | Progreso lógico: el compromiso visible para el usuario | Unidades técnicas reales de ejecución y sus dependencias |
| Granularidad | Pasos comprensibles ("Fix AuthenticationService") | Tasks con Lane, modelo y aislamiento |
| Quién lo modifica | `PlanService`, a partir de propuestas del modelo o del usuario, o del `ProgressReconciler` | Engine y scheduler |
| UX | `/plan` | `/tasks` |

Un Run contiene **un** Plan (con revisiones) y **un** TaskGraph. La relación entre ambos es N:M mediante `PlanItemLink`.

### 2. Modelo

```csharp
public sealed record Plan(PlanId Id, RunId RunId, int Revision, IReadOnlyList<PlanItem> Items);

public sealed record PlanItem(
    PlanItemId Id,
    string Description,
    PlanItemState State,
    int Order,
    PlanItemId? ParentId,
    IReadOnlyList<PlanItemId> DependsOn,       // además del orden
    IReadOnlyList<PlanItemLink> LinkedTasks,
    bool Required,                             // participa en PlanCompletionGate
    PlanItemOutcome? Outcome,                  // resumen, evidencia, razón de skip o cancel
    IReadOnlyDictionary<string, string> Metadata);

public sealed record PlanItemLink(TaskId TaskId, bool Required, LinkRole Role); // Role: Implements | Verifies | Supports

public enum PlanItemState { Pending, Ready, InProgress, Blocked, Completed, Failed, Skipped, Cancelled }
```

- `Completed`, `Failed`, `Skipped` y `Cancelled` son terminales.
- `Failed` puede volver a `Ready` solo mediante una revisión (`Revise`).

### 3. El modelo propone; `PlanService` decide

- El modelo **nunca** modifica el Plan canónico. Propone `PlanMutation`s con la tool interna `plan.propose` (pipeline ADR-0014) o dentro de su `AgentResult`.
- Tipos de mutación: `Start`, `Complete`, `Block`, `Add`, `Split`, `Reorder`, `Skip`, `Revise`.
- `PlanService`:
  1. valida la transición contra la máquina de estados;
  2. verifica la coherencia con el TaskGraph (§5);
  3. evalúa la política de impacto (§6);
  4. emite eventos canónicos;
  5. devuelve al modelo el resultado (`accepted`, o `rejected` con la razón) como resultado de la tool.
- Ejemplo de rechazo: el modelo propone `Complete` para P3, pero T193 (vinculada y requerida) está `Running`. Se rechaza con "T193 aún en ejecución".
- **Toda mutación registra su `Cause`:** `Model`, `User`, `Reconciler`, `Policy` o `Recovery`.

### 4. Eventos

`PlanCreated`, `PlanRevised`, `PlanItemAdded`, `PlanItemUpdated`, `PlanItemStarted`, `PlanItemBlocked`, `PlanItemUnblocked`, `PlanItemCompleted`, `PlanItemFailed`, `PlanItemSkipped`, `PlanItemCancelled`, `PlanItemReordered`, `PlanItemLinked`, `PlanItemUnlinked`, `ProgressStalled`, `PlanMutationRejected`.

- El estado del Plan y todas sus revisiones se reconstruyen desde el Canonical Journal (ADR-0001).
- `PlanMutationRejected` también es canónico: sirve para medir `PlanTrackingReliability`.

### 5. `ProgressReconciler`

Es un componente del Engine, **determinista y puro** sobre proyecciones: `(TaskGraph, Plan, Lanes, último Turn, outcomes) → PlanMutation[]` con `Cause = Reconciler`.

Se ejecuta tras cambios en el estado de Tasks o Lanes, al completar un Turn, tras outcomes de tools o validaciones, y al reanudar.

| Regla | Condición | Efecto |
|---|---|---|
| R1 | Alguna Task vinculada pasa a `Running` y el item está `Pending` o `Ready` | item → `InProgress` |
| R2 | Todas las Tasks vinculadas **requeridas** están `Completed` y las `Verifies` pasaron | item → `Completed` |
| R3 | Una Task requerida está `Blocked` | item → `Blocked` (razón = la de la Task) |
| R4 | La Task que bloqueaba vuelve a `Ready` o `Running` | item → `InProgress` |
| R5 | Una Task requerida está `Failed` con su `RecoveryPolicy` agotada | item → `Failed` o `Blocked` según la `FailurePolicy` del Run (spec FR-TG-004) |
| R6 | Todas las dependencias (`DependsOn` + orden) están `Completed` o `Skipped` | item `Pending` → `Ready` |
| R7 | El item actual es terminal | Se recalcula el item actual: el primer `InProgress` por orden, o si no hay, el primer `Ready` |

- Los items **sin Tasks vinculadas** solo avanzan por mutaciones explícitas (del modelo o del usuario), y el watchdog los vigila (§9).
- Las reglas nunca retroceden un item terminal.
- Ante un conflicto (el modelo propone algo que contradice R1–R7), gana el reconciler y se emite `PlanMutationRejected`.

### 6. Revisiones y política de impacto

- Toda mutación estructural (`Add`, `Split`, `Reorder`, `Revise`, `Skip` de un item requerido) crea `Plan rev.N+1` con `PlanRevised { revision, mutations, impact }`. Las revisiones previas quedan en el journal.
- `MutationImpact` lo calcula `PlanService`:

| Impact | Ejemplos | Política por defecto |
|---|---|---|
| `Minor` | agregar una prueba, dividir un paso, reordenar pendientes | Automático |
| `Moderate` | agregar varios items dentro del objetivo, saltar un item no requerido | Automático + visible en `/plan` |
| `ScopeExpansion` | el objetivo cambia de naturaleza ("fix auth bug" → "reemplazar el subsistema de auth"), se agregan recursos fuera del `TaskScope` | `Ask` (ADR-0003: sin cliente → `Deny`, se mantiene la revisión anterior) |
| `RequiredSkip` / `RequiredCancel` | saltar o cancelar un item requerido | `Ask` y razón obligatoria |

- **Cálculo del impacto:** en M1 son reglas estructurales (recursos fuera de scope, cantidad de items, cambio del objetivo del item raíz). Una clasificación más fina es DEFERABLE.

### 7. `WorkingState`

- Es una **proyección**, no un dato almacenado: se reconstruye en cada Turn desde el Journal.
- Campos:
  - `RunObjective`
  - `PlanRevision`
  - `Items` compactos
  - `CurrentPlanItem`
  - `CurrentTask`
  - `AcceptanceCriteria`
  - `Blockers`
  - `PendingWork`
  - `NextExpectedWork`
- En el Context Engine entra como `ContextItemKind.WorkingState`, con `Priority = Pinned` y `Retention = RegenerateEachTurn`. Tiene presupuesto reservado y nunca se poda, comprime ni compacta.
- **Sobrevive** a compaction, rebuild de contexto, cambio de modelo, resume y subagentes: siempre se regenera desde el estado canónico. El `TaskPacket` de un subagente incluye la parte del WorkingState que le corresponde.
- Representación compacta y estable, lo que la hace amigable con el prompt caching:

```text
Plan rev.3 — Objetivo: corregir fallos de autenticación
✓ 1 Inspect auth
✓ 2 Reproduce failure
→ 3 Fix AuthenticationService   [T193 running]
○ 4 Update tests
○ 5 Full validation
Blockers: —   Siguiente: aplicar el fix en AuthenticationService.cs
```

### 8. Control según el modelo

`HarnessPolicy.PlanControl` (ADR-0007) ajusta cuánto decide el runtime:

- **`RuntimeDriven`** (modelos pequeños o con `PlanTrackingReliability` baja): el runtime elige el siguiente item y restringe el Turn a él. El modelo solo propone `Complete`, `Block` o `Split` del item actual.
- **`Assisted`:** el modelo puede proponer cualquier mutación, y el reconciler y la política validan.
- **`ModelDriven`** (alta fiabilidad): igual que `Assisted`, con menos WorkingState expandido (solo el item actual y los adyacentes) para ahorrar contexto.

### 9. Watchdog de progreso

- **Señales de progreso:**
  - cambio de estado de una Task o Lane vinculada;
  - ToolCall con efecto `Applied`;
  - transición del Plan;
  - resultado de una validación.
- **Detección:** si un item lleva `N` Turns en `InProgress` sin ninguna señal, se emite `ProgressStalled { planItemId, turns, lastProgressAt }`. `N` sale de `HarnessPolicy.StallThresholdTurns`.
- **Respuesta según `StallPolicy`:** `Replan`, `Diagnose`, `EscalateModel`, `SplitTask` o `AskUser`, en ese orden por defecto y con límites.
- **Loops:** también se detectan loops por repetición (misma tool con los mismos argumentos, o el mismo error), portando los umbrales de `AgentExecutionGuard` de OmniCoder.

### 10. `PlanCompletionGate`

Forma parte del Completion Pipeline:

```text
AcceptanceCriteriaGate → BuildGate → TestGate → PendingTaskGate → PlanCompletionGate → ValidationGate → WorkspaceConsistencyGate
```

- Rechaza la finalización si algún item `Required` está en `Pending`, `Ready`, `InProgress` o `Blocked`.
- Acepta `Skipped` y `Cancelled` solo si llegaron por una transición válida con la política aplicada (§6).
- Un `Failed` requerido hace que el Run termine como `Failed`, o como `CompletedWithIssues` si la `FailurePolicy` es `AllowPartial`. Nunca como `Completed` limpio.
- Si rechaza, devuelve al modelo qué items faltan: "El modelo propone DONE; el runtime decide".

### 11. Creación del plan

- **Direct** sin fase de planificación: el runtime crea `Plan rev.1` con un único item, el objetivo del Run, vinculado a la Task raíz. Así toda Run tiene plan desde el inicio.
- **PLAN y ORQ:** el primer Turn o Lane de planificación propone items con `plan.propose`.
- **ACT:** igual, si la `HarnessPolicy` lo permite. Si no, se mantiene el item único.

### 12. CLI

```text
PLAN rev.3                                   TASK | PLAN | STATE     | LANE | MODEL
✓ 1. Inspect authentication flow             T191 | P1   | Completed | L11  | local-small
✓ 2. Reproduce failing test                  T192 | P2   | Completed | L12  | local-small
→ 3. Fix AuthenticationService               T193 | P3   | Running   | L13  | local-worker
○ 4. Update tests                            T194 | P4   | Ready     | -    | -
○ 5. Run full validation                     T195 | P5   | Blocked   | -    | -
Progress: 2 / 5
```

## Clasificación

| Elemento | Categoría |
|---|---|
| Tipos `Plan`, `PlanItem`, `PlanItemLink`, `PlanMutation`, eventos, `PlanService`, `ProgressReconciler` R1–R7, `PlanCompletionGate`, proyección `WorkingState`, watchdog por Turns, tests deterministas Plan ↔ Task | **Necesario desde M1** |
| Render de `WorkingState` en el contexto (M2), tool `plan.propose` (M2), `PlanControl` desde `HarnessPolicy` (M2), impacto `ScopeExpansion` con `Ask` (M2) | **Contract now / implementation later** |
| Clasificación semántica fina del impacto, sugerencias automáticas de split | **Deferable** |
