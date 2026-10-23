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
[Huevo durante la caricia, 100×30](settings-companion-egg-pet-100x30.png).
La silueta tiene punta estrecha y base redondeada, con sombreado suave de cáscara;
el corazón de cinco píxeles de ancho asciende junto al huevo.

El detalle usa una tarjeta centrada que ajusta su ancho al resumen y los controles,
con 20 filas para el dibujo completo. Al reducir la terminal adapta los controles
a una tarjeta de 11 filas: [vista compacta, 80×20](settings-companion-compact-80x20.png).

[Panel restaurado al ocultar la mascota, 120×40](settings-companion-hidden-120x40.png).
El control **Ocultar** está junto a **Acariciar** y libera la región completa.

Escapadas con el panel lateral cerrado: [Zorro](settings-companion-fox-roaming-120x40.png),
[huevo](settings-companion-egg-roaming-120x40.png). El control **Ocultar mascota**
permanece fijo en el encabezado. La mascota se superpone al texto sin reservar
filas, con la silueta opaca y eventos de entrada dirigidos al chat subyacente.

Más travesuras entre renglones: [zigzag](settings-companion-fox-weave-120x40.png),
[voltereta](settings-companion-fox-roll-120x40.png),
[baile](settings-companion-fox-dance-120x40.png) y
[escondite](settings-companion-fox-hide-120x40.png). Cada escena procede del mismo
recorrido real, y la prueba compara las celdas del texto antes, durante y después.

[Persecución de letras al escribir, 120×40](settings-companion-fox-typing-120x40.png):
la `s` que salta junto al zorro es una copia visual; el borrador original permanece
intacto debajo. La captura se obtiene escribiendo con teclas nativas y conserva
el control **Ocultar mascota** en el encabezado.

La suite verifica Enter y ratón nativos, reducción de movimiento, redimensionado
a 80×25 y detalle textual a 80×12, sin mutar la conversación ni iniciar inferencia.
La inspección humana en otros emuladores de terminal queda fuera de estas capturas.
