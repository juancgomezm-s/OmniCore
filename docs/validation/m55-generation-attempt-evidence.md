# Evidencia durable de intentos de generación

Checkpoint integrado de M5/M5.5, 2026-10-07. No redefine ni cierra los objetivos
completos ADR0007/0046/0047.

## Contrato y compatibilidad

`model_step.completed`, `meta_model.invocation_completed` y
`meta_model.invocation_failed` pasan a v3 con `GenerationAttempts` opcional:

- `ObservedGenerationSends`: contador observado de envíos de generación durante
  la invocación; no incluye consultas de catálogo ni tokenización.
- `MaximumGenerationRequestAttempts`: cota positiva declarada por el adaptador,
  capturada antes de llamar al proveedor; null significa desconocida.

El contador debe ser no negativo y no superar una cota declarada. El tracker
rechaza evidencia estructuralmente inválida. Los upcasts v1/v2 conservan null:
no reconstruyen envíos históricos. Los helpers `IsValid` y
`HasCompleteUsageCoverage` no son campos serializados ni recibos de facturación.

Una respuesta cubre la invocación cuando se observó un único envío con cota
positiva, o cuando el contador no instrumentado permanece en cero y la cota
de uno permite inferir una sola solicitud. Este último caso es inferencia
acotada, no un envío medido; cero no demuestra que una llamada fuera gratuita.
Dos o más envíos, contador no observado con cota mayor de uno, o cota ausente
no demuestran consumo completo mediante el uso de la respuesta final.

## Lectura y admisión

El presupuesto finito de tokens de Run exige cobertura completa, además de
input/output reportados y válidos. Evidencia legacy ausente, overflow o llamada
sin resolver dejan el saldo desconocido y bloquean otra admisión; nunca se
convierten en cero. Compactaciones se contabilizan como invocaciones meta.

El reporte de conversación conserva idempotencia por identidad de invocación,
sin sumar nuevamente el resumen final de Turn. Nueva evidencia incompleta hace
desconocidos los totales/coste e incrementa las invocaciones incompletas. Los
recibos legacy mantienen la compatibilidad anterior del reporte y de la lectura
monetaria; no se promueven a evidencia nueva de intentos. La lectura monetaria
retiene la estimación de la respuesta final pero marca consumo incompleto:
ese importe no demuestra el coste de todos los reintentos ni una factura real.

## Evidencia reproducible

### Contrato aditivo de presentación y seguridad del reporte

`UsageSnapshot.SessionTokenMeasurement` es una propiedad opcional de tipo
`Metric<TokenTotals>`: conserva disponibilidad, fuente y fecha del reporte de
la misma sesión. Los clientes nuevos la prefieren a `SessionTokens`, cuyos
campos numéricos legacy no representan incertidumbre. Si la propiedad falta,
se conserva la presentación legacy; no se infiere una medición nueva.
`CurrentUsage` transmite el resultado de `ReadConversation` sin convertir
`Unknown` en consumo cero. El cliente y la status line muestran `session — tok`
cuando la medición no está disponible o el total legacy no es representable.
Esto describe consumo acumulado; no representa ocupación del contexto actual.

Las sumas de tokens y coste se comprueban frente a overflow. Una suma de coste
no representable deja el coste desconocido sin destruir tokens representables;
tokens no representables no se envuelven, truncan ni se presentan como cero.
Un marcador `NotDispatched` elimina una invocación únicamente si existe su
inicio y no hay terminal ni evidencia contradictoria. No constituye un recibo
del proveedor con consumo cero. Marcadores sin inicio y terminales incompatibles
conservan incertidumbre. Dos terminales distintos no pueden reemplazar una
respuesta desconocida por una conocida; terminales idénticos son idempotentes.

`ConversationUsageSafetyTests` añade fixtures SQLite/CAS/reopen para estos
casos, el camino real de rechazo pre-dispatch de `MetaModelService`, recibos
repetidos y aislamiento de sesión incluso con identidad de invocación igual.
También verifica roundtrip JSON del contrato hasta la status line.
`UsagePresentationTests` cubre la presentación de incertidumbre y overflow.
Build `authority-report-build-1052.log`: cero errores/advertencias, 16.54 s.
Focal conjunta `authority-report-focal-1052.log`: 106 PASS, cero FAIL/SKIP,
1.999 s. La suite completa posterior **no es verde**: `authority-report-full-1053.log`
terminó con 2818 casos, 2800 PASS, 14 FAIL y 4 SKIP, 118.228 s. La arquitectura
registró 55 PASS y 1 FAIL por dependencia Domain en la TUI de razonamiento.
Se conservan como defectos abiertos, no se acreditan como cierre ni se omiten.

```powershell
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor -class '*ConversationUsageSafetyTests' -class '*UsagePresentationTests'
```

### Integración con el panel WPF de OmniCoder

