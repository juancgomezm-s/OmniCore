# ADR-0046 — Fronteras pre-M6: ejecución, routing autorizado, eventos y control

- **Estado:** Aceptada (2026-09-30), con los ajustes de §8 respecto al anexo de origen
- **Origen:** anexo "Delta pre-M6 de OmniCore" (necesidades de OmniCoder), contrastado con el código en
  [docs/architecture/gap-pre-m6.md](../architecture/gap-pre-m6.md)
- **Modifica:**
  - ADR-0036 §6, Turn de 1..N pasos;
  - ADR-0005 (`Continuation` dentro del Turn);
  - ADR-0011 §2, el router elige rutas;
  - ADR-0017, componentes obligatorios;
  - ADR-0024/0019, `CommandOutcome`;
  - ADR-0037 §7, topes configurables y autorización por Session.
- **Relacionado:** ADR-0001, 0004, 0012, 0016, 0021, 0034, 0035, 0043, 0044
- **Roadmap:** nuevo hito **M5.5 — Fronteras pre-M6**; el resto se reparte entre M6, M7, M9 y M10 (§9)

## Contexto

M6 introduce varios agentes concurrentes. El anexo pide congelar antes las fronteras difíciles de
cambiar. La auditoría muestra que parte ya existe, parte falta, y hay contradicciones entre el código
y ADRs vigentes: Turn multi-paso frente a ADR-0036 §6, estado opaco descartado, escalación que puede
gastar sin autorización y un fingerprint mal etiquetado. Abrir M6 sobre esas ambigüedades obligaría a
tener dos semánticas del mismo concepto, que es justo lo que el anexo prohíbe.

## Decisión

### 1. Ejecución lógica

- **Turn** es una interacción lógica completa orientada a una intención; contiene 1..N **ModelStep**,
  N ToolCalls y sus interacciones. La suspensión (`user.ask`, permiso) no crea otro Turn; la respuesta
  pertenece al mismo Turn. Una intención nueva crea un Turn nuevo. Esto ya es lo que hace el código; se
  corrige ADR-0036 §6 para admitir varios pasos.
- **ModelStep** es una invocación al provider. Nuevos eventos `model_step.started` / `model_step.completed`
  con `TurnId`, `StepIndex`, `ModelSelection` (con `RouteId`), referencia al `ContextSnapshot`, `Usage`,
  `StopReason` y referencia al artifact de respuesta, incluido el `ProviderState`. Se emiten **antes**
  de cualquier suspensión. `ModelCompleted` se mantiene como resumen del Turn (cambio aditivo).
- **ToolStep = ToolCall.** No se introduce un segundo nombre: el anexo dice ToolStep; en OmniCore es la
  `ToolCall` existente.
- **Input durante un Turn:**
  - `InteractionResponse` es la respuesta a una petición del Turn, y ya existe.
  - `FollowUp` es una intención encolada para un Turn posterior, con eventos
    `followup.queued` / `followup.promoted`.
  - `Steering` cambia la dirección del Turn activo, con eventos `turn.steering_received` / `applied` /
    `dropped`, y se aplica solo en fronteras de ModelStep.
  - El cliente declara cuál de los tres envía. El runtime no lo adivina: sin declaración es FollowUp,
    como ya hace ADR-0035 §1.

### 2. Identidad de ejecución

- `AgentProfile` es la configuración reutilizable, `Lane` el camino de ejecución de una Task y
  `AgentExecution` la instancia concreta. Se añade `ExecutionId` y el evento
  `agent_execution.started/completed/failed` con `LaneId`, `ProfileId`, `ParentExecutionId?`,
  `Relation (Awaited|Detached)` y `Supervision (Managed|Unmanaged)`. `TaskCreated` gana `ParentTaskId?`.
- Lineage, relación, supervisión y pertenencia a un join son ejes independientes.
- `JoinPolicy`, los joins, el mailbox y el wake se **nombran y congelan** como contrato ahora y se
  **implementan en M6**.
