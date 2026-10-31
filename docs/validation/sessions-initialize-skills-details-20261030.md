# Entrega verificable: sesiones, inicializar y skills

> Seguimiento: el avance local descrito abajo se recupera e integra en la
> [consolidación de Main](main-publication-20261031.md), con desktop y M10 vigentes.
> Las referencias a falta de commit/CI corresponden a este checkpoint histórico.

Checkout: `C:\Users\juanc\source\repos\OmniCore`, Main, base `04d226b9`.
Cambios locales sin commit, push ni merge. La CI del main anterior no valida este lote.
Este documento amplía el [acta de implementación](sessions-initialize-skills-20261030.md).

## Sesiones y alcance del historial

La entrada de producción `TuiApp.Run` solicita elección explícita al arrancar y
tras el bucle existente de cambio de workspace. Ofrece Nueva conversación,
Retomar existente y Continuar la última. La lista tiene título, fecha local y
búsqueda por título/ID; el foco inicial está en Nueva conversación.

Cancelar conserva el puntero durable y el borrador. La TUI mantiene una proyección
vacía y bloquea envío/acciones hasta elegir. Continuar la Session ya recuperada
sólo habilita su presentación: no escribe un turno, no reejecuta inferencia ni
responde una interacción. Nuevo/retomar otra Session siguen pasando por los
commands humanos autorizados del Host y sus guards de turno, writer y workspace.
Se bloquea cambiar de carpeta durante trabajo principal o delegado, interacción,
overlay o adjuntos pendientes. No se añadieron comandos de borrar/importar/copiar
historial. Vaciar la proyección no borra el journal; no se trasladan mensajes entre
Sessions ni carpetas. Los borradores de la vista están indexados por Session.

Comandos existentes conservados: `/new` o `/nuevo`, `/sessions` o `/sesiones`,
`/resume-session` o `/retomar`, y F6 para el selector. CLI conserva `omni new`,
`omni sessions`, `omni resume-session <id>` y sus traducciones existentes.
Dos Sessions por carpeta, dos Hosts/workspaces, reaperturas SQLite, cancelación,
turno durable abierto y trabajo principal/delegado tienen pruebas reales.

## Rutas de skills que realmente descubre el código

`app` es `AppContext.BaseDirectory`; `data` lo resuelve `DefaultPlatformPaths`.
En Windows, el default es `C:\Users\juanc\AppData\Local\OmniCore`; en Linux,
`$XDG_DATA_HOME/omnicore` o `~/.local/share/omnicore`. `OMNICORE_DATA_DIR` permite
reubicarlo desde el entorno del usuario/proceso. `workspaceData` es
`<data>/workspaces/<WorkspaceId>/`, fuera del repositorio.

| Fuente | Ruta de descubrimiento |
| --- | --- |
| Integrada | `<app>/skills` |
| Integrada de este build | `Main/src/OmniCore.Cli/bin/Debug/net10.0/skills/visual-explainer` |
| Fuente versionada que se empaqueta | `Main/skills/visual-explainer` |
| Usuario nativa | `<data>/skills` |
| Proyecto nativa | `<workspace>/.omnicore/skills`, con confianza del workspace |
| Workspace nativa | `<workspaceData>/skills` |
| Session nativa | `<workspaceData>/sessions/<SessionId>/skills` |
| Extensiones | `provides.skills`, dentro del paquete validado y con consentimiento `skill.register` |
| Portables de proyecto | `.agents/skills`, `.claude/skills`, `.opencode/skills` entre raíz Git verificada y cwd autorizado |
| Codex de usuario | `~/.agents/skills`, `$CODEX_HOME/skills`, `$CODEX_HOME/skills/.system`; default `C:/Users/juanc/.codex` |
| Claude de usuario | `$CLAUDE_CONFIG_DIR/skills`; default `C:/Users/juanc/.claude/skills` |
| OpenCode de usuario | `$OPENCODE_CONFIG_DIR/skills`; default `$XDG_CONFIG_HOME/opencode/skills` o `~/.config/opencode/skills` |

`OMNICORE_AGENT_HOME` permite aislar el home de descubrimiento. Un padre Git de un
cwd anidado necesita su propia confianza: la confianza del subdirectorio no se
transfiere al padre. No se recorren proyectos vecinos para buscar skills.
Los formatos nativos usan el paquete `skill.yaml`/instrucciones/recursos existente;
los adaptadores portables leen `SKILL.md` con sus restricciones, no ejecutan scripts.

## Comandos exactos de skills y resolución de menciones

