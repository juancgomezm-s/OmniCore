# OmniCore v1

## Especificación Funcional y Técnica

**Versión:** 0.6 (2026-09-27)
**Estado:** Draft técnico en revisión, previo a M1
**Runtime objetivo:** .NET 10 LTS / C#
**Producto consumidor inicial:** Omni CLI
**Producto consumidor futuro:** OmniCoder
**Dependencias prohibidas:** Pi, little-coder, UI de OmniCoder

> Documento original de análisis. Las decisiones que lo precisan o modifican están en `docs/adr/`.
> Cuando un ADR contradiga esta especificación, prevalece el ADR.

## Cambios en v0.2

Revisión arquitectónica previa a M1. Diagramas, tabla completa de cambios, preguntas abiertas y roadmap en [`docs/architecture/arquitectura.md`](../architecture/arquitectura.md).

| Tema | Secciones afectadas | ADR |
|---|---|---|
| Canonical Journal = Event Store + artifacts inmutables content-addressed | §4, §40, §55–§59 | 0001, 0002 |
| Versión de schema de evento separada del protocolo | §55, §61–§62 | 0013 |
| Effect Journal y reconciliación de ToolCalls | §12, §71–§72 | 0004 |
| Pipeline `Prepare → ToolIntent → Permission → AuthorizedToolIntent → Execute` | §33 | 0014 |
| `process.exec` como primitive; shell como superficie de riesgo | §38–§39 | 0015 |
| Model Runtime provider-native con content blocks y estado opaco | §17–§19 | 0005, 0011 |
| Model Qualification Framework en lugar de clases por tamaño | §20–§21 | 0007 |
| Plan/Todo canónico, WorkingState, ProgressReconciler, PlanCompletionGate | §5, §7, §24, §50, §64 | 0016 |
| ExecutionFingerprint | §12, §29 | 0017 |
| Secretos y redacción | §4, §76 | 0018 |
| CLI → IOmniClient/IOmniTransport → Host | §61–§62, §81 | 0019 |
| Hooks: confianza y capacidades | §49 | 0020 |
| Worktrees con workspace sucio | §48 | 0021 |
| Roadmap: Planning y persistencia en M1 | §86–§95, §99 | — |

## Cambios en v0.3

| Tema | Secciones afectadas | ADR |
|---|---|---|
| Scopes, `ProjectId` ≠ `WorkspaceId`, estrategias de resolución por subsistema | §51, §59 | 0022 |
| Extensiones: manifest, Extension API fuera de proceso, `TrustLevel` unificado, `ExtensionBoundary` | §31, §44, §49 | 0023, 0020 |
| Commands como subsistema; el Engine no interpreta `/` | §60, §64 | 0024 |
| Client Actions, keybindings, Command Palette (solo cliente) | §64 | 0025 |
| Skills: scopes, ciclo de vida, precedencia | §52–§53 | 0026 |
| Origen de tools y precedencia; built-ins protegidas | §31–§32 | 0027 |
| Memory como servicio externo; Memory ≠ Knowledge ≠ Skills; promoción | §24, §26, §54 | 0028 |
| Procedencia de `ContextItem` y diagnósticos | §24, §29, §66 | 0029 |

## Cambios en v0.4

| Tema | Secciones afectadas | ADR |
|---|---|---|
| Cliente: `OmniCore.Client` + `ClientProjection`; renderers TUI (Terminal.Gui v2), plain (Spectre.Console) y JSON | §4 (INV-024), §64.1, §78 | 0030 |
| Layout de 4 zonas, header, status line con `UsageSnapshot`, responsive, roles de tema | §64.1 | 0031 |
| SidebarHost y widgets | §64.1 | 0032 |
| Conversación semántica, `ToolPresentation`, `SendInput { InputPart[] }` con `@references` | §60, §64.1 | 0033 |
| `InteractionRequest` para todo human-in-the-loop | §42, §60, §64.1 | 0034 |

## Cambios en v0.5 (revisión integral + entrevista)

| Tema | Secciones afectadas | ADR |
|---|---|---|
| El Run abarca la conversación; 1 Run activo por Session; PLAN → ACT en el mismo Run con aprobación; cancelación | §6–§9, §14, §50, §60, §70 | 0035 |
| Máquinas de estado canónicas, con un evento por transición | §7, §9, §10, §12 | 0036 |
| Permisos: tipos, combinación Deny < Ask < Allow, **defaults autónomos**, grants por `WorkspaceId`, topes de gasto | §15, §42–§46 | 0037 |
| **Multiplataforma** (Windows + Linux completos, macOS best-effort), sandbox `Strong`/`Basic`, self-contained por RID, multi-target net8 | §3, §39, §47, §77 | 0038 |
| Confianza de workspace, **configuración YAML** y rutas por scope y plataforma | §51, §52, §59 | 0039 |
| UI localizable, español por defecto | §64.1 | 0040 |
| `omni sim` y componentes mínimos de M1 | §86 | 0041 |
| Conteo de tokens y política de contexto antes de M4 | §24, §26 | 0042 |
| Auditoría (sobrevive a la purga) y telemetría solo local | §58, §69 | 0043 |
| **Toda la memoria entra en v1** (corrige §3) | §3, §54 | 0028 rev. 2 |
| Claude Code lanes en M7; TUI después de M3; roadmap consolidado en `arquitectura.md` §24 | §86–§99 | 0012 rev. 3 |

## Cambios en v0.6

| Tema | Secciones afectadas | ADR |
|---|---|---|
| Política operativa por configuración de modelo, onboarding, límites de tools y mutaciones | §20, §35, §38, §88–§90, §99, §103 | 0044 |
| Cuestionarios estructurados: single/multi-select, texto libre, `Otro`, `user.ask`, durabilidad y frame TUI | §60, §64.1, §88–§89, §99, §103 | 0045 |

---

# 1. Propósito

OmniCore será un runtime de agentes independiente, local-first y orientado a tareas, encargado de ejecutar workflows agentic sobre repositorios y workspaces mediante modelos locales y modelos frontera.

OmniCore deberá proporcionar de forma desacoplada:

* administración de sesiones y ejecuciones;
* descomposición de objetivos en tareas;
* ejecución mediante Tasks y Lanes;
* administración de contexto;
* routing entre modelos;
* ejecución de herramientas;
* control determinista de permisos;
* sandbox;
* subagentes;
* aislamiento de ejecución;
* validación;
* manejo de artifacts;
* eventos;
* persistencia;
* hooks;
* skills;
* recuperación de ejecuciones;
* CLI y protocolo para consumidores externos.

OmniCore no deberá contener dependencias específicas de la interfaz de OmniCoder ni de Pi.

El primer consumidor será `omni`, un CLI diseñado inicialmente como herramienta técnica de desarrollo y diagnóstico del propio runtime.

Cuando OmniCore alcance los criterios de madurez definidos en este documento, OmniCoder podrá reemplazar progresivamente Pi mediante un adapter sobre OmniCore.

---

# 2. Objetivos de diseño

OmniCore deberá poder ejecutar el mismo objetivo utilizando modelos con capacidades muy diferentes sin cambiar la arquitectura.

Ejemplo conceptual:

```text
Task: corregir AuthenticationService
                 │
                 ▼
             OmniCore
                 │
     ┌───────────┼────────────┐
     ▼           ▼            ▼
   4B CPU     Qwen local    Frontier
  metadata       worker     Luna / Sol
```

La diferencia entre modelos deberá resolverse mediante:

* `ModelDescriptor`;
* `ModelPolicy`;
* `EffectiveModelProfile`;
* `ContextBudget`;
* `ToolPlan`;
* `ReasoningEffort`;
* estrategias de ejecución.

Nunca mediante lógica del tipo:

```csharp
if (model == "qwen")
```

o:

```csharp
if (model == "gpt")
```

---

# 3. Alcance de OmniCore v1

OmniCore v1 deberá incluir:

| Área                | Alcance |
| ------------------- | ------- |
| Sessions            | Sí      |
| Runs                | Sí      |
| Task Graph          | Sí      |
| Tasks               | Sí      |
| Lanes               | Sí      |
| Turns               | Sí      |
| Agent Runtime       | Sí      |
| Explorer            | Sí      |
| Coder               | Sí      |
| Verifier            | Sí      |
| Context Engine      | Sí      |
| Context budgeting   | Sí      |
| Context pruning     | Sí      |
| Externalización     | Sí      |
| Context compression | Sí      |
| Context compaction  | Sí      |
| Tool runtime        | Sí      |
| Tool discovery      | Sí      |
| Permissions         | Sí      |
| Sandbox             | Sí      |
| Artifact store      | Sí      |
| Model providers     | Sí      |
| Local llama.cpp     | Sí      |
| Frontier providers  | Sí      |
| Model routing       | Sí      |
| Completion gates    | Sí      |
| Hooks               | Sí      |
| Skills              | Sí      |
| Event stream        | Sí      |
| Persistence         | Sí      |
| CLI                 | Sí      |
| Protocol            | Sí      |
| Worktree isolation  | Sí      |
| Parallel lanes      | Sí      |

Queda fuera de OmniCore v1:

* interfaz Desktop;
* Office;
* interfaz Web completa;
* agentes especializados en documentos;
* agente especializado en networking;
* marketplace de plugins;
* cluster distribuido;
* multiusuario server;
* RunPod/H200 orchestration;
* ~~memoria de usuario de largo plazo~~ — **v0.5: entra en v1** (Session, Project, Workspace y Global; ADR-0028 rev. 2);
* RAG avanzado;
* gestión organizacional.

La arquitectura deberá permitir estas extensiones posteriormente sin modificar el núcleo conceptual.

**Plataformas (v0.5, ADR-0038):** Windows y Linux con soporte completo (sandbox, credenciales, tests); macOS en best-effort (sin sandbox fuerte en v1).

---

# 4. Invariantes arquitectónicos

Las siguientes reglas son obligatorias.

## INV-001 — Separación modelo/herramientas

Un modelo nunca ejecutará herramientas directamente.

```text
Model
  ↓
Tool Request
  ↓
Tool Runtime
```

## INV-002 — Autoridad de permisos

El modelo nunca otorgará sus propios permisos.

## INV-003 — Tools sin autoridad

Una Tool nunca decidirá si está autorizada.

## INV-004 — Subagentes estructurados

Todo subagente deberá existir mediante:

```text
Task
 ↓
Lane
 ↓
AgentExecution
```

No se permitirá `SpawnAgent(prompt)` como abstracción primaria.

## INV-005 — Context isolation

Los subagentes no recibirán automáticamente el transcript del padre.

Recibirán un `TaskPacket` y una materialización explícita de contexto.

## INV-006 — Historia canónica

El historial canónico será append-only.

*v0.2:* la historia canónica es el **Canonical Journal**: un Event Store append-only más artifacts inmutables referenciados por eventos (ADR-0001). Un artifact referenciado por historia canónica no puede desaparecer arbitrariamente.

## INV-007 — Contexto como proyección

El contexto enviado al modelo será siempre una proyección derivada del estado.

## INV-008 — Compaction no destructivo

La compactación nunca modificará el historial canónico.

## INV-009 — Externalización

Outputs grandes deberán poder convertirse en `ArtifactRef`.

## INV-010 — Model capability driven

La lógica del runtime dependerá de capacidades, no de nombres de modelos.

## INV-011 — Providers desacoplados

El Agent Runtime no conocerá OpenAI, llama.cpp, Anthropic, OpenRouter ni otros providers concretos.

## INV-012 — Persistence desacoplada

El Engine no escribirá directamente SQLite, JSONL ni archivos de log.

## INV-013 — Tools sin UI

Las Tools no contendrán lógica de rendering.

## INV-014 — Hooks restrictivos

