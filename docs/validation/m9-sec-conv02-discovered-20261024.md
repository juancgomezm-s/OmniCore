# SEC-CONV-02: recuperación de planes Discovered

**Resultado:** aceptación local independiente en Windows con `ScopedCoder`, **2/2 casos aprobados** sobre la candidata final, sin recompilar durante la validación. No se cambió producción. No atribuye ni resuelve el fallo genérico de un intento histórico cuyo journal/snapshot no se conservaron. SEC-CONV-02, SEC-CONV global, M9 y M8 manual siguen abiertos.

Base local: `2aa4d3da98989758935ef7ee82cb7dd6d0d799b5`; incremento limitado a pruebas y documentación, registrado en un checkpoint local posterior.

## Recorrido ejecutado

Se reutilizó `NativeChoiceFixture` de `StdioTransportTests.NativePermissionPlanApproval.cs`: Host/CLI real en proceso OS separado, protocolo stdio firmado, workspace/data/config temporales, y proveedor HTTP scripted. El ejecutable hijo fue `tests/OmniCore.Tests/bin/Debug/net10.0/omni.dll` (hash por intento en el manifiesto). La política efectiva del modelo y el default de Session se configuraron como el preset `ScopedCoder`; el test verifica `ToolMode.Discovered` y `AllowToolDiscovery == true`. No se usó `PatchOnly` como positivo ni se modificó su política.

En cada variante, el proveedor siguió la misma secuencia: `tool.search` descubre `filesystem.patch`; `filesystem.read` lee el archivo fixture; `filesystem.patch` presenta preimagen y `expectedVersion` calculados sobre esos bytes y solicita permiso. Una acción nativa firmada para la Session/Run/revisión pendiente responde con `allow_once` o `allow_workspace`. El test comprueba la vida del Turn, el evento `PermissionGranted` con el lifetime esperado, `ToolCallSucceeded`, el contenido final y exactamente cuatro solicitudes al proveedor (tres antes del Ask y una continuación).

Antes de la aprobación, el test valida y registra el `ModelStepStarted` más reciente del Turn: snapshot CAS íntegro, componente `tools.plan` versión 3, `componentHash == artifactHash`, tamaño/hash del artefacto de plan y presencia de `filesystem.patch`. Después de aprobar, compara el alias de función emitido por la llamada descubierta con los nombres de herramientas en la petición real de continuación al proveedor. El alias continúa en esa petición. Este recorrido demuestra recuperación válida de la lista descubierta bajo la política permitida; no instrumenta ni identifica cuál guarda habría rechazado el intento histórico.

## Resultado de recuperación y límites

En las dos variantes la continuación conservó la función descubierta y el efecto terminó correctamente. En `TryRecoverDiscoveredToolPlan`, esto es consistente con pasar las verificaciones del snapshot/CAS, versión y hashes, mapeo canónico y visibilidad de las herramientas, equivalencia con el plan efectivo y fingerprint reconstruido. El test no fuerza ni rebaja ninguna de esas comprobaciones.

El error genérico observado en el intento anterior continúa sin causa conocida: no existe snapshot conservado de ese intento que permita distinguir CAS/versión, visibilidad/elegibilidad, equivalencia o fingerprint. El nuevo caso usa `ScopedCoder`, que permite discovery. No demuestra que el fixture histórico usara el mismo estado/política, ni acredita discovery bajo `PatchOnly`, GUI/Electron, live o plataformas adicionales. No hubo cambio de producción.

## Aislamiento y preservación

El Host hereda `OMNICORE_AGENT_HOME` apuntando a un directorio temporal vacío; el valor previo se restaura al terminar. `ExternalAgentDiscoveryCatalog.ConfigRoot` mantiene aislados en ese home los overrides heredados de `CODEX_HOME`, `CLAUDE_CONFIG_DIR`, `XDG_CONFIG_HOME` y `OPENCODE_CONFIG_DIR` que quedan fuera de él. El workspace temporal no contiene instrucciones de proyecto: solo el archivo de patch fixture. El resultado verificado en los paquetes es un `agent-home` vacío y ese único archivo en `workspace`.

Los paquetes por intento en `artifacts/m9-sec-conv02-discovered/` contienen el backup SQLite hecho con `BackupDatabase` después de detener el Host, el árbol completo data/CAS del fixture, config fake, workspace, stderr, cuerpos HTTP del proveedor fixture, transcript stdio derivado con `trustedActionNonce` redactado y una lista SHA-256 de los archivos capturados. El transcript está identificado como redacción/derivación, no como bytes originales. El manifiesto incluye hashes de fuentes relevantes y de los binarios realmente ejecutados (`OmniCore.Tests.dll`, `OmniCore.Host.dll` y el `omni.dll` hijo). El primer intento anterior al aislamiento `OMNICORE_AGENT_HOME` se eliminó sin inspeccionar sus contenidos y no forma parte de la evidencia.

## Revisión independiente y corrección de cleanup

