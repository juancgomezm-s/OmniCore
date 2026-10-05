# Avance nocturno — 2026-10-05

Checkpoint 08:14 UTC /02:14 America/Mexico_City. Rama autorizada
`codex/omnicore-consolidation-20261004`, base del turno `a627892`.
Trabajo autónomo autorizado sólo hasta14:00 UTC /08:00 local. Sin push ni integración a main.

## Entregas auditadas

- `16b8680`: regresión de atribución ExplorerTurn entre dos Run/Lane, suspensión user.ask,
  interacción pendiente, resolución y lectura posterior con reapertura SQLite. Verifica el Turn
  original y restauración del scope padre. Coordinador reprodujo1/1 PASS. Los dos Runs son una
  fixture de aislamiento, no una implementación de concurrencia ni una excepción a ADR0046§8.
- `6216dd6` y `2cc7784`: frontera de telemetría tipada, numérica y sin contenido; sink local
  acotado/noop y wrapper de provider opt-in. Preserva objetos del stream, token, cancelación y
  fallos del provider. Pruebas de timestamp usan offset explícito para no depender de la zona del
  host. No modifica configuración de providers ni conecta automáticamente los callsites actuales.
- `0b527d4`: reparación del fixture FileAuditSinkTests. Cierra el store, limpia exclusivamente el
  pool de la conexión propia y dispone esa conexión antes de borrar el journal. Conserva las
  assertions de supervivencia de auditoría; no modifica producción ni usa ClearAllPools.

## Verificación independiente

- TelemetryBoundaryTests + ExplorerTurnExecutionScopeBoundaryTests:7/7 PASS.
- Suite completa de arquitectura:54/54 PASS, sin omisiones.
- Primera suite funcional posterior a telemetría:1257 casos,1251 PASS /2 FAIL /4 SKIP.
  El output truncado permitió identificar el fallo de borrado del journal en FileAuditSinkTests;
  no se recuperó la identidad del segundo fallo y no se le atribuye una causa sin evidencia.
- Repetición funcional completa tras0b527d4: **1257 casos,1253 PASS /0 FAIL /4 SKIP**, exit0.
  Los cuatro SKIP son por permisos Windows para symlinks. Los conteos focales se solapan con la
  suite y no se suman. No equivale a prueba de TTY real, reinicio de OS ni cualificación remota.

## En curso y límites

`303691e` añade ExecutionId UUIDv7, tres eventos agent_execution lifecycle y ejes independientes
Relation/Supervision, además de TaskCreated v2 con ParentTaskId? y upcaster v1. Root auditó los
cinco archivos y reprodujo3/3 focales y suite completa: **1260 casos,1256 PASS /0 FAIL /4 SKIP**.
Lifecycle no cambia estados canónicos Task/Lane/Run ni implica producción/aceptación de resultado.
No existe emisión automática ni scheduler/joins. Luna corrige cleanup exclusivo del fixture en
commit separado `be6fd80`, reproducido junto a contratos por root.

`f86cc5f` añade ExecutionId opcional al envelope y scope, prioriza el campo propio del payload
(no ParentExecutionId) y lo conserva en memoria/SQLite y lector de workspace. Migración legacy
aditiva, idempotente y protegida por BEGIN IMMEDIATE ante aperturas simultáneas. Root auditó diff
y reprodujo5/5 nuevos tests; batería combinada ExecutionId/AgentExecution/telemetría:10/10 PASS.
No genera automáticamente ExecutionIds para los ejecutores del runtime.

`2be0171` añade dos tests de evicción FIFO/snapshot independiente y cuatro escritores con lector
del sink local. Implementados por coordinador, NO por Ling;2/2 PASS independientes. No exige orden
entre writers ni una planificación concreta del sistema.

Última suite **solución** del worker:1321 casos,1315 PASS /2 FAIL /4 SKIP (incluye varios proyectos).
Fallos: FileAuditSinkTests al borrar journal aún bloqueado y SecurityP0Tests test TLS real con
TaskCanceledException. Root repitió ambos focales:2/2 PASS; esto NO resuelve su posible flakiness.
Suite funcional completa independiente en curso al checkpoint, aún sin resultado. Luna investiga
recursos SQLite; no se debilitan assertions ni se ocultan errores de cleanup/TLS. No confundir esta
suite solución con los anteriores conteos de OmniCore.Tests ni afirmar verde global actual.

### Reparación posterior y contratos de escalación

La repetición funcional del coordinador previa al nuevo probe terminó1267=1263 PASS /0 FAIL /4 SKIP.
Después, el probe propio SqliteResourceLifetimeTests reprodujo directamente un IOException en
File.Delete tras Close/ClearPool exclusivo/Dispose, en la iteración12 de24 journals bajo la suite
completa. Pasaba aislado; la reproducción bajo carga fue necesaria. Los directorios de un probe
fallido se preservan para diagnóstico y no se enmascara el fallo con cleanup en finally.

`94987eb` añade using a15 DbCommands del store. No cambia permisos, pool global, transacciones,
política de durabilidad ni assertions. Tras el fix, el worker ejecutó solución completa:
**1326=1322 PASS /0 FAIL /4 SKIP**, y diez ejecuciones focales de FileAuditSinkTests sin fallo.
Root auditó diff y reprodujo14/14 focales de recursos/FileAudit/escalación. Rerun funcional
completo independiente posterior al fix está en curso al checkpoint. No se atribuye el timeout
TLS previo al mismo defecto ni se declara garantizada la ausencia de intermitencias futuras.

`b252b6d` añade TurnId?/LaneId? opcionales a las tres familias ModelEscalation v2 y upcasters v1
(ADR0046§2). Root implementó y verificó8/8 codec/lineage; Luna revisó sin defecto reproducible y
su suite completa incluye esos cambios. Eventos antiguos conservan campos y nulls, prioridad
payload sobre scope y replay sin transiciones canónicas nuevas. No cambia callers actuales,
consentimiento, routing, precios ni validación de providers.

Luna tiene ahora el contrato aditivo CommandOutcome/CommandAck de ADR0046§5, pendiente entrega:
no inferir outcome explícito desde status legacy ni implementar commands internos/transporte nuevo
en esta ronda. M5.5 no se declara cerrado por estos contratos parciales.

Ling03 recibió un paquete cerrado mínimo para pruebas CausationScope y terminó con HTTP429
sin código visible. Ninguna entrega de esa ronda fue aplicada; logs preservados. No fallback
pagado ni repetición inmediata. Ling04 nuevo paquete de tests de sink concurrente también recibió
HTTP429 sin entrega. Su log reveló override NODE_TLS_REJECT_UNAUTHORIZED=0 heredado: no afirmar
TLS validado para esa llamada. Se retiró ese override sólo del runner antes de cualquier fetch
futuro; no se cambiaron certificados, servidores ni el entorno padre. Qwen permanece bloqueado
tras length8192 sin entrega: no se
relanzó ni se comprobó/modificó su servidor durante este checkpoint.

Telemetry es opt-in y process-local; no exportadores ni readmodels durables. ExecutionScope ya
incorpora ExecutionId; ModelStepId, Source y eliminación del fallback de causation permanecen
pendientes. M4 TTY/sesiones largas/reinicio OS y M5 cualificación/acceso real siguen abiertos.
No se gastó presupuesto de validación real del proveedor. Main y procesos no identificados como
propios se preservaron sin cambios.
