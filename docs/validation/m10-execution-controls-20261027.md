# M10 — modos, permisos, esfuerzo y razonamiento visible (2026-10-27)

## Encargo y coordinación

El usuario pidió reconciliar el trabajo de OpenCode, recuperar espacio mediante
worktrees ya fusionados y corregir el recorrido mostrado en su captura: conversación
en el home, shell rechazado con mensaje redactado y lectura fallida de una ruta
absoluta de Windows. Solicitó `/modo` con F1, `/permiso` con Ctrl+F1, comandos de
esfuerzo y visualización de razonamiento, selección de esfuerzo al elegir modelo
y un rediseño concreto del menú por Astra.

Este encargo autoriza cambiar esas superficies de Core/TUI; no modifica la
responsabilidad de OmniCoder. Astra implementó el menú, sus recursos en cuatro
idiomas y la fachada de capacidades declaradas. La raíz revisa su entrega, conserva
los arreglos de OpenCode en una copia aislada y conecta comandos, teclado,
navegación de workspace y proyección de razonamiento público. Un worker activo
a la vez. No se escriben los checkouts de M9 ni de OmniCoder.

Base integrada: main `536335a`, incluyendo el merge M10 `754559b`. Los cambios de
OpenCode permanecen en el checkout principal mientras esta revisión trabaja en
`codex/m10-keybindings`. Se preservaron en este árbol sus arreglos de cursor,
doble Esc, explicación de techo de shell, redacción y rutas/JSON, y su actualizador
en curso. Una copia no equivale a aceptación de todos sus recorridos.

## Comportamiento

| Acceso | Función |
|---|---|
| `/modo`, F1 | Elegir PLAN/ACT/ORQ, distinguiendo Run actual y Runs futuros. Aplicar usa los comandos humanos existentes. |
| `/permiso`, Ctrl+F1 | Resumen de techo de la sesión, default para sesiones nuevas, perfil, reglas y confianza/autorizaciones; edición por secciones. |
| `/esfuerzo`, `/reasoning` | Preferencia nativa declarada por la ruta del modelo; distingue Run y default, conserva revisión y valida modelo/ruta al aplicar. |
| `/think`, `/think on`, `/think off` | Mostrar/ocultar texto visible de razonamiento; preferencia local persistente, independiente del esfuerzo y de los permisos. |
| F3 | Elegir modelo y ofrecer configuración de esfuerzo/visualización. |
| `!cd <ruta>` | Abrir otro workspace con Host, selección y Session propios; conserva la conversación anterior en su workspace. |

El perfil «Autónomo/Libre» no anula un techo «Sólo observación». El menú muestra
las dos capas y conserva confirmaciones/revisiones. Abrirlo no amplía permisos.
Esc cancela el editor; el acceso desde F4 vuelve a su categoría y el acceso
directo vuelve al chat. Las aprobaciones pendientes conservan prioridad.

`!cd` es navegación humana explícita de la aplicación. Admite rutas con espacios
y comillas, rutas relativas, `~` y `/d` en Windows. No interpreta una cadena de
shell como navegación, ni cambia silenciosamente la identidad de una conversación.
Rechaza cambiar mientras haya operación/interacción o adjuntos pendientes y
conserva el borrador ante una carpeta inexistente. El modelo continúa sujeto a su
workspace y política; una petición suya de shell no reconfigura la aplicación.

## Razonamiento y evidencia

El texto procede exclusivamente de `visibleContent` del artifact verificado de
`ModelStepCompleted`, con tamaño/esquema acotados y visibilidad Full/Summarized.
No se serializan bloques Omitted, firmas, referencias ni estado opaco del proveedor.
El wire añade `reasoningText` al evento existente; Client lo proyecta como rol de
razonamiento. La TUI y su búsqueda respetan el toggle, manteniendo la respuesta final.
La exposición ocurre al completar el paso; no se acredita streaming de ese texto.

La capacidad para configurar esfuerzo es independiente de recibir texto de
razonamiento: una ruta compatible puede devolver `reasoning_content` sin declarar
parámetros configurables. No se inventan niveles por nombre/tamaño del modelo.
El default de esfuerzo conserva el mecanismo durable existente; la preferencia de
visualización usa un escalar local `true/false` junto a los demás ajustes de
presentación, con sustitución atómica y fallo de guardado visible.

Las capturas exportadas por el driver se rasterizan conservando RGB. Se descartó
una primera exportación sin color (blanco sobre blanco); no se usa como prueba
visual. La revisión de capturas de fixture no sustituye aceptación humana con
su terminal y proveedor reales.

## Espacio y actualizador

El inventario encontró sólo tres checkouts Core registrados: main, M9 y esta
sesión. Todos tienen trabajo vigente; no se elimina ninguno. Los restos antiguos
de Core sin metadata son casi vacíos. M9 ocupa aproximadamente 9,7 GiB, incluyendo
8,4 GiB de evidencias/SDK; quedan fuera de limpieza por estar activos. La lectura
inicial de C: mostró aproximadamente 47 GiB libres. No se eliminan datos ajenos
a Core ni conversaciones, modelos, credenciales o artefactos de M9.

La revisión del checker corrigió el falso rate limit: una respuesta HTTP 200
con X-RateLimit-Reset es normal y no equivale a rechazo. Además, se acotan reset
y delta para evitar desbordamientos. El acta anterior que decía que `omni update
check` sólo muestra uso sin red era incorrecta: la consulta es una acción explícita
con red. La aceptación real de apply/rollback/publicador mantiene su evidencia propia.

## Validación e instalación

