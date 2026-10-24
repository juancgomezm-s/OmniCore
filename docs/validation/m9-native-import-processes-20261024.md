# M9 Native/Imported entre procesos OS (2026-10-24)

**Estado:** aceptado localmente por Luna bajo: **3/3 casos aprobados**, sin errores, fallos, omisiones ni casos no ejecutados, sobre binarios congelados y sin recompilar durante la revisión. Se apoya en el bloque Native/Imported aceptado en `docs/validation/m9-native-import-20261024.md`; no repite ni suma su campaña 16/16.

## Hueco concreto

La aceptación existente verifica `CliApp.RunAsync` dentro del proceso xUnit y dos runtimes/conexiones SQLite en un mismo proceso. No acredita el ejecutable `dotnet omni.dll conversation import <archivo>` como proceso separado ni dos Hosts stdio OS separados compartiendo el receipt SQL. Este bloque cubrirá esos límites con los binarios del worktree y fixtures propios en Windows.

## Casos propuestos

1. **CLI OS separado, archivo explícito y cero efectos.** Preparar workspace, config, data, `OMNICORE_AGENT_HOME` vacío y provider HTTP fixture en temporales. Ejecutar el `omni.dll` construido como `dotnet omni.dll conversation import <archivo>` y comprobar salida 0, Session Imported/ObserveOnly/ImportReview, un evento de creación/importación, ningún Run/Input/hook y cero requests al provider. Repetir la entrada con un transcript malformado y comprobar salida 2 sin segunda Session ni evento parcial. No invoca provider, hooks ni herramientas durante importación.
2. **Dos Hosts OS comparten un receipt durable.** Iniciar dos procesos `dotnet omni.dll host --stdio` sobre el mismo workspace y data/config fixture. Obtener un nonce distinto por Hello y firmar para ambos el mismo CommandId y payload canónico. Enviar ambos comandos desde una barrera de inicio, medir los intervalos request-response y verificar el ACK final por consulta/replay estable en ambas conexiones, un único receipt completado, una Session y un evento `conversation.transcript_imported`, estado Imported/ObserveOnly/ImportReview y cero Run/Input/hook/provider.

La campaña afirma solapamiento cuando los intervalos medidos de ambas peticiones se superponen; no afirma que las dos transacciones SQL entraran simultáneamente a la reserva. No se añadieron seams, delays ni hooks para forzar la carrera.

## Aislamiento y preservación

Todo el workspace, config, data, CAS, `OMNICORE_AGENT_HOME` y archivos transcript viven bajo una raíz temporal propia. El provider HTTP fixture usa contenido falso y debe registrar cero solicitudes. No se consulta ni copia contenido del perfil/Home del usuario ni de OmniCoder. Las claves bootstrap y nonces se mantienen en memoria/fixtures y se omiten de logs y manifiestos; los logs conservan hashes e IDs seguros. Después de detener los Hosts propios, la DB se preserva con backup SQLite consistente y el árbol fixture/CAS y protocolo se conserva en `artifacts/m9-native-import-processes/`. Los manifiestos separan bytes originales de copias redactadas. Cleanup usa espera propia acotada y restaura entornos aun si falla otro paso.

## Límites

Windows local solamente. La prueba de CLI es proceso OS separado pero no aceptación Electron. Dos Hosts son procesos OS separados en un mismo workspace y data temporal; el intervalo concurrente se reporta solo si fue observado. No acredita proveedores live, otros sistemas operativos, formatos de terceros, efectos durante importación ni cierre global SEC-CONV/M9/M8. Sin cambios de producción salvo defecto concreto demostrado por estos recorridos.

## Defecto observado y fix focal

La primera ejecución OS con dos Hosts reprodujo un defecto: con el mismo CommandId y payload, un Host completó y el segundo observó la reserva en curso (`unknown`). `command.result` mostró el receipt completado original (`ok / Accepted`, secuencia 1–5). El replay desde el Host propietario devolvía el ACK original; el replay idéntico desde el segundo Host devolvía `busy`, porque `DispatchCommand` exigía `EnsureWriterOwnershipForCommand()` antes de consultar el receipt workspace-global.

Se corrigió exclusivamente la ruta autenticada `conversation.import`: después de validar HMAC, nonce y workspace, `DispatchCommand` consulta el receipt antes de la barrera de escritura. Completed, Unknown y conflicto se responden pasivamente; si el receipt no existe, se conservan la barrera de lease y la reserva SQL existentes. Otros comandos conservan su orden actual.

La prueba negativa confirma que proof HMAC inválido sigue rechazado antes de consultar el receipt; mismo ID con payload distinto devuelve `command_id_conflict`; un ID nuevo desde el Host sin la Session lease devuelve `busy`, sin receipt. Los replays completados de ambos Hosts devuelven el ACK original. Antes y después permanecen iguales la selección de Session en cada Host, `lastsession.txt`, los nombres de archivos de lease y la secuencia del journal.

Los intentos iniciales se conservan como diagnósticos, sin sumar al dictamen: uno consultó por error `conversation.result` (estado de ejecución de Run, que es `unknown` para un import sin Run aunque el receipt de comando esté Completed); la siguiente reproducción aisló el `busy` del segundo Host. Una candidata intermedia firmó el ID nuevo con Session nula obsoleta y obtuvo el rechazo de scope esperado; se corrigió el scope de la prueba. Ninguno de esos resultados se cuenta como PASS ni se ocultó. `attempt-index.json` relaciona cada intento con su fixture y log; el resumen de scope obsoleto se identifica como resumen, no como salida cruda. Los directorios por intento guardan journal/fixture coherentes en `artifacts/m9-native-import-processes/`.

## Validación final

