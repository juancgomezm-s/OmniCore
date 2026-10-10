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