El catálogo publica `/skill-<id>`; los puntos del ID pasan a guiones. Publica
además `/<id>` cuando sólo contiene letras ASCII, números, guion o underscore,
no está reservado y no colisiona. Una normalización en conflicto añade un sufijo
estable de ocho caracteres SHA-256 de `Scope:id`. El ID canónico de catálogo es
`skill.invoke.<scope-en-minúsculas>.<id>:<nombre-generado>` y conserva el origen.
Ejemplo del paquete incluido: `/skill-visual-explainer`; `/visual-explainer` sólo
si está libre. `/commands` o `/comandos`, ayuda, palette y autocompletado utilizan
ese catálogo. Siguen existiendo `/skills` o `/habilidades`, `load <id>` y
`release <id>` del flujo humano anterior.

El handler de comando devuelve un prompt con `Scope:id` y el objetivo; no envía
el cuerpo por el catálogo ni ejecuta una herramienta. La composición del turno
hace la activación humana existente y materializa el cuerpo una sola vez.

En texto nuevo del usuario se admiten ID, nombre visible completo, `$id`,
`/skill-id` dentro del texto o `Scope:id` (por ejemplo `Project:mi-skill`). El
matcher es léxico, case-insensitive y escapa el nombre como texto literal.
No coincide por substring, sufijo de otra palabra, ruta o extensión de archivo.
También excluye ocurrencias dentro de emails y URLs reconocidas, incluidos
query/fragment y `www.`. Una mención separada después de la URL sí se admite.
Los nombres completos más largos ganan sobre nombres contenidos; una mención
corta separada posterior se mantiene. Repetir un nombre no duplica activación.

Si quedan varias candidatas se rechaza antes de despachar al proveedor y se
muestran identidades con Scope. `User:id`/`Project:id` desambiguan cuando las
identidades son diferentes; un conflicto de fuentes de la misma identidad debe
resolverse en la fuente, no se elige en silencio. Nombres que coinciden con Core,
una extensión actualmente consentida o un alias humano requieren forma explícita
como `$id`, `/skill-id` o `Scope:id`. Los workflows activos conservan sus nombres
antes de admitir aliases nuevos de skills. Revocar una extensión invalida sus
nombres ocupados; no se usa un catálogo viejo para decidir la colisión.

Límites de resolución: 131.072 caracteres de input, 1.024 candidatas, nombres de
hasta 160 caracteres, 16.384 ocurrencias y regex con timeout de 100 ms. Después
se aplican `SkillSelector`, restricciones de invocación humana, confianza y los
límites de cantidad/contexto del harness efectivo (`usableContext/8`, techo
16.384 tokens para skills). Se valida el conjunto propuesto antes de mutar el
store. Una activación repetida con mismo hash/origen no duplica eventos; un
paquete modificado se valida de nuevo. Los workflows stale no quedan invocables,
pero el comando metadata sigue disponible para reactivar el paquete válido.

La detección se aplica sólo a input humano nuevo del principal. En stdio se
recupera sólo el `UserInputReceived` del rango autenticado del receipt y del mismo
Run/Session; no se vuelve a escribir el input. No se inspeccionan salida del
modelo, mensajes históricos, delegación ni continuaciones durables para activar
skills por esta vía. No cambia `allowed-tools`, permisos, sandbox, modo ni gasto.

Límite semántico explícito: una cita o una negación que contenga un nombre completo
inequívoco puede activarlo. No se interpreta intención lingüística; tampoco se
promete resolver cualquier texto con apariencia de URL sin esquema ni `www.`.

## Qué lee inicializar y cómo actualiza el archivo

Formas CLI: `omni initialize`, `omni inicializar`, `omni initialiser`,
`omni inizializza`, sin argumentos. La forma con slash también se acepta en CLI.
TUI: `/initialize`, `/inicializar`, `/initialiser`, `/inizializza`, con ayuda y
autocompletado. No convierte la palabra libre `inicializar` en comando del chat.
La acción es administrativa humana; no es una herramienta expuesta al modelo.

Lee desde la raíz actual, sin ascender a un repo padre:

- Raíz: `README.md`, `CLAUDE.md`, `AGENTS.override.md`, `package.json`,
  `global.json`, `Cargo.toml`, `pyproject.toml`, `go.mod`, `Makefile`,
  `Directory.Build.props`, `*.sln`, `*.slnx` y `*.csproj`.
- En el árbol permitido: `AGENTS.md`/`agents.md` y `CLAUDE.md` como instrucciones.
- Memorias: `MEMORY.md`, `MEMORIES.md`, `memory_summary.md`, `raw_memories.md`, y
  Markdown bajo directorios `memory`, `memories`, `.memory`, `rollout_summaries`.
