# Contratos de registros pre-M6

Implementación de ADR0046 §2 y §8.4/§8.6. No implementa scheduler, transporte,
evaluación de joins, activación por wake, supervisión ni aceptación automática.
Los productores operativos de estas familias corresponden a M6.

## Identidad y envelope

`PreM6Ids.cs` define DelegationId, JoinId, BindingId, MailboxId,
MailboxMessageId, WakeRequestId, DispositionId y ValidationDebtId. `New()` usa
UUIDv7; Parse acepta UUID válidos igual que las identidades existentes.
Un identificador vacío se rechaza. No se exige versión UUIDv7 para leer datos.

Todos los eventos implementan `IPreM6ContractEvent`: ExecutionId, Type(),
SchemaVersion(), Validate() y RecordArtifacts(). El envelope conserva SessionId,
RunId/CorrelationId, TaskId, LaneId, ExecutionId, UTC, Source, CausationId y
ArtifactRefs mediante el escritor existente. La identidad de ejecución debe
resolver una Task/Lane/Profile durable y coincidir con el envelope.
Sequence sigue siendo local a Session. No se toma identidad de la selección UI.
Una segunda AgentExecutionStarted idéntica es compatible; con la misma
ExecutionId y otros metadatos se rechaza antes de append y durante replay.

## Tipos y eventos schemaVersion 1

| Registro | Campos | Eventos |
|---|---|---|
| Delegation | DelegationId, ParentExecutionId, ChildTaskId, ChildLaneId, ProfileId, PacketRef, Relation, Supervision | `delegation.created`, `.accepted` (ChildExecutionId), `.returned` (ResultRef), `.failed` (Reason) |
| ExecutionJoin | JoinId, OwnerExecutionId, MemberExecutionIds, Policy | `execution_join.created`, `.resolved` (SatisfyingExecutionIds), `.failed` (Reason) |
| SupervisionBinding | BindingId, SubjectExecutionId, SupervisorExecutionId, PolicyRevision, FailurePolicy | `supervision_binding.created`, `.accepted`, `.failed` (Reason) |
| ExecutionMailbox | MailboxId, OwnerExecutionId | `execution_mailbox.created` |
| ExecutionMailboxMessage | MessageId, MailboxId, SenderExecutionId?, ContentRef, CorrelationEvent? | `execution_mailbox.message_received`, `.message_acknowledged` (MailboxId, MessageId) |
| WakeRequest | WakeRequestId, TargetExecutionId, SourceEvent, Reason, PolicyRevision, MessageId? | `wake_request.created`, `.accepted`, `.resolved`, `.failed` (Reason) |
| Resultado producido | ResultRef, ResultRevision, ResultSchemaId, SupersedesResultRef? | `agent_result.produced` |
| ResultDisposition | DispositionId, ExecutionId, ResultRef, Outcome, EvaluatorExecutionId?, Reason, EvidenceRefs | `result_disposition.recorded` |
| ValidationState | Scope, Level, Status, RequiredChecks, EvidenceRefs | `validation_state.recorded` |
| ValidationDebt | DebtId, SubjectExecutionId, Scope, RequiredLevel, MissingChecks, EvidenceRefs | `validation_debt.created`, `.resolved` (DebtId, EvidenceRefs) |
| IntegrationStatusRecord | Scope, Status, EvidenceRefs | `integration_status.recorded` |

Todos incluyen ExecutionId del propietario del hecho. Son 23 tipos de evento.
Las referencias PacketRef, ContentRef, ResultRef y SupersedesResultRef usan
ArtifactRef existente. ResultSchemaId identifica el formato del artifact; este
bloque no implementa el contenido de AgentResult por schema de M6.

JoinPolicy: Kind All/Any/Quorum/Explicit, RequiredCount? y RequiredExecutionIds?.
Las listas de miembros son únicas y no vacías; Quorum requiere cantidad entre
1 y N; Explicit requiere subconjunto no vacío. Campos de otro Kind se rechazan.
No se calcula readiness ni se comprueba si una ejecución tuvo éxito al resolver.
SupervisionFailurePolicy sólo tiene Wait; no existe degradación a Unmanaged.
Outcome de disposition: Accepted/Rejected/ReworkRequested, registrado sin
aplicar autoridad ni completar Task. Relation y Supervision no se infieren.

## Evidencia y alcance

EvidenceEventRef contiene SessionId y EventId. Se usa en SourceEvent y
CorrelationEvent, evitando resolver un EventId contra la sesión seleccionada.
EvidenceReceiptRef añade ToolCallId. EvidenceRef contiene Kind Claim/ToolBacked,
Summary, ArtifactRef? y ReceiptRef?. Claim no puede llevar receipt; ToolBacked
debe llevarlo y la proyección resuelve un ToolCallSucceeded canónico con el mismo
ToolCallId en la sesión indicada. No acepta ToolCallFailed ni texto del modelo.