`tools/OmniCoderMetricsProbe` usa el proyecto real `OmniCoder.OmniCore` y sus
fuentes `SessionMetricsViewModel`, `SessionMetricsPanel.xaml` y code-behind.
Sus outputs, incluido el proyecto OmniCoder, se redirigen a un directorio
privado mediante `UseArtifactsOutput`/`ArtifactsPath`; no se edita el repositorio
OmniCoder ni su sección Git. `OmniCoreRoot` apunta al worktree autorizado.

`authority-report-omnicoder-probe-1051.log` terminó con exit 0: Host y SQLite,
Explorer, JSON Protocol, ViewModel net8 y binding WPF. Comprueba que razonamiento
no es primer texto, que los terminales retiran indicadores, que contexto y
consumo son distintos, que polling es idempotente y que cambiar de sesión
descarta métricas e indicadores previos. La primera ejecución falló porque
el constructor Explorer no recibió la cota; el fixture single-call ahora la
declara y la transmite explícitamente, sin modificar las assertions.

Es **integración de componentes reales con proveedor scripted**, no consulta
autenticada ni facturación/cualificación real. No se usó `--accounts`. El build
del grafo aislado pasó con cero errores/advertencias, 18.21 s; la recompilación
del probe tras corregir la configuración pasó en 0.97 s. Ese último build usó
`BuildProjectReferences=false` y conservó los componentes compilados del
checkpoint anterior; no acredita cambios de producción posteriores.

```powershell
dotnet build tools/OmniCoderMetricsProbe/OmniCoderMetricsProbe.csproj -p:UseArtifactsOutput=true -p:ArtifactsPath=C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/metrics-artifacts -p:OmniCoreRoot=C:/Users/juanc/.codex/worktrees/m55-artifactrefs-roundtrip/OmniCore
dotnet C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/metrics-artifacts/bin/OmniCoderMetricsProbe/debug/OmniCoderMetricsProbe.dll
```

### Verificación conjunta — 2026-10-07 11:11 UTC

La regresión de atribución multi-Run se corrigió comparando el envelope con el
Run propietario del payload, sin eliminar los controles de scope falsificado.
La dependencia indirecta CLI→Domain se eliminó mediante el overload neutral
de `ActAsync`; arquitectura: **56 PASS, 0 FAIL**, 0.485 s.
Focal conjunta: **201 PASS, 0 FAIL**, 15.751 s. La FULL siguiente encontró dos
fallos de limpieza de `.artifact-gc.lease`: la prueba de preimagen modificaba
`OMNICORE_DATA_DIR` fuera de la colección existente de entorno del proceso.
Se incorporó a esa colección sin quitar assertions ni cleanup, ni desactivar
el paralelismo global. Build final: 0 errores/advertencias, 3.22 s.
FULL final `authority-report-full-1110.log`: **2819 casos, 2815 PASS, 0 FAIL,
4 SKIP**, 120.235 s. Conteos solapados, no sumables.

El grafo completo del probe OmniCoder se recompiló después de las correcciones:
`authority-report-omnicoder-build-1101.log`, 0 errores/advertencias, 13.34 s.
`authority-report-omnicoder-probe-1102.log` terminó con exit 0, incluyendo binding
WPF real, contexto frente a consumo, idempotencia e aislamiento de sesión.
Sigue usando proveedor scripted: no acredita autenticación, facturación real
ni cualificación del proveedor. El cierre íntegro de M5/M5.5 sigue pendiente.

### Checkpoint verificado anterior

Build: cero errores/advertencias, 4.09 s. Focal relacionada: 114 PASS, cero FAIL,
1.472 s. Suite completa: **2780 casos, 2776 PASS, cero FAIL, 4 SKIP** por permisos
de symlink, 118.617 s. Los conteos se solapan, no se suman. Logs:
`C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/`, archivos
`retry-evidence-build-1006.log`, `retry-evidence-focal-1007.log` y
`retry-evidence-full-1007.log`.

```powershell
dotnet build OmniCore.slnx --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor
```

`RunTokenBudgetAdmissionTests` y `MetaModelRetryAttemptEvidenceTests` ejecutan
adaptadores de producción con HTTP controlado, journal SQLite privado y CAS:
503 seguido de éxito, transporte fallido, agotamiento de retries y reapertura
sin volver a llamar al proveedor. `GenerationRequestAttemptEvidenceTests`,
`RunTokenBudgetReaderTests` y `ConversationRetryCoverageTests` verifican codecs,
evidencia legacy, inferencia acotada, snapshots repetidos y aislamiento de sesión.
Los providers scripted de regresión declaran su cota real de una solicitud;
ninguna assertion se rebajó para convertir incertidumbre en éxito.

Son fixtures offline: no acreditan consultas autenticadas, consumo de cuenta,
cualificación real ni integración real con OmniCoder. Siguen abiertos los demás
criterios íntegros: transición UltraCodePolicy autorizada, límites/resolución
completos, contratos pre-M6 aceptados y cualificación real de M5. No se implementa
scheduler, joins ni lógica M6.
