# OmniCore — Arquitectura (revisión v0.3)

- **Fecha:** 2026-09-24
- **Estado:** para revisión antes de iniciar M1. **No se ha implementado código funcional.**
- **Alcance:**
  - **v0.2:** decisiones P0/P1 (puntos 1–30), en §4–§24.
  - **v0.3:** extensibilidad, commands y memoria (puntos 31–41), en §25–§30. Además amplía las tablas de §21–§24.
- **Documentos:** [ADRs](../adr/README.md) · [Spec v0.3](../spec/OmniCore-v1.md)

La base conceptual se mantiene sin cambios:

```text
Session → Run → { Plan, TaskGraph → Task → Lane → AgentExecution → Turn }
```

Se conservan también el Context Engine, ToolCatalog/Planner/Plan/Router/Runtime, el Model Runtime, el Permission Engine, los Completion Gates, el Sandbox independiente, el Protocol separado, el Artifact Store, el runtime por eventos, Task/Lane como único camino para subagentes, los context snapshots, las extensiones (Skills/Hooks/Scope Resolver), `omni` como primer consumidor y la independencia de Pi y OmniCoder.

## Índice de entregables

| # | Entregable | Dónde |
|---|---|---|
| 1 | ADRs afectados | [README de ADRs](../adr/README.md). v0.2: 0001–0012 revisados y 0013–0021 nuevos. v0.3: 0009, 0017, 0019 y 0020 revisados; 0022–0029 nuevos |
| 2 | Spec principal | [OmniCore-v1.md](../spec/OmniCore-v1.md), v0.3 |
| 3 | Tests de arquitectura | Sin cambios de grafo; dos tests nuevos planificados para M1 (ADR-0009 §2) |
| 4–20 | Diagramas y diseños v0.2 | §4–§20 |
| 21 | Tabla de decisiones (puntos 1–41) | §21 |
| — | Clasificación de abstracciones | §22 |
| — | Open Architecture Questions | §23 |
| — | Roadmap M1–M10 revisado | §24 |
| 31–41 | Diagramas y diseños v0.3: scopes, extensiones, commands/keybindings, skills, tools, memory | §25–§30 |

---

## 4. Canonical Journal

[ADR-0001](../adr/0001-canonical-journal.md) · [ADR-0002](../adr/0002-event-store-sqlite.md)

```mermaid
flowchart LR
  W["Writer único por sesión"] -->|"1 · blob + fsync"| AS
  W -->|"2 · evento con ArtifactRefs"| ES
  subgraph Journal["Canonical Journal"]
    ES[("Event Store · SQLite<br/>append-only · (session_id, seq)")]
    META[("artifacts · artifact_refs<br/>metadata en SQLite")]
    AS[("Artifact Store · blobs/sha256/…<br/>inmutable · content-addressed")]
  end
  ES --- META
  META -.->|"ContentHash"| AS
  ES --> P1["Estado: Session · Run · Plan<br/>TaskGraph · Lane · Turn · ToolCall"]
  ES --> P2["Read models / UI"]
  ES --> P3["WorkingState"]
  ES --> P4["Context projections · Replay"]
  AS --> P4
  GC["GC mark-and-sweep"] -.->|"solo blobs sin referencia + gracia"| AS
```

**Regla:** estado en eventos, contenido en artifacts. Un artifact perdido degrada el replay, nunca el estado.

## 5. Event schema vs Omni Protocol

[ADR-0013](../adr/0013-event-schema-vs-protocol.md)

```mermaid
flowchart LR
  subgraph Durable["Durable · Domain + Infrastructure"]
    DB[("events<br/>type · schema_version · payload")]
    UP["Upcasters vN → actual<br/>al leer"]
  end
  subgraph Runtime["Runtime · Engine"]
    DE["DomainEvent<br/>EventType + EventSchemaVersion"]
  end
  subgraph Wire["Wire · Protocol"]
    WE["WireEnvelope<br/>ProtocolVersion · MessageType · payload"]
  end
  DE -->|"append"| DB
  DB -->|"read"| UP
  UP --> DE
  DE -->|"ProtocolMapper · Host"| WE
  WE -->|"InProcess · Stdio · NamedPipe"| C["Clientes: CLI · OmniCoder"]
```

Un cambio en un lado no obliga a cambiar el otro: el mapper absorbe la diferencia.

## 6. ToolCall durable, Effect Journal y recovery

[ADR-0004](../adr/0004-resume-y-effect-journal.md) · [ADR-0002 §2](../adr/0002-event-store-sqlite.md)

```mermaid
stateDiagram-v2
  [*] --> Requested: ToolCallRequested con ToolCallId
  Requested --> Rejected: esquema o Prepare inválido
  Requested --> Prepared: ToolCallPrepared con ToolIntent
  Prepared --> AwaitingPermission: PermissionRequested
  AwaitingPermission --> Denied: PermissionDenied
  AwaitingPermission --> Authorized: PermissionGranted y ToolCallAuthorized
  Authorized --> Started: ToolCallStarted, commit Barrier si hay efecto
  Started --> Succeeded: ToolCallSucceeded
  Started --> Failed: ToolCallFailed con EffectOutcome
  Started --> EffectUnknown: recovery sin outcome
  EffectUnknown --> Reconciled: ToolCallReconciled
  Succeeded --> [*]
  Failed --> [*]
  Reconciled --> [*]
  Denied --> [*]
  Rejected --> [*]
```

```mermaid
flowchart TD
  R["ResumeRun"] --> S{"¿ToolCalls Started<br/>sin outcome?"}
  S -->|"no"| T
  S -->|"sí"| U["ToolCallEffectUnknown"]
  U --> C{"EffectClass"}
  C -->|"None · Rerunnable"| RE["Reejecutar"]
  C -->|"Reconcilable"| RP["ReconciliationPolicy<br/>hash · HEAD/tree · trailer"]
  C -->|"NonIdempotent"| AK["Ask<br/>sin cliente → Deny → Task Blocked"]
  RP --> O{"Resultado"}
  O -->|"Applied · NotApplied"| RC["ToolCallReconciled"]
  O -->|"Conflict · Unresolvable"| AG["ToolResult al agente o Ask"]
  RE --> RC
  RC --> T{"¿Respuesta del modelo<br/>registrada completa?"}
  AG --> T
  AK --> T
  T -->|"sí"| X["Continuar ToolCalls autorizadas<br/>aún no iniciadas"]
  T -->|"no"| Y["TurnAbandoned → re-inferir"]
```

**Orden de durabilidad:**

1. Intent confirmado con **Barrier** (`ToolCallStarted`).
2. Efecto.
3. Outcome confirmado con Standard.

No hay transacción entre sistemas; la garantía es que todo intento de efecto queda registrado y se puede reconciliar.

## 7. Pipeline Raw ToolCall → Execute

[ADR-0014](../adr/0014-pipeline-tool-permission-execution.md)