- `Delegation`, `Escalation` y `WakeRequest` son registros durables reconstruibles desde el journal,
  con eventos `*Created/Accepted/Returned|Resolved/Failed`. Las tres familias `ModelEscalation*`
  existentes ganan `TurnId?` y `LaneId?`; los eventos ya persistidos siguen siendo válidos.
- Invariante: `AgentExecutionCompleted ≠ AgentResultProduced ≠ ResultAccepted ≠ TaskCompleted`.
  `AgentResult` es inmutable; la evaluación es un `ResultDisposition` aparte. El rework produce un
  `AgentResult` nuevo. La aceptación explícita para ejecuciones Managed se implementa en M6.

### 3. Modelos, rutas y autorización de gasto

- **`ModelRoute`** = provider + endpoint + protocolo/perfil + nombre de modelo en el provider. Tiene sus
  capacidades y su dialecto efectivos. El router elige `Model + ModelRoute`.
  - **Migración:** cada `ModelDefinition` actual define su ruta por defecto 1:1, así que el YAML
    existente sigue cargando.
  - `RouteCandidate.Alias` pasa a `RouteId`.
  - El router consulta el precio y la disponibilidad real del circuit breaker (ADR-0011 §5).
- **Perfil por ruta:**
  - `EffectiveModelProfile` y `ModelQualificationKey` se asocian a la ruta.
  - La key gana `Endpoint`, `Protocol` y `RuntimeBuild` (nulables). Los perfiles existentes pasan a
    `Stale` en lugar de invalidarse en silencio.
- **Reasoning:** se añaden `ReasoningCapability` (capa Declared) y `ReasoningReplayPolicy`
  (`None | ProviderManaged | RequiredWithTools | PreserveAcrossSteps`). El adapter hace el round-trip
  exacto; el runtime guarda el `ProviderState` por ModelStep y lo reenvía dentro del Turn solo a la
  misma ruta y el mismo modelo.
- **`SessionRoutingPolicy`:**
  - Se congela al crear la Session con el evento `session.routing_policy_set`, que incluye `Revision`,
    `AllowedRoutes`, `BillingPolicy`, `CrossProviderRouting` y `SessionSpendLimit?`.
  - Cualquier cambio emite `session.routing_policy_revised`.
  - Cada provider declara su **`BillingMode`**: `Local | IncludedQuota | CreditBalance | MeteredCurrency | Unknown`.
    Sin declaración vale `Unknown`.
  - `MeteredCurrency` y `Unknown` no entran en routing ni escalación automáticos sin consentimiento
    explícito, que pasa por `InteractionRequest`. Así se cierra también el modo `ask` de escalación
    pendiente de M5.
  - **Tener credencial no autoriza a gastar.** Ningún agente, Task, skill, extensión, router ni
    supervisor puede ampliar el conjunto autorizado.
- **Topes (ADR-0037 §7):**
  - Los topes se leen de la configuración.
  - El diario se calcula sobre todas las sesiones del día.
  - Se añade la opción "continuar" vía `InteractionRequest`.
  - Las rutas `IncludedQuota` aplican la regla de cuota. Hoy Codex no tiene tope.

### 4. Envelope, orden y telemetría

- **Envelope:**
  - El `DomainEvent` existente **es** el EngineEvent del anexo; no se renombra.
  - Se rellena con un `ExecutionScope` ambiental (Task/Lane/Execution/Turn/ModelStep/ToolCall), por el
    mismo patrón que `CausationScope`.
  - Las ids del payload tienen prioridad y **no se eliminan** de los payloads v1, para no romper el schema.
  - Se añaden `Source` y una causation real: se elimina el fallback "último evento de esta instancia".
  - El timestamp pasa a UTC y `ArtifactRefs` se persiste y se lee.
- **Sequence:** se mantiene monótona **por Session**, no por Run (ajuste, §8). La reconexión es "after
  sequence N" por Session; el seq viaja en el `WireEnvelope` y las notificaciones del servidor tienen
  su propio orden y no se repiten.
