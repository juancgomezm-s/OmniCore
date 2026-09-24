# ADR-0021 — GitWorktree isolation con workspace sucio: `WorktreeBase`

- **Estado:** Aceptada (2026-09-24). Diseño ahora; implementación en M7.
- **Reemplaza:** la ubicación `.omnicore/worktrees/<lane-id>` dentro del repo (spec §48)
- **Relacionado:** ADR-0004 (reconciliación Git), ADR-0012 (lanes de Claude Code), ADR-0022 §3 (directorio de datos)

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