```mermaid
flowchart LR
  subgraph AR["Agent / Model Runtime"]
    RAW["RawToolCall<br/>name · args · ProviderCallId"]
  end
  subgraph TL["OmniCore.Tools"]
    SV["Schema validation<br/>pura"]
    PR["ITool.Prepare<br/>pura · síncrona"]
    EX["ITool.ExecuteAsync<br/>AuthorizedToolIntent"]
  end
  subgraph SC["OmniCore.Security"]
    PE["Permission Engine<br/>intersección de capas · claims<br/>rutas físicas"]
  end
  RAW -->|"ToolCallRequested"| SV
  SV -->|"ok"| PR
  PR -->|"ToolIntent<br/>Effect · Claims · Risk"| PE
  SV -->|"inválida"| RJ["ToolCallRejected → repair loop"]
  PR -->|"rechazo"| RJ
  PE -->|"Allow → AuthorizedToolIntent<br/>constructor internal · solo Security"| EX
  PE -->|"Ask"| Q["Cola de aprobaciones"]
  PE -->|"Deny"| DN["PermissionDenied"]
  EX -->|"dentro de Claims"| RES["ToolResult + EffectOutcome"]
```

**Garantías estructurales:**

- `ExecuteAsync` solo acepta `AuthorizedToolIntent`, y solo Security puede construirlo.
- `Prepare` es síncrona y su contexto no expone servicios de I/O.

## 8. Process Runtime

[ADR-0015](../adr/0015-process-runtime.md)

```mermaid
flowchart TB
  PX["process.exec<br/>executable · argv · cwd · env delta · ProcessEffect"] --> PEN
  SX["shell.exec · riesgo alto<br/>shell · script"] --> PEN
  PEN["Permission Engine<br/>IExecutableResolver · políticas estructuradas<br/>shell: Ask + AppContainer obligatorio"] --> L
  subgraph UPR["Unified Process Runtime · OmniCore.Execution"]
    L["ProcessLaunch<br/>ArgumentList · env allowlist + SecretRef"]
    SB["SandboxProfile<br/>JobObjectOnly · AppContainer"]
    H["IProcessHandle"]
    OUT["Output: canal acotado<br/>stdout/stderr + seq"]
    SP["Spool → Artifact CAS<br/>ring buffer para preview"]
    CX["Cancel: graceful → KillTree"]
    L --> SB
    SB --> H
    H --> OUT
    OUT --> SP
    H --> CX
  end
  SB -.->|"implementado por"| SBX["OmniCore.Sandbox<br/>AppContainer · Job Objects"]
  SP --> TR["ToolResult redactado"]
```

## 9. Model Provider architecture

[ADR-0005](../adr/0005-model-runtime-provider-native.md) · [ADR-0011](../adr/0011-conexion-a-proveedores.md)

```mermaid
flowchart LR
  subgraph NP["Protocolos nativos"]
    OR["OpenAI Responses API<br/>perfiles api · codex"]
    AM["Anthropic Messages API"]
    OC["Chat Completions<br/>ik_llama · llama.cpp · OpenRouter"]
  end
  subgraph AD["OmniCore.Models · provider adapters"]
    A1["OpenAIResponsesProvider"]
    A2["AnthropicMessagesProvider"]
    A3["OpenAiChatCompatibleProvider<br/>+ compat flags"]
  end
  subgraph REP["Representación OmniCore · Domain"]
    MR["ModelStreamEvent · ModelResponse<br/>ContentBlocks · Usage · StopReason"]
    PO["ProviderOpaque · ProviderState<br/>Sensitive · ReplayPolicy"]
  end
  OR <--> A1
  AM <--> A2
  OC <--> A3
  A1 --> MR
  A2 --> MR
  A3 --> MR
  A1 --> PO
  A2 --> PO
  MR --> AGR["Agent Runtime<br/>no conoce el provider"]
  PO -.->|"reenvío intacto solo a destino compatible"| A1
  PO -.->|"reenvío intacto solo a destino compatible"| A2
```

## 10. Contratos: ModelResponse, content blocks y estado opaco

Los contratos completos y la tabla "qué se normaliza, qué se persiste, qué entra al contexto y qué es sensible" están en [ADR-0005 §2–§4](../adr/0005-model-runtime-provider-native.md).

```mermaid
classDiagram
  class ModelResponse {
    ContentBlock[] Content
    StopReason StopReason
    TokenUsage Usage
    ProviderState State
    ProviderMetadata Metadata
  }
  class ContentBlock {
    <<abstract>>
  }
  class ProviderOpaque {
    ProviderFamily Family
    string ModelId
    string Kind
    ArtifactRef Payload
    ReplayPolicy Replay
  }
  ContentBlock <|-- TextBlock
  ContentBlock <|-- ToolCallBlock
  ContentBlock <|-- ToolResultBlock
  ContentBlock <|-- ReasoningBlock
  ContentBlock <|-- CitationBlock
  ContentBlock <|-- MediaBlock
  ContentBlock <|-- ProviderOpaqueBlock
  ModelResponse o-- ContentBlock
  ReasoningBlock --> ProviderOpaque
  ProviderOpaqueBlock --> ProviderOpaque
```

## 11. EffectiveModelProfile

[ADR-0007 §1–§3](../adr/0007-model-qualification-framework.md)

```mermaid
flowchart LR
  D["DeclaredCapabilities<br/>hechos: contexto · output · modalidades · tools"] --> RES
  H["HeuristicDefaults<br/>parámetros · MoE · familia<br/>provisional"] --> RES
  E["EmpiricalModelProfile<br/>traits por ModelQualificationKey"] --> RES
  U["UserOverrides<br/>siempre gana"] --> RES
  RES["ModelProfileResolver<br/>hechos = U ?? D<br/>traits = U ?? E si Qualified ?? H"] --> EMP["EffectiveModelProfile"]
  EMP --> HP["HarnessPolicyResolver · puro<br/>ToolCallFormat · ToolMode · MaxVisibleTools<br/>Guidance · Repair · PlanControl · StallThreshold"]
  EMP --> RT["Model Router"]
  HP --> CONS["Agent Runtime · ToolPlanner<br/>Context Engine · PlanService"]
```

## 12. Model Qualification Framework

[ADR-0007 §4–§7](../adr/0007-model-qualification-framework.md)

```mermaid
flowchart TB
  CLI["omni model inspect · omni model qualify --quick/--full"] --> QR
  subgraph Q["OmniCore.Qualification · M5 · cliente del runtime"]
    QR["QualificationRunner"]
    S["Suites versionadas quick · full<br/>BenchmarkIdentity"]
    SC["Scorers deterministas<br/>esquema · tests · exact match"]
    QR --> S
    S --> SC
  end
  QR -->|"Runs aislados · presupuesto · consentimiento"| ENG["Engine · Tasks/Lanes normales"]
  SC --> ST[("Model profile store · scope User<br/>model_profiles · model_traits<br/>resultados como artifacts")]
  TEL["Contadores del runtime<br/>parse failures · repairs · stalls · gate rejections"] -->|"calibración"| ST
  ST --> RESV["ModelProfileResolver · Models"]
  RESV --> RTR["Router · solo consume EffectiveModelProfile"]
```

## 13. Qualification states y ModelQualificationKey

```mermaid
stateDiagram-v2
  [*] --> Unknown
  Unknown --> Declared: metadata de provider o catálogo
  Unknown --> ProvisionallyClassified: heurística mínima, ya usable
  Declared --> ProvisionallyClassified: HeuristicDefaults
  ProvisionallyClassified --> Qualified: pasa la suite para su clave
  Qualified --> Calibrated: N muestras reales por trait
  Qualified --> Stale: nueva versión mayor de la suite
  Calibrated --> Stale: nueva versión mayor de la suite
  Stale --> Qualified: recualificación
```

