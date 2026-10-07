# A. Capability Transition Matrix

Fecha: 2026-10-01. TO-BE autoritativo: [arquitectura vigente](../arquitectura.md) y ADRs aprobadas. Esta matriz complementa implementación; **no rediseña contratos ni sustituye código nativo existente**.

Fuentes, commits, límites de evidencia, comparación C#/TS y clasificación de tests: [evidencia](evidencia.md). Roadmap anotado: [B](roadmap-anotado.md). Retiro/autoridad: [C](retirement-map.md).

Disposición principal por unidad: ADOPT, ADAPT, REIMPLEMENT, RETAIN TEMPORARILY, RETIRE, KEEP IN OMNICODER o UNKNOWN. Una decisión REIMPLEMENT usa comportamiento/tests y preserva implementación nativa de OmniCore cuando ya existe: **no ordena reescribirla**. Best donor selecciona activos, no clases enteras. La prueba de paridad debe ejecutarse; ningún test listado acredita resultado verde en esta revisión.

**Blocker Yes** significa garantía/contrato pre-M6 vinculante o corte de autoridad imprescindible para esa transición; no significa que la falta de extracción del donante deba bloquear desarrollo nativo. UNKNOWN bloquea aprobar reutilización/retiro de esa pieza, no obliga a cambiar milestones.

## Índice

