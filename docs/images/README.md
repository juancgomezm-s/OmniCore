# Imágenes del README

- `tui-conversation.svg` y `tui-preferences.svg` son frames exportados del driver
  real de Terminal.Gui. Se generan con `tools/test-conversation-rendering.ps1 -Snapshots`;
  usan fixtures deterministas locales, sin llamadas a proveedores.
  Representan la interfaz implementada en el tamaño de terminal indicado por el
  frame, no una captura de escritorio ni una sesión autenticada.
- `product-vision`: concepto visual de conversación, composer y panel lateral de
  una experiencia futura; no representa la interfaz actual.
- `runtime-map`: responsabilidades y fronteras de arquitectura.
- `roadmap`: hitos y estado documental; no promete fechas.

Los SVG de la TUI contienen el rasterizado por celdas que exporta el test driver.
Las ilustraciones mantienen sus fuentes SVG y versiones PNG para render y legibilidad.

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