ValidationScope contiene SessionId, RunId?, TaskId?, LaneId?, WorkspaceId? y
SpecificationRef?. Lane requiere Task, y Task requiere Run. SpecificationRef
describe el alcance/checks, no concede permisos de filesystem.
Los niveles son Local/Integration/Project/EndToEnd; el status de validación es
Unknown/Pending/Passed/Failed; integración Unknown/NotIntegrated/Integrated/Verified.
La proyección exige al menos un receipt ToolBacked para registrar Passed,
Verified o resolver deuda. Esto sólo acredita procedencia de la evidencia:
**no demuestra que los checks requeridos pasaron ni que el tool validó ese scope**.
La interpretación y producción de esa evidencia siguen perteneciendo al cliente
o extensión responsable, no a un detector de wiring incorporado al Core.

ArtifactRefs se validan estructuralmente y se extraen al envelope, deduplicados
por ArtifactId. El hash debe ser sha256 con 64 caracteres hexadecimales en
minúsculas, igual que el CAS existente. La proyección no accede al CAS ni demuestra integridad de bytes,
ni vincula automáticamente un attachment al output del receipt. El test con CAS
real privado verifica sus propios blobs; no acredita artifacts de un usuario.

## Reconstrucción y atomicidad

`PreM6RecordProjection.Replay` reconstruye hechos por familia/identidad, fases y
referencias desde el journal. Un lector puede aportar un callback readonly para
resolver eventos de otras sesiones. Sin él, una referencia externa no disponible
se rechaza, no se convierte en evidencia vacía. No crea un segundo journal.

EventStream valida la sesión más el candidato antes de Append/AppendBatch. Un
candidato inválido no se persiste; la proyección temporal no sobrevive a un fallo.
Hechos idénticos con la misma identidad son idempotentes en la proyección, aunque
el journal pueda conservar eventos repetidos. Contenido conflictivo, otro owner
o transición sin creación se rechazan. Los snapshots copian las colecciones.

Delegación exige Task hija y Lane/Profile exactos; aceptación exige ejecución
hija con ParentExecutionId/Relation/Supervision exactos. Returned referencia el
último resultado producido por ese hijo. Resultado nuevo aumenta revisión y
referencia exactamente el anterior, sin mutarlo. Disposition referencia un
resultado producido. Ninguno emite TaskCompleted/LaneCompleted/RunCompleted.

## Compatibilidad y evidencia reproducible

Familias nuevas empiezan en v1: no hay v0 histórico que upcastear. v0 de estas
familias se rechaza; una versión futura usa UnsupportedEventVersionException.
Los upcasters existentes y los contratos legacy no se modifican para inventar
registros, bindings, resultados o autorización retrospectivos.

Ejecutar desde el worktree autorizado, después de compilar:

```powershell
dotnet build tests/OmniCore.Tests --no-restore
dotnet run --project tests/OmniCore.Tests --no-build -- -class '*PreM6FrozenContractTests' -class '*PreM6RecordProjectionTests' -class '*AgentExecutionContractTests' -noColor
dotnet run --project tests/OmniCore.Tests --no-build -- -noColor
dotnet build tests/OmniCore.ArchitectureTests --no-restore
dotnet run --project tests/OmniCore.ArchitectureTests --no-build -- -noColor
```

2026-10-07: focal final 16 PASS; FULL final 2887 casos/2883 PASS/0 FAIL/4 SKIP symlink,
126.976 s; arquitectura 56 PASS tras build actualizado (0 advertencias/errores).
Primer focal 10 PASS/1 FAIL detectó EventType
comparado por referencia en la deduplicación; corrección sin cambiar assertions.
Auditoría Luna detectó además rebinding de ExecutionId y hashes inválidos; cinco
controles nuevos fallaron antes del fix y pasaron después, sin cambiar assertions.
Logs iniciales `pre-m6-*-1337/1338/1340.log` y finales
`pre-m6-audit-red-1343.log`, `pre-m6-audit-fixed-1343.log`,
`pre-m6-audit-full-1344.log`, `pre-m6-audit-architecture-1345.log` bajo
`C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/`.
Son fixtures deterministas con SQLite/CAS reales privados, no workers reales,
consultas autenticadas, consumo real ni aceptación integral M5/M5.5.

## Auditoría pendiente antes del cierre

La segunda revisión de Luna encontró que AgentExecutionStarted conserva un
ParentExecutionId sin resolverlo y no valida sus enlaces/envelope al registrarlo:
RequireExecution comprueba Lane/Profile/Task cuando los registros nuevos lo usan,
pero no comprueba el padre. No se acredita integridad completa de lineage todavía.
También falta demostrar que un callback de evidencia cross-session no puede
presentar un ToolCallSucceeded aislado sin lifecycle canónico válido.
Los tests verdes anteriores no cubren esos escenarios; deben reproducirse y
corregirse antes de declarar el conjunto completamente congelado/cerrado.
