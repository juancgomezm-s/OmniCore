# ADR-0021 — GitWorktree isolation con workspace sucio: `WorktreeBase`

- **Estado:** Aceptada (2026-09-24). Diseño ahora; implementación en M7.
- **Reemplaza:** la ubicación `.omnicore/worktrees/<lane-id>` dentro del repo (spec §48)
- **Relacionado:** ADR-0004 (reconciliación Git), ADR-0012 (lanes de Claude Code), ADR-0022 §3 y ADR-0039 §2 (directorio de datos por plataforma)

## Contexto

El workspace del usuario puede tener archivos tracked modificados, cambios staged, archivos untracked, archivos ignorados y submódulos. Una Lane **no** debe trabajar sobre un árbol distinto del que ve el usuario, y la integración nunca debe pisar cambios suyos.

## Decisión

### 1. `WorktreeBase`

| Opción | Qué ve la Lane | Uso |
|---|---|---|
| `Head` | el último commit, sin cambios locales | Tareas explícitamente sobre HEAD (por ejemplo, revisar un PR) |
| **`SnapshotOfWorkingTree`** (por defecto) | exactamente el working tree del usuario: tracked modificados + staged + untracked no ignorados | Caso normal: "arregla esto en lo que tengo" |
| `PatchOverlay` | HEAD + un patch explícito | Reproducir un estado concreto (por ejemplo, un patch de otra Lane) |

### 2. Creación de `SnapshotOfWorkingTree`

La creación no toca el index ni el working tree del usuario:

