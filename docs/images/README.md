# Imágenes del README

- `tui-conversation.svg`, `tui-preferences.svg` y `tui-sidebar.svg` son frames exportados del driver
  real de Terminal.Gui. Se generan con `tools/test-conversation-rendering.ps1 -Snapshots`;
  usan fixtures deterministas locales, sin llamadas a proveedores.
  Representan la interfaz implementada en el tamaño de terminal indicado por el
  frame, no una captura de escritorio ni una sesión autenticada.
- `product-vision`: concepto visual de conversación, composer y panel lateral de
  una experiencia futura; no representa la interfaz actual.
- `runtime-map`: responsabilidades y fronteras de arquitectura.
- `roadmap`: hitos y estado documental; no promete fechas.

Los SVG de la TUI contienen las celdas y atributos que exporta el test driver.
Sus versiones PNG se rasterizan sin modificar colores, contenido ni layout, y se
inspeccionan visualmente antes de publicarse. El README utiliza estos PNG.
Las ilustraciones mantienen sus fuentes SVG y versiones PNG para render y legibilidad.

Para rasterizar los frames de la TUI:

```powershell
node tools/render-tui-frames.cjs docs/images C:/ruta/node_modules/sharp
```

Para regenerar, con Node.js y `sharp` disponible, desde la raíz:

```powershell
node tools/render-readme-images.cjs
```

También puedes pasar una ruta absoluta al paquete `sharp` ya instalado:

```powershell
node tools/render-readme-images.cjs C:/ruta/node_modules/sharp
```

No se descargan fuentes ni imágenes remotas. Al actualizar hitos, cambiar también
sus etiquetas y el texto del README según las evidencias de `docs/validation`.

## Formularios e indicadores de extensiones

`settings-extension-form-es-80x25` y
`settings-extension-indicators-en-120x35` proceden del búfer RGB real de
Terminal.Gui con `example.preferences`, proceso Node/Core y datos locales de
prueba. Se inspeccionaron sus PNG; no son capturas del escritorio ni aceptación
visual humana. El indicador de cuota es desconocido, no un proveedor simulado.

Para regenerar los SVG, compila el SDK y la solución; ejecuta las pruebas
`Extension_form_edits*` y `Extension_indicator_renderer*` con
`OMNICORE_TUI_SNAPSHOT_DIR` apuntando al destino, `TERM=xterm-256color`,
`COLORTERM=truecolor` y sin `NO_COLOR`. Rasteriza los SVG como se indica arriba.
Conserva las dimensiones de celda y los colores del driver.
