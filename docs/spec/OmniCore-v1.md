# OmniCore v1

## Especificación Funcional y Técnica

**Versión:** 0.1
**Estado:** Draft técnico para implementación
**Runtime objetivo:** .NET 10 LTS / C#
**Producto consumidor inicial:** Omni CLI
**Producto consumidor futuro:** OmniCoder
**Dependencias prohibidas:** Pi, little-coder, UI de OmniCoder

> Documento original de análisis. Las decisiones que lo precisan o modifican están en `docs/adr/`.
> Cuando un ADR contradiga esta especificación, prevalece el ADR.

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
* memoria de usuario de largo plazo;
* RAG avanzado;
* gestión organizacional.

La arquitectura deberá permitir estas extensiones posteriormente sin modificar el núcleo conceptual.

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

---

# 5. Modelo conceptual principal

La jerarquía fundamental será:

```text
Session
  │
  └── Run
       │
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

Una sesión podrá asociarse a un Workspace.

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
Failed
Cancelled
```

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

    public ContextSource Source { get; init; }
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
```

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

    public string ModelDescriptorHash { get; init; }

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
}
```

---

# 33. ITool

```csharp
public interface ITool
{
    ToolDescriptor Descriptor { get; }

    ValueTask<ToolValidationResult> ValidateAsync(
        ToolCall call,
        ToolExecutionContext context,
        CancellationToken cancellationToken);

    ValueTask<ToolResult> ExecuteAsync(
        ToolCall call,
        ToolExecutionContext context,
        CancellationToken cancellationToken);
}
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

shell.exec

git.status
git.diff
git.log
git.show
```

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

---

# 40. Artifact Store

Outputs grandes no deberán saturar el contexto.

```csharp
public sealed record ArtifactRef
{
    public ArtifactId Id { get; init; }

    public string MediaType { get; init; }

    public long Size { get; init; }

    public ArtifactKind Kind { get; init; }
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
  preferred-model-class: local-small

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

---

# 55. Event Model

Toda operación relevante deberá producir eventos.

Envelope:

```csharp
public abstract record EngineEvent
{
    public int ProtocolVersion { get; init; }

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
/context
/events
/tasks
/lanes
/tools
/model
/files
/artifacts
/stats
/permissions
/exit
```

Posteriormente:

```text
/new
/resume
/fork

/plan
/act
/orq

/models

/compact

/agents

/skills

/diff
/undo

/usage
/doctor
```

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

Permisos y sandbox.

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

# 86. Milestone M1 — Runtime sin IA

Debe implementar:

```text
Session
Run
Task
TaskGraph
Lane
Turn
Events
State machines
```

No requiere modelo.

Criterio:

una ejecución completamente simulada puede reconstruirse por eventos.

---

# 87. Milestone M2 — Explorer

Incluye:

```text
Model abstraction
llama.cpp provider
Context Engine v1
read/list/search
permissions
Tool Runtime
Explorer
```

Debe permitir:

```bash
omni "explícame este repositorio"
```

---

# 88. Milestone M3 — Native Coder

Incluye:

```text
write
patch
Process Runtime
build
test
optimistic concurrency
validation
Completion Gates
```

Debe permitir:

```bash
omni act "corrige este test"
```

---

# 89. Milestone M4 — Context management completo

Incluye:

```text
Artifact Store
Prune
Externalize
Compress
Compact
MetaModelService
ContextCheckpoint
```

Debe soportar sesiones prolongadas sin crecimiento ilimitado de hot context.

---

# 90. Milestone M5 — Model Router

Incluye:

```text
Model registry
ModelDescriptor
EffectiveModelProfile
ModelPolicy
routing
reasoning effort
escalation
```

Debe poder cambiar entre worker local y frontera sin modificar Task.

---

# 91. Milestone M6 — Multi-agent

Incluye:

```text
Task decomposition
Lane scheduler
parallel lanes
background lanes
heartbeat
AgentResult
```

Debe permitir:

```text
Explore
+
Implement
+
Verify
```

como Tasks diferenciadas.

---

# 92. Milestone M7 — Isolation

Incluye:

```text
Git worktrees
Workspace isolation
stale write protection
integration validation
```

---

# 93. Milestone M8 — Extensibility

Incluye:

```text
Hooks
Skills
Scope Resolver
MCP integration
```

Memory/RAG podrá comenzar después sobre `IContextContributor`.

---

# 94. Milestone M9 — Persistence + Protocol

Incluye:

```text
SQLite
durable EventStore
ContextSnapshot persistence
Omni Protocol
stdio transport
Host separado
```

---

# 95. Milestone M10 — CLI v1

CLI ya usable como producto independiente.

Debe soportar:

```text
sessions
resume
plan
act
orq
context
models
tasks
lanes
tools
skills
permissions
diff
stats
doctor
```

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

OmniCoder deberá depender de una frontera pequeña:

```csharp
public interface IAgentRuntime
{
    IAsyncEnumerable<EngineEvent> ExecuteAsync(
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

Orden propuesto:

```text
EPIC-001 Domain primitives

EPIC-002 Event model

EPIC-003 State machines

EPIC-004 Task Graph

EPIC-005 Permission Engine

EPIC-006 Tool contracts

EPIC-007 Process Runtime

EPIC-008 Artifact Store

EPIC-009 Model abstractions

EPIC-010 llama.cpp provider

EPIC-011 ContextItem

EPIC-012 ContextBudget + Materializer

EPIC-013 Explorer

EPIC-014 CLI v0

EPIC-015 File write/patch

EPIC-016 Native Coder

EPIC-017 Validation + Completion Gates

EPIC-018 Externalization

EPIC-019 MetaModelService 4B

EPIC-020 Compression + Compaction

EPIC-021 Model Router

EPIC-022 Tool Discovery

EPIC-023 Task Decomposition

EPIC-024 Lane Scheduler

EPIC-025 Parallel/Background Lanes

EPIC-026 Worktree Isolation

EPIC-027 Hooks

EPIC-028 Skills + Scope Resolver

EPIC-029 SQLite Persistence

EPIC-030 Omni Protocol

EPIC-031 Host Process

EPIC-032 CLI v1

EPIC-033 Regression Suite

EPIC-034 Pi Comparison

EPIC-035 OmniCoder Adapter
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
Create Task
 ↓
Create Lane
 ↓
Materialize Context
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
Completion Gate
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
15. OmniCore pueda compararse objetivamente contra Pi.

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