- No abre bases de memoria nativas, transcripciones, cachés de agentes, memoria de
  home ni memorias de otros proyectos. No sigue imports Markdown o enlaces.

El destino raíz existente se busca por nombre con `OrdinalIgnoreCase`; se usa
su spelling real. Si no existe, crea `agents.md`. Si hay dos variantes distintas
en un filesystem sensible a mayúsculas, falla sin elegir una. El lector de
instrucciones de proyecto también enumera por nombre case-insensitive y abre la
ruta encontrada; en un conflicto deja la fuente inactiva con diagnóstico.

Se conserva el texto manual fuera de `<!-- omnicore:project:start -->` y
`<!-- omnicore:project:end -->`. Sólo se reemplaza ese bloque. Marcadores
incompletos/duplicados detienen la escritura. No se añade timestamp variable:
las mismas fuentes ordenadas y hashes producen el mismo contenido. `Changed`
compara contenido completo; si no cambia, no se reescribe. Antes de mover el
temporal se recalculan confianza, fuentes y preimagen; una edición manual o de
fuentes detectada cancela la actualización, no la sobrescribe.

La síntesis es determinista y no usa LLM: stack/manifiestos, scripts y comandos
declarados, inventario con SHA-256 y extractos de hasta 1.200 caracteres. El JSON
escapa marcadores y presenta memorias como datos de baja autoridad, no permisos ni
órdenes para ejecutar. CLI informa creado/actualizado/sin cambios y fuentes;
la TUI añade revisión desplazable del bloque resultante. No se promete un diff
unificado de todo el contenido manual anterior.

Límites: 64 fuentes incluidas, 64 KiB UTF-8 por archivo, 8.192 entradas del escaneo,
profundidad 12 y alrededor de 28.000 caracteres generados; el destino completo
no puede superar 64 KiB. UTF-8 inválido, NUL, archivos grandes o credenciales
posibles se omiten con diagnóstico. Se excluye la fuente completa si cambia bajo
el redactor; eso puede producir falsos positivos, no una exportación parcialmente
redactada. Se filtran rutas secretas y directorios generados/privados, otros repos,
symlinks y junctions; se comprueban también ancestros y destino de escritura.

Se respetan `.gitignore`, `.ignore`, `.omniignore` y `.git/info/exclude` local.
Globs no admitidos omiten el directorio y las negaciones no reabren excluidos.
Un worktree con `.git` apuntando a metadata externa omite fuentes con diagnóstico,
sin leer esa metadata fuera del proyecto. No ejecuta Git ni comandos de fuentes.
Se exige confianza vigente, writer, lease del workspace y ausencia de turnos
abiertos/delegaciones antes de generar/aplicar.

## Verificación de nombres en Windows y alcance Linux

La enumeración real de Main devolvió exactamente un destino: `agents.md`.
No se creó un segundo `AGENTS.md` ni un archivo `agentes.md` en español.
Pasaron las pruebas de preservar un `AGENTS.md` existente y de descubrir una sola
vez el `agents.md` generado. La ruta encontrada evita depender de la equivalencia
de casing de Windows. La rama de colisión de dos case-variants está prevista
para Linux; no se ejecutó esta batería en Linux y no se afirma aceptación en ese OS.

## Pruebas ejecutadas y qué acreditan

- Solución completa: `dotnet build OmniCore.slnx --no-restore -m:1 -v:q`;
  correcta, cero errores/advertencias. Proyecto de tests reconstruido después
  de añadir las capturas opcionales, también correcto.
- Batería revisada: 244 casos, 243 aprobados, cero fallos, una omisión, 65,773 s.
  Combina unitarias, servicios, integración real y E2E de CLI/proveedor HTTP local;
  no son 244 pruebas visuales. Entre ellas: 29 del resolver/activación humana,
  15 del inicializador (una omitida), 22 del ciclo de conversación, ocho de
  CLI E2E, ocho de stdio y 21 de cableado TUI programático.
- Arquitectura: 62/62 aprobadas, sin omisiones ni fallos, 1,570 s.
- Foco adicional con temporales bajo Main: 12/12 aprobadas (cinco casos TUI,
  uno de dos workspaces y seis nativos de admisión/reinicio); 27,159 s.
- Captura final: cinco casos TUI repetidos, 5/5 aprobados, 9,281 s.
  Son subconjunto/repetición; no se suman como pruebas únicas a las 244.

