# Frontera de escritores, telemetría y fallos de simulación

## Journal y telemetría — ADR-0046 §4

CanonicalWriterArchitectureTests inspecciona IL de los assemblies productivos
OmniCore presentes en el output de tests. Engine/Host/Infrastructure son
obligatorios: un inventario vacío o parcialmente cargado no cuenta como prueba.
Una llamada a IEventStore.Append/AppendBatch debe pertenecer a EventStream.
Las implementaciones físicas del store pueden delegar en sus propios métodos.
Fuera de Engine/Host se prohíbe construir EventStream o invocar sus writers.

El scanner incluye métodos, constructores y tipos generados por el compilador,
resuelve tokens con sus contextos genéricos y decodifica ambos tamaños de opcode,
operandos y tablas switch. Los controles positivos, que NO ejecutan writes,
contienen un writer directo prohibido, un constructor/append de EventStream
fuera de frontera y un writer dentro de una máquina de estados async.
Los errores de carga/resolución de IL hacen fallar el guard; no se omiten tipos.

También se exige que los ModelStreamEvent concretos no sean DomainEventPayload,
y que ModelStepCompleted siga siendo canónico. TelemetryBoundaryTests conserva
la integración SQLite close/reopen: deltas privados de texto/razonamiento no
llegan al journal, mientras los eventos finales y ModelSteps sí se persisten.
Este control de código compilado no inspecciona reflexión dinámica, SQL generado
fuera de los stores ni assemblies no compilados en este output. La auditoría del
código actual no encontró otro INSERT del journal ni writers fuera de frontera.
No es una garantía sobre extensiones futuras no auditadas.

## Outcomes en caminos excepcionales — ADR-0046 §5

SimulationEngine.Execute acepta una SessionId opcional elegida por Host; los
callers anteriores siguen creando su propia sesión. Host conserva la identidad
del intento incluso si Execute falla antes de devolver RunResult. Así nunca
atribuye una simulación parcialmente escrita a la sesión anterior.

Los catches de RunSim/ResumeSim devuelven un ack correlacionado:

- Si el journal conserva eventos nuevos del command, Outcome=Accepted y
  FirstSeq/LastSeq cubren exactamente esos eventos, aunque Status=error.
- Si no hay eventos nuevos del command, Outcome=Rejected sin rango.
- Una simulación nueva que sólo alcanzó SessionCreated no conserva el Run,
  snapshot ni WorkingState de la sesión anterior.
- Si el resume persiste EffectUnknown y falla antes de Reconciled, el ack incluye
  únicamente lo persistido. El reintento reconcilia sin duplicar EffectUnknown.

No se borra ningún evento ni se anuncia éxito de ejecución después del fallo.
La clasificación anterior requiere un journal consultable. La caída del propio
ReadFrom durante la construcción del ack se cubre en el bloque posterior de
[confirmación durable y rangos causales](m55-command-causal-ranges.md):
Deferred(JournalOutcomeUnavailable), sin inferir cero consumo ni cero efectos.

## Evidencia

Logs bajo `C:\Users\juanc\.codex\omni-m55-three-20261006`:

- simulation-exception-outcome-red-test.log: 2 FAIL antes de implementar outcomes.
- simulation-partial-outcome-red-test.log: 4 casos, 3 PASS/1 FAIL por Run anterior
  conservado en una sesión nueva parcialmente creada. Assertion de identidad
  intacta; corregido el estado seleccionado, no el test.
- writer-architecture-final-build.log: 0 warnings/0 errores.
- writer-architecture-final-focal.log: 45 PASS/0 FAIL/0 SKIP, 1.175 s; incluye
  comandos, cuatro caminos excepcionales, controles IL y telemetría SQLite.
- writer-architecture-simulation-full.log: 1795 casos/1791 PASS/0 FAIL/4 SKIP
  por permisos symlink, 209.877 s; proceso9303 terminó exit0. Mismo build del
  focal, sin los nuevos tests de Source entregados después de compilar.

Son fixtures deterministas de store/audit/proveedor, no consultas autenticadas
ni consumo real. Las cifras de suites y focales se solapan.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*CanonicalWriterArchitectureTests' -class '*SimulationExceptionalCommandOutcomeTests' -class '*CommandOutcome*' -class '*Telemetry*'
```

Source, el resto de los commands de frontera y los demás bloques de la cola
M5.5 mantienen su verificación independiente. Este bloque no cierra M5.5.