Un Hook podrá restringir, observar o enriquecer una operación, pero nunca ampliar permisos establecidos por capas superiores.

## INV-015 — Completion determinista

Una ejecución no terminará únicamente porque el modelo declare haber terminado.

La finalización deberá pasar por `CompletionGates`.

## INV-016 — Commands/Events

Los clientes interactuarán con OmniCore mediante commands y eventos.

## INV-017 — Plan canónico propiedad del runtime (v0.2)

El Plan de un Run es estado canónico. El modelo solo propone `PlanMutation`s; `PlanService` y `ProgressReconciler` deciden (ADR-0016).

## INV-018 — Ninguna ejecución sin `AuthorizedToolIntent` (v0.2)

Una tool solo se ejecuta con un `AuthorizedToolIntent`, que solo el Permission Engine puede construir. `Prepare` es puro (ADR-0014).

## INV-019 — Intent durable antes del efecto (v0.2)

Ningún efecto lateral comienza antes de que su intent esté confirmado con un commit Barrier. Todo intento de efecto es reconciliable tras un crash (ADR-0002, ADR-0004).

## INV-020 — Los secretos nunca se persisten (v0.2)

API keys, tokens y credenciales nunca llegan al journal, los artifacts, el contexto, el transcript, los logs, el audit ni la telemetría (ADR-0018).

## INV-021 — Estado en eventos, contenido en artifacts (v0.2)

El estado de Session, Run, Plan, TaskGraph, Lane, Turn y ToolCall se reconstruye solo desde eventos. Los artifacts guardan contenido (ADR-0001).

## INV-022 — Autorización de procesos estructurada (v0.2)

La autorización de procesos se decide sobre ejecutable resuelto + argv, nunca interpretando texto de shell (ADR-0015).

## INV-023 — Procedencia de contexto (v0.3)

Todo `ContextItem` lleva `ContextProvenance`, y el Materializer rechaza items sin procedencia (ADR-0029).

## INV-024 — UI desacoplada (v0.4)

- **Dependencias:** ningún tipo de framework visual (Terminal.Gui, Spectre.Console) existe fuera de `OmniCore.Cli`.
- **Estado único:** todos los renderers (TUI, plain y JSON) consumen el mismo estado (`ClientProjection`) o los mismos eventos del protocolo.
- **Independencia:** OmniCore funciona igual sin la TUI (ADR-0030).

---

# 5. Modelo conceptual principal

La jerarquía fundamental será:

*v0.2:* un Run contiene **un Plan** (progreso lógico, con revisiones) y **un TaskGraph** (ejecución técnica), relacionados N:M por `PlanItem ↔ Task` (ADR-0016).

```text
Session
  │
  └── Run
       │
       ├── Plan (rev.N)
       │    └── PlanItem ──(PlanItemLink N:M)──┐
       │                                        ▼
       └── TaskGraph
            │
            ├── Task
            │    │
            │    └── Lane
            │         │
            │         └── AgentExecution
            │              │
            │              ├── Turn
            │              ├── Turn
            │              └── Turn
            │
            └── Task
                 └── Lane
```

---

# 6. Session

Una `Session` representa un espacio lógico durable de interacción.

No representa una ejecución particular.

## FR-SES-001

El sistema deberá crear sesiones.

## FR-SES-002

Una sesión deberá tener un identificador estable.

## FR-SES-003

Una sesión podrá contener múltiples Runs.

## FR-SES-004

Una sesión deberá asociarse a un Workspace (v0.5: el journal se ubica por `WorkspaceId`, así que no existe sesión sin workspace; ADR-0022).

## FR-SES-005

Una sesión deberá poder restaurarse desde persistencia.

Modelo mínimo:

```csharp
public sealed record Session
{
    public required SessionId Id { get; init; }

    public required WorkspaceRef Workspace { get; init; }

    public ProfileId Profile { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
```

---

# 7. Run

Un `Run` representa una intención explícita del usuario.

Ejemplo:

```text
"Corrige todos los errores de autenticación."
```

## FR-RUN-001

Una Session podrá contener múltiples Runs.

## FR-RUN-002

Un Run deberá contener un objetivo.

## FR-RUN-003

Un Run deberá tener un modo de ejecución.

```csharp
public enum RunMode
{
    Plan,
    Act,
    Orchestrate
}
```

## FR-RUN-004

PLAN, ACT y ORQ deberán ser políticas de ejecución, no tres runtimes distintos.

## FR-RUN-005

Un Run deberá poder cancelarse.

## FR-RUN-006

Un Run interrumpido deberá poder recuperar suficiente estado para ser inspeccionado y, cuando sea técnicamente posible, reanudado.

Estados:

```text
Created
Preparing
Running
Validating
Completed
CompletedWithIssues   (v0.2: FailurePolicy = AllowPartial con items requeridos Failed; ADR-0016 §10)
Failed
Cancelled
```

*v0.5:* la máquina de estados canónica de Run, con su evento por transición, está en **ADR-0036 §1**:

- `Preparing` desaparece;
- se agrega `AwaitingInput`;
- `RunCompleted` lleva el outcome `Completed | CompletedWithIssues | Planned`.

Cómo se relacionan los mensajes del usuario con el Run y el paso PLAN → ACT están en **ADR-0035**.

## FR-RUN-007 (v0.2)

Todo Run tendrá un Plan desde su creación. En estrategia Direct sin fase de planificación, el runtime crea `Plan rev.1` con un único item igual al objetivo del Run (ADR-0016 §11).

## 7.1 Plan (v0.2)

- **Qué es:** el Plan es el progreso lógico, el compromiso visible para el usuario. El TaskGraph son las unidades técnicas de ejecución.
- **Relación:** `PlanItem ↔ Task` es N:M mediante `PlanItemLink(TaskId, Required, Role)`.
- **Estados de `PlanItem`:** `Pending`, `Ready`, `InProgress`, `Blocked`, `Completed`, `Failed`, `Skipped`, `Cancelled`.
- **Quién lo modifica:** el modelo **propone** `PlanMutation`s (`Start`, `Complete`, `Block`, `Add`, `Split`, `Reorder`, `Skip`, `Revise`). `PlanService` valida, aplica la política de impacto y emite eventos. `ProgressReconciler` sincroniza automáticamente con el TaskGraph (reglas R1–R7).
- **Revisiones:** el plan conserva sus revisiones (`rev.1 → rev.2 → …`). Los cambios que expanden el scope requieren `Ask`.
- **Detalle completo:** ADR-0016.

---

# 8. TaskPacket

Una `Task` será la unidad lógica mínima de trabajo delegable.

Su representación portable será `TaskPacket`.

```csharp
public sealed record TaskPacket
{
    public required TaskId Id { get; init; }

    public required string Objective { get; init; }

    public TaskScope Scope { get; init; }

    public IReadOnlyList<ResourceRef> Resources { get; init; }

    public IReadOnlyList<AcceptanceCriterion>
        AcceptanceCriteria { get; init; }

    public VerificationPlan Verification { get; init; }

    public PermissionScope Permissions { get; init; }

    public ModelPolicy ModelPolicy { get; init; }

    public ContextPolicy ContextPolicy { get; init; }

    public IsolationPolicy Isolation { get; init; }

    public TaskBudget Budget { get; init; }

    public OutputContract Output { get; init; }

    public RecoveryPolicy Recovery { get; init; }
}
```

## FR-TASK-001

Un TaskPacket deberá poder entenderse sin acceder al transcript completo de la sesión.

## FR-TASK-002

Deberá declarar un Objective explícito.

## FR-TASK-003

Deberá poder declarar criterios de aceptación.

## FR-TASK-004

Deberá poder declarar recursos relevantes.

## FR-TASK-005

Deberá declarar límites de permisos.

## FR-TASK-006

Deberá poder declarar límites de presupuesto.

## FR-TASK-007

Deberá poder especificar aislamiento.

## FR-TASK-008

Deberá poder especificar requisitos mínimos de modelo.

---

# 9. Task Graph

Un Run podrá contener un DAG de Tasks.

Ejemplo:

```text
Explore
   │
   ▼
Implement
   │
   ▼
Verify
```

o:

```text
Explore backend ───┐
                   ├──► Implement ───► Verify
Explore tests ─────┘
```

## FR-TG-001

El sistema deberá resolver dependencias entre Tasks.

## FR-TG-002

Un Task podrá declararse Ready únicamente cuando sus dependencias requeridas hayan finalizado correctamente.

## FR-TG-003

El scheduler deberá imponer un límite configurable de concurrencia.

## FR-TG-004

El fallo de un Task deberá poder propagarse según política.

Opciones:

```text
FailRun
BlockDependents
AllowPartial
Retry
Escalate
```

## FR-TG-005 — Estados de Task (v0.2)

*v0.5:* la tabla definitiva de transiciones y eventos está en **ADR-0036 §2**. Agrega:

- `Running → Cancelled`;
- `Blocked → Ready | Failed`;
- `Failed` solo al agotar la `RecoveryPolicy`.

La `FailurePolicy` del Run (`FailRun | BlockDependents | AllowPartial`, por defecto `BlockDependents`) está en ADR-0035 §2. `Retry` y `Escalate` pasan a la `RecoveryPolicy` (§72).

```text
Pending → Ready → Running → Completed
                         ↘ Blocked ↔ Running
                         ↘ Failed
Pending/Ready/Blocked → Cancelled | Skipped
```

- `Ready` requiere que las dependencias requeridas estén `Completed` (FR-TG-002).
- Una Task puede tener **varias Lanes a lo largo del tiempo**, por reintento, escalación o restart, pero en v1 solo una activa a la vez.

---

# 10. Lane

Una `Lane` representa una ejecución concreta de una Task.

Task responde:

```text
¿Qué debe hacerse?
```

Lane responde:

```text
¿Quién, dónde y cómo lo está ejecutando?
```

Modelo conceptual:

```csharp
public sealed record Lane
{
    public required LaneId Id { get; init; }

    public required TaskId TaskId { get; init; }

    public AgentProfileId AgentProfile { get; init; }

    public ModelSelection Model { get; init; }

    public IsolationHandle Isolation { get; init; }

    public PermissionScope Permissions { get; init; }

    public LaneExecutionMode Mode { get; init; }

    public LaneState State { get; init; }
}
```

Estados:

```text
Queued
Provisioning
Running
WaitingForModel
WaitingForTool
WaitingForPermission
WaitingForSubtask
Validating
Blocked
Completed
Failed
Cancelled
Stalled
```

*v0.5 (ADR-0036 §3):*

- **Canónicos:** solo `Queued`, `Provisioning`, `Running`, `Blocked`, `Completed`, `Failed` y `Cancelled`, cada uno con su evento.
- **Derivados:** `Waiting*`, `Validating` y `Stalled` son actividad que se calcula de otros eventos; no se persisten.

## FR-LANE-001

No podrá existir ejecución de subagente fuera de una Lane.

## FR-LANE-002

La Lane deberá registrar su modelo efectivo.

## FR-LANE-003

La Lane deberá registrar su contexto efectivo.

## FR-LANE-004

La Lane deberá registrar su frontera de permisos.

## FR-LANE-005

La Lane deberá soportar aislamiento.

## FR-LANE-006

La Lane deberá soportar ejecución:

```text
Foreground
Background
Parallel
```

---

# 11. Heartbeat

Cada Lane deberá emitir heartbeat.

Modelo:

```csharp
public sealed record LaneHeartbeat
{
    public DateTimeOffset Timestamp { get; init; }

    public LaneState State { get; init; }

    public int CurrentTurn { get; init; }

    public string? CurrentOperation { get; init; }

    public DateTimeOffset LastProgressAt { get; init; }

    public long TokensUsed { get; init; }

    public int ToolCalls { get; init; }
}
```

