# Rango causal y confirmación durable del command

## Bloque completo de admisión/checkpoint — 2026-10-07 06:33 UTC

EnsureSessionRoutingPolicy, ExecuteExplorerTurn, FollowUp y las tres escalaciones
conservan ahora Failure original y Ack también si falla admisión o checkpoint.
Sin checkpoint confirmado el outcome queda Deferred sin rango inventado; con
checkpoint se distingue rechazo sin writes, prefijo durable confirmado e imposible
confirmación. CLI propaga el error antes de autorizar, esperar o invocar. Accepted
con error describe efectos parciales, nunca éxito de la operación.

Luna HIGH implementó el bloque y PolicyAdmissionFailureTests. Root auditó los diffs,
adaptaciones de tests existentes y fixture completo, y corrigió dos assertions de
restauración situadas fuera de su scope, dos errores de analyzer y el momento de
inyección de read-first: LastLaneId realiza una lectura propia y sus argumentos
deben resolverse antes de armar el fallo. Assertions de producción conservadas;
los fallos iniciales no se presentan como RED del producto.

Logs privados: C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0623/.
commands-build.log: dos errores de analyzer; commands-focal.log: 57 casos,
56 PASS/1 FAIL por el fixture LastLaneId. commands-final-build.log: 0 warnings/
errores. commands-final-focal.log: 57 PASS/0 FAIL/0 SKIP en 1.602s.
commands-architecture.log: 56 PASS/0 FAIL/0 SKIP en .663s, build 0/0.
Full de este bloque pendiente: estos focales no sustituyen una suite integral.
SQLite/reapertura y faults son fixtures privados, no consultas autenticadas.

Qwen baseline respondió sin tool call (finish_reason length), por lo que NO ejecutó
pruebas; evidencia preservada. Un nuevo encargo de herramienta específica sobre el
binario actualizado está en curso, sin atribuirle todavía ejecución. La verificación
focal anterior la ejecutó root. M5.5 sigue abierto: AgentProfile efectivo, contratos
congelados completos y autoridad de modos/UltraCode de ADR0047. Su documento
aceptado se incorpora fielmente desde el checkout principal sólo leído; no se han
implementado sus gates ni se modifica main.

## Routing y denegaciones: frontera excepcional — 2026-10-07 05:29 UTC

Full posterior sobre af1918a terminal exit0: 2620 casos/2616 PASS/0 FAIL/4 SKIP
symlink en 231.835s; routing-command-failure-full.log. El pending de abajo es
histórico. Auditoría adicional: ExecuteExplorerTurn conserva fallos del callback,
pero admisión/checkpoint previos siguen fuera de esa frontera excepcional.

AuthorizeModelRoute devuelve Ack + Failure original y sólo autoriza cuando el
NoOp permitido puede confirmarse. Un fallo de lectura de outcome devuelve
Deferred/JournalOutcomeUnavailable, Authorized=false y ninguna ID no confirmada.
Append que persiste y luego lanza confirma una sola vez su rango causal y la
solicitud observada (Accepted/error); ausencia confirmada da Rejected; incertidumbre
da Deferred sin rango. Nunca reintenta ni concede permiso en el catch.

ResolveModelRouteWithoutClient y ResolveBudgetWithoutClient conservan el ACK
correlacionable junto a la excepción/OCE original mediante InternalCommandResult,
un carrier exclusivo del Host: no cambia el contrato wire ni su vocabulario.
El CLI valida Status/Outcome antes de permitir el paso al proveedor y propaga
Failure con ExceptionDispatchInfo. No cambia la política de gasto ni la selección
de sesión/Run al confirmar errores.

Fixtures de fallos con SQLite privado real, sin consultas de proveedor ni consumo.
RED válido: 7 casos/7 FAIL en 0.716s. Primer final ampliado: 62 casos/1 FAIL,
porque el fixture esperaba dos filas de denegación; el lifecycle real produce
InteractionResolved, LaneCancelled, TaskCancelled, PlanItemCancelled y RunFailed.
Root corrigió el oracle a esos cinco eventos exactos y comprobó orden, secuencias,
Run y causation, además de ausencia de duplicados. Final con admission-read,
confirmación de consentimiento y cancelación: 52 PASS/0 FAIL en 5.588s.
Arquitectura: 56 PASS/0 FAIL en 0.599s; builds sin errores ni advertencias.
Logs routing-command-failure-red/expanded-final/admission-final/architecture.log.
Full nueva pendiente. Full anterior sobre98224ea: 2607 casos/2603 PASS/0 FAIL/
4 SKIP symlink, 250.515s, plan-approval-command-failure-full.log.
EnsureSessionRoutingPolicy, follow-up y escalación aún requieren fronteras
excepcionales; estos resultados no acreditan cierre de todos los commands.

