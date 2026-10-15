# Reparación y validación de CI/Linux de OmniCore

Fecha: 2026-10-14, Ciudad de México. Rama `codex/cierre-linux` y [PR #1](https://github.com/juancgomezm-s/OmniCore/pull/1). El [acta Linux](validation/linux-milestones-closure-20261014.md) conserva el inventario, logs, hashes y pasadas intermedias. La recuperación de la interfaz de preferencias confirmada por el usuario se prepara en otra rama; esta reparación no modifica su diseño.

## Workflow y criterio de cierre

`.github/workflows/ci.yml` se ejecuta con push a main, PR hacia main y despacho manual. La matriz `test` restaura, compila y ejecuta toda la solución en Ubuntu y Windows; `fail-fast: false` permite observar ambas plataformas. `linux-package` es independiente: publica self-contained y Native AOT y ejecuta escenarios CLI, recuperación de SQLite y terminales PTY reales, con PATH privado vacío y sin variables de localización del host .NET. No depende de la conclusión de los tests. Un fallo suyo es independiente; no es una consecuencia de `test`.

Los tres jobs finales conservan todas las pruebas y validaciones de empaquetado. Windows añade una focal previa para obtener fallo temprano en los seis casos críticos de recuperación, Git y aislamiento; esos casos también siguen en la integral y sus cantidades no se suman. La preparación de NUL se revierte con `always()`; cualquier fallo de preparación/restauración falla el job. No hay `continue-on-error`, resultados simulados ni supresión de códigos de salida. El job de diagnóstico y los hooks nativos temporales fueron retirados.

La validación final corresponde al **head del PR** y se consulta en [sus tres checks](https://github.com/juancgomezm-s/OmniCore/pull/1/checks). La descripción del PR registra el run final y sus resultados. No se autoriza la fusión mientras alguno no sea exitoso; las pasadas históricas siguientes no sustituyen esa validación.

## Causas y evidencia

| Fallo confirmado | Evidencia y clasificación | Reparación |
| --- | --- | --- |
| Compilación/publicación Native AOT | La carga YAML por deserialización dinámica emitía IL3050. Fallo de publicación, independiente de la matriz. | `ScenarioLoader` construye el árbol desde nodos YAML sin reflexión; valida profundidad antes de materializarlo. Se conserva el rechazo real de entradas profundas y se retira la supresión IL3050. |
| Diferencias Linux/Windows de fixtures | Nombre de apphost, comandos/rutas Windows, separador de identidad Unix, stdout/input y vida de owners M9. Pasadas Linux fallidas y correcciones detalladas en el acta. | Apphost nativo, argv de plataforma y cierres de store antes de reabrir/purgar. Se conserva la prueba de cancelación de procesos real en Linux y las assertions de ownership, dedup y replay. |
| Reapertura tras matar Host: SQLite 10/1546 | [Run 38677276693, diagnóstico Windows](https://github.com/juancgomezm-s/OmniCore/actions/runs/38677276693/job/116233729188): `SetEndOfFile failed: Win32=1224` sobre `journal.db-shm`, al ejecutar `PRAGMA journal_mode=WAL`, con el Host ya terminado. Reproducido también localmente. Fallo de ejecución Windows. | Espera acotada sólo para IOERR_TRUNCATE en la inicialización WAL de Windows, antes del esquema/efectos. Trace informa la espera y su recuperación. A los cinco segundos se propaga el error original; los demás errores y plataformas no se reintentan. Dos pruebas con mapeo real verifican recuperación, persistencia y fallo permanente. No se borra WAL/SHM ni se reenvían commands. |
| Git no puede abrir `/dev/null` | [Run 38671700600](https://github.com/juancgomezm-s/OmniCore/actions/runs/38671700600/job/116216065675): `could not open '/dev/null' for reading and writing: Permission denied`, exit 128. El runner nuevo carecía de ACE AppContainer en el dispositivo NUL. | Preparación explícita mediante handle en la VM efímera de CI, snapshot de su DACL y restauración obligatoria. Enable/Restore se rechazan fuera de GitHub Actions. No se cambia NUL en el equipo del usuario. |
| Git no puede normalizar cwd | [Run 38672460009](https://github.com/juancgomezm-s/OmniCore/actions/runs/38672460009/job/116218578138): superado NUL, `Unable to read current working directory: Permission denied`. Expandir RUNNER~1 a ruta larga no lo corrigió. | Fixtures bajo un TEMP sintético de la VM con lectura de metadata de directorios para AppContainer, sin herencia de lectura a archivos. No se modifica la ACL de la raíz del volumen. El caso Strong sigue exigiendo denegación del archivo externo. |
| Git reconoce el repo pero `diff` falla | El diagnóstico 38677276693 lee `.git/HEAD/config`, obtiene `--is-inside-work-tree=true` y `core.bare=false`; `diff --check` devuelve `this operation must be run in a work tree`, exit 128, incluso declarando la raíz de instalación. `mingw_chdir` usa GetFinalPathNameByHandleW/VOLUME_NAME_DOS cuando core.symlinks está activo. | El repo sintético de M3 no contiene symlinks: fija `core.symlinks=false` en Windows. Mantiene `git diff --check`, el error de whitespace, el feedback al modelo y la reparación comprobada. No amplía permisos del runtime ni modifica repos reales. La compatibilidad Git Strong con symlinks permanece limitada por esa API de Windows. |
| stdin de procesos Strong sin payload | Probe nativo de GetStdHandle: handle inválido, exit 28. Era independiente del dispositivo NUL abierto internamente por Git. | Pipe heredable válido con EOF aunque no haya payload; los dos casos de stdin siguen probándose. |
| Assertions TUI sensibles a foco/hilo | Marcador de captura de conversación dependía del footer de enlaces; lectura de layout del catálogo desde el hilo del test. | Marcador de contenido estable y assertion de layout en el hilo UI, conservando texto y límites de tamaño. Se conserva RGB al rasterizar evidencia. |

La instrumentación nativa registrada en el run de evidencia llamaba a SetEndOfFile real y devolvía el BOOL/error sin alterarlos. Se usó para diagnosticar, no para acreditar una pasada. Microsoft documenta [1224 como ERROR_USER_MAPPED_FILE](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-); SQLite [IOERR_TRUNCATE](https://www.sqlite.org/rescode.html#ioerr_truncate) y la [inicialización del WAL-index](https://www.sqlite.org/walformat.html). La incompatibilidad del path DOS en AppContainer está documentada en [microsoft/STL #6286](https://github.com/microsoft/STL/issues/6286); [Git for Windows](https://raw.githubusercontent.com/git-for-windows/git/v2.56.0.windows.2/compat/mingw.c) confirma la rama usada por symlinks. El [harness de libuv](https://github.com/libuv/libuv/blob/v1.x/test/appcontainer.c) documenta el requisito de NUL.

## Reproducibilidad y versiones

Debian 13.5/WSL2 x64, usuario sin privilegios; .NET SDK 10.0.401 y runtime 10.0.12. `global.json` fija el SDK también en CI. Se verifican Protocol/Client/Sandbox net8 y net10. No se cambiaron versiones de dependencias para obtener una pasada. Git del runner evolucionó de 2.55.0.windows.5 a 2.56.0.windows.2; la evidencia de las rutas y la API, y no la diferencia con Git local 2.54, sustenta el diagnóstico.

La matriz ejecuta solución completa en serie con stdin cerrado. Linux publica ambas distribuciones con warnings tratados como errores y valida escenarios y PTY reales. Windows requiere el host preparado para AppContainer; ese requisito de CI no se presenta como provisión automática del runtime en instalaciones nuevas.

## Pruebas ejecutadas y resultados

| Evidencia | Resultado observado | Alcance |
| --- | --- | --- |
| [Run 38668015994](https://github.com/juancgomezm-s/OmniCore/actions/runs/38668015994), fuente 0789ebf | Ubuntu 4553 total / 4536 PASS / 17 SKIP / 0 FAIL; linux-package PASS; Windows 4543 PASS / 8 SKIP / 2 FAIL | Pasada integral que expuso los dos fallos Windows; no era cierre de CI. |
| Debian sobre integración M9-D 793e1fa | Build 0 warnings/errores; 20/20 `StdioTransportTests` | Procesos reales, ownership, recibos, replay y nuevas acciones de M9-D. |
| Windows `windows-wal-git-final.log` | 23/23 PASS, 0 omitidas | Los 20 casos de transporte M9-D, dos mapeos reales y el gate Git. Incluye doce caídas independientes; cualquier fallo aborta el estrés. |
| Debian integral, fuente C# b673091 | 4557 total / 4538 PASS / 19 SKIP / 0 FAIL, 11m37s; build sin warnings/errores, runner exit 0 | `acceptance-b673-full.log`, `acceptance-b673-exit.txt`; ambas distribuciones publicadas y ejecutadas. |
| Pruebas de mapeo real | Liberado: reapertura y recibo Completed persistido; retenido: error original 10/1546 tras cinco segundos, sin truncar el archivo; luego se puede reabrir | No se sustituye SQLite ni se simula el resultado del sistema operativo. |
| [Checks del head del PR](https://github.com/juancgomezm-s/OmniCore/pull/1/checks) | Registro definitivo de Windows, Ubuntu y linux-package; run y cantidades en la descripción del PR | Deben aprobarse los tres antes del merge. |

Los conjuntos son superpuestos y no se suman. El acta conserva los intentos fallidos y las evidencias anteriores. Los siete casos opt-in de cuentas/modelos reales continúan declarados; las pruebas específicas de Windows se omiten explícitamente en Linux. Ningún test existente se deshabilitó.

La revisión final detectó un hueco de evidencia: los primeros smoke eliminaban DOTNET_ROOT pero heredaban PATH del proceso de publicación. Se corrigieron ambos scripts para usar un directorio privado vacío como PATH, retirar también DOTNET_HOST_PATH y verificar que `dotnet` no es descubrible. Las pasadas anteriores no acreditan ese aislamiento del entorno. La nueva validación no desinstala el SDK necesario para compilar: aísla el entorno de los ejecutables publicados.

Los paquetes Debian recién publicados de b673091 se ejecutaron de nuevo con los scripts corregidos: seis escenarios verificados por paquete y cuatro PTY en total, todos con exit 0 (`strict-sdk-path-b673/`). La modificación final sólo cambia scripts/evidencia; la fuente C# coincide con b673091, cuya integral Debian está registrada arriba.

## Pendientes fuera de esta reparación

M2/M3 requieren aceptación con el modelo local; Strong Linux no tiene backend implementado. OR5 autenticado y los consumidores pendientes de metadata de skills siguen en M8. M9/M10 conservan sus pendientes de producto/consumidor. Las pruebas de este informe no acreditan cuentas, inferencias pagadas, aceptación humana de Electron ni soporte de filesystem de red. Los cambios nuevos de main/M9-D se conservan; no se ha fusionado el PR sin la validación final.
