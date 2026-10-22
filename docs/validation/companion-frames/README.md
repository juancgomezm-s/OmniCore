# Frames reales de la TUI

Exportados desde `Application.Driver.Contents` de Terminal.Gui, con la TUI,
Host, journal y persistencia de producción. Las fixtures usan perfiles temporales
y semilla controlada para obtener una especie por prueba. Las capturas no proceden
de un modelo generativo ni de una maqueta.

Cada escena conserva el SVG y TXT originales y su PNG rasterizado con
`tools/render-tui-frames.cjs`. Se revisaron visualmente las seis siluetas.

| Especie | Panel lateral | Detalle | Caricia |
|---|---|---|---|
| Buey | [PNG](settings-companion-ox-sidebar-120x40.png) | [PNG](settings-companion-ox-idle-120x40.png) | [PNG](settings-companion-ox-pet-120x40.png) |
| Zorro | [PNG](settings-companion-fox-sidebar-120x40.png) | [PNG](settings-companion-fox-idle-120x40.png) | [PNG](settings-companion-fox-pet-120x40.png) |
| Búho | [PNG](settings-companion-owl-sidebar-120x40.png) | [PNG](settings-companion-owl-idle-120x40.png) | [PNG](settings-companion-owl-pet-120x40.png) |
| Castor | [PNG](settings-companion-beaver-sidebar-120x40.png) | [PNG](settings-companion-beaver-idle-120x40.png) | [PNG](settings-companion-beaver-pet-120x40.png) |
| Gato | [PNG](settings-companion-cat-sidebar-120x40.png) | [PNG](settings-companion-cat-idle-120x40.png) | [PNG](settings-companion-cat-pet-120x40.png) |
| Capibara | [PNG](settings-companion-capybara-sidebar-120x40.png) | [PNG](settings-companion-capybara-idle-120x40.png) | [PNG](settings-companion-capybara-pet-120x40.png) |

[Huevo descubierto, 100×30](settings-companion-egg-100x30.png).

[Panel restaurado al ocultar la mascota, 120×40](settings-companion-hidden-120x40.png).
El control **Ocultar** está junto a **Acariciar** y libera la región completa.

La suite verifica Enter y ratón nativos, reducción de movimiento, redimensionado
a 80×25 y detalle textual a 80×12, sin mutar la conversación ni iniciar inferencia.
La inspección humana en otros emuladores de terminal queda fuera de estas capturas.