Debe permitir diferenciar:

* generación lenta;
* tool bloqueada;
* proceso externo colgado;
* provider muerto;
* ausencia de progreso;
* loop agentic.

---

# 12. Turn

Un `Turn` representa una interacción individual con un modelo.

```csharp
public sealed record AgentTurn
{
    public required TurnId Id { get; init; }

    public required ContextSnapshotId Context { get; init; }

    public required ModelSelection Model { get; init; }

    public required ToolPlanId ToolPlan { get; init; }

    public TokenUsage Usage { get; init; }

    public IReadOnlyList<ToolCallId> ToolCalls { get; init; }
}
```

## FR-TURN-001

Cada request a un modelo deberá pertenecer a un Turn.

## FR-TURN-002

El Turn deberá registrar el ContextSnapshot exacto utilizado.

## FR-TURN-003

El Turn deberá registrar ToolPlan.

## FR-TURN-004

Deberá registrar consumo de tokens cuando el provider lo permita.

## FR-TURN-005

Deberá permitir reproducir posteriormente las condiciones relevantes de una ejecución.

## FR-TURN-006 (v0.2)

`TurnStarted` registrará un `ExecutionFingerprint` por componentes: modelo, perfil efectivo, harness, adapter, política de contexto, ToolPlan, template, agent profile, skills, revisión de plan, overrides y build (ADR-0017).

## FR-TURN-007 (v0.2)

Las ToolCalls de un Turn siguen el ciclo de vida durable del Effect Journal. Al reanudar, se reconcilian antes de continuar, y un Turn con respuesta de modelo completa no se re-infiere (ADR-0004).

---

# 13. Agent Runtime

El Agent Runtime deberá permanecer pequeño.

Loop conceptual:

```text
Materialize Context
       ↓
Select Model
       ↓
Build ToolPlan
       ↓
Model Inference
       ↓
Tool calls?
 ┌─────────────┐
 │ yes         │ no
 ▼             ▼
Execute       Proposed completion
 │             │
 └──────► Next Turn
```

No deberá contener lógica específica de:

* llama.cpp;
* OpenAI;
* Git;
* PowerShell;
* filesystem;
* SQLite;
* MCP.

---

# 14. Execution Strategies

Se soportarán inicialmente:

```csharp
public enum ExecutionStrategy
{
    Direct,
    Workflow,
    Delegated
}
```

## Direct

```text
Task
 ↓
Agent
 ↓
Tools
```

## Workflow

```text
Explore
 ↓
Implement
 ↓
Verify
```

## Delegated

```text
            Parent
              │
     ┌────────┼────────┐
     ▼        ▼        ▼
 Explorer   Coder   Research
     │        │        │
     └────────┼────────┘
              ▼
           Integrate
              │
              ▼
            Verify
```

PLAN usará típicamente Direct.

ACT usará Direct o Workflow.

ORQ podrá utilizar Delegated.

---

# 15. Agent profiles

V1 deberá incluir perfiles conceptuales:

### Explorer

Tools preferidas:

```text
read
list
glob
grep
symbols
tool.search
```

Sin escritura.

### Coder

```text
read
search
patch
write
build
test
```

### Verifier

```text
read
build
test
lint
diff
```

El perfil define capacidades máximas.

Los permisos efectivos serán la intersección de todas las capas.

---

# 16. AgentResult

Los subagentes no devolverán normalmente transcripts completos.

Contrato:

```csharp
public sealed record AgentResult
{
    public AgentOutcome Outcome { get; init; }

    public string Summary { get; init; }

    public IReadOnlyList<Finding> Findings { get; init; }

    public IReadOnlyList<EvidenceRef> EvidenceRefs { get; init; }

    public IReadOnlyList<ArtifactRef> ArtifactRefs { get; init; }

    public IReadOnlyList<PathRef> FilesRead { get; init; }

    public IReadOnlyList<PathRef> FilesChanged { get; init; }

    public ValidationResult Validation { get; init; }

    public IReadOnlyList<string> RemainingIssues { get; init; }

    public ConfidenceLevel Confidence { get; init; }

    public IReadOnlyList<PlanMutation> ProposedPlanMutations { get; init; }   // v0.5: el PlanService las valida (ADR-0016, ADR-0041)
}
```

El transcript detallado permanecerá accesible mediante eventos/artifacts.

---

# 17. Model Runtime

El modelo será representado por capabilities.

```csharp
public sealed record ModelDescriptor
{
    public required ModelId Id { get; init; }

    public long ContextWindow { get; init; }

    public long RecommendedUsableContext { get; init; }

    public long MaxOutputTokens { get; init; }

    public ModelCapabilities Capabilities { get; init; }

    public IReadOnlyList<ReasoningEffort> ReasoningEfforts
        { get; init; }

    public IReadOnlyList<ToolMode> ToolModes { get; init; }

    public IReadOnlyList<InputModality> InputModalities { get; init; }

    public bool ParallelTools { get; init; }

    public bool MultiAgent { get; init; }
}
```

---

# 18. Model Provider

```csharp
public interface IModelProvider
{
    ValueTask<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken);
}
```

Providers previstos:

```text
LlamaCppProvider
OpenAIProvider
OpenRouterProvider
otros futuros
```

Agent Runtime dependerá únicamente de `IModelProvider`/`IModelRuntime`.

**v0.2 — reemplazado por ADR-0005:**

- **Contrato:** un único método, `StreamAsync(ModelRequest) → IAsyncEnumerable<ModelStreamEvent>`. `CompleteAsync` pasa a ser un helper que agrega el stream.
- **Tres familias nativas:**
  - `OpenAIResponsesProvider`, con los perfiles `api` y `codex`;
  - `AnthropicMessagesProvider`, nativo y nunca vía OpenAI-compatible;
  - `OpenAiChatCompatibleProvider`, para ik_llama, llama.cpp y OpenRouter.
- **Representación interna:**
  - `ModelResponse { ContentBlocks[] (Text, ToolCall, ToolResult, Reasoning, Citation, Media, ProviderOpaque), Usage, StopReason, ProviderState, Metadata }`;
  - `ProviderOpaque` y `ProviderState` preservan, sin interpretarlos, los datos que deben volver intactos al provider: firmas de thinking y reasoning cifrado.

---

# 19. ModelSelection

El router devolverá una selección completa:

```csharp
public sealed record ModelSelection
{
    public required ModelId Model { get; init; }

    public ReasoningEffort Effort { get; init; }

    public ToolMode ToolMode { get; init; }

    public int ContextBudget { get; init; }

    public string? ServiceTier { get; init; }
}
```

No únicamente el nombre del modelo.

---

# 20. EffectiveModelProfile

*v0.5:* la intersección que describe esta sección es **por Turn** y se llama **`EffectiveExecutionProfile`**. `EffectiveModelProfile` es **por modelo** (por `ModelQualificationKey`, ADR-0007). La tabla de propiedad de los tipos de modelo está en ADR-0005.

Las capacidades reales durante una ejecución serán:

```text
ModelCapabilities
       ∩
TaskRequirements
       ∩
AgentProfile
       ∩
Permissions
       ∩
RuntimeAvailability
       ↓
EffectiveModelProfile
```

Un modelo capaz de usar 50 tools no implica que recibirá 50 tools.

**v0.2 — ADR-0007:**

- **Fuentes del perfil:** `EffectiveModelProfile` se resuelve **por campo** desde cuatro fuentes: `DeclaredCapabilities` (hechos), `HeuristicDefaults` (provisionales; el número de parámetros solo participa aquí), `EmpiricalModelProfile` (traits medidos por la Model Qualification Suite para una `ModelQualificationKey`) y `UserOverrides` (siempre gana).
- **Política del harness:** `HarnessPolicyResolver`, una función pura, deriva `ToolCallFormat`, `ToolMode`, `MaxVisibleTools`, `GuidanceLevel`, `RepairAttempts`, `PlanControl`, `StallThresholdTurns` y `CompletionStrictness`.
- **Estados de cualificación:** `Unknown → Declared → ProvisionallyClassified → Qualified → Calibrated` (+ `Stale`).
- **Router:** consume solo `EffectiveModelProfile`. No ejecuta benchmarks ni infiere calidad por nombre.
- **Política operativa (ADR-0044):** `UserModelPolicy` fija un techo por `ModelPolicyKey` exacta. El
  runtime intersecta ese techo con Task, AgentProfile, permisos, trust y disponibilidad; una
  categoría de modelo nunca es un grant. Configuraciones desconocidas usan `ObserveOnly`.

---

# 21. Model Router

El router deberá considerar al menos:

* complejidad;
* capacidades requeridas;
* tamaño de contexto;
* herramientas;
* latencia;
* privacidad;
* disponibilidad;
* costo;
* preferencia local;
* escalación permitida.

Ejemplo de política, no hardcode:

```text
Meta tasks
→ CPU 4B

Exploration
→ local small/medium

Implementation
→ local worker

Ambiguous reasoning
→ frontier fast

Complex architecture/debugging
→ frontier strong
```

Los nombres concretos de Luna, Sol, Qwen, etc. pertenecerán a configuración.

---

# 22. MetaModelService

El modelo CPU pequeño no deberá tratarse principalmente como agente conversacional.

Contrato funcional:

```text
SummarizeToolOutput
ExtractFacts
ExtractDecisions
RankFiles
ClassifyTask
CompressContext
SummarizeDiff
GenerateSessionTitle
SuggestRoute
```

Objetivo:

```text
output grande
   ↓
MetaModel
   ↓
representación estructurada pequeña
```

---

# 23. Context Engine

El modelo nunca recibirá directamente la Session.

Flujo:

```text
Canonical Events
      │
      ▼
Session Projection
      │
      ▼
Context Contributors
      │
      ▼
Context Materializer
      │
      ▼
ContextSnapshot
      │
      ▼
Model
```

---

# 24. ContextItem

Todo elemento potencialmente insertable será un `ContextItem`.

```csharp
public sealed record ContextItem
{
    public ContextItemId Id { get; init; }

    public ContextItemKind Kind { get; init; }

    public string Content { get; init; }

    public int EstimatedTokens { get; init; }

    public ContextPriority Priority { get; init; }

    public RetentionPolicy Retention { get; init; }

    public ContextSource Source { get; init; }   // v0.3: ContextProvenance (contributor, categoría, ComponentSource, refs, sensibilidad; ADR-0029)
}
```

Kinds:

```text
System
Task
Decision
Constraint
UserMessage
AssistantMessage
ToolResult
File
Skill
Knowledge
SubagentResult
Summary
Checkpoint
WorkingState      (v0.2)
Memory            (v0.3, ADR-0028; Knowledge sigue siendo un kind distinto)
```

*v0.2:* `WorkingState` es una proyección del estado canónico: objetivo, plan compacto, item y Task actuales, criterios de aceptación, blockers, trabajo pendiente y siguiente paso.

- Entra con `Priority = Pinned` y `Retention = RegenerateEachTurn`, y tiene presupuesto reservado.
- Nunca se poda, comprime ni compacta. Se ubica al final del contexto, antes del turno actual, para no invalidar el prefijo cacheado.
- Sobrevive a compaction, rebuild, cambio de modelo y resume (ADR-0016 §7).

---

# 25. Context contributors

Servicios externos podrán aportar ContextItems:

```csharp
public interface IContextContributor
{
    ValueTask<IReadOnlyList<ContextItem>> GetContextAsync(
        ContextRequest request,
        CancellationToken cancellationToken);
}
```

Ejemplos:

```text
SkillContributor
KnowledgeContributor
RagContributor
ProjectMemoryContributor
SessionMemoryContributor
GitContributor
WorkspaceContributor
```

El Context Engine no deberá conocer su implementación interna.

---

# 26. Context Budget