## PlanApproval: errores y consulta idempotente — 2026-10-07 05:15 UTC

RequestPlanApprovalCommand devuelve Failure original + Ack + InteractionId.
Ante append que persiste y luego lanza, confirma una sola vez los eventos
causales posteriores al checkpoint y devuelve la ID observada, Accepted/error
y rango exacto. Sin eventos confirmados: Rejected sin ID/rango. Si no puede
confirmar: Deferred/JournalOutcomeUnavailable sin ID ni rango; nunca publica de
nuevo en el catch. Tampoco modifica la sesión seleccionada ni aprueba el plan.

La consulta del Run AwaitingInput devuelve NoOp con la misma aprobación pendiente
y sin eventos nuevos. Sólo un Run Running puede publicar una nueva solicitud.
El CLI recibe el resultado del command y valida Status/Outcome antes de pedir
respuesta. El wrapper público mantiene su firma y preserva exception/OCE original,
pero no reduce silenciosamente un error/Deferred a «sin interacción».

Luna propuso cuatro casos offline; root leyó/integró y añadió confirmación fallida
después de append normal, propagación OCE del wrapper y dos pruebas SQLite con
reapertura del journal + archivo de sesión realmente guardado. El primer fixture
de reapertura omitía stateFile (2 FAIL): se corrigió la configuración del fixture,
sin quitar assertions de identidad/ID/no duplicación. No es un fallo del producto.
RED válido: 8 casos/6 FAIL, 1.010s, build sin errores/advertencias. Final ampliado:
81 PASS/0 FAIL/0 SKIP, 12.268s; arquitectura 56 PASS en 0.776s.
Logs plan-approval-command-failure-red/expanded-final/architecture.log. Providers
scripted y fallos de store son fixtures; SQLite y stateFile privados son reales.
Full del bloque pendiente. Full anterior sobre565f747 terminal exit0:2599 casos,
2595 PASS/0 FAIL/4 SKIP symlink,264.680s,completion-command-failure-full.log.
Routing/follow-up/escalación aún requieren el inventario excepcional completo.

## Completion: errores correlacionables — 2026-10-07 05:05 UTC

CheckRunCompletionAndGate conserva Failure original, Completed=null y ACK ante
errores de gates, cancelación y appends. Un prefijo confirmado da Accepted/error
con rango exacto; ausencia confirmada de eventos da Rejected; fallo de lectura o
checkpoint no verificable da Deferred/JournalOutcomeUnavailable sin rango.
No repite gates ni cambia la sesión/Run seleccionado al confirmar un error de
otra sesión. Explorer usa también la confirmación sin recuperar identidad de Run.
Los callsites CLI inicial y posterior a aceptación preservan la excepción con
ExceptionDispatchInfo y comprueban Status, no sólo Accepted.

RunValidationStarted deja el Run en Validating: no se finge un rollback a Running
ni un RunCompleted al fallar. Esto no implementa recuperación de validación
inconclusa, scheduler ni joins.

Luna entregó propuesta externa y root la leyó completa, integró, corrigió el
oracle Running por Validating confirmado por StateMachines, y añadió admission
read/checkpoint, aislamiento entre sesiones y SQLite real/reopen.
RED válido 12 casos/7 FAIL en 0.801s; final 61 PASS/0 FAIL/0 SKIP en 9.897s,
arquitectura 56 PASS en 0.727s, builds sin errores/advertencias.
Logs completion-command-failure-red/sqlite-final/architecture.log. Los errores
de callbacks/stores son fixtures; no acreditan consultas autenticadas.
Full del bloque pendiente. Full anterior sobre 2b03571 terminal exit0: 2589 casos,
2585 PASS/0 FAIL/4 SKIP symlink en 260.875s, explorer-command-failure-full.log.
PlanApproval y el inventario completo de commands aún requieren seguimiento.

## Explorer: errores correlacionables — 2026-10-07 04:56 UTC