1. Se usa un index temporal: `GIT_INDEX_FILE=<tmp> git read-tree HEAD` y luego `git add -A` con ese index. Así se capturan tracked modificados, staged y untracked no ignorados.
2. `git write-tree` genera el tree `T`. Luego `git commit-tree T -p HEAD -m "omnicore snapshot <lane>"` genera el commit `S`.
3. `S` se guarda en una ref privada `refs/omnicore/snapshots/<lane-id>`, para que `git gc` no lo borre.
4. Aparte se registra el tree del **index real** del usuario (`git write-tree` con su index, que solo lee), para saber qué tenía staged.
5. `git worktree add --detach <dir> S`. `<dir>` está **fuera del repo**, en el directorio de datos del workspace (`%LOCALAPPDATA%\OmniCore\workspaces\<WorkspaceId>\worktrees\`, ADR-0022 §3), para que las búsquedas del agente no indexen los worktrees.
6. **Archivos ignorados:** por defecto no se copian (`bin/`, `obj/`, `node_modules/`). `IgnoredFilesPolicy = None | Allowlist(globs)`. Los secretos (ADR-0018 §3) nunca entran en una allowlist.
7. **Submódulos:** el snapshot registra el commit de cada gitlink.
   - En el worktree se inicializan con `git submodule update --init` desde los objetos locales.
   - Si un submódulo tiene cambios locales, la Lane lo trata como de **solo lectura** o falla con un error claro, según la política. No hay snapshot recursivo en v1.
8. **Identidad del repo:** se registran la raíz, `git rev-parse --git-common-dir`, HEAD, la rama y `S` en el evento `WorktreeCreated`.

### 3. Integración

1. La Lane termina con sus Completion Gates evaluados **en el worktree**.
2. Se calcula el patch `S → resultado de la Lane`.
3. **Verificación de identidad:** el workspace destino debe tener el mismo `git-common-dir`. Si cambió la rama o el HEAD desde el snapshot, se avisa en el evento y en la UI.
4. **Aplicación con 3-way merge:** base = `S`, ours = el working tree actual del usuario, theirs = el resultado de la Lane. Se usa `git merge-tree --write-tree` o `git apply --3way` sobre un index temporal, y el resultado se escribe al working tree solo si no hay conflictos.
5. **Conflictos:** si el usuario modificó las mismas zonas mientras tanto, se emite `IntegrationConflict` y **no se escribe nada**. El usuario decide: ver el diff, aplicarlo parcialmente o descartarlo.
6. **Permisos:** la integración necesita `WorkspaceWrite` en el workspace principal. Es un efecto `Reconcilable` (ADR-0004): `IntegrationStarted` se confirma con Barrier y el intent incluye el hash esperado de cada archivo.
7. **Qué se integra:** solo cambios del working tree; el index del usuario no se modifica. Los commits los decide el usuario o una tool `git.commit` explícita.

### 4. Cleanup y recuperación

- **Cleanup:** `git worktree remove` tras integrar o descartar. La ref `refs/omnicore/snapshots/<lane>` se borra cuando la Lane se cierra y vence su retención (7 días por defecto, para poder inspeccionarla).
- **Al arrancar o reanudar:** se comparan los worktrees del journal con `git worktree list`:
  - una Lane viva conserva su worktree, que se reanuda;
  - un worktree huérfano se elimina y se ejecuta `git worktree prune`;
  - una ref sin Lane se borra tras su retención.
- **Crash durante la integración:** se reconcilia por hash de cada archivo (ADR-0004 §4). Si el estado es parcial, queda como `Conflict` y el usuario decide.
- **Workspace que no es un repo git:** `GitWorktree` falla con un error claro. `DirectoryCopy` queda como opción futura (DEFERABLE).

## Clasificación

| Elemento | Categoría |
|---|---|
| Todo este ADR | **Contract now / implementation later** (M7) |
| En M1 solo existen `IsolationPolicy` e `IsolationHandle` como tipos del dominio de la Lane | **Necesario desde M1** (solo tipos) |
| Submódulos recursivos, `DirectoryCopy` para workspaces sin git | **Deferable** |

## Implementación M7 v1 (2026-10-10)

Esta sección describe el alcance implementado; los detalles de las secciones anteriores que exceden estos límites permanecen como diseño futuro. La captura usa un index privado y objetos temporales fuera del repositorio para calcular la base y el árbol del usuario, sin escribir en el index real, HEAD ni refs de usuario. `Inspect` es de solo lectura. El estado durable vincula WorkspaceId, raíz física, git-common-dir, HEAD/rama, ownership de la Lane y sus árboles. Se vuelve a verificar la identidad y la captura antes de integración, aplicación, recuperación y disposición.

La propuesta de integración se persiste como un manifiesto protegido que incluye preimágenes y postimágenes por archivo, y se referencia desde el evento `ToolCallStarted`; el evento de entidad `WorktreeIntegrationStateRecorded` registra `Started`, `Completed`, `Conflict` o `NotApplied`. La aplicación completa es un efecto autorizado y reconciliable: aplicar solo un subconjunto o una parte de una propuesta no está soportado. Antes de cada escritura comprueba topología y preimagen, y luego contrasta todos los postestados. Un crash parcial se clasifica como conflicto/estado desconocido y requiere resolución; recovery no termina automáticamente el lote restante ni revierte ediciones posteriores. El reconciliador incorpora el estado de integración junto al estado de ToolCall de manera durable e idempotente. El index del usuario permanece intacto.

Los worktrees creados se vinculan al journal con `LaneWorktreeCreated` después de persistir el ownership. Recovery y ejecución resuelven cada Lane por su binding exacto; una Lane hija sin vínculo válido no cae de vuelta a la raíz principal. Las reservas de escritura excluyen escritores coordinados para la misma raíz física. La integración desde la Lane al workspace principal sigue siendo una acción separada con autoridad `WorkspaceWrite`, barrera durable y pre/post hashes. La aceptación del resultado puede reabrirse sin regenerar la Lane, pero se invalida si cambia la captura aceptada.

La retención predeterminada de snapshots es de siete días. Cleanup/discard requieren propiedad física comprobada y revalidación inmediata. Cleanup de una Lane integrada requiere evidencia de resultado completo, integración completa del árbol, ausencia de consumidores/Verify pendientes, y ausencia de cambios posteriores o salidas ignoradas que no estén retenidas. Discard es explícito y conserva primero la historia/árbol bajo una ref retenida; retries validan y reutilizan la ref ya fijada. Recovery de arranque solo elimina estados terminales que pasan estas comprobaciones; un worktree o ref huérfano ambiguo se conserva para resolución, no se poda automáticamente.

Límites de v1: hasta 64 archivos; hasta 2 MiB por pre/postimagen, 8 MiB agregados de imágenes y 16 MiB para el manifiesto protegido. La aplicación admite archivos de texto UTF-8 ordinarios con modo `100644`; contenido binario, modo ejecutable `100755`, directorios nuevos, symlinks/reparse points, gitlinks/submódulos modificados, secretos detectados, filtros/drivers o transformaciones Git no soportados se rechazan tipadamente antes del efecto. Los archivos ignorados no forman parte de snapshot ni integración; su presencia impide cleanup/discard para evitar pérdida. No es una prohibición general de compilar ni de aplicar cambios de texto admitidos. La validación documentada corresponde a Windows; Linux queda por verificar. Los leases coordinan operaciones de este Host, no excluyen editores o procesos externos.

Quedan como diseño futuro el snapshot recursivo de submódulos, `DirectoryCopy` para carpetas sin Git y el pruning de worktrees ambiguos. Las limitaciones anteriores se rechazan de forma explícita; no se omiten silenciosamente ni se afirman como soporte M7.

## Retención nativa M9-L (2026-10-24)

La excepción acotada de ADR-0050 permite que el Run actual autorice la poda de
refs privadas vencidas de un Run histórico terminado de la misma Session y
workspace. La autoridad actual se mantiene separada del propietario histórico.
El binding Task/Lane/Execution/TaskPacket y los datos retirados se verifican sin
reabrir el directorio del worktree ni recuperar permisos antiguos.

La revisión es pasiva y la acción fija el conjunto de refs y OIDs, limitado a
snapshot y lane retenida de un ownership. Se conserva el plazo de siete días.
Una transacción Git compara todos los OIDs antes de efectuar el lote, usa stdin
exacto y `--no-deref`, y rechaza refs simbólicas; el barrido CLI conserva también
esta protección. No se borran metadatos ni directorios. Un resultado incierto no
se reintenta automáticamente. La transacción no ofrece una vista atómica a
lectores concurrentes ni una garantía general ante caída intermedia.

Aceptación local por Luna bajo: 13 casos sobre los mismos binarios sin recompilar.
[Evidencia, manifiestos y límites](../validation/m9-worktree-retention-20261024.md).
No hay nueva CI/publicación ni aceptación del consumidor por este incremento.
