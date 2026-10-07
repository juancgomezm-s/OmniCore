# B. Roadmap vigente con anotaciones de migración

Fecha: 2026-10-01. Es un **anexo**, no un roadmap sustituto. [A: matriz](capability-transition-matrix.md), [evidencia](evidencia.md), [C: retiro](retirement-map.md).

## Roadmap reproducido sin cambios de estructura

Copia de las filas (enlaces relativos rebasados) de [arquitectura §24](../arquitectura.md), snapshot local de esta revisión. Los estados y cifras son los del roadmap, **no una nueva certificación de cierre**. Si el original cambia, actualizar esta copia y sus anotaciones antes de implementarlas.

| Milestone | Contenido | Criterio de salida |
|---|---|---|
| **M1 — Runtime sin IA + Planning** — ✅ **CERRADO EN WINDOWS** (2026-09-30; verificación en Linux diferida por decisión del usuario) | **Dominio:** ids tipados, `ScopeLevel`, `WorkspaceId`/`ProjectId` con rutas canónicas (ADR-0038 §4); Session, Run, Plan/PlanItem, Task/TaskGraph, Lane, Turn, ToolCall y sus máquinas de estado (ADR-0036); conversación ↔ Run, modos y cancelación (ADR-0035).<br>**Journal:** envelope, `EventType`, upcasters vacíos, `SqliteEventStore` + `InMemoryEventStore`, `IArtifactStore` simple (ADR-0041 §3), rutas de plataforma (ADR-0038/0039).<br>**Plan:** `PlanService`, `ProgressReconciler` R1–R7, watchdog, Completion Pipelines de Lane y de Run, `WorkingState`.<br>**Permisos:** tipos + `ScriptedPermissionPolicy` + `AuditSink` básico.<br>**Contratos congelados** (§22), incluidos `ITokenCounter` + `FakeTokenCounter`, `InteractionRequest` con eventos de dominio y `LocalizedText`.<br>**Cliente:** `OmniCore.Client` (`ClientProjection`), plain renderer interactivo (con pregunta en línea) y JSON renderer, recursos `es`/`en`.<br>**Simulación:** `omni sim` + escenarios YAML + golden rule.<br>**Tests de arquitectura:** IVT solo Security, referencias IL del CLI, multi-target | Los escenarios de `omni sim` (plan de varios items, TaskGraph N:M, `Ask`, crash + resume) se reconstruyen idénticos desde el journal, en **Windows y Linux** |
| **M2 — Explorer** — 🟢 **CÓDIGO COMPLETO** (2026-09-30; falta la validación manual con el modelo local) | **Modelo local:** `OpenAiChatCompatibleProvider` (ik_llama), `LocalModelHost` attach + managed, `IProcessRuntime` mínimo (control del árbol de procesos), registro mínimo de modelos + `NoModelConfigured`.<br>**Configuración:** YAML + schemas, Scope Resolver de configuración, confianza de workspace (ADR-0039).<br>**Seguridad:** `ISecretProvider` + `ICredentialStore` por plataforma; Permission Engine v1 (defaults autónomos, grants por `WorkspaceId`, `/permissions`, topes de gasto); `IPathBoundaryValidator` por plataforma; audit store.<br>**Modelo y contexto:** `EffectiveModelProfile` (Declared + Heuristic + Overrides) + `HarnessPolicy`; token counters reales; Context Engine v1 (`WorkingState`, `SessionConversationContributor`, política provisional de ADR-0042); `ContextSnapshot` + fingerprint.<br>**Tools:** pipeline real, read tools, `reference.resolve`, CAS v1, `ToolPresentation`.<br>**Planificación y diagnóstico:** `plan.propose`, `PlanApproval` (PLAN → ACT), `PromptCommand`, `/context` y `/tools` | Pendiente la ejecución real de `omni "explícame este repositorio"` con el modelo local (requiere la API key del usuario) |
| **M3 — Native Coder** — 🟢 **CÓDIGO COMPLETO** (2026-09-30; falta la validación manual) | `filesystem.write` y `patch` con tokens de versión; límites de mutación por Turn de ADR-0044 §5 (`BeginTurn` al iniciar cada Turn); `RequirePostEditValidation` exige un gate Build/Test posterior a la última edición; Coder vía `omni act` con gates Build/Test/Acceptance desde configuración tipada (`argv[]`). `process.exec`/`shell.exec` usan el launcher del sandbox; Strong en Windows (AppContainer + Job Object, grants ACL serializados) y `WeakSandboxConsent`; `process.exec` rechaza `.bat`/`.cmd`. ADR-0045 end-to-end: `user.ask` suspende el Turn y la respuesta vuelve como `ToolResult`; formulario plain TTY y `InputRequired` sin TTY. Effect Journal recupera efectos huérfanos incluso de Runs terminales; no inicia otro Run si quedan efectos sin reconciliar, permite resolución humana con `omni resolve`, y lecturas interrumpidas o simulaciones no bloquean falsamente. | Código completo (errores de tool con `ToolErrorCode` tipado, persistido en los eventos v2). Pendientes: sandbox Strong en Linux (diferido) y la ejecución real del criterio `omni act "corrige este test"` con el modelo local (`docs/validation/m1-m4.md`) |
> **Estado ADR-0045:** integración end-to-end verificada. `user.ask` suspende el Turn; tras responder, el mismo Turn recibe la respuesta como `ToolResult`. El formulario plain TTY está cableado y, sin TTY, el flujo termina con `InputRequired` sin inventar una respuesta.
| **M4 — Context management** — 🟡 **IMPLEMENTADO; VALIDACIÓN DE USO PENDIENTE** (+ **track TUI v0**) | Prune, Externalize, Compress y Compact con `MetaModelService` + `ContextCheckpoint`; GC, `verify-journal`, purga de sesiones y auditoría (retención de 180 días; gracia de GC de 24 h); herramienta para releer artifacts externalizados.<br>**TUI v0:** Terminal.Gui 2.6.0-develop.61, 4 zonas, sidebar, overlays, autocomplete, overlay de cuestionario y onboarding de modelos. La usabilidad queda pendiente de validación del usuario en un terminal real | Sesiones largas sin crecimiento ilimitado; el `WorkingState` sobrevive a la compaction; la TUI v0 usable (pendiente de validación en terminal real); eliminar una política hace reaparecer el onboarding; cuestionarios single/multi/texto/`Otro` producen la misma respuesta tipada en TUI y plain |
| **M5 — Models + Qualification** — 🟢 **CÓDIGO COMPLETO** (2026-09-30; criterios cubiertos por tests, falta la validación con providers reales) | `OpenAIResponsesProvider` (perfiles `api` y `codex`, **login ChatGPT**); `AnthropicMessagesProvider` (`ProviderOpaque`); registro completo + alias; router + escalación; `OmniCore.Qualification` + suite `quick`, estados y store; `FileMutationReliability` y recomendación de categoría; costo y cuota reales en la status line; calibración de estimaciones de tokens | Cambiar de worker local a frontier sin modificar la Task; cualificar un modelo sin cambiar el runtime; la evidencia puede recomendar pero nunca ampliar automáticamente su política operativa |
| **M5.5 — Fronteras pre-M6** (ADR-0046) — 🔜 **SIGUIENTE** | **Fase A, bugs** ([gap-pre-m6.md §4](../gap-pre-m6.md#4-bugs-que-no-dependen-del-anexo)): timestamp UTC; `ArtifactRefs` del envelope; `ContextPolicyHash` del fingerprint; estado opaco descartado y `Continuation` nula; input perdido al reanudar un Turn; uso perdido al suspender; escalación `auto` sin autorización; topes de ADR-0037 §7; `PendingEditValidation` durable.<br>**Fase B, contratos:** Turn de 1..N `ModelStep`; `FollowUp`/`Steering` explícitos; `ModelRoute` (ruta por defecto 1:1) y perfil/qualification key por ruta; `ReasoningReplayPolicy`; `SessionRoutingPolicy` + `BillingMode` + consentimiento vía `InteractionRequest` (cierra el modo `ask` de escalación); `ExecutionScope` en el envelope, `Source` y causation real; `ITelemetrySink` separado del journal; `CommandOutcome` + commands internos (el Host deja de escribir el journal por fuera); `toolcall.started` v3 con reversibilidad y pre-imagen; componentes del fingerprint; **tipos y eventos congelados** de `AgentExecution` (lineage, Awaited/Detached, Managed), Delegation/Escalation/Wake, `AgentResult` inmutable + `ResultDisposition`, `JoinPolicy` | Criterios de ADR-0046: un Turn multi-paso con suspensión cuenta todos los pasos; el estado opaco solo se reenvía a la misma ruta; una Session `Local` no puede acabar en una ruta de pago sin consentimiento; todos los eventos de una ToolCall están atribuidos a Task/Lane/Turn; solo el servidor escribe el journal (test de arquitectura); los eventos de M6 congelados serializan y se reproducen; los deltas nunca llegan al journal |
| **M6 — Multi-agent** | Descomposición, scheduler de lanes, lanes background, heartbeat agregado; **`AgentExecution` con lineage**, `ContextInheritancePolicy` (por defecto `SelectedProjection`; la herencia nunca amplía autoridad ni visibilidad); `FanOutGroup` + `FanInPolicyEvaluator` determinista (`Direct`/`Aggregate`); `ExecutionJoin` (`All/Any/Quorum/Explicit`); `AgentResult` por schema (`core.explorer.v1`, …) + `ResultDisposition` (aceptación explícita si es Managed); `SupervisionBinding` + handshake + `SupervisionFailurePolicy` (por defecto `Wait`; nunca degrada en silencio a Unmanaged); `ExecutionMailbox` + `WakePolicy` determinista (sin polling con LLM); presupuesto jerárquico + `RunBudgetPool`; `CapacityScheduler` (FIFO + prioridad + recursos reales, `WaitingForCapacity`); `WorkflowCommand`, tools `Dynamic`, `core.agents` + `LaneInspector` (TUI). **Solo las lanes de lectura corren en paralelo**; las escritoras se serializan con un lease de escritura hasta M7 | Explore + Implement + Verify como Tasks diferenciadas, con el Plan reconciliado; además, los flujos de ADR-0046/anexo §37–38 con supervisor fake en proceso: padre suspendido hasta que el join se cumple; supervisor caído observable y sin aceptación implícita; capacidad agotada → `WaitingForCapacity`; presupuestos limitan hijo y Run; el routing no escapa de la `SessionRoutingPolicy`; módulo sin cablear → `ReworkRequested` |
| **M6/M7 — Validación adaptativa** | `ImmediateValidation` (diagnósticos baratos tras editar); niveles de completion (`Local/Integration/Project/EndToEnd`); `EvidenceRef` (claim vs respaldada por tool), `IntegrationStatus`, `ValidationState` + `ValidationDebt` durables (`CompletedWithValidationDebt` visible); gates `IntegrationGate` y `WorkspaceConsistencyGate`. Core define los registros; la detección de wiring por lenguaje y el `ChangeRiskAssessment` (M7+) los aportan OmniCoder o extensiones | Un test unitario aislado no satisface un `IntegrationGate`; la falta de evidencia nunca se presenta como verificación |
| **M7 — Isolation** | Worktrees con `SnapshotOfWorkingTree`, integración 3-way, reconciliación Git, cleanup; **lanes escritoras paralelas**; **lanes delegadas a Claude Code** (ADR-0012 rev. 3, riesgo residual aceptado); **`WorkspaceSnapshotStore` + `UndoService`/`UndoPlan`** (restore, compensación y conflictos; solo efectos atribuibles a OmniCore, nunca `git reset --hard`) | Coders concurrentes sin pisar el workspace ni los cambios del usuario; un undo con cambios ajenos posteriores pide resolución en lugar de destruirlos |
| **M8 — Extensibility** | Host de extensiones (`OmniCore.Extensions`) + manifest YAML + `ExtensionBoundary`; **MCP** con las reglas de confianza de ADR-0023; hooks; ciclo de vida de skills; `CommandRegistry` con precedencia + `ExtensionCommand`; `toolPreferences`; **Session Memory**; `/commands`, `/skills`, `/extensions`, `/memory session` | Un hook o extensión puede restringir pero no ampliar, verificado por test |
| **M9 — Protocol + Host** | `StdioTransport`, negociación de `ProtocolVersion`, Host separado, lease entre procesos, read models persistidos; integración de OmniCoder vía Protocol y Client en net8; **reconexión "after sequence N" por stream**; **procedencia y fidelidad de historial** (`Native`/`Imported`, `Complete`/`Partial`); supervisor de OmniCoder por el transporte real | El mismo CLI funciona in-process y por stdio; un cliente net8 se conecta; reconectar no duplica ni pierde eventos |
| **M10 — CLI v1 + memoria + evaluación** | **Memoria Project, Workspace y Global** con promoción (toda la memoria en v1); keybindings configurables, Command Palette, `session.search`; suite `full` + calibración; regression suite; comparación con Pi; adapter de OmniCoder; **`/undo`**, inspección de `AgentExecution`, estado de supervisión, capacidad y `ValidationDebt` como proyecciones | Criterios de madurez de la spec §96 |

## Anotaciones por item existente

No cambian orden, nombres, milestones ni criterios aprobados. PRE-M6 MIGRATION es una etiqueta. Los deltas D1–D4 permanecen candidatos, no trabajo nuevo aceptado.

### M1 — Runtime sin IA + Planning

- **Migration source:** [life — C#/TS y tests exactos](evidencia.md#life); [dag — C#/TS y tests exactos](evidencia.md#dag); [journal — C#/TS y tests exactos](evidencia.md#journal).
- **Capabilities:** Session/Run/Plan/TaskGraph.
- **Strategy:** No reemplazar Domain/PlanService/SqliteEventStore actuales. Portar transiciones, ciclos y crash/replay; ADOPT solo helper DFS si aporta valor y el test N:M lo admite.
- **Preserve / parity:** Replays idénticos, mismos gates y errores; ningún import cambia ids o states por coincidencia de nombre.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** No retirar históricos JSON hasta importer y export verificados.

### M2 — Explorer

- **Migration source:** [model — C#/TS y tests exactos](evidencia.md#model); [context — C#/TS y tests exactos](evidencia.md#context); [artifact — C#/TS y tests exactos](evidencia.md#artifact); [process — C#/TS y tests exactos](evidencia.md#process); [permission — C#/TS y tests exactos](evidencia.md#permission).
- **Capabilities:** Model/Context/Read tools.
- **Strategy:** Usar selector/budget como contributor opcional y framing/timeout como tests. Mantener registro/CAS/pipeline existentes; ADAPT algoritmo o REIMPLEMENT semántica incompatible.
- **Preserve / parity:** Read-only real, provenance/pinned, secrets fuera del contexto y permisos por mínimo.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** Pi lectura se retira por rollout producto, no porque unit tests pasen.

### M3 — Native Coder

- **Migration source:** [sandbox — C#/TS y tests exactos](evidencia.md#sandbox); [worktree — C#/TS y tests exactos](evidencia.md#worktree); [validation — C#/TS y tests exactos](evidencia.md#validation); [guard — C#/TS y tests exactos](evidencia.md#guard).
- **Capabilities:** Prepare/Permission/Execute, process/shell, effects y gates.
- **Strategy:** ADAPT AppContainer/Job tests y exact-replace hash; usar recovery/promotion como failure cases. Mantener barrier nativo; broker Pi no fabrica AuthorizedIntent.
- **Preserve / parity:** Proceso aislado real, cancelación mata árbol, error de tool no es éxito, reconciliación después de crash y prueba del camino host→tool→modelo.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** Pi escritura solo después de parity y seguridad demostrada; WPF/TS guardas no se borran por anticipado.

### M4 — Context management (+ track TUI v0)

- **Migration source:** [context — C#/TS y tests exactos](evidencia.md#context); [guard — C#/TS y tests exactos](evidencia.md#guard); [artifact — C#/TS y tests exactos](evidencia.md#artifact); [product — C#/TS y tests exactos](evidencia.md#product).
- **Capabilities:** Pressure, checkpoints y proyecciones.
- **Strategy:** Knowledge selección se adapta; compaction Pi solo referencia. Coalescers/UI permanecen producto; CAS GC protege artifacts referenciados.
- **Preserve / parity:** WorkingState survives, snapshot dentro de budget, formulario single/multi/Otro y sin eventos canónicos perdidos.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** Retirar ownership context legacy por backend seleccionado; no GC de sesiones Pi aún usadas.

### M5 — Models + Qualification

- **Migration source:** [model — C#/TS y tests exactos](evidencia.md#model); [budget — C#/TS y tests exactos](evidencia.md#budget).
- **Capabilities:** Descriptors/profiles/qualification y routing.
- **Strategy:** ADAPT RoleModelRouter casos y reservation ledger; eliminar hints por nombre del camino nativo. Qualification actual prevalece sobre perfiles declarados.
- **Preserve / parity:** No ruta de pago sin consentimiento, límites/usage observados, real provider tests separados de mocks.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** Retirar Pi providers por ruta cualificada y acceso real probado, sin migrar secretos en logs.

### M5.5 — Fronteras pre-M6 (ya aprobado)

- **Migration source:** [pi — C#/TS y tests exactos](evidencia.md#pi); [journal — C#/TS y tests exactos](evidencia.md#journal); [delivery — C#/TS y tests exactos](evidencia.md#delivery); [budget — C#/TS y tests exactos](evidencia.md#budget); [protocol — C#/TS y tests exactos](evidencia.md#protocol).
- **Capabilities:** PRE-M6 MIGRATION.
- **Strategy:** Anotar Fase A/B existentes: ModelStep, input consumido una vez, opaque replay, uso completo, SessionRoutingPolicy, envelope, CommandOutcome y authority ADR-0047. No crear ni renombrar hito.
- **Preserve / parity:** Tres model steps/suspensión/restart con identidad y coste; commands correlacionados, causalidad, revocación; migración legacy por defecto sin UltraCode.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** No retirar adapters todavía; congelar contracts para M6. Opaque/preimage D1 requiere decisión antes de almacenar bytes sensibles.

### M6 — Multi-agent

- **Migration source:** [life — C#/TS y tests exactos](evidencia.md#life); [delivery — C#/TS y tests exactos](evidencia.md#delivery); [dag — C#/TS y tests exactos](evidencia.md#dag); [budget — C#/TS y tests exactos](evidencia.md#budget); [validation — C#/TS y tests exactos](evidencia.md#validation).
- **Capabilities:** AgentExecution, mailbox, joins, scheduler y budgets.
- **Strategy:** REIMPLEMENT lifecycle/joins/capacity contra ADR-0046; ADAPT deliveryId/ACK y reserve/settle. Colas de workspace son referencia, no CapacityScheduler completo. Solo lanes read paralelas; writers serializados.
- **Preserve / parity:** Awaited/Detached y Managed distintos, supervisor caído visible, join/ResultDisposition, WaitingForCapacity, pool/hijo limitados, no polling LLM.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** WorkerBridge scheduler puede perder autoridad solo con paridad de control/completion y ownership por ejecución.

### M6/M7 — Validación adaptativa

- **Migration source:** [validation — C#/TS y tests exactos](evidencia.md#validation); [lsp — C#/TS y tests exactos](evidencia.md#lsp); [product — C#/TS y tests exactos](evidencia.md#product).
- **Capabilities:** Evidence, wiring, risk y debt.
- **Strategy:** ADAPT validators/fixtures; detector LSP/wiring/risk queda producto/extensión. UNKNOWN detector de WiringEvidence completo; no inventarlo.
- **Preserve / parity:** IntegrationGate no aceptado por test aislado; deuda durable, nivel visible, wiring demostrado por composition real.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** Retirar review lifecycle duplicado solo cuando resulta equivalente para cada revisión.

### M7 — Isolation

- **Migration source:** [worktree — C#/TS y tests exactos](evidencia.md#worktree); [artifact — C#/TS y tests exactos](evidencia.md#artifact); [sandbox — C#/TS y tests exactos](evidencia.md#sandbox).
- **Capabilities:** Snapshots/worktrees/promoción/UndoPlan.
- **Strategy:** ADAPT promotion/recovery C#; PORT corpus TS CRLF/untracked/unborn. Encodings/hash/baselines y preimage snapshot se diseñan contra contratos existentes.
- **Preserve / parity:** Dirty user changes, branch/head moved, preview mismatch, promoción doble, crash antes/después apply y undo con cambios ajenos posteriores.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** Retiro legacy worktree/promotion por dueño por operación; mantener recovery records/importer mientras existan resultados pendientes.

### M8 — Extensibility

- **Migration source:** [context — C#/TS y tests exactos](evidencia.md#context); [protocol — C#/TS y tests exactos](evidencia.md#protocol); [permission — C#/TS y tests exactos](evidencia.md#permission).
- **Capabilities:** Skills/Extensions/MCP/Commands/Session Memory.
- **Strategy:** Metadata/casos MCP adaptables; host Pi/Assets no es ExtensionBoundary. Crear host out-of-process ya previsto; no arrastrar Pi ni permissive evaluator al Engine.
- **Preserve / parity:** Manifest techo y no grant, sources/protected/ambiguity, hooks nunca amplían, memory opcional.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** Extensión Pi se retira capability por capability tras parity y migración de config de producto.

### M9 — Protocol + Host

- **Migration source:** [protocol — C#/TS y tests exactos](evidencia.md#protocol); [process — C#/TS y tests exactos](evidencia.md#process); [product — C#/TS y tests exactos](evidencia.md#product); [journal — C#/TS y tests exactos](evidencia.md#journal).
- **Capabilities:** Transport, reconnect y integración Electron.
- **Strategy:** ADAPT DTO/failure cases; ADOPT framing C# con corpus TS. Stdio/negotiation nativos mandan. HTTP/SSE TS solo adapter si se aprueba/usa; no segunda Session/Run authority.
- **Preserve / parity:** Cliente net8/TS, reconexión after sequence sin pérdida/duplicación, import Partial/Imported, secretos ausentes del renderer.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** Servidor TS deja de crear runtime; renderer/main conserva fachada hasta adapter e2e. WPF retiro aún sujeto a parity de producto.

### M10 — CLI v1 + memoria + evaluación

- **Migration source:** [pi — C#/TS y tests exactos](evidencia.md#pi); [context — C#/TS y tests exactos](evidencia.md#context); [product — C#/TS y tests exactos](evidencia.md#product); [validation — C#/TS y tests exactos](evidencia.md#validation).
- **Capabilities:** Memoria, regression parity, /undo y adapters.
- **Strategy:** Mantener CLI Lab; PORT/ADAPT benchmarks y tests Pi al comparativo nativo. Plan-memory no sustituye IMemoryStore ni promotion humana.
- **Preserve / parity:** Regression corpus local/cloud, sesiones importadas, /undo y product E2E. Evidencia real separada de REQUIERE_CREDENCIAL_REAL.
- **Tests:** ADAPT suites C# y PORT casos TS indicados en las familias; excepciones COPY/REPLACE/DROP en evidencia §Tests. Los tests nativos existentes no se sustituyen. Agregar cobertura del criterio de salida de este item.
- **Retire after:** Retiro final Pi/WPF/dependencies solo con regression parity, datos exportados y rollback/recuperación probados.

## Orden de extracción dentro de cada item

1. Fijar baseline de código/config y guardar corpus de tests sin tocar datos del usuario.
2. Comparar contrato e invariantes; UNKNOWN no pasa a ADOPT sin revisión.
3. Añadir pruebas de parity a los componentes OmniCore existentes antes de portar implementación.
4. Extraer helper/behavior por la frontera aprobada; no referenciar OmniCoder.Core desde Engine.
5. Componer por Host/adapter; validar recorrido real, errores, restart y ownership.
6. Activar por capability/backend con una sola autoridad. Retirar según C, no por fecha ni cantidad de tests.

M1–M5 ya implementados se enriquecen con pruebas y ajustes necesarios, no se reabren para hacer greenfield rewrite. Las modificaciones de esos contratos que bloquean M6 pertenecen al trabajo PRE-M6 MIGRATION de M5.5 existente.