`ModelQualificationKey`:

- **Campos:** provider, modelo, revisión o hash de pesos, cuantización, adapters, backend y build, hash de chat template, perfil del adapter, `ToolCallFormat`, `ToolMode` y versión del prompt profile.
- **Identidad:** SHA-256 de la serialización canónica.
- **Detalle y ejemplos:** [ADR-0007 §5](../adr/0007-model-qualification-framework.md).

## 14. Run → Plan y Run → TaskGraph

[ADR-0016 §1–§2](../adr/0016-plan-y-working-state.md)

```mermaid
flowchart TB
  RUN["Run"] --> PLAN["Plan rev.N<br/>progreso lógico · visible al usuario"]
  RUN --> TG["TaskGraph<br/>unidades técnicas · dependencias"]
  PLAN --> P1["P1 Inspect auth ✓"]
  PLAN --> P3["P3 Fix AuthenticationService →"]
  PLAN --> P4["P4 Update tests ○"]
  TG --> T191["T191 Explore auth"]
  TG --> T193["T193 Fix backend"]
  TG --> T196["T196 Migration"]
  TG --> T194["T194 Tests"]
  T191 --> T193
  T193 --> T194
  T193 --> L13["Lane L13 · local-worker"]
  P1 -. "Implements · req" .- T191
  P3 -. "Implements · req" .- T193
  P3 -. "Supports · req" .- T196
  P4 -. "Verifies · req" .- T194
```

La relación es N:M. Un PlanItem puede requerir varias Tasks, y una Task puede servir a varios items.

## 15. ProgressReconciler

[ADR-0016 §5, §9](../adr/0016-plan-y-working-state.md)

```mermaid
flowchart LR
  EV["Eventos canónicos<br/>Task · Lane · Turn · ToolCall · Validation"] --> PROJ["Proyecciones<br/>TaskGraph · Plan · Lanes"]
  PROJ --> PRC["ProgressReconciler · puro<br/>reglas R1–R7"]
  PRC -->|"PlanMutation · Cause=Reconciler"| PS["PlanService<br/>transición · coherencia · política"]
  MP["Propuestas del modelo<br/>plan.propose · AgentResult"] --> PS
  PS -->|"acepta"| E2["Eventos PlanItem"]
  PS -->|"rechaza"| E3["PlanMutationRejected<br/>→ resultado al modelo"]
  E2 --> EV
  PROJ --> WD["Watchdog<br/>N Turns sin señal de progreso"]
  WD -->|"ProgressStalled"| SP["StallPolicy<br/>Replan · Diagnose · Escalate · Split · Ask"]
```

| Regla | Disparador | Transición |
|---|---|---|
| R1 | Una Task vinculada pasa a `Running` | `Pending`/`Ready` → `InProgress` |
| R2 | Todas las Tasks requeridas `Completed`, y las `Verifies` OK | → `Completed` |
| R3 | Una Task requerida `Blocked` | → `Blocked` |
| R4 | Se desbloquea | `Blocked` → `InProgress` |
| R5 | Una Task requerida `Failed`, con recovery agotado | → `Failed` o `Blocked` según la `FailurePolicy` |
| R6 | Dependencias `Completed`/`Skipped` | `Pending` → `Ready` |
| R7 | El item actual es terminal | Se recalcula el item actual |

## 16. PlanCompletionGate

[ADR-0016 §10](../adr/0016-plan-y-working-state.md)

```mermaid
flowchart LR
  M["Modelo propone DONE"] --> G1
  subgraph CP["Completion Pipeline"]
    G1["AcceptanceCriteria"] --> G2["Build"]
    G2 --> G3["Test"]
    G3 --> G4["PendingTask"]
    G4 --> G5["PlanCompletion"]
    G5 --> G6["Validation"]
    G6 --> G7["WorkspaceConsistency"]
  end
  G5 -->|"item requerido no terminal"| NO["Continue<br/>items faltantes → modelo"]
  G7 --> OK["RunCompleted"]
```

| Estado de un item `Required` | ¿Permite completar? |
|---|---|
| `Completed` | Sí |
| `Skipped` / `Cancelled` por transición válida con política | Sí |
| `Pending`, `Ready`, `InProgress`, `Blocked` | **No** |
| `Failed` | El Run termina `Failed`, o `CompletedWithIssues` si la `FailurePolicy` es `AllowPartial` |

## 17. Plan y WorkingState en el Context Engine

[ADR-0016 §7](../adr/0016-plan-y-working-state.md) · [ADR-0017](../adr/0017-execution-fingerprint.md)

```mermaid
flowchart LR
  J["Canonical Journal"] --> WS["WorkingState · proyección<br/>objetivo · plan compacto · item y task actuales<br/>criterios · blockers · siguiente"]
  WS --> CI["ContextItem WorkingState<br/>Priority=Pinned<br/>Retention=RegenerateEachTurn"]
  OTHER["Otros contributors<br/>system · tools · skills · task · historia"] --> MAT
  CI --> MAT["Context Materializer<br/>presupuesto reservado"]
  MAT --> SNAP["ContextSnapshot · artifact<br/>+ ExecutionFingerprint"]
  SNAP --> MOD["Modelo"]
  COMP["Prune · Compress · Externalize · Compact"] -.->|"opera sobre"| OTHER
  COMP -.->|"nunca toca"| CI
```

- **Orden del contexto para el prompt caching:** system → tools → skills → task → historia → **WorkingState** → turno actual. El WorkingState cambia en cada Turn, así que va al final para no invalidar el prefijo cacheado.
- **Supervivencia:** sobrevive a compaction, rebuild, cambio de modelo y resume, porque se regenera desde el Journal.

## 18. Permission y Sandbox boundaries

[ADR-0008 §7](../adr/0008-sandbox-compartido.md) · [ADR-0009](../adr/0009-grafo-de-dependencias.md) · [ADR-0014](../adr/0014-pipeline-tool-permission-execution.md)

```mermaid
flowchart TB
  subgraph ABS["OmniCore.Abstractions"]
    IPV["IPathBoundaryValidator"]
    ATI["AuthorizedToolIntent<br/>ctor internal · IVT solo Security"]
  end
  subgraph SEC["OmniCore.Security"]
    PEN["Permission Engine"]
  end
  subgraph EXE["OmniCore.Execution"]
    ADP["SandboxPathBoundaryValidator"]
    PRT["Unified Process Runtime"]
  end
  subgraph SBX["OmniCore.Sandbox · sin deps OmniCore · compartida con OmniCoder"]
    PB["PathBoundary"]
    AC["AppContainer · Job Objects · ACL leases"]
  end
  subgraph TLS["OmniCore.Tools"]
    TR["Tool Runtime"]
  end
  ENG["OmniCore.Engine"] -->|"1 · autoriza"| PEN
  ENG -->|"2 · ejecuta"| TR
  PEN -->|"usa"| IPV
  PEN -->|"crea"| ATI
  TR -->|"solo acepta"| ATI
  ADP -.->|"implementa"| IPV
  ADP --> PB
  PRT --> AC
  HOST["OmniCore.Host"] -->|"registra"| ADP
```

