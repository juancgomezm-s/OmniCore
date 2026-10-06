# Gasto sin tope y regresión de recuperación M4

2026-10-06 22:48 UTC / 16:48 America/Mexico_City.

La suite previa y una reproducción aislada fallaban al superar dos minutos
durante el proceso seed de M4ProcessRecoveryTests. No había logs de progreso y
Child solo guardaba stdout/stderr tras salir normalmente.

## Diagnóstico y corrección

- El fixture ahora anuncia el directorio de evidencia antes de arrancar el
  proceso. Cada diez turnos registra UTC, PID, secuencia, tiempo acumulado y
  duración del último turno, vaciando el marcador a disco. Guarda stdout/stderr
  también al matar su propio hijo por fallo/timeout. No toca procesos ajenos.
- La reproducción instrumentada volvió a fallar en 120.358 s. Llegó al turno
  190 en 113.555 s: avanzaba, pero el coste de cada turno crecía con el historial.
- ExplorerTurn reconstruía y verificaba recibos monetarios históricos incluso
  sin topes de Session/día ni MaxCostUsd del Run. Una prueba contada independiente
  reprodujo esa lectura innecesaria (2 casos: 1 PASS / 1 FAIL, 1.123 s).
- La primera optimización (36a5260) omitía el replay sin límite monetario. La suite
  completa posterior detectó cuatro regresiones de overflow: incluso sin topes,
  una suma conocida debe seguir siendo representable. Esa condición era incorrecta.
- La corrección omite SOLO ese cálculo cuando no hay límite monetario, precio
  actual ni costes históricos conocidos. Los summaries legacy sin ModelStep
  requieren validación porque su coste vive en CAS; evidencia ilegible también.
  No omite historial conversacional, contexto, contadores de
  tokens/pasos/tools, validación del uso actual ni publicación de recibos/costes.
- Si se activan topes predeterminados, MaxCostUsd del Run o existe precio/coste conocido, se conserva la
  validación histórica completa. No hay caché de gasto que pueda quedar obsoleta.

Los 200 turnos, el plazo original de dos minutos por hijo y todas las aserciones
de checkpoints/cuestionario/persistencia se mantienen. No se simula restart del
OS ni se declara una cualificación de proveedor real.

## Evidencia

Fixtures explícitos: provider/resumen/usage sintéticos, filesystem/SQLite/CAS
reales privados y procesos dotnet independientes, sin autenticación ni cargos.

- Focal final: 119 PASS / 0 FAIL / 0 SKIP, 8.348 s; build 0 errores/advertencias.
  Incluye control de no-tope, topes predeterminados y tope de Run, con los costes
  y tokens completos todavía publicados en ambos ModelSteps.
- M4 final aislado: 1 PASS, 94.749 s. Seed completó 200 turnos en 89.718 s,
  validado en 89.928 s; restore independiente y journal verifier pasan.
- Verificador: 1 sesión, 1663 eventos, 1849 referencias de artifacts — OK.
- Arquitectura: 56 PASS, 1.174 s; build 0 errores/advertencias.

Evidencia RED preservada:
C:/Users/juanc/AppData/Local/Temp/omnicore-m4-process-d004f3706cac43e18175accf22383eaf/

Evidencia GREEN preservada:
C:/Users/juanc/AppData/Local/Temp/omnicore-m4-process-df39cef41c96498c98bb8d720b00d56e/

Logs en C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/:
m4-instrumented-*, uncapped-replay-red-*, uncapped-replay-final-*,
m4-uncapped-optimized-test.log y uncapped-replay-architecture-*.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*UncappedSpendReplayTests' -class '*Budget*' -class '*Spend*'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*M4ProcessRecoveryTests'
```

No recompilar sobre el mismo output mientras se ejecutan los tests en Windows.
Suite posterior a 36a5260: 2258 casos / 2250 PASS / 4 FAIL / 4 SKIP,
314.973 s. Los cuatro FAIL son los controles existentes de overflow monetario
sin defaultcaps (legacy y ModelStep, gasto previo y gasto actual).
Corrección focal: 54 PASS / 0 FAIL / 0 SKIP, 8.248 s; build 0 errores/advertencias.
Incluye los controles de overflow sin modificar sus assertions y cinco variantes
de replay, incluida historia con precio seguida de modelo actual sin precio.
No transforma costes desconocidos en cero en los eventos ni en el reporting.
M4 sobre la corrección: 1 PASS / 0 FAIL, 94.418 s, mismos 200 turnos y plazo.
Arquitectura sobre la corrección: 56 PASS, 1.381 s.
Logs: monetary-replay-correction-build/test/m4-test/architecture-test.log en el
directorio de evidencia anterior. Suite completa sobre 5fa5cf2:
2260 casos / 2256 PASS / 0 FAIL / 4 SKIP por permisos symlink, 318.331 s.
Log: monetary-replay-correction-full-test.log. Estos resultados no cierran M5.5 solos.
