# Recuperación M4 — verificación serial y margen del límite

2026-10-09, America/Mexico_City (mediciones entre 22:06 y 22:34; 2026-10-10 UTC).
Windows 10.0.26300, AMD Ryzen 9 8945HS, 16 procesadores lógicos, 15661 MiB de RAM;
SDK 10.0.401, runtime .NET 10.0.12, x64.

Base: `629b9880075207bebfd45ce453a48152f2f09bbe`, posterior al merge A1–A12
`23a9eba`. Corrección verificada: `ae0cf114f3545edf17352bdf258764abd6175b5f`.
Trabajo realizado en la rama aislada `codex/m4-recovery-verification`.

## Mediciones

Se compiló primero la solución y se ejecutó **solo** `M4ProcessRecoveryTests`,
tres veces consecutivas, sin otra suite concurrente. Después se repitió el mismo
procedimiento sobre la corrección. El test conserva **200 Turns**, dos procesos
independientes y el **límite original de 120 segundos por hijo**.

Las columnas seed/restore son los tiempos de ejecución que informa xUnit en los
logs de cada proceso hijo; no incluyen su arranque y descubrimiento. El total
incluye también el runner `dotnet test` y el muestreo externo. Todas las corridas
terminaron exit0, por lo que también cumplieron el deadline del proceso completo.

| Versión | Corrida | Seed (s) | Restore (s) | Total observado (s) |
| --- | ---: | ---: | ---: | ---: |
| Base `629b988` | 1 | 102.503 | 2.172 | 111.460 |
| Base `629b988` | 2 | 101.125 | 2.203 | 105.835 |
| Base `629b988` | 3 | 105.354 | 2.310 | 111.190 |
| Corrección `ae0cf11` | 1 | 60.838 | 2.370 | 67.281 |
| Corrección `ae0cf11` | 2 | 56.962 | 2.194 | 61.753 |
| Corrección `ae0cf11` | 3 | 59.467 | 2.382 | 64.193 |
| Comparación `1f16c63`, worktree independiente | 1 | 98.225 | 2.160 | 105.791 |

La mediana de seed baja de 102.503 a 59.467 s (**42 %**). Las tres mediciones
finales están por debajo de 72 s, el 60 % del límite. No se aumentó el timeout,
no se redujeron los Turns y no se debilitaron assertions.

Cada restore verificó **1 sesión, 1664 eventos, 1849 referencias a artifacts y
cero problemas**. El checkpoint conserva cursor de compactación creciente y
hechos durables; el snapshot sigue acotado a 3000 tokens. El objetivo, el trabajo
pendiente y el único cuestionario pendiente sobreviven al proceso nuevo. Los
payloads del journal son idénticos antes y después de restaurar: no se añade una
respuesta ni se reejecutan Turns. Esta es la recuperación automática de M4; no
se repitió aquí la aceptación visual de terminal del cierre original.

## Carga y atribución

Qwen estaba activo en `llama-server.exe`, PID 31112, durante todas las mediciones.
No se detuvo ningún proceso del usuario. Su consumo fue solo **0.17–0.48 segundos
de CPU por corrida**: que estuviera activo no permite atribuirle la lentitud.
Hubo actividad de `node`, MSBuild y VBCSCompiler. Se muestrearon procesos y sus
contadores de CPU durante las corridas, además de carga total y memoria libre.

| Corrida | CPU total media/máxima (%) | RAM libre mínima (MiB) |
| --- | ---: | ---: |
| Base 1 | 33.94 / 91 | 1025 |
| Base 2 | 53.29 / 90 | 1172 |
| Base 3 | 42.33 / 85 | 1338 |
| `1f16c63` | 44.71 / 87 | 1123 |
| Corrección 1 | 48.82 / 82 | 492 |
| Corrección 2 | 33.60 / 65 | 669 |
| Corrección 3 | 41.40 / 87 | 459 |

La comparación anterior a A1–A12 también va justa: 98.225 s frente a la mediana
actual de 102.503 s, una diferencia de 4.278 s. Es una comparación bajo carga
variable, no un A/B controlado que aísle cada cambio. No reproduce los timeouts
históricos ni identifica su causa ambiental exacta; sí descarta interpretar el
problema completo como una regresión nueva del watchdog.

El progreso de la primera corrida muestra crecimiento por Turn: 268.6 ms en el
Turn 50, 876.7 ms en el 170 y 1054.1 ms en el 200. Se perfiló otra ejecución de
seed con `dotnet-trace` 10.0.750501, perfil `dotnet-sampled-thread-time`, buffer
32 MiB; terminó correctamente en 104.741 s. `SqliteEventStore.ReadFrom` aparece
en el 6.74 % de todas las muestras, frente al 12.88 % de `ExplorerTurn.Ask`
(aproximadamente el 52 % relativo). `EventCodecs.Decode` representa 2.92 % y
`ApplyStallWatchdog`, 0.18 %. Son muestras de stacks/tiempo de hilos, incluyen
esperas y **no son porcentajes de utilización de CPU**.