El Context Engine deberá presupuestar explícitamente el contexto.

Ejemplo:

```text
Usable context: 32K

System               3K
Task                 2K
Skills               2K
Files                8K
Conversation         4K
Tool observations    3K
Working state        3K
Generation reserve   7K
```

El presupuesto dependerá de `ModelDescriptor`.

*v0.2/v0.3:* hay slots explícitos para `WorkingState` (reservado y pinned; ADR-0016), `Skills` (ADR-0026) y `Memory` (ADR-0028). La memoria nunca consume el slot del WorkingState.

---

# 27. Gestión de presión de contexto

Se implementarán cuatro operaciones distintas.

## PRUNE

Eliminar información que ya no aporta valor.

## COMPRESS

Transformar información conservando semántica relevante.

Ejemplo:

```text
12K build output
      ↓
800 token StructuredObservation
```

## EXTERNALIZE

Mover contenido fuera del hot context.

```text
tool output
   ↓
ArtifactStore
   ↓
ArtifactRef
```

## COMPACT

Sustituir una región histórica por un checkpoint.

Estas operaciones no deberán confundirse.

---

# 28. ContextCheckpoint

No se dependerá únicamente de resúmenes narrativos.

Modelo conceptual:

```csharp
public sealed record ContextCheckpoint
{
    public IReadOnlyList<string> Goals { get; init; }

    public IReadOnlyList<string> Constraints { get; init; }

    public IReadOnlyList<string> Decisions { get; init; }

    public IReadOnlyList<string> Facts { get; init; }

    public IReadOnlyList<PathRef> RelevantFiles { get; init; }

    public IReadOnlyList<PathRef> ModifiedFiles { get; init; }

    public IReadOnlyList<string> FailedAttempts { get; init; }

    public TestState Tests { get; init; }

    public IReadOnlyList<string> PendingWork { get; init; }

    public IReadOnlyList<string> OpenQuestions { get; init; }
}
```

*v0.2:* el checkpoint **no** es la fuente del plan. `PendingWork` y el progreso se toman del Plan canónico vía `WorkingState`. El checkpoint resume hechos, decisiones e intentos fallidos de la región compactada.

---

# 29. ContextSnapshot

Cada Turn deberá asociarse al contexto exacto utilizado.

```csharp
public sealed record ContextSnapshot
{
    public ContextSnapshotId SnapshotId { get; init; }

    public SessionId SessionId { get; init; }

    public RunId RunId { get; init; }

    public TaskId TaskId { get; init; }

    public LaneId LaneId { get; init; }

    public TurnId TurnId { get; init; }

    public long BasedOnEventSequence { get; init; }

    public ExecutionFingerprint Fingerprint { get; init; }   // v0.2: reemplaza ModelDescriptorHash (ADR-0017)

    public IReadOnlyList<ContextItem> Items { get; init; }

    public int TokenCount { get; init; }
}
```

Esto permitirá debugging y replay.

---

# 30. Tool architecture

La arquitectura de tools será:

```text
ToolCatalog
     ↓
ToolPlanner
     ↓
ToolPlan
     ↓
ToolRouter
     ↓
ToolRuntime
```

---

# 31. ToolCatalog

Contendrá las Tools instaladas/disponibles.

No significa que todas serán visibles al modelo.

Fuentes futuras:

```text
Builtin
Project
Plugin
MCP
Dynamic
```

*v0.3 (ADR-0027):*

- **Orígenes:** `BuiltIn`, `Project`, `Skill`, `Extension`, `Mcp` y `Dynamic`.
- **Ids:** el `ToolId` canónico lleva un namespace por origen (`project.`, `skill.<id>.`, `ext.<id>.`, `mcp.<server>.`, `dyn.<owner>.`), así que no puede colisionar.
- **Sustitución:** las built-ins sensibles son `Protected`. Solo se reemplazan con una preferencia explícita del usuario más un `Ask`, nunca en silencio.

---

# 32. ToolDescriptor

```csharp
public sealed record ToolDescriptor
{
    public required ToolId Id { get; init; }

    public required string Description { get; init; }

    public required JsonSchema InputSchema { get; init; }

    public IReadOnlyList<ToolCapability> Capabilities
        { get; init; }

    public IReadOnlyList<string> Tags { get; init; }

    public bool ReadOnly { get; init; }

    public bool ConcurrencySafe { get; init; }

    public bool Destructive { get; init; }

    public ToolRisk Risk { get; init; }

    public OutputPolicy OutputPolicy { get; init; }

    public ComponentSource Source { get; init; }        // v0.3: kind · scope · trust · owner · version (ADR-0023)

    public ToolProtection Protection { get; init; }     // v0.3: Protected impide sustitución implícita (ADR-0027)
}
```

---

# 33. ITool

*v0.2 — reemplazado por ADR-0014.* `ValidateAsync` desaparece, porque podía hacer I/O observable antes de la autorización.

```csharp
public interface ITool
{
    ToolDescriptor Descriptor { get; }

    // Pura: sin I/O ni efectos; síncrona.
    ToolPreparation Prepare(
        ValidatedToolCall call,
        ToolPreparationContext context);

    // Solo acepta intents autorizados; solo Security puede construirlos.
    ValueTask<ToolResult> ExecuteAsync(
        AuthorizedToolIntent intent,
        ToolExecutionContext context,
        CancellationToken cancellationToken);
}
```

Pipeline:

```text
RawToolCall → schema validation → Prepare → ToolIntent { Effect, Claims, Risk, Reconciliation } → Permission Engine → AuthorizedToolIntent → ExecuteAsync
```

---

# 34. ToolPlanner

El ToolPlanner deberá determinar qué tools son relevantes para cada Turn.

Entrada:

```text
Task
AgentProfile
ModelCapabilities
Permissions
CurrentContext
PreviousTurns
```

Salida:

```text
ToolPlan
```

---

# 35. Progressive Tool Disclosure

No se expondrá automáticamente todo el catálogo.

Ejemplo:

```text
Installed tools:     87
Task relevant:       21
Agent compatible:    12
Permitted:            9
Selected:             6
Visible to model:     5
```

Esto será particularmente importante para modelos pequeños.

La selección tiene dos capas (ADR-0044): `ToolPlanner` reduce lo visible y
`ModelCapabilityBoundary` rechaza en runtime tools o mutaciones fuera de la política, aunque el
modelo consiga emitirlas.

---

# 36. tool.search

OmniCore deberá proporcionar una herramienta de descubrimiento.

Ejemplo:

```text
tool.search("git history")
```

Resultado:

```text
git.log
git.show
git.diff
```

El ToolPlan posterior podrá incorporar esas tools.

---

# 37. Tool modes

Se contemplarán:

```csharp
public enum ToolMode
{
    Direct,
    Discovered,
    Code
}
```

### Direct

Pocas tools explícitas.

Orientado a modelos pequeños.

### Discovered

Catálogo parcial + `tool.search`.

### Code

Superficie mínima con ejecución programática cuando el modelo/provider lo soporte.

---

# 38. Tools iniciales

MVP read-only:

```text
filesystem.read
filesystem.list
search.text
```

Posteriormente:

```text
filesystem.write
filesystem.patch
filesystem.create
filesystem.delete
filesystem.move

shell.exec

git.status
git.diff
git.log
git.show
```

ADR-0044 distingue patch, creación, reemplazo y operaciones destructivas. En `PatchOnly` solo se
expone patch sobre archivos previamente leídos con `ExpectedVersionToken`; borrar y recrear la
misma ruta cuenta como reemplazo. El runtime valida el diff real antes de aplicar.

*v0.2 (ADR-0015):* el primitive es **`process.exec(executable, argv[], workingDirectory, environment)`**. `shell.exec(shell, script)` existe como superficie de **riesgo alto**: `Ask` por defecto, AppContainer obligatorio y `EffectClass = NonIdempotent`. Security nunca autoriza interpretando texto de shell.

---

# 39. Process Runtime

La ejecución de procesos será un subsistema común.

```csharp
public interface IProcessRuntime
{
    ValueTask<ProcessHandle> StartAsync(...);

    ValueTask WriteAsync(...);

    ValueTask<ProcessResult> WaitAsync(...);

    ValueTask CancelAsync(...);
}
```

Debe manejar:

```text
stdout
stderr
working directory
environment
timeout
exit code
CancellationToken
```

PTY e interacción podrán añadirse sobre el mismo runtime.

*v0.2 — contrato concreto en ADR-0015.* `IProcessRuntime.StartAsync(ProcessLaunch, SandboxProfile)` devuelve un `IProcessHandle` con:

- salida por canal acotado con backpressure;
- spool de la salida completa a un artifact CAS;
- stdin;
- `WaitAsync`;
- cancelación graceful → kill del árbol vía Job Object.

El entorno se arma desde una allowlist + `SecretRef`, y solo se persisten los nombres de las variables.

---

# 40. Artifact Store

Outputs grandes no deberán saturar el contexto.

```csharp
public sealed record ArtifactRef
{
    public ArtifactId Id { get; init; }

    public ContentHash Hash { get; init; }          // v0.2: sha256; identidad física del blob (ADR-0001)

    public string MediaType { get; init; }

    public long Size { get; init; }

    public ArtifactKind Kind { get; init; }

    public Sensitivity Sensitivity { get; init; }   // v0.2: Normal | Sensitive
}
```

ToolResult:

```csharp
public sealed record ToolResult
{
    public string Summary { get; init; }

    public string? Preview { get; init; }

    public ArtifactRef? Artifact { get; init; }

    public long OriginalSize { get; init; }

    public bool WasExternalized { get; init; }
}
```

Política inicial sugerida:

```text
<= 8 KB
inline

8–64 KB
preview + ArtifactRef

> 64 KB
summary + ArtifactRef
```

Configuración posterior podrá depender del modelo.

---

# 41. Optimistic concurrency

Las operaciones de escritura deberán detectar archivos modificados desde su lectura.

Lectura:

```csharp
public sealed record FileReadResult
{
    public string Content { get; init; }

    public FileVersionToken VersionToken { get; init; }
}
```

Escritura:

```csharp
public sealed record FileWriteRequest
{
    public string Content { get; init; }

    public FileVersionToken ExpectedVersionToken { get; init; }
}
```

Si no coincide:

```text
STALE_WRITE
```

El agente deberá releer y reconciliar.

---

# 42. Permission Engine

La decisión de permisos será ternaria.

```csharp
public enum PermissionDecision
{
    Allow,
    Ask,
    Deny
}
```

No `bool`.

---

# 43. Permission scopes

Categorías generales:

```text
Read
WorkspaceWrite
Execute
External
Privileged
```

Además deberá existir granularidad para:

```text
PathRead
PathWrite
Network
Process
```

---

# 44. Permission calculation

Los permisos efectivos serán:

```text
CoreBoundary
   ∩
ParentPermissions
   ∩
TaskPermissions
   ∩
AgentProfile
   ∩
WorkspaceBoundary
   ∩
UserPolicy
   ∩
HookRestrictions
   ∩
ExtensionBoundary   (v0.3: techo declarado en el manifest de la extensión o skill; ADR-0023 §6)
```

Ninguna capa inferior podrá ampliar la frontera superior.

---

# 45. PermissionDelta

Una operación podrá solicitar privilegios adicionales granulares.

Ejemplo:

```text
Need:
read C:\Program Files\dotnet\sdk
```

No:

```text
Need:
PRIVILEGED
```

Modelo:

```csharp
public sealed record PermissionDelta
{
    public IReadOnlyList<PathGrant> Paths { get; init; }

    public NetworkGrant? Network { get; init; }

    public ProcessGrant? Process { get; init; }
}
```

---

# 46. Permission lifetime

Una autorización podrá ser:

```text
Once
Run
Session
Project
```

