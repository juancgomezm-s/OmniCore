# Steering explícito — contrato incremental M5.5

ADR-0046 §1 distingue tres entradas: respuesta a una interacción, FollowUp para un
Turn posterior y Steering para el Turn activo. Sin declaración explícita sigue
siendo FollowUp. Steering no crea una intención nueva ni otro Turn.

## Eventos v1

Los tres payloads conservan `SteeringId`, `RunId`, `LaneId` y `TurnId`.
El envelope conserva además Session, Sequence y las atribuciones existentes.

- `turn.steering_received`: `InputPartsJson` y `Origin?`; identifica la entrada
  explícita destinada a ese Turn, no texto generado por el modelo.
- `turn.steering_applied`: `StepIndex`; identifica la frontera del ModelStep
  que incorpora la entrada, no una aplicación en medio de una invocación.
- `turn.steering_dropped`: `Reason` no vacío; deja constancia del descarte.

`SteeringId` es un identificador durable independiente de InteractionId y
FollowUpId. No existe supersesión automática ni conversión a FollowUp.
`InputPartsJson` se conserva exactamente en la serialización del contrato;
la admisión de texto debe aplicar la política de redacción existente.

Los codecs y el source generator registran los tres eventos como schema v1.
No se cambia ningún schema previo ni se necesita un upcaster de v0.
Las restricciones de lifecycle y consumo pertenecen a la cola/consumidor,
no quedan acreditadas por un roundtrip de los records.

## Evidencia reproducible y alcance

Logs bajo `C:\Users\juanc\.codex\omni-m55-three-20261006`:
`steering-contract-build.log` y `steering-contract-focal.log`.
Build sin warnings/errores; focal 48 PASS/0 FAIL/0 SKIP, incluyendo los tres
roundtrips, ModelStep, FollowUp y envelope/scopes. Son pruebas deterministas
de contratos, no consultas autenticadas ni consumo real de proveedores.

## Admisión y consumo implementados

`IOmniClient.Send` acepta un command `session.input` con `kind: "steering"`.
Exige `sessionId`, `runId`, `laneId`, `turnId` y `text`. `steeringId` es opcional:
si falta se utiliza el UUID del command. `origin` también es opcional.
El destino debe pertenecer a la sesión abierta en ese servidor, con Run no
terminal, Lane perteneciente a una Task del Run y el Turn exacto abierto.
Un destino inválido se rechaza; nunca se cambia a la sesión/Turn actual ni a FollowUp.
Un `kind` desconocido también se rechaza. Sin `kind`, el comportamiento anterior
permanece intacto; `interaction.respond` sigue siendo el command de respuestas.

El mismo SteeringId con igual destino/texto redactado/origen es idempotente
mientras el destino sigue abierto. Devuelve `NoOp` sin nuevos eventos/rango.
Un ID con distinto contenido/destino se rechaza. `Accepted` sólo acredita
recepción durable, no aplicación; FirstSeq/LastSeq identifican el recibo causado
por ese command. Ni un incremento ajeno de Sequence ni una consulta cuentan
como recepción nueva. La cola no cambia lifecycle ni inicia otra invocación.

`SteeringQueue.Pending` y `Applied` son proyecciones de sólo lectura ordenadas
por Sequence de recepción. Conservan ReceivedEventId, payload y destino.
Se validan IDs duplicados, transiciones dobles o sin recepción, scope incorrecto,
índice negativo, razón vacía y recepción fuera de un destino abierto.
El scope de recepción se deriva de Lane→Task→Run, no de un caller ambiental ajeno.

ExplorerTurn toma una instantánea del FIFO en cada frontera de ModelStep.
Incorpora esos mensajes al contexto después de los resultados de herramientas,
sin alterar un request en vuelo ni descartar la continuación del proveedor.
Después de los guards de contexto/presupuesto, los Applied y ModelStepStarted
comparten un único batch Barrier. Al terminar/abandonar/interrumpir el Turn,
los Dropped pendientes comparten batch con el evento terminal. Los commands
de cancelación/interrupción y la denegación de presupuesto descartan también
las entradas afectadas, con scope por item y causation del command real.

Una suspensión por interacción no descarta ni aplica steering: permanece
pendiente hasta el siguiente ModelStep. El replay reconstruye los mensajes
Applied en su lugar del historial; los Pending se incorporan sólo en la
siguiente frontera. No se escriben UserInputReceived ni FollowUp por steering.