**Sin aristas nuevas en el grafo:** Security → Abstractions; Execution → Sandbox (ya existía).

## 19. SecretProvider y redacción

[ADR-0018](../adr/0018-secretos-y-redaccion.md)

```mermaid
flowchart LR
  subgraph SRC["Fuentes"]
    CM["cred: Credential Manager · DPAPI"]
    EN["env: variables"]
    CMD["cmd: comando del usuario"]
  end
  CM --> SP["ISecretProvider"]
  EN --> SP
  CMD --> SP
  SP --> SEC["Secret<br/>no serializable · ToString = ***"]
  SEC --> AUTH["IAuthProvider · Models"]
  SEC --> PROC["Lanzamiento de procesos · env"]
  SP -->|"registra valores"| RED["ISecretRedactor<br/>valores conocidos + patrones"]
  RED --> S1["Serializador del journal"]
  RED --> S2["Writer de artifacts"]
  RED --> S3["ToolResult · provider → ContextItem"]
  RED --> S4["Logs · audit · telemetría"]
  RED --> S5["Errores tipados · headers HTTP"]
  X["Tools · Hooks · Context Engine"] -.->|"nunca reciben Secret"| SEC
```

## 20. CLI → Transport → Host → Engine

[ADR-0019](../adr/0019-cli-transport-host.md)

```mermaid
flowchart LR
  subgraph CLIP["omni · OmniCore.Cli"]
    PGM["Program.cs<br/>única composición"]
    APP["CliApp<br/>solo tipos de Protocol"]
  end
  subgraph PROT["OmniCore.Protocol"]
    CL["IOmniClient"]
    TP["IOmniTransport<br/>InProcess · Stdio · NamedPipe"]
  end
  subgraph HOSTP["OmniCore.Host"]
    SRV["OmniServer"]
    MAP["ProtocolMapper"]
  end
  ENG["Engine · vía Abstractions"]
  PGM -->|"CreateInProcessClient"| CL
  APP --> CL
  CL --> TP
  TP --> SRV
  SRV -->|"WireCommand → comandos"| ENG
  ENG -->|"DomainEvents"| MAP
  MAP -->|"WireEvents"| SRV
  OCD["OmniCoder · futuro<br/>adapter IAgentRuntime"] --> CL
```

---

## 21. Tabla de decisiones

