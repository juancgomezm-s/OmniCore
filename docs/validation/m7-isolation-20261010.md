# Construcción de M7 — aislamiento e integración

Inicio: 2026-10-10, México. Estado: **en construcción; M7 no está cerrado**.

El usuario autorizó publicar el estado previo e iniciar M7 con un worker `gpt-6-luna` en esfuerzo **high**. El push de la base `bd4823f1f6717820728a4d5c3f020cdd51fb14b2` terminó `Everything up-to-date`: `main` y `origin/main` coincidían. La base incluye el cierre de implementación OAuth nativa de Claude en Windows y su evidencia independiente. La construcción de M7 se aísla en la rama `codex/m7-isolation-20261010`.

## Alcance y orden

La arquitectura §24 y ADR-0021, ADR-0012 y ADR-0046 determinan la salida de M7. Se conserva el scheduler de M6 hasta disponer de worktrees e integración segura; crear un worktree no autoriza escritores concurrentes sobre el workspace principal.

| Fase | Entrega | Evidencia de salida |
| --- | --- | --- |
| F1 — Base Git y snapshot | Identidad, index temporal, snapshot del working tree, ref privada, worktree externo y ownership | Git real en repositorios temporales: contenido tracked/staged/untracked no ignorado; index y HEAD del usuario intactos; errores tipados y cancelación; rutas controladas |
| F2 — Integración | Base/ours/theirs, preview, autorización de WorkspaceWrite, Barrier y reconciliación | Ediciones posteriores conservadas; conflicto sin escritura; recuperación de integración parcial; index del usuario intacto |
| F3 — Ejecución aislada | TaskPacket materializado y ejecución nativa con raíz del worktree; leases por destino | Dos writers en worktrees independientes, una integración a la vez, capacidad/budget/gates conservados |
| F4 — Restauración | WorkspaceSnapshotStore, UndoPlan, restore y compensaciones atribuibles | Restauración sin revertir eventos; cambios ajenos provocan conflicto; topología y efectos no reversibles explícitos |
| F5 — Ejecutor externo | Lane Claude Code con stream-json, TaskPacket, permisos derivados y recuperación | Gates del Host en el worktree; resultados como propuestas; cancelación/crash; contrato registrado y una vertical autenticada autorizada |
| F6 — Aceptación | Commands/cliente, recovery, documentación y regresiones | Build multi-target, arquitectura y suite integral sobre código final; evidencia Windows, límites y diferidos visibles |

El undo físico de M7 y el comando de producto `/undo`/sus proyecciones de M10 son entregables diferentes (ADR-0046 §9). Linux sigue diferido; no se extrapola evidencia de Windows.

## Primer checkpoint

Se construye el backend Git y una entrada CLI **de inspección**, sin mutaciones del workspace. El backend de provisioning se prepara para una posterior frontera autoritativa de command, permisos y journal; no se presenta como una Lane operativa ni se expone una creación manual que eluda esa frontera.

La fidelidad del snapshot exige no sustituir silenciosamente el estado del usuario por HEAD ni omitir archivos no ignorados. Las opciones todavía no construidas se rechazan mediante un resultado tipado. Secretos y topologías no soportadas se rechazan de forma explícita, antes de materializar un árbol nuevo. Hooks, fsmonitor y filtros definidos por un repo no pueden ejecutar procesos arbitrarios como efecto oculto del snapshot.

La limpieza verifica ownership, identidad y rutas físicas. Un worktree con cambios, una rama activa o commits nuevos se conserva si no existe una decisión explícita de descarte; las refs de snapshot se retienen. El barrido de retención de siete días y la adopción de huérfanos siguen pendientes. No se usa `git reset --hard`, `git clean` ni una eliminación recursiva de paths suministrados por el usuario.

## Verificación

Código verificado: `6718fd15613e5ac77103568e40e342ebf5ade59a`, sobre la base `bd4823f`. Árboles Git: `src` = `14e9c336cfff5a177b31e3a8b791fab2b71cbca2`; `tests` = `f23e10dbaaa9b727e7ecc21a149bdcb6b107d101`. El commit posterior solo actualiza documentación y evidencia.

| Comprobación | Resultado |
| --- | --- |
| `dotnet restore OmniCore.slnx` | exit 0 |
| `dotnet build OmniCore.slnx --no-restore -v quiet` | 0 errores, 0 advertencias; solución multi-target |
| `GitWorktreeStoreTests` + `WorktreeInspectionCliTests` | 20 PASS, 0 FAIL, 0 SKIP; Git real y CLI como proceso hijo |
| `OmniCore.ArchitectureTests` | 56 PASS, 0 FAIL |
| `CliEndToEndTests` + `ClientTests` + `ClientActionTests` | 71 PASS, 0 FAIL |
| `git diff --check` | exit 0 |