Evidencia final: `TestResults/sessions-final-review/isolated-result.xml` y `.log`,
`TestResults/sessions-final-review/architecture.xml` y `.log`;
`TestResults/sessions-visual-evidence/smoke.xml`, `final-capture.xml` y sus logs.
Los procesos de prueba usan sus perfiles sintéticos de datos/configuración. El
smoke y sus capturas quedaron dentro de Main. Las fixtures generales conservan
su ubicación temporal efímera definida por la batería, sin clones ni respaldos.

Un intento de reubicar TODA la batería bajo Main produjo 11 fallos de aislamiento
Git de fixtures (232 aprobadas, 11 fallidas y una omitida de 244): esas
fixtures quedaron anidadas en el repo Main, y el descubridor exigió confianza
independiente para el ancestro, tal como debe. Se conserva ese resultado en
`TestResults/sessions-final-review/result.xml`; no es la pasada de aceptación.
No se relajó la confianza ni se cambió el descubridor para ocultarlo. La repetición
en la topología prevista por los tests pasó 243/244 con la única omisión descrita.

Comando reproducible de la batería final (desde Main, perfiles de fixtures aislados):

```powershell
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -parallelMode none -filterVSTest 'FullyQualifiedName~Skill|FullyQualifiedName~ProjectInitializerTests|FullyQualifiedName~SessionOpening|FullyQualifiedName~Initialization_|FullyQualifiedName~Cli_inicializar|FullyQualifiedName~ConversationLifecycleTests|FullyQualifiedName~WorkspaceNavigation|FullyQualifiedName~ExternalAgentDiscoveryTests|FullyQualifiedName~CommandRegistryTests|FullyQualifiedName~CommandCatalogPresentationTests|FullyQualifiedName~CommandPaletteTests|FullyQualifiedName~HumanCommandAliasTests|FullyQualifiedName~LocaleCatalogCoverageTests|FullyQualifiedName~Conversation_commands_clear|FullyQualifiedName~Directory_navigation_requests|FullyQualifiedName~A_second_CLI_slash_invocation|FullyQualifiedName~First_native_explain_admits|FullyQualifiedName~Native_conversation_lifecycle_uses|FullyQualifiedName~Conversation_admission_is_separate|FullyQualifiedName~Native_pending_permission_survives|FullyQualifiedName~Native_questionnaire_response_resumes|FullyQualifiedName~Native_questionnaire_response_survives' -result-xml TestResults/sessions-final-review/isolated-result.xml -noLogo

dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll -parallelMode none -result-xml TestResults/sessions-final-review/architecture.xml -noLogo
```

Para capturas se fijaron `TMP`/`TEMP` a `Main/TestResults/sessions-visual-evidence/temp`
y `OMNICORE_TUI_SNAPSHOT_DIR` a `.../frames`; se ejecutaron sólo
`SessionOpening_requires_choice`, `SessionOpening_cancel_keeps` y
`SessionOpening_new_creates` con el runner en serie. El wrapper de esos tres
métodos habilita explícitamente el color durante la fixture y restaura el entorno.

## Capturas de framebuffer y smoke nativo pendiente

`TestResults/sessions-visual-evidence/frames` contiene SVG/TXT de celdas reales
estables del driver de Terminal.Gui, y PNG derivados para inspección. Se revisaron
selector 80x25 y 120x40, cancelación con borrador sin envío, sesión nueva, lista
con dos Sessions y retomar la original. La navegación/cambio de carpeta con dos
Sessions y reaperturas se verificó programáticamente con Hosts/SQLite aislados;
no se observó ese cambio en una ventana nativa.

Los PNG no son capturas del escritorio. El conversor sólo rasteriza los rectángulos,
texto y estilos del SVG exportado; puede diferir en fallback de glifos del terminal.
La prueba afirma geometría/contenido/foco/flujo del driver; no prueba interacción
humana, terminal físico, IME, todos los DPI o apariencia de cada terminal.

Se intentó abrir una TUI nativa con perfil propio bajo
`Main/TestResults/visible-session-smoke`. Las consolas iniciadas desde la sesión de
ejecución no ofrecieron una ventana controlable; esos procesos propios se cerraron.
La única solicitud de lanzamiento por Computer Use fue:

```javascript
await cua.computer.launch_app({app:"C:\\Users\\juanc\\source\\repos\\OmniCore\\TestResults\\visible-session-smoke\\Launcher\\bin\\Debug\\net10.0\\Launcher.exe"});
```