| # | Decisión | Estado anterior | Cambio | Razón | Impacto M1–M10 |
|---|---|---|---|---|---|
| 1 | Fuente canónica | "Eventos = única fuente de verdad" (ADR-0001 rev. 1) | Canonical Journal = Event Store + artifacts inmutables content-addressed; estado en eventos, contenido en artifacts; retención por Session, GC y detección de corrupción | Snapshots, outputs y `ProviderState` viven en artifacts y también son historia | M1: envelope + tipos. M2: CAS simple. M4: GC y verify |
| 2 | Versionado | `EngineEvent.ProtocolVersion` mezclaba dos cosas | `EventType` + `EventSchemaVersion` (durable) separados de `ProtocolVersion` + `MessageType` (wire); upcasters y `ProtocolMapper` | Evolucionar CLI o protocolo sin migrar el store, y viceversa | M1: columnas + mapper mínimo. M9: negociación |
| 3 | Crash consistency | Resume por Turn completo (ADR-0004 rev. 1) | Effect Journal: `ToolCallId` estable, ciclo de vida durable, `EffectUnknown`, `ReconciliationPolicy` por clase | Repetir una tool con efectos no es seguro | M1: ciclo de vida en el dominio. M3: FS. M7: Git |
| 4 | Pipeline de tools | `ValidateAsync` + `ExecuteAsync(ToolCall)` | `Prepare` puro → `ToolIntent` → Permission → `AuthorizedToolIntent` (solo Security lo construye) → `ExecuteAsync` | Ejecutar sin autorización debe ser imposible por tipos | M1: contratos + `FakeTool`. M2: tools reales |
| 5 | Procesos | `shell.exec` como primitive | `process.exec(executable, argv[], cwd, env)`; shell como superficie de riesgo alto con `Ask` + AppContainer; runtime unificado | No interpretar strings de shell para autorizar | M1: tipos. M3: runtime |
| 6 | Model Runtime | Un provider OpenAI-compatible como base (ADR-0005 rev. 1) | Tres familias nativas (Responses, Messages, Chat-compatible); `ContentBlocks` + `ProviderOpaque`/`ProviderState` | No perder semántica de providers (firmas de thinking, reasoning cifrado) | M1: contratos en Domain. M2: Chat-compatible. M5: Responses y Messages |
| 7 | Perfil de modelo | 4 categorías por parámetros (ADR-0007 rev. 1) | Declared + Heuristic + Empirical + Overrides → `EffectiveModelProfile` → `HarnessPolicy` | El tamaño no predice el comportamiento (MoE, cuantización, tuning) | M2: resolver sin empíricos. M5: MQF |
| 8 | Estados de cualificación | — | `Unknown → Declared → ProvisionallyClassified → Qualified → Calibrated` (+ `Stale`) | Usable de inmediato y mejorable con datos | M2: estados. M5: suite |
| 9 | Identidad de cualificación | — | `ModelQualificationKey` (pesos, cuantización, backend, plantilla, adapter, `ToolMode`, prompt profile) | La cualificación es de una configuración concreta | M2: clave. M5: store |
| 10 | Suite de cualificación | — | Perfiles `quick`/`full`, deterministas, nunca automática, consentimiento en providers de pago | Cualificar sin tocar Router ni runtime | M5: quick. M10+: full y calibración |
| 11 | Security ↔ Sandbox | Contradicción entre ADR-0008 y ADR-0009 | `IPathBoundaryValidator` en Abstractions; implementación en Sandbox; adapter en Execution | Sin ciclos, sin duplicar, sin P/Invoke en Security | M2 |
| 12 | Plan como estado de primera clase | No existía | `Run → Plan` + `Run → TaskGraph`; `PlanItem ↔ Task` N:M | El seguimiento no puede depender de la memoria del modelo | **M1** |
| 13 | Modelo de Plan | — | `Plan`/`PlanItem`/`PlanItemLink`, 8 estados | Estado estructurado y reconstruible | **M1** |
| 14 | Mutaciones | — | El modelo propone `PlanMutation`; `PlanService` valida, aplica política y emite eventos | INV-002 aplicado al plan | M1: service. M2: `plan.propose` |
| 15 | Eventos de Planning | — | `PlanCreated` … `PlanItemReordered`, `PlanMutationRejected`, `ProgressStalled` | Reconstrucción y métricas | **M1** |
| 16 | Sincronización TaskGraph ↔ Plan | — | `ProgressReconciler` puro con R1–R7 | Sin depender de que el modelo "recuerde" | **M1** |
| 17 | Revisiones | — | `Plan rev.N`; impacto `Minor`/`Moderate`/`ScopeExpansion`/`RequiredSkip`, con `Ask` para las dos últimas | Cambios de scope visibles y controlados | M1: revisiones. M2: `Ask` |
| 18 | WorkingState | — | Proyección `Pinned` y regenerada en cada Turn, al final del contexto | Sobrevive a compaction, cambio de modelo y resume | M1: proyección. M2: en contexto |
| 19 | ProgressReconciler | — | Componente explícito tras cada cambio relevante | Coherencia determinista | **M1** |
| 20 | PlanCompletionGate | — | Nuevo gate en el pipeline; estado de Run `CompletedWithIssues` | El modelo propone DONE; el runtime decide | **M1** |
| 21 | Watchdog | — | `ProgressStalled` tras N Turns sin señal; `StallPolicy` | Evitar agentes girando en falso | M1: detección. M2: políticas |
| 22 | Trait de plan | — | `PlanTrackingReliability` en el MQF, que alimenta `PlanControl` | Ajustar cuánto controla el runtime según el modelo | M2: `PlanControl`. M5: medición |
| 23 | CLI de planning | `/plan` era un modo | `/plan` (vista lógica) y `/tasks` (vista técnica) desde la primera versión útil | Plan y TaskGraph son UX distintas | M1: `omni sim` + `/plan` `/tasks` |
| 24 | Durabilidad | WAL + `NORMAL` para todo | Commits Standard vs **Barrier** antes de efectos; sin ACID entre sistemas | El intento de efecto debe sobrevivir a un corte de energía | M1: contrato. M3: uso |
| 25 | `ArtifactRef` | Id, MediaType, Size, Kind | + `ContentHash`, `Sensitivity`; CAS | Integridad, dedupe, replay | M1: tipos. M2: CAS |
| 26 | Fingerprint | `ContextSnapshot.ModelDescriptorHash` | `ExecutionFingerprint` por componentes en `TurnStarted` | Explicar qué configuración recibió cada Turn | M1: tipo. M2/M5/M8: componentes |
| 27 | Secretos | `ICredentialStore` + redactor genérico (ADR-0011) | `ISecretProvider` + `Secret` no serializable + redactor obligatorio en cada sink | Imposible persistir un secreto por accidente | M1: journal. M2: resto |
| 28 | CLI y composition root | `OmniHost.RunAsync(CliClient.RunAsync)` | CLI → `IOmniClient`/`IOmniTransport` → Host → Engine, incluso in-process | Mismo CLI para InProcess, stdio y pipes | M1: in-process. M9: stdio |
| 29 | Hooks | Solo INV-014 | Niveles Core/Project/Plugin/External, capacidades explícitas, `HookResult` sin ampliación | Hooks de terceros sin acceso a secretos ni estado privado | M8 |
| 30 | Worktrees | `.omnicore/worktrees/<lane>` sin semántica de workspace sucio | `WorktreeBase` (por defecto `SnapshotOfWorkingTree`), worktrees fuera del repo, integración 3-way sin pisar cambios | La Lane ve lo que ve el usuario | M7 |
| 31 | Commands | Lista de `/` commands en el CLI | Subsistema formal (ADR-0024): `CommandDescriptor`/`Registry`/`Handler`; kinds Client, Engine, Prompt, Workflow, Extension; el Engine nunca interpreta `/`; precedencia con nombres reservados y restricción de confianza | Commands de extensiones y proyectos sin colisiones silenciosas; mismo catálogo para CLI y OmniCoder | M1: mínimo. M2: Prompt. M6: Workflow. M8: Extension y precedencia |
| 32 | Keybindings | No existía | `ClientAction` como unidad única; `KeyBindingService` con `When`/`Priority`/`Source`; Command Palette (ADR-0025), solo cliente | Un solo sistema de acciones para teclado, texto y palette | M1: interrupt y cancel. M10: configurable, palette |
| 33 | Skills | Formato + activación (spec §52–§53) | Scopes BuiltIn→Session; ciclo de vida `Discovered → Eligible → Activated → Loaded → Released`; costo cero si solo está instalada; precedencia con `sealed` y confianza (ADR-0026) | No consumir contexto por estar instalada; conflictos visibles | M8 |
| 34 | Extensiones | "Plugin" sin contrato | Paquete con manifest versionado; ejecución fuera de proceso vía Extension API (JSON-RPC); `TrustLevel` unificado Core/Trusted/Project/ThirdParty/Untrusted; capa `ExtensionBoundary` (ADR-0023) | No acoplar extensiones al Host ni a tipos .NET internos | M1: tipos. M8: host |
| 35 | Origen de tools | ToolCatalog sin origen | `ToolDescriptor.Source` + `Protection`; `ToolId` con namespace por origen; built-ins sensibles no sustituibles sin preferencia explícita + `Ask` (ADR-0027) | Ninguna extensión reemplaza en silencio `filesystem.write` | M1: tipos. M2: planner. M8: preferencias |
| 36 | Memory fuera del Core | §54: "no parte del Core" | Working Context (Context Engine) separado de Session/Project/Workspace/Global Memory; memoria solo vía `IContextContributor`; el runtime funciona sin ella (ADR-0028) | Núcleo determinista | M8+ |
| 37 | `MemoryRecord` | — | Contrato con scope, kind, importance, confidence, provenance, expiración y `Supersedes`; recuperación `retrieve → rank → dedupe → budget` | No last-write-wins | M8+ |
| 38 | Memory / Knowledge / Skills | Mezclados en §52–§54 | Tres conceptos con storage, ciclo de vida y políticas propios | Evitar que "todo sea contexto" sin reglas | M1: `ContextItemKind`s |
| 39 | Promoción de memoria | — | `Observation → MemoryCandidate → MemoryPolicy → Promote/Reject/Ask`; Global siempre `Ask`; Project requiere evidencia | El LLM no promociona solo | M8+ |
| 40 | Memory en el contexto | — | `MemoryContextContributor` con slot propio en `ContextBudget`; `WorkingState` pinned y separado | Cientos de memorias no entran al prompt | M8+ |
| 41 | Diagnósticos | `/context` sin origen | `ContextProvenance` en cada `ContextItem`; `/context` por categoría; `/commands`, `/keybindings`, `/skills`, `/extensions`, `/memory` (ADR-0029) | Explicar qué contribuyó a una ejecución sin exponer contenido sensible | M1: tipos. M2: `/context`, `/tools`. M8/M10: resto |
| — | Scopes e identidades | Jerarquía de §51 sin estrategias | `ScopeLevel`, `ProjectId` (repo) ≠ `WorkspaceId` (carpeta local), estrategia de resolución por subsistema (ADR-0022) | Listas de scopes distintas por subsistema; memoria multi-repo | **M1**: identidades para ubicar el journal |

## 22. Clasificación de abstracciones

