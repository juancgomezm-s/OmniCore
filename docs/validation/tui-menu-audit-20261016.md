# Auditoría de menús, herramientas y preferencias — 2026-10-16

Rama `codex/tui-openrouter-usability`, sobre `6cbd8e7`. Alcance: organización,
superficies visibles y navegación de la TUI de producción; se conserva su estilo
integrado. La evidencia usa drivers y Host reales con almacenamiento temporal y
HTTP de prueba; no acredita una cuenta real de OpenRouter.

## Inventario revisado

| Categoría | Destinos y responsabilidad |
|---|---|
| General y apariencia | Idioma; panel y widgets; animaciones. |
| Cuentas y conexiones | ChatGPT y Claude por suscripción; formulario directo por proveedor API. |
| Motores locales | Lista, alta, edición, estado/control y eliminación confirmada del motor exacto. |
| Modelos y perfiles | Selector de modelo, rutas y límites de gasto, catálogo y políticas por modelo. |
| Herramientas y seguridad | Catálogo completo de herramientas, sustituciones de usuario, reglas, autorizaciones y confianza. |
| Contexto y sesiones | Inspección del contexto, consumo y memoria de sesión. |
| Extensiones y habilidades | Extensiones, habilidades, automatizaciones, MCP, alias y catálogo de comandos de trabajo. |
| Diagnóstico y mantenimiento | Diagnóstico, verificación del registro, limpieza confirmada y ayuda. |

Las herramientas conservan sus IDs y fuentes: no se esconden herramientas distintas
para aparentar ausencia de duplicados. El catálogo conserva todas las filas del Host,
búsqueda y detalle navegable; consultar no ejecuta ni habilita herramientas.

## Defectos corregidos

- Política única para autocompletado, catálogo y ayuda TUI: las opciones que ya son
  propiedad de Preferencias no se vuelven a anunciar como comandos. También se
  filtran sus traducciones y los alias de usuario que apunten a esas opciones.
  La CLI, los comandos escritos de versiones anteriores y los contratos permanecen
  compatibles. El demostrador de Markdown deja de ofrecerse en el autocompletado.
- Se elimina el segundo selector «Conexiones API» del menú: cada proveedor abre
  directamente su propio formulario. Estados y fechas del Host siguen disponibles;
  registrar modelos, cancelar o desconectar devuelve al proveedor exacto y conserva
  los avisos, sin reutilizar metadata anterior a la operación.
- Las preferencias de sustitución de herramientas y los alias salen de Modelos y
  pasan a sus categorías. Se editan por filas, con validación y guardado explícito;
  guardar también incluye la fila que se está editando. Volver descarta el borrador.
- Se añaden entradas claras para automatizaciones y MCP. Sus consultas son de sólo
  lectura, traducidas y con página de error recuperable si no se pueden leer.
- La ayuda y las instrucciones internas dejan de recomendar comandos retirados del
  menú de trabajo. Los nombres de navegación distinguen catálogo, rutas/gasto y
  permisos/confianza. La búsqueda encuentra OpenRouter, MCP, habilidades y alias.
- Se evita repetir la misma decisión de permisos y la misma descripción original en
  el detalle de una herramienta.
- Animaciones deja de superponer Cerrar sobre sus controles. Los formularios largos
  se desplazan; panel y animaciones regresan a General con Esc o Volver. Los errores
  de lectura de preferencias no cierran la TUI ni descartan el mensaje del chat.

## Validación

El recorrido de aceptación automatizado cubre las ocho categorías y sus entradas,
en es/en/fr/it a 80×25 y 120×40. Abre destinos con clics por coordenadas a través del
enrutador de mouse de la aplicación y vuelve con teclado. Comprueba identidad del
destino, acciones distintas, retorno a la categoría, preservación del mensaje y
cero operaciones de conexión, catálogo, créditos o escritura del registro al navegar.

Se mantienen las pruebas de credencial oculta, entrega al Host, borrado del campo,
cancelación y rechazo de resultados tardíos. La prueba de las cinco acciones de
proveedor conserva HTTP aislado, selección exacta de proveedor, fechas, catálogo,
confirmación y borrado de la credencial; sólo cambia el recorrido de entrada.

Resultados locales:

- Compilación de la solución: 0 errores y 0 advertencias.
- Regresión amplia de TUI, presentación, alias, recursos, catálogo y OpenRouter:
  **388/388 aprobadas, 0 fallos, 0 omisiones** (5m 24s).
- Arquitectura: **62/62 aprobadas, 0 fallos, 0 omisiones**.
- Después de eliminar una instrucción duplicada en el editor por filas y ampliar
  la ayuda de Animaciones a dos líneas: nueva compilación y **30/30 comprobaciones
  focales aprobadas**, incluidos los ocho recorridos completos y los editores.
- Publicación Release independiente de la instalación global.

Logs externos: `tui-menu-audit-final-regression.log`,
`tui-menu-audit-final-architecture.log`, `tui-menu-audit-visual-build.log` y
`tui-menu-audit-visual-regression.log`, en
`C:/Users/juanc/.codex/omnicore-linux-20261014`. Las capturas de fixture proceden de
las celdas reales del driver y se guardan en `tui-menu-audit-final-captures` y
`tui-menu-audit-visual-captures`; se revisaron menús, editores y Animaciones.

## Límites de aceptación

La confirmación de entrada nativa de mouse en la terminal del usuario y OR5 con
cuenta real siguen pendientes. Esta auditoría no cierra M8 ni acredita CI remoto o
Linux. No se inició WSL. La instalación global permanece intacta. La ventana
anterior ya estaba cerrada al comprobar los procesos; la revisión se muestra por
separado como «OmniCore - Menú revisado», en español y sin heredar NO_COLOR.
