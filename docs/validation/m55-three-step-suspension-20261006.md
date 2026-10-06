# M5.5: tres ModelSteps con suspensión, mismo Turn y uso completo

Checkpoint: 2026-10-06 21:29 UTC / 15:29 America/Mexico_City.

## Requisito y alcance

Primer criterio de salida de ADR0046: un Turn con tres pasos y una suspensión
produce tres `ModelStepCompleted`, un único `TurnId` y todo el uso en el gasto derivado.

`M55ThreeStepSuspensionTests` integra `ExplorerTurn`, ejecutor de tools, servicio de
cuestionarios, respuesta por `OmniServer`, SQLite, CAS y `SessionUsageReporter`.
Solo el modelo es scripted. Root construyó el control; las propuestas externas
GLM/Nemotron/DeepSeek no aportaron código aceptado a este bloque.

1. Paso0 invoca `fake.read`; paso1 recibe su resultado y solicita `user.ask`.
2. La suspensión tiene dos completions durables y ninguna invocación tercera ni
   resumen final. El reporter ya muestra entrada30/salida5 y coste0.000040USD.
3. Se cierra y reabre SQLite/CAS, compara el prefijo completo y recupera el mismo
   uso. `OmniServer` acepta la respuesta al cuestionario original.
4. Paso2 recibe el resultado de la ToolCall original y termina el mismo Turn.
   Tres índices0/1/2, un TurnStarted/TurnCompleted/ModelCompleted, sin abandono.
5. Cada completion conserva uso/máscara, precio calculado y referencia CAS válida
   en su envelope. El resumen final conserva todos los contadores, incluido reasoning.
6. Segundo close/reopen conserva todos los envelopes y secuencia. Leer varias veces
   snapshots no añade eventos, duplica gasto ni cuenta el resumen como otra llamada.

Uso total: entrada60, salida9, cacheRead12, cacheWrite6, reasoning5. Cache/reasoning
son subconjuntos: total69, no92. Tres invocaciones completas; coste estimado desde
tarifas declaradas1USD/M entrada y2USD/M salida:0.000078USD. No es una factura.

## Reproducción y límites

Prueba aislada1 PASS,0 FAIL,0 SKIP (1.354s), build0 errores/advertencias.
Focal ModelStep/questionnaire/replay/reporter/atribución:84 PASS,0 FAIL,0 SKIP (5.776s).
Los intentos
iniciales tenían errores de compilación del fixture (inferencias de Serialize y
firma de Verify); no se acreditan como regresión RED de producción. Tras corregir
el fixture, el criterio pasa sin cambios productivos.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*M55ThreeStepSuspensionTests'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*ModelStep*' -class '*Questionnaire*' -class '*ExplorerTurnDurableProviderStateTests' -class '*M55ThreeStepSuspensionTests' -class '*ConversationUsage*' -class '*ToolCallMultiRunDurableAttributionTests'
```

Logs: `C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/three-step-suspension-*`.
La full2181 de cancelación pasó antes de agregar este Fact: no incluye este control
ni se atribuye a él. La verificación focal sí usa un build fresco con el nuevo test.

No acredita provider autenticado, presupuesto monetario reservado ni otros seis
criterios completos de ADR0046. ToolCallStartedv3, opaco que atraviese redacción,
ledger/reservas, contratos congelados y auditoría integral de commands siguen
pendientes según el plan. No habilita scheduler/joins M6 ni restore M7.