| Necesaria desde M1 | Contract now / implementation later | Fully deferable |
|---|---|---|
| Ids fuertemente tipados (UUIDv7) | CAS en disco (M2) y GC (M4) | Dedupe entre workspaces |
| Envelope de evento (`EventType`, `EventSchemaVersion`, causation/correlation, `ArtifactRefs`) | `verify-journal` y `ArtifactRedacted` (M4) | Cifrado en reposo de `Sensitive` |
| `IEventStore` + `SqliteEventStore` + `InMemoryEventStore` | Commit Barrier en uso (M3) | Lease de escritor entre procesos (OAQ-2) |
| Session, Run (+ `CompletedWithIssues`), Task, TaskGraph, Lane, Turn y sus máquinas de estado | `ReconciliationPolicy` FS (M3) y Git (M7) | Reconciliación de APIs específicas |
| **Plan, PlanItem, PlanItemLink, PlanMutation, PlanService, ProgressReconciler, PlanCompletionGate, WorkingState, watchdog** | `plan.propose` y `PlanControl` (M2) | Clasificación semántica del impacto |
| Ciclo de vida de ToolCall, `EffectClass`, `EffectOutcome` | Tools reales (M2/M3), `IProcessRuntime` (M3) | Analizador Roslyn para `Prepare` |
| `ITool` (`Prepare`/`ExecuteAsync`), `ToolIntent`, `ResourceClaims`, `AuthorizedToolIntent` | `shell.exec` (M3) | PTY/ConPTY |
| Contratos de modelo en Domain (`ContentBlock`, `ModelResponse`, `ProviderOpaque`) | Adapters (M2/M5) | Media, citations, documentos |
| Tipos `EffectiveModelProfile` y `HarnessPolicy` | Resolver (M2), MQF, key y store (M5) | Traits no mínimos |
| `ExecutionFingerprint` (tipo) | Componentes reales (M2+) | `omni turn explain` |
| `SecretRef`, `Secret`, redactor en el serializador del journal | `ICredentialStore` y redacción completa (M2) | Detección heurística de secretos |
| `IOmniClient` + `InProcessTransport` + `ProtocolMapper` mínimo | Stdio (M9), negociación de versión | Named Pipes, clientes remotos |
| `IsolationPolicy` e `IsolationHandle` (solo tipos) | Worktrees (M7) | `DirectoryCopy` sin git |
| — | Hooks: niveles y capacidades (M8) | Firma de plugins, marketplace |
| `ScopeLevel`, `WorkspaceId`, `ProjectId` (v0.3) | `IScopeResolver<T>` con estrategias por subsistema (M2/M8) | Scope `Organization` |
| `TrustLevel`, `SourceKind`, `ComponentSource`; `ToolDescriptor.Source`/`.Protection` (v0.3) | Manifest y Extension API (M8), `ExtensionBoundary` (M8) | Carga in-process de `Trusted`, marketplace |
| Regla "el Engine no interpreta `/`", `CommandInvocation`, `ClientCommandRegistry` mínimo (v0.3) | `CommandRegistry` del Host + precedencia (M8), Prompt (M2), Workflow (M6), Extension (M8) | Completers avanzados |
| `ClientAction` + bindings de interrupt/cancel (v0.3) | `KeyBindingService` configurable, palette (M10) | Combinaciones concretas restantes |
| `ContextProvenance`, `ContributionCategory`, `ContextItemKind.Memory`/`Knowledge` (v0.3) | Skills (M8), Memory (M8+), `/context` por categoría (M2) | Búsqueda vectorial, dedupe semántico, diff de contexto |

## 23. Open Architecture Questions

Solo las decisiones realmente abiertas. Todo lo demás está decidido en los ADRs.

### OAQ-1 — Ubicación de los datos de runtime por workspace · **RESUELTA** (2026-09-24)

> **Decisión:** el usuario aprobó la opción B. Queda registrada en ADR-0022 §3. El análisis se conserva como referencia.

**Problema:** M1 crea el Event Store, y M2 el Artifact Store. Hay que decidir dónde viven el journal, los blobs, los worktrees (ADR-0021) y los grants con lifetime `Project`. La spec sugiere `.omnicore/` dentro del repo.

**Alternativas:**