Herramienta: `mcp__cua_repl.js`. Objetivo: un lanzador local de fixture, que fija
perfil/cwd aislados y abre el `omni.exe` compilado. La herramienta devolvió
`Computer Use app approval timed out`. Coordinación informó que la tarjeta decía
`Allow Codex to use Synology Drive Client?`. Se registra la discrepancia entre la
etiqueta reportada y el argumento `Launcher.exe`, SIN atribuir una causa.
No se seleccionó ni operó una ventana de Synology, no se enviaron clics/teclas a
esa aplicación y no hubo cambio de objetivo por una ventana en primer plano.
Después de caducar no se repitió el lanzamiento ni se buscó una vía alternativa
para ese permiso. El smoke nativo queda pendiente; no se contabiliza como éxito.

## Límites y pendientes reales

- Omitida `ProjectInitializerTests.Symlink_memory_and_broken_target_are_rejected_without_following_outside_paths`:
  el entorno no permite crear symlinks. No se cambiaron privilegios para forzarla.
- Linux no ejecutado; compatibilidad de casing revisada en código y pruebas
  portables, sin afirmar ejecución en filesystem Linux.
- Smoke de ventana nativa pendiente por la aprobación caducada/discrepante;
  framebuffer y driver programático se distinguen de esa aceptación.
- Menciones son léxicas: las citas/negaciones siguen siendo un límite semántico.
- Fuentes son convencionales/acotadas; worktrees con metadata externa y patrones
  ignorados no admitidos conservan la omisión segura documentada.
- La comparación de preimagen detecta cambios observados antes del rename; no
  se afirma compare-and-swap del filesystem frente a un escritor externo que
  escriba exactamente entre la última comprobación y el rename atómico.
- Sin proveedor externo autenticado ni nueva CI de este lote.

## Archivos de este incremento
- `agents.md`
- `docs/project-initialization.md`
- `docs/skills-human-invocation.md`
- `docs/validation/session-opening-20261030.md`
- `docs/validation/sessions-initialize-skills-20261030.md`
- `docs/validation/sessions-initialize-skills-details-20261030.md`
- `src/OmniCore.Cli/CliApp.cs`
- `src/OmniCore.Cli/CliApp.Initialization.cs`
- `src/OmniCore.Cli/Tui/TuiApp.Conversations.cs`
- `src/OmniCore.Cli/Tui/TuiApp.cs`
- `src/OmniCore.Cli/Tui/TuiApp.DirectControls.cs`
- `src/OmniCore.Cli/Tui/TuiApp.Initialization.cs`
- `src/OmniCore.Client/Localization.cs`
- `src/OmniCore.Client/ProjectEntryResources.cs`
- `src/OmniCore.Host/ExternalAgentDiscoveryCatalog.cs`
- `src/OmniCore.Host/OmniCliRuntime.cs`
- `src/OmniCore.Host/OmniCliRuntime.Initialization.cs`
- `src/OmniCore.Host/OmniCliRuntime.SkillInvocations.cs`
- `src/OmniCore.Host/OmniServer.cs`
- `src/OmniCore.Host/OmniServer.SkillInvocations.cs`
- `src/OmniCore.Host/ProjectInitializer.cs`
- `src/OmniCore.Host/SkillActivationResolver.cs`
- `src/OmniCore.Host/SkillHumanInvocation.cs`
- `src/OmniCore.Host/SkillInvocationCommands.cs`
- `src/OmniCore.Host/SkillWorkflowCommandHandler.cs`
- `src/OmniCore.Host/TuiTurnHost.cs`
- `src/OmniCore.Protocol/HumanCommandAliases.cs`
- `tests/OmniCore.Tests/CliEndToEndTests.cs`
- `tests/OmniCore.Tests/CliEndToEndTests.Initialization.cs`
- `tests/OmniCore.Tests/CliEndToEndTests.SkillInvocations.cs`
- `tests/OmniCore.Tests/ProjectInitializerTests.cs`
- `tests/OmniCore.Tests/SessionOpeningWorkspaceRuntimeTests.cs`
- `tests/OmniCore.Tests/SkillHumanInvocationTests.cs`
- `tests/OmniCore.Tests/SkillInvocationCatalogBoundaryTests.cs`
- `tests/OmniCore.Tests/TuiWiringTests.cs`
- `tests/OmniCore.Tests/TuiWiringTests.Initialization.cs`
- `tests/OmniCore.Tests/TuiWiringTests.SessionOpening.cs`
- `tests/OmniCore.Tests/TuiWiringTests.SkillInvocations.cs`
- `tests/OmniCore.Tests/TuiWiringTests.WorkspaceNavigationActivity.cs`

ToolNameWireMap.cs figuraba modificado antes del encargo y no se editó como parte de este lote. Los artefactos de TestResults, incluidos lanzadores de prueba y capturas, están ignorados por Git y no forman parte de los archivos de producto.
