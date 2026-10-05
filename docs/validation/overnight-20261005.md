# Avance nocturno — 2026-10-05

Checkpoint 09:17 UTC /03:17 America/Mexico_City. Rama autorizada
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

Rerun funcional del coordinador posterior a94987eb+b252b6d terminó1272 casos:
1268 PASS /0 FAIL /4 SKIP. No sumar arquitectura ni focales solapados.

`1a77ac1` añade RuntimeCommandOutcome explícito Accepted/Rejected/NoOp/Deferred(reason) y
CommandAck.Outcome nullable, con FirstSeq/LastSeq opcionales pareados, positivos y ordenados.
Coordinador auditó diff y reprodujo5/5. Worker verificó Protocol net8/net10 sin warnings/errores.
Legacy ctor/Ok/Fail no infieren outcome desde Status, que sigue intacto. No existe una ruta de
serialización/lectura CommandAck actual para probar round-trip; no se inventó un transporte nuevo.
El tipo permite declarar resultados pero el servidor aún no los emite por tener el contrato.

Suite funcional independiente tras el contrato Ack:1277 casos,1273 PASS /0 FAIL /4 SKIP.

`09f1984` conecta run.interrupt/run.cancel con Accepted o Rejected explícitos, manteniendo Status
y Error y las transiciones existentes. El rango filtra eventos posteriores en la misma Session
por CommandCausation exacto; no incluye eventos previos ni notificaciones. Sin escritura causada,
el rango es null. Root auditó diff y reprodujo31/31 pruebas RunControl vía IOmniClient in-process,
incluidos primera/segunda interrupción, cancelación, rechazo sin sesión/terminal, suscripción y
proyección. No provider invocado por esos tests. No se clasifican NoOp/Deferred por conveniencia.

Suite funcional independiente posterior a09f1984:1280=1276 PASS /0 FAIL /4 SKIP.

`dd994a3` conecta session.input/interaction.respond con outcomes explícitos y rangos causales.
Root auditó diff y reprodujo75/75 pruebas de input, interacción, cuestionario y controles. Una
Session nueva no hereda el rango de otra; FollowUp aceptado no se confunde con ejecución de un
nuevo Turn. Opciones/respuestas inválidas o duplicadas preservan rechazo y permisos existentes.

La suite independiente posterior terminó1286=1281 PASS /1 FAIL /4 SKIP. Falló
M2IntegrationTests.Explaine_repo_criterion_renders_with_plain_renderer (assert de intención asumida),
con stdout contaminado por JSON de otra ejecución. Aislado pasa1/1. Tres fixtures que reemplazan
Console.Out carecían de la colección de aislamiento global existente; ahora M2IntegrationTests,
ClientTests y JournalCommandsTests usan ProcessEnvironmentCollection. No se debilitan assertions,
se cambian rutas de producción ni se desactiva el paralelismo de toda la suite.

`066dd68` añade outcomes a explore.start, act y command.invoke. Los Runs nuevos reportan sólo
eventos de su nueva Session causados por ese command. Objetivo vacío rechaza sin crear Session;
expansión correcta es Accepted sin journal (no NoOp), errores de catálogo/JSON mantienen Error.
Root auditó diff y reprodujo73/73 incluyendo estas pruebas y los fixtures de consola corregidos.
Suite funcional completa independiente posterior a creation y aislamiento de consola terminó
**1289=1285 PASS /0 FAIL /4 SKIP**, exit0. Fix de aislamiento: `74e6c53`; no modifica código de
producción ni assertions. No confundir este resultado con cierre M4/M5 o cualificación remota.

`e860be1` conecta sim/sim.resume: distingue aceptación del comando de éxito del Run y preserva
Status/Error/diagnósticos. Un escenario aceptado que termina fallido conserva Status=error y
Outcome=Accepted. Escenario inválido o resume sin Run rechazan sin rango; resume correcto incluye
sólo eventos nuevos de su Session causados por el comando. Catches genéricos conservan outcome
legacy null y stack/error: no se infiere rechazo de una excepción posterior a efectos.

