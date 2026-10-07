# Imágenes del README

Ilustraciones propias del proyecto, editables en SVG. No son capturas de OmniCore
ni evidencia de consultas autenticadas o de funciones terminadas.

- `product-vision`: concepto de conversación, composer y panel lateral del producto final.
- `runtime-map`: responsabilidades y fronteras de arquitectura.
- `roadmap`: hitos y estado documental al 2026-10-07; no promete fechas.

El README utiliza las versiones PNG por compatibilidad de render y legibilidad.
Las fuentes SVG incluyen título y descripción accesibles; el README añade texto alternativo.

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
