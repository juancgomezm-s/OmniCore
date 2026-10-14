# Homologación: cierre de la contribución de OmniCore — 2026-10-14

**Estado: contribución de OmniCore cerrada en Windows.** Base revisada: `6dc3bec`, más las
correcciones y pruebas incluidas en este mismo commit. El usuario indicó que la experiencia
actual y los últimos menús ya son una buena referencia y pidió cerrar las discrepancias
concretas conservando lo que funciona. No se sustituye esa interfaz por otra estructura.

Este cierre resuelve el alcance CLI/TUI actual de Core, no declara aceptados OmniCoder u
OmniLlama ni levanta por sí solo `P0-OMNI-UNIFICACION` global. La aceptación autenticada OR5
de M8 y los contratos todavía no implementados de M9/M10 conservan sus propios criterios.

## Discrepancias reproducidas y corregidas

1. **Texto de conversación recortado:** el envío aplicaba `Trim()` al texto del editor,
   perdiendo indentación inicial y espacios o líneas finales. Ahora el prompt ordinario llega
   literal al Turn local o a `session.input`, según el cliente. Los saltos de línea corresponden
   al texto del editor Terminal.Gui en la plataforma; no se modifica su normalización interna.
   La gramática y los aliases de comandos mantienen su comportamiento existente.
2. **Borrador eliminado sin envío aceptado:** una referencia `@` no disponible o un comando
   desconocido mostraban un aviso y vaciaban el editor. La ruta directa `session.input` además
   ignoraba el rechazo del servidor. Ahora se conserva el texto editable y se explica el fallo;
   se respeta también `Outcome=Rejected` aunque el status compatible sea `ok`. Un reintento
   aceptado vacía el editor y crea una sola entrada canónica. Un workflow sin Host local
   conserva igualmente su invocación en vez de consumirla sin ejecutarlo.

La corrección está en [TuiApp](../../src/OmniCore.Cli/Tui/TuiApp.cs). Las seis regresiones de
[ComposerAcceptance](../../tests/OmniCore.Tests/TuiWiringTests.ComposerAcceptance.cs) recorren
el editor y Enter de producción con el servidor persistente de la fixture. Dos variantes
comprueban el prompt literal hasta el journal, dos prueban rechazo/reintento y las otras
comprueban comando desconocido y referencia no disponible. El rechazo inyectado se identifica
como tal: no se presenta como una cuenta remota ni como una conexión fallida real.

Las seis fallaron sobre la implementación previa. Al ajustar las pruebas se corrigieron dos
expectativas del harness: terminadores de línea del editor y comparación del texto JSON
decodificado, en lugar de su escape Unicode. No se cambiaron las reglas de producción para
hacerlas coincidir con la fixture.

## Resolución de U-C01–U-C12 en Core

La matriz de arquitectura conserva el inventario original. Esta tabla resuelve su alcance
local: una capacidad diferente o planificada no se convierte en defecto de homologación.
Las diferencias se registran con el criterio vigente del usuario de preservar la interfaz.

