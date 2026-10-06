# M5.5 — captura filesystem anterior al efecto

Verificado 2026-10-06 22:01 UTC / 16:01 America/Mexico_City.

## Implementación y contrato

La composición normal `OmniHost.CreateActExecutor` proporciona CAS al executor.
La CLI reutiliza su mismo store de workspace. Después de autorización y validación
de frontera, `filesystem.write` y `filesystem.patch` capturan el contenido anterior
antes del `ToolCallStarted` Barrier y antes de ejecutar la mutación.

`BeforeStateRef` referencia JSON versionado con existencia, encoding, texto,
hash original y longitud original. Media type:
`application/vnd.omnicore.filesystem-preimage+json`; Kind Other, Sensitivity Sensitive.
Se verifica reconstrucción byte por byte para UTF-8 sin/con BOM y UTF-16 LE/BE con
BOM, preservando saltos de línea. Ausencia y archivo vacío son estados distintos.
No se almacena base64 ni se añade una vía raw que evada la redacción.

El texto decodificado se inspecciona antes de serializar. Si la redacción cambia
el contenido, o CAS publica un resultado incompleto/no fiel, se rechaza antes del
efecto con `ToolCallFailed`, EffectOutcome.None y mensaje constante sin contenido.
Sensitivity.Sensitive es clasificación, no cifrado. No se promete captura fiel de
contenido que debe redactarse. La comprobación expectedVersion sigue rechazando
cambios concurrentes posteriores a la captura.

`IArtifactPublicationLease` es una capacidad opcional **síncrona**: adquirir,
PutText y liberar en el mismo thread. FileArtifactStore mantiene el lock usado por
GC desde la publicación hasta después del evento durable (actualmente durante la
ejecución completa de la herramienta). Así GC con gracia cero no elimina un blob
todavía sin referencia canónica. El runtime rechaza stores incapaces de proteger
esta publicación cuando se solicita captura; no continúa con un falso éxito.

Solo una mutación de hoja con ancestros existentes y sin reparse points se marca
Reversible. Crear directorios o recorrer links requiere evidencia de topología y
permanece Unknown, sin BeforeStateRef. Executors primitivos sin CAS conservan
Unknown. No se implementa restore físico M7 ni reversibilidad universal.

## Evidencia

Root implementó producción y 18 casos nuevos; Luna HIGH auditó el contrato y
detectó la ventana publicación/GC y el límite de topología. Tests con filesystem,
SQLite y CAS reales privados, proveedores simulados: no acreditan autenticación
ni consumo real de modelos.

- Antes de implementar: 8 casos / 8 FAIL, 1.539 s; faltaba captura en factory normal.
- Final focal: 147 PASS, 0 FAIL, 0 SKIP, 14.546 s; build sin warnings/errores.
- Arquitectura: 56 PASS, 0 FAIL, 0 SKIP, 1.202 s; build sin warnings/errores.
- Los controles verifican contenido antes del Barrier, bloqueo de GC concurrente
  con gracia cero antes del append, reapertura, scope conservado, GC posterior que
  conserva preimagen y elimina orphan privado, errores CAS, secretos registrados,
  ausencia/vacío, creación de ancestros y rechazo de versión obsoleta.
- No hay full fresca verde. Existen regresiones separadas de cuestionarios y
  mantenimiento M5. Los resultados focales se solapan y no se suman.

Logs: `C:\Users\juanc\.codex\omni-m55-workers-20261006-2103\preimage-*`.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*FilesystemPreimageIntegrationTests' -class '*ToolCallStartedV3ContractTests' -class '*FilesystemWriteToolTests' -class '*FilesystemPatchToolTests' -class '*M3ExecuteToolBarrierTests' -class '*M3ActVerticalTests' -class '*CliEndToEndTests' -class '*RecreatedBoundaryValidationRegressionTests' -class '*MaintenanceTests'
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore -v quiet
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll -noLogo -parallelMode none
```
