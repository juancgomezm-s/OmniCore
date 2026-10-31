# OmniDesktop Control para OmniCore

Adaptador de OmniCore para Windows x64 sobre `../desktop-engine`. Captura con WGC o GDI/PrintWindow,
accesibilidad mediante Microsoft UI Automation y entrada con SendInput. No utiliza
el ejecutable original, Python ni un servidor HTTP/MCP.

Construir primero con Rust estable y MSVC/Windows SDK:

```powershell
./native/desktop-control/build.ps1
dotnet build OmniCore.slnx
```

El script ejecuta los tests, compila con Cargo.lock y genera `desktop.sha256`.
Host integra ese hash como recurso del ensamblado; CLI copia únicamente el
ejecutable fijo a `native/omnicore-desktop.exe` y los archivos de la skill.
Después puede usarse `dotnet publish` o el empaquetado npm habitual. Un build sin
el ejecutable verificado no puede iniciar el componente. Requiere sesión Windows
interactiva; no es un servicio ni necesita puerto o token local.

Prueba de integración de desarrollo (requiere Python y Pillow únicamente para
el verificador): `python native/desktop-control/tests/verify.py <directorio-resultados>`.
Abre y opera exclusivamente una ventana WinForms propia y la cierra al terminar.
El componente distribuido y el Host no requieren Python.

El protocolo interno es JSON por líneas, peticiones de 64 KiB como máximo. El
proceso se crea sólo después de la autorización y dura durante la composición
del turno. Captura y UIA se ejecutan en hijos separados, terminados a los ocho
segundos. PNG: 40 megapíxeles/8 MiB; UIA: 500 elementos, profundidad 16, texto
acotado y contraseñas redactadas. La entrada exige observación reciente, identidad,
geometría y foco. No se reintenta una operación incierta. Al terminar se eliminan
las capturas temporales; Host conserva la imagen necesaria como artefacto sensible.

En OmniCore basta pedir, por ejemplo, «prueba la ventana de mi aplicación» o
«captura la pantalla». La skill se admite automáticamente para esa tarea y se
descarga al terminar su turno; un permiso pendiente conserva la activación para
reanudar. La herramienta no está en el catálogo inactivo ni se hereda a Lanes
delegadas. Las cargas manuales `/skills load desktop-control BuiltIn` siguen
disponibles como selección persistente; `/skills release desktop-control BuiltIn`
las retira. La activación automática conserva permisos, presupuestos y supresiones.

Mientras el helper está activo muestra una cortina morada al 6,3 % de opacidad,
con bordes suavizados, que deja pasar el ratón y no toma el foco. El aviso
superior indica la ventana objetivo y ofrece «Cancelar (Esc)». Cancelar detiene
también la espera del modelo en ese turno y descarga su selección automática;
una selección manual permanece disponible para otro turno. Las entradas propias
retenidas se liberan antes de avisar al Host. Escape queda reservado al usuario.
La cortina cubre el escritorio virtual; no aplica desenfoque al contenido.

Las imágenes son exclusivamente de una ventana de aplicación seleccionada:
escritorio, barra de tareas y ventanas del propio helper se rechazan. PrintWindow
no incluye la cortina ni el aviso; ambos también solicitan exclusión de captura
de Windows. El indicador desaparece al cancelar o cerrar el helper. La prueba
`python native/desktop-control/tests/verify_indicator.py <directorio-resultados>`
comprueba el indicador y su cancelación sin enviar Escape a otras aplicaciones.

Límites: PrintWindow puede devolver negro o contenido incompleto; captura y UIA
no son atómicos. No detectamos todos los cambios de contenido sin cambio de
geometría. No se eluden UIPI, pantallas protegidas ni reglas de foco de Windows.
La API de escritorio opera con la autoridad del usuario de Windows, fuera del
sandbox de procesos de proyectos. Sólo el backend fijo de Core tiene esa ruta;
la skill no concede permisos. Véase ADR-0064.


Desde 0.4.0, el motor nativo reside en `native/desktop-engine`. Este ejecutable
conserva su protocolo NDJSON, pin SHA-256, integración por skills y cancelación.
La biblioteca se compila dentro del ejecutable distribuido; no requiere Claude,
MCP ni rutas de fuentes en el equipo de destino. WGC es predeterminado y PrintWindow
se elige explícitamente. Se incorporan `image_scale`, `invoke`, `set_value`, texto
seleccionado, rueda horizontal y más alias de teclado. Véase el README del núcleo.
