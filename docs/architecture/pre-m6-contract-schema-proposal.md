# Contratos agrupados pre-M6: implementación y validación

Estado: **implementado en WIP, validación integral pendiente; no cierre de M5.5**.
El [contrato efectivo](pre-m6-record-contracts.md) describe los campos y garantías
implementados; la propuesta inferior se conserva sólo como antecedente y no es
el schema vigente. En particular, el receipt real es ToolCallSucceeded, no el
ToolCallCompleted mencionado en la propuesta original.
Nota de implementación 2026-10-07 13:19 UTC: ADR0046 §8.4 ya autoriza concretar
las representaciones técnicas compatibles; no requiere otra aprobación por campo.
Root está implementando el conjunto en PreM6Contracts/Events.PreM6/PreM6Ids y
EventCodecs. Aún es WIP, no cierre verificado. La tabla inferior conserva la propuesta
original para trazabilidad: el código reemplaza SourceEventId por EvidenceEventRef
(SessionId + EventId), ScopeRef por ValidationScope tipado y añade ResultSchemaId.
Se documentará el schema final y sus pruebas antes de llamarlo congelado.
Actualización 13:38 UTC: codecs y proyección factual pasan 11 controles focales,
incluyendo SQLite reopen, las 23 familias, aislamiento de envelope, referencias,
idempotencia y rechazo atómico. La primera ejecución detectó 1 fallo real en
la comparación por referencia de EventType; se corrigió sin cambiar assertions.
La suite completa terminó: 2882 casos, 2878 PASS, 0 FAIL y 4 SKIP por permisos
de symlink; arquitectura 56 PASS tras build actualizado sin advertencias ni errores.
Estos controles usan fixtures, no ejecución de
workers ni consultas autenticadas. La proyección no verifica bytes CAS por sí
misma: comprueba forma de ArtifactRef y referencias a receipts canónicos; el
fixture SQLite/CAS verifica por separado integridad y contenido de los blobs.
Fecha: 2026-10-07. Fuente normativa: ADR0046 §2/§8, ADR0047 y arquitectura §24.
No modifica esos ADR ni autoriza lógica de scheduler, joins o delegación M6.

El ADR fija conceptos y algunos valores, pero no los campos de todos sus registros.
Este documento concreta una propuesta única para revisar el bloque completo;
no presenta nombres sueltos como implementación ni inventa aprobación del propietario.

## Fronteras que ya están aceptadas

- AgentProfile reutilizable, Lane y AgentExecution no son sinónimos.
- Relation Awaited/Detached, Supervision Managed/Unmanaged, lineage y join son ejes
  independientes. Ninguna combinación se deduce de otra.
- AgentExecutionCompleted, resultado producido, resultado aceptado y TaskCompleted
  son hechos distintos. Rework produce otro resultado, no muta el anterior.
- ExecutionJoin admite All/Any/Quorum/Explicit según arquitectura §24.
- SupervisionFailurePolicy propone Wait por defecto, sin degradación implícita.
- Core conserva registros de validación; no detecta wiring por lenguaje ni decide riesgo.
- Campos del envelope, sequence por Session, UTC y causation siguen el contrato existente.

## Propuesta de campos y eventos v1

Las identidades nuevas se proponen como wrappers UUIDv7, igual que ExecutionId.
SessionId/RunId siguen en el envelope; las referencias a otra sesión deben
llevar SessionId explícito. Ninguna identidad de destino se toma de la selección UI.
`PacketRef` y `ResultRef` son ArtifactRef existentes, no transcripts embebidos.
Las siguientes shapes son propuestas de representación: se contrastan con los ADR
y se verifican antes de declararlas schemas congelados. No requieren un nuevo
approval gate para campos técnicos que no cambian semántica ni política aceptada.

| Registro | Campos propuestos | Eventos propuestos |
|---|---|---|
| Delegation | DelegationId, ParentExecutionId, ChildTaskId, ChildLaneId, ProfileId, PacketRef, Relation, Supervision; ChildExecutionId nullable hasta aceptación | `delegation.created` con registro; `.accepted` con ChildExecutionId; `.returned` con ResultRef; `.failed` con razón |
| ExecutionJoin | JoinId, OwnerExecutionId, MemberExecutionIds, JoinPolicy | `execution_join.created`; `.resolved` con miembros que satisfacen la decisión; `.failed` con razón |
| JoinPolicy | Kind All/Any/Quorum/Explicit; RequiredCount sólo para Quorum; RequiredExecutionIds sólo para Explicit | Forma anidada, no otro lifecycle |
| SupervisionBinding | BindingId, SubjectExecutionId, SupervisorExecutionId, PolicyRevision, FailurePolicy | `supervision_binding.created`; `.accepted`; `.failed` con razón |
| ExecutionMailbox | MailboxId, OwnerExecutionId; mensajes inmutables con MessageId, SenderExecutionId nullable, ContentRef y CorrelationId nullable | `execution_mailbox.created`; `execution_mailbox.message_received`; `.message_acknowledged` |
| WakeRequest | WakeRequestId, TargetExecutionId, SourceEventId, Reason, PolicyRevision; MessageId nullable | `wake_request.created`; `.accepted`; `.resolved`; `.failed` con razón |
| AgentResult producido | ExecutionId, ResultRef, ResultRevision, SupersedesResultRef nullable | `agent_result.produced` |
| ResultDisposition | DispositionId, ExecutionId, ResultRef, Outcome Accepted/Rejected/ReworkRequested, EvaluatorExecutionId nullable, Reason, EvidenceRefs | `result_disposition.recorded`; no emite TaskCompleted |