- **Canonical vs Telemetry:** si eliminar un evento impide reconstruir estado, causalidad, efectos o
  una decisión aceptada, es canónico. Los deltas, el progreso, los heartbeats de pulso y las muestras
  van a un `ITelemetrySink` local, separado del `IEventStore` (ADR-0043 §2). Un test de arquitectura
  impide que pasen por `EventStream`.

### 5. Frontera de control

- `Event ≠ Decision ≠ RuntimeCommand`. Las decisiones (policy de producto, supervisor) viven fuera de
  Core y se traducen a `RuntimeCommand`.
- `RuntimeCommand` es de primera clase solo en la frontera autoritativa, es decir, en operaciones que:
  - cruzan cliente/host/producto → Core;
  - cambian lifecycle o estado;
  - requieren autorización o auditoría;
  - producen efectos.

  Los servicios internos siguen siendo métodos normales.
- **`CommandOutcome`:**
  - Valores: `Accepted | Rejected | NoOp | Deferred(Reason)`.
  - Se añade a `CommandAck` junto a `Status`, que se mantiene por compatibilidad, con `FirstSeq`/`LastSeq`
    para correlacionar los eventos resultantes.
  - Una aceptación no implica éxito inmediato.
- **Escrituras fuera de command:** las que hoy hace el Host fuera de un command (escalación,
  cuestionario, turno del Explorer, aprobación de plan) pasan a commands internos bajo `CausationScope`.
  Después `AcquireStore()`/`AcquireCodecs()` dejan de ser públicos.

### 6. Atribución de efectos para undo

- `toolcall.started` v3 gana `Reversibility (Reversible|Compensatable|Irreversible|Unknown)`, `TargetRef`
  y `BeforeStateRef?` (pre-imagen en el CAS, solo para escrituras reversibles de filesystem; el CAS
  deduplica).
- La atribución Task/Lane/Execution/Turn sale del `ExecutionScope` de §4.
- Undo nunca revierte eventos, solo efectos atribuibles a OmniCore, y nunca con primitivas destructivas
  (`git reset --hard`).
- El `WorkspaceSnapshotStore` y el restore físico quedan en **M7**.

### 7. Fingerprint

`ExecutionFingerprint` gana `Components: FingerprintComponent[]`, como ya exigía ADR-0017, con estos
componentes:

- ruta;
- perfil efectivo;
- política de contexto;
- plan de tools;
- prompt/plantilla;
- AgentProfile;
- skills;
- revisión del plan;
- build real del runtime.

Se corrige el `ContextPolicyHash`, que hoy recibe el id del tokenizer.

### 8. Ajustes respecto al anexo

1. **Sequence por Session, no por Run (B8).** El journal ya es por Session y solo hay 1 Run activo por
   Session (invariante 26): cada Run es una subsecuencia contigua del orden de la Session. Un segundo
   contador por Run no añade información y abre la puerta a dos órdenes que pueden divergir.
2. **Sin renombrar contratos existentes:** `DomainEvent` = EngineEvent, `ToolCall` = ToolStep,
   `ModelEscalation*` = Escalation. Renombrar violaría el principio del propio anexo: no dejar dos
   semánticas para un concepto.
3. **Ids duplicadas en los payloads v1.** No se eliminan, porque hacerlo rompería el schema. El envelope
   se rellena desde el `ExecutionScope`; los payloads v2+ ya no las añaden.
4. **Contratos de M6 congelados como tipos y eventos, no implementados en M5.5:** `JoinPolicy`, mailbox,
   wake, `SupervisionBinding` y `ResultDisposition`. Congelar significa tipos, nombres de eventos,
   upcasters y tests de serialización, sin la lógica.
5. **Supervisor externo en M6 = supervisor en proceso vía `IOmniClient`.** El transporte real llega en
   M9 (stdio); el test end-to-end de supervisión de M6 usa un supervisor fake en proceso que solo
   habla por commands y eventos.