- `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -f net10.0 --no-restore` — correcto, 0 advertencias y 0 errores. Los cambios compartidos Protocol/Client net8 no aplican; solo cambió Host y las pruebas net10.
- `StdioTransportTests.Native_transcript_import_cli_executable_process_is_passive_and_fails_closed` — 1 PASS: `dotnet omni.dll conversation import <archivo>` corre como proceso OS; válido exit 0 y malformado exit 2. Persisten una Session y un evento importado con ObserveOnly/Imported/ImportReview; cero Runs, Inputs, hooks y requests HTTP.
- `StdioTransportTests.Native_transcript_import_two_host_processes_share_receipt_and_replay_stably` — 1 PASS: PID distintos vivos antes de liberar barrera; mismo CommandId/payload con nonces HMAC individuales; los intervalos de request-response se superpusieron. A respondió `ok`, B `unknown` mientras el receipt estaba reservado; luego ambos `command.result` indicaron Completed. HMAC inválido fue rechazado, payload cambiado con ID repetido dio `command_id_conflict`, ID nuevo en Host no-owner dio `busy/not_found`. Los dos replays devolvieron `ok/Accepted`, secuencia 1–5. Una Session/evento, cero Runs/Inputs/hooks/provider requests; selección, last-session, archivos de lease y secuencia del journal estables.
- `StdioTransportTests.Native_transcript_import_is_receipted_passive_observe_only_and_feeds_later_authorized_context` — 1 PASS, regresión existente de stdio/import/contexto.

Conteo: **3 PASS, 0 FAIL, 0 SKIP, 0 no ejecutados en 3 métodos**. La aceptación Native/Imported anterior de 16 casos no se repitió ni sumó.

## Artefactos y freeze

`artifacts/m9-native-import-processes/` contiene los logs finales de build/tests, los intentos diagnósticos y los manifests por intento. Cada fixture usa temporales propios para workspace/config/data, `OMNICORE_AGENT_HOME`, `HOME` y roots de agentes; el provider loopback registró cero solicitudes. Tras detener Hosts se creó un backup SQLite coherente y se conservaron CAS/workspace/config. Los logs no contienen claves HMAC ni nonces.

Se preservaron los bytes completos de los outputs finales: `frozen-image/src/OmniCore.Cli/bin/Debug/net10.0/` (161 archivos) y `frozen-image/tests/OmniCore.Tests/bin/Debug/net10.0/` (225 archivos), **386 archivos, 1,142,800,762 bytes**. También se copiaron las fuentes relacionadas a `frozen-source/`. `final-manifest.json` da hashes SHA-256 de cada archivo preservado y de las fuentes; la CLI lanzada fue `src/OmniCore.Cli/bin/Debug/net10.0/omni.dll`. El bootstrap/HMAC key se mantuvo solo en memoria y en los procesos de fixture, nunca se preservó.

El SHA-256 del manifiesto final está en `artifacts/m9-native-import-processes/final-manifest.sha256.txt`. El archivo preexistente `x` conserva `84E292A1C73B431B64FBC2C8F0FA8B08CC771E58B98F05FB968C842CBBF6FE45`.

## Aceptación independiente

Luna bajo ejecutó los tres métodos anteriores, secuencialmente y una vez cada uno. Los logs independientes son `low-final-01.log`, `low-final-02.log` y `low-final-03.log`, dentro de `artifacts/m9-native-import-processes/`. Verificó antes y después el manifiesto final SHA-256 `3EB078B64EA747D36555634101C9186EE7DF0D8433604ED00E04015DF6FB8EC8`: 386 archivos binarios, ocho fuentes y nueve logs originales, sin discrepancias. Una de esas fuentes es esta acta congelada antes del dictamen; las actualizaciones editoriales posteriores no cambian el código ni los binarios validados.

Informe: `low-final-validation-20261024.md`, SHA-256 `6E83155219AE8BCC8844F975A1A72F3F21947009CB8EC1FC9D8D0A35C76F4FB2`.

| Imagen ejecutada | SHA-256 |
|---|---|
| Tests DLL | `5E5D544BC94ADBB1D34DC905610D617C503BA31BA00817835B311403DEA7572B` |
| Host DLL, en CLI y Tests | `3919D88F09BE14611684CC74ACBD2FF2483878AD84FC1058F45495FA5619C4E4` |
| CLI `omni.dll`, en CLI y Tests | `166FD600C615CA0505B5231E7F9AAA953A3BCC7BFD72F6A6C4A069FC15480CAC` |

Los paquetes de la ejecución independiente son `omnicore-native-import-process-5ca9baddb96a4b5197b15d73e1ff9711` (CLI) y `omnicore-native-import-process-7765b2912e654e618ab0a54053841fdd` (dos Hosts). Low verificó sus hashes de archivos/fuentes/binarios y `PRAGMA integrity_check=ok` para ambos backups SQLite. No quedaron Hosts/CLI de la campaña activos y no se tocaron procesos ajenos.

La evidencia de lease compara nombres de archivos, no su contenido o propietario interno. Los intervalos de requests se solaparon en construcción y revisión; el test registra el booleano, no exige ese resultado en toda corrida. La aprobación no demuestra concurrencia interna de SQLite. Se revisó también la rama existente de Turn activo: ya devuelve receipts previos antes de `busy`, por lo que no requirió cambio adicional. Los recorridos de fallo excepcional del cleanup se revisaron estáticamente; no se indujo un fallo del SO para terminar procesos.

Windows local solamente; no acredita Linux/macOS, Electron, proveedor live, formatos de terceros, ni cierre global SEC-CONV/M9/M8. Tampoco atribuye una causa al incidente histórico Discovered sin snapshot. Checkpoint local únicamente; OmniCoder quedó en solo lectura, sin CI remota, publicación, merge ni instalación.
