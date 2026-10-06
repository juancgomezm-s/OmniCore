# Mantenimiento de cualificación — terminales y redacción

Verificado 2026-10-06 22:20 UTC / 16:20 America/Mexico_City. M5 permanece
cerrado como hito; este bloque corrige regresiones, no redefine sus gates.

## Cambios

- ProbeRunner detiene el stream al recibir ResponseFailed y no acepta una
  completion posterior para convertir el fallo en Passed. Conserva usage/coste
  válidos ya observados; no inventa usage de eventos posteriores al fallo.
- Una ResponseCompleted con Error/Cancelled/ContentFilter/Refusal/ContextOverflow/
  ToolUse/InputRequired no se puntúa como éxito aunque coincida el texto. EndTurn,
  StopSequence y MaxOutputTokens siguen pasando por el oráculo exacto, no por una
  regla blanket de EndTurn únicamente.
- Finalización normal conserva el recorrido/cancelación existente del stream:
  no se omiten las comprobaciones del caller antes de publicar evidencia.
- ModelQualificationSuiteIncompleteException redacta y captura una copia inmutable
  de Failures antes de componer Message. Mutaciones del caller no alteran su reporte.
- CLI dispone su qualification Host; dos consultas del runtime disponen su servicio
  de políticas. Los fixtures liberan únicamente sus pools privados con la cadena
  exacta del store; no ClearAllPools ni cleanup que oculte errores.

## Evidencia

Luna HIGH aportó fixtures/auditorías; root auditó, corrigió compilación/cleanup,
implementó producción y reprodujo. Providers scripted y HTTP loopback normales
con auth none; no credenciales, consultas autenticadas, factura o consumo real.

- RED previo: 17 casos / 12 FAIL; once fallos funcionales y un cleanup CLI que
  ocultaba la assertion. Tras corregir conexión/pool, RED3 de redacción confirmó
  filtración en Host, CLI y constructor (maintenance-cleanup2-test.log).
- Terminales corregidos pasaron los 14 casos de terminales/incomplete dentro de
  selección de 19; errores de redacción/fixture presupuestario quedaron separados.
- Primer focal amplio reveló 2 fallos de cancelación: dejar de consumir el stream
  en ResponseCompleted omitía cancelación. Se corrigió sin debilitar assertions.
- Final: 167 PASS / 0 FAIL / 0 SKIP, 7.662 s, build 0 warnings/errores.
- Arquitectura: 56 PASS / 0 FAIL / 0 SKIP, 1.221 s, build 0 warnings/errores.

La full diagnóstica anterior a estas correcciones terminó 2240 casos:
2223 PASS / 13 FAIL / 4 SKIP, 365.200 s. Doce fallos de mantenimiento y un timeout
M4ProcessRecovery. Este último se repitió aislado: 1 FAIL, 120.363 s, en el límite
original de dos minutos del child; no assertion de estado. La causa sigue pendiente.
No se acredita full verde después de este bloque. Un build paralelo con esa prueba
falló por bibliotecas bloqueadas; no fue fallo de producción, y se repitió solo
después de confirmar ambos procesos terminales.

El fixture nuevo de presupuesto entre workspaces queda fuera de este commit:
RED válido posterior 2 casos / 1 PASS / 1 FAIL, 2.428 s, build0/0. Composición
TuiTurnHost/OmniCliRuntime real y HTTP privado CreditBalance declarado: A registra
USD0.60 hoy con tope USD0.50; B alcanza al provider antes de bloquear por su propio
gasto. El control sin gasto previo pasa. No es una reserva ni una factura real.

Logs: `C:\Users\juanc\.codex\omni-m55-workers-20261006-2103\maintenance-*`,
`m55-post-attribution-full-diagnostic.log`, `m4-post-attribution-isolated-test.log`,
`cross-workspace-budget2-*`. Las selecciones se solapan y no se suman.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*M5Qualification*' -class '*M5ResponseFailureTerminalCorrectionTests' -class '*ModelQualificationHostTests' -class '*ModelQualificationCliTests' -class '*QualificationQuickSuiteTests' -class '*ModelPolicyCliTests'
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore -v quiet
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll -noLogo -parallelMode none
```