La persistencia concreta de grants estará fuera del Engine.

*v0.5 (ADR-0037 §5):*

- **Lifetimes:** `Once | Run | Session | Workspace`; `Project` pasa a llamarse `Workspace`.
- **Clave:** los grants persistentes se guardan por `WorkspaceId`, nunca por `ProjectId`, para que un clon o fork no los herede.
- **Almacén:** los de Run y Session son eventos del journal; los de Workspace, reglas en la configuración local del workspace.
- **Revocación:** con `/permissions revoke` (M2).

---

# 47. IsolationPolicy

Task/Lane deberá poder declarar aislamiento.

```text
None
Workspace
GitWorktree
Process
Container
Remote
```

OmniCore v1 implementará al menos:

```text
Workspace
GitWorktree
```

---

# 48. Worktree isolation

Una Lane aislada podrá trabajar sobre:

```text
.omnicore/worktrees/<lane-id>
```

Flujo:

```text
Task
 ↓
create worktree
 ↓
execute
 ↓
validate
 ↓
produce patch/result
 ↓
integration decision
```

Esto reducirá conflictos entre coders concurrentes.

*v0.2 — ADR-0021:*

- **Ubicación:** los worktrees viven **fuera del repo**, en `%LOCALAPPDATA%\OmniCore\workspaces\<WorkspaceId>\worktrees\` (ADR-0022), no en `.omnicore/worktrees`.
- **Base por defecto:** `WorktreeBase = SnapshotOfWorkingTree`. Es un commit sintético con tracked modificados, staged y untracked no ignorados, creado con un index temporal sin tocar el del usuario. Alternativas: `Head` y `PatchOverlay`.
- **Integración:** 3-way merge con el snapshot como base. Si el usuario cambió las mismas zonas, se emite `IntegrationConflict` y no se escribe nada.
- **Resto del ciclo:** cleanup y recuperación por reconciliación con `git worktree list`.

---

# 49. Lifecycle hooks

El Core deberá definir puntos de intercepción.

```text
SessionStarting
SessionStarted

RunStarting

TaskCreated

LaneStarting

BeforeModel
AfterModel

BeforeTool
AfterTool
ToolFailed

BeforePermission

BeforeCompact
AfterCompact

BeforeValidation
AfterValidation

BeforeCompletion

RunCompleted
RunFailed
```

Contrato:

```csharp
public interface IHook
{
    LifecyclePoint Point { get; }

    ValueTask<HookResult> ExecuteAsync(
        HookContext context,
        CancellationToken cancellationToken);
}
```

Los hooks podrán:

```text
observe
annotate
restrict
request validation
```

Nunca elevar permisos.

*v0.2 — ADR-0020:*

- **Niveles de confianza:** en v0.3 se unifican con el modelo de extensiones (ADR-0023): `Core`, `Trusted`, `Project`, `ThirdParty` y `Untrusted`, con ejecución fuera de proceso para los no-Core.
- **Capacidades explícitas:** `Observe` redactado, `Annotate`, `Restrict`, `ReadToolArguments(None|Redacted|Full)`, `ReadArtifacts` y `ReadProviderState` (solo Core).
- **Secretos:** `ReadSecrets` no existe.
- **Resultado:** `HookResult` no tiene variante de ampliación de permisos.

---

# 50. Completion Gates

El modelo podrá proponer finalizar.

El runtime decidirá si es válido.

```text
Model proposes completion
          ↓
Completion Pipeline
          │
          ├── AcceptanceCriteriaGate
          ├── BuildGate
          ├── TestGate
          ├── PendingTaskGate
          ├── PlanCompletionGate        (v0.2, ADR-0016 §10)
          ├── ValidationGate
          └── WorkspaceConsistencyGate
          │
          ▼
Complete / Continue
```

Contrato:

```csharp
public interface ICompletionGate
{
    ValueTask<CompletionDecision> EvaluateAsync(
        CompletionContext context,
        CancellationToken cancellationToken);
}
```

---

# 51. Scope Resolver

La plataforma deberá tener una jerarquía común:

```text
System
Organization
User
Project
Workspace
Session
Run
Task
Lane
```

Se utilizará para resolver:

```text
Configuration
Permissions
Skills
Hooks
Memory
Model preferences
```

Contrato conceptual:

```csharp
public interface IScopeResolver<T>
{
    ValueTask<T> ResolveAsync(
        ScopeContext context,
        CancellationToken cancellationToken);
}
```

*v0.3 (ADR-0022):*

- **Niveles:** `BuiltIn → Organization* → User (Global) → Project → Workspace → Session → Run → Task → Lane`. `Organization` es diferible.
- **Identidades:** `ProjectId` identifica el repo y es común a clones y worktrees. `WorkspaceId` identifica la carpeta raíz local, que puede contener varios proyectos.
- **Resolución:** no hay last-write-wins genérico, cada subsistema declara su estrategia:
  - configuración: el más específico gana, salvo claves `locked`;
  - permisos: intersección;
  - commands, tools y skills: precedencia con restricción de confianza;
  - hooks: unión;
  - memoria: ranking.
- **Resultado:** `ResolveAsync` devuelve además las contribuciones de cada scope, para diagnóstico.

---

# 52. Skills

Formato conceptual:

```text
skill.yaml
instructions.md
references/
scripts/
tests/
```

Metadata ejemplo:

```yaml
id: progress.compile
version: 1

activation:
  paths:
    - "**/*.p"
    - "**/*.w"

execution:
  context: fork
  preferred-model-alias: local-worker   # v0.5: alias, no clase por tamaño (ADR-0007)

tools:
  - filesystem.read
  - progress.compile
```

Una Skill podrá:

* aportar instrucciones;
* habilitar Tools;
* aportar knowledge;
* crear workflows;
* influir en Task decomposition.

No podrá saltarse permisos.

---

# 53. Skill activation

Deberá soportar activación por:

```text
path
extension
language
framework
task type
explicit invocation
```

No deberán cargarse skills irrelevantes en el contexto.

*v0.3 (ADR-0026):*

- **Scopes:** `BuiltIn → Global → Project → Workspace → Session`.
- **Ciclo de vida:** `Discovered → Eligible → Activated → Loaded → Released`. Una skill instalada no consume contexto; solo lo hace en `Loaded`.
- **Precedencia:** gana el scope más específico, salvo skills `sealed` o que el reemplazo venga de una fuente de menor confianza. En esos casos el conflicto queda visible.
- **Contribuciones:** instrucciones, context contributors, tools, workflows, validators y referencias de knowledge.
- **Permisos:** una skill nunca amplía permisos, y su `permissions` es un techo.

---

# 54. Knowledge, RAG y Memory

No serán parte del Core.

Deberán integrarse mediante `IContextContributor`.

Ejemplo:

```text
OmniCore
    │
    ├── ProjectMemoryContributor
    ├── KnowledgeContributor
    ├── RagContributor
    └── SkillContributor
```

La memoria persistente podrá desarrollarse posteriormente sin modificar Context Engine.

*v0.3 (ADR-0028):*

- **Tres conceptos distintos:** `Skill` (cómo hacer), `Knowledge` (dominio estable) y `Memory` (lo aprendido). No comparten storage, ciclo de vida ni políticas.
- **Working Context** (WorkingState y observaciones recientes) no es memoria: lo administra el Context Engine.
- **Scopes de memoria:** Session, Project, Workspace, Global/User y, opcionalmente, scratch de Run/Task/Lane.
- **Registro:** `MemoryRecord` con scope, kind (Fact, Preference, Decision, Convention, Procedure, Pitfall, Architecture, Environment, Summary), importance, confidence, provenance, expiración y `Supersedes`.
- **Recuperación:** `retrieve → rank → dedupe → ContextBudget`.
- **Promoción:** `Observation → MemoryCandidate → MemoryPolicy → Promote | Reject | Ask`. El LLM nunca promociona solo, y Global siempre requiere `Ask`.
- **Independencia:** el runtime funciona igual con la memoria deshabilitada.

---

# 55. Event Model

Toda operación relevante deberá producir eventos.

Envelope:

*v0.2 (ADR-0001, ADR-0013):*

- **Versión del protocolo:** `ProtocolVersion` sale del evento de dominio y pasa al `WireEnvelope` del protocolo.
- **Versión del schema:** el evento durable se identifica por `EventType` + `EventSchemaVersion`.
- **Campos nuevos:** `CausationId`, `CorrelationId`, `PlanItemId?`, `ToolCallId?` y `ArtifactRefs[]`.
- **Tipo de `Sequence`:** `long` por sesión.

Envelope original, conservado como referencia:

```csharp
public abstract record EngineEvent
{
    public int ProtocolVersion { get; init; }   // v0.2: eliminado del dominio; ver ADR-0013

    public EventId EventId { get; init; }

    public SessionId SessionId { get; init; }

    public RunId RunId { get; init; }

    public TaskId? TaskId { get; init; }

    public LaneId? LaneId { get; init; }

    public TurnId? TurnId { get; init; }

