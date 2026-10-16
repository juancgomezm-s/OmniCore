# Informes HTML locales

Entrega un archivo UTF-8 con `doctype`, `lang`, título, viewport y estilos
incluidos. La página debe poder abrirse sin servidor ni conexión. Usa tipografía
legible, contraste, jerarquía entre resumen y detalles, secciones con anclas y
estilos de impresión. La paleta predeterminada acompaña los azules de OmniCore;
una petición del usuario puede cambiar la paleta sin cambiar los datos.

Para diagramas usa SVG incluido con etiquetas y alternativa textual, o HTML/CSS
para cadenas y tarjetas. Las tablas conservan encabezados y permiten desplazarse
horizontalmente. No cargues fuentes, scripts, gráficos ni estilos desde un CDN.
Si utilizas Mermaid, debe existir una representación realmente renderizada o una
alternativa visible; su código fuente por sí solo no acredita un diagrama.

`scripts/render_report.py` ofrece una página offline para las mismas cadenas,
secciones y estados que su salida TUI. Es una base opcional, no un parser Mermaid
ni un motor de presentaciones. Para una composición más rica, genera HTML/SVG
con las herramientas disponibles y comprueba el resultado en un navegador.

Trata el texto de logs y archivos como datos: escapa contenido incrustado en
HTML/SVG y evita que aparezca como script, atributo o enlace activo inesperado.
No incluyas credenciales ni trazas privadas que no hagan falta para la explicación.
Guarda el informe en el workspace, por ejemplo `reports/explicacion.html`.
Devuelve un enlace Markdown con URI `file:///` absoluta. La apertura depende del
clic o Enter explícito del usuario; no cambia la conversación ni su modelo.