La entrega de Astra pasó 17/17 casos de controles, persistencia y capacidades,
incluido F3 → esfuerzo declarado alto → ocultar razonamiento, sin cambiar el default
del usuario. La raíz revisó las capturas reales RGB estrechas/amplias y corrigió
además que el cursor emulado borraba visualmente la letra bajo el caret.

La regresión de raíz pasó 133/133 casos de protocolo, teclado/paleta, redacción,
lectura de rutas, preferencias, seguridad, esfuerzo y apertura real de otro Host.
El foco TUI amplio ejecutó 76 casos: 75 pasaron y uno esperaba el regreso directo
al chat al cambiar modelo. Se adaptó esa expectativa al nuevo prompt y pasó la
revalidación de tres casos (cambio de modelo ocho veces, cursor intermedio y cursor
al final de línea). Todos los casos nuevos de navegación, comandos y texto de
razonamiento pasaron. Arquitectura final: 62/62, después de encapsular en Host la
creación del almacén del actualizador para conservar las dependencias permitidas
Client/Host/Protocol de la CLI.

Luna revisó el actualizador preservado y corrigió tres defectos: reintento ante
operación huérfana, archivo cambiado durante la espera y prefijo npm implícito.
Pending/Running huérfano queda Unknown, sin aplicar ni revertir; no se acredita una
recuperación automática. Cada operación tiene identidad y backup propios, evitando
reutilizar un backup distinto con la misma versión. El helper vuelve a comprobar
SHA-256/tamaño de los mismos bytes cuya identidad npm comprobó el downloader, y
revalida estado/identidad antes de activar. npm usa prefijo explícito; Windows
invoca npm-cli.js mediante argv de Node, sin interpolación de shell. Esto valida el
destino, no una identidad del publicador ni propiedad genérica de cualquier prefijo.

El foco .NET del actualizador pasó 58/58 casos. Las pruebas Node de helper pasaron
4 casos, incluida activación con npm falso y prefijo con espacios, % y &, archivo
modificado y operación obsoleta. Un caso de symlink está omitido expresamente por
falta de permiso de creación en este Windows. No se ejecutó npm global real desde
el helper ni se publicó una release. El foco final adicional de CLI/búsqueda pasó
19/19 casos. `npm test` pasó 30 casos, con la misma omisión explícita de symlink
(31 descubiertos, cero fallos); incluye launcher, instalación y sync-main, y mantiene
las pruebas del helper fuera del paquete distribuido. Los builds Release terminaron
sin advertencias ni errores. Logs locales
`%TEMP%/omni-controls-*.log`; capturas en `artifacts/m10-controls-20261027/frames`.

Estas son validaciones focales y fixtures; no una suite global, una aceptación
manual del proveedor/terminal o una prueba real de apply/rollback. No se acredita
una suite por invocaciones con cero casos.

Instalación final verificada en ZephyrusG14:
`0.1.0+b83d7c05a4b6173086a42cdb93db6e33c1521f0c`, destino
`%LOCALAPPDATA%/OmniCore/cli/releases/b83d7c05`. SHA-256 del tgz:
`62C209965F44BF073AAB15883AAFFB7CB378ED1A2040CF815F72E233663D2FDA`.
Los 160 archivos publicados de dist coinciden por SHA-256, sin ausentes ni extras.
Los scripts/manifest coinciden, salvo la normalización estándar del CR del shebang
de bin/omni.js que realiza npm; el resto de sus bytes coincide.

Se incorporaron exclusivamente commits terminados de main en esta copia aislada:
`30970306` (importación confinada y normalización de facts heredados), `04cdb88e`
(compañeros) y `f70bbebb`/merge `da71f1a4` (nivel de permisos y recuperación del
rechazo nativo). No se trasladan los cambios sin commit ni se escribe el checkout
compartido. Después de combinar estos bloques pasaron 148/148 casos Core,
26/26 de controles TUI y 62/62 de arquitectura; build Release sin advertencias ni
errores. Logs `%TEMP%/omni-controls-synced-*.log`.

Una instalación concurrente de M9 cambió los launchers durante el empaquetado;
el control de hashes detuvo el reemplazo. Se comprobó que su versión `da71f1a4`
ya era ancestro del paquete revisado y se tomó un baseline nuevo antes de activar.
Se conserva ese paquete global, con copia de sus tres launchers en
`previous-launchers` dentro del nuevo destino. La instalación intermedia `5b7f66de`
también permanece disponible. No se sustituyen DLL de procesos abiertos ni se
termina una ventana del usuario.

Los launchers públicos finales se generaron con cmd-shim oficial. CMD y PowerShell
pasan `--help`; ProductVersion del DLL coincide con el commit construido. Manifest
del paquete, hashes por archivo e `installation-verification.json` están guardados
en la carpeta de release. La consulta real por el launcher final pasó (exit 0):
«No hay releases publicados en este canal», stable. `update status` pasó sin
operaciones registradas. Esto comprueba el checker y el falso rate limit corregido;
no acredita aplicar una release ni aceptación con un modelo real.

La limpieza de dist/npm y del staging propio habría eliminado 748.474.150 bytes
(aproximadamente 714 MiB), pero la revisión automática de la herramienta rechazó
la operación por política, incluida la variante con ruta literal. No dio otro
motivo. No se ejecutó el borrado ni se intentó otra vía. Esos archivos permanecen
junto con las evidencias. No se eliminó ningún checkout activo; el inventario no
encontró otro worktree Core registrado que pudiera podarse sin perder trabajo.

Main conserva sus cambios de OpenCode; las correcciones están comprometidas en
esta rama aislada y en la instalación local, sin nuevo merge/push sobre el trabajo
compartido de M9. M10 global y la recuperación/aceptación real del actualizador
siguen abiertos.