## Verificación de integración

`steering-durable-identity-build.log`: 0 warnings/0 errores.
`steering-durable-identity-focal.log`: 102 PASS/0 FAIL/0 SKIP, 2.800 s.
Incluye commands públicos, scope ambiental ajeno, rechazos/idempotencia,
continuación/herramientas, descartes terminales/control/budget y batches scoped.

`ExplorerTurnSteeringDurableResumeTests` utiliza SQLite y CAS reales, nueva
instancia OmniServer/ExplorerTurn tras reopen y respuesta pública al cuestionario.
El proveedor y los precios son fixtures, no consultas autenticadas:

- Un Turn, tres ModelSteps, una suspensión: replay exacto de ProviderState y
  steering aplicado una vez en step 1. Uso persistido 21 input/5 output;
  costes de fixture 0.000014/0.000007/0.000010 USD, total 0.000031 USD,
  conservado también en el resumen durable del Turn.
- Segunda suspensión tras aplicar una entrada: reopen conserva esa entrada
  una sola vez y consume otra pendiente en step 2, sin convertirla a FollowUp.

Suite previa del primer build runtime: 1742 casos/1738 PASS/0 FAIL/4 SKIP,
211.105 s (`steering-runtime-full.log`). No incluye los últimos cambios de
atomicidad terminal, budget denial ni las dos pruebas durable-resume.
Suite final `steering-durable-full-final.log`: 1745 casos/1741 PASS/0 FAIL/
4 SKIP por permisos symlink, 211.540 s. Incluye las últimas correcciones y
las dos pruebas de reapertura; proceso terminado con exit 0.
Las cifras se solapan y no se suman.

Se preservaron fallos intermedios: build durante entrega todavía incompleta,
dos analyzers xUnit, TurnCompleted de fixture sin scope tras otro Run y
comparación por referencia de EventType en un predicate de fixture.
Se corrigieron sin suppress ni debilitar las assertions de destino/continuidad.
No se ejecutaron tests sobre una DLL anterior después de build fallido.

Reproducción:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*Steering*' -class '*ExplorerTurnDurableProviderStateTests' -class '*ExplorerTurnSuspendedUsageRegressionTests' -class '*InputInteractionCommandOutcomeTests' -class '*RunControl*' -class '*FollowUp*' -class '*ScopedEventBatchTests'
```

## Límites

La implementación no añade un atajo/menu visual en OmniCoder ni constituye
evidencia de su renderer. La frontera IOmniClient y el runtime se integran en
estas pruebas, no una aplicación externa autenticada.
No implementa concurrencia de lanes, scheduler o joins de M6; conserva el modelo
de un escritor por Session. Source/fallback causal global y guards generales del
journal siguen en el bloque de cierre separado de ADR-0046 §4/5.
`CanonicalStateTracker` registra Pending/Applied/Dropped y las relaciones
Run/Task/Lane/Turn. EventStream rechaza identidades duplicadas, destinos cerrados
o ajenos y transiciones inválidas antes de persistir, también en batches.
Clone y Snapshot conservan el estado; un fallo del store no consume una entrada.
Dropped sigue permitido después de terminar el Run para completar el descarte.

## Guard canónico previo a persistencia

Se reprodujeron 15 fallos antes de implementar el guard
(`steering-preappend-red-test.log`): writes directos inválidos no se rechazaban.
Las pruebas exigen que Sequence y LiveState no cambien tras el rechazo.
Los journals malformados de los tests de proyección se siembran exclusivamente
por un store de fixture; no se permite fabricarlos mediante EventStream.

Build final: 0 warnings/0 errores (`steering-canonical-final-build.log`).
Focal final: 115 PASS/0 FAIL/0 SKIP, 1.914 s
(`steering-canonical-final-focal.log`), con replay, clones independientes,
rollback/reintento de Received y Applied mediante Append y AppendBatch,
reapertura SQLite, scopes existentes pero ajenos y cierre terminal.
Estos casos son deterministas; no acreditan consultas ni consumo autenticados.
Suite completa del mismo build: 1771 casos/1767 PASS/0 FAIL/4 SKIP por permisos
symlink, 211.331 s (`steering-canonical-final-full.log`), exit 0.
No incluye los tests del siguiente paquete de causalidad independiente,
entregados después de compilar. Las cifras focales se solapan con la suite.
