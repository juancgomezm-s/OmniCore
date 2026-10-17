# Consolidación de ramas de agentes — 2026-10-16

## Alcance e historial

Se revisaron las ramas locales/remotas y todos los worktrees registrados de OmniCore y OmniCoder.
Tras actualizar origin, la única rama Core aún fuera de main era
`codex/tui-openrouter-usability`, HEAD `16345a8d6c9a3753afda266ec5498d50616fa9ba`, con 17 commits
descendientes de `464cb83`. Se integró mediante merge real de dos padres `2305259`, sin conflictos,
sin squash ni reescritura. Conserva los avances de menús, proveedores, sesiones, temas, animaciones,
vista previa Markdown, skill visual y continuidad al aceptar otra ruta de modelo. El contrato
nativo M9-J y sus capacidades ya negociadas permanecen presentes; la paridad nativa pendiente no
se declara cerrada por incorporar los nuevos commands de conversaciones de la TUI.

Las otras ramas Core y las ramas existentes OmniCoder ya estaban incluidas. Los cuatro cambios
locales pendientes de OmniCoder se respaldaron en `%TEMP%\omni-consolidation-20261016-1600045164594cc5a9abd2dc5320db32`
y se conservaron en `3f9ca9d`, rama `codex/integrar-pendientes-20261016`: plan de seguridad por
conversación, panel, CSS y selectores E2E. El paquete se regeneró desde esas fuentes con el lock
exacto. Se mantienen los stashes anteriores y las ramas de respaldo
`codex/backup-main-before-merge-20261016`; no se borraron ramas ni worktrees de otros agentes.

Dos worktrees detached contenían commits únicos de artefactos generados: `0fe4a28` y `9876fb5`.
Se verificó exhaustivamente que sólo modifican `packages/app/out/` y que sus fuentes padre ya
pertenecen a main. Sus commits se incorporan al historial con merge de preservación `ours`,
manteniendo el paquete recién compilado; sus artefactos anteriores siguen recuperables por SHA.

## Corrección encontrada al revisar el merge

Si `conversation.new` o `conversation.resume` fallaba después de adquirir el lease SQLite del
destino, el Host restauraba la selección previa pero retenía ese lease. `f33c9af` registra qué
handle se adquirió realmente y lo libera al fallar; los leases preexistentes se conservan. El ACK
se construye antes de liberar la sesión anterior. Cuatro casos físicos de fallo de persistencia
comprueban new/resume, selección actual y destino ya poseído, con un segundo store competidor.
La sesión previa sigue protegida y los eventos ya persistidos no se eliminan.

## Validación proporcional

- `dotnet build OmniCore.slnx --no-restore -v minimal`: árbol recién fusionado compilado multi-target,
  0 advertencias y 0 errores. `%TEMP%\omni-merge-core-build-20261016.log`.
- `dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore --filter 'FullyQualifiedName~ConversationLifecycleTests|FullyQualifiedName~StdioTransportTests'`:
  recompila la corrección y pasa 46/46, sin omitidos. `%TEMP%\omni-merge-core-focused-20261016.log`.
- Arquitectura Core, sin recompilación innecesaria: 62/62. `%TEMP%\omni-merge-core-architecture-20261016.log`.
- Formatter Python offline: 4/4. `%TEMP%\omni-merge-core-formatter-20261016.log`.
- OmniCoder: build de las seis workspaces con React/React DOM 19.2.8, Electron root 44.2.0 y
  signal-exit 4.1.0, iguales al lock, sin modificar manifiestos ni lockfile.
  `%TEMP%\omni-merge-coder-dependencies-20261016.log` y `omni-merge-coder-build-20261016.log`.
- SDK/supervisor: 10/10; scanner 37 archivos, 143/143/141, sin bypass.
  `%TEMP%\omni-merge-coder-unit-20261016.log` y `omni-merge-coder-boundaries-20261016.log`.
- Electron real → Host integrado → HTTP fixture: chat/inferencia/historial/modo/interrupción
  aprobado en la corrida de dos casos; inventario/consentimiento/revocación/desconexión aprobado
  después de adaptar selectores al menú compacto. Son dos casos aprobados en corridas distintas,
  no una nueva suite integral. Logs `omni-merge-coder-e2e-20261016.log` y
  `omni-merge-coder-inventory-final-20261016.log` en `%TEMP%`.
- `Assert-NoOrphanWorkers.ps1`: `NO_ORPHANS`; `git diff --check` limpio. Se normalizaron únicamente
  finales de línea del HTML generado. `%TEMP%\omni-merge-coder-orphans-20261016.log`.

El primer intento E2E omitió los casos por no configurar `OMNICORE_E2E_ENTRY` y no se contó como
validación. Después, el inventario detectó selectores de summary ambiguos y un botón dentro del
menú Conexión cerrado; se corrigieron los selectores y la navegación del test, sin cambiar la UI ni
debilitar sus asserts. La evidencia del donante del 16 de octubre permanece válida para su árbol
idéntico; no se repitieron sus suites amplias ni la inferencia autenticada Luna → Sol.

Estas verificaciones no acreditan nuevos proveedores reales, instaladores ni cierre de M8/M9.
La verificación de publicación y CI remoto se informa por separado tras subir main.

## Comprobación final del consumidor

Se completaron los otros seis casos del archivo Host E2E: conexión/replay/caída, guard MCP,
cambio de proyecto con conexión pendiente, media.read, imágenes nativas CAS/persistencia y
command.sim real. Resultado: 6 aprobados, 2 filtrados por haberse validado antes, sin fallos;
`%TEMP%\omni-merge-coder-remaining-e2e-20261016.log`. Los ocho casos únicos del archivo se aprobaron
en corridas separadas. Sólo se adaptaron selectores al menú existente (summary directo, badge
Disponible y ruta del proyecto en Sidecar); se conservaron los asserts de comportamiento.
`%TEMP%\omni-merge-coder-orphans-final-20261016.log` confirma `NO_ORPHANS`.

Tras un nuevo fetch no quedan ramas locales/remotas fuera de main en ninguno de los dos repos.
Todos los HEAD de worktrees, incluidos los snapshots detached, son ancestros de main. Los otros
worktrees accesibles están limpios; los stashes previos se conservaron.
