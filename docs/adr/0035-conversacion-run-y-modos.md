# ADR-0035 — Conversación ↔ Run, modos de ejecución y cancelación

- **Estado:** Aceptada (2026-09-24). Decisiones del usuario en la entrevista de revisión: **el Run abarca la conversación** y **PLAN → ACT en el mismo Run con aprobación**.
- **Resuelve:** huecos B1, B6, H1, H2 y H6 de la revisión integral (cómo se relacionan los mensajes con los Runs, semántica de PLAN, `FailurePolicy`, completion y cancelación).
- **Spec:** §6–§9, §14, §50, §60, §70
- **Relacionado:** ADR-0016 (Plan), ADR-0034 (interacciones), ADR-0036 (máquinas de estado)
- **Diagrama:** [arquitectura §35](../architecture/arquitectura.md#35-conversación-run-y-modos)

> **Ampliación 2026-10-01:** [ADR-0047](0047-modos-explicitos-y-ultracode.md) precisa los modos
> vinculantes, ejecución directa y UltraCode. Prevalece sobre §3–§5 cuando una regla exige
> planificación/descomposición/aprobación para una consulta directa.

## Decisión

### 1. Un Run abarca la conversación hasta completarse

- **Un solo Run activo por Session en v1.** Los demás comandos que intenten iniciar otro Run reciben `RunAlreadyActive`.
- **`SendInput` sin Run activo:** inicia un Run en el modo actual de la sesión (`Session.DefaultMode`, que se cambia con `/mode`). El texto del input es el objetivo inicial del Run.
- **`SendInput` con Run activo:** se agrega al Run como mensaje de usuario y lo ve el **próximo Turn de la Lane raíz**. No interrumpe el Turn en curso; para eso existe `Interrupt` (§6).
- **Una Lane esperando al usuario:** si espera conversación abierta o continuación tras el final de
  su trabajo, `SendInput` la reactiva. Si espera un cuestionario de `user.ask`, solo una
  `QuestionnaireResponse` válida y correlacionada mediante `RespondToInteraction` la reactiva;
  texto genérico del composer no resuelve el formulario (ADR-0045).
- **Fin del Run:** el Run termina cuando pasa los Completion Gates. El siguiente `SendInput` inicia un Run nuevo en la misma sesión.

**Eventos canónicos de conversación** (el contenido va en artifacts, ADR-0001):

| Evento | Payload |
|---|---|
| `UserInputReceived` | `RunId`, `InputParts` (con referencias ya resueltas, ADR-0033), `ArtifactRef` del contenido |
| `AssistantMessageRecorded` | `RunId`, `LaneId`, `TurnId`, `ArtifactRef` (bloques de texto finales del Turn) |

**Contexto entre Runs:** `SessionConversationContributor` proyecta la conversación previa de la sesión al slot `Conversation`:

- incluye mensajes de usuario y asistente de los Runs anteriores, pero **no** transcripts de subagentes ni resultados crudos de tools;
- si excede el presupuesto, usa el resumen de cada Run anterior, un `RunSummary` generado al cerrarlo.

### 2. Estructura mínima de todo Run

```csharp
public sealed record Run(
    RunId Id, SessionId Session,
    string Objective,
    RunMode Mode,                       // Plan | Act | Orchestrate
    ExecutionStrategy Strategy,         // Direct | Workflow | Delegated
    FailurePolicy FailurePolicy,        // FailRun | BlockDependents | AllowPartial   (default: BlockDependents)
    RunBudget Budget,                   // ADR-0037 §7
    TaskId RootTask);
```

- **Tarea y Lane raíz:** todo Run tiene una Task raíz con una Lane raíz. Los Turns del agente principal viven en esa Lane.
- **Subagentes:** son Tasks hijas con sus propias Lanes (INV-004).
- **`FailurePolicy` por defecto: `BlockDependents`.**
  - El trabajo independiente continúa y las Tasks dependientes quedan `Blocked`.
  - El Run termina `Failed` si un item requerido del Plan falló, salvo que el usuario elija `AllowPartial`, que da `CompletedWithIssues`.
- **Recuperación:** `Retry` y `Escalate` pertenecen a la `RecoveryPolicy` de la Task (spec §72), no a la `FailurePolicy`. Esto reemplaza la lista mezclada de FR-TG-004.

### 3. Modo → estrategia y fase de planificación

| Modo | Estrategia por defecto | Fase de planificación | Permisos |
|---|---|---|---|
| `Plan` | Direct | Si el usuario pide un plan; una explicación se responde directamente | solo lectura (ADR-0037); sin agentes hijos |
| `Act` | Direct, o Workflow en la Lane raíz | Pasos proporcionales al trabajo; sin cambio implícito de modo | ADR-0037; sin agentes hijos |
| `Orchestrate` | Direct, Workflow o Delegated según utilidad | Descomposición cuando aporta valor; no obligatoria para consultas | ADR-0037; delegación permitida, no obligatoria |

- **`Plan rev.1`** es siempre el item único con el objetivo del Run, vinculado a la Task raíz (ADR-0016 §11). La planificación lo **revisa** y nunca parte de un plan vacío, así el gate no pasa trivialmente.
- Ese item es seguimiento interno: no obliga a mostrar un plan, usar `plan.propose` o crear workers.
- El modo elegido por el usuario es vinculante. El modelo puede sugerir otro; los cambios automáticos
  solo se autorizan con el preset UltraCode explícito y sus límites (ADR-0047), nunca por complejidad
  inferida ni por un esfuerzo alto del provider.

### 4. PLAN → ACT en el mismo Run

1. Un Run en `Plan` que prepara una ejecución o requiere aprobación explícita de una revisión emite un `InteractionRequest` de tipo **`PlanApproval`** (ADR-0034), con las opciones `Aprobar y ejecutar`, `Aprobar sin ejecutar` y `Seguir planificando`. Una consulta observacional directa no solicita esta aprobación (ADR-0047).
2. **`Aprobar y ejecutar`:** el mismo Run pasa a `Act` (evento `RunModeChanged { from: Plan, to: Act, cause: PlanApproved }`) y ejecuta ese plan con los permisos de `Act`.
3. **`Aprobar sin ejecutar`:** el Run termina con el outcome **`Planned`**. Para un plan que requiere aceptación formal, `PlanCompletionGate` exige la revisión aprobada, no items ejecutados. Una respuesta observacional o un plan pedido únicamente como documento no exige aprobación para ser entregado (ADR-0047).
4. **`Seguir planificando`:** el Run continúa en `Plan`.
5. **Sin cliente interactivo:** aplica ADR-0003, así que la respuesta es `Deny` y el Run termina `Planned`.

Fuera de este flujo, `/mode` solo cambia `Session.DefaultMode` para el **próximo** Run. No altera el Run activo.
Los commands explícitos de cambio del Run activo y la autorización adaptativa de UltraCode se definen
en ADR-0047; ambos pasan por la frontera autoritativa y se aplican en puntos seguros.

### 5. Completion

- **Quién propone completar:**
  - la Lane raíz, cuando el modelo declara que terminó;
  - el scheduler, cuando todas las Tasks están en estado terminal y no queda Turn pendiente.
- **Quién decide:** los Completion Gates, siempre.
- **Pipelines por nivel** (esto aclara ADR-0012 y la spec §50):
  - **Lane / Task:** `AcceptanceCriteria`, `Build`, `Test` y `Validation` del `VerificationPlan` de la Task.
  - **Run:** `PendingTask`, `PlanCompletion` y `WorkspaceConsistency`.
- Los gates son proporcionales a los criterios y efectos reales: una explicación directa no requiere
  build/test, edición, descomposición ni aceptación humana obligatoria (ADR-0047).
- **`PendingTaskGate`** rechaza la finalización si alguna Task está `Running`, o si alguna Task vinculada a un item requerido no está en estado terminal.
- **Resumen final:** al completar, la Lane raíz produce un resumen final para el usuario, que es su último Turn. Si no hay modelo disponible, el runtime genera un `RunSummary` estructurado.

### 6. Cancelación

| Comando | Turn en curso | ToolCalls | Lane | Task | Run |
|---|---|---|---|---|---|
| `Interrupt { RunId }` (primera interrupción) | Se corta la generación; la respuesta parcial queda como `TurnInterrupted` | Las previas a `Started` pasan a `ToolCallCancelled`; las `Started` reciben cancelación graceful y terminan con `ToolCallFailed { EffectOutcome }` | **La Lane en foreground** (la raíz, salvo que el usuario haya seleccionado otra) queda esperando input | sin cambio | sigue activo y espera input |
| `CancelLane { LaneId }` | igual que arriba | igual que arriba | `Cancelled` | Se aplica su `RecoveryPolicy`; si no hay recuperación, `Failed` | Se aplica la `FailurePolicy` |
| `CancelRun { RunId }` (segunda interrupción o explícito) | igual que arriba | igual que arriba | todas `Cancelled` | todas `Cancelled` | `Cancelled` |

Los comandos llevan un `CommandId` para la causalidad (ADR-0013).

## Clasificación

| Elemento | Categoría |
|---|---|
| Todo este ADR: define el comportamiento del Run en M1 (simulado con `ScriptedModelProvider`) | **Necesario desde M1** |
| `SessionConversationContributor` con resúmenes (M2), resumen final generado por modelo (M2) | **Contract now / implementation later** |
| Varios Runs activos por Session | **Fully deferable** (fuera de v1) |
