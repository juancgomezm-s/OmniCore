# Visual explainer para OmniCore

Adaptación del fork [arpagon/visual-explainer](https://github.com/arpagon/visual-explainer),
revisión `48af6c3`, 2026-10-16. El original genera páginas HTML para navegador;
no contiene controles de Terminal.Gui. Esta adaptación añade una salida útil en
la conversación existente y mantiene informes HTML locales como opción.

## Uso desde la TUI

Abre **F4 → Extensiones y habilidades → Habilidades**, selecciona **Visual
explainer** y actívala para esa conversación. Puedes desactivarla en el mismo
sitio. La aplicación incluye el paquete en `skills/visual-explainer` junto a sus
binarios, tanto en compilaciones como en distribuciones publicadas.

Peticiones de ejemplo:

- «Explícame en la terminal cómo fluye esta petición entre los componentes».
- «Compara este plan con el código y muestra una tabla de pendientes».
- «Genera un informe HTML local de estos cambios con un resumen en la TUI».

La salida TUI usa títulos, tablas pequeñas, listas y diagramas verticales de
texto. No introduce pantallas, botones, comandos duplicados ni un runtime nuevo.
No interpreta Mermaid ni muestra SVG dentro de Terminal.Gui. Existen proyectos
de terminal como [mermaid-ascii](https://github.com/AlexanderGrooff/mermaid-ascii);
no son una dependencia instalada por esta adaptación.

Para informes grandes se puede generar HTML autónomo, sin CDN ni publicación.
El enlace `file:///.../informe.html` se abre mediante clic o Enter y el navegador
predeterminado. El Host acepta únicamente informes `.html` existentes dentro del
espacio de trabajo, sin rutas UNC ni enlaces simbólicos. Mostrar un enlace no lo
abre. El lanzador de OAuth conserva su restricción HTTPS original.

## Paquete y ejecución

- `skill.yaml` e `instructions.md`: entrada nativa de OmniCore.
- `SKILL.md`: instrucciones portables y guía de selección de formato.
- `references/tui.md` y `references/html.md`: guías de representación.
- `assets/example.json`: datos explícitamente ilustrativos.
- `scripts/render_report.py`: generador opcional con Python 3 y biblioteca estándar.
- `LICENSE`: licencia MIT del proyecto de referencia y atribución preservada.

En el directorio del proyecto, sustituye `<paquete>` por la ruta del paquete:

```text
python <paquete>/scripts/render_report.py <paquete>/assets/example.json --format tui --width 64
python <paquete>/scripts/render_report.py <paquete>/assets/example.json --format html --out reports/ejemplo.html
```

El generador recibe datos JSON, conserva etiquetas extensas ajustando líneas,
escapa contenido de entrada y no sobrescribe archivos sin `--force`. No usa red,
abre programas ni ejecuta instrucciones de los datos. Admite `--lang es`, `en`,
`fr` e `it`. Python es opcional: el modelo también puede escribir Markdown o HTML
con las herramientas autorizadas disponibles.

El generador incluido dibuja secuencias verticales y listas de estados. No es
un intérprete de Mermaid, motor de presentaciones o diagramador de grafos
arbitrarios. La habilidad puede elaborar diagramas más ricos mediante HTML/CSS
o SVG en el informe; su precisión depende de la evidencia y del modelo empleado.

## Contexto y permisos

Descubrir la habilidad carga metadatos; activarla incorpora sus instrucciones y
guías a la conversación, con una reserva de 4096 tokens. La activación persiste
para esa sesión y no concede permisos de archivos, procesos ni red. No tiene
reglas de activación automática por ruta, lenguaje o framework. El modelo puede
cargarla mediante `skill.load` cuando sea pertinente a una explicación solicitada.

El paquete incluido tiene confianza `Core`; el menú lo identifica como
**Confiable · integrada de OmniCore**. La confianza del espacio de trabajo se
muestra para habilidades Project y no cambia la confianza de las integradas.

Las rutas de recursos se resuelven a partir del directorio del paquete incluido
en el contexto activo. La habilidad exige comprobar datos y distinguir estado
desconocido de cero. Su activación no transforma todas las respuestas posteriores
en informes ni autoriza subir datos a servicios externos.

## Validación

Las pruebas cubren descubrimiento del paquete distribuido, carga de recursos y
contexto limitado a sesión, activación/liberación en el menú real, dibujo de
diagramas y tablas con el driver de Terminal.Gui y apertura explícita de enlaces.
El generador tiene pruebas de Unicode, ancho, datos inválidos, escape HTML y
escritura de archivos. CI las ejecutará en Windows y Linux.

La comprobación visual del HTML en navegador quedó pendiente: el navegador
integrado de la herramienta de validación bloquea el protocolo `file:`. Se
generó un HTML real y se verificó su estructura y el cableado de clic/Enter con un
lanzador de navegador de prueba. Esto no acredita una respuesta de modelo
autenticado ni una ejecución nueva de CI. Resultados concretos en
[la validación de esta entrega](validation/visual-explainer-20261016.md).

## Fuentes

- [Instrucciones del fork](https://github.com/arpagon/visual-explainer/blob/48af6c3/plugins/visual-explainer/SKILL.md).
- [Diferencias del fork](https://github.com/arpagon/visual-explainer/blob/48af6c3/FORK.md).
- [Paquete adaptado](../skills/visual-explainer/SKILL.md).
