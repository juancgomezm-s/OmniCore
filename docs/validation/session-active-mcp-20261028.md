# Mascota preservada e indicador MCP de la sesión — 2026-10-28

Petición del usuario: conservar en Main sus cambios de mascota y mostrar debajo
del modo los conectores MCP que estén en uso en ese momento.

## Integración y conservación

Main ya tenía los commits de mascota `ca0f3302` y `ba2f90f1`. Se incorporaron a la
rama de M10 mediante `85135db2`, sin conflictos. Se cotejaron los blobs Git de sus
23 archivos, incluidos los PNG: son idénticos antes y después de la integración.
Las diferencias iniciales entre copias eran de LF/CRLF, no cambios perdidos.
Las animaciones, expresiones y caché acotada conservan su
[acta propia](companion-graphics/panel-animation-20261027.md).

El cambio MCP está en `15413df7efb88d7be17a0e8d912439047a9938ce`; Main avanzó
por fast-forward a ese commit. Antes se preservaron los 31 archivos pendientes
originales de OpenCode con copia binaria, hashes y patch local, y en el stash
`7bcfb11528bc198dbf35296ef951081574ad529a`, mensaje
`codex-main-before-mcp-integration-20261028`. La ref de respaldo es
`codex/backup-main-before-mcp-ba2f90f1`.
Esos originales ya estaban revisados e incorporados con correcciones en M10;
no se reaplicaron sus versiones anteriores encima del resultado revisado.

Después aparecieron nuevos cambios de proveedores en el checkout Main y trabajo
en M9. Se conservaron en curso, sin incorporarlos ni modificarlos desde esta
sesión. No se hizo push, ni se alteraron archivos del worktree de M9.

## Comportamiento y contrato

La sección SESIÓN muestra `MCP: github` directamente debajo de `act`, seguido de
los demás identificadores activos, ordenados y con sangría de dos espacios.
Las llamadas paralelas al mismo conector producen una sola entrada. Al terminar
su última llamada, desaparece; sin llamadas MCP en ejecución se muestra `MCP: —`.
La altura del resumen se adapta y permite scroll si no cabe, sin superponerse al
contenido inferior. Se retiró previamente el rótulo interno `! consumption`.

El runtime escribe `ToolCallAttribution.McpServerId` desde el propietario del
descriptor MCP, nunca desde argumentos del modelo ni adivinando por el prefijo
del nombre de herramienta. `toolcall.started` pasa a versión 5 con upcaster
identidad desde v4; los registros anteriores se leen sin inventar identidad.
`SessionSidebarSnapshot.ActiveMcpServers` es un campo opcional y aditivo.

Host proyecta exclusivamente eventos de la Session/Run seleccionados: inicio,
resultado, fallo o recuperación incierta. Al cerrar el Run o bloquearse su
recuperación no conserva actividad obsoleta. No hace discovery, escaneo de
configuración, red, escrituras de journal ni cambios de permisos para dibujarlo.
La lista representa llamadas en ejecución, no todos los servidores configurados
ni su estado de conexión.

## Validación

- Build Release de solución y publicación CLI correctos; cero errores.
- Foco Core: 82/82 casos de sidebar, runtime, codecs y mascota. Después se añadió
  el caso de compatibilidad v4; el foco MCP final pasó 12/12.
- TUI: 6/6 casos, incluidos MCP activos/finalizados en 80×25 y 130×38 y regresiones
  de animación/caché de la mascota. Se inspeccionaron ambos PNG del driver real.
- Arquitectura: 62/62. CLI conserva sus dependencias Client/Host/Protocol.
- No se invocaron proveedores de pago ni servidores MCP reales: estas pruebas
  validan runtime/proyección/UI con fixtures, no conectividad externa.

Evidencia local: `artifacts/mcp-main-integration-20261027/`, con hashes de mascota,
backup y stash, capturas, manifiestos y verificación de instalación. Logs
`%TEMP%/omni-active-mcp-*.log` y `%TEMP%/omni-mcp-package-build.log`.

## Instalación local

Instalada y activada `0.1.0+15413df7efb88d7be17a0e8d912439047a9938ce` en
`%LOCALAPPDATA%/OmniCore/cli/releases/15413df7`. SHA-256 del paquete:
`5552aa9c1ba594e215e2fb1da74ed77e3a759d0383390d8292ffc4a1eb62688d`.
Los 160 archivos publicados coinciden por SHA-256, sin ausentes ni extras.
Los scripts y el manifest coinciden, con la normalización estándar del primer CR
del shebang de `bin/omni.js` por npm. Los launchers se generaron con cmd-shim
oficial después de verificar que sus hashes y el DLL global seguían iguales al
baseline; CMD y PowerShell pasaron `--help` con exit 0.

Se conservó el paquete global de mascota `ba2f90f1`, así como las releases
anteriores y copia de los tres launchers previos dentro de `previous-launchers`.
No se reemplazaron DLL de procesos abiertos ni se cerraron ventanas del usuario.
La instancia nueva de `omni` usa el paquete validado; las instancias abiertas
conservan su imagen hasta que el usuario las cierre.

M10 global sigue abierto. Esta aceptación corresponde a este cambio y no declara
terminadas las tareas de M9 ni la aceptación manual del usuario.