Low detuvo la revisión antes de lanzar casos (0/2) porque `DisposeAsync` usaba el token cancelable de xUnit y podía saltarse la restauración de `OMNICORE_AGENT_HOME`, otras variables, CWD y la clave si una espera o el cierre del provider fallaban. Se cambió solo la fixture: el Host propio se termina y espera con un token interno, se intenta el cierre del provider, la puesta a cero de la clave, cada restauración y la limpieza en pasos independientes; los fallos se propagan al final. La limpieza borra únicamente el pool SQLite de `data/user.db` del fixture, que quedó bloqueado en la primera ejecución tras aplicar el manejo de errores; no usa `ClearAllPools` ni modifica pools ajenos.

La ejecución de diagnóstico antes de limpiar ese pool acabó 0/2 PASS y 2/2 FAIL por `IOException` al borrar `user.db` en `DisposeAsync`; no se cuenta como aceptación aunque el stack la sitúa en teardown. Después de añadir `SqliteConnection.ClearPool` para la cadena exacta de ese archivo, se hizo un build net10 y la teoría focal pasó 2/2. Una revisión estática posterior encontró que la segunda espera tras reintentar `Kill` era ilimitada; se sustituyó por otra espera de 10 segundos, que registra `TimeoutException` y continúa la restauración si el Host no termina. La última variante se compiló y quedó congelada; solo Low ejecutó la teoría en esa imagen final, con 2/2 PASS. La revisión confirmó en los cuerpos HTTP que el alias de `filesystem.patch` no está en la primera petición y aparece tras `tool.search`. Los caminos excepcionales de cleanup se revisaron estáticamente; esta campaña no indujo rechazos del SO a terminar el proceso ni fallos del provider al disponerlo.

## Campaña y archivos

- La campaña inicial de construcción había pasado 2/2 antes de la revisión Low; no es la validación independiente.
- Build de la variante final con espera secundaria acotada: `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -f net10.0 --no-restore`, 0 advertencias/errores. Low ejecutó el runner directo una vez sobre esa imagen, sin recompilar: 2 casos, 2 PASS, 0 errores/fallos/omisiones/no ejecutados. No se suman ejecuciones de candidatas previas al conteo de aceptación final.
- Hubo además un recorrido preliminar aislado `allow_workspace` durante desarrollo, antes de añadir la comprobación explícita del artefacto/fingerprint y el control `allow_once`; su paquete se conserva marcado como preliminar. No se suma al conteo final anterior. El intento anterior a aislar `OMNICORE_AGENT_HOME` se eliminó sin inspeccionar su contenido y no es evidencia.
- Sin net8: solo cambiaron pruebas/fixture de tests; no cambió una biblioteca compartida.
- Fuentes cambiadas: `tests/OmniCore.Tests/StdioTransportTests.NativePermissionPlanApproval.cs` y `tests/OmniCore.Tests/CliEndToEndTests.cs` (captura de requests/responses del provider fixture); producción intacta.
- Evidencia por intento, snapshots, hashes y logs: `artifacts/m9-sec-conv02-discovered/`.

## Evidencia de la candidata final

El informe independiente es `artifacts/m9-sec-conv02-discovered/low-final-validation-20261024.md`, SHA-256 `859DC29A839BBF257B12777D61A1F233B4DABF5E6E0783DD91335FE6C7E2ADA3`. El manifiesto congelado `final-build-manifest.json` tiene SHA-256 `0EBCDF544572842BC0EEA9113AEA56854B3B5FCC02A921070E65A1B27EC74260`; su indicación de prueba pendiente describe el momento de entrega a Low, no el resultado posterior. El log independiente es `low-final-20261024.log`.

| Elemento | SHA-256 |
|---|---|
| Tests DLL final | `A606219A73BE9B5BFBD605D6D7A92C901E202BD9A9141144D25734ABDAD8BEB3` |
| Host DLL ejecutada | `860C7D7674EDF544A3155704C4FC6943EB43168A5DB05125A7B73A4B805206AE` |
| CLI `omni.dll` hijo | `2A39C29E71EB33334EB05588D141C62E4E108F7E7AB8428095A6AFCC16DDDDB6` |

Paquetes preservados de la ejecución final de Low, dentro del directorio de evidencia:

- `20261024T184652098Z-50b6296ec2f541fb9a54cfb62b450b14`.
- `20261024T184654659Z-d9637c00086841d786eaf14bf7c0056b`.

Low verificó fuentes/binarios/archivos capturados contra los manifiestos antes y después, `integrity_check=ok` para ambos backups SQLite, cuatro requests/responses por variante, workspace de un archivo y home vacío. No quedaron Hosts hijos de la candidata. `x` conserva su hash previo y no forma parte del cambio. Los paquetes permanecen locales e ignorados por Git; este documento conserva sus identificadores y hashes.

Tras la aprobación, root copió la imagen completa de salida a `artifacts/m9-sec-conv02-discovered/frozen-image-A606219A/` y verificó SHA-256 de los 225 archivos contra sus originales, sin compilar ni ejecutar más pruebas. Así se conservan los bytes ejecutables y dependencias, no solo sus hashes en rutas que futuras compilaciones podrían sobrescribir. Inventario: `preserved-image-manifest.json`, SHA-256 `30C0F8FD7F56ED784A83D34518AF1329A2ED1B04E88F45A2962CB09EDB3EAE8F`.

Checkpoint local únicamente: sin push, merge, CI remota, instalación ni cambios de OmniCoder.