| ID | Resolución de Core y límite explícito | Evidencia |
|---|---|---|
| U-C01 | Conservar marca OmniCore, roles y estilos actuales. Terminal adapta color, foco y glyphs; no se promete tema claro/sistema ni identidad de píxeles con Electron/React. | `TuiPresentation`, `ThemeGlyphs`, `TuiApp.ApplyTheme`; wiring de conversación y preferencias con/sin color. |
| U-C02 | Conservar hilo, autoría, Markdown, posición de lectura y selección. Los eventos actualizan la vista sin volver a escribir la conversación. | `ConversationHistoryCanonicalTests`, `ConversationMarkdownRegressionTests`; wiring de scroll, selección y contenido entrante. |
| U-C03 | Corregido el envío literal y el borrador en los rechazos descritos arriba. Enter envía; Shift+Enter inserta línea. `@` sugiere rutas, pero explica que no materializa referencias. `media.read` permanece en M9. | Seis regresiones nuevas; wiring existente de foco, Turn, cancelación y autocompletado. |
| U-C04 | Conservar actividad y esfuerzo solicitado/aplicado. No hay panel equivalente de razonamiento público plegable; no se inventa contenido a partir de estado opaco. Diferencia de capacidad, sin panel nuevo. | `ActivityPresentation`, status line; wiring de actividad, fases y finalización/cancelación. |
| U-C05 | Conservar Markdown/tablas, lenguaje y selección/copia nativa del texto, sin ANSI ni adornos en el contenido copiado. Copiar bloque por identidad mediante toolbar no está construido; no se declara equivalente ni se exige como rediseño. | `ConversationPresentationTests`, `MarkdownRichPresentationTests`, `MarkdownTableRendererTests`; copia nativa y enlaces de `TuiWiringTests`. |
| U-C06 | Conservar catálogo, detalles y estado real de herramientas/MCP. Inspeccionar o seleccionar no equivale a ejecutar ni concede permiso. La UI de terminal no requiere la tarjeta React. | Wiring de Tools, estados vacío/ausente/inválido y permisos; ejecución MCP/Ask/Deny ya verificada en el acta M8. |
| U-C07 | Conservar inspección de contexto y procedencia disponible. `/context` no se anuncia como RAG/citas; fuentes web o enlaces sólo reflejan metadata real. | `SessionObservabilityProjection`; wiring `/context`, vistas de inspección y enlaces; acta M8 para web/MCP. |
| U-C08 | Conservar preferencias, guardar/cancelar, valores efectivos, modelos y conexiones. Los perfiles técnicos de Core no reintroducen los perfiles por conversación retirados de OmniLlama. OR5 necesita cuenta real y no se acredita con fixtures. | Wiring de preferencias/cuentas/políticas/seguridad/diagnóstico y conflictos de revisión; aceptación OR5 separada. |
| U-C09 | Conservar menús, sidebar y atajos actuales. Slash/menu/tecla llegan a sus handlers existentes; volver con Esc devuelve el foco. Paleta global y bindings configurables siguen en M10. | `ClientActionTests`, `TuiWiringTests.Commands`, ayuda y autocompletado; comprobación negativa nueva de comando desconocido. |
| U-C10 | Conservar estados y errores con texto; cuotas/costes/velocidades ausentes siguen desconocidos. La señal no depende sólo de animación o color. Rechazo de input ahora tiene explicación visible. | `ConversationUsageSafetyTests`; wiring de consentimiento, cancelación, error, actividad detenida y movimiento reducido. |
| U-C11 | Conservar es/en/fr/it, contenido literal, borrador, foco y preferencias. ASCII se aplica a los componentes que lo soportan; no se promete que todo Unicode del usuario se translitere. | `ClientCommandLocalizationTests`, `ShellLocalizationTests`, preferencias estrechas/localizadas, copia con `NO_COLOR` y pruebas nuevas de literalidad. |
| U-C12 | Fuera de la homologación de la TUI actual. El transporte externo Electron→Host, reconexión/lease y origen humano verificable quedan en M9. `IOmniClient` in-process y stdio MCP no los sustituyen. | [Wire](../../src/OmniCore.Protocol/Wire.cs), contrato y dependencias M9 de arquitectura; no se afirma un recorrido externo que no existe. |

Convenciones existentes que se conservan: roles `Active`/`Success`/`Attention`/`Agent`/`Info`/
`Error`/`Muted`/`Primary` expresan actividad, éxito, atención, agente, información, fallo,
secundario y texto principal. Glyph y texto mantienen el significado cuando no hay color.
El foco usa selección/inversión en los controles existentes; no se exportan colores del
renderer al protocolo ni se agregan dependencias visuales al runtime.

