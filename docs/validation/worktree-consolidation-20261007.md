# Consolidación a main y poda — 2026-10-07

Por instrucción del usuario se integró `22416b2` a `main` mediante fast-forward.
Es el último checkpoint completo de la rama de consolidación, no un cierre de
M5/M5.5. El checkpoint posterior `5a843f1` es WIP sin validar y permanece
preservado en `codex/omnicore-consolidation-20261004`, fuera de main.

## Preservación y poda

- Inventario: 97 worktrees registrados; al terminar sólo queda el checkout
  principal `C:\Users\juanc\source\repos\OmniCore`, en `main`.
- 92 checkouts externos se trasladaron completos, incluidos cambios locales,
  archivos ignorados y binarios, al respaldo
  `C:\Users\juanc\.codex\worktree-backups\omnicore-20261007-1600`.
- Cuatro worktrees administrados por Codex se archivaron mediante su mecanismo
  recuperable: m2-closure-audit, m5-qualification, m55-artifactrefs-roundtrip y
  m55-luna-repairs. Sus logs/artefactos/archivos locales se preservaron antes.
- No se eliminaron ramas ni se hizo push. Las ramas históricas divergentes y
  borradores conservan sus commits; no se hizo un merge ciego de versiones
  antiguas sobre implementaciones actuales.
- El trabajo local previo del checkout principal quedó en un stash identificado
  como `preserve primary local work before main consolidation 20261007` y en
  `codex/preserved-primary-wip-20261007`. El stash incluye también los archivos
  no rastreados; no se aplicó el parche SQLite incompleto al código actual.
- El inventario exacto de rutas, HEAD, ramas, cambios y destinos está en
  `INVENTARIO.md` dentro del respaldo. Los checkouts trasladados son respaldos
  de archivos, no worktrees operativos: para recuperarlos se crea un worktree
  desde la rama conservada y se copian los cambios necesarios. Los administrados
  se restauran mediante Codex.

## Verificación

Build de main: 0 errores y 0 advertencias. Arquitectura: 56 PASS, 0 FAIL.
La primera suite completa produjo 2930 casos, 1 FAIL y 4 SKIP por symlink.
El fallo fue un fixture YAML que mutaba únicamente LF, pero el checkout tenía
CRLF: la eliminación de `writes` no ocurría. Se normalizan los finales de línea
del fixture antes de mutarlo, conservando todas las assertions. Focal posterior:
7 PASS, 0 FAIL. Los logs de primera suite, focal y suite final están en el respaldo.

La suite final terminó con exit0: **2930 casos, 2926 PASS, 0 FAIL y 4 SKIP**
por permisos de symlink, 124.942s (`main-full-final.log`). Arquitectura:
**56 PASS, 0 FAIL, 0 SKIP**, 0.689s (`main-architecture-tests.log`). Los siete
casos focales se solapan con la suite completa. No se ejecutó cualificación
autenticada ni se reanudó el objetivo M5/M5.5 o su automatización.