Las suites se ejecutaron en Windows, en serie (`-parallelMode none`), sin TTY ni stdin interactivo. El Git observado fue 2.54.0.windows.1; soporta `check-attr --source`. [Logs del checkpoint](m7-checkpoint-20261010.txt). No se ejecutó nuevamente la suite integral de 3696 casos de la base OAuth; esos resultados no se atribuyen a M7.

Las primeras compilaciones detectaron un helper mal nombrado, ambigüedad con `Domain.Task` y llamadas de fixtures sin el token de cancelación exigido por xUnit. La primera pasada focal falló en la limpieza de objetos Git de solo lectura en Windows; tras corregirla quedaron dos defectos del fixture/clasificación (merge sin identidad y repo anidado), también corregidos antes de la pasada final. La revisión raíz y la revisión readonly de Luna incorporaron las defensas de HEAD nuevo y gitlinks de repositorios anidados. Los resultados finales anteriores corresponden al código congelado.

El backend de creación/limpieza se valida directamente con contratos reales; su entrada autoritativa de Host y journal pertenece a F2/F3 y no está expuesta por CLI. `omni worktree inspect [ruta]` sí recorre CLI → Host → Infrastructure en producción, con mensajes es/en y errores tipados. Snapshot rechaza `PatchOverlay`, allowlists de ignorados, gitlinks, symlinks/reparse points, filtros y transformaciones de contenido no soportadas. La cancelación conserva metadata/refs para una futura recuperación; esa recuperación todavía no está cableada. Los writers continúan serializados.

**Siguiente entrega:** integración base/ours/theirs con preview, conservación del index del usuario, autorización de WorkspaceWrite, Barrier y reconciliación; después, materialización de TaskPacket y scheduling de lanes por worktree. El cierre integral de M7 requiere F1–F6, no la suma de pruebas unitarias aisladas.

## Segundo checkpoint — preview F2 (histórico)

El usuario pidió reactivar el trabajo después del primer checkpoint. Luna high construye la propuesta de integración; la revisión raíz compone Host/CLI y pruebas con el CLI como proceso hijo.

La propuesta calcula base = snapshot, ours = working tree actual del usuario y theirs = working tree actual de la Lane, incluidos cambios sin commit. Registra paths y hashes pre/post de los cambios propuestos; evita exponer contenido o stderr Git en el CLI. Los conflictos impiden presentar la propuesta como aplicable. El comando cableado es `omni worktree preview <repositorio> <ownershipId>`: el Host deriva el directorio de datos por WorkspaceId y solo carga su metadata de ownership, nunca un path de metadata elegido por el cliente. Los mensajes tienen paridad es/en; conflicto devuelve exit 3 y preview limpia exit 0. La creación/aplicación manual continúa sin exponerse.

La metadata v2 vincula ownership, base, snapshot y captura del index; metadata incompleta o de otra identidad se rechaza. La preview usa índices y objetos Git temporales privados, con alternates al almacén real, y no cambia objetos/refs, HEAD, index ni archivos del usuario o de la Lane. Verifica nuevamente HEAD, branch, paths y hashes de ambos lados al finalizar; una edición intermedia devuelve `WorkspaceChanged`. Los hashes post corresponden a bytes materializados, incluidos binarios y paths Unicode.

**Frontera pendiente:** ToolRuntime y la recuperación actuales representan reconciliación de un solo archivo por efecto. La aplicación de un lote necesita metadatos por archivo durables, permisos, Barrier, exclusión de escritores e integración con recovery. Este checkpoint no expone `apply`, no habilita escritores paralelos y no declara F2 cerrada. Las propuestas son observaciones que deben recalcularse y persistirse antes de una futura aplicación; sus objetos de merge temporales no acreditan un artifact durable de producto.

Código congelado: `14a5d6411cc4c44d2e98ff85f2c7ff1044b9b84c`. Árboles Git: `src` = `7c76c5301dee1b700f2bdb9474b6d6c54318c5cb`; `tests` = `9efaac82681447fbf9b639a62ec74f4d692ed2c4`. El checkpoint documental posterior conserva esos árboles.

La compilación multi-target pasó con 0 errores y 0 advertencias. Las pruebas focalizadas pasaron 31/31 y arquitectura 56/56. La regresión de CLI/cliente dio **70 PASS y 1 FAIL por timeout**, tanto en la pasada inicial (65.720 s) como en la repetición del grupo (60.612 s): `Runtime_runs_two_delegations_on_one_host_and_defers_root_writer_behind_live_readers`, esperando el primer lector en `CliEndToEndTests.cs:205`. Esa prueba aislada pasó 1/1 (2.139 s) sin editar fuentes. El fallo dependiente del grupo seguía abierto al terminar ese checkpoint; quedó resuelto y volvió a pasar el grupo exacto en el tercer checkpoint. Esta observación histórica no se atribuye al código F2 actual. La suite integral queda pendiente para el cierre de M7. [Logs y hashes del segundo checkpoint](m7-preview-checkpoint-20261010.txt).