## Corrección

`EventStream.EventsSince` conserva un snapshot de envelopes por instancia y
consulta el sufijo desde la siguiente secuencia en cada lectura. Una petición
anterior al rango conservado reconstruye el prefijo; un retroceso del cursor
por purga descarta el snapshot. Las lecturas devuelven colecciones independientes
y siguen viendo appends desde otros streams/conexiones. Los dobles sin secuencias
canónicas mantienen el recorrido original.

Se elimina la rematerialización repetida del prefijo de SQLite dentro de un
stream. El snapshot vive mientras vive ese `EventStream`; no es una cache global
ni modifica el journal, sus commits Barrier, los payloads o las reglas de
admisión. Las proyecciones aún pueden recorrer la historia: no se afirma que
todo el runtime tenga coste lineal en sesiones arbitrariamente largas.

Tres tests deterministas nuevos comprueban coste de materialización lineal
(220 eventos materializados para 21 lecturas crecientes), estabilidad de los
snapshots, rangos anteriores/futuros, visibilidad entre conexiones SQLite,
aislamiento entre sesiones y purga. No se añadió configuración nueva.

## Verificación final

- Build de la solución con `-m:1 --no-restore`: **0 errores y 0 advertencias**.
- Clases `EventStream*`, en serie: **13 PASS**, exit0.
- Recuperación M4 con la corrección, en serie: **3 de 3 PASS**.
- Suite completa **sin M4**, sin TTY y con stdin redirigido/cerrado:
  **3454 casos: 3450 PASS, 0 FAIL, 4 SKIP**, exit0, **392.445 s** según el runner.
  Arquitectura incluida y correcta. Los SKIP son las restricciones habituales
  de symlinks de Windows. Son suites solapadas; no sumar sus recuentos.
- La suite anterior de `23a9eba` había registrado un timeout intermitente de
  delegaciones; esta corrida serial no lo reprodujo. Una pasada no acredita
  ausencia universal de carreras.
- La suite mantuvo carga: CPU media 51.49 %, máxima 91 %, RAM libre mínima
  369 MiB. No se ejecutó una segunda suite completa.
- Los tests conservaron el aislamiento temporal de
  `OMNICORE_DATA_DIR`/`OMNICORE_CONFIG_DIR`; no se tocaron datos del usuario.

```powershell
dotnet build OmniCore.slnx -m:1
dotnet test --project tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --filter-class "OmniCore.Tests.M4ProcessRecoveryTests"
dotnet test --solution OmniCore.slnx --no-build --timeout 25m --filter-not-class "OmniCore.Tests.M4ProcessRecoveryTests" --parallel none --max-parallel-test-modules 1
```

Árboles verificados: `src` = `fc44ccb4f09839fbeae66b6d6a20b68b2e05b5c9`;
`tests` = `fbdb180967221d04c106269342039b4e725943bb`.
SHA256 coincidentes antes y después de la suite:

| Binario Debug/net10.0 | SHA256 |
| --- | --- |
| `OmniCore.Tests.dll` | `263E64A409F36935AB166BC346B756D1D3966D3B2DE7FB83EDDCB636AE5C114B` |
| `OmniCore.ArchitectureTests.dll` | `954863170027D0E40BEFAFF69D1C5E7946E300467B19D58F4358E7A9BC260D9E` |
| `OmniCore.Engine.dll` | `3CBF187461CE44ACF65B31A2DA0ECEF05CC2E7F0288DA3142763D5B142788C20` |

## Evidencia local

Logs, snapshots de carga, resúmenes, traza y manifests en
`C:/Users/juanc/.codex/m4-recovery-20261009/`: `initial-{1,2,3}-*`,
`optimized-{1,2,3}-*`, `baseline-1-*`, `profile.log`, `seed.nettrace`,
`profile-inclusive.txt`, `profile-exclusive.txt`, `integrity-results.json`,
`load-stats.json`, `full.log`, `full-summary.json`, `full-load.json` y
`manifest-{before,after}-full.json`. Los resúmenes conservan las rutas de los
fixtures independientes con SQLite, CAS y sus queries restauradas.

Builds/focales en `C:/Users/juanc/.codex/m4-recovery-{build,build-fix,read-tests}-20261009.log`
y `m4-baseline-build-20261009.log`. Scripts de medición:
`m4-measure-20261009.ps1` y `m4-full-20261009.ps1` en el mismo directorio `.codex`.

La verificación corresponde al árbol comprometido de la rama aislada. Los
cambios OAuth ajenos sin commit en `main` se conservan y no forman parte de
estos binarios ni de este cierre.