Semántica propuesta a revisar:

- Miembros de join únicos y no vacíos; Quorum entre 1 y el número de miembros;
  Explicit exige una lista no vacía contenida en MemberExecutionIds. Otros modos
  rechazan campos de política que no les pertenecen.
- ResultRevision positiva y monotónica por ExecutionId. Rework sólo referencia
  la entrega anterior; nunca reutiliza su ResultRef para contenido modificado.
- Una entrega de mailbox recibida y su ACK son hechos distintos. Repetir MessageId
  idéntico es idempotente; el mismo ID con contenido distinto es inválido.
  El transporte puede repetir; el consumidor lógico no debe duplicar la entrega.
- Una resolución de wake sólo significa que la petición fue atendida; no implica
  éxito de ejecución, aceptación de resultado ni TaskCompleted.
- Binding aceptado no amplía permisos, rutas ni autoridad del sujeto. FailurePolicy
  no autoriza convertir Managed en Unmanaged. El algoritmo de respuesta es M6.
- Escribir/leer estos eventos para pruebas no inicia workers, no despierta agentes
  ni evalúa cuándo se cumple un join. Los productores de runtime son M6.

## Evidencia y validación: propuesta de shapes

| Tipo | Campos propuestos y significado |
|---|---|
| EvidenceRef | Kind Claim/ToolBacked, Summary, ArtifactRef nullable; ReceiptRef nullable con SessionId, EventId, ToolCallId. ToolBacked exige receipt concreto; Claim no adquiere autoridad por incluir un enlace. |
| ValidationState | Level Local/Integration/Project/EndToEnd; Status Unknown/Pending/Passed/Failed; EvidenceRefs; RequiredChecks; ScopeRef. Passed no significa que otro nivel también pasó. |
| ValidationDebt | DebtId, SubjectExecutionId, ScopeRef, RequiredLevel, MissingChecks, EvidenceRefs; registro separado de estado resuelto |
| IntegrationStatus | Unknown/NotIntegrated/Integrated/Verified; ScopeRef, EvidenceRefs. Integrated declara cableado; Verified requiere evidencia explícita para ese scope. |

`ScopeRef` se propone como ArtifactRef de alcance inmutable, no como path libre
con autoridad de filesystem. Si el alcance no puede resolverse, es desconocido.
Eventos propuestos: `validation_state.recorded`, `validation_debt.created`,
`validation_debt.resolved` e `integration_status.recorded`.
La verificación de receipt enlazará ToolCallCompleted durables de la sesión
identificada y artifacts válidos; un texto del modelo no constituye esa prueba.
No sustituye `PostEditValidationPending/Consumed`, ya existentes para otro nivel.

## Compatibilidad y migración propuesta

- No renombrar ModelEscalation ni reconstruir una Delegation a partir de una Task
  histórica. Journals sin estas familias se siguen reproduciendo sin estado nuevo.
- Familias nuevas comienzan en schemaVersion 1: no existe v0 que upcastear.
  Versions desconocidas fallan por el mecanismo existente; no se descartan silenciosamente.
- AgentResult mantiene su constructor, Findings string y FilesChanged string
  actuales. EvidenceRefs se añadiría nullable: ausencia legacy = no disponible,
  no lista de evidencias vacía supuestamente medida. El cambio a Findings/PathRef
  tipados de spec §16 necesita versión explícita, no una conversión silenciosa.
- No elevar un AgentResult histórico a Accepted, ni crear bindings por su Relation.
- Arrays y colecciones son copias inmutables; rechazar IDs vacíos, duplicados,
  valores de enum desconocidos y referencias con scope incompatible.

## Pruebas requeridas para congelar el contrato

Un mismo bloque deberá cubrir codecs de cada evento, igualdad semántica tras
JSON y SQLite reopen, ArtifactRefs/transitividad CAS, payload/envelope scope,
legacy sin nuevos campos, rechazo de versions/enum inválidos, idempotencia de
identidad, conflictos de contenido y snapshots sin colecciones mutables.
La prueba de replay reconstruye sólo registros y lifecycle; no ejecuta joins,
mailbox, wake ni supervisores. También debe demostrar que terminar una ejecución
no produce automáticamente resultado, disposition o TaskCompleted.

## Relación con el código C# de OmniCoder

`DelegationDeliveryStore` ya separa almacenamiento, despacho y ACK con identidad
run/task/resultRevision, y usa ParentSessionId estable, no la selección UI.
Se reutiliza ese principio; no se copia su store de JSON como segundo journal Core
ni su recuperación de corrupción que ignora archivos inválidos.
`DelegationApplicationService` deduplica request+fingerprint: se conserva el
principio de conflicto explícito, no su orquestador como scheduler adicional.
Su EvidenceRef local `(Id, Kind, Source)` es asesoría; no acredita por sí sola
la evidencia tool-backed canónica solicitada por ADR0046.

Pendiente: completar implementación, documentación del schema efectivo y pruebas
de serialización/replay/enlaces del conjunto. Las decisiones de política/ejecución
no definidas siguen fuera del bloque. Hasta verificarlo no se declara cumplido
el criterio de eventos pre-M6 congelados.
