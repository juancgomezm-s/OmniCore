# Mascota, panel compacto y ClearType — 2026-10-28

Integración local de main. No se publica el historial pendiente a GitHub.

## Comportamiento

- `/compacto` ofrece una vista previa reversible del espaciado interno del panel lateral; `/compacto guardar` conserva la elección y `/compacto cancelar` restaura la anterior. La densidad inicial sigue siendo normal. El chat principal, su compositor y el tamaño del sprite conservan su geometría.
- El mini chat de la mascota y sus ajustes usan controles de una fila. Se quitan separadores vacíos entre mensajes, conservando los párrafos escritos dentro de cada mensaje. Abrirlo con la barra lateral cerrada ya no fuerza a abrir esa barra ni modifica la geometría del chat.
- `CompanionRowButton` evita que Terminal.Gui reserve la sombra inferior de un botón de una sola fila, que dejaba invisible su texto. La corrección se limita a los controles de la mascota.
- Al ejecutar `omni` o `omni tui` interactivamente en Windows, el lanzador npm busca `wt.exe` en el alias local de Windows Terminal y crea su propio fragmento `Fragments/OmniCore/omnicore.json`. Este perfil utiliza `antialiasingMode: cleartype`. Se abre una instancia nueva, como requiere [Microsoft](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-advanced#text-antialiasing).
- El perfil invoca Node y OmniCore directamente, sin shell intermedio; conserva el directorio de trabajo y utiliza `--current-terminal` para evitar recursión. No modifica `settings.json`, el perfil predeterminado ni otros perfiles. Los [fragmentos de Windows Terminal](https://learn.microsoft.com/en-us/windows/terminal/json-fragment-extensions) permiten esta configuración por aplicación.
- `omni --current-terminal` y `OMNICORE_CURRENT_TERMINAL=1` conservan el terminal actual. Los comandos no interactivos, argumentos adicionales, redirecciones y conexiones SSH/tmux tampoco abren otra ventana. La ejecución directa del ensamblado .NET conserva su terminal.
- Si no existe Windows Terminal, no puede escribirse el fragmento o falla su proceso de apertura, se ejecuta OmniCore en el terminal actual. Un fragmento editado por el usuario se conserva y también se usa esa alternativa. Las rutas de trabajo con `;` usan el terminal actual para evitar que Windows Terminal las interprete como varios comandos.
- Este ajuste suaviza texto. No cambia la detección de Sixel, la evolución de la mascota ni los modelos del mini chat.

## Verificación

Se compila una copia de main con únicamente los archivos de esta integración, para excluir trabajo simultáneo de otras funciones. Las pruebas nativas usan el ciclo de dibujo de Terminal.Gui a 120×40 y 80×25, comprueban que «Enviar» sea visible, conservan borrador y párrafos, restauran el foco y verifican la geometría del chat al cambiar la densidad.

Los tests del lanzador cubren elegibilidad interactiva, redirección y SSH, perfil aislado, rutas con espacios, actualizaciones propias, conservación de personalizaciones, ausencia de Windows Terminal, fallo de apertura y prevención de recursión. Las capturas y registros locales se guardan bajo `TestResults/companion-combined-*`.

Resultados: compilación sin errores ni advertencias; suite npm con 35 aprobadas y una omitida por falta de creación de enlaces simbólicos. La regresión de mascotas/densidad ejecutó 148 casos: 145 aprobados, dos no ejecutables y una prueba gráfica que asumía la apertura automática del lateral. Se corrigió su preparación para abrirlo explícitamente, coherente con la conservación de la geometría del chat, y los seis casos relacionados pasaron (tristeza gráfica, cambio de tamaño, controles compactos y densidad a dos resoluciones). Los scripts de perfil de prueba pasan tanto en Windows PowerShell 5.1 como en PowerShell 7.

ClearType es una preferencia del renderizador de Windows Terminal; no hay una API de Terminal.Gui que verifique el resultado subpíxel de la pantalla. La comprobación consiste en el perfil aplicado y una sesión real abierta con ese perfil, sin atribuir mejoras de velocidad al suavizado.

La apertura real se comprobó con Windows Terminal 1.24.12741.0. Su alias `wt.exe` requiere `lstatSync`: `existsSync` seguía el reparse point y devolvía falso. Se añadió una regresión para este comportamiento; los 11 casos del lanzador y perfil pasan. Si Terminal ya estaba abierto al registrar el fragmento por primera vez, puede ser necesario cerrarlo y volver a abrirlo para cargar el perfil. El comando explícito de la nueva pestaña garantiza que OmniCore arranque incluso antes de esa recarga, con el suavizado del perfil que Terminal tenga cargado.
