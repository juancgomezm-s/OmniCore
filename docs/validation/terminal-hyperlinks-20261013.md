# Enlaces de terminal — corrección (2026-10-13)

Base de trabajo `6d09135`; revisión de código `8cf0fd85fc759b114427b102cddaa9f7fc317b0d`,
desarrollada en el worktree aislado `terminal-hyperlinks`, rama `codex/terminal-hyperlinks`.
Árbol `src`: `a7bc73e5caa0e12425c9fc22f9ab6ef62ea5b3b4`.
Árbol `tests`: `a51ef1ba4e35b6afd8836768b4f1e4b8a28f34b0`.

## Defecto y comportamiento corregido

El renderer mostraba enlaces subrayados, pero perdía su destino al materializar las
celdas y no tenía una acción de apertura. `ConversationSpan` conserva ahora el destino
de Markdown, autolinks y URLs, incluyendo estilos, tablas, ajuste de línea y scroll.
Los mensajes literales conservan su texto y reciben solo metadata de enlace.

`StyledTextPresenter` asocia las celdas con destinos; clic simple o Enter sobre el enlace
llega a la fachada `WebLinkHost`, que usa el navegador predeterminado de plataforma.
Seleccionar por arrastre y copiar no inicia apertura. Renderizar, recibir contenido o
cambiar de idioma tampoco. Reemplazar el contenido elimina los enlaces anteriores.
La ayuda y el error de apertura tienen recursos es/en/fr/it.

Solo HTTP(S) absoluto sin credenciales ni controles es activable. Ejemplos de código,
destinos relativos y esquemas de ejecución permanecen sin acción. La apertura es una
acción explícita del cliente; no pasa por una tool del modelo ni escribe el journal.
La composición de navegador se mantiene detrás del Host, sin nueva arista del CLI hacia
Abstractions/Infrastructure. OAuth mantiene el launcher HTTPS-only por defecto.

En plain interactivo se emiten enlaces OSC 8 con posiciones precisas de Markdown;
el gesto de apertura depende de la terminal. La salida redirigida y `TERM=dumb` conservan
el texto original. La metadata o las secuencias de estilo no se copian al portapapeles.

## Validación

Build Windows de `OmniCore.slnx`, incluyendo proyectos multi-target: **0 errores,
0 advertencias**. Ejecuciones seriales, stdin cerrado y sin navegador/red reales:

| Batería | Resultado |
|---|---|
| Nuevos casos de enlaces (`--filter-method '*Hyperlink*'`) | 28 PASS, 0 FAIL |
| Conversación (`--filter-class '*Conversation*'`) | 148 PASS, 0 FAIL |
| Markdown (`--filter-class '*Markdown*'`) | 42 PASS, 0 FAIL |
| Cliente/plain (`--filter-class '*ClientTests'`) | 59 PASS, 0 FAIL |
| OAuth (`--filter-class '*OAuth*'`) | 244 PASS, 0 FAIL |
| Arquitectura | 62 PASS, 0 FAIL |
| TUI completa (`--filter-class '*TuiWiringTests'`) | 189 PASS, 0 FAIL |

Los filtros se solapan; sus conteos no representan casos únicos de una suite integral.
Las pruebas de clic/Enter, Unicode, native wrap/scroll, copia, arrastre y reemplazo usan
la vista y el driver reales de Terminal.Gui, con una fachada de navegador falsa que
registra el destino recibido. No abren ventanas ni consumen un provider.

La primera regresión TUI dio 188 PASS y 1 FAIL: el filtro de una prueba buscaba «Lee el
contenido», descripción reemplazada previamente por la lectura paginada de M8. La prueba
se actualizó a «página acotada». La primera comprobación de arquitectura detectó una
referencia binaria CLI→Abstractions por el parámetro opcional del constructor de Host;
se corrigió con composición en el constructor sin parámetros y se verificó la frontera.

Logs externos: `C:/Users/juanc/.codex/terminal-hyperlinks-validation-20261013/`.
`tui.log`/`results.json` conservan la pasada TUI inicial;
`intermediate-architecture.log`/`intermediate-results.json` conservan la comprobación
intermedia; `final-*.log` y `final-results.json` identifican la revisión final.

## Límites

No se ejecutó aceptación manual abriendo el navegador del usuario, ni pruebas Linux/macOS.
OSC 8 requiere soporte de la terminal; en plain la aplicación no controla el clic de ese
emulador. Esta evidencia focal y de TUI no se presenta como la suite integral de OmniCore.