| | A · `.omnicore/` dentro del repo | B · Directorio por usuario: `%LOCALAPPDATA%\OmniCore\workspaces\<workspace-id>\` | C · Una sola base global de usuario + blobs por workspace |
|---|---|---|---|
| Visibilidad | Alta, junto al código | Media (`omni doctor` muestra la ruta) | Media |
| Riesgo de commitear datos o secretos | **Alto** (depende de `.gitignore`) | Nulo | Nulo |
| Las tools del agente indexan sus propios datos y worktrees | Sí; hay que excluirlos en todas partes | No | No |
| Permisos del sistema operativo | Heredados del repo | ACL del perfil de usuario | ACL del perfil de usuario |
| Portabilidad con el repo | Se mueve con la carpeta | Requiere resolver la identidad del workspace | Igual que B |
| Purga por workspace | Borrar la carpeta | Borrar la carpeta | Consultas de borrado en una base compartida |
| Contención entre workspaces | Ninguna | Ninguna | Una sola base para todo |

**Recomendación: B.**

- **Estructura:** `%LOCALAPPDATA%\OmniCore\workspaces\<workspace-id>\{journal.db, blobs\, worktrees\}`.
- **Identidad del workspace** (*refinada en v0.3, ADR-0022*): `WorkspaceId` = primeros 16 hex de SHA-256 de la **ruta canónica de la raíz abierta**. Un registro `workspaces.json` mapea id → ruta, lo que permite re-vincular si la carpeta se mueve.
- **Identidad del proyecto:** `ProjectId` se deriva de la URL `origin` normalizada, del `git-common-dir` o de la raíz, y es compartido por todos los clones y worktrees. Sus datos no versionables (memoria de proyecto, grants `Project`) van en `%LOCALAPPDATA%\OmniCore\projects\<ProjectId>\`.
- **Workspaces multi-repo:** un workspace con varios repos tiene un solo journal y varios `ProjectId`.
- **Datos de scope User:** van en `%LOCALAPPDATA%\OmniCore\user.db`, que incluye perfiles de modelo (ADR-0007) y grants con lifetime `Session`/`User`.
- **`.omnicore/` en el repo** queda **solo** para configuración versionable del proyecto (settings, skills, hooks) y nunca para datos de runtime.

**Trade-offs:** se pierde "todo junto al repo" a cambio de seguridad (sin riesgo de commitear historia con contenido sensible) y limpieza. Además, ningún glob del agente tiene que excluir los datos de OmniCore.

**Decisión sugerida:** aprobar B.

### OAQ-2 — Escritor único por sesión entre procesos · DEFERABLE (hasta M9)

En M1–M8 un solo proceso escribe. Cuando CLI y OmniCoder puedan abrir la misma sesión, harán falta un lease en `journal.db` (tabla `session_leases` con heartbeat) o un lock file por sesión. Se decide con el Host separado (M9).

### OAQ-3 — Clasificación semántica de `ScopeExpansion` · DEFERABLE (M2+)

Las reglas estructurales de M1/M2 cubren los casos claros. Una clasificación semántica (por ejemplo, con el MetaModelService) se evalúa con datos reales.

### OAQ-4 — Defaults de retención · DEFERABLE (M4)

Cuánto se retienen las sesiones, las refs de snapshot (7 días propuesto) y la frecuencia del GC. No afecta al modelo de datos.

### OAQ-5 — Contenido y umbrales de la suite de cualificación · DEFERABLE (M5)

Qué probes componen `quick`, los umbrales de trait → `HarnessPolicy` y el tamaño de muestra para `Calibrated`. Los contratos ya están congelados.

### OAQ-6 — Momento de migrar OmniCoder a .NET 10 · DEFERABLE

Pendiente desde ADR-0008. No afecta a OmniCore.

### OAQ-7 — Capacidades reales de ik_llama por request · IMPLEMENTATION DETAIL (M2)

Hay que verificar en vivo `grammar`/`json_schema` por request, tool calls con `--jinja` y `/health`. El diseño ya lo trata como capacidad declarada y probada.

### OAQ-8 — Costo del commit Barrier en Windows · IMPLEMENTATION DETAIL (M3)

Hay que medir si conviene alternar `synchronous` o usar una conexión dedicada con `FULL`. La semántica ya está decidida.

### OAQ-9 — Interrupción limpia de procesos hijos en Windows · IMPLEMENTATION DETAIL (M3/M6)

`CTRL_BREAK_EVENT` con grupo de procesos nuevo, frente al interrupt por protocolo en las lanes de Claude Code. La semántica (graceful → kill del árbol) ya está decidida.

### OAQ-10 — Relación entre la Extension API y MCP · DEFERABLE (M8)

Para las tools, la Extension API podría ser un superconjunto de MCP (reutilizar su framing y su `tools/list`) o un protocolo propio con un adaptador MCP.

- **Qué ya está decidido:** la frontera (fuera de proceso, versionada, sin tipos .NET) y el manifest.
- **Qué falta:** el framing concreto se decide al implementar el host de extensiones.

### OAQ-11 — Derivación de `ProjectId` con varios remotos o sin `origin` · IMPLEMENTATION DETAIL (M1)

Por defecto se usa `origin`; si no existe, el primer remoto en orden alfabético; y si no hay remotos, el `git-common-dir`. Un override manual en `.omnicore/settings` permite fijarlo.

### OAQ-12 — Pesos de ranking y umbrales de promoción de memoria · DEFERABLE (M8+)

La fórmula `relevancia × importance × confidence × recencia × afinidad` y las N ocurrencias para promover a Project se calibran con datos reales. Los contratos ya están congelados.

## 24. Roadmap revisado M1–M10

**Principio:** vertical slices correctos sin hipotecar el diseño. Solo se implementa lo que el milestone usa; los contratos marcados como "Necesaria desde M1" se congelan.

| Milestone | Contenido | Criterio de salida |
|---|---|---|
| **M1 — Runtime sin IA + Planning** | Ids y primitives; envelope de eventos; `SqliteEventStore` + `InMemoryEventStore` (OAQ-1); Session, Run, **Plan, PlanItem**, Task, TaskGraph, Lane, Turn, ToolCall (ciclo de vida) con sus máquinas de estado; `PlanService`, `ProgressReconciler` R1–R7, watchdog; Completion Pipeline con `PendingTaskGate` y `PlanCompletionGate`; proyección `WorkingState`; contratos congelados de §22; `FakeTool` + `ScriptedModelProvider`; `IOmniClient` in-process; CLI `omni sim`, `/plan`, `/tasks`, `/events`; redactor en el serializador del journal; tests de arquitectura nuevos (IVT de Security, referencias IL del CLI) | Una ejecución simulada con Plan de varios items y un TaskGraph N:M se reconstruye **completa** desde el journal. Los tests deterministas de reconciliación Plan ↔ Task pasan. `PlanCompletionGate` bloquea un DONE prematuro. |
| **M2 — Explorer** | `OpenAiChatCompatibleProvider` (ik_llama) + `LocalModelHost` attach/managed; `ISecretProvider` + Credential Manager; resolver de `EffectiveModelProfile` (Declared + Heuristic + Overrides) y `HarnessPolicy`; Context Engine v1 con `WorkingState` fijo al final; `ContextSnapshot` + `ExecutionFingerprint`; pipeline de tools real; `filesystem.read/list` y `search.text`; Permission Engine v1 + `IPathBoundaryValidator` (Sandbox `PathBoundary`); CAS v1 + externalización básica; `plan.propose`; `PlanControl`; `StallPolicy`; `Ask` por `ScopeExpansion` | `omni "explícame este repositorio"` produce un Plan mantenido por el runtime, un `AgentResult` y Turns explicables por fingerprint |
| **M3 — Native Coder** | `filesystem.write/patch` con version tokens; Effect Journal + reconciliación FS; commits Barrier; `IProcessRuntime` + Job Objects + AppContainer; `process.exec` y luego `shell.exec`; tools de build y test; Build/Test/AcceptanceCriteria gates; reintentos y circuit breaker de providers | `omni act "corrige este test"`. Matar el proceso a mitad de un write y reanudar reconcilia sin duplicar el efecto |
| **M4 — Context management** | Prune, Compress, Externalize, Compact; `ContextCheckpoint`; MetaModelService; GC y `verify-journal` | Sesiones largas sin crecimiento ilimitado; el WorkingState sobrevive a la compaction |
| **M5 — Models + Qualification** | `OpenAIResponsesProvider` (api + codex, login ChatGPT); `AnthropicMessagesProvider` con `ProviderOpaque`; registro y alias; router + escalación; `OmniCore.Qualification` con suite `quick`; `ModelQualificationKey`, estados y store; `omni model inspect/qualify` | Cambiar de worker local a frontier sin modificar la Task; cualificar un modelo nuevo sin cambiar el runtime |
| **M6 — Multi-agent** | Descomposición, scheduler de lanes, lanes paralelas y background, heartbeat, `AgentResult`; lanes delegadas a Claude Code (ADR-0012) | Explore + Implement + Verify como Tasks diferenciadas, con el Plan reconciliado en paralelo |
| **M7 — Isolation** | Worktrees con `SnapshotOfWorkingTree`, integración 3-way, reconciliación Git, cleanup y recuperación | Coders concurrentes sin pisar el workspace ni los cambios del usuario |
| **M8 — Extensibility** | Hooks con niveles y capacidades, Skills, Scope Resolver, MCP | Un hook de proyecto puede restringir pero no ampliar, verificado por test |
| **M9 — Protocol + Host** | `StdioTransport`, negociación de `ProtocolVersion`, Host separado, lease entre procesos (OAQ-2), read models persistidos | El mismo CLI funciona in-process y por stdio sin cambios |
| **M10 — CLI v1 + evaluación** | CLI completo, suite `full`, calibración, regression suite agentic, comparación con Pi | Criterios de madurez de la spec §96 |

**Cambios frente a la spec v0.1:**

- Planning entra en M1.
- La persistencia pasa de M9 a M1.
- La cualificación se suma a M5.
- La integración del login ChatGPT queda en M5.
- Las lanes de Claude Code quedan en M6.
- M9 se reduce a protocolo y Host.

**Agregados de v0.3 al roadmap** (sin cambiar los criterios de salida):

| Milestone | Agrega |
|---|---|
| M1 | `ScopeLevel`, `WorkspaceId`/`ProjectId` (ubicación del journal); tipos `TrustLevel`/`SourceKind`/`ComponentSource`; `ToolDescriptor.Source`/`.Protection`; `ContextProvenance` + `ContributionCategory` + `ContextItemKind.Memory`/`Knowledge`; regla "el Engine no interpreta `/`" con test; `ClientCommandRegistry` mínimo; `ClientAction` + bindings de interrupt/cancel |
| M2 | `/context` por categoría y origen; `/tools` con origen; nombres visibles únicos en el `ToolPlanner`; `PromptCommand` desde `.omnicore/commands` |
| M6 | `WorkflowCommand`; tools `Dynamic` |
| M8 | Host de extensiones (`OmniCore.Extensions`) + manifest + `ExtensionBoundary`; `CommandRegistry` con precedencia + `ExtensionCommand`; ciclo de vida y precedencia de skills; `toolPreferences`; tools MCP; hooks con `TrustLevel` unificado; Session Memory + `MemoryContextContributor` + `memory.propose`; `/commands`, `/skills`, `/extensions`, `/memory session` |
| M10 | `KeyBindingService` configurable + `/keybindings`; Command Palette/TUI; Project/Workspace/Global Memory con promoción; `/memory project\|global` |

---

# Revisión v0.3 — extensibilidad, commands y memoria

## 25. Modelo de scopes

[ADR-0022](../adr/0022-modelo-de-scopes.md)

```mermaid
flowchart LR
  B["BuiltIn"] --> U["User · Global"]
  U --> P["Project<br/>ProjectId: identidad del repo"]
  P --> W["Workspace<br/>WorkspaceId: carpeta raíz local"]
  W --> S["Session"]
  S --> R["Run"]
  R --> T["Task"]
  T --> L["Lane"]
  W -. "contiene 1..N Projects" .-> P
