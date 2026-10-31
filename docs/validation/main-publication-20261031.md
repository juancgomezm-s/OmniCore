# Consolidación, publicación e instalación de OmniCore

Encargo del usuario: comprobar todo el avance, desktop incluido, integrar en Main,
actualizar documentación funcional, publicar, aceptar CI de Main, actualizar la
instalación y podar worktrees/ramas remotas ya fusionados. Se conserva la mascota,
Consolas 16 px lógicos/ClearType, panel compacto y arranque en la terminal actual.
Fecha local: 30 de octubre; UTC: 31 de octubre de 2026.

## Integración efectiva

- Main ya contenía M9 Core, cierre funcional de M10 y sus recibos agentic:
  Core 11/11, Pi 10/11. Son mediciones de la imagen documentada, no una nueva
  ejecución de modelos en este corte. No se consume una API ni la GPU.
- `d0122c6c` y `a1dcebe6` incorporaron OmniDesktop Control 0.4.1: motor Rust
  compartido, captura WGC/PrintWindow, UIA, entrada, skill automática e indicador
  cancelable. Su distribución MCP independiente conserva su propia aplicación.
- Se recupera el avance aún no publicado del checkout principal, base `04d226b9`:
  elección explícita de conversación, varias Sessions por carpeta, inicializador
  local de agents.md y comandos/menciones humanas de skills. Se revisan sus tres
  cruces con Main: atributos de licencias, recursos de idiomas y texto humano
  original usado por desktop/skills. Se conservan ambos recorridos y el M10 vigente.
- Snapshot de recuperación `4a2478dfaf159c83f00bfa33f97861980b5fe438` y bundle
  independiente preservan el estado original. La integración se construye en un
  checkout aislado; el índice y archivos del principal no se cambian durante ella.
- README actualizado desde el uso: iniciar/retomar, modelos, modo/permisos/esfuerzo,
  archivos/validación/undo, agentes, memoria, skills/MCP, desktop y actualizaciones.
  Se corrige el arranque por defecto para reflejar la terminal actual.

## Validación local de la combinación

| Comprobación | Resultado |
|---|---|
| Solución Release | 0 advertencias, 0 errores |
| Sesiones/inicialización/skills + desktop/MCP + regresión M10 | 392 casos: 391 PASS, 0 FAIL, 1 SKIP |
| Arquitectura | 62 PASS, 0 FAIL, 0 SKIP |
| SDK de extensiones | 44 PASS, 0 FAIL |
| Paquete npm, lanzador y updater | 67 PASS, 0 FAIL, 1 SKIP |
| Métricas/comparación/oráculos Python offline | 47 PASS, 0 FAIL |

La primera pasada focal encontró el SDK de extensiones sin compilar; se conserva
ese fallo y se repite después de preparar el SDK, como exige el recorrido CI.
La omisión de symlink conserva la condición real de la máquina, sin cambiar sus
permisos. La imagen desktop local se verifica por pin y fuentes idénticas a Main;
CI/release recompilan Rust. Estas pruebas no certifican aplicaciones/DPI arbitrarios
ni una inferencia comercial nueva. El test de selección de sesiones usa el driver
real con fixtures; no se atribuye aceptación visual humana a ese resultado.

## Corrección detectada por la CI publicada

El primer smoke Linux llegó a la TUI self-contained, pero `/exit` volvió a abrir
el selector después de cancelarlo. El guard de selección bloqueaba también la
acción local de salida. Se permite exclusivamente `app.exit` y su texto normalizado
`/exit` (incluido `/salir`) sin conversación; los mensajes y las otras acciones
siguen requiriendo elección. El smoke original no se modifica ni se rebaja.
Dos casos nuevos verifican salida sin modelo, cambios de Session ni eventos nuevos.
El foco conjunto de apertura/salida/densidad pasa 20/20, sin fallos ni omisiones,
y el build Release queda sin advertencias/errores. La CI vuelve a comprobar los
paquetes self-contained y AOT desde el árbol corregido.

La pasada completa Linux posterior ejecutó 6.302 casos y detectó una aserción
cerrada de la paleta francesa: esperaba exactamente una coincidencia para
`model.select`. El contrato de búsqueda es difuso y el catálogo ampliado puede
producir otras filas. Se comprueba que la primera/seleccionada sea `model.select`,
que conserve F3 y que Enter use el selector existente, con borrador intacto y cero
comandos enviados. No se filtran coincidencias ni se cambia la búsqueda de producto.
La regresión local de paleta pasa 19/19, sin fallos ni omisiones.

La instalación npm Windows posterior alcanzó el límite de 15 minutos tras compilar
el componente Rust y publicar el CLI, sin registrar un test fallido. Este job
ejercita dos instalaciones desde fuentes; se amplía sólo su límite Windows a 30
minutos. Linux conserva 15 minutos y todos los pasos, smoke y comprobaciones
siguen habilitados. La CI final debe aceptar también ese recorrido completo.

## CI, release, instalación y poda

El resultado final de CI, SHA de Main, versión preview, hashes de assets e
instalación, controles visuales y lista exacta de ramas/worktrees podados se
conservan en el recibo de publicación. Sólo se instala tras CI de Main aprobada;
no se usa la CI de una revisión anterior para acreditar esta combinación.

La poda exige HEAD contenido en Main y ausencia de trabajo sin fusionar. Conserva
el checkout principal y respalda evidencia necesaria antes de retirar copias.
Las ramas remotas se eliminan con el SHA observado como lease, excluyendo Main;
una rama que avance durante el análisis se vuelve a revisar. No se elimina una
rama por semejanza de nombre ni sólo porque su PR esté cerrado.

Evidencia durable: `C:/Users/juanc/.codex/artifacts/omnicore-publication-20261031`.
[CI de Main](https://github.com/juancgomezm-s/OmniCore/actions/workflows/ci.yml?query=branch%3Amain)
y [Releases](https://github.com/juancgomezm-s/OmniCore/releases) conservan las
verificaciones y la distribución pública. No se copian credenciales al recibo.
