# Gap analysis pre-M6: anexo "Delta pre-M6" contra el código real

- **Fecha:** 2026-09-30, sobre `m1/journal-estado` @ `4b52427` (M5 con código completo).
- **Método:** tres auditorías de solo lectura (ejecución/lifecycle, modelos/routing, eventos/commands/efectos). Los hallazgos
  marcados con ✔ los verifiqué directamente en el código.
- **Decisión resultante:** [ADR-0046](../adr/0046-fronteras-pre-m6.md); roadmap en [arquitectura.md §24](arquitectura.md#24-roadmap-m1m10-fuente-de-verdad).

Clasificación: **Hecho** · **Parcial** · **Falta** · **Conflicto** (el código o un ADR contradice el anexo, o el código contradice un ADR).

## 1. Semántica de ejecución

| Punto del anexo | Estado | Evidencia y diferencia |
|---|---|---|
| B1 Turn = interacción lógica | **Conflicto (documental)** | El código ya es multi-paso: un `TurnStarted`, hasta `MaxSteps = 8` llamadas al modelo con tools, un `ModelCompleted` y un `TurnCompleted` (`ExplorerTurn.cs:247-566`). ADR-0036 §6 se lee como "una respuesta por Turn" y ADR-0005 (`Continuation` = "estado del Turn anterior") también. |
| B1 ModelStep | **Falta** | No hay registro por llamada. Fingerprint y `ContextSnapshotRef` solo en `TurnStarted`; el contexto se rematerializa en cada paso pero solo se persiste el primero; el `Usage` se suma y el `StopReason` no se journaliza. |
| B1 Suspensión sin nuevo Turn | **Hecho** | `user.ask`/permiso → `InputRequired` sin evento terminal; `FindOpenTurn` reanuda el mismo `TurnId` (`ExplorerTurn.cs:242-251, 975`). La espera es actividad derivada (ADR-0045 §8). |
| B1 Uso perdido al suspender | **Bug** | Los pasos previos a `InputRequired` no emiten `ModelCompleted`: el gasto derivado del journal se queda corto. |
| B2 Steering / FollowUp | **Parcial** | `SendInput` es follow-up implícito (ADR-0035 §1). No hay steering ni cola con eventos. ✔ **Bug:** al reanudar un Turn abierto, el texto nuevo se descarta sin journalizar (`safeQuestion = isResume ? "" : …`, `ExplorerTurn.cs:209`). |
| B10 AgentProfile ≠ AgentExecution ≠ Lane | **Parcial** | Lane implementada (ADR-0036 §3). `AgentExecution` está en la jerarquía documental (`Lane → AgentExecution → Turn`, ADR-0012 §1) pero no existe en código. `ProfileId` es siempre `ProfileId.New()`. |
| B11/B12 Lineage, Awaited/Detached, Managed, Join | **Falta** | Sin `ParentExecutionId`, sin `ParentTaskId`, sin profundidad. ADR-0035 §2 dice que los subagentes son Tasks hijas; no está modelado. |
| §10 Escalation | **Parcial** | `ModelEscalationRequested/Approved/Completed` existen pero solo con `RunId`; el modo `ask` nunca se resuelve. |
| §10 Delegation / Wake | **Falta** | `ExecutionStrategy.Delegated` existe sin uso; Wake no existe ni en docs. |
| §13–14 AgentResult / ResultDisposition | **Parcial** | `AgentResult` existe (`AgentResult.cs:21`) sin `EvidenceRefs`. ✔ Nunca se llena: `LaneCompleted(lane, null)` / `TaskCompleted(task, null)` (`RunCoupon.cs:86-264`). "Agente terminó", "resultado aceptado" y "Task completada" no se distinguen; solo el Run tiene aceptación/rechazo. |
| §21–28 Validación | **Parcial** | Gates: Plan, PendingTask, Lane, build/test/acceptance, post-edit. `PendingEditValidation` vive **solo en memoria** (se pierde al reanudar) y la limpia cualquier build/test sin relación con los archivos. Sin `EvidenceRef` claim vs tool-backed. |

## 2. Modelos, routing y gasto

| Punto | Estado | Evidencia y diferencia |
|---|---|---|
| B3 Model ≠ Provider ≠ Route | **Falta** | `ModelDefinition` tiene un único `ProviderId`; no existe ruta. El router recibe ids de modelo en el campo `Alias` (conflicto con ADR-0011 §2). El mismo modelo por dos providers exige dos ids. El precio se calcula pero el router no lo lee; `Available` es siempre `true` (el circuit breaker no alimenta al router, ADR-0011 §5). |
| B4 Perfil por ruta | **Parcial** | Capas Heuristic + Empirical + Overrides implementadas; Declared parcial (modalidades fijas `text`), overrides siempre `null`. `ModelQualificationKey` no tiene endpoint, protocolo ni build del runtime, y en la práctica solo rellena provider + modelo + formato. |
| B5 Reasoning | **Conflicto con ADR-0005 §4** | Los adapters capturan el estado opaco, pero ✔ el runtime manda siempre `Continuation = null` (`ExplorerTurn.cs:273-279`) y ✔ reconstruye el mensaje del asistente solo con el `ToolCallBlock` (`:506`). Romperá Anthropic con thinking + tools. `ReplayPolicy` existe como tipo sin uso. |
| B6 SessionRoutingPolicy | **Falta** | Nada congela rutas autorizadas por Session ni modela `BillingMode`. ✔ La escalación `auto` aprueba cualquier modelo de la cadena sin mirar precio ni credencial (`OmniCliRuntime.cs:905-925`). |
| Topes de gasto (ADR-0037 §7) | **Conflicto** | 5 USD/sesión y 20 USD/día fijos en código, solo con API key; el "diario" suma solo la sesión actual; sin opción de continuar; **la suscripción Codex no tiene tope ni regla de cuota**. Cache y reasoning tokens no entran en el costo. |
| 35.3 ExecutionFingerprint | **Conflicto con ADR-0017** | ✔ `ContextPolicyHash` recibe el id del tokenizer; toolkit constante; `Build` = "M2"/"M3"; `ModelKey` = id del modelo. Sin componentes nombrados. |
| §19 Budgets | **Parcial** | `TaskBudget` + `SpendGuard` existen pero todo se crea `null`; límites fijos `MaxSteps = 8` y `maxTurns = 16`. Sin jerarquía ni pool. |
| §20 Capacity | **Falta** | Ni scheduler, ni `WaitingForCapacity`, ni slot de GPU, ni lease de escritura. |

## 3. Eventos, control y efectos

| Punto | Estado | Evidencia y diferencia |
|---|---|---|
| B7 Envelope | **Parcial** | `DomainEvent` ya tiene EventId, SchemaVersion, SessionId, RunId, Causation, Correlation y columnas Task/Lane/Turn/PlanItem/ToolCall. Pero: ✔ timestamp `DateTimeOffset.Now` (ADR-0001 exige UTC); ✔ `ArtifactRefs` se escribe vacío y se descarta al leer; las ids de ejecución solo se copian si están en el payload (los `toolcall.*` quedan sin Turn/Lane/Task); ✔ la causation por defecto es "el último evento de esta instancia de stream", no una causa real; Correlation = RunId siempre; sin `Source`. |
| B8 Sequence | **Parcial / ajuste** | Monótona **por Session** (índice único `(session_id, seq)`). Ver ADR-0046 §4: se conserva por Session. Las notificaciones del servidor no tienen seq y se repiten al reconectar; el seq viaja en el payload, no en `WireEnvelope`. |
| B9 Canonical vs Telemetry | **Hecho en espíritu** | Los deltas de streaming nunca se journalizan. Pero no existe canal de telemetría (ADR-0043 §2 sin implementar). |
| B13–B15 RuntimeCommand + Outcome | **Conflicto** | `CommandAck` es binario (`ok/error`), sin Accepted/Rejected/NoOp/Deferred ni rango de eventos. Commands como JSON con `cmd` string. ✔ `OmniServer.AcquireStore()/AcquireCodecs()` son públicos y `OmniCliRuntime` escribe el journal directamente (escalación, cuestionarios, ExplorerTurn, aprobación de plan) fuera de un command. El CLI referencia Host (permitido por ADR-0009, en tensión con ADR-0019 regla 2). |
| B16 EffectRecord | **Parcial** | El Effect Journal es `toolcall.*` con `EffectClass` y `ReconciliationJson` (pre/post hash solo para filesystem). Sin reversibilidad, sin pre-imagen restaurable, sin atribución Turn/Lane/Task. |
| B17 Snapshots | **Falta** | `SnapshotOfWorkingTree` (ADR-0021) solo en docs, M7. |
| §31 HistoryProvenance | **Falta** | Nada distingue historial nativo de importado. |
| §32 Observabilidad | **Parcial** | Atribución por Run fiable; por Task/Lane/Turn en tool calls solo reconstruible por tiempos (frágil); causalidad fiable solo bajo command. |

## 4. Bugs que no dependen del anexo

Se corrigen primero, en la fase A de M5.5 (ADR-0046 §9):

1. Timestamp no UTC en el envelope (ADR-0001).
2. `ArtifactRefs` del envelope vacío y descartado al leer.
3. `ContextPolicyHash` del fingerprint = id del tokenizer (ADR-0017).
4. Estado opaco descartado dentro del Turn y `Continuation = null` (ADR-0005 §4).
5. Texto del usuario descartado al reanudar un Turn abierto.
6. Uso de los pasos previos a una suspensión no contabilizado.
7. Escalación `auto` sin comprobar precio ni autorización.
8. Tope "diario" que solo suma la sesión actual; Codex sin tope (ADR-0037 §7).
9. `PendingEditValidation` solo en memoria.
