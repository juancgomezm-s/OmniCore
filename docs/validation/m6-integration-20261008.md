# Integración recuperada y continuación de M6 — 2026-10-08

Por instrucción del usuario se recuperó el avance de los worktrees vigentes,
se integró a `main` y se inició la continuación hacia los criterios de salida
de M6. La implementación pendiente se encargó a un worker `gpt-6-luna` con
esfuerzo `xhigh` (muy alto), también por instrucción explícita del usuario.

## Inventario e integración

- Principal: `C:/Users/juanc/source/repos/OmniCore`, `main` en `1e30c79` al iniciar.
- `codex/rescue-glm-20261008`, HEAD `ab1837f`: ya era ancestro de `main`;
  no requirió un segundo merge. Su worktree permanece disponible.
- `codex/tui-markdown-polish`, HEAD `06c5781`: nueve commits nuevos respecto
  a `main`. Se integraron mediante merge `a95b089`, sin conflictos: 81 archivos,
  6909 líneas añadidas y 133 eliminadas. Ambas ramas son ahora ancestros de main.
- Respaldo del punto anterior: rama `codex/pre-m6-integration-20261008`.
- Se preservaron temporalmente y luego restauraron los cambios locales de
  `docs/adr/0011-conexion-a-proveedores.md`, `docs/adr/README.md` y
  `docs/validation/claude-oauth-plan-20261008.md`. No forman parte del merge
  ni de la implementación de M6.
- No se eliminaron worktrees, backups ni ramas; no se hizo push. El inventario
  y la recuperación de los worktrees históricos permanecen descritos en
  [consolidación anterior](worktree-consolidation-20261007.md) y
  [archivos recuperados](recovered-worktrees-20261007.md).

## Avance recuperado

Conversación canónica y resumen por Run, aislamiento y herencia de contexto
SelectedProjection, paquetes durables de delegación, cola con admisión acotada,
ejecución de hijos lectores, resultados inmutables en CAS, dispositions
explícitas y joins All/Any/Quorum/Explicit sobre resultados aceptados.
También se integraron el sidebar funcional, diffs atribuibles, preferencias
responsive y la compatibilidad del codec histórico de tools.

Los bloques anteriores conservan sus actas focales y su alcance; no se suman
sus conteos ni se extrapolan a un cierre integral de M6.

## Estado reconocido al integrar

M1, M4, M5 y M5.5 cerrados en Windows. M2 y M3 conservan código completo y
aceptación manual con el modelo local pendiente. La cualificación real de M5
no sustituye esos dos criterios. M6 pasa de la declaración antigua «sin empezar»
a implementación parcial; M7–M10 no se inician por esta integración.

Pendientes de cierre M6 identificados en el código y actas recuperadas:

- Scheduling paralelo de lectores y background con capacidad real, prioridades
  y exclusión de escritores; journal atómico entre streams concurrentes.
- Pool compartido de Run con reservas y liberaciones reconstruibles; límites
  efectivos tanto para principal como para hijos.
- FanOut/FanIn Direct/Aggregate y supervisión en proceso por commands/eventos,
  handshake, fallo Wait observable, mailbox y wake determinista.
- Recuperación de ejecución interrumpida sin adoptar ownership desconocido ni
  repetir efectos o inferencia a ciegas.
- WorkflowCommands, tools Dynamic, inspector y criterio Explore → Implement →
  Verify como Tasks diferenciadas con Plan reconciliado.
- Autoridad/routing heredados sin ampliación; revocación durable; módulo sin
  cablear produce ReworkRequested en vez de una aceptación implícita.

## Verificación de la base integrada

- `dotnet build OmniCore.slnx --no-restore -v quiet`: exit0, 0 advertencias,
  0 errores, 57.50 s.
- `dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
  -class '*M4ProcessRecoveryTests' -noColor`: exit0, 1 PASS, 0 FAIL/SKIP,
  100.137 s. Log `C:/Users/juanc/.codex/m6-merged-m4-20261008.log`.

Esta evidencia corresponde a la base del merge, antes de los cambios pendientes
de M6. No acredita su cierre, llamadas autenticadas nuevas ni ejecución Linux.
La aceptación integral posterior debe identificar los binarios y pruebas finales.