6. **Validación avanzada en Core = registros, no políticas.** Core define `EvidenceRef` (claim vs
   respaldada por tool), `ValidationState`, `ValidationDebt` e `IntegrationStatus`. Detectar el wiring
   concreto (.NET, ABL) y producir un `ChangeRiskAssessment` corresponde a OmniCoder o a extensiones
   que publican esos registros. `ChangeRiskAssessment` pasa a M7+.
7. **HistoryProvenance en M9.** Hoy no se importa historial, así que no bloquea M6. Se reserva el campo
   (`SessionCreated` v2) para que el Protocol no asuma causalidad perfecta.

### 9. Plan

- **M5.5 fase A, bugs:** los nueve de [gap-pre-m6.md §4](../architecture/gap-pre-m6.md#4-bugs-que-no-dependen-del-anexo).
- **M5.5 fase B, contratos:**
  - §1 ModelStep + FollowUp/Steering;
  - §2 tipos y eventos de AgentExecution/lineage/registros;
  - §3 `ModelRoute` + perfil por ruta + reasoning + `SessionRoutingPolicy` + `BillingMode` + topes;
  - §4 `ExecutionScope` + telemetría;
  - §5 `CommandOutcome` + commands internos;
  - §6 `toolcall.started` v3;
  - §7 componentes del fingerprint.
- **M6:** herencia de contexto, FanOut/FanIn, joins, `AgentResult` por schema, `ResultDisposition`,
  Managed + binding + failure policy, mailbox + `WakePolicy`, presupuesto jerárquico + pool de Run,
  `CapacityScheduler`, lanes en paralelo/background.
- **M6/M7:** validación inmediata, niveles de completion, `IntegrationEvidence`, `ValidationDebt` durable.
- **M7:** `WorkspaceSnapshotStore`, restore, conflictos, compensación.
- **M9:** procedencia/fidelidad de historial y reconexión por stream.
- **M10:** `/undo` y proyecciones.

### Avance parcial de M6 — frontera de contexto (2026-10-08)

`ContextInheritancePolicy` implementa sólo `SelectedProjection`: IDs explícitos e
inmutables, selección vacía por defecto. `ContextInheritanceService` es un adapter
read-only del Host para una Task/Lane hija ya creada. Verifica Session/Run, lineage
directo, el recibo `ModelStepStarted` del padre y su snapshot CAS íntegro y atribuido.
No admite un snapshot arbitrario ni los antiguos snapshots multi-Lane que podían
contener transcripts mezclados. El snapshot nuevo declara `contextScopeVersion=1`
y sus IDs de Session/Run/Task/Lane/Turn como campos aditivos.

La selección admite mensajes visibles y hechos (`Decision`, `Constraint`, `Summary`)
de las categorías Conversation/Task y scopes compartidos Session/Run, no sensibles.
No importa System, WorkingState, skills, Memory, cuerpos de archivos, tools,
checkpoints o datos privados Task/Lane. Los items se materializan como datos
históricos sujetos al presupuesto del hijo, sin conservar pinning del padre.
La procedencia conserva evento/snapshot/item de origen, pero no copia las referencias
CAS que permitirían releer el snapshot completo del padre. El destino debe coincidir
exactamente con Session/Run/Task/Lane y no anteceder al recibo fuente.

El replay ordinario usa sólo la Lane destinataria. La conversación de Runs anteriores
pertenece al principal y no entra automáticamente a los hijos. La compactación genera
checkpoints con índice local y `laneId`; los checkpoints multi-Lane anteriores sin esa
marca se reconstruyen desde el journal, sin reescribirlo. Tools y respuestas legacy
se atribuyen por sus recibos Turn/ToolCall o por una única Lane inequívoca, nunca por
el último Turn observado.

`artifact.read` dentro de una ejecución resuelve raíces canónicas de su propia Lane
y edges de externalización/resumen escritos por Core. Texto de usuario/modelo/tool
que menciona un hash no crea autoridad para leerlo. Los snapshots/checkpoints antiguos
de atribución ambigua no se usan como capacidades de lectura. La consulta por Session
fuera de una ejecución conserva su frontera independiente. Esta corrección limita
visibilidad; no amplía permisos, perfiles ni routing.

La conexión durable usa `DelegationContextPacket` v1 en `Delegation.PacketRef`.
Es el payload de SelectedProjection, no el TaskPacket de aislamiento de M7.
La preparación aplica redacción, exige un límite explícito de bytes (máximo 1 MiB)
y no escribe al CAS ni al journal. El publisher del command debe publicar el handle
inmutable bajo su lease y journalizar la referencia exacta en `DelegationCreated`.
El adapter no ofrece una vía alternativa de admisión ni emite ese evento.

El consumidor exige `DelegationAccepted` canónico y valida el replay de lineage,
identidades Session/Run/Task/Lane/Profile/Execution, relación y supervisión, versión,
integridad CAS, límite y coincidencia de los hechos con el snapshot fuente. La fuente
se resuelve por EventId/secuencia; el paquete no exporta el hash del snapshot padre.
Un source sin ExecutionId sólo se vincula al owner si era la única ejecución iniciada
en esa Lane hasta el recibo fuente; con varios candidatos se rechaza, sin elegir el
último inicio. Si hay ExecutionId debe coincidir, y la fuente no puede anteceder al
inicio del owner. No se fabrica una atribución ExecutionId que el recibo no tenga.

`ExplorerTurn` incorpora automáticamente ese contributor sólo para la Lane hija de
un paquete aceptado de este formato, también tras reinicio. Dos aceptaciones activas
para la misma Lane son ambiguas y se rechazan; no se mezclan por recencia. Una
proyección cached deja de aportar items después de `DelegationFailed`/`Returned`.
Los hechos siguen sometidos al presupuesto del hijo y no desplazan su intención
actual. Los antiguos PacketRef genéricos no se reinterpretan como SelectedProjection.

Las pruebas recorren un `ExplorerTurn` real de padre e hijo con provider scripted,
SQLite reabierto, CAS y presupuesto. Ese bloque de contexto no acredita por sí solo
scheduler, joins, admisión por command, selección efectiva de perfiles ni supervisión
de M6. Sus hechos de aceptación se publican como fixtures, no workers operativos.
Tampoco adelanta Session Memory (M8) o Memory multiscoped (M10).
Evidencia: [validación de contexto](../validation/context-inheritance-20261008.txt).
Conexión packet: [validación de delegación](../validation/delegation-context-20261008.txt).

### Avance parcial de M6 — identidad, admisión a cola e inspección (2026-10-08)

El command interno de Ask registra el `AgentExecutionStarted` del principal de forma
lazy, dentro del primer Ask real, antes de su Turn/snapshot. Un callback sintético,
una excepción sin efectos o una interacción todavía pendiente no crea ejecutores.
La identidad se reutiliza entre Turns y tras reapertura; dos ejecutores posibles en
una Lane se rechazan por ambigüedad, no por recencia. El scope de Turn conserva la
identidad explícita que coincida con Task/Lane/Run y su inicio durable. Un inicio
registrado no es una medición de liveness, resultado aceptado o completion del Run.

`delegation.create` es una acción explícita del usuario a través de
`ITrustedUserActionClient`, no una tool del modelo ni una decisión basada en `origin`.
Este primer corte sólo admite un hijo directo del principal en **ORQ**, con límites
UltraCode vigentes y perfiles configurados/revisionados. ORQ estándar sin límites de
coordinación durables devuelve `Deferred(CoordinationLimitsUnavailable)`; no se
fabrica una autorización ilimitada ni se activa UltraCode automáticamente.

El perfil hijo debe ser de sólo lectura: sin escritura, procesos, shell, red o
secretos; sus reglas de lectura deben ser un subconjunto literal de las del padre
(no se infiere inclusión entre globs). El techo de perfil no concede permisos.
Los límites de agentes/profundidad y presupuestos explícitos del hijo se comprueban
contra los límites vigentes y los del Run. Se reutilizan los lectores canónicos de
consumo principal/meta y tokens; consumo desconocido o invocación abierta bloquea la
admisión. Los techos de todos los hijos admitidos se suman conservadoramente para
las siguientes admisiones, sin liberar slots implícitamente. No constituye una
reserva de `RunBudgetPool`: el principal mantiene sus guards actuales y el futuro
dispatcher debe revalidar recursos restantes; la cola no garantiza presupuesto
o capacidad de ejecución futuros.

La fuente es un EventId exacto de `ModelStepStarted`, atribuido al principal del Run
actual. La preparación valida la selección antes de crear la Task/Lane. El publisher
publica el packet bajo lease y confirma **TaskCreated + TaskReady + LaneCreated +
DelegationCreated** en un único batch Barrier con scopes padre/hijo separados.
`Accepted` en el ACK significa admisión a cola; no se emite `DelegationAccepted`,
no hay `AgentExecutionStarted` hijo y no se llama a ningún proveedor. Reutilizar un
CommandId ya escrito se rechaza y remite a inspección; nunca crea otro hijo. Una
excepción posterior al commit devuelve ACK `Accepted` con error y rango durable,
no una falsa afirmación de rollback. Un blob publicado sin batch queda sin raíz
canónica y sujeto al GC existente, no se borra en una compensación improvisada.

`agents` proyecta Task/Lane/Profile/Execution/Delegation y modelo observado por Lane,
con secuencia de journal, fuente de contexto e IDs elegibles, sin exportar contenido
privado. El cliente descarta snapshots de otra Session/anteriores y los limpia al
cambiar sesión. La TUI incorpora `core.agents`, `/agents` y `/delegate <JSON>`; la
Lane hija muestra **en cola · sin worker**. El inspector no mezcla transcripts hijos
con el chat principal ni deduce progreso de un PID/heartbeat.

Pendientes: scheduler/capacidad y release de reservas, dispatch hijo con su routing
y permisos efectivos, joins, binding/supervisión y aceptación de resultados,
formularios de delegación/inspector de transcript. No se declara M6 completo, ni
se adelanta el aislamiento TaskPacket/worktrees de M7 o Memory de M8/M10.
Evidencia: [admisión e inspección](../validation/agents-admission-20261008.txt).

## Criterios de salida de M5.5

Deben quedar cubiertos por tests deterministas:

- Un Turn con 3 pasos y una suspensión produce 3 `model_step.completed`, un único `TurnId` y el uso
  completo en el gasto derivado.
- El `ProviderState` se reenvía dentro del Turn a la misma ruta y nunca a otra.
- Una Session `Local` no puede, ni por routing ni por escalación `auto`, seleccionar una ruta
  `MeteredCurrency`/`Unknown`: termina en `InteractionRequest` o en `Deferred`.
- Todo evento de una ToolCall lleva Task/Lane/Turn en el envelope; el timestamp es UTC y `ArtifactRefs`
  sobrevive al round-trip.
- Ningún componente fuera de Engine/Host-server escribe el journal: lo verifica un test de arquitectura.
  Cada command devuelve un `CommandOutcome` correlacionable.
- Los eventos congelados de M6 serializan y se reproducen, aunque no se emitan todavía.
- Los deltas de streaming nunca llegan al `IEventStore`: lo verifica un test.

## Consecuencias

- M6 empieza con una sola semántica para Turn, ruta, envelope y command.
- M5.5 añade eventos y versiones de schema (upcasters) sin romper journals existentes.
- El coste es retrasar M6 a cambio de no tener que migrar el protocolo con varios agentes en marcha.
- Las políticas sofisticadas (fairness, preemption, riesgo) siguen pudiendo evolucionar en M6+ sin
  cambiar estos contratos.
