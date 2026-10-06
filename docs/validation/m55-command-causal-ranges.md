# Rango causal y confirmación durable del command

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
