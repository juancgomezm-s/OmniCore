# Validación de Visual explainer · 2026-10-16

Rama: `codex/tui-openrouter-usability`. Fuente revisada:
`arpagon/visual-explainer@48af6c3`. Adaptación local con licencia MIT preservada.

## Resultados

| Comprobación | Resultado |
| --- | --- |
| `dotnet build OmniCore.slnx --no-restore` | Aprobada, 0 errores y 0 advertencias. |
| Pruebas de habilidades, enlaces, menú y dibujo TUI | 115 aprobadas, 0 omitidas. |
| Arquitectura | 62 aprobadas, 0 omitidas. |
| Generador Python | 4 pruebas aprobadas. |
| Validador de `SKILL.md` de skill-creator | Aprobado. |
| Publicación Release mediante `tools/npm/prepare.js` | Aprobada. |
| Inspección del paquete npm con `pack --ignore-scripts --dry-run --json` | Incluye los 8 archivos de la habilidad; excluye cachés Python. |
| Aplicación publicada, consulta `/skills` con datos de prueba aislados | Descubre `visual-explainer`. |
| Generación real del ejemplo JSON a Markdown y HTML | Aprobada. Datos identificados como ilustrativos. |
| Vista HTML en navegador | Pendiente: la política del navegador de validación bloquea `file:`. |
| Proveedor/modelo autenticado | No ejecutado para esta entrega. |
| Nueva ejecución remota de CI y ejecución Linux | No acreditadas en esta entrega; no se inició WSL. |

## Cobertura relevante

`VisualExplainerSkillTests` carga el paquete real que acompaña a la aplicación,
verifica sus recursos y hashes, el límite de contexto de 4096 tokens, la ruta del
paquete en el contexto y el aislamiento entre sesiones. También carga la entrada
portable `SKILL.md` mediante el adaptador compatible con Codex.

`TuiWiringTests.VisualExplainer` activa y libera la habilidad integrada desde
el menú de producción en 80×25 y 120×40, incluso con el proyecto sin confiar.
El alcance `BuiltIn` se envía al Host; las habilidades Project conservan su
exigencia de confianza. Otra prueba dibuja una respuesta ilustrativa con diagrama
y tabla usando el journal, CAS y driver de Terminal.Gui reales. En 80×25 también
recorre la respuesta para comprobar el contenido que requiere desplazamiento.

La etiqueta de confianza de las habilidades integradas muestra **Confiable ·
integrada de OmniCore** y aparece antes que sus identificadores técnicos. El
aviso de confianza del espacio sólo aparece al inspeccionar una habilidad Project.
Las pruebas del menú comprueban que el Host mantiene `BuiltIn/Core` antes y
después de activar/liberar, sin cambiar la confianza del espacio. Esta etiqueta
identifica la clasificación de confianza existente; no es una firma digital ni
una certificación externa del código.

La corrección de esa presentación se validó con 83 pruebas de habilidades,
preferencias y auditoría del menú, todas aprobadas y ninguna omitida. Evidencia:
`visual-skill-trust-tests.log` y `visual-skill-trust-captures/` en el directorio
local de validación indicado más abajo.

`ConversationHyperlinkTests` verifica reconocimiento de informes HTML, límites
del espacio de trabajo, archivos existentes y rechazo de ejecutables, rutas UNC,
controles y consultas locales. El lanzador de OAuth sigue rechazando HTML.
`TuiWiringTests.Hyperlinks` comprueba clic y Enter sobre el enlace HTML en la
conversación real, con un lanzador de navegador de prueba que registra el destino.
La carga y presentación del enlace no abren ningún navegador.

`test_visual_explainer_report.py` comprueba ancho con Unicode, conservación de
etiquetas, rechazo de campos y estados inválidos, escape de HTML y creación real
de archivos en un espacio temporal. Incluye rechazo de sobrescrituras y rutas
fuera del directorio de trabajo. El workflow CI ejecutará estas pruebas en ambos
sistemas, además de las pruebas .NET existentes.

## Reproducción

```text
dotnet build OmniCore.slnx --no-restore
dotnet test --project tests/OmniCore.Tests --no-build --no-restore --filter "FullyQualifiedName~Skill|FullyQualifiedName~Hyperlink|FullyQualifiedName~Visual_text_diagram|FullyQualifiedName~Local_HTML_report|FullyQualifiedName~Menu_audit|FullyQualifiedName~SessionPreferences" --no-progress
dotnet test --project tests/OmniCore.ArchitectureTests --no-build --no-restore --no-progress
python -m unittest discover -s tests -p test_visual_explainer_report.py
node tools/npm/prepare.js
npm pack --ignore-scripts --dry-run --json
```

## Evidencia local

Se conservan logs, Markdown/HTML generados y capturas de las celdas del driver en
`C:/Users/juanc/.codex/omnicore-linux-20261014/`:

- `visual-skill-build.log`, `visual-skill-tests.log`, `visual-skill-architecture.log`.
- `visual-skill-publish.log`, `visual-skill-npm-pack.json`, `visual-skill-published-discovery.log`.
- `visual-explainer-captures/`: menú y conversación, en los dos tamaños.
- `visual-explainer-demo/ejemplo.md` y `ejemplo.html`.

Las imágenes proceden de pruebas de la TUI y contienen datos de prueba. No son
capturas de una conversación del usuario ni acreditan generación mediante un
modelo real. El HTML se comprobó estructuralmente, sin simular una comprobación
visual de navegador.