Antes de la pasada focal final se corrigieron el contexto de objetos privados para `check-attr --source`, nulabilidad/tipos internos y el terminador LF del archivo Git alternates. Un CRLF incorporaba el CR a la ruta en Windows y causaba `GitCommandFailed`. Las pruebas también corrigieron expectativas de los fixtures y limpieza de objetos de solo lectura. La prueba de carrera muta el workspace entre captura y merge, y verifica el rechazo conservando la edición del usuario. Estos fallos iniciales no se cuentan como pases.

## Tercer checkpoint — aplicación autorizada y recuperación de F2

El código de F2 está en `codex/m7-isolation-20261010`, commit `d65d9f40e91a487778fb498af68225c550bb31fc`, publicado en `origin/codex/m7-isolation-20261010`. Esta entrega conecta el backend de captura con `worktree.integrate` a través de Security, ToolRuntime, leases de escritura del Host y un Barrier durable que agrupa `ToolCallStarted`, el estado de integración `Started` y la deuda de validación previa al efecto. La propuesta/claim no permite al cliente escoger metadata ni CAS. La política por lotes se proyecta antes del Barrier y se vuelve a comprobar antes de escribir.

La reconciliación conserva el manifest tipado y las pre/postimágenes en CAS protegido, confirma la identidad Git/ownership en recuperación y registra el estado de cada path junto con la transición reconciliada del ToolCall. Las reaperturas activas y terminales son idempotentes; un crash después del último reemplazo se clasifica como Applied solo si todo el lote coincide con posthashes. Los paths con topology, encoding, size o mode fuera del contrato fallan cerrados. La prueba de `OmniServer` verifica CAS durable, root canónico y retención idempotente entre dos reaperturas. El propósito del CAS usa ahora la raíz física canónica, evitando que el casing equivalente de Windows altere la clave de protección.

La suite exacta de CLI/cliente que había quedado con timeout en el checkpoint 2 pasó 71/71 sin ampliar plazos ni relajar assertions. Build multi-target, recuperación single-file existente, referencias de artifacts, ledger/deuda y el backend de Git también pasaron en las ejecuciones indicadas abajo.

Límites conocidos: el lease excluye otros escritores coordinados por este Host, no editores externos. Para altas se usa publicación atómica sin overwrite y se prueba una creación ajena entre la comprobación y la publicación. En reemplazos y borrados queda una ventana entre el último preimage check y el efecto porque la API de archivos no ofrece compare-and-swap con hashes frente a procesos externos; se hacen rechecks por archivo y verificación postimage del lote, y una divergencia queda como Unknown/Conflict, pero no se atribuye exclusión absoluta de procesos externos. `ChangedFilesReader` todavía no proyecta el lote multarchivo de integración en el diff de producto. Siguen pendientes F3 (TaskPacket, worktree operativo, leases por destino y provisioning journalizado), F4, F5, F6, changedFiles/diff y la suite integral; M7 no está cerrado.

Código de implementación: commit `d65d9f40e91a487778fb498af68225c550bb31fc`; los 245 tests focales y de regresión se ejecutaron sobre ese commit. Un arreglo solo de representación cambió el byte NUL literal del source a escape C# `\0` en `6a31c154598867b11773c34a89e37011b096f1f0`; el build completo de esa revisión pasó 0/0 sin warnings. HEAD al publicar esta acta: `6a31c154598867b11773c34a89e37011b096f1f0`; árbol de `src` `9ccfdd3ee8f21f5dca98d116a72a12b190d2321d`; árbol de `tests` `2398312ac414a14fb4d3bad21171414027bb8473`.

| Comprobación | Resultado |
| --- | --- |
| `dotnet build OmniCore.slnx --no-restore` | exit 0; 0 errores, 0 advertencias |
| `GitWorktreeStoreTests` | 34 PASS; incluye integración por Runtime, no-overwrite, recovery parcial y reapertura real de OmniServer |
| `OmniCore.ArchitectureTests` | 56 PASS |
| `CliEndToEndTests` + `ClientTests` + `ClientActionTests` | 71 PASS; prueba de lectores/writer incluida |
| Recovery single-file + ArtifactRefs + PostEditValidation + MutationLedger | 84 PASS |
| `git diff --check` | exit 0 |

La evidencia y SHA-256 de logs externos está en [m7-f2-checkpoint-20261010.txt](m7-f2-checkpoint-20261010.txt). Hubo una pasada preliminar con fallo al limpiar el fixture SQLite y otra que detectó la divergencia de casing del propósito CAS; se conservaron los logs y no cuentan como éxito. Los resultados anteriores son de una repetición secuencial posterior al arreglo. No se ejecutó la suite integral de M7 ni se integra esta rama a `main`.