    public ulong Sequence { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}
```

---

# 56. Eventos mínimos

```text
SessionCreated

RunStarted
RunCompleted
RunFailed
RunCancelled

TaskCreated
TaskReady
TaskStarted
TaskCompleted
TaskFailed

LaneCreated
LaneStarted
LaneHeartbeat
LaneBlocked
LaneCompleted
LaneFailed

TurnStarted
TurnCompleted

ModelSelected
ModelStarted
ModelCompleted

ToolPlanCreated
ToolStarted
ToolCompleted
ToolFailed

PermissionRequested
PermissionGranted
PermissionDenied

ContextMaterialized
ContextPruned
ContextCompressed
ContextExternalized
ContextCompacted

ArtifactCreated

ValidationStarted
ValidationCompleted
```

Eventos agregados en v0.2:

```text
Planning (ADR-0016)
  PlanCreated · PlanRevised · PlanItemAdded · PlanItemUpdated · PlanItemStarted
  PlanItemBlocked · PlanItemUnblocked · PlanItemCompleted · PlanItemFailed
  PlanItemSkipped · PlanItemCancelled · PlanItemReordered · PlanItemLinked
  PlanItemUnlinked · PlanMutationRejected · ProgressStalled

Effect Journal (ADR-0004); reemplazan ToolStarted/ToolCompleted/ToolFailed
  ToolCallRequested · ToolCallRejected · ToolCallPrepared · ToolCallAuthorized
  ToolCallStarted · ToolCallSucceeded · ToolCallFailed
  ToolCallEffectUnknown · ToolCallReconciled

Turn
  TurnAbandoned

Artifacts (ADR-0001)
  ArtifactRedacted

Worktrees (ADR-0021)
  WorktreeCreated · IntegrationStarted · IntegrationCompleted · IntegrationConflict · WorktreeRemoved

Lanes externas (ADR-0012)
  ExternalToolObserved

Seguridad (ADR-0018)
  SecretLeakSuspected   (solo audit log)
```

---

# 57. Event Store

La historia canónica deberá poder reconstruirse desde eventos.

Inicial:

```text
InMemoryEventStore
```

Posteriormente:

```text
SQLiteEventStore
```

El historial canónico será append-only.

*v0.2 (ADR-0002):* `SqliteEventStore` desde **M1**; `InMemoryEventStore` queda solo para tests. Hay dos clases de commit:

- **Standard** (WAL + `NORMAL`);
- **Barrier** (`FULL`), obligatoria antes de cualquier efecto lateral.

Junto con el Artifact Store, el Event Store forma el Canonical Journal (ADR-0001).

---

# 58. Separación de datos

Se distinguirán cuatro conceptos.

## Transcript

Contenido conversacional.

## Event Log

Qué ocurrió.

## Audit Log

Operaciones sensibles.

## Telemetry

Métricas.

No deberán confundirse ni requerir el mismo almacenamiento.

---

# 59. Persistence

Cuando se introduzca persistencia durable se usarán inicialmente:

```text
SQLite
+
filesystem para artifacts/blobs
```

Esquema aproximado:

```text
sessions
runs
tasks
task_dependencies
lanes
turns

events

model_calls
tool_calls

context_snapshots

checkpoints

artifacts
```

El Event Store seguirá siendo la fuente histórica principal.

*v0.2:*

- **Read models:** las tablas distintas de `events`, `artifacts` y `artifact_refs` son read models reconstruibles.
- **Blobs:** son content-addressed (`blobs/sha256/…`).
- **Ubicación:** todo vive en el directorio de datos del workspace, fuera del repo: `%LOCALAPPDATA%\OmniCore\workspaces\<WorkspaceId>\` (ADR-0022 §3).
- **`.omnicore/` en el repo:** queda solo para configuración versionable del proyecto.

---

# 60. Engine Commands

Los consumidores deberán enviar Commands.

Tipos mínimos:

```text
CreateSession
StartRun
SendInput

ApprovePermission
DenyPermission

CancelRun
CancelLane

Interrupt

ResumeRun
```

*v0.3 (ADR-0024):*

- **Commands tipados:** son `WireCommand`s. El Engine **nunca** interpreta texto que empiece por `/`: un `SendInput("/x")` es texto literal.
- **Parsing:** los commands textuales los parsea el cliente, y sus kinds son `ClientCommand`, `ServerCommand` (v0.5, antes `EngineCommand`), `PromptCommand`, `WorkflowCommand` y `ExtensionCommand`.
- **Registry del Host:** expone el catálogo de commands de servidor vía `ListCommands`, con reglas de precedencia y nombres reservados.

*v0.4:*

- **`SendInput`:** pasa a ser `SendInput { InputPart[] }`, con `TextPart` y `ReferencePart` (ADR-0033).
- **Permisos:** `ApprovePermission` y `DenyPermission` se generalizan en `RespondToInteraction`.
  Las interacciones simples usan `ChoiceResponse { OptionId }` (ADR-0034).

*v0.6 — ADR-0045:*

- **Preguntas del modelo:** `user.ask` crea un `InteractionRequest` de tipo `Question` con
  `QuestionnairePrompt`; admite varias preguntas, selección única, selección múltiple, texto libre
  y `OtherInput` (`Otro`) con texto.
- **Respuesta:** `RespondToInteraction { InteractionId, InteractionResponse }` usa una unión
  `ChoiceResponse | QuestionnaireResponse`. El Host valida ids, requeridos, mínimos, máximos y
  longitud; el cliente solo presenta y captura.
- **Sin cliente:** una Question queda durable y la invocación devuelve `InputRequired` mientras el
  Run permanece activo; nunca se auto-responde con `Deny`. Permisos y operaciones de riesgo
  conservan ADR-0003.

---

# 61. Omni Protocol

Modelo:

```text
Client
   │
   │ EngineCommand
   ▼
Engine

Engine
   │
   │ EngineEvent
   ▼
Client
```

El protocolo no deberá depender de CLI.

*v0.2 (ADR-0013, ADR-0019):*

- **Mensajes wire:** son `WireEnvelope { ProtocolVersion, MessageType, payload }`, con DTOs propios en `OmniCore.Protocol`, separados de los eventos de dominio.
- **Mapeo:** `ProtocolMapper` (Host) traduce `DomainEvent → WireEvent`.
- **Negociación:** el cliente declara sus versiones con `hello` (M9).

---

# 62. Transport abstractions

```csharp
public interface IOmniTransport
{
    ...
}
```

Implementaciones previstas:

```text
InProcessTransport
StdioTransport
NamedPipeTransport
```

Primero se implementará `InProcessTransport`.

Después `StdioTransport`.

En Windows podrá añadirse `NamedPipeTransport`.

*v0.2 (ADR-0019):*

- **Flujo:** CLI → `IOmniClient` (`SendAsync`, `SubscribeAsync`, `QueryAsync`) → `IOmniTransport` → Host (`OmniServer`) → Engine.
- **In-process:** también usa DTOs wire. En debug serializa cada mensaje para detectar fugas de tipos de dominio.
- **Composición:** el código del CLI solo conoce `OmniCore.Protocol`, y `Program.cs` es el único punto de composición.

---

# 63. Backpressure

Los streams de comandos y eventos deberán tener capacidad controlada.

Se podrán usar:

```text
System.Threading.Channels
```

Políticas diferentes:

```text
Audit
→ nunca descartar

Engine state events
→ nunca descartar

Telemetry high-frequency
→ podrá agregarse/coalescerse

UI progress
→ podrá coalescerse
```

---

# 64. Omni CLI

El primer CLI será técnico.

Objetivo:

```text
laboratorio de OmniCore
```

No maximizar UX.

Comandos iniciales:

```text
/plan          (v0.2: vista lógica del Plan; ADR-0016 §12)
/context
/events
/tasks         (v0.2: vista técnica con columna PLAN)
/lanes
/tools
/model
/files
/artifacts
/stats
/permissions
/exit
```

*v0.2:* `/plan` es la vista del Plan. El cambio de modo de ejecución pasa a `/mode plan|act|orq`, y los one-shot `omni plan|act|orq "…"` (§65) se mantienen.

Posteriormente:

```text
/new
/resume
/fork

/mode plan|act|orq   (v0.2: antes /plan, /act, /orq)

/models

/compact

/agents

/skills

/diff
/undo

/usage
/doctor

/commands       (v0.3)
/keybindings    (v0.3)
/extensions     (v0.3)
/memory [session|project|global]   (v0.3)
```

*v0.3 (ADR-0025):*

- **Acciones:** los keybindings y la Command Palette resuelven a las mismas `ClientAction`s que los `ClientCommand`s; no hay dos sistemas de acciones.
- **`KeyBindingService`:** tiene `When`, `Priority` y `Source` (`User > Extension > Default`) y es configurable en `keybindings.yaml` (v0.5, ADR-0039).
- **Ámbito:** es solo del cliente y no pertenece al Core.
- **Bindings obligatorios:** `run.interrupt` y `run.cancel` siempre tienen tecla.

## 64.1 Cliente interactivo (v0.4)

**Referencia conceptual:** una experiencia parecida a OpenCode, pero más informativa y diseñada alrededor de Plan, Lanes, permisos, procedencia y uso.

- **Tecnología** (ADR-0030):
  - Terminal.Gui v2 es el dueño de la TUI;
  - Spectre.Console aporta renderables ricos y el modo plain;
  - ambos viven solo en `OmniCore.Cli`.
- **Estado:** `OmniCore.Client` contiene `ClientProjection`, un reducer puro de eventos a `ClientState`, sin frameworks visuales.
- **Degradación:** TTY interactiva → TUI; stdout redirigido o `--plain` → plain; `--json` → NDJSON de eventos del protocolo.
- **Layout** (ADR-0031):
  1. header con el working directory dominante y git compacto (`main +2 -1`);
  2. conversación + sidebar multipropósito;
  3. composer;
  4. status line como última línea física (`MODEL · EFFORT · CONTEXT` a la izquierda y `SESSION USAGE · COST · CREDITS/QUOTA` a la derecha).
- **Status line:** su `UsageSnapshot` distingue valores reportados, estimados (solo costo, marcado `≈`) y no soportados (`—`). La cuota nunca se infiere.
- **Sidebar** (ADR-0032):
  - `SidebarHost` con widgets registrables: Session, Plan (lógico, no el TaskGraph), Agents y ChangedFiles;
  - cada widget declara prioridad, acento y relevancia, y se configura con `visible = true|false|auto`, `expanded` y `priority`;
  - el transcript de una Lane nunca se vuelca en la conversación; se ve con `agent.inspect`.
- **Responsive:** modos `Stacked`/`Tabbed`/`Overlay` según breakpoints configurables.
- **Tema:** roles semánticos (Active, Success, Attention, Agent, Info, Error, Muted, Primary) con glyphs; el color nunca es la única señal.
- **Conversación** (ADR-0033):
  - bloques semánticos, no un log crudo; los de tools se actualizan en su lugar;
  - verbosidad Normal/Verbose/Trace solo en el renderer;
  - las tools declaran `ToolPresentation` como metadata.
- **Composer:**
  - texto, `/commands` y `@file|@folder|@task|@lane|@artifact|@skill`;
  - envía `SendInput { InputPart[] }`, y el Host resuelve las referencias con permisos y procedencia.
- **Human-in-the-loop** (ADR-0034):
  - llega como `InteractionRequest`, con opciones decididas por el servidor;
  - se muestra en un overlay sin destruir la vista principal, y la Lane aparece como `WAITING FOR PERMISSION` en el sidebar.
- **Acciones:** teclado, commands, palette y menús convergen en `ClientAction` (ADR-0025).
- **Fuera de alcance por ahora:** framework desktop, paleta final, animaciones, temas finales, integración con Office.

---

# 65. CLI one-shot

Deberá soportar:

```bash
omni "analiza este repositorio"
```

```bash
omni act "corrige los tests"
```

```bash
omni plan "planea la migración"
```

```bash
omni orq "corrige todos los problemas y valida"
```

---

# 66. `/context`

Deberá mostrar desglose real.

Ejemplo:

```text
Context: 22,418 / 32,768

System              2,731
Task                1,420
Skills              1,903
Conversation        5,149
Files               6,224
Tool results        2,991
Subagents             882
Working state       1,118

Generation reserve  6,350

Compressible        4,100
Externalizable      2,700
Pinned              6,054
```

*v0.3 (ADR-0029):*

- **Procedencia:** cada `ContextItem` lleva `ContextProvenance` (contributor, categoría, `ComponentSource`, refs y sensibilidad).
- **Desglose:** `/context` agrupa por categoría (Plan/WorkingState, Skills, Project Memory, Session Memory, Knowledge/RAG, Files, Tool observations, Conversation…) y muestra el origen de cada una.
- **Contenido sensible:** nunca se muestra; solo metadata.

---

# 67. `/lanes`

Ejemplo:

```text
LANE   TASK             AGENT      MODEL       STATE
41     Explore auth     explorer   local-14b   completed
42     Fix auth         coder      local-27b   running
43     Verify tests     verifier   local-14b   blocked
```

---

# 68. `/doctor`

Deberá inspeccionar al menos:

```text
model providers
tool reachability
permission configuration
artifact store
event store
workspace
git
process runtime
context configuration
```

---

# 69. Logging

Engine no escribirá logs directamente.

```text
Engine Events
      │
      ├── CLI Renderer
      ├── EventStore
      ├── DebugLogSink
      ├── AuditSink
      └── TelemetrySink
```

---

# 70. Cancellation

Toda operación async significativa deberá recibir `CancellationToken`.

Debe propagarse a:

```text
model providers
tool execution
processes
subagents
artifact operations
context operations
```

Primera interrupción CLI:

```text
cancel current generation/action
```

Segunda interrupción fuerte podrá cancelar Run.

---

# 71. Failure handling

Deben distinguirse errores como mínimo:

```text
ModelFailure
ToolFailure
PermissionDenied
PermissionTimeout
ContextOverflow
ArtifactFailure
ProcessFailure
ValidationFailure
StaleWrite
LaneStalled
ProviderUnavailable
Cancellation
InternalInvariantViolation
```

Agregados en v0.2:

```text
UnknownEffect          (ADR-0004)
ReconciliationConflict (ADR-0004)
ArtifactMissing        (ADR-0001)
ArtifactCorrupted      (ADR-0001)
RateLimited            (ADR-0011)
AuthenticationFailed   (ADR-0011)
IntegrationConflict    (ADR-0021)
PlanMutationRejected   (ADR-0016; se devuelve al modelo, no es fallo del Run)
ProgressStalled        (ADR-0016)
```

No deberán reducirse todos a `Exception`.

---

# 72. Recovery

TaskPacket podrá declarar:

```text
Retry
EscalateModel
RebuildContext
RestartLane
FailTask
FailRun
```

Ejemplo:

```text
local worker fails twice
      ↓
ModelPolicy permits escalation
      ↓
frontier model
```

---

# 73. Model escalation

La escalación será explícita y observable.

Eventos:

```text
ModelEscalationRequested
ModelEscalationApproved
ModelEscalationCompleted
```

El router deberá registrar causa:

```text
capability missing
context limit
repeated failure
uncertainty
tool reliability
manual request
```

---

# 74. Multi-agent limits

V1 utilizará límites conservadores.

Defaults iniciales sugeridos:

```text
MaxAgentDepth = 1

MaxConcurrentLanes = 3

MaxTasksPerRun = 10

MaxTurnsPerLane = configurable

MaxToolCallsPerLane = configurable
```

No habrá recursión libre de agentes.

---

# 75. Background agents

Un agente background no deberá inundar el contexto padre.

El padre recibirá normalmente:

```text
LaneCompleted
+
AgentResult
```

No polling continuo del transcript.

---

# 76. Seguridad

OmniCore deberá considerar el runtime host como la autoridad.

Una instrucción proveniente del modelo nunca podrá:

* ampliar sandbox;
* modificar límites de seguridad;
* otorgar permisos;
* ignorar Workspace boundaries;
* saltarse Completion Gates.

---

# 77. Workspace boundary

Una Task/Lane tendrá un workspace explícito.

Cualquier Path deberá normalizarse antes de autorización.

El sistema deberá proteger contra:

```text
..
symlink escape
junction escape
absolute path escape
unexpected mount
```

según las capacidades del sistema operativo.

---

# 78. Architecture projects

Estructura inicial:

```text
src/

OmniCore.Abstractions
OmniCore.Domain

OmniCore.Engine

OmniCore.Context
OmniCore.Models
OmniCore.Tools
OmniCore.Security
OmniCore.Execution

OmniCore.Infrastructure
OmniCore.Protocol

OmniCore.Host
OmniCore.Cli
```

*v0.2–v0.4:*

- **Ya existen:**
  - `OmniCore.Sandbox`, compartido con OmniCoder (ADR-0008);
  - `OmniCore.Client` (v0.4), con el estado de cliente independiente del framework visual, que depende solo de Protocol (ADR-0030). `OmniCore.Cli` depende de Client, Host y Protocol.
- **Proyectos futuros, con su frontera en ADR-0009 §2.5:**
  - `OmniCore.Qualification` (M5);
  - `OmniCore.Extensions` (M8), el host de extensiones fuera de proceso;
  - `OmniCore.Memory` (M8+), opcional y que ningún proyecto del Core referencia.

---

# 79. Responsabilidades por proyecto

## OmniCore.Abstractions

Contratos públicos mínimos.

## OmniCore.Domain

Tipos y entidades puras.

## OmniCore.Engine

Orquestación de Runs/Tasks/Lanes.

## OmniCore.Context

Materialización y gestión del contexto.

## OmniCore.Models

Providers, registry, routing.

## OmniCore.Tools

ToolCatalog/Planner/Runtime.

## OmniCore.Security

Permisos (Permission Engine, políticas, grants). *v0.5:* el sandbox vive en `OmniCore.Sandbox` y se usa desde `OmniCore.Execution` (ADR-0008, ADR-0038); Security solo lo ve a través de `IPathBoundaryValidator`.

## OmniCore.Execution

Procesos, isolation, worktrees.

## OmniCore.Infrastructure

SQLite, artifacts, logging, stores.

## OmniCore.Protocol

Commands, events, DTOs wire-safe.

## OmniCore.Host

Composition root.

## OmniCore.Cli

Cliente CLI.

---

# 80. Regla de dependencias

`OmniCore.Domain` no deberá depender de Infrastructure.

`OmniCore.Engine` no deberá depender directamente de implementations.

Los runtimes dependerán de Abstractions/Domain.

El Host compondrá todo.

Conceptualmente:

```text
               Host
                │
 ┌──────────────┼───────────────┐
 ▼              ▼               ▼
Engine        Models           Tools
 ▼              ▼               ▼
Domain      Abstractions      Security
                ▲
                │
         Infrastructure
```

La dirección exacta de proyectos deberá verificarse para evitar ciclos.

---

# 81. Composition root

`Program.cs` deberá permanecer mínimo.

Ejemplo conceptual:

```csharp
await OmniHost
    .Create(args)
    .RunAsync();
```

Configuración/registro deberá encapsularse.

*v0.2 (ADR-0019):* el patrón del skeleton, `OmniHost.RunAsync(CliClient.RunAsync)`, es temporal y se elimina en M1. Forma final:

```csharp
await using var client = OmniHost.CreateInProcessClient(options);   // o StdioOmniClient.Connect(...)
return await CliApp.RunAsync(client, args);
```

---

# 82. Testing strategy

OmniCore deberá priorizar pruebas deterministas sobre pruebas dependientes de LLM.

Áreas obligatorias:

| Área         | Prueba                              |
| ------------ | ----------------------------------- |
| Task Graph   | dependencies                        |
| Lane         | state transitions                   |
| Permissions  | no escalation                       |
| Sandbox      | path escape                         |
| Tools        | schema validation                   |
| Context      | deterministic budget                |
| Context      | canonical history untouched         |
| Files        | stale writes                        |
| Artifact     | externalization                     |
| Hooks        | no permission broadening            |
| Completion   | failing criterion blocks completion |
| Protocol     | sequence correctness                |
| Cancellation | propagates                          |
| Router       | capability constraints              |
| Scheduler    | concurrency limits                  |
| Plan (v0.2)  | reconciliación Plan ↔ Task determinista (R1–R7), rechazo de mutaciones inválidas, revisiones reconstruibles |
| PlanCompletionGate (v0.2) | item requerido no terminal bloquea DONE |
| Watchdog (v0.2) | `ProgressStalled` tras N Turns sin señal |
| Effect Journal (v0.2) | crash entre `Started` y outcome → reconciliación sin duplicar efecto |
| Tool pipeline (v0.2) | `Prepare` sin I/O; `AuthorizedToolIntent` solo desde Security |
| Journal (v0.2) | artifact referenciado no se recolecta; corrupción detectada |
| Protocol (v0.2) | upcasters; mapper dominio → wire |
| Secrets (v0.2) | un secreto de prueba no aparece en ningún sink |
| Profile (v0.2) | cada campo de `HarnessPolicy` cambia el comportamiento |

---

# 83. Fake providers

Se implementarán providers deterministas para tests.

Ejemplo:

```text
FakeModelProvider
ScriptedModelProvider
FakeTool
FakeProcessRuntime
FakeArtifactStore
```

Esto permitirá probar el Agent Runtime sin GPU ni red.

---

# 84. Regression suite agentic

Además de unit tests deberá existir una batería fija de tareas.

Categorías:

```text
repository exploration

simple bug fix

multi-file bug fix

build failure

test failure

refactor

context-heavy task

delegated task

permission denial

model failure

resume after interruption
```

---

# 85. Métricas de evaluación

Por ejecución:

```text
success/failure

wall time

number of Turns

tool calls

prompt tokens

output tokens

peak context

context compactions

artifacts externalized

model escalations

frontier usage

cost when known
```

---

# 86. Milestone M1 — Runtime sin IA + Planning

> **v0.5: fuente de verdad del roadmap = `docs/architecture/arquitectura.md` §24.** Las secciones §86–§95 resumen cada milestone; ante cualquier diferencia, prevalece §24.

- **Dominio:** Session, Run, Plan, Task, Lane, Turn y ToolCall, con máquinas de estado y un evento por transición (ADR-0036). La conversación se relaciona con el Run según ADR-0035.
- **Journal:** `SqliteEventStore` + artifact store mínimo, ubicados por `WorkspaceId` en las rutas de plataforma (ADR-0038, ADR-0039).
- **Runtime del plan:** `PlanService`, `ProgressReconciler`, watchdog, Completion Pipelines de Lane y de Run, y la proyección `WorkingState`.
- **Permisos y contratos:** tipos de permisos + `ScriptedPermissionPolicy` (ADR-0037 §9); contratos congelados (arquitectura §22).
- **Cliente:** `OmniCore.Client` con `ClientProjection`, plain renderer interactivo, JSON renderer y `LocalizedText` con recursos `es`/`en`.
- **Simulación:** `omni sim` con escenarios YAML y la *golden rule* de reconstrucción desde el journal (ADR-0041).

**Criterio de salida:** los escenarios de `omni sim` (plan de varios items, TaskGraph N:M, crash y resume) se reconstruyen idénticos desde el journal, en Windows y en Linux.

---

# 87. Milestone M2 — Explorer

- **Modelo local:** `OpenAiChatCompatibleProvider` (ik_llama) y `LocalModelHost` en modo attach y managed, con un `IProcessRuntime` mínimo.
- **Configuración:** registro mínimo de modelos, configuración YAML, Scope Resolver de configuración y confianza de workspace.
- **Permisos:** Permission Engine v1 con los defaults autónomos, grants, `/permissions` y topes de gasto.
- **Contexto:** Context Engine v1 con `WorkingState`, conversación de la sesión y la política provisional de ADR-0042; `ContextSnapshot` + fingerprint.
- **Tools:** tools de lectura y `reference.resolve`.
- **Planificación:** `plan.propose` y `PlanApproval`.

**Criterio de salida:** `omni "explícame este repositorio"` funciona con el plain renderer interactivo.

---

# 88. Milestone M3 — Native Coder

- **Escritura y política de modelo:** `write` y `patch` con version tokens, `ModelPolicyKey`,
  `ModelToolPolicy`, `FileMutationPolicy` y `ModelCapabilityBoundary` (ADR-0044).
- **Preguntas humanas:** DTOs tipados, schema/upcaster, tool `user.ask`, validación del Host,
  persistencia/resume y formulario plain TTY para cuestionarios (ADR-0045).
- **Efectos:** Effect Journal con reconciliación de filesystem y commits Barrier.
- **Sandbox:** `Strong` por plataforma + `WeakSandboxConsent`.
- **Procesos:** `process.exec`, build y test (con red), `shell.exec`.
- **Gates:** Build, Test y Acceptance.

**Criterio de salida:** `omni act "corrige este test"`; un crash durante un write se reanuda sin
duplicar el efecto; un cuestionario se resuelve exactamente una vez y sobrevive a restart; sin TTY
devuelve `InputRequired`.

---

# 89. Milestone M4 — Context management completo (+ track TUI v0)

- **Contexto:** Prune, Externalize, Compress, Compact, `ContextCheckpoint` y MetaModelService.
- **Journal y auditoría:** GC, `verify-journal` y retención de auditoría.
- **TUI v0** (decisión del usuario: después de M3): se desarrolla en paralelo sobre
  `ClientProjection` e incluye 4 zonas, sidebar (sesión, plan, archivos), overlays, responsive,
  `ModelPolicySetup`, `Preferences > Models`, `QuestionnaireOverlay` con radio/checkbox/texto/`Otro`
  y el spike de `SpectreSegmentAdapter`.

---

# 90. Milestone M5 — Models + Qualification

- **Providers:** `OpenAIResponsesProvider` (perfiles `api` y `codex`, con login ChatGPT) y `AnthropicMessagesProvider`.
- **Ruteo:** registro completo con alias, router y escalación.
- **Cualificación:** suite `quick`, estados y store de perfiles.
- **Uso:** costo y cuota reales en la status line.

---

# 91. Milestone M6 — Multi-agent

- **Ejecución:** descomposición, scheduler y lanes background. **Solo las lanes de lectura corren en paralelo**; las que escriben se serializan con un lease de escritura hasta M7.
- **Resultados y monitoreo:** agregación de `AgentResult` y heartbeat agregado.
- **Otros:** `WorkflowCommand` y el widget de agentes.

---

# 92. Milestone M7 — Isolation

- **Worktrees:** worktrees con `SnapshotOfWorkingTree`, integración 3-way y reconciliación Git.
- **Paralelismo:** se habilitan las lanes escritoras en paralelo.
- **Claude Code:** lanes delegadas a Claude Code (ADR-0012 rev. 3, riesgo residual aceptado).

---

# 93. Milestone M8 — Extensibility

- **Extensiones:** host de extensiones con manifest YAML, `ExtensionBoundary` y MCP.
- **Componentes:** hooks, ciclo de vida de skills, precedencia de commands y `toolPreferences`.
- **Memoria:** Session Memory.

---

# 94. Milestone M9 — Protocol + Host

- **Protocolo:** stdio y negociación de versión.
- **Host:** Host separado, lease entre procesos y read models persistidos.
- **OmniCoder:** vía de integración, con Protocol y Client en net8.

---

# 95. Milestone M10 — CLI v1

- **Memoria:** Project, Workspace y Global, con promoción.
- **CLI:** keybindings configurables y Command Palette.
- **Cualificación:** suite `full` y calibración.
- **Evaluación:** regression suite, comparación con Pi y adapter de OmniCoder.

---

# 96. Madurez requerida antes de integrar OmniCoder

No se reemplazará Pi sólo porque exista un Agent Loop funcional.

OmniCore deberá demostrar:

## Reliability

* cancelación fiable;
* ausencia de deadlocks conocidos;
* recuperación razonable;
* EventStore consistente.

## Security

* sandbox probado;
* protección contra path escape;
* ausencia de privilege escalation conocida.

## Agent behavior

* Explorer estable;
* Coder estable;
* build/test estable;
* Completion Gates fiables.

## Context

* sin overflow sistemático;
* compaction no destructivo;
* externalización estable.

## Multi-agent

* fallo de una Lane no destruye necesariamente el Run;
* límites de concurrencia respetados.

## Observability

* cada Turn puede inspeccionarse;
* contexto utilizado identificable;
* tools utilizadas identificables;
* modelo utilizado identificable.

---

# 97. Comparación contra Pi

Antes de integración se ejecutará:

```text
OmniCoder + Pi
       vs
Omni CLI + OmniCore
```

sobre la misma regression suite.

La comparación considerará:

```text
success rate
time
Turns
tool calls
tokens
context growth
frontier escalations
cost
```

---

# 98. Integración futura con OmniCoder

OmniCoder deberá depender de una frontera pequeña.

*v0.5 (ADR-0019, ADR-0038 §6):*

- **`IAgentRuntime`:** es una interfaz **de OmniCoder**.
- **Adapter:** `OmniCoreRuntimeAdapter` se implementa sobre `IOmniClient` y consume **`WireEvent`s**, no eventos de dominio.
- **Referencias:** OmniCoder (net8) referencia `OmniCore.Protocol` y `OmniCore.Client`, que compilan también para net8.
- **Transporte:** habla con OmniCore por stdio (M9).

Forma original, conservada como referencia:

```csharp
public interface IAgentRuntime
{
    IAsyncEnumerable<WireEvent> ExecuteAsync(      // v0.5: WireEvent (protocolo), no EngineEvent
        RunRequest request,
        CancellationToken cancellationToken);
}
```

Temporalmente podrán coexistir:

```text
IAgentRuntime
   ├── PiRuntimeAdapter
   └── OmniCoreRuntimeAdapter
```

La migración podrá hacerse por modos:

```text
PLAN
→ OmniCore

ACT read-only
→ OmniCore

ACT write
→ OmniCore

ORQ
→ OmniCore
```

Finalmente se retirará Pi.

---

# 99. Epics de implementación

> **v0.5:** los epics se derivan del roadmap de `docs/architecture/arquitectura.md` §24, que es la fuente de verdad. Esta lista es el desglose de trabajo.

```text
M1
EPIC-001 Domain primitives, ids, identidades de workspace/proyecto y rutas de plataforma
EPIC-002 Event model (envelope, EventType, upcasters) + SqliteEventStore + artifact store mínimo
EPIC-003 Máquinas de estado (Run, Task, Lane, PlanItem, ToolCall, Turn) — ADR-0036
EPIC-004 Conversación ↔ Run, modos y cancelación — ADR-0035
EPIC-005 TaskGraph + Plan + PlanService
EPIC-006 ProgressReconciler + watchdog
EPIC-007 Completion Pipelines (Lane, Run)
EPIC-008 Tipos de permisos + ScriptedPermissionPolicy + AuditSink básico
EPIC-009 Contratos congelados (tools, modelo, perfil, fingerprint, secretos, tokens, interacciones)
EPIC-010 OmniCore.Client: ClientProjection, plain/JSON renderers, LocalizedText es/en
EPIC-011 omni sim + escenarios + golden rule

M2
EPIC-012 OpenAiChatCompatibleProvider (ik_llama) + LocalModelHost + IProcessRuntime mínimo
EPIC-013 Configuración YAML + Scope Resolver (config) + confianza de workspace + registro mínimo de modelos
EPIC-014 SecretProvider + ICredentialStore por plataforma + redacción completa
EPIC-015 Permission Engine v1 (defaults autónomos, grants, revocación, topes de gasto)
EPIC-016 EffectiveModelProfile + HarnessPolicy + ITokenCounter reales
EPIC-017 Context Engine v1 + WorkingState + conversación de sesión + política provisional
EPIC-018 Tool pipeline + read tools + reference.resolve + IPathBoundaryValidator por plataforma
EPIC-019 Artifact Store CAS v1
EPIC-020 Explorer + plan.propose + PlanApproval

M3
EPIC-021 File write/patch + version tokens + ModelPolicyKey/store/onboarding CLI + ModelToolPolicy/FileMutationPolicy/ModelCapabilityBoundary
EPIC-021A user.ask + cuestionarios tipados + validación Host + artifacts/resume + plain TTY
EPIC-022 Effect Journal + reconciliación FS + commits Barrier
EPIC-023 Sandbox Strong (Windows, Linux) + WeakSandboxConsent + process.exec/shell.exec
EPIC-024 Native Coder + gates Build/Test/Acceptance

M4 (+ track TUI v0)
EPIC-025 Prune · Compress · Externalize · Compact · MetaModelService · GC · retención de auditoría
EPIC-026 TUI v0 (Terminal.Gui) + SpectreSegmentAdapter + ModelPolicySetup + Preferences > Models + QuestionnaireOverlay

M5
EPIC-027 OpenAIResponsesProvider (api + codex/ChatGPT) + AnthropicMessagesProvider
EPIC-028 Model registry + Router + escalación + costo/cuota
EPIC-029 Model Qualification Framework + suite quick + FileMutationReliability y recomendación de categoría

M6
EPIC-030 Decomposition + Lane Scheduler + background lanes + AgentResult agregado

M7
EPIC-031 Worktree Isolation + integración + lanes escritoras paralelas
EPIC-032 Lanes delegadas a Claude Code

M8
EPIC-033 Extension host + MCP + Hooks + Skills + Commands (precedencia) + Session Memory

M9
EPIC-034 Omni Protocol stdio + Host Process + integración OmniCoder

M10
EPIC-035 Memoria Project/Workspace/Global + CLI v1 (palette, keybindings)
EPIC-036 Regression Suite + qualification full + calibración
EPIC-037 Pi Comparison + OmniCoder Adapter
```

---

# 100. Primer vertical slice

La primera meta realmente útil será:

```text
User
 ↓
omni
 ↓
Create Session
 ↓
Create Run
 ↓
Create Plan rev.1          (v0.2)
 ↓
Create Task  ↔ PlanItem    (v0.2)
 ↓
Create Lane
 ↓
Materialize Context (+ WorkingState, ExecutionFingerprint)
 ↓
Select local model
 ↓
Build ToolPlan
 ↓
Explorer
 ↓
read/search
 ↓
AgentResult
 ↓
ProgressReconciler → PlanItem Completed   (v0.2)
 ↓
Completion Gate (incluye PlanCompletionGate)
 ↓
RunCompleted
```

Prompt de prueba:

```bash
omni "analiza este repositorio y dime dónde se implementa la autenticación"
```

Este vertical slice deberá incluir ya:

```text
events
permissions
context snapshots
tool results
artifacts básicos
model abstraction
```

aunque sus implementaciones iniciales sean sencillas.

---

# 101. Segundo vertical slice

```bash
omni act "corrige el test que está fallando"
```

Flujo:

```text
Explore
 ↓
Read
 ↓
Patch
 ↓
Build
 ↓
Test
 ↓
Completion Gates
 ↓
AgentResult
```

Éste demostrará que OmniCore ya es un coding agent funcional sin Pi.

---

# 102. Tercer vertical slice

```bash
omni orq "corrige los fallos de autenticación y valida todo"
```

Flujo esperado:

```text
Run
 │
 └── TaskGraph
      │
      ├── Explore auth
      │      ↓
      │    Lane
      │
      ├── Implement
      │      ↓
      │    isolated Lane
      │
      └── Verify
             ↓
           Lane
```

Con posible routing:

```text
Explorer
→ modelo local pequeño/medio

Coder
→ worker local

Escalation
→ frontier

Verifier
→ local
```

Éste será el primer momento en que OmniCore represente plenamente el concepto arquitectónico buscado.

---

# 103. Definition of Done de OmniCore v1

OmniCore v1 se considerará terminado cuando:

1. el Engine opere independientemente de Pi y OmniCoder;
2. CLI permita ejecutar PLAN, ACT y ORQ;
3. Tasks y Lanes sean la única ruta para subagentes;
4. contexto sea presupuestado por modelo;
5. tools sean seleccionadas dinámicamente;
6. outputs grandes sean externalizables;
7. permisos sean deterministas;
8. filesystem tenga protección contra stale writes;
9. worktrees permitan aislamiento;
10. Completion Gates validen finalización;
11. local y frontier models sean intercambiables;
12. EventStore permita inspección completa;
13. sessions puedan persistirse/reanudarse;
14. regression suite sea ejecutable;
15. OmniCore pueda compararse objetivamente contra Pi;
16. (v0.2) todo Run tenga un Plan canónico mantenido por el runtime y reconciliado con el TaskGraph;
17. (v0.2) un crash durante un efecto lateral se reconcilie sin duplicarlo;
18. (v0.2) los modelos nuevos puedan cualificarse empíricamente sin modificar Router ni runtime;
19. (v0.2) ningún secreto aparezca en journal, artifacts, contexto ni logs;
20. (v0.6) el modelo pueda solicitar cuestionarios estructurados con selección única, múltiple,
    texto libre y `Otro`, y la interacción sobreviva a resume sin duplicar ni inventar respuestas.

---

# 104. Principio final

OmniCore no deberá diseñarse como un clon de Pi, Claude Code, Codex, Claw o Claurst.

Su unidad conceptual será:

```text
Task
 +
Lane
 +
ContextBudget
 +
ToolPlan
 +
ModelDescriptor
 +
PermissionBoundary
```

Su objetivo diferencial será operar correctamente tanto con modelos locales limitados como con modelos frontera sin cambiar el modelo arquitectónico.

El mismo Task deberá poder ejecutarse mediante:

```text
4B
14B
27B
frontier-fast
frontier-strong
```

cambiando únicamente la política y las capacidades efectivas.

Ese principio debe guiar todas las decisiones de implementación posteriores.