| Capability | Donante | Disposición | Milestone |
|---|---|---|---|
| [Session](#session) | Both | ADAPT | M1 / M9 |
| [Run](#run) | Both | REIMPLEMENT | M1 |
| [Plan / PlanItem](#plan-planitem) | Both | REIMPLEMENT | M1 |
| [TaskGraph](#taskgraph) | Both | ADAPT | M1 / M6 |
| [Task](#task) | Both | REIMPLEMENT | M1 / M6 |
| [Lane](#lane) | Both | REIMPLEMENT | M1 / M6 |
| [AgentProfile](#agentprofile) | Both | ADAPT | M6 |
| [AgentExecution](#agentexecution) | Both | REIMPLEMENT | PRE-M6 MIGRATION / M6 |
| [Turn](#turn) | Both | REIMPLEMENT | M2 / PRE-M6 MIGRATION |
| [ModelStep](#modelstep) | None | UNKNOWN | PRE-M6 MIGRATION |
| [ToolStep](#toolstep) | C# | ADAPT | M2–M3 / PRE-M6 MIGRATION |
| [Steering](#steering) | Both | ADAPT | PRE-M6 MIGRATION |
| [FollowUp](#followup) | Both | ADAPT | PRE-M6 MIGRATION |
| [InteractionRequest](#interactionrequest) | Both | ADAPT | M1 / M3 / PRE-M6 MIGRATION |
| [ModelDescriptor](#modeldescriptor) | Both | ADAPT | M2 / M5 |
| [ProviderDescriptor](#providerdescriptor) | Both | ADAPT | M2 / M5 |
| [ModelRouteDescriptor](#modelroutedescriptor) | Both | REIMPLEMENT | PRE-M6 MIGRATION |
| [EffectiveModelProfile](#effectivemodelprofile) | Both | ADAPT | M2 / M5 / PRE-M6 MIGRATION |
| [Model Qualification](#model-qualification) | Both | REIMPLEMENT | M5 |
| [SessionRoutingPolicy](#sessionroutingpolicy) | C# | ADAPT | PRE-M6 MIGRATION |
| [Context Engine](#context-engine) | Both | ADAPT | M2 / M4 |
| [Context inheritance](#context-inheritance) | Both | REIMPLEMENT | M6 |
| [Context pressure](#context-pressure) | C# | ADAPT | M4 / PRE-M6 MIGRATION |
| [Compaction](#compaction) | Both | REIMPLEMENT | M4 |
| [Tool Runtime](#tool-runtime) | C# | REIMPLEMENT | M2 / M3 |
| [Tool Registry](#tool-registry) | None | UNKNOWN | M2 / M8 |
| [Prepare / Permission / Execute](#prepare-permission-execute) | C# | ADAPT | M2 / M3 / PRE-M6 MIGRATION |
| [Process execution](#process-execution) | Both | ADAPT | M2 / M3 |
| [Shell](#shell) | C# | REIMPLEMENT | M3 |
| [PTY](#pty) | None | UNKNOWN | M3 / M9 |
| [Permission Engine](#permission-engine) | Both | REIMPLEMENT | M2 / M3 |
| [Isolation](#isolation) | C# | ADAPT | M3 / M7 |
| [AppContainer](#appcontainer) | C# | ADAPT | M3 |
| [Workspace isolation](#workspace-isolation) | Both | ADAPT | M7 |
| [Git worktrees](#git-worktrees) | Both | ADAPT | M7 |
| [WorkspaceSnapshotStore](#workspacesnapshotstore) | Both | ADAPT | M7 |
| [Undo](#undo) | Both | ADAPT | M7 / M10 |
| [Effect Journal](#effect-journal) | Both | ADAPT | M3 / PRE-M6 MIGRATION / M7 |
| [Canonical Journal](#canonical-journal) | C# | ADAPT | M1 / M4 / PRE-M6 MIGRATION |
| [Telemetry](#telemetry) | TS | ADAPT | M4 / PRE-M6 MIGRATION |
| [EngineEvent envelope](#engineevent-envelope) | C# | REIMPLEMENT | PRE-M6 MIGRATION |
| [Causality](#causality) | C# | REIMPLEMENT | PRE-M6 MIGRATION |
| [Sequence](#sequence) | C# | ADAPT | M1 / PRE-M6 MIGRATION / M9 |
| [ArtifactStore](#artifactstore) | Both | ADAPT | M2 / M4 |
| [Delegation](#delegation) | C# | ADAPT | M6 |
| [Escalation](#escalation) | Both | ADAPT | M5 / PRE-M6 MIGRATION / M6 |
| [WakeRequest](#wakerequest) | C# | ADAPT | M6 |
| [Awaited / Detached](#awaited-detached) | None | UNKNOWN | PRE-M6 MIGRATION / M6 |
| [Managed / Unmanaged](#managed-unmanaged) | None | UNKNOWN | PRE-M6 MIGRATION / M6 |
| [SupervisionBinding](#supervisionbinding) | None | UNKNOWN | PRE-M6 MIGRATION / M6 |
| [ExecutionMailbox](#executionmailbox) | C# | ADAPT | M6 |
| [WakePolicy](#wakepolicy) | C# | REIMPLEMENT | M6 |
| [RuntimeCommand](#runtimecommand) | Both | REIMPLEMENT | PRE-M6 MIGRATION / M9 |
| [CommandOutcome](#commandoutcome) | Both | REIMPLEMENT | PRE-M6 MIGRATION |
| [FanOut](#fanout) | Both | REIMPLEMENT | M6 |
| [FanIn](#fanin) | Both | REIMPLEMENT | M6 |
| [ExecutionJoin](#executionjoin) | None | UNKNOWN | M6 |
| [AgentResult](#agentresult) | Both | ADAPT | PRE-M6 MIGRATION / M6 |
| [ResultDisposition](#resultdisposition) | Both | REIMPLEMENT | PRE-M6 MIGRATION / M6 |
| [Hierarchical budgets](#hierarchical-budgets) | C# | ADAPT | M6 |
| [RunBudgetPool](#runbudgetpool) | C# | ADAPT | M6 |
| [CapacityScheduler](#capacityscheduler) | Both | REIMPLEMENT | M6 |
| [ImmediateValidation](#immediatevalidation) | C# | ADAPT | M6/M7 |
| [CompletionValidation](#completionvalidation) | Both | ADAPT | M3 / M6/M7 |
| [IntegrationEvidence](#integrationevidence) | Both | ADAPT | M6/M7 |
| [WiringEvidence](#wiringevidence) | None | UNKNOWN | M6/M7 |
| [ChangeRiskAssessment](#changeriskassessment) | None | UNKNOWN | M7+ |
| [ValidationDebt](#validationdebt) | None | UNKNOWN | M6/M7 |
| [Completion Gates](#completion-gates) | Both | ADAPT | M1 / M3 / M6/M7 |
| [Skills](#skills) | Both | ADAPT | M8 |
| [Extensions](#extensions) | None | UNKNOWN | M8 |
| [MCP](#mcp) | Both | ADAPT | M8 |
| [Commands](#commands) | Both | ADAPT | M8 / M9 |
| [Memory](#memory) | Both | REIMPLEMENT | M8 / M10 |
| [Knowledge](#knowledge) | Both | ADAPT | M8 / M10 |
| [RAG](#rag) | None | UNKNOWN | M8 / M10 |
| [Protocol](#protocol) | Both | ADAPT | M9 |
| [Host](#host) | Both | REIMPLEMENT | M9 |
| [CLI Lab](#cli-lab) | Both | ADAPT | M1–M5 / M10 |
| [Observability projections](#observability-projections) | TS | ADAPT | M4 / M9 / M10 |
| [Modes / UltraCode](#modes-ultracode) | TS | REIMPLEMENT | PRE-M6 MIGRATION / M6 |
| [Framer (algoritmo)](#framer-algoritmo) | C# | ADOPT | M9 |
| [Diff primitives](#diff-primitives) | None | UNKNOWN | M3 / M7 |
| [Coalescers](#coalescers) | TS | KEEP IN OMNICODER | M4 / M9 |
| [WPF product](#wpf-product) | TS | RETAIN TEMPORARILY | M9 / M10 |
| [Electron product](#electron-product) | TS | KEEP IN OMNICODER | M9 / M10 |

<a id="session"></a>

## Session

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0035; sesión canónica, 1 Run activo |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Orchestration/Execution/PiRpcAgentRuntime.cs`; `src/OmniCoder.Core/Orchestration/Execution/IAgentRuntime.cs` ([evidencia](evidencia.md#pi)). |
| TS source | `packages/core/src/rpc/pi-rpc-client.ts`; `packages/core/src/hosting/agent-runtime.ts`; `packages/core/src/kernel/kernel.ts`; `packages/core/src/kernel/pi-kernel.ts` ([evidencia](evidencia.md#pi)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | ACK correlacionado, cancelación, mensajes sin pérdida, error explícito y respuesta a UI pendiente. |
| Replace | Dependencia Pi en contratos finales, settled como prueba de éxito y transcript padre como contexto heredado. |
| Required migration | Encapsular en adapter de compatibilidad del producto; reconstruir estado nativo con Turn/ModelStep e InteractionRequest, sin segunda autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/PiRpcToolGatingTests.cs`; PORT TS: `packages/core/test/pi-rpc-e2e.test.ts`, `packages/core/test/kernel.test.ts`, `packages/core/test/runtime.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Session. |
| Dependencies | ADR-0005/0014/0019/0034/0046; spec §96–98. |
| Milestone | M1 / M9 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Catálogo/JSONL Pi es fuente de importación, no nueva SessionStore. Migración historial M9 con procedencia Partial/Imported. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="run"></a>

## Run

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0035/0036; conversación hasta Completion Gates |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationStateMachine.cs`; `src/OmniCoder.Core/Orchestration/OrchestrationCoordinator.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs` ([evidencia](evidencia.md#life)). |
| TS source | `packages/core/src/orchestration/state-machine.ts`; `packages/core/src/goals/goal-scheduler.ts` ([evidencia](evidencia.md#life)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Rechazo de transiciones ilegales, dependencias no liberadas prematuramente y separación producir/validar/aceptar/promover. |
| Replace | Identidades string, estados de orquestación/Goal y plan obligatorio; no equivalen al lifecycle de OmniCore. |
| Required migration | Usar fixtures de comportamiento contra las state machines y PlanService existentes; no sustituir el dominio ya construido. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`, `tests/OmniCoder.Core.Tests/OrchestrationCoordinatorTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Run. |
| Dependencies | Domain/Engine, ADR-0016/0035/0036/0046/0047. |
| Milestone | M1 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="plan-planitem"></a>

## Plan / PlanItem

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0016; reconciliación canónica, Plan distinto de TaskGraph |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationStateMachine.cs`; `src/OmniCoder.Core/Orchestration/OrchestrationCoordinator.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs` ([evidencia](evidencia.md#life)). |
| TS source | `packages/core/src/orchestration/state-machine.ts`; `packages/core/src/goals/goal-scheduler.ts` ([evidencia](evidencia.md#life)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Rechazo de transiciones ilegales, dependencias no liberadas prematuramente y separación producir/validar/aceptar/promover. |
| Replace | Identidades string, estados de orquestación/Goal y plan obligatorio; no equivalen al lifecycle de OmniCore. |
| Required migration | Usar fixtures de comportamiento contra las state machines y PlanService existentes; no sustituir el dominio ya construido. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`, `tests/OmniCoder.Core.Tests/OrchestrationCoordinatorTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Plan / PlanItem. |
| Dependencies | Domain/Engine, ADR-0016/0035/0036/0046/0047. |
| Milestone | M1 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | No trasladar Goal ni exigir un plan visible a toda consulta (ADR-0047). Implementation reuse y behavioral reuse se verifican por separado. |

<a id="taskgraph"></a>

## TaskGraph

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0016; grafo N:M validado |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationPlanValidator.cs` ([evidencia](evidencia.md#dag)). |
| TS source | `packages/core/src/orchestration/plan-validator.ts`; `packages/core/src/goals/goal-contracts.ts` ([evidencia](evidencia.md#dag)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | DFS visiting/visited, detección de ciclos, referencias inexistentes y ids duplicados. |
| Replace | Plan/Task 1:1 legado, reglas de billing/modelos y paths embebidas en el validador. |
| Required migration | Extraer solamente validación pura del grafo hacia el contrato N:M; conservar PlanService y añadir paridad de ciclos. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationPlanValidatorTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico TaskGraph. |
| Dependencies | ADR-0016/0036; TaskGraph. |
| Milestone | M1 / M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Medium |
| Notes | ADOPT solo algoritmo DFS; ADAPT tests y referencias. No copiar todo PlanValidator. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="task"></a>

## Task

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0036; Task con dependencias y gates |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationStateMachine.cs`; `src/OmniCoder.Core/Orchestration/OrchestrationCoordinator.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs` ([evidencia](evidencia.md#life)). |
| TS source | `packages/core/src/orchestration/state-machine.ts`; `packages/core/src/goals/goal-scheduler.ts` ([evidencia](evidencia.md#life)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Rechazo de transiciones ilegales, dependencias no liberadas prematuramente y separación producir/validar/aceptar/promover. |
| Replace | Identidades string, estados de orquestación/Goal y plan obligatorio; no equivalen al lifecycle de OmniCore. |
| Required migration | Usar fixtures de comportamiento contra las state machines y PlanService existentes; no sustituir el dominio ya construido. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`, `tests/OmniCoder.Core.Tests/OrchestrationCoordinatorTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Task. |
| Dependencies | Domain/Engine, ADR-0016/0035/0036/0046/0047. |
| Milestone | M1 / M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="lane"></a>

## Lane

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0036; ejecución concreta de Task |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationStateMachine.cs`; `src/OmniCoder.Core/Orchestration/OrchestrationCoordinator.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs` ([evidencia](evidencia.md#life)). |
| TS source | `packages/core/src/orchestration/state-machine.ts`; `packages/core/src/goals/goal-scheduler.ts` ([evidencia](evidencia.md#life)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Rechazo de transiciones ilegales, dependencias no liberadas prematuramente y separación producir/validar/aceptar/promover. |
| Replace | Identidades string, estados de orquestación/Goal y plan obligatorio; no equivalen al lifecycle de OmniCore. |
| Required migration | Usar fixtures de comportamiento contra las state machines y PlanService existentes; no sustituir el dominio ya construido. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`, `tests/OmniCoder.Core.Tests/OrchestrationCoordinatorTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Lane. |
| Dependencies | Domain/Engine, ADR-0016/0035/0036/0046/0047. |
| Milestone | M1 / M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | OrchestrationTaskState no demuestra Lane separada; reutilización conductual, no implementación. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="agentprofile"></a>

## AgentProfile

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; configuración, no ejecución viva |
| C# source | `src/OmniCoder.Core/Orchestration/Model/RoleModelRouter.cs`; `src/OmniCoder.Core/Orchestration/Model/ModelRouting.cs` ([evidencia](evidencia.md#model)). |
| TS source | `packages/core/src/models/router.ts`; `packages/core/src/providers/model-effort-capabilities.ts`; `packages/core/src/providers/provider-probe.ts` ([evidencia](evidencia.md#model)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Mapeo explícito por rol y clases de coste observables; capacidades separadas de UI. |
| Replace | Presets por substring/nombre, scores declarados como qualification y free como permiso para cualquier ruta. |
| Required migration | Portar casos al Registry/ModelProfileResolver/Qualification existentes; resolver ModelRoute y SessionRoutingPolicy sin cambiar permisos por recomendación. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/RoleModelRouterTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ModelRoutingTests.cs`; PORT TS: `packages/core/test/router.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico AgentProfile. |
| Dependencies | ADR-0005/0007/0011/0044/0046. |
| Milestone | M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Traducir perfil a capabilities/techo; ids y roles del producto no definen el dominio. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="agentexecution"></a>

## AgentExecution

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; identidad, lineage y lifecycle propios |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationStateMachine.cs`; `src/OmniCoder.Core/Orchestration/OrchestrationCoordinator.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs` ([evidencia](evidencia.md#life)). |
| TS source | `packages/core/src/orchestration/state-machine.ts`; `packages/core/src/goals/goal-scheduler.ts` ([evidencia](evidencia.md#life)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Rechazo de transiciones ilegales, dependencias no liberadas prematuramente y separación producir/validar/aceptar/promover. |
| Replace | Identidades string, estados de orquestación/Goal y plan obligatorio; no equivalen al lifecycle de OmniCore. |
| Required migration | Usar fixtures de comportamiento contra las state machines y PlanService existentes; no sustituir el dominio ya construido. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`, `tests/OmniCoder.Core.Tests/OrchestrationCoordinatorTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico AgentExecution. |
| Dependencies | Domain/Engine, ADR-0016/0035/0036/0046/0047. |
| Milestone | PRE-M6 MIGRATION / M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Coordinator/AgentRuntime son referencias parciales; no hay paridad de ParentExecutionId+binding demostrada. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="turn"></a>

## Turn

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046 §1; intención lógica con 1..N ModelStep |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Orchestration/Execution/PiRpcAgentRuntime.cs`; `src/OmniCoder.Core/Orchestration/Execution/IAgentRuntime.cs` ([evidencia](evidencia.md#pi)). |
| TS source | `packages/core/src/rpc/pi-rpc-client.ts`; `packages/core/src/hosting/agent-runtime.ts`; `packages/core/src/kernel/kernel.ts`; `packages/core/src/kernel/pi-kernel.ts` ([evidencia](evidencia.md#pi)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | ACK correlacionado, cancelación, mensajes sin pérdida, error explícito y respuesta a UI pendiente. |
| Replace | Dependencia Pi en contratos finales, settled como prueba de éxito y transcript padre como contexto heredado. |
| Required migration | Encapsular en adapter de compatibilidad del producto; reconstruir estado nativo con Turn/ModelStep e InteractionRequest, sin segunda autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/PiRpcToolGatingTests.cs`; PORT TS: `packages/core/test/pi-rpc-e2e.test.ts`, `packages/core/test/kernel.test.ts`, `packages/core/test/runtime.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Turn. |
| Dependencies | ADR-0005/0014/0019/0034/0046; spec §96–98. |
| Milestone | M2 / PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Mantener Pi temporal detrás del adapter; nativo no identifica turn_start de Pi con Turn canónico. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="modelstep"></a>

## ModelStep

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046 §1; invocación individual durable |
| C# source | No demostrado; UNKNOWN. |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | No extrapolar nombres parecidos ni crear contratos alternativos. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | C#: sin suite equivalente demostrada; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ModelStep. |
| Dependencies | Contrato objetivo indicado en cada capability. |
| Milestone | PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Pi eventos/usage no prueban ModelStep persistido con opaque state. Construir según contrato aprobado. Equivalencia pendiente, no ausencia global demostrada. |

<a id="toolstep"></a>

## ToolStep

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; alias de ToolCall, no entidad nueva |
| C# source | `src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/SandboxBridgeEndpoint.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/AppContainerSandboxWorker.cs`; `src/OmniCoder.Core/Interop/AppContainerProcessLauncher.cs`; `src/OmniCoder.Core/Interop/ProcessJobObject.cs` ([evidencia](evidencia.md#sandbox)). |
| TS source | `packages/core/src/platform/isolation.ts`; `packages/worker/src/security.ts` ([evidencia](evidencia.md#sandbox)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | ACL/Job Object, límites físicos, validación antes de tocar disco, version hash para reemplazo y rechazo de escape. |
| Replace | Command string del broker como autorización, enum de operación Pi y Strong declarado sin launcher real. |
| Required migration | Aprovechar pruebas en OmniCore.Sandbox multi-target ya existente; toda operación conserva Prepare→Permission→AuthorizedIntent→Barrier→Execute. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs`, `tests/OmniCoder.Core.Tests/SandboxBridgeEndpointTests.cs`, `tests/OmniCoder.Core.Tests/AppContainerSandboxWorkerTests.cs`; PORT TS: `packages/worker/test/security.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ToolStep. |
| Dependencies | ADR-0008/0014/0015/0037/0038; no dependencia Security→Sandbox. |
| Milestone | M2–M3 / PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Broker suministra failure cases, no ciclo durable Requested→terminal completo. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="steering"></a>

## Steering

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; input aplicado en frontera segura del Turn |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Orchestration/Execution/PiRpcAgentRuntime.cs`; `src/OmniCoder.Core/Orchestration/Execution/IAgentRuntime.cs` ([evidencia](evidencia.md#pi)). |
| TS source | `packages/core/src/rpc/pi-rpc-client.ts`; `packages/core/src/hosting/agent-runtime.ts`; `packages/core/src/kernel/kernel.ts`; `packages/core/src/kernel/pi-kernel.ts` ([evidencia](evidencia.md#pi)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | ACK correlacionado, cancelación, mensajes sin pérdida, error explícito y respuesta a UI pendiente. |
| Replace | Dependencia Pi en contratos finales, settled como prueba de éxito y transcript padre como contexto heredado. |
| Required migration | Encapsular en adapter de compatibilidad del producto; reconstruir estado nativo con Turn/ModelStep e InteractionRequest, sin segunda autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/PiRpcToolGatingTests.cs`; PORT TS: `packages/core/test/pi-rpc-e2e.test.ts`, `packages/core/test/kernel.test.ts`, `packages/core/test/runtime.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Steering. |
| Dependencies | ADR-0005/0014/0019/0034/0046; spec §96–98. |
| Milestone | PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Pi steer es donante de comportamiento; persistir received/applied/dropped y probar consumo una vez. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="followup"></a>

## FollowUp

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; intención encolada para Turn posterior |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Orchestration/Execution/PiRpcAgentRuntime.cs`; `src/OmniCoder.Core/Orchestration/Execution/IAgentRuntime.cs` ([evidencia](evidencia.md#pi)). |
| TS source | `packages/core/src/rpc/pi-rpc-client.ts`; `packages/core/src/hosting/agent-runtime.ts`; `packages/core/src/kernel/kernel.ts`; `packages/core/src/kernel/pi-kernel.ts` ([evidencia](evidencia.md#pi)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | ACK correlacionado, cancelación, mensajes sin pérdida, error explícito y respuesta a UI pendiente. |
| Replace | Dependencia Pi en contratos finales, settled como prueba de éxito y transcript padre como contexto heredado. |
| Required migration | Encapsular en adapter de compatibilidad del producto; reconstruir estado nativo con Turn/ModelStep e InteractionRequest, sin segunda autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/PiRpcToolGatingTests.cs`; PORT TS: `packages/core/test/pi-rpc-e2e.test.ts`, `packages/core/test/kernel.test.ts`, `packages/core/test/runtime.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico FollowUp. |
| Dependencies | ADR-0005/0014/0019/0034/0046; spec §96–98. |
| Milestone | PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Pi follow_up y TS followupQueued no bastan: correlacionar en cola durable, no añadir texto al objective mutándolo. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="interactionrequest"></a>

## InteractionRequest

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0034/0045; formulario/respuesta tipada del servidor |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Orchestration/Execution/PiRpcAgentRuntime.cs`; `src/OmniCoder.Core/Orchestration/Execution/IAgentRuntime.cs` ([evidencia](evidencia.md#pi)). |
| TS source | `packages/core/src/rpc/pi-rpc-client.ts`; `packages/core/src/hosting/agent-runtime.ts`; `packages/core/src/kernel/kernel.ts`; `packages/core/src/kernel/pi-kernel.ts` ([evidencia](evidencia.md#pi)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | ACK correlacionado, cancelación, mensajes sin pérdida, error explícito y respuesta a UI pendiente. |
| Replace | Dependencia Pi en contratos finales, settled como prueba de éxito y transcript padre como contexto heredado. |
| Required migration | Encapsular en adapter de compatibilidad del producto; reconstruir estado nativo con Turn/ModelStep e InteractionRequest, sin segunda autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/PiRpcToolGatingTests.cs`; PORT TS: `packages/core/test/pi-rpc-e2e.test.ts`, `packages/core/test/kernel.test.ts`, `packages/core/test/runtime.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico InteractionRequest. |
| Dependencies | ADR-0005/0014/0019/0034/0046; spec §96–98. |
| Milestone | M1 / M3 / PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Portar no-presenter→cancelled; no convertirlo en aprobación. Cuestionario incluye single/multi/texto/Otro. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="modeldescriptor"></a>

## ModelDescriptor

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0005/0007; identidad y capacidades declaradas |
| C# source | `src/OmniCoder.Core/Orchestration/Model/RoleModelRouter.cs`; `src/OmniCoder.Core/Orchestration/Model/ModelRouting.cs` ([evidencia](evidencia.md#model)). |
| TS source | `packages/core/src/models/router.ts`; `packages/core/src/providers/model-effort-capabilities.ts`; `packages/core/src/providers/provider-probe.ts` ([evidencia](evidencia.md#model)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Mapeo explícito por rol y clases de coste observables; capacidades separadas de UI. |
| Replace | Presets por substring/nombre, scores declarados como qualification y free como permiso para cualquier ruta. |
| Required migration | Portar casos al Registry/ModelProfileResolver/Qualification existentes; resolver ModelRoute y SessionRoutingPolicy sin cambiar permisos por recomendación. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/RoleModelRouterTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ModelRoutingTests.cs`; PORT TS: `packages/core/test/router.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ModelDescriptor. |
| Dependencies | ADR-0005/0007/0011/0044/0046. |
| Milestone | M2 / M5 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="providerdescriptor"></a>

## ProviderDescriptor

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0011; protocolo/acceso separado del modelo |
| C# source | `src/OmniCoder.Core/Orchestration/Model/RoleModelRouter.cs`; `src/OmniCoder.Core/Orchestration/Model/ModelRouting.cs` ([evidencia](evidencia.md#model)). |
| TS source | `packages/core/src/models/router.ts`; `packages/core/src/providers/model-effort-capabilities.ts`; `packages/core/src/providers/provider-probe.ts` ([evidencia](evidencia.md#model)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Mapeo explícito por rol y clases de coste observables; capacidades separadas de UI. |
| Replace | Presets por substring/nombre, scores declarados como qualification y free como permiso para cualquier ruta. |
| Required migration | Portar casos al Registry/ModelProfileResolver/Qualification existentes; resolver ModelRoute y SessionRoutingPolicy sin cambiar permisos por recomendación. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/RoleModelRouterTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ModelRoutingTests.cs`; PORT TS: `packages/core/test/router.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ProviderDescriptor. |
| Dependencies | ADR-0005/0007/0011/0044/0046. |
| Milestone | M2 / M5 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="modelroutedescriptor"></a>

## ModelRouteDescriptor

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046 §3; endpoint/acceso/billing por ruta |
| C# source | `src/OmniCoder.Core/Orchestration/Model/RoleModelRouter.cs`; `src/OmniCoder.Core/Orchestration/Model/ModelRouting.cs` ([evidencia](evidencia.md#model)). |
| TS source | `packages/core/src/models/router.ts`; `packages/core/src/providers/model-effort-capabilities.ts`; `packages/core/src/providers/provider-probe.ts` ([evidencia](evidencia.md#model)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Mapeo explícito por rol y clases de coste observables; capacidades separadas de UI. |
| Replace | Presets por substring/nombre, scores declarados como qualification y free como permiso para cualquier ruta. |
| Required migration | Portar casos al Registry/ModelProfileResolver/Qualification existentes; resolver ModelRoute y SessionRoutingPolicy sin cambiar permisos por recomendación. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/RoleModelRouterTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ModelRoutingTests.cs`; PORT TS: `packages/core/test/router.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ModelRouteDescriptor. |
| Dependencies | ADR-0005/0007/0011/0044/0046. |
| Milestone | PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Provider/model no demuestra route key. No portar catálogo sin separar ruta. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="effectivemodelprofile"></a>

## EffectiveModelProfile

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0007/0044/0046; declarado+verificado+overrides |
| C# source | `src/OmniCoder.Core/Orchestration/Model/RoleModelRouter.cs`; `src/OmniCoder.Core/Orchestration/Model/ModelRouting.cs` ([evidencia](evidencia.md#model)). |
| TS source | `packages/core/src/models/router.ts`; `packages/core/src/providers/model-effort-capabilities.ts`; `packages/core/src/providers/provider-probe.ts` ([evidencia](evidencia.md#model)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Mapeo explícito por rol y clases de coste observables; capacidades separadas de UI. |
| Replace | Presets por substring/nombre, scores declarados como qualification y free como permiso para cualquier ruta. |
| Required migration | Portar casos al Registry/ModelProfileResolver/Qualification existentes; resolver ModelRoute y SessionRoutingPolicy sin cambiar permisos por recomendación. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/RoleModelRouterTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ModelRoutingTests.cs`; PORT TS: `packages/core/test/router.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico EffectiveModelProfile. |
| Dependencies | ADR-0005/0007/0011/0044/0046. |
| Milestone | M2 / M5 / PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Eliminar hints por nombre; usuario conserva categoría operativa y qualification solo recomienda. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="model-qualification"></a>

## Model Qualification

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0007; suite quick/full, evidencia por ruta |
| C# source | `src/OmniCoder.Core/Orchestration/Model/RoleModelRouter.cs`; `src/OmniCoder.Core/Orchestration/Model/ModelRouting.cs` ([evidencia](evidencia.md#model)). |
| TS source | `packages/core/src/models/router.ts`; `packages/core/src/providers/model-effort-capabilities.ts`; `packages/core/src/providers/provider-probe.ts` ([evidencia](evidencia.md#model)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Mapeo explícito por rol y clases de coste observables; capacidades separadas de UI. |
| Replace | Presets por substring/nombre, scores declarados como qualification y free como permiso para cualquier ruta. |
| Required migration | Portar casos al Registry/ModelProfileResolver/Qualification existentes; resolver ModelRoute y SessionRoutingPolicy sin cambiar permisos por recomendación. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/RoleModelRouterTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ModelRoutingTests.cs`; PORT TS: `packages/core/test/router.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Model Qualification. |
| Dependencies | ADR-0005/0007/0011/0044/0046. |
| Milestone | M5 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | router.test es referencia, no suite de cualificación; conservar Qualification actual y sumar probes. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="sessionroutingpolicy"></a>

## SessionRoutingPolicy

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046 §3; autorización de ruta y gasto por Session |
| C# source | `src/OmniCoder.Core/Orchestration/Usage/OrchestrationUsageMeter.cs` ([evidencia](evidencia.md#budget)). |
| TS source | `packages/core/src/goals/goal-scheduler.ts`; `packages/core/src/providers/usage-parsers.ts` ([evidencia](evidencia.md#budget)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Reserva previa con precio observado, liberación/commit idempotente y falla cerrada con precio desconocido. |
| Replace | Ledger JSON separado y token agregado que pierde input/output/cache/reasoning; lock estático no es coordinación entre procesos. |
| Required migration | Portar reservation/settlement y tests a gasto derivado de ModelStep y RunBudgetPool; eliminar doble contabilización al resumen del Turn. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationUsageMeterTests.cs`; PORT TS: `packages/core/test/goals-domain.test.ts`, `packages/core/test/usage.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico SessionRoutingPolicy. |
| Dependencies | ADR-0037/0043/0046; topes jerárquicos y autorización por ruta. |
| Milestone | PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Autorización ExactModel del donante es insuficiente: endpoint/billing/revisión/revocación también cuentan. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="context-engine"></a>

## Context Engine

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0029/0042; proyección materializada, provenance/budget |
| C# source | `src/OmniCoder.Core/Knowledge/KnowledgePlane.cs`; `src/OmniCoder.Core/Knowledge/KnowledgeSelector.cs`; `src/OmniCoder.Core/Knowledge/ContextAssembler.cs`; `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs` ([evidencia](evidencia.md#context)). |
| TS source | `packages/core/src/knowledge/registry.ts`; `packages/core/src/kernel/pi-kernel.ts`; `packages/core/src/plan/plan-memory.ts` ([evidencia](evidencia.md#context)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Presupuesto de selección, fuentes/trust, separación datos/instrucciones y prefijo estable. |
| Replace | Prompt tail propietario, decisión en MainViewModel, transcript completo heredado y tokens heurísticos como verdad. |
| Required migration | Adaptar selection como IContextContributor opcional; ContextMaterializer y checkpoint nativos siguen siendo autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeSelectorTests.cs`, `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeRegistryTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ContextBudgetTests.cs`; PORT TS: `packages/core/test/knowledge.test.ts`, `packages/core/test/plan-memory.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Context Engine. |
| Dependencies | ADR-0017/0026/0028/0029/0042/0046. |
| Milestone | M2 / M4 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="context-inheritance"></a>

## Context inheritance

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; SelectedProjection sin ampliar autoridad |
| C# source | `src/OmniCoder.Core/Knowledge/KnowledgePlane.cs`; `src/OmniCoder.Core/Knowledge/KnowledgeSelector.cs`; `src/OmniCoder.Core/Knowledge/ContextAssembler.cs`; `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs` ([evidencia](evidencia.md#context)). |
| TS source | `packages/core/src/knowledge/registry.ts`; `packages/core/src/kernel/pi-kernel.ts`; `packages/core/src/plan/plan-memory.ts` ([evidencia](evidencia.md#context)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Presupuesto de selección, fuentes/trust, separación datos/instrucciones y prefijo estable. |
| Replace | Prompt tail propietario, decisión en MainViewModel, transcript completo heredado y tokens heurísticos como verdad. |
| Required migration | Adaptar selection como IContextContributor opcional; ContextMaterializer y checkpoint nativos siguen siendo autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeSelectorTests.cs`, `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeRegistryTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ContextBudgetTests.cs`; PORT TS: `packages/core/test/knowledge.test.ts`, `packages/core/test/plan-memory.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Context inheritance. |
| Dependencies | ADR-0017/0026/0028/0029/0042/0046. |
| Milestone | M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | No copiar parentSessionPath/transcript. TaskPacket y selección explícita de items. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="context-pressure"></a>

## Context pressure

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0042/0046; overflow y reacción limitada |
| C# source | `src/OmniCoder.Core/Orchestration/Execution/AgentExecutionGuard.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProgressDetector.cs`; `src/OmniCoder.Core/Orchestration/Execution/InterventionController.cs` ([evidencia](evidencia.md#guard)). |
| TS source | `packages/core/src/scaffold/guards.ts`; `packages/worker/src/hooks/baseline.ts` ([evidencia](evidencia.md#guard)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Límites host-owned, separar transporte vivo de progreso, retry acotado y cancelación terminal. |
| Replace | Soft recovery asumida por encolar texto Pi; token delta interpretado como evidencia suficiente de avance útil. |
| Required migration | Usar como referencia HarnessPolicy/Completion Gates; esperar consumo/terminal por identidad ModelStep y no reejecutar efectos confirmados. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/ProgressDetectorTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/RuntimeGuardTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/InterventionControllerTests.cs`; PORT TS: `packages/worker/test/security-hooks.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Context pressure. |
| Dependencies | ADR-0004/0007/0016/0046/0047. |
| Milestone | M4 / PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Pressure no equivale a watchdog lento ni a ProgressDetector. Separar señales y política. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="compaction"></a>

## Compaction

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0029/0042; checkpoint y WorkingState pinned |
| C# source | `src/OmniCoder.Core/Knowledge/KnowledgePlane.cs`; `src/OmniCoder.Core/Knowledge/KnowledgeSelector.cs`; `src/OmniCoder.Core/Knowledge/ContextAssembler.cs`; `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs` ([evidencia](evidencia.md#context)). |
| TS source | `packages/core/src/knowledge/registry.ts`; `packages/core/src/kernel/pi-kernel.ts`; `packages/core/src/plan/plan-memory.ts` ([evidencia](evidencia.md#context)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Presupuesto de selección, fuentes/trust, separación datos/instrucciones y prefijo estable. |
| Replace | Prompt tail propietario, decisión en MainViewModel, transcript completo heredado y tokens heurísticos como verdad. |
| Required migration | Adaptar selection como IContextContributor opcional; ContextMaterializer y checkpoint nativos siguen siendo autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeSelectorTests.cs`, `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeRegistryTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ContextBudgetTests.cs`; PORT TS: `packages/core/test/knowledge.test.ts`, `packages/core/test/plan-memory.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Compaction. |
| Dependencies | ADR-0017/0026/0028/0029/0042/0046. |
| Milestone | M4 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Pi compact sirve como referencia; conservar MetaModelService/ContextCheckpoint propios, nunca compactar journal. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="tool-runtime"></a>

## Tool Runtime

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0014; tools no otorgan permisos |
| C# source | `src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/SandboxBridgeEndpoint.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/AppContainerSandboxWorker.cs`; `src/OmniCoder.Core/Interop/AppContainerProcessLauncher.cs`; `src/OmniCoder.Core/Interop/ProcessJobObject.cs` ([evidencia](evidencia.md#sandbox)). |
| TS source | `packages/core/src/platform/isolation.ts`; `packages/worker/src/security.ts` ([evidencia](evidencia.md#sandbox)). |
| Best donor | C# |
| Disposition | REIMPLEMENT |
| Preserve | ACL/Job Object, límites físicos, validación antes de tocar disco, version hash para reemplazo y rechazo de escape. |
| Replace | Command string del broker como autorización, enum de operación Pi y Strong declarado sin launcher real. |
| Required migration | Aprovechar pruebas en OmniCore.Sandbox multi-target ya existente; toda operación conserva Prepare→Permission→AuthorizedIntent→Barrier→Execute. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs`, `tests/OmniCoder.Core.Tests/SandboxBridgeEndpointTests.cs`, `tests/OmniCoder.Core.Tests/AppContainerSandboxWorkerTests.cs`; PORT TS: `packages/worker/test/security.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Tool Runtime. |
| Dependencies | ADR-0008/0014/0015/0037/0038; no dependencia Security→Sandbox. |
| Milestone | M2 / M3 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Conservar ToolRuntime actual. Broker legado solo aporta operaciones/tests, no authority. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="tool-registry"></a>

## Tool Registry

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0027; fuentes/precedencia/protected |
| C# source | No demostrado; UNKNOWN. |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | No extrapolar nombres parecidos ni crear contratos alternativos. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | C#: sin suite equivalente demostrada; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Tool Registry. |
| Dependencies | Contrato objetivo indicado en cada capability. |
| Milestone | M2 / M8 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Importar exports de Pi no demuestra registry con protección/ambigüedad requerido. Equivalencia pendiente, no ausencia global demostrada. |

<a id="prepare-permission-execute"></a>

## Prepare / Permission / Execute

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0014; Prepare puro y AuthorizedIntent infalsificable |
| C# source | `src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/SandboxBridgeEndpoint.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/AppContainerSandboxWorker.cs`; `src/OmniCoder.Core/Interop/AppContainerProcessLauncher.cs`; `src/OmniCoder.Core/Interop/ProcessJobObject.cs` ([evidencia](evidencia.md#sandbox)). |
| TS source | `packages/core/src/platform/isolation.ts`; `packages/worker/src/security.ts` ([evidencia](evidencia.md#sandbox)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | ACL/Job Object, límites físicos, validación antes de tocar disco, version hash para reemplazo y rechazo de escape. |
| Replace | Command string del broker como autorización, enum de operación Pi y Strong declarado sin launcher real. |
| Required migration | Aprovechar pruebas en OmniCore.Sandbox multi-target ya existente; toda operación conserva Prepare→Permission→AuthorizedIntent→Barrier→Execute. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs`, `tests/OmniCoder.Core.Tests/SandboxBridgeEndpointTests.cs`, `tests/OmniCoder.Core.Tests/AppContainerSandboxWorkerTests.cs`; PORT TS: `packages/worker/test/security.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Prepare / Permission / Execute. |
| Dependencies | ADR-0008/0014/0015/0037/0038; no dependencia Security→Sandbox. |
| Milestone | M2 / M3 / PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Separar validación de operación de autorización y barrier. Legacy Validate permitido no fabrica intent. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="process-execution"></a>

## Process execution

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0015; executable + argv, drain, timeout/kill |
| C# source | `src/OmniCoder.Core/Processes/AgentProcessManager.cs`; `src/OmniCoder.Core/Processes/NewlineFramer.cs`; `src/OmniCoder.Core/Interop/ProcessJobObject.cs` ([evidencia](evidencia.md#process)). |
| TS source | `packages/core/src/processes/agent-process-manager.ts`; `packages/core/src/processes/newline-framer.ts`; `packages/core/src/platform/process-tree.ts` ([evidencia](evidencia.md#process)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Framing solo LF, CRLF, chunks/U+2028, control de overflow y kill de árbol. |
| Replace | Proceso ligado al agent/chat, shell-string como contrato de process.exec y cualquier reset del proceso sin nueva identidad. |
| Required migration | Separar IProcessRuntime del backend Pi; llevar framing puro al transporte y pruebas de timeout/drain/kill a Execution. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/AgentProcessManagerIntegrationTests.cs`, `tests/OmniCoder.Core.Tests/NewlineFramerTests.cs`; PORT TS: `packages/core/test/framer.test.ts`, `packages/worker/test/process-tree.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Process execution. |
| Dependencies | ADR-0015/0019/0038. |
| Milestone | M2 / M3 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="shell"></a>

## Shell

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0015/0037; launcher aislado + autorización |
| C# source | `src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/SandboxBridgeEndpoint.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/AppContainerSandboxWorker.cs`; `src/OmniCoder.Core/Interop/AppContainerProcessLauncher.cs`; `src/OmniCoder.Core/Interop/ProcessJobObject.cs` ([evidencia](evidencia.md#sandbox)). |
| TS source | `packages/core/src/platform/isolation.ts`; `packages/worker/src/security.ts` ([evidencia](evidencia.md#sandbox)). |
| Best donor | C# |
| Disposition | REIMPLEMENT |
| Preserve | ACL/Job Object, límites físicos, validación antes de tocar disco, version hash para reemplazo y rechazo de escape. |
| Replace | Command string del broker como autorización, enum de operación Pi y Strong declarado sin launcher real. |
| Required migration | Aprovechar pruebas en OmniCore.Sandbox multi-target ya existente; toda operación conserva Prepare→Permission→AuthorizedIntent→Barrier→Execute. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs`, `tests/OmniCoder.Core.Tests/SandboxBridgeEndpointTests.cs`, `tests/OmniCoder.Core.Tests/AppContainerSandboxWorkerTests.cs`; PORT TS: `packages/worker/test/security.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Shell. |
| Dependencies | ADR-0008/0014/0015/0037/0038; no dependencia Security→Sandbox. |
| Milestone | M3 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | SandboxToolRequest.Command no es process.exec ni permiso para interpretar shell text. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="pty"></a>

## PTY

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0015; capability de terminal declarada |
| C# source | No demostrado; UNKNOWN. |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | No extrapolar nombres parecidos ni crear contratos alternativos. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | C#: sin suite equivalente demostrada; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico PTY. |
| Dependencies | Contrato objetivo indicado en cada capability. |
| Milestone | M3 / M9 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | No se demostró backend PTY real en archivos inspeccionados; pipes/stdout no prueban PTY. Equivalencia pendiente, no ausencia global demostrada. |

<a id="permission-engine"></a>

## Permission Engine

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0037; combinación por mínimo y grants por scope |
| C# source | `src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs`; `src/OmniCoder.Core/Orchestration/OrchestrationPlanValidator.cs` ([evidencia](evidencia.md#permission)). |
| TS source | `packages/core/src/permission/permission-service.ts`; `packages/core/src/security/plan-mode-policy.ts` ([evidencia](evidencia.md#permission)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | No efectos antes de autorización, scopes hijos subconjunto y Ask sin respuesta no es Allow. |
| Replace | Última regla coincidente gana en TS, approvals locales mutables y permisos por texto bash. |
| Required migration | Conservar Permission Engine de OmniCore; portar casos negativos, reescribir expectations de precedencia restrictiva y usar grants tipados. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs`; PORT TS: `packages/core/test/permission.test.ts`, `packages/worker/test/security.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Permission Engine. |
| Dependencies | ADR-0003/0014/0037/0044/0047. |
| Milestone | M2 / M3 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="isolation"></a>

## Isolation

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0008/0038; Strong verificable o WeakConsent |
| C# source | `src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/SandboxBridgeEndpoint.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/AppContainerSandboxWorker.cs`; `src/OmniCoder.Core/Interop/AppContainerProcessLauncher.cs`; `src/OmniCoder.Core/Interop/ProcessJobObject.cs` ([evidencia](evidencia.md#sandbox)). |
| TS source | `packages/core/src/platform/isolation.ts`; `packages/worker/src/security.ts` ([evidencia](evidencia.md#sandbox)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | ACL/Job Object, límites físicos, validación antes de tocar disco, version hash para reemplazo y rechazo de escape. |
| Replace | Command string del broker como autorización, enum de operación Pi y Strong declarado sin launcher real. |
| Required migration | Aprovechar pruebas en OmniCore.Sandbox multi-target ya existente; toda operación conserva Prepare→Permission→AuthorizedIntent→Barrier→Execute. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs`, `tests/OmniCoder.Core.Tests/SandboxBridgeEndpointTests.cs`, `tests/OmniCoder.Core.Tests/AppContainerSandboxWorkerTests.cs`; PORT TS: `packages/worker/test/security.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Isolation. |
| Dependencies | ADR-0008/0014/0015/0037/0038; no dependencia Security→Sandbox. |
| Milestone | M3 / M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="appcontainer"></a>

## AppContainer

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0008/0038; ACL, launcher y Job Object |
| C# source | `src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/SandboxBridgeEndpoint.cs`; `src/OmniCoder.Core/Orchestration/Sandbox/AppContainerSandboxWorker.cs`; `src/OmniCoder.Core/Interop/AppContainerProcessLauncher.cs`; `src/OmniCoder.Core/Interop/ProcessJobObject.cs` ([evidencia](evidencia.md#sandbox)). |
| TS source | `packages/core/src/platform/isolation.ts`; `packages/worker/src/security.ts` ([evidencia](evidencia.md#sandbox)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | ACL/Job Object, límites físicos, validación antes de tocar disco, version hash para reemplazo y rechazo de escape. |
| Replace | Command string del broker como autorización, enum de operación Pi y Strong declarado sin launcher real. |
| Required migration | Aprovechar pruebas en OmniCore.Sandbox multi-target ya existente; toda operación conserva Prepare→Permission→AuthorizedIntent→Barrier→Execute. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs`, `tests/OmniCoder.Core.Tests/SandboxBridgeEndpointTests.cs`, `tests/OmniCoder.Core.Tests/AppContainerSandboxWorkerTests.cs`; PORT TS: `packages/worker/test/security.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico AppContainer. |
| Dependencies | ADR-0008/0014/0015/0037/0038; no dependencia Security→Sandbox. |
| Milestone | M3 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | C# es donante de plataforma, TS isolation.ts es interfaz, no equivalente físico. Sandbox actual ya existe. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="workspace-isolation"></a>

## Workspace isolation

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0021; snapshot del working tree + lease |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationWorktreeManager.cs`; `src/OmniCoder.Core/Orchestration/Promotion/ProductionResultPromotionService.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs` ([evidencia](evidencia.md#worktree)). |
| TS source | `packages/core/src/orchestration/worktree-manager.ts`; `packages/core/src/worker-bridge/service.ts` ([evidencia](evidencia.md#worktree)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Baseline/branch/patch verificados, preview antes de aplicar, recovery durable, conflicto explícito y protección de cambios ajenos. |
| Replace | Token de promoción propietario, aceptación=éxito y cualquier supuesto de workspace limpio único. |
| Required migration | Extraer promoción/recovery por etapas a snapshots/UndoPlan; tests reales Git con dirty/untracked/CRLF, sin reset destructivo. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationWorktreeManagerTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs`; PORT TS: `packages/core/test/worktree.test.ts`, `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Workspace isolation. |
| Dependencies | ADR-0004/0021/0038/0046; M6 solo escritores serializados. |
| Milestone | M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="git-worktrees"></a>

## Git worktrees

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0021; baseline, dirty y integración 3-way |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationWorktreeManager.cs`; `src/OmniCoder.Core/Orchestration/Promotion/ProductionResultPromotionService.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs` ([evidencia](evidencia.md#worktree)). |
| TS source | `packages/core/src/orchestration/worktree-manager.ts`; `packages/core/src/worker-bridge/service.ts` ([evidencia](evidencia.md#worktree)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Baseline/branch/patch verificados, preview antes de aplicar, recovery durable, conflicto explícito y protección de cambios ajenos. |
| Replace | Token de promoción propietario, aceptación=éxito y cualquier supuesto de workspace limpio único. |
| Required migration | Extraer promoción/recovery por etapas a snapshots/UndoPlan; tests reales Git con dirty/untracked/CRLF, sin reset destructivo. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationWorktreeManagerTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs`; PORT TS: `packages/core/test/worktree.test.ts`, `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Git worktrees. |
| Dependencies | ADR-0004/0021/0038/0046; M6 solo escritores serializados. |
| Milestone | M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | C# aporta recovery/promoción; TS casos CRLF/blob exacto/unborn. No elegir por lenguaje. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="workspacesnapshotstore"></a>

## WorkspaceSnapshotStore

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; snapshots/preimages para restore |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationWorktreeManager.cs`; `src/OmniCoder.Core/Orchestration/Promotion/ProductionResultPromotionService.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs` ([evidencia](evidencia.md#worktree)). |
| TS source | `packages/core/src/orchestration/worktree-manager.ts`; `packages/core/src/worker-bridge/service.ts` ([evidencia](evidencia.md#worktree)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Baseline/branch/patch verificados, preview antes de aplicar, recovery durable, conflicto explícito y protección de cambios ajenos. |
| Replace | Token de promoción propietario, aceptación=éxito y cualquier supuesto de workspace limpio único. |
| Required migration | Extraer promoción/recovery por etapas a snapshots/UndoPlan; tests reales Git con dirty/untracked/CRLF, sin reset destructivo. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationWorktreeManagerTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs`; PORT TS: `packages/core/test/worktree.test.ts`, `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico WorkspaceSnapshotStore. |
| Dependencies | ADR-0004/0021/0038/0046; M6 solo escritores serializados. |
| Milestone | M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Extraer captura/baseline del worktree, no asumir snapshot persistido cubre todas las pre-imágenes. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="undo"></a>

## Undo

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; UndoPlan con conflictos, solo efectos atribuibles |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationWorktreeManager.cs`; `src/OmniCoder.Core/Orchestration/Promotion/ProductionResultPromotionService.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs` ([evidencia](evidencia.md#worktree)). |
| TS source | `packages/core/src/orchestration/worktree-manager.ts`; `packages/core/src/worker-bridge/service.ts` ([evidencia](evidencia.md#worktree)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Baseline/branch/patch verificados, preview antes de aplicar, recovery durable, conflicto explícito y protección de cambios ajenos. |
| Replace | Token de promoción propietario, aceptación=éxito y cualquier supuesto de workspace limpio único. |
| Required migration | Extraer promoción/recovery por etapas a snapshots/UndoPlan; tests reales Git con dirty/untracked/CRLF, sin reset destructivo. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationWorktreeManagerTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs`; PORT TS: `packages/core/test/worktree.test.ts`, `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Undo. |
| Dependencies | ADR-0004/0021/0038/0046; M6 solo escritores serializados. |
| Milestone | M7 / M10 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | C# UndoAsync es referencia con branch/recovery checks; paridad requiere cambios ajenos posteriores y restore bytes. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="effect-journal"></a>

## Effect Journal

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0004; commit Barrier y reconciliación de efectos |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationWorktreeManager.cs`; `src/OmniCoder.Core/Orchestration/Promotion/ProductionResultPromotionService.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs` ([evidencia](evidencia.md#worktree)). |
| TS source | `packages/core/src/orchestration/worktree-manager.ts`; `packages/core/src/worker-bridge/service.ts` ([evidencia](evidencia.md#worktree)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Baseline/branch/patch verificados, preview antes de aplicar, recovery durable, conflicto explícito y protección de cambios ajenos. |
| Replace | Token de promoción propietario, aceptación=éxito y cualquier supuesto de workspace limpio único. |
| Required migration | Extraer promoción/recovery por etapas a snapshots/UndoPlan; tests reales Git con dirty/untracked/CRLF, sin reset destructivo. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationWorktreeManagerTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs`; PORT TS: `packages/core/test/worktree.test.ts`, `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Effect Journal. |
| Dependencies | ADR-0004/0021/0038/0046; M6 solo escritores serializados. |
| Milestone | M3 / PRE-M6 MIGRATION / M7 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | RecoveryPrepared antes de WorkspaceApplied es donante parcial; extender a efectos no Git y ToolCall v3. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="canonical-journal"></a>

## Canonical Journal

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0001/0002; append-only SQLite + CAS |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationJournalStore.cs` ([evidencia](evidencia.md#journal)). |
| TS source | `packages/core/src/orchestration/journal-store.ts`; `packages/core/src/goals/goal-store.ts` ([evidencia](evidencia.md#journal)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Sequence contigua, evento durable antes de snapshot, replay idempotente y rechazo de snapshot adelantado. |
| Replace | Archivos JSON por evento/Run, snapshot propietario, redacción indiscriminada de payload opaco y acceso directo del producto. |
| Required migration | Portar escenarios a SqliteEventStore/EventCodecs/JournalVerifier actuales; importer separado, nunca reemplazo del Canonical Journal. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Canonical Journal. |
| Dependencies | ADR-0001/0002/0013/0018/0043/0046. |
| Milestone | M1 / M4 / PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Donante C# por flushToDisk/WriteThrough; TS rename sin fsync observado no prueba la misma durabilidad. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="telemetry"></a>

## Telemetry

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0043/0046; ITelemetrySink separado |
| C# source | `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#product)). |
| TS source | `packages/app/src/main/index.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/renderer/app.tsx`; `packages/core/src/threading/coalescers.ts` ([evidencia](evidencia.md#product)). |
| Best donor | TS |
| Disposition | ADAPT |
| Preserve | IPC/renderer desacoplado de secretos, UI real, descarte de flush obsoleto y experiencia de preguntas. |
| Replace | Acceso a internals Pi y decisiones runtime desde controles visuales. |
| Required migration | Electron usa adapter Protocol/Client; política Supervisor/Goal permanece producto y solo envía commands. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/MainViewModelOrchestrationRoutingTests.cs`; ADAPT TS: `packages/app/test/e2e/workbench.e2e.test.ts`, `packages/app/test/e2e/goals.e2e.test.ts`, `packages/core/test/coalescer.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Telemetry. |
| Dependencies | ADR-0019/0025/0030/0031–0034/0045/0047. |
| Milestone | M4 / PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Medium |
| Notes | Coalescers solo cliente; trasladar contratos, no deltas al EventStore. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="engineevent-envelope"></a>

## EngineEvent envelope

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046 §4; scope, Source, UTC y ArtifactRefs |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationJournalStore.cs` ([evidencia](evidencia.md#journal)). |
| TS source | `packages/core/src/orchestration/journal-store.ts`; `packages/core/src/goals/goal-store.ts` ([evidencia](evidencia.md#journal)). |
| Best donor | C# |
| Disposition | REIMPLEMENT |
| Preserve | Sequence contigua, evento durable antes de snapshot, replay idempotente y rechazo de snapshot adelantado. |
| Replace | Archivos JSON por evento/Run, snapshot propietario, redacción indiscriminada de payload opaco y acceso directo del producto. |
| Required migration | Portar escenarios a SqliteEventStore/EventCodecs/JournalVerifier actuales; importer separado, nunca reemplazo del Canonical Journal. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico EngineEvent envelope. |
| Dependencies | ADR-0001/0002/0013/0018/0043/0046. |
| Milestone | PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Envelope legado run/task no tiene toda atribución Task/Lane/Turn/AgentExecution. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="causality"></a>

## Causality

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; causation real y ExecutionScope |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationJournalStore.cs` ([evidencia](evidencia.md#journal)). |
| TS source | `packages/core/src/orchestration/journal-store.ts`; `packages/core/src/goals/goal-store.ts` ([evidencia](evidencia.md#journal)). |
| Best donor | C# |
| Disposition | REIMPLEMENT |
| Preserve | Sequence contigua, evento durable antes de snapshot, replay idempotente y rechazo de snapshot adelantado. |
| Replace | Archivos JSON por evento/Run, snapshot propietario, redacción indiscriminada de payload opaco y acceso directo del producto. |
| Required migration | Portar escenarios a SqliteEventStore/EventCodecs/JournalVerifier actuales; importer separado, nunca reemplazo del Canonical Journal. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Causality. |
| Dependencies | ADR-0001/0002/0013/0018/0043/0046. |
| Milestone | PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Sequence no implica CausationId; no inferir causalidad perfecta del transcript importado. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="sequence"></a>

## Sequence

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0001/0046; orden durable por stream |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationJournalStore.cs` ([evidencia](evidencia.md#journal)). |
| TS source | `packages/core/src/orchestration/journal-store.ts`; `packages/core/src/goals/goal-store.ts` ([evidencia](evidencia.md#journal)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Sequence contigua, evento durable antes de snapshot, replay idempotente y rechazo de snapshot adelantado. |
| Replace | Archivos JSON por evento/Run, snapshot propietario, redacción indiscriminada de payload opaco y acceso directo del producto. |
| Required migration | Portar escenarios a SqliteEventStore/EventCodecs/JournalVerifier actuales; importer separado, nunca reemplazo del Canonical Journal. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Sequence. |
| Dependencies | ADR-0001/0002/0013/0018/0043/0046. |
| Milestone | M1 / PRE-M6 MIGRATION / M9 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Portar non-contiguous/snapshot-ahead; no igualar revision de UI con journal sequence. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="artifactstore"></a>

## ArtifactStore

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0001/0018; CAS inmutable y retention segura |
| C# source | `src/OmniCoder.Core/Orchestration/Artifacts/ArtifactSet.cs` ([evidencia](evidencia.md#artifact)). |
| TS source | `packages/core/src/orchestration/artifact-set.ts` ([evidencia](evidencia.md#artifact)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Hash SHA-256 de patch congelado, manifiesto ordenado y verificación de contenido antes de promoción. |
| Replace | FrozenPatchPath absoluto, generationId ajeno al CAS y representación textual universal. |
| Required migration | Reusar fixtures como pruebas de FileArtifactStore y evidence refs; acordar formato canónico antes de comparar hashes entre lenguajes. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/ArtifactSetStoreTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ArtifactStore. |
| Dependencies | ADR-0001/0018/0021/0046; CAS existente. |
| Milestone | M2 / M4 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="delegation"></a>

## Delegation

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; Task→Lane→AgentExecution con resultado |
| C# source | `src/OmniCoder.Core/Orchestration/Services/DelegationApplicationService.cs`; `src/OmniCoder.Core/Orchestration/Services/DelegationDeliveryStore.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#delivery)). |
| TS source | `packages/core/src/worker-bridge/service.ts`; `packages/core/src/worker-bridge/contracts.ts`; `packages/worker/src/cli.ts` ([evidencia](evidencia.md#delivery)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Resultado guardado antes de notificar, deliveryId determinista por revisión, ACK durable y sesión padre explícita. |
| Replace | Store separado que arranca vacío al corrupción, identidad UI como owner, resultado como texto mutable y despertar por mera terminación. |
| Required migration | Llevar dedup/ACK a ExecutionMailbox y eventos de servidor; result disposition separado de la entrega y WakePolicy separado del transporte. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/DelegationDeliveryTests.cs`, `tests/OmniCoder.Core.Tests/DelegationDeliveryIntegrationTests.cs`; PORT TS: `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Delegation. |
| Dependencies | ADR-0046; AgentExecution/AgentResult, SupervisionBinding. |
| Milestone | M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="escalation"></a>

## Escalation

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0007/0046; ruta consentida con causa |
| C# source | `src/OmniCoder.Core/Orchestration/Model/RoleModelRouter.cs`; `src/OmniCoder.Core/Orchestration/Model/ModelRouting.cs` ([evidencia](evidencia.md#model)). |
| TS source | `packages/core/src/models/router.ts`; `packages/core/src/providers/model-effort-capabilities.ts`; `packages/core/src/providers/provider-probe.ts` ([evidencia](evidencia.md#model)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Mapeo explícito por rol y clases de coste observables; capacidades separadas de UI. |
| Replace | Presets por substring/nombre, scores declarados como qualification y free como permiso para cualquier ruta. |
| Required migration | Portar casos al Registry/ModelProfileResolver/Qualification existentes; resolver ModelRoute y SessionRoutingPolicy sin cambiar permisos por recomendación. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/RoleModelRouterTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ModelRoutingTests.cs`; PORT TS: `packages/core/test/router.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Escalation. |
| Dependencies | ADR-0005/0007/0011/0044/0046. |
| Milestone | M5 / PRE-M6 MIGRATION / M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Recovery/routing no puede crear gasto ni cambio de modo; aplicar ADR-0047. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="wakerequest"></a>

## WakeRequest

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; solicitud, no reanudación automática |
| C# source | `src/OmniCoder.Core/Orchestration/Services/DelegationApplicationService.cs`; `src/OmniCoder.Core/Orchestration/Services/DelegationDeliveryStore.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#delivery)). |
| TS source | `packages/core/src/worker-bridge/service.ts`; `packages/core/src/worker-bridge/contracts.ts`; `packages/worker/src/cli.ts` ([evidencia](evidencia.md#delivery)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Resultado guardado antes de notificar, deliveryId determinista por revisión, ACK durable y sesión padre explícita. |
| Replace | Store separado que arranca vacío al corrupción, identidad UI como owner, resultado como texto mutable y despertar por mera terminación. |
| Required migration | Llevar dedup/ACK a ExecutionMailbox y eventos de servidor; result disposition separado de la entrega y WakePolicy separado del transporte. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/DelegationDeliveryTests.cs`, `tests/OmniCoder.Core.Tests/DelegationDeliveryIntegrationTests.cs`; PORT TS: `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico WakeRequest. |
| Dependencies | ADR-0046; AgentExecution/AgentResult, SupervisionBinding. |
| Milestone | M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Guardar resultado no autoriza wake ni aceptación; decisión se journaliza. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="awaited-detached"></a>

## Awaited / Detached

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; relación de espera separada de supervision |
| C# source | No demostrado; UNKNOWN. |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | No extrapolar nombres parecidos ni crear contratos alternativos. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | C#: sin suite equivalente demostrada; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Awaited / Detached. |
| Dependencies | Contrato objetivo indicado en cada capability. |
| Milestone | PRE-M6 MIGRATION / M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Background Promise no prueba semántica durable de Detached. Construir contrato y tests nativos. Equivalencia pendiente, no ausencia global demostrada. |

<a id="managed-unmanaged"></a>

## Managed / Unmanaged

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; aceptación por supervisor distinta del join |
| C# source | No demostrado; UNKNOWN. |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | No extrapolar nombres parecidos ni crear contratos alternativos. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | C#: sin suite equivalente demostrada; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Managed / Unmanaged. |
| Dependencies | Contrato objetivo indicado en cada capability. |
| Milestone | PRE-M6 MIGRATION / M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | awaitingReview es referencia parcial, no demuestra handshake ni failure policy. Equivalencia pendiente, no ausencia global demostrada. |

<a id="supervisionbinding"></a>

## SupervisionBinding

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; bind/handshake/failure policy |
| C# source | No demostrado; UNKNOWN. |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | No extrapolar nombres parecidos ni crear contratos alternativos. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | C#: sin suite equivalente demostrada; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico SupervisionBinding. |
| Dependencies | Contrato objetivo indicado en cada capability. |
| Milestone | PRE-M6 MIGRATION / M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | No trasladar supervisorService por coincidencia de nombre. Equivalencia pendiente, no ausencia global demostrada. |

<a id="executionmailbox"></a>

## ExecutionMailbox

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; bandeja durable correlacionada |
| C# source | `src/OmniCoder.Core/Orchestration/Services/DelegationApplicationService.cs`; `src/OmniCoder.Core/Orchestration/Services/DelegationDeliveryStore.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#delivery)). |
| TS source | `packages/core/src/worker-bridge/service.ts`; `packages/core/src/worker-bridge/contracts.ts`; `packages/worker/src/cli.ts` ([evidencia](evidencia.md#delivery)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Resultado guardado antes de notificar, deliveryId determinista por revisión, ACK durable y sesión padre explícita. |
| Replace | Store separado que arranca vacío al corrupción, identidad UI como owner, resultado como texto mutable y despertar por mera terminación. |
| Required migration | Llevar dedup/ACK a ExecutionMailbox y eventos de servidor; result disposition separado de la entrega y WakePolicy separado del transporte. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/DelegationDeliveryTests.cs`, `tests/OmniCoder.Core.Tests/DelegationDeliveryIntegrationTests.cs`; PORT TS: `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ExecutionMailbox. |
| Dependencies | ADR-0046; AgentExecution/AgentResult, SupervisionBinding. |
| Milestone | M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Modelo deliveryId/ACK útil; ledger debe integrarse con causalidad y leases de servidor. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="wakepolicy"></a>

## WakePolicy

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; evaluador determinista sin polling LLM |
| C# source | `src/OmniCoder.Core/Orchestration/Services/DelegationApplicationService.cs`; `src/OmniCoder.Core/Orchestration/Services/DelegationDeliveryStore.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#delivery)). |
| TS source | `packages/core/src/worker-bridge/service.ts`; `packages/core/src/worker-bridge/contracts.ts`; `packages/worker/src/cli.ts` ([evidencia](evidencia.md#delivery)). |
| Best donor | C# |
| Disposition | REIMPLEMENT |
| Preserve | Resultado guardado antes de notificar, deliveryId determinista por revisión, ACK durable y sesión padre explícita. |
| Replace | Store separado que arranca vacío al corrupción, identidad UI como owner, resultado como texto mutable y despertar por mera terminación. |
| Required migration | Llevar dedup/ACK a ExecutionMailbox y eventos de servidor; result disposition separado de la entrega y WakePolicy separado del transporte. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/DelegationDeliveryTests.cs`, `tests/OmniCoder.Core.Tests/DelegationDeliveryIntegrationTests.cs`; PORT TS: `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico WakePolicy. |
| Dependencies | ADR-0046; AgentExecution/AgentResult, SupervisionBinding. |
| Milestone | M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | DeliveryStore explícitamente no scheduler; no copiar el mecanismo de notificar como policy. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="runtimecommand"></a>

## RuntimeCommand

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0024/0046; comando tipado, autoridad en servidor |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Processes/NewlineFramer.cs` ([evidencia](evidencia.md#protocol)). |
| TS source | `packages/protocol/src/index.ts`; `packages/server/src/server.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/main/index.ts` ([evidencia](evidencia.md#protocol)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | DTOs sin tipos de renderer, requestId correlacionado, capabilities explícitas y runtime unavailable visible. |
| Replace | HTTP/SSE como obligación del Core, EventId en memoria como sequence durable y endpoints que crean su propio AgentRuntime. |
| Required migration | Mantener Omni Protocol/IOmniClient como contrato; portar tests de transporte al Host stdio y adaptar Electron por borde. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/NewlineFramerTests.cs`; PORT TS: `packages/server/test/server.test.ts`, `packages/core/test/framer.test.ts`, `packages/app/test/e2e/workbench.e2e.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico RuntimeCommand. |
| Dependencies | ADR-0013/0019/0030/0046; Protocol net8. |
| Milestone | PRE-M6 MIGRATION / M9 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | PiCommand/GoalCommand/IPC sirven como casos; no parser slash en Engine. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="commandoutcome"></a>

## CommandOutcome

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046 §5; resultado durable/correlacionado |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Processes/NewlineFramer.cs` ([evidencia](evidencia.md#protocol)). |
| TS source | `packages/protocol/src/index.ts`; `packages/server/src/server.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/main/index.ts` ([evidencia](evidencia.md#protocol)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | DTOs sin tipos de renderer, requestId correlacionado, capabilities explícitas y runtime unavailable visible. |
| Replace | HTTP/SSE como obligación del Core, EventId en memoria como sequence durable y endpoints que crean su propio AgentRuntime. |
| Required migration | Mantener Omni Protocol/IOmniClient como contrato; portar tests de transporte al Host stdio y adaptar Electron por borde. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/NewlineFramerTests.cs`; PORT TS: `packages/server/test/server.test.ts`, `packages/core/test/framer.test.ts`, `packages/app/test/e2e/workbench.e2e.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico CommandOutcome. |
| Dependencies | ADR-0013/0019/0030/0046; Protocol net8. |
| Milestone | PRE-M6 MIGRATION |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | HTTP ok/ACK del proceso no demuestra Completed de command canónico. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="fanout"></a>

## FanOut

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; FanOutGroup y limits |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationPlanValidator.cs` ([evidencia](evidencia.md#dag)). |
| TS source | `packages/core/src/orchestration/plan-validator.ts`; `packages/core/src/goals/goal-contracts.ts` ([evidencia](evidencia.md#dag)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | DFS visiting/visited, detección de ciclos, referencias inexistentes y ids duplicados. |
| Replace | Plan/Task 1:1 legado, reglas de billing/modelos y paths embebidas en el validador. |
| Required migration | Extraer solamente validación pura del grafo hacia el contrato N:M; conservar PlanService y añadir paridad de ciclos. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationPlanValidatorTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico FanOut. |
| Dependencies | ADR-0016/0036; TaskGraph. |
| Milestone | M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Medium |
| Notes | Cola workspace/parallel tasks son referencia; no prueban grupo durable y budgets. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="fanin"></a>

## FanIn

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; evaluator Direct/Aggregate determinista |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationPlanValidator.cs` ([evidencia](evidencia.md#dag)). |
| TS source | `packages/core/src/orchestration/plan-validator.ts`; `packages/core/src/goals/goal-contracts.ts` ([evidencia](evidencia.md#dag)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | DFS visiting/visited, detección de ciclos, referencias inexistentes y ids duplicados. |
| Replace | Plan/Task 1:1 legado, reglas de billing/modelos y paths embebidas en el validador. |
| Required migration | Extraer solamente validación pura del grafo hacia el contrato N:M; conservar PlanService y añadir paridad de ciclos. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationPlanValidatorTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico FanIn. |
| Dependencies | ADR-0016/0036; TaskGraph. |
| Milestone | M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Medium |
| Notes | No concatenar summaries para aceptar resultados sin ResultDisposition. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="executionjoin"></a>

## ExecutionJoin

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; All/Any/Quorum/Explicit |
| C# source | No demostrado; UNKNOWN. |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | No extrapolar nombres parecidos ni crear contratos alternativos. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | C#: sin suite equivalente demostrada; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ExecutionJoin. |
| Dependencies | Contrato objetivo indicado en cada capability. |
| Milestone | M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | dependencias all-done no demuestran joins con esas políticas. Equivalencia pendiente, no ausencia global demostrada. |

<a id="agentresult"></a>

## AgentResult

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; resultado inmutable por schema |
| C# source | `src/OmniCoder.Core/Orchestration/Artifacts/ArtifactSet.cs` ([evidencia](evidencia.md#artifact)). |
| TS source | `packages/core/src/orchestration/artifact-set.ts` ([evidencia](evidencia.md#artifact)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Hash SHA-256 de patch congelado, manifiesto ordenado y verificación de contenido antes de promoción. |
| Replace | FrozenPatchPath absoluto, generationId ajeno al CAS y representación textual universal. |
| Required migration | Reusar fixtures como pruebas de FileArtifactStore y evidence refs; acordar formato canónico antes de comparar hashes entre lenguajes. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/ArtifactSetStoreTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico AgentResult. |
| Dependencies | ADR-0001/0018/0021/0046; CAS existente. |
| Milestone | PRE-M6 MIGRATION / M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | ArtifactSet aporta evidencia; reemplazar handoff/workerSummary por schema y resultado versionado. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="resultdisposition"></a>

## ResultDisposition

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; aceptación independiente del resultado |
| C# source | `src/OmniCoder.Core/Orchestration/Validation/ProductionOrchestrationTaskValidator.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs` ([evidencia](evidencia.md#validation)). |
| TS source | `packages/core/src/goals/goal-scheduler.ts`; `packages/core/src/worker-bridge/service.ts` ([evidencia](evidencia.md#validation)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Evidencia congelada, validator independiente, no promover dos veces y no aparentar éxito sin runtime/servicio. |
| Replace | WorkerSummary como evidencia, test verde como aceptación global y criterios shell string. |
| Required migration | Portar fixtures a CompletionGates/ResultDisposition; registrar nivel y evidencia real, no activar inferencia para scheduler. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationValidatorBehaviorTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs`; PORT TS: `packages/core/test/goals-domain.test.ts`, `packages/core/test/worker-bridge.test.ts`, `packages/worker/test/no-runtime-fails-loud.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ResultDisposition. |
| Dependencies | ADR-0016/0036/0046; M6/M7 validación adaptativa. |
| Milestone | PRE-M6 MIGRATION / M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Portar accepted/rejected/rework fixtures, no cambiar ArtifactSet ya producido. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="hierarchical-budgets"></a>

## Hierarchical budgets

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; topes hijo y ancestros simultáneos |
| C# source | `src/OmniCoder.Core/Orchestration/Usage/OrchestrationUsageMeter.cs` ([evidencia](evidencia.md#budget)). |
| TS source | `packages/core/src/goals/goal-scheduler.ts`; `packages/core/src/providers/usage-parsers.ts` ([evidencia](evidencia.md#budget)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Reserva previa con precio observado, liberación/commit idempotente y falla cerrada con precio desconocido. |
| Replace | Ledger JSON separado y token agregado que pierde input/output/cache/reasoning; lock estático no es coordinación entre procesos. |
| Required migration | Portar reservation/settlement y tests a gasto derivado de ModelStep y RunBudgetPool; eliminar doble contabilización al resumen del Turn. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationUsageMeterTests.cs`; PORT TS: `packages/core/test/goals-domain.test.ts`, `packages/core/test/usage.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Hierarchical budgets. |
| Dependencies | ADR-0037/0043/0046; topes jerárquicos y autorización por ruta. |
| Milestone | M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | No se demostró jerarquía nativa en ledger del donante. Reserva útil; jerarquía se construye. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="runbudgetpool"></a>

## RunBudgetPool

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; reserva/consumo/liberación compartidos |
| C# source | `src/OmniCoder.Core/Orchestration/Usage/OrchestrationUsageMeter.cs` ([evidencia](evidencia.md#budget)). |
| TS source | `packages/core/src/goals/goal-scheduler.ts`; `packages/core/src/providers/usage-parsers.ts` ([evidencia](evidencia.md#budget)). |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Reserva previa con precio observado, liberación/commit idempotente y falla cerrada con precio desconocido. |
| Replace | Ledger JSON separado y token agregado que pierde input/output/cache/reasoning; lock estático no es coordinación entre procesos. |
| Required migration | Portar reservation/settlement y tests a gasto derivado de ModelStep y RunBudgetPool; eliminar doble contabilización al resumen del Turn. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationUsageMeterTests.cs`; PORT TS: `packages/core/test/goals-domain.test.ts`, `packages/core/test/usage.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico RunBudgetPool. |
| Dependencies | ADR-0037/0043/0046; topes jerárquicos y autorización por ruta. |
| Milestone | M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Critical |
| Notes | Test concurrente legacy usa semáforo estático en proceso; no certifica atomicidad multiproceso. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="capacityscheduler"></a>

## CapacityScheduler

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; FIFO/prioridad/recursos, WaitingForCapacity |
| C# source | `src/OmniCoder.Core/Orchestration/OrchestrationStateMachine.cs`; `src/OmniCoder.Core/Orchestration/OrchestrationCoordinator.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs` ([evidencia](evidencia.md#life)). |
| TS source | `packages/core/src/orchestration/state-machine.ts`; `packages/core/src/goals/goal-scheduler.ts` ([evidencia](evidencia.md#life)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Rechazo de transiciones ilegales, dependencias no liberadas prematuramente y separación producir/validar/aceptar/promover. |
| Replace | Identidades string, estados de orquestación/Goal y plan obligatorio; no equivalen al lifecycle de OmniCore. |
| Required migration | Usar fixtures de comportamiento contra las state machines y PlanService existentes; no sustituir el dominio ya construido. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs`, `tests/OmniCoder.Core.Tests/OrchestrationCoordinatorTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/goals-domain.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico CapacityScheduler. |
| Dependencies | Domain/Engine, ADR-0016/0035/0036/0046/0047. |
| Milestone | M6 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | maxConcurrentRuns y byWorkspace queue no representan todos los recursos; separar Ready/capacidad. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="immediatevalidation"></a>

## ImmediateValidation

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; diagnósticos baratos tras editar |
| C# source | `src/OmniCoder.Core/Lsp/LspLanguageClient.cs`; `src/OmniCoder.Core/Lsp/LspProtocol.cs` ([evidencia](evidencia.md#lsp)). |
| TS source | No demostrado; UNKNOWN. |
| Best donor | C# |
| Disposition | ADAPT |
| Preserve | Diagnósticos por documento y protocolo de language server. |
| Replace | Dependencias visuales y diagnóstico como dictamen universal de integración. |
| Required migration | Mantener cliente/detector en producto o extensión; Core acepta evidencia tipada para ImmediateValidation, no conoce lenguaje/LSP. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/LspProtocolTests.cs`, `tests/OmniCoder.Core.Tests/LspLanguageClientEndToEndTests.cs`; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ImmediateValidation. |
| Dependencies | ADR-0023/0029/0046. |
| Milestone | M6/M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Medium |
| Notes | Conservar cliente LSP en producto; adapter de evidencia hacia Core, no mover UI/lenguaje al dominio. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="completionvalidation"></a>

## CompletionValidation

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0016/0046; Local/Integration/Project/EndToEnd |
| C# source | `src/OmniCoder.Core/Orchestration/Validation/ProductionOrchestrationTaskValidator.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs` ([evidencia](evidencia.md#validation)). |
| TS source | `packages/core/src/goals/goal-scheduler.ts`; `packages/core/src/worker-bridge/service.ts` ([evidencia](evidencia.md#validation)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Evidencia congelada, validator independiente, no promover dos veces y no aparentar éxito sin runtime/servicio. |
| Replace | WorkerSummary como evidencia, test verde como aceptación global y criterios shell string. |
| Required migration | Portar fixtures a CompletionGates/ResultDisposition; registrar nivel y evidencia real, no activar inferencia para scheduler. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationValidatorBehaviorTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs`; PORT TS: `packages/core/test/goals-domain.test.ts`, `packages/core/test/worker-bridge.test.ts`, `packages/worker/test/no-runtime-fails-loud.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico CompletionValidation. |
| Dependencies | ADR-0016/0036/0046; M6/M7 validación adaptativa. |
| Milestone | M3 / M6/M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="integrationevidence"></a>

## IntegrationEvidence

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; evidencia de integración, no unit test |
| C# source | `src/OmniCoder.Core/Orchestration/Validation/ProductionOrchestrationTaskValidator.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs` ([evidencia](evidencia.md#validation)). |
| TS source | `packages/core/src/goals/goal-scheduler.ts`; `packages/core/src/worker-bridge/service.ts` ([evidencia](evidencia.md#validation)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Evidencia congelada, validator independiente, no promover dos veces y no aparentar éxito sin runtime/servicio. |
| Replace | WorkerSummary como evidencia, test verde como aceptación global y criterios shell string. |
| Required migration | Portar fixtures a CompletionGates/ResultDisposition; registrar nivel y evidencia real, no activar inferencia para scheduler. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationValidatorBehaviorTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs`; PORT TS: `packages/core/test/goals-domain.test.ts`, `packages/core/test/worker-bridge.test.ts`, `packages/worker/test/no-runtime-fails-loud.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico IntegrationEvidence. |
| Dependencies | ADR-0016/0036/0046; M6/M7 validación adaptativa. |
| Milestone | M6/M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Portar fixtures, agregar recorrido composition root→cliente→efecto; mocks solos no certifican. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="wiringevidence"></a>

## WiringEvidence

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; Core registra, producto detecta wiring |
| C# source | `src/OmniCoder.Core/Lsp/LspLanguageClient.cs`; `src/OmniCoder.Core/Lsp/LspProtocol.cs` ([evidencia](evidencia.md#lsp)). |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | Dependencias visuales y diagnóstico como dictamen universal de integración. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/LspProtocolTests.cs`, `tests/OmniCoder.Core.Tests/LspLanguageClientEndToEndTests.cs`; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico WiringEvidence. |
| Dependencies | ADR-0023/0029/0046. |
| Milestone | M6/M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Medium |
| Notes | No se verificó detector que pruebe módulo consumido. No inventar implementación a partir de LSP. Equivalencia pendiente, no ausencia global demostrada. |

<a id="changeriskassessment"></a>

## ChangeRiskAssessment

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; política/detector del producto |
| C# source | `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#product)). |
| TS source | `packages/app/src/main/index.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/renderer/app.tsx`; `packages/core/src/threading/coalescers.ts` ([evidencia](evidencia.md#product)). |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | Acceso a internals Pi y decisiones runtime desde controles visuales. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/MainViewModelOrchestrationRoutingTests.cs`; ADAPT TS: `packages/app/test/e2e/workbench.e2e.test.ts`, `packages/app/test/e2e/goals.e2e.test.ts`, `packages/core/test/coalescer.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ChangeRiskAssessment. |
| Dependencies | ADR-0019/0025/0030/0031–0034/0045/0047. |
| Milestone | M7+ |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Medium |
| Notes | No se demostró evaluador reusable contra el contrato; UI Armour no prueba assessment canónico. Equivalencia pendiente, no ausencia global demostrada. |

<a id="validationdebt"></a>

## ValidationDebt

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0046; deuda durable y visible |
| C# source | No demostrado; UNKNOWN. |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | No extrapolar nombres parecidos ni crear contratos alternativos. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | C#: sin suite equivalente demostrada; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico ValidationDebt. |
| Dependencies | Contrato objetivo indicado en cada capability. |
| Milestone | M6/M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Warnings legacy no prueban CompletedWithValidationDebt ni resolución de deuda. Equivalencia pendiente, no ausencia global demostrada. |

<a id="completion-gates"></a>

## Completion Gates

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0016/0036/0046/0047; gates proporcionales |
| C# source | `src/OmniCoder.Core/Orchestration/Validation/ProductionOrchestrationTaskValidator.cs`; `src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs` ([evidencia](evidencia.md#validation)). |
| TS source | `packages/core/src/goals/goal-scheduler.ts`; `packages/core/src/worker-bridge/service.ts` ([evidencia](evidencia.md#validation)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Evidencia congelada, validator independiente, no promover dos veces y no aparentar éxito sin runtime/servicio. |
| Replace | WorkerSummary como evidencia, test verde como aceptación global y criterios shell string. |
| Required migration | Portar fixtures a CompletionGates/ResultDisposition; registrar nivel y evidencia real, no activar inferencia para scheduler. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationValidatorBehaviorTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs`; PORT TS: `packages/core/test/goals-domain.test.ts`, `packages/core/test/worker-bridge.test.ts`, `packages/worker/test/no-runtime-fails-loud.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Completion Gates. |
| Dependencies | ADR-0016/0036/0046; M6/M7 validación adaptativa. |
| Milestone | M1 / M3 / M6/M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="skills"></a>

## Skills

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0026; lifecycle y fuentes bajo trust |
| C# source | `src/OmniCoder.Core/Knowledge/KnowledgePlane.cs`; `src/OmniCoder.Core/Knowledge/KnowledgeSelector.cs`; `src/OmniCoder.Core/Knowledge/ContextAssembler.cs`; `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs` ([evidencia](evidencia.md#context)). |
| TS source | `packages/core/src/knowledge/registry.ts`; `packages/core/src/kernel/pi-kernel.ts`; `packages/core/src/plan/plan-memory.ts` ([evidencia](evidencia.md#context)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Presupuesto de selección, fuentes/trust, separación datos/instrucciones y prefijo estable. |
| Replace | Prompt tail propietario, decisión en MainViewModel, transcript completo heredado y tokens heurísticos como verdad. |
| Required migration | Adaptar selection como IContextContributor opcional; ContextMaterializer y checkpoint nativos siguen siendo autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeSelectorTests.cs`, `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeRegistryTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ContextBudgetTests.cs`; PORT TS: `packages/core/test/knowledge.test.ts`, `packages/core/test/plan-memory.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Skills. |
| Dependencies | ADR-0017/0026/0028/0029/0042/0046. |
| Milestone | M8 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Portar metadata/selección; ejecución fuera de proceso por ExtensionBoundary, no bloque prompt=skill completa. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="extensions"></a>

## Extensions

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0023; host out-of-process y manifest ceiling |
| C# source | No demostrado; UNKNOWN. |
| TS source | No demostrado; UNKNOWN. |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | No extrapolar nombres parecidos ni crear contratos alternativos. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | C#: sin suite equivalente demostrada; TS: sin suite equivalente demostrada. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Extensions. |
| Dependencies | Contrato objetivo indicado en cada capability. |
| Milestone | M8 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Assets JS Pi no prueban ExtensionBoundary OmniCore. Mantener temporal adapters si son necesarios. Equivalencia pendiente, no ausencia global demostrada. |

<a id="mcp"></a>

## MCP

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0023/0027; MCP tools bajo permisos y trust |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Processes/NewlineFramer.cs` ([evidencia](evidencia.md#protocol)). |
| TS source | `packages/protocol/src/index.ts`; `packages/server/src/server.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/main/index.ts` ([evidencia](evidencia.md#protocol)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | DTOs sin tipos de renderer, requestId correlacionado, capabilities explícitas y runtime unavailable visible. |
| Replace | HTTP/SSE como obligación del Core, EventId en memoria como sequence durable y endpoints que crean su propio AgentRuntime. |
| Required migration | Mantener Omni Protocol/IOmniClient como contrato; portar tests de transporte al Host stdio y adaptar Electron por borde. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/NewlineFramerTests.cs`; PORT TS: `packages/server/test/server.test.ts`, `packages/core/test/framer.test.ts`, `packages/app/test/e2e/workbench.e2e.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico MCP. |
| Dependencies | ADR-0013/0019/0030/0046; Protocol net8. |
| Milestone | M8 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Worker/MCP endpoints donan fixtures de fallos. No transportar hostAction Pi como command Core. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="commands"></a>

## Commands

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0024/0025; registro y precedencia, UI actions aparte |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Processes/NewlineFramer.cs` ([evidencia](evidencia.md#protocol)). |
| TS source | `packages/protocol/src/index.ts`; `packages/server/src/server.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/main/index.ts` ([evidencia](evidencia.md#protocol)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | DTOs sin tipos de renderer, requestId correlacionado, capabilities explícitas y runtime unavailable visible. |
| Replace | HTTP/SSE como obligación del Core, EventId en memoria como sequence durable y endpoints que crean su propio AgentRuntime. |
| Required migration | Mantener Omni Protocol/IOmniClient como contrato; portar tests de transporte al Host stdio y adaptar Electron por borde. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/NewlineFramerTests.cs`; PORT TS: `packages/server/test/server.test.ts`, `packages/core/test/framer.test.ts`, `packages/app/test/e2e/workbench.e2e.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Commands. |
| Dependencies | ADR-0013/0019/0030/0046; Protocol net8. |
| Milestone | M8 / M9 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="memory"></a>

## Memory

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0028; opcional, externa, promoción no decidida por LLM |
| C# source | `src/OmniCoder.Core/Knowledge/KnowledgePlane.cs`; `src/OmniCoder.Core/Knowledge/KnowledgeSelector.cs`; `src/OmniCoder.Core/Knowledge/ContextAssembler.cs`; `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs` ([evidencia](evidencia.md#context)). |
| TS source | `packages/core/src/knowledge/registry.ts`; `packages/core/src/kernel/pi-kernel.ts`; `packages/core/src/plan/plan-memory.ts` ([evidencia](evidencia.md#context)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | Presupuesto de selección, fuentes/trust, separación datos/instrucciones y prefijo estable. |
| Replace | Prompt tail propietario, decisión en MainViewModel, transcript completo heredado y tokens heurísticos como verdad. |
| Required migration | Adaptar selection como IContextContributor opcional; ContextMaterializer y checkpoint nativos siguen siendo autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeSelectorTests.cs`, `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeRegistryTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ContextBudgetTests.cs`; PORT TS: `packages/core/test/knowledge.test.ts`, `packages/core/test/plan-memory.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Memory. |
| Dependencies | ADR-0017/0026/0028/0029/0042/0046. |
| Milestone | M8 / M10 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | plan-memory.ts es referencia de comportamiento; no único store canónico ni promoción automática. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="knowledge"></a>

## Knowledge

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0028/0029; contributor con provenance |
| C# source | `src/OmniCoder.Core/Knowledge/KnowledgePlane.cs`; `src/OmniCoder.Core/Knowledge/KnowledgeSelector.cs`; `src/OmniCoder.Core/Knowledge/ContextAssembler.cs`; `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs` ([evidencia](evidencia.md#context)). |
| TS source | `packages/core/src/knowledge/registry.ts`; `packages/core/src/kernel/pi-kernel.ts`; `packages/core/src/plan/plan-memory.ts` ([evidencia](evidencia.md#context)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | Presupuesto de selección, fuentes/trust, separación datos/instrucciones y prefijo estable. |
| Replace | Prompt tail propietario, decisión en MainViewModel, transcript completo heredado y tokens heurísticos como verdad. |
| Required migration | Adaptar selection como IContextContributor opcional; ContextMaterializer y checkpoint nativos siguen siendo autoridad. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeSelectorTests.cs`, `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeRegistryTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ContextBudgetTests.cs`; PORT TS: `packages/core/test/knowledge.test.ts`, `packages/core/test/plan-memory.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Knowledge. |
| Dependencies | ADR-0017/0026/0028/0029/0042/0046. |
| Milestone | M8 / M10 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | C# selector/assembler frente a TS scope/trust/budgets: comparar fixtures antes de elegir algoritmo. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="rag"></a>

## RAG

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0028; servicio externo sin dependencia del Engine |
| C# source | `src/OmniCoder.Core/Knowledge/KnowledgePlane.cs`; `src/OmniCoder.Core/Knowledge/KnowledgeSelector.cs`; `src/OmniCoder.Core/Knowledge/ContextAssembler.cs`; `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs` ([evidencia](evidencia.md#context)). |
| TS source | `packages/core/src/knowledge/registry.ts`; `packages/core/src/kernel/pi-kernel.ts`; `packages/core/src/plan/plan-memory.ts` ([evidencia](evidencia.md#context)). |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | Prompt tail propietario, decisión en MainViewModel, transcript completo heredado y tokens heurísticos como verdad. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeSelectorTests.cs`, `tests/OmniCoder.Core.Tests/Knowledge/KnowledgeRegistryTests.cs`, `tests/OmniCoder.Core.Tests/Orchestration/ContextBudgetTests.cs`; PORT TS: `packages/core/test/knowledge.test.ts`, `packages/core/test/plan-memory.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico RAG. |
| Dependencies | ADR-0017/0026/0028/0029/0042/0046. |
| Milestone | M8 / M10 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | TS registry observado usa overlap lexical; eso no demuestra vector DB/embeddings. Donante vectorial no verificado. Equivalencia pendiente, no ausencia global demostrada. |

<a id="protocol"></a>

## Protocol

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0013/0019; wire versionado independiente del dominio |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Processes/NewlineFramer.cs` ([evidencia](evidencia.md#protocol)). |
| TS source | `packages/protocol/src/index.ts`; `packages/server/src/server.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/main/index.ts` ([evidencia](evidencia.md#protocol)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | DTOs sin tipos de renderer, requestId correlacionado, capabilities explícitas y runtime unavailable visible. |
| Replace | HTTP/SSE como obligación del Core, EventId en memoria como sequence durable y endpoints que crean su propio AgentRuntime. |
| Required migration | Mantener Omni Protocol/IOmniClient como contrato; portar tests de transporte al Host stdio y adaptar Electron por borde. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/NewlineFramerTests.cs`; PORT TS: `packages/server/test/server.test.ts`, `packages/core/test/framer.test.ts`, `packages/app/test/e2e/workbench.e2e.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Protocol. |
| Dependencies | ADR-0013/0019/0030/0046; Protocol net8. |
| Milestone | M9 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Reutilización propuesta con evidencia estática parcial; APIs y ownership se ajustan al contrato objetivo, no al revés. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="host"></a>

## Host

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0019/0046; servidor único, composition y stdio |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Processes/NewlineFramer.cs` ([evidencia](evidencia.md#protocol)). |
| TS source | `packages/protocol/src/index.ts`; `packages/server/src/server.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/main/index.ts` ([evidencia](evidencia.md#protocol)). |
| Best donor | Both |
| Disposition | REIMPLEMENT |
| Preserve | DTOs sin tipos de renderer, requestId correlacionado, capabilities explícitas y runtime unavailable visible. |
| Replace | HTTP/SSE como obligación del Core, EventId en memoria como sequence durable y endpoints que crean su propio AgentRuntime. |
| Required migration | Mantener Omni Protocol/IOmniClient como contrato; portar tests de transporte al Host stdio y adaptar Electron por borde. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/NewlineFramerTests.cs`; PORT TS: `packages/server/test/server.test.ts`, `packages/core/test/framer.test.ts`, `packages/app/test/e2e/workbench.e2e.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Host. |
| Dependencies | ADR-0013/0019/0030/0046; Protocol net8. |
| Milestone | M9 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | server.ts contiene sesiones/runtime propios; reusar casos, no establecer segundo Host. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="cli-lab"></a>

## CLI Lab

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0019/0030; omni cliente técnico real |
| C# source | `src/OmniCoder.Core/Rpc/PiRpcClient.cs`; `src/OmniCoder.Core/Processes/NewlineFramer.cs` ([evidencia](evidencia.md#protocol)). |
| TS source | `packages/protocol/src/index.ts`; `packages/server/src/server.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/main/index.ts` ([evidencia](evidencia.md#protocol)). |
| Best donor | Both |
| Disposition | ADAPT |
| Preserve | DTOs sin tipos de renderer, requestId correlacionado, capabilities explícitas y runtime unavailable visible. |
| Replace | HTTP/SSE como obligación del Core, EventId en memoria como sequence durable y endpoints que crean su propio AgentRuntime. |
| Required migration | Mantener Omni Protocol/IOmniClient como contrato; portar tests de transporte al Host stdio y adaptar Electron por borde. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/PiRpcClientTests.cs`, `tests/OmniCoder.Core.Tests/NewlineFramerTests.cs`; PORT TS: `packages/server/test/server.test.ts`, `packages/core/test/framer.test.ts`, `packages/app/test/e2e/workbench.e2e.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico CLI Lab. |
| Dependencies | ADR-0013/0019/0030/0046; Protocol net8. |
| Milestone | M1–M5 / M10 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | Retener omni actual; worker CLI/MCP proporciona failure cases, no reemplazo de CLI. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="observability-projections"></a>

## Observability projections

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0030/0043/0046; proyecciones, no control |
| C# source | `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#product)). |
| TS source | `packages/app/src/main/index.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/renderer/app.tsx`; `packages/core/src/threading/coalescers.ts` ([evidencia](evidencia.md#product)). |
| Best donor | TS |
| Disposition | ADAPT |
| Preserve | IPC/renderer desacoplado de secretos, UI real, descarte de flush obsoleto y experiencia de preguntas. |
| Replace | Acceso a internals Pi y decisiones runtime desde controles visuales. |
| Required migration | Electron usa adapter Protocol/Client; política Supervisor/Goal permanece producto y solo envía commands. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/MainViewModelOrchestrationRoutingTests.cs`; ADAPT TS: `packages/app/test/e2e/workbench.e2e.test.ts`, `packages/app/test/e2e/goals.e2e.test.ts`, `packages/core/test/coalescer.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Observability projections. |
| Dependencies | ADR-0019/0025/0030/0031–0034/0045/0047. |
| Milestone | M4 / M9 / M10 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Medium |
| Notes | Electron Timeline/Goal observa wire; estado canónico sigue en Core, coalescers no pierden eventos durables. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="modes-ultracode"></a>

## Modes / UltraCode

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0047; selección explícita y autoridad acotada |
| C# source | `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#product)). |
| TS source | `packages/app/src/main/index.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/renderer/app.tsx`; `packages/core/src/threading/coalescers.ts` ([evidencia](evidencia.md#product)). |
| Best donor | TS |
| Disposition | REIMPLEMENT |
| Preserve | IPC/renderer desacoplado de secretos, UI real, descarte de flush obsoleto y experiencia de preguntas. |
| Replace | Acceso a internals Pi y decisiones runtime desde controles visuales. |
| Required migration | Electron usa adapter Protocol/Client; política Supervisor/Goal permanece producto y solo envía commands. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/MainViewModelOrchestrationRoutingTests.cs`; ADAPT TS: `packages/app/test/e2e/workbench.e2e.test.ts`, `packages/app/test/e2e/goals.e2e.test.ts`, `packages/core/test/coalescer.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Modes / UltraCode. |
| Dependencies | ADR-0019/0025/0030/0031–0034/0045/0047. |
| Milestone | PRE-M6 MIGRATION / M6 |
| Blocker | Yes |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | Medium |
| Notes | No importar presets de nombre/modelo ni cambios automáticos de Pi. UltraCode no viene de OmniCoder donante. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="framer-algoritmo"></a>

## Framer (algoritmo)

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0019; framing del transporte |
| C# source | `src/OmniCoder.Core/Processes/AgentProcessManager.cs`; `src/OmniCoder.Core/Processes/NewlineFramer.cs`; `src/OmniCoder.Core/Interop/ProcessJobObject.cs` ([evidencia](evidencia.md#process)). |
| TS source | `packages/core/src/processes/agent-process-manager.ts`; `packages/core/src/processes/newline-framer.ts`; `packages/core/src/platform/process-tree.ts` ([evidencia](evidencia.md#process)). |
| Best donor | C# |
| Disposition | ADOPT |
| Preserve | Algoritmo puro LF/CRLF, chunks, U+2028/U+2029, límite y reset; kill-tree pertenece a Process execution. |
| Replace | Proceso ligado al agent/chat, shell-string como contrato de process.exec y cualquier reset del proceso sin nueva identidad. |
| Required migration | ADOPT helper C# en transporte; PORT corpus TS. Decoder UTF-8 incremental, error ante pérdida de evento y negociación requieren tests nativos; D2 no queda aprobado por adoptar el algoritmo. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/NewlineFramerTests.cs`; PORT TS: `packages/core/test/framer.test.ts`. Añadir oversized→error/gap y multibyte fragmentado. |
| Dependencies | ADR-0015/0019/0038. |
| Milestone | M9 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | ADOPT C# NewlineFramer puro; PORT TS framer.test con U+2028/U+2029/overflow. Decoder/negociación se implementan por contrato. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="diff-primitives"></a>

## Diff primitives

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0021; patch versionado/conflicto |
| C# source | `src/OmniCoder.Core/Orchestration/Artifacts/ArtifactSet.cs` ([evidencia](evidencia.md#artifact)). |
| TS source | `packages/core/src/orchestration/artifact-set.ts` ([evidencia](evidencia.md#artifact)). |
| Best donor | None |
| Disposition | UNKNOWN |
| Preserve | Garantía objetivo; equivalencia del donante no demostrada. No autorizar extracción por el nombre. |
| Replace | FrozenPatchPath absoluto, generationId ajeno al CAS y representación textual universal. |
| Required migration | Construir lo ya aprobado si no hay donante cualificado; cerrar evidencia pendiente antes de adoptar o retirar. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/ArtifactSetStoreTests.cs`; PORT TS: `packages/core/test/orchestration.test.ts`, `packages/core/test/worker-bridge.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Diff primitives. |
| Dependencies | ADR-0001/0018/0021/0046; CAS existente. |
| Milestone | M3 / M7 |
| Blocker | No |
| Retirement trigger | No borrar al extraer: retirar autoridad legacy solo tras paridad y con un solo owner por ejecución; ver mapa C. |
| Risk | High |
| Notes | TS unified-diff-parser/diff-builder y tests localizados; equivalencia binaria/encoding aún no revisada. No adoptar parser por nombre. Equivalencia pendiente, no ausencia global demostrada. |

<a id="coalescers"></a>

## Coalescers

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0030/0043; deltas visuales fuera del journal |
| C# source | `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#product)). |
| TS source | `packages/app/src/main/index.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/renderer/app.tsx`; `packages/core/src/threading/coalescers.ts` ([evidencia](evidencia.md#product)). |
| Best donor | TS |
| Disposition | KEEP IN OMNICODER |
| Preserve | IPC/renderer desacoplado de secretos, UI real, descarte de flush obsoleto y experiencia de preguntas. |
| Replace | Acceso a internals Pi y decisiones runtime desde controles visuales. |
| Required migration | Electron usa adapter Protocol/Client; política Supervisor/Goal permanece producto y solo envía commands. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/MainViewModelOrchestrationRoutingTests.cs`; ADAPT TS: `packages/app/test/e2e/workbench.e2e.test.ts`, `packages/app/test/e2e/goals.e2e.test.ts`, `packages/core/test/coalescer.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Coalescers. |
| Dependencies | ADR-0019/0025/0030/0031–0034/0045/0047. |
| Milestone | M4 / M9 |
| Blocker | No |
| Retirement trigger | No retiro del producto; únicamente del acoplamiento runtime reemplazado. |
| Risk | Medium |
| Notes | TS generation invalida flush ya encolado: conservar implementación en cliente y pruebas Electron, no en Engine. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="wpf-product"></a>

## WPF product

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0030; Core independiente del framework |
| C# source | `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#product)). |
| TS source | `packages/app/src/main/index.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/renderer/app.tsx`; `packages/core/src/threading/coalescers.ts` ([evidencia](evidencia.md#product)). |
| Best donor | TS |
| Disposition | RETAIN TEMPORARILY |
| Preserve | IPC/renderer desacoplado de secretos, UI real, descarte de flush obsoleto y experiencia de preguntas. |
| Replace | Acceso a internals Pi y decisiones runtime desde controles visuales. |
| Required migration | Electron usa adapter Protocol/Client; política Supervisor/Goal permanece producto y solo envía commands. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/MainViewModelOrchestrationRoutingTests.cs`; ADAPT TS: `packages/app/test/e2e/workbench.e2e.test.ts`, `packages/app/test/e2e/goals.e2e.test.ts`, `packages/core/test/coalescer.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico WPF product. |
| Dependencies | ADR-0019/0025/0030/0031–0034/0045/0047. |
| Milestone | M9 / M10 |
| Blocker | No |
| Retirement trigger | Paridad end-to-end + selección explícita del backend + migración de datos comprobada + rollback probado; ver mapa C. |
| Risk | Medium |
| Notes | UI WPF se retira después de paridad Electron y export/import probado; extraer política/runtime de ViewModels sin mover UI al Core. Implementation reuse y behavioral reuse se verifican por separado. |

<a id="electron-product"></a>

## Electron product

| Campo | Contenido |
|---|---|
| OmniCore target | ADR-0030; cliente sobre Protocol/Client |
| C# source | `src/OmniCoder.Presentation/ViewModels/MainViewModel.cs`; `src/OmniCoder.Presentation/CompositionRoot.cs`; `src/OmniCoder.App/App.xaml.cs` ([evidencia](evidencia.md#product)). |
| TS source | `packages/app/src/main/index.ts`; `packages/app/src/preload/index.ts`; `packages/app/src/renderer/app.tsx`; `packages/core/src/threading/coalescers.ts` ([evidencia](evidencia.md#product)). |
| Best donor | TS |
| Disposition | KEEP IN OMNICODER |
| Preserve | IPC/renderer desacoplado de secretos, UI real, descarte de flush obsoleto y experiencia de preguntas. |
| Replace | Acceso a internals Pi y decisiones runtime desde controles visuales. |
| Required migration | Electron usa adapter Protocol/Client; política Supervisor/Goal permanece producto y solo envía commands. |
| Tests to migrate | ADAPT C#: `tests/OmniCoder.Core.Tests/MainViewModelOrchestrationRoutingTests.cs`; ADAPT TS: `packages/app/test/e2e/workbench.e2e.test.ts`, `packages/app/test/e2e/goals.e2e.test.ts`, `packages/core/test/coalescer.test.ts`. Selección exacta y excepciones en evidencia §Tests; añadir tests del contrato específico Electron product. |
| Dependencies | ADR-0019/0025/0030/0031–0034/0045/0047. |
| Milestone | M9 / M10 |
| Blocker | No |
| Retirement trigger | No retiro del producto; únicamente del acoplamiento runtime reemplazado. |
| Risk | Medium |
| Notes | Destino de producto confirmado por este encargo; adaptar IPC, preferencias y formularios, no mover React/Electron al Core. Implementation reuse y behavioral reuse se verifican por separado. |
