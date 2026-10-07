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
