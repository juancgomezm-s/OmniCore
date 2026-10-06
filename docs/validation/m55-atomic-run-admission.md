# Inicialización atómica y errores posteriores a la admisión

## Frontera pública

`act` y `explore.start` comparten una inicialización de once eventos:
Session, política de routing, raíz verificable, Run/Task/Lane y Plan.
Ahora se valida todo el batch antes de escribir y se confirma en un único
Barrier. Un fallo de insert revierte el journal completo y no modifica la
sesión o el Run previamente seleccionado. No se publica un Run incompleto.

El marcador de identidad del workspace se establece con la API existente antes
del batch para poder referenciarlo. No se promete una transacción distribuida
entre filesystem y SQLite: un fallo del journal puede dejar ese marcador de
identidad, pero no una sesión parcial. No se borra ni revierte un marcador ajeno.
Esto no ejecuta un modelo ni una tool y no implementa restore de M7.

Después del commit, un fallo al materializar o guardar el estado de selección
no convierte los eventos ya persistidos en rechazo: el ack lleva Status=error,
Outcome=Accepted y su rango. Se mantiene la identidad del nuevo Run, no el cache
de otra sesión. Fallar SaveLastSession no acredita que la selección pueda
recuperarse tras reiniciar; el journal y sus eventos sí permanecen.

El primer `session.input` elige su SessionId antes de inicializarla. SessionCreated
y SessionRoutingPolicySet forman otro batch Barrier. Si éste falla sin persistir,
el command es Rejected sin rango; si la Session se confirma pero falla el siguiente
batch de Run, queda Accepted/error por los dos eventos realmente persistidos,
con la sesión seleccionada y sin inventar un Run.

## Resolución de interacciones

RunControl captura fallos no relacionados con validación y clasifica a partir
de los eventos durables de ese command. Si AuditEffectResolutions falla después
de resolver, el cliente recibe Accepted/error y rango, no una excepción sin ack.
Los eventos y el efecto reconciliado conservan sus identidades de origen.
Responder otra vez a la misma interacción no vuelve a resolverla ni crea eventos.
Los rechazos de dominio existentes mantienen su semántica independiente.

Si el propio journal no puede consultarse para confirmar, se conserva la regla
del [bloque anterior](m55-command-causal-ranges.md): Deferred con razón explícita,
sin rango inventado ni afirmar cero efectos. No hay retry automático.

## Patologías de admisión y selección en la misma sesión

La canonicalización del path de `act` está dentro de su frontera de admisión.
Un workspace sintácticamente inválido (incluido NUL en JSON) devuelve Rejected/error,
sin rango ni escrituras, preservando la Session/Run anterior. No incluye el path
ni el mensaje de excepción en el error público. No captura como Rejected fallos
ocurridos después del commit: la inicialización durable conserva su propia frontera.

Si `session.input` en una sesión con Run terminal confirma otro Run y después falla
el append separado de UserInputReceived, queda Accepted/error con el rango real del
Run admitido. Al seleccionar un Run diferente se invalidan snapshot y working state
anteriores incluso dentro de la misma Session. No se inventa un snapshot nuevo ni un
UserInputReceived que no se persistió. RunCreated conserva el objetivo enviado; este
caso **no** promete atomicidad de creación del Run y append del mensaje, ni autoriza
retry automático ante Accepted/error.

Evidencia adicional (fixtures offline, no proveedor autenticado):

- `command-admission-boundary-red-test.log`: primer intento 3 FAIL; dos excepciones
  de path reproducidas y fixture de inyección inicialmente con nombre de evento incorrecto.
- `command-admission-boundary-red-corrected-test.log`: 3 FAIL verdaderos; type derivado
  del payload real y comprobación de que la inyección fue consumida. El tercero revela
  `runState=M2:35` del Run anterior donde debería no haber snapshot seleccionado.
- `command-admission-boundary-fixed-build.log`: 0 warnings/0 errores.
- `command-admission-boundary-fixed-focal.log`: 57 PASS/0 FAIL/0 SKIP, 3.640s.
- `command-admission-boundary-full.log`: 1826 casos = 1822 PASS/0 FAIL/4 SKIP por
  permisos symlink, 209.921s, exit0; incluye las tres regresiones nuevas.

Repro focal: `dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*CommandAdmissionBoundaryRegressionTests'`.

## Evidencia reproducible

Logs en `C:\Users\juanc\.codex\omni-m55-three-20261006`:

- `interaction-creation-red-build-fixed.log`: build 0 warnings / 0 errores,
  después de corregir un uso de Assert.Empty que el analyzer rechazaba; mismo predicado.
- `interaction-creation-red-test.log`: 3 FAIL: auditoría de interacción y creación
  fallida con/sin sesión anterior propagaban IOException.
- `first-input-initial-red-test.log`: 2 casos / 1 PASS / 1 FAIL, por identidad de
  intento no retenida e inicialización parcial al fallar la política.
- `interaction-creation-final-build.log`: 0 warnings / 0 errores.
- `interaction-creation-final-focal.log`: 50 PASS / 0 FAIL / 0 SKIP, 1.488 s.
- `interaction-creation-final-full.log`: 1814 casos / 1810 PASS / 0 FAIL /
  4 SKIP por permisos symlink, 210.307 s, exit0. No incluye helper/tests de
  RuntimeBuildIdentity entregados después de este build.

Fault injection de audit/store/state file es fixture. El rollback integrado usa
SQLite real: un trigger temporal de la base de prueba aborta en RunCreated,
las tres filas anteriores desaparecen, synchronous vuelve a NORMAL y un retry
tras retirar el trigger crea once filas contiguas confirmadas en FULL.
Los workspaces y state files son temporales propios. No consultas autenticadas,
consumo real ni cualificación de proveedores.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*SessionInputInitialFailureTests*' -class '*RunCreationSqliteBoundaryTests*' -class '*RunCreationAtomicFailureTests*' -class '*InteractionAuditFailureCommandOutcomeTests*' -class '*RunCreationCommandOutcomeTests*' -class '*InputInteractionCommandOutcomeTests*' -class '*CommandCausal*' -class '*SimulationExceptionalCommandOutcomeTests*' -class '*SimulationCommandOutcomeTests*' -class '*SimulationOutcomeReadFailureTests*' -class '*EffectResolution*' -class '*CanonicalWriterArchitectureTests*' -class '*RunControlCommandOutcomeTests*' -class '*RejectedCommandOutcomeBoundaryTests*'
```

No es cierre de todos los commands ni de M5.5; la cola restante conserva sus gates.