Root `ae278c2` completó atribución de telemetría con ExecutionId? opcional, sin generar IDs ni registrar
contenido. Dos pruebas nuevas verifican scopes anidados/restauración/ausencia de scope y dos
flujos async concurrentes usando el mismo sink; callers legacy conservan null. El fixture de
reapertura de journal de TelemetryBoundaryTests ahora dispone la conexión real y limpia sólo su
pool antes de reabrir y borrar: no oculta IOException ni debilita assertions.

Verificación independiente combinada sim/telemetría/fixtures:51/51 PASS. Suite funcional completa
posterior: **1295=1291 PASS /0 FAIL /4 SKIP**. Arquitectura recompilada (incluido whitelist de
ExecutionId sin texto arbitrario):54/54 PASS, conteo separado. No sumar cifras solapadas.
Luna auditó telemetría sin defecto concreto y prepara una frontera interna Host-server para la
ejecución ExplorerTurn que llamaba Ask directamente fuera de command (ADR0046§5).
`4d89915` implementa esa frontera interna sólo para Ask no-Act: valida Session/Run activo,
conserva CommandCausation existente o genera CommandId interno por operación, devuelve resultado
y Ack/rango causal tras retorno normal, sin capturar excepciones/cancelación ni cambiar protocolo
del provider. Root auditó y reprodujo sus5 tests, incluida suspensión user.ask, rechazo sin callback
para par Session/Run incorrecto o terminal, rangos con causa preexistente y excepción tras escritura.
No se añade una causa sintética por lote en Engine. `f331704` extiende la misma frontera a cada
invocación de Turn del loop Act. Root reprodujo25/25 incluyendo gates existentes, controles Ask/Act,
cuestionario y causalidad de persistencia. El test nuevo mantiene TurnId al suspender/reanudar bajo
dos CommandId distintos. La excepción con scope interno generado restaura su padre EventCausation
incluso después de append, sin fabricar Ack de éxito. Auditoría detectó que el nuevo test Act usaba
fixture RunMode.Plan; Luna lo corrige a RunMode.Act con assertion explícita, cambio sólo de pruebas.
Gates/aprobación/escalación todavía no están envueltos por esa frontera.

Root `a809c34` reprodujo y reparó un defecto independiente de EventStream: tres pruebas fallaban porque el cursor
de Run/último evento se adelantaba al construir envelopes antes de persistir. Append fallido,
AppendBatch fallido o error al codificar un elemento posterior dejaban al siguiente evento con
causa de un envelope nunca persistido. El fix usa candidatos locales por lote y publica cursores
sólo después de Append/AppendBatch confirmado, sin catch ni eliminación global del fallback.
Los tres repros pasan; dos controles adicionales verifican ausencia de fuga de RunId y que un
batch correcto conserve su cadena interna y tail. Root batería16/16 PASS (5 de fallo/persistencia,
5 Ask interno,5 ExecutionId envelope,1 Scope). Suite funcional independiente tras fix y los3 repros:
**1303=1299 PASS /0 FAIL /4 SKIP**; los2 controles añadidos después se verificaron focalmente,
no se inventa un full de1305. Luna auditó el fix sin defecto concreto. Siguiente full combinado
independiente tras `f331704` y los5 controles EventStream: **1307=1303 PASS /0 FAIL /4 SKIP**, exit0.
Ese full precede al ajuste test-only de RunMode.Act, que se verificará focalmente.

Siguiente operación interna de Luna: RequestPlanApprovalIfNeeded conserva API y condiciones de
autorización actuales; debe atribuir su publicación al command interno, devolver Ack/rango causal y
clasificar como NoOp sólo sus ramas condicionales ya inefectivas. No introduce aprobación automática.
Un build focal del worker coincidió con la suite root y encontró locks MSB3026; lo detuvo sin tocar
el proceso root y ya recibió aviso de compilación libre tras el resultado completo. No fue un fallo
del producto ni una validación remota, y no se reiniciaron servidores.
No declarar todos los commands migrados ni commands internos listos. M5.5 no
se declara cerrado; Source/fallback causal y wiring general siguen pendientes.

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
