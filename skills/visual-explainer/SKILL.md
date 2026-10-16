---
name: visual-explainer
description: Explica arquitecturas, flujos, cambios de código y planes mediante diagramas y tablas legibles en la TUI de OmniCore, con informes HTML locales cuando se solicita una salida visual más amplia.
license: MIT
metadata:
  upstream: https://github.com/arpagon/visual-explainer
  upstream_revision: 48af6c3
---

# Visual explainer para OmniCore

Convierte una petición de explicación visual, revisión de cambios, comparación
de un plan o resumen de proyecto en una representación comprobable y fácil de
leer. Usa el idioma del usuario. Actúa cuando solicite esa explicación o active
esta skill; activar la skill no convierte todas las respuestas posteriores en
informes ni abre navegadores automáticamente.

## Elegir la salida

- **TUI:** explica lo esencial dentro de la conversación. Usa Markdown nativo,
  tablas breves y diagramas de texto verticales. Lee `references/tui.md`.
- **HTML:** cuando el usuario pida un informe, una página, presentación visual
  extensa o un diagrama que no resulte legible en terminal, genera un archivo
  local. Lee `references/html.md`. Mantén un resumen útil en la conversación.
- Si pide ambas, entrega las dos vistas a partir de los mismos datos. Si pide
  únicamente una, respeta esa elección.

## Trabajar con evidencia

Revisa los archivos, cambios, resultados o datos relevantes antes de dibujar.
Conserva las relaciones, decisiones y nombres reales. Distingue lo observado
de lo propuesto y lo desconocido. No marques un test, despliegue o hito como
aprobado sin su resultado. Cita archivos y líneas o fuentes cuando sostengan
afirmaciones importantes; no inventes métricas para llenar un gráfico.

Para revisar un diff, relaciona el comportamiento anterior y posterior con
archivos y validación. Para un plan, compara sus requisitos con implementación
y evidencia. Para retomar un proyecto, muestra objetivo, componentes, estado y
siguiente decisión sin reproducir todas las trazas.

## Generación opcional desde datos

Si hay Python 3 y ejecución de procesos permitida, el paquete incluye
`scripts/render_report.py`, sin dependencias externas. Recibe un JSON con
`title`, `summary` y `sections`. Cada sección admite `title`, `text`,
`flow` (lista de pasos) y/o `rows` con `label`, `status` y `detail`. Los estados
son `info`, `passed`, `failed`, `pending` y `unknown`. `sources` es una lista de
referencias textuales. `assets/example.json` muestra el formato.

```text
python <directorio-del-paquete>/scripts/render_report.py datos.json --format tui --width 64
python <directorio-del-paquete>/scripts/render_report.py datos.json --format html --out reports/informe.html
```

Los ejemplos usan un marcador de ruta: sustitúyelo por el directorio real del
paquete. El script no abre programas ni usa red. No sobrescribe un archivo
existente salvo que se pida `--force`. El HTML se guarda dentro del workspace.
Si Python o los procesos no están disponibles, escribe el Markdown o HTML con
las herramientas de archivos permitidas; la salida TUI no requiere Python.

## Entrega

Comprueba que el diagrama y el texto cuentan la misma historia. En TUI, revisa
que el flujo quepa en una conversación estrecha. En HTML, comprueba estructura,
legibilidad y ausencia de contenido remoto necesario para verlo. Ofrece el enlace
local `file:///.../informe.html` para que el usuario lo abra con clic o Enter.
No generes enlaces hacia ejecutables, scripts o archivos ajenos al informe.
No publiques, subas datos, instales paquetes ni abras procesos por el mero hecho
de activar la skill. Una petición de abrir o publicar se trata como una acción
separada según la autorización existente.

Esta adaptación toma como referencia visual-explainer de Nico Bailon y el fork
de arpagon, con salida nativa para OmniCore. El paquete conserva su licencia MIT.
