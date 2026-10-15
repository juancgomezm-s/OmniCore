# Descubrimiento local de skills, instrucciones y memorias

Estado: **implementado localmente en `codex/m9-agent-discovery-core`; revisión e integración pendientes** (2026-10-15). Bloque independiente
de los adjuntos de texto/documentos/imágenes. OmniCoder también tiene cambios locales
en `codex/m9-agent-discovery-coder`; ambos árboles requieren revisión raíz antes de integrar.
No reabre OR5 de M8 ni acredita la memoria multiscoped completa de M10.

## Base y resultado esperado

OmniCore ya tiene discovery, `skill.load`, activación, presupuestos y permisos para
su formato `skill.yaml` + instrucciones. Falta adaptar el formato portable
`SKILL.md` y reconocer las fuentes locales de otros agentes. Reutilizar esos
consumidores; descubrir un archivo no acredita que sus instrucciones lleguen al
modelo.

Una misma inspección debe distinguir **skill**, **instrucciones** y **memoria**,
mostrar procedencia, ámbito, ruta, hash y estado aplicable/inactivo/conflicto/error.
`AGENTS.md` y `CLAUDE.md` son instrucciones; no se convierten artificialmente en
skills. Las memorias entran como datos con procedencia, según ADR-0028/0029.

## Ubicaciones acotadas

`~` representa el directorio del usuario del Host. Los overrides de directorio
proceden del entorno/configuración de usuario confiable, nunca del repositorio.

| Fuente | Lugares de descubrimiento |
|---|---|
| Skills portables de Codex y agentes | `.agents/skills/<nombre>/SKILL.md` en la cadena directorio activo–raíz del repo; `~/.agents/skills`. Compatibilidad con `<CODEX_HOME>/skills` (por defecto `~/.codex/skills`) y su subdirectorio conocido `.system`, presentes en instalaciones existentes. |
| Skills Claude | `.claude/skills/<nombre>/SKILL.md` en esa cadena; `<CLAUDE_CONFIG_DIR>/skills` (por defecto `~/.claude/skills`). |
| Skills OpenCode | `.opencode/skills/<nombre>/SKILL.md` en esa cadena; `$XDG_CONFIG_HOME/opencode/skills` o `~/.config/opencode/skills`, con override de directorio confiable cuando corresponda. Dedupe de raíces compatibles compartidas. |
| Instrucciones por directorio | `AGENTS.override.md`, `AGENTS.md`, `CLAUDE.md`, `.claude/CLAUDE.md`, `CLAUDE.local.md` en los directorios aplicables. `AGENTS.override.md` sustituye a `AGENTS.md` en el mismo ámbito Codex. Conservar las otras fuentes y explicar conflictos y precedencia; no escoger silenciosamente según qué agente las descubrió. |
| Instrucciones de usuario | `<CODEX_HOME>/AGENTS.override.md` o `AGENTS.md`; `<CLAUDE_CONFIG_DIR>/CLAUDE.md`; directorio de configuración OpenCode `AGENTS.md`. Un archivo común descubierto por varios adaptadores se representa una sola vez. |
| Memoria Claude por proyecto | `<CLAUDE_CONFIG_DIR>/projects/<proyecto>/memory/MEMORY.md` y temas locales de ese directorio, con identidad de proyecto comprobada. Respetar un directorio de memoria explícito confiable. No elegir por nombre parecido ni leer memorias de todos los proyectos para encontrar una coincidencia. |
| Memoria Codex | `<CODEX_HOME>/memories`: inventario local acotado de formatos reconocidos, resúmenes y entradas. Mantener el ámbito global o el proyecto verificable del registro; no atribuir un resumen global al repo actual. No leer bases internas, transcripciones ni credenciales. |
| Memorias locales convencionales | `MEMORY.md`, `.claude/memory/MEMORY.md`, `.agents/memory/MEMORY.md` y `.opencode/MEMORY.md` en los directorios aplicables. Son convenciones de compatibilidad de OmniCore; no se presentan como formatos nativos universales de todos los agentes. |

La cadena empieza en la raíz física del proyecto y termina en el directorio activo;
al trabajar sobre un archivo anidado sólo se consultan sus ancestros aplicables.
Sin repo, limitarse al directorio autorizado. No recorrer descendientes ajenos,
unidades, todos los repositorios, cachés de plugins ni árboles completos de home.
Paquetes de plugins sólo mediante registro explícito existente.

## Integración y límites

- Parser real de frontmatter YAML y cuerpo Markdown, conservando `name`,
  `description` y restricciones de invocación reconocidas. Índice de metadatos;
  cuerpo bajo demanda y presupuesto. Metadatos incompatibles reciben diagnóstico.
  Ningún hook, sustitución de shell, MCP, script o modelo externo se activa por
  descubrir un paquete. `allowed-tools` nunca concede permisos de OmniCore.
- Instrucciones y memorias tienen contributors separados. Sólo fuentes aplicables
  al directorio y permitidas por confianza llegan a contexto; proyecto no confiable
  no lee sus archivos. Memorias externas no se promocionan ni escriben en silencio.
  La memoria global conserva Ask y los controles existentes de privacidad.
