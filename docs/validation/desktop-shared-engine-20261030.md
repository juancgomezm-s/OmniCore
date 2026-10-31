# Núcleo de escritorio compartido — 30 de octubre de 2026

Instalado OmniCore `0.1.0-desktopshared.20261030` y Desktop Agent Rust `0.4.0`.
Son dos aplicaciones independientes que enlazan el mismo núcleo Rust 0.4.0.
La coincidencia de fuentes se verifica por SHA-256:
`259bb05834908bd1219e397a36ea23629532785b1a0c18f0aebec832a3575851`.

## Distribución y autoridad

Fuente canónica: `native/desktop-engine`. OmniCore depende de ella por Cargo y
distribuye únicamente su ejecutable nativo fijo, verificado por Host. Claude usa
una instantánea autocontenida de esa misma crate, fijada por un manifiesto de
hashes y actualizada mediante `scripts/sync_shared_engine.py`. La distribución
de Claude incluye su propio instalador y ejecutable con runtime C estático.
Ningún producto necesita la instalación, proceso o carpetas del otro al ejecutar.

OmniCore conserva la herramienta `desktop.control` detrás de la skill BuiltIn
exacta, carga/descarga automática, AuthorizedToolIntent, Ask/Deny, riesgo y techo
de modelo, CAS sensible, aislamiento por sesión y cancelación por botón/Escape.
Sus nuevas mutaciones `invoke` y `set_value` son NonIdempotent. No se agregó un
servidor MCP ni se modificó OmniCoder.

Claude conserva sus once herramientas MCP stdio y su indicador de bordes. El
instalador quedó en una carpeta independiente sin registrar un servidor nuevo:
los archivos de configuración de Claude conservaron exactamente sus hashes.
Se puede configurar luego mediante sus accesos de instalación o manual.

## Capacidades incorporadas

- WGC predeterminado y PrintWindow explícito; sin fallback implícito de escritorio.
- PNG a escala 0.1–1, con origen/dimensiones físicos retenidos para convertir clics.
- Lectura de selección, UIA InvokePattern y ValuePattern. Se rechazan contraseñas,
  estados de contraseña desconocidos, controles de sólo lectura y referencias viejas.
- Rueda horizontal/vertical, alias numéricos y puntuación, F1–F24 y botones l/r/m.
  `delta` antiguo conserva Win32 positivo arriba; `scroll_x/scroll_y` positivo
  significa derecha/abajo. Escape y atajos de sistema conservan sus restricciones.
- Observaciones con TTL 30 s, identidad por creación del proceso y geometría;
  invalidación tras actuar. Captura y UIA en workers terminables a los ocho segundos.
- La opción explícita visible `screen_gdi` se conserva sólo en el adaptador MCP.
  OmniCore la rechaza; captura únicamente su ventana objetivo.

## Pruebas y resultados

- Núcleo: 13 tests unitarios, incluyendo reducción RGBA y coordenadas; clippy sin
  advertencias. Adaptador Claude: siete tests de protocolo/registro y formato correcto.
- Solución OmniCore completa compilada sin advertencias ni errores. 64 pruebas
  focalizadas de Host/skills/permisos/cancelación, una conversación automática y
  62 de arquitectura: 127/127 aprobadas.
- Regresión nativa de OmniCore: 15/15, incluido timeout de PrintWindow y continuidad.
- Indicador: botón, manejador de Escape, alfa, foco, exclusión de captura, bloqueo
  de objetivos prohibidos, cierre por EOF y cancelación de captura bloqueada.
- Claude MCP: 12/12 comprobaciones GUI sobre su ejecutable portátil final. Las 17
  comparaciones funcionales contra las evidencias previamente recogidas del original
  aprobaron también sobre ese ejecutable. No se ejecutó el original en esta tarea.
- 24 capturas WGC consecutivas entre ambos adaptadores, escalas 1, .8, .5 y .1.
- WPF propio: siete verificaciones de selección, redacción de contraseña, rechazo de
  escritura sensible, SetValue, caducidad tras mutación, Invoke y rechazo de captura
  visible en OmniCore.
- Host real con proveedor HTTP local programado: siete herramientas autorizadas,
  WGC reducido, SetValue e Invoke comprobados; cancelar interrumpe la espera del
  modelo en 469 ms, descarga la selección automática y retira el indicador, sin
  reintentos ni llamadas a un proveedor remoto.
- Launchers PowerShell/CMD y hashes de OmniCore instalados comprobados. El doctor
  del Desktop Agent instalado confirmó MCP 2025-11-25, versión 0.4.0 y once herramientas.

Durante las primeras pruebas hubo dos caídas del worker `desktop-agent.exe`:
Windows registró `GraphicsCapture.dll_unloaded`, excepción `0xc0000005`.
La DLL del sistema queda ahora fijada durante la vida del worker para que callbacks
retrasados no ejecuten código descargado. Se repitieron capturas y entrada tras la
corrección, sin nuevos eventos de caída. Véase
[GetModuleHandleExW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandleexw).

La aceptación GUI cubre fixtures WinForms y WPF en este equipo; no acredita todas
las aplicaciones, DPI, monitores, contenido protegido ni ventanas elevadas. No se
ejecutó toda la suite global. La captura y UIA siguen siendo observaciones secuenciales.