`ExecuteExplorerTurn` devuelve también `Failure`, la excepción original del
callback, junto a `Result=null` y el ACK confirmado. Con eventos persistidos:
Accepted + Status=error y rango causal exacto; sin eventos confirmados: Rejected
sin rango; lectura imposible: Deferred/JournalOutcomeUnavailable sin rango.
Accepted no equivale a éxito. No se repite el callback, ni se completa/cancela
el Run como tratamiento del error. El CLI conserva la propagación original de
la excepción/cancelación mediante ExceptionDispatchInfo después de recibir el ACK.

RED válido: 7 casos, 3 FAIL por excepciones desnudas, 0 errores de compilación.
Final ampliado: 59 PASS/0 FAIL/0 SKIP en 7.601s; arquitectura 56 PASS en 0.689s.
Logs `explorer-command-failure-red.log`, `explorer-command-failure-cli-final.log`
y `explorer-command-failure-architecture.log`. Cuatro variantes usan SQLite real,
reapertura y callback/lectura inyectados: no son consultas a proveedores.
La suite completa de este bloque está pendiente. Completion y plan approval
todavía requieren el mismo tratamiento excepcional; no se declara cerrado el
criterio de todos los commands con este subconjunto.

## Contrato

`FirstSeq` y `LastSeq` delimitan los eventos resultantes observados de esta
invocación en su Session. Se incluyen raíces con `CommandCausation(CommandId)`
posteriores al checkpoint y sus descendientes mediante `EventCausation(EventId)`.
No bastan coincidencia de Run, cercanía cronológica o pertenencia a otra invocación
anterior del mismo CommandId. El journal por Session ordena padres antes de hijos.

Un rango mínimo/máximo no afirma que cada fila intermedia pertenezca al command:
los consumidores siguen consultando la causa del envelope. La selección excluye
otras sesiones y causas, incluidos descendientes de padres fuera del intento.
No se reescribe el journal ni se crea una causación implícita al último evento.

La reconciliación de un Run terminal conserva scope del efecto original, pero si
hay un caller causal explícito lo conserva igual que el recovery de un Run activo.
El recovery de fondo sin caller sigue causado por el evento EffectUnknown original.
Cada solicitud de resolución conserva como padre su propio ToolCallReconciled.

## Fallo de consulta del journal

Si falla la lectura usada para confirmar el resultado, el ack lleva `Status=error`,
`Outcome=Deferred`, `Reason=JournalOutcomeUnavailable` y ningún rango.
La confirmación está diferida: **no** demuestra rechazo, ausencia de writes,
consumo cero o posibilidad de volver a ejecutar ciegamente el command.
El error es constante y no expone excepciones del store. El intento y los eventos
ya persistidos no se borran. Este bloque no añade un retry automático ni garantiza
reconexión cuando el journal sigue inaccesible.

## Evidencia reproducible

Logs en `C:\Users\juanc\.codex\omni-m55-three-20261006`:

- `command-range-read-red-build.log`: build 0 warnings / 0 errores.
- `command-range-read-red-test.log`: 3 casos, 1 PASS / 2 FAIL: causa de command
  perdida en recovery terminal y excepción ReadFrom escapando al cliente.
- `command-range-tail-red-test.log`: después de corregir esas dos rutas, 2 PASS /
  1 FAIL; LastSeq=33 omitía la solicitud causal en seq34. Assertion intacta.
- `command-range-final-build.log`: 0 warnings / 0 errores.
- `command-range-final-focal.log`: 35 PASS / 0 FAIL / 0 SKIP, 1.039 s.
- `command-range-final-full.log`: 1806 casos / 1802 PASS / 0 FAIL / 4 SKIP
  por permisos symlink, 210.503 s, exit0. No incluye los nuevos tests del bloque
  posterior de auditoría de interacción o creación atómica, entregados tras el build.

El caso integrado usa OmniServer.Send real con sim scripted: Started sin outcome,
resume parcialmente persistido, cancel y resume terminal. Otra prueba verifica
recovery de fondo. ReadFailure usa store/audit de fault injection y demuestra que
existían eventos después de recuperar la lectura. El filtro puro usa envelopes
fixture para comprobar exclusión de causas/sesiones y descendientes transitivos.
No son consultas autenticadas ni consumo real de proveedores.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*CommandCausal*' -class '*SimulationOutcomeReadFailureTests*' -class '*SimulationExceptionalCommandOutcomeTests*' -class '*SimulationCommandOutcomeTests*' -class '*EffectResolution*' -class '*RunControlCommandOutcomeTests*' -class '*RejectedCommandOutcomeBoundaryTests*' -class '*InputInteractionCommandOutcomeTests*'
```

La auditoría de los demás catches de frontera continúa; no es cierre global de M5.5.
