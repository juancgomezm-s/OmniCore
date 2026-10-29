# Vista compacta reversible y perfil ClearType — 2026-10-28

**Revisión posterior del usuario:** descartó la compactación del chat y composer;
aceptó únicamente la del panel lateral y pidió una muestra separada de ClearType.
Este documento conserva la evidencia del primer mock; el estado vigente está en
[panel compacto y muestra ClearType](m10-sidebar-cleartype-preview-20261028.md).

Petición: mostrar más texto mediante menos espacios, conservar el diseño y la
mascota, preparar un mock y permitir volver al original. Sólo Core/CLI/TUI;
no se modifica el worktree de M9 ni OmniCoder.

## Prototipo

La vista normal sigue siendo el valor predeterminado. `/compacto` o
`/density preview` inicia una prueba local, sin escribir la preferencia.
El pie muestra primero `Esc volver`; Esc restaura la densidad anterior sin salir
de la aplicación ni borrar el borrador. Los overlays y la interrupción de un
Run mantienen su prioridad existente sobre esa tecla.

`/compacto guardar` (`/density save`) conserva la elección en un archivo pequeño
`.density` junto a las preferencias de animación. `/compacto cancelar` restaura
la prueba; `/compacto normal` guarda de nuevo la vista normal. La configuración
es independiente de las cuentas, proveedores, permisos, journal y memoria.
También hay una acción de prueba en F4 → General → Interfaz, y autocompletado.
`/compact` conserva su significado de compactación del contexto.

Se reducen márgenes horizontales y verticales, el relleno del composer y las
separaciones decorativas entre mensajes. El borde lateral del chat usa un trazo
fino en compacto. No se eliminan líneas vacías del código ni del texto del modelo.
Los colores, paneles, especie, XP, imágenes, animación y caché de la mascota se
conservan. No se añade una sombra gráfica ni un nuevo renderizador.

## Comparación real de distribución

`artifacts/compact-preview-20261028/comparacion-120x40.png` y
`comparacion-80x25.png` se generan desde el buffer del driver Terminal.Gui,
con Host/journal temporales, historial y capacidad Sixel controlados por el
fixture. En la captura amplia se incorpora el raster exacto emitido por el
driver, no una mascota distinta ni una ilustración del resultado esperado.

A 120×40 y con la misma fuente, el área de lectura pasa de 31 a 34 filas.
En este ejemplo caben 25 filas con texto en normal y 34 en compacto; la ganancia
de contenido depende de la cantidad de mensajes y del ajuste de líneas.
El grupo SVG del raster y su posición es idéntico entre ambas capturas:
SHA-256 `65ad41c9b175b260ed6262f45902764e5a404823bf360aed8e12e4c1418079ec`.
Las mediciones están en `comparison-measurements.json` junto a los PNG.

## ClearType: perfil separado y optativo

`tools/terminal/OmniCore-CompactPreview.ps1` sin argumentos sólo muestra el mock
JSON. `-Apply` crea un fragmento propio con un perfil nuevo de Windows Terminal
y `antialiasingMode: cleartype`; no modifica `settings.json` ni el perfil habitual.
`-Restore` retira exclusivamente ese fragmento si sigue coincidiendo con el que
creó el script. Si alguien lo modifica, conserva el archivo y comunica el conflicto.

El perfil usa `OMNICORE_TUI_DENSITY=compact` como prueba de sesión; no guarda la
densidad por sí mismo. Requiere una versión de OmniCore con este prototipo para
aplicar esa parte. ClearType requiere una instancia nueva del terminal. Las
capturas comparan distribución; no simulan ni acreditan el resultado físico de
ClearType en la pantalla del usuario. Referencias oficiales:
[antialiasing de texto](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-advanced#text-antialiasing)
y [fragmentos de perfiles](https://learn.microsoft.com/en-us/windows/terminal/json-fragment-extensions).

## Verificación y alcance

- Build Release aislado: cero errores y advertencias. Outputs/intermedios propios,
  sin restaurar paquetes ni duplicar las bibliotecas nativas; aproximadamente 65 MiB
  antes de las imágenes, sin nuevo worktree ni escrituras en F:.
- 5/5 pruebas de densidad/store/renderizado y 3/3 recorridos TUI, incluidos
  80×25, 120×40, borrador, mascota, journal intacto y guardar/restaurar preferencia.
- 7 regresiones TUI existentes pasaron: composer, doble Esc, tema, sidebar y
  reloj/caché gráfica de la mascota. Los primeros dos fallos nuevos pertenecían
  al fixture que seleccionaba buey en lugar de zorro; el fixture corregido pasó.
- El filtro inicial que combinaba clases y métodos seleccionó cero casos; no se
  cuenta como validación. Los focos finales separados verificaron 5 y 3 casos.
- Script de perfil probado en PowerShell 7 y Windows PowerShell 5.1 con directorio
  temporal: mock sin escrituras, Apply, conflicto conservado y Restore exacto;
  perfil ajeno intacto. No se aplicó al directorio real de Windows Terminal.
- `TuiWiringTests.Density.cs` es propiedad de este cambio. El error inicial de
  `KeyCode` se corrigió importando `Terminal.Gui.Drivers`; el build actual pasa.

No se actualizó la instalación global ni se publicó el prototipo. La revisión
visual del usuario y la aceptación de ClearType en su terminal siguen pendientes.
Los cambios ajenos en Main y el trabajo de M9 se conservan en curso.