Acciones disponibles: conversación → Enter; nueva línea → Shift+Enter; ajustes → F4 o
`/preferences`; contexto → `/context`; herramientas → `/tools`; modelos → `/models`;
ayuda → `/help`; cerrar panel → Esc; sidebar → F2; modo → Shift+Tab; detener →
`/interrupt` o `/cancel`, con cancelación del Turn activo en la TUI. Los aliases de idioma
no cambian los IDs de acción/command ni la autorización.

## Verificación y límites

Fuente corregida `TuiApp.cs`, SHA-256:
`D422B8AA18AD396DBA3721322EC79E05AF024CB55F7E885D8DF6862C6CB69C8C`.

| Verificación | Resultado observado |
|---|---|
| `dotnet build OmniCore.slnx --no-restore -v:q` | Exit 0; 0 advertencias, 0 errores; 8.70 s. |
| Lote de CLI/TUI y presentación, `--no-build --no-restore --filter` | 316 casos: 314 correctos, 2 fallos de las nuevas expectativas JSON escapado, 0 omitidos; 4 min 05.942 s, exit 2. El journal ya contenía el prompt exacto: fallaba comparar su representación escapada con Unicode literal. No se declara verde este lote. |
| Recompilación y pasada final de las seis regresiones del composer | 6 correctas, 0 fallos, 0 omitidas; 6.039 s, exit 0. Incluye las dos expectativas JSON corregidas. El resto del lote no se repitió porque no cambió producción ni sus pruebas. |
| `dotnet test tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-build --no-restore -v:q` | 62 correctas, 0 fallos, 0 omitidas; 7.926 s, exit 0. |
| `git diff --check` y referencias documentales | Sin errores; doce IDs únicos y rutas locales existentes. |

Así quedan cubiertos los 316 casos distintos del área mediante el lote y la pasada final,
además de los 62 de arquitectura, sin fallos pendientes. El filtro del lote incluyó
`TuiWiringTests`, `ConversationHistoryCanonicalTests`, `ConversationPresentationTests`,
`ConversationMarkdownRegressionTests`, `ConversationUsageSafetyTests`,
`MarkdownRichPresentationTests`, `MarkdownTableRendererTests`,
`ClientCommandLocalizationTests`, `ShellLocalizationTests` y `ClientActionTests`.

Logs de esta máquina en `%TEMP%`: `OmniCore-homologation-build-20261014.log`,
`OmniCore-homologation-tests-20261014.log`,
`OmniCore-homologation-composer-final-20261014.log` y
`OmniCore-homologation-architecture-20261014.log`. El primer lote queda conservado también
con sus fallos de aserción; no se sobrescribe para aparentar una ejecución verde.

Se reutiliza la suite integral M8 sobre `09ad2ba`: 4,505 correctas, cero fallos y 11 omisiones
explícitas; no se presenta como una suite repetida sobre este cambio. La verificación nueva
cubre el área CLI/TUI y los contratos de presentación afectados. Se mantienen las fronteras
de Client/Protocol/Host; sólo cambia el renderer y se agregan pruebas de su envío.

El driver DOTNET, controles de producción y journal SQLite son reales. El Turn/proveedor y
algunos rechazos/cuentas son fixtures deterministas: esta evidencia no acredita inferencia,
créditos o conexión autenticada de OpenRouter. La conformidad del usuario con la referencia
visual/menús existentes se registra tal como fue expresada; no se inventa una aceptación
interactiva de esta corrección ni de los otros dos productos. El lote reutiliza las pruebas
existentes que exportan frames del driver; no se hace una nueva campaña visual interactiva
al no cambiar layout, temas ni navegación.

**Continuación:** Core no tiene pendiente otra campaña UC-A–UC-D sobre estas superficies.
Reabrir sólo por una discrepancia concreta reproducible. Completar el registro global con los
owners de los otros productos; conservar el gate global hasta su cierre. Para construcción,
el próximo pendiente propio es OR5 autenticado de M8 y después el alcance técnico M9 previsto,
sin confundir este cierre local con la implementación de ese transporte.
