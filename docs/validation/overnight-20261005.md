# Avance nocturno — 2026-10-05

Checkpoint 07:34 UTC /01:34 America/Mexico_City. Rama autorizada
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

Luna tiene ownership de contratos Domain AgentExecution/ExecutionId/lineage y EventCodecs con
pruebas nuevas, conforme ADR0046§2/8/9. Pendiente su entrega, revisión independiente y pruebas.
No se declara implementado por haber asignado el trabajo. Scheduler, joins, ejecución de agentes
concurrentes y aceptación de resultados siguen fuera de este bloque.

Ling03 recibió un paquete cerrado mínimo para pruebas CausationScope y terminó con HTTP429
sin código visible. Ninguna entrega de esa ronda fue aplicada; logs preservados. No fallback
pagado ni repetición inmediata. Qwen permanece bloqueado tras length8192 sin entrega: no se
relanzó ni se comprobó/modificó su servidor durante este checkpoint.

Telemetry es opt-in y process-local; no exportadores ni readmodels durables. ExecutionScope aún
no incorpora ExecutionId/ModelStepId; Source y eliminación del fallback de causation permanecen
pendientes. M4 TTY/sesiones largas/reinicio OS y M5 cualificación/acceso real siguen abiertos.
No se gastó presupuesto de validación real del proveedor. Main y procesos no identificados como
propios se preservaron sin cambios.
