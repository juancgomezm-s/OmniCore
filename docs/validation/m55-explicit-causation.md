# Causas explícitas y rechazos de commands — M5.5

ADR-0046 §4 elimina el fallback «último evento de esta instancia». El orden de
Sequence por Session no demuestra una relación causal. EventStream conserva
únicamente la causa explícita de CausationScope; sin scope, Causation es null.
No se cambia el formato ni se reescriben journals históricos.

## Batch atómico

Las firmas anteriores de AppendBatch siguen disponibles. La firma aditiva acepta
una lista de CausationId? por elemento, independiente de ExecutionScopeState?.
Si la lista se omite se usa la causa ambiental. Un elemento null de una lista
presente significa ausencia de causa, incluso bajo un scope ambiental.
Una longitud distinta de payloads se rechaza antes de leer o escribir el store.
Todos los elementos siguen persistidos en un único commit; no se publica estado
ni el cursor de Run si la escritura falla.

## Recuperación de efectos

- La recuperación activa restaura Run/Task/Lane/Turn/ToolCall/Execution desde el
  Started/EffectUnknown del efecto, sin heredar el scope ambiental ajeno.
- Cuando existe un command explícito de resume, se conserva como causa real de
  sus outcomes. La recuperación sin command atribuye EffectUnknown a Started,
  y Reconciled al EffectUnknown persistido, incluso después de un crash.
- Cada solicitud de conflicto tiene como causa su propio ToolCallReconciled.
  Dos conflictos independientes no forman una cadena artificial entre sí.
  El Run que espera sigue siendo el activo; el efecto conserva su Run original.
- El gate RunAwaitingInput se atribuye al command real cuando existe; fuera de
  él, el primer efecto sin resolver es suficiente para explicar el bloqueo.
- Consultar otra vez no duplica reconciliaciones ni solicitudes pendientes.

## Rechazos de la frontera pública

OmniServer.Send devuelve Status=error y Outcome=Rejected para commands desconocidos,
payloads sin cmd/JSON inválido/no objeto y MessageType incorrecto. Conserva el
MessageId recibido como CommandId del ack, sin fabricar un UUID. No produce
FirstSeq/LastSeq ni eventos nuevos. La rama legacy de query sigue sin efectos,
con Status=ok y Outcome=NoOp. No se modifica la factoría legacy CommandAck del
protocolo, que permanece compatible.

JsonObj.Parse ya convierte JSON malformado/no objeto en un mapa vacío: no se
confirmó la hipótesis anterior de una excepción JSON escapando en esta frontera.
El defecto reproducido era un ack sin Outcome, también en esos casos.

## Evidencia reproducible

Logs en `C:\Users\juanc\.codex\omni-m55-three-20261006`:

- independent-causation-red-test.log: 4 casos, 2 PASS/2 FAIL antes del fix.
- effect-causation-red-test.log: 9 casos, 6 PASS/3 FAIL; solicitudes Memory/SQLite
  y recuperación activa sin causa. Incluye las pruebas del batch aditivo.
- rejected-command-red-test.log: 6 casos, 0 PASS/6 FAIL por Outcome ausente.
- causation-command-final-focal.log: 126 casos, 125 PASS/1 FAIL; regresión real
  de atribución de sim.resume al command, preservada y corregida sin debilitar
  su assertion sobre ToolCallReconciled ni el rango resultante.
- causation-command-parent-build.log: 0 warnings/0 errores.
- causation-command-parent-focal.log: 126 PASS/0 FAIL/0 SKIP, 1.689 s.
- causation-command-full.log: 1787 casos/1783 PASS/0 FAIL/4 SKIP por permisos
  symlink, 209.548 s, proceso33790 terminado exit0. Es el mismo build del focal;
  no incluye el siguiente paquete de simulación excepcional entregado después.

Fixtures deterministas: stores Memory/SQLite reales, reconciliador controlado,
IOmniClient/OmniServer y simulaciones locales. No consultas autenticadas ni gasto
real de proveedor. Las cifras focales se solapan y no se suman.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*RejectedCommandOutcomeBoundaryTests' -class '*CommandOutcome*' -class '*EffectResolution*' -class '*Reconciliation*' -class '*EventStream*' -class '*ScopedEventBatchTests' -class '*JournalEnvelopeTests' -class '*Steering*'
```

Source, guards de arquitectura y el inventario completo de commands siguen
pendientes. Este bloque no acredita cierre total de ADR-0046 §4/5 ni M5.5.