```

| Subsistema | Estrategia de resolución |
|---|---|
| Configuración | El más específico gana, salvo claves `locked` |
| Permisos | Intersección |
| Commands, tools, skills | Especificidad + restricción de confianza + reservados/protegidos/`sealed` |
| Hooks | Unión ordenada; las restricciones se intersectan |
| Memoria | `retrieve → rank → dedupe → ContextBudget` |

## 26. Modelo de extensiones

[ADR-0023](../adr/0023-modelo-de-extensiones.md)

```mermaid
flowchart LR
  MAN["omnicore-extension.json<br/>id · version · omnicoreApi<br/>provides · requests · runtime · source"] --> INST["Instalación o confianza<br/>consentimiento de capacidades"]
  INST --> TL{"TrustLevel"}
  TL -->|"Core"| INP["in-process"]
  TL -->|"Trusted · Project"| JO["proceso · JobObjectOnly"]
  TL -->|"ThirdParty"| APC["proceso · AppContainer"]
  TL -->|"Untrusted"| OFF["deshabilitado o solo Observe"]
  JO --> API["OmniCore Extension API<br/>JSON-RPC stdio · semver"]
  APC --> API
  API --> EXH["OmniCore.Extensions · host"]
  EXH --> REG["Registries: Tools · Commands · Skills · Hooks<br/>Contributors · Providers · Validators · Gates"]
  REG --> SRC["ComponentSource<br/>kind · scope · trust · owner · version"]
  MAN -->|"requests.permissions"| EB["Capa ExtensionBoundary<br/>techo en la intersección de permisos"]
```

## 27. Commands, Client Actions y keybindings

[ADR-0024](../adr/0024-commands.md) · [ADR-0025](../adr/0025-client-actions-keybindings.md)

```mermaid
flowchart LR
  subgraph CLIENT["Cliente · OmniCore.Cli"]
    TXT["Texto '/review x'"] --> PAR["Parser de commands"]
    KEY["Teclado"] --> KBS["KeyBindingService<br/>When · Priority · Source"]
    PAL["Command Palette"]
    CCR["ClientCommandRegistry"]
    ACT["ClientAction"]
    INV["CommandInvocation"]
    CCR --- PAR
    PAR --> INV
    KBS --> ACT
    PAL --> ACT
    PAL --> INV
    INV -->|"ClientCommand"| ACT
  end
  subgraph HOSTC["Host"]
    CR["CommandRegistry<br/>Engine · Prompt · Workflow · Extension"]
    CSV["CommandService<br/>expansión · validación"]
  end
  INV -->|"otros kinds"| CSV
  ACT -->|"run.cancel · run.interrupt"| WC["WireCommand"]
  CSV --> WC
  CSV -->|"PromptCommand"| SI["SendInput o StartRun con Origin"]
  CR -->|"ListCommands"| PAR
  WC --> ENG["Engine · nunca interpreta '/'"]
  SI --> ENG
```

## 28. Ciclo de vida de skills

[ADR-0026](../adr/0026-skills.md)

```mermaid
stateDiagram-v2
  [*] --> Discovered: escaneo del scope, solo skill.yaml
  Discovered --> Eligible: activación coincide
  Discovered --> Activated: invocación explícita
  Eligible --> Activated: SkillSelector dentro de límites
  Activated --> Loaded: contribuciones materializadas
  Loaded --> Released: deja de ser relevante
  Released --> Eligible: vuelve a coincidir
  Eligible --> Discovered: deja de coincidir
```

El costo de contexto es cero en `Discovered` y `Eligible` (salvo la línea opcional del índice) y solo aparece en `Loaded`.

## 29. Origen y precedencia de tools

[ADR-0027](../adr/0027-tool-sources-y-precedencia.md)

```mermaid
flowchart TB
  REGT["Registro de tool<br/>ToolId con namespace por SourceKind"] --> DUP{"¿Id duplicado?"}
  DUP -->|"sí"| REJ["ToolRegistrationRejected"]
  DUP -->|"no"| CAT["ToolCatalog"]
  CAT --> PL["ToolPlanner"]
  PREF["toolPreferences explícitas · scope User"] --> PL
  PL --> PROT{"¿Reemplaza una built-in Protected?"}
  PROT -->|"sin preferencia"| KEEP["Se usa la built-in"]
  PROT -->|"con preferencia"| ASKP["Ask una vez → alternativa"]
  PL --> VIS["Nombres visibles únicos por ToolPlan<br/>mapeo → ToolId en el fingerprint"]
  VIS --> TRUST{"TrustLevel de la fuente"}
  TRUST -->|"ThirdParty"| ASK1["Ask en el primer uso"]
  TRUST -->|"Untrusted"| DIS["deshabilitada"]
  TRUST -->|"Core · Trusted · Project"| NORM["política normal"]
```

## 30. Memory

[ADR-0028](../adr/0028-memory.md) · [ADR-0029](../adr/0029-procedencia-de-contexto-y-diagnosticos.md)

```mermaid
flowchart LR
  subgraph CORE["Core determinista"]
    WSX["WorkingState · Pinned"]
    MAT["Context Materializer"]
  end
  OBS["Observaciones<br/>modelo · runtime · usuario"] --> CAND["MemoryCandidate<br/>scope propuesto · evidencia"]
  CAND --> POL{"MemoryPolicy"}
  POL -->|"Promote"| GM
  POL -->|"Ask"| USR["Usuario"]
  POL -->|"Reject"| RJM["MemoryRejected"]
  USR --> PM
  subgraph STORES["Memory stores · OmniCore.Memory"]
    GM["Global · User"]
    PM["Project"]
    WM["Workspace"]
    SM["Session"]
  end
  GM --> RET["retrieve"]
  PM --> RET
  WM --> RET
  SM --> RET
  RET --> RANK["rank"]
  RANK --> DED["dedupe"]
  DED --> BUD["ContextBudget · slot Memory"]
  BUD --> CIM["ContextItems Kind=Memory<br/>con procedencia"]
  CIM --> MAT
  WSX --> MAT
  KN["Knowledge · RAG"] --> MAT
  SK["Skills cargadas"] --> MAT
```

Memory, Knowledge y Skills entran por contributors distintos, con storage, ciclo de vida y políticas propios. El `WorkingState` no compite por el presupuesto de memoria.