- Límites explícitos de directorios, paquetes, profundidad, bytes y contexto;
  cancelación y UTF-8 válido. Identidad física, dedupe, enlaces/ciclos y rutas
  protegidas se validan antes de leer. Redactar/rechazar secretos antes de persistir
  o materializar contenido. Conservar los originales sin modificarlos.
  Imports y globs declarados sólo dentro de raíces autorizadas y con cotas;
  referencias remotas quedan inactivas y visibles, sin descarga automática.
- Invalidación por contenido y directorio: al cambiar proyecto, fuente o confianza
  no se conserva una activación antigua. Snapshot/procedencia reproducibles;
  ninguna memoria de otro proyecto aparece por recuperar una sesión.
- Cablear catálogo e inspección actuales de CLI/TUI y Host/OmniCoder, con recursos
  propios es/en/fr/it. Sin rediseñar menús ni introducir un segundo runtime.

## Aceptación independiente

Fixtures con home/config/repos aislados prueban cada raíz, overrides, dos proyectos,
subdirectorios y worktrees; archivos fuera de las raíces no se consultan. Verificar
skills portables mediante `skill.load` hasta el request capturado del modelo;
instrucciones aplicables y memoria con procedencia en contexto real. Cubrir
duplicados/conflictos, UTF-8/frontmatter inválido, exceso de presupuesto, enlaces,
workspace no confiable, cambio de directorio y fuentes eliminadas. Inspección por
cliente real y pruebas focales, sin repetir la suite integral de M8.

M9 incorpora estos adaptadores/lecturas externas; M10 conserva creación, promoción,
consolidación, expiración y almacenamiento completo Project/Workspace/Global.
Su cierre se acredita por separado del bloque de adjuntos.

## Implementación local M9-G (2026-10-15)

El Host expone sólo metadatos por `agent.sources`; no devuelve cuerpos en el inventario.
Las skills portables `SKILL.md` se adaptan al consumidor de skills existente y `skill.load`
recorre `Prepare → AuthorizedIntent → Execute` antes de aparecer en un request capturado.
`SkillIndexContributor` incluye descripción acotada/redactada y filtra invocation restrictions;
ningún `allowed-tools` se convierte en permiso. Las activaciones se invalidan ante cambios
de directorio físico, cuerpo o política del paquete.

La consulta y los controles Ask están conectados a `/skills sources` y `/memory external`
en TUI, y al panel OmniCoder mediante query/capability stdio, main, preload y bridge. Electron
main exige grant humano vigente y confirmación nativa por acción para permitir/revocar; no
acepta grants del renderer. Memoria global Codex/Claude requiere Ask; memoria de proyecto se
enlaza a la identidad exacta del workspace autorizado. Al revocar se limpian enlaces. Workspaces
sin confianza no leen contenido local. `OMNICORE_AGENT_HOME` permite aislar las raíces de agente
en pruebas, restringiendo overrides heredados a ese perfil.

El lector usa cotas y sólo raíces conocidas; las fuentes no se ejecutan. Codex activa únicamente
`MEMORY.md`, `memory_summary.md`, `raw_memories.md` y `rollout_summaries/*.md`; otros nombres
quedan visibles como inactivos y no se recorren bases/transcripciones. Claude auto-memory respeta
un `autoMemoryDirectory` de configuración confiable y Ask. Los imports Markdown Claude y globs
OpenCode sólo expanden archivos locales bajo la raíz autorizada, con cotas; remoto, escape,
reparse/symlink, secreto, ciclo, formato no admitido o configuración inválida se reportan sin
seguirlos. El cwd/workspace de ejecución no cambia al detectar Git: para un cwd anidado se busca
el checkout físico verificado y se consulta por separado su confianza antes de leer ancestros. Los
worktrees enlazados leen desde su checkout físico; la identidad común comprobada se usa para asociar
memoria, no para ampliar la raíz de lectura. Si la confianza del root no se puede confirmar, sus
archivos quedan inactivos y visibles. No se ofrece paridad de extensiones de ejecución Claude (hooks/fork/model), imports
remotos/recursivos OpenCode ni memorias internas Codex no incluidas en esa allowlist.

La validación se registra en `docs/validation/m9-block-g-20261015.md`. Incluye pruebas de catálogo,
instrucciones, `skill.load` y requests capturados, invalidación/revocación, diálogo y consulta desde
Electron con Host real y perfil temporal. Son fixtures offline; no se leyó el perfil del usuario ni
se declara M9 cerrado, M8 OR5 aceptado, aceptación live/manual TUI de F/F2, o verificación completa
multi-OS/cuatro idiomas. Los conteos exactos se mantienen en esa acta tras la validación final.

## Fuentes de compatibilidad

Documentación oficial consultada el 2026-10-14:
[skills Codex](https://learn.chatgpt.com/docs/build-skills),
[AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md),
[memorias Codex](https://learn.chatgpt.com/docs/customization/memories),
[skills Claude](https://code.claude.com/docs/en/skills),
[instrucciones y memoria Claude](https://code.claude.com/docs/en/memory),
[skills OpenCode](https://opencode.ai/docs/skills/),
[reglas OpenCode](https://opencode.ai/docs/rules/).
Las rutas de compatibilidad adicionales se distinguen arriba de los formatos
documentados; versiones/metadatos desconocidos no se declaran soportados.
