# Vista previa de ejemplos Markdown · 2026-10-16

## Causa

La reparación anterior (`a5b6efb`) seguía presente en `MarkdownExamples`: ocultaba
un bloque `md` o `markdown` sólo si detectaba una copia visible después. La
respuesta de la captura contenía ejemplos encerrados en bloques Markdown sin
esa copia posterior. Por eso se mostraban como código fuente, con `##`, `**` y
otros delimitadores, aunque el renderizador sí admitía sus estilos.

La comparación anterior también consideraba únicamente la primera línea al
decidir si un ejemplo estaba repetido. Dos bloques con el mismo encabezado y
datos diferentes podían ocultar contenido de la vista normal.

## Corrección

La vista normal previsualiza los bloques completos `md`/`markdown` de mensajes
del asistente, tengan o no una copia posterior. Sólo pliega la copia cuando la
comparación cubre todo el cuerpo del ejemplo. Los delimitadores exteriores no
ocultan las tablas ni los estilos y los bloques interiores de C#, Python u otros
lenguajes conservan su representación de código.

`/fuente` sigue alternando con la representación literal. El journal y los
artefactos conservan el texto original. Los mensajes del usuario, bloques de
otros lenguajes y ejemplos todavía incompletos no se convierten en vistas previas.
El límite de tamaño existente sigue vigente. La corrección se aplica también a
respuestas ya guardadas al volver a mostrarlas en la conversación.

Las instrucciones del turno piden ejemplos Markdown formateados directamente
en la respuesta. Para peticiones explícitas de sintaxis literal se usa un bloque
`text`, que conserva sus delimitadores. No es necesario regenerar una respuesta
existente para que funcione la vista previa.

## Validación

| Comprobación | Resultado |
| --- | --- |
| Reproducción previa: bloques sin copia y cuerpos distintos con igual encabezado | Las tres pruebas fallaron antes de corregir. |
| Markdown, tablas, sintaxis, Unicode, enlaces y presentación de conversación | 94 pruebas aprobadas, 0 omitidas. |
| Revisión final de TUI y mensajes literales del usuario | 3 pruebas aprobadas, 0 omitidas; dos casos ya pertenecían a la serie anterior. |
| Compilación de la solución | Aprobada, 0 errores y 0 advertencias. |
| Arquitectura | 62 pruebas aprobadas, 0 omitidas. |

Las pruebas de TUI usan la conversación, journal, CAS y driver de Terminal.Gui
reales a 80×40 y 120×40. Comprueban que los encabezados y tablas se dibujan, que
negrita/cursiva/tachado llegan a los atributos de las celdas y que el código
interior se conserva. Alternan `/fuente` mediante teclado y verifican que no
cambian ni la secuencia del journal ni el contenido original del artefacto.

Los datos de las capturas son ejemplos de prueba. No se solicitó una respuesta
a un modelo remoto para acreditar el renderizado ni se modificaron conversaciones
del usuario.

Evidencia en `C:/Users/juanc/.codex/omnicore-linux-20261014/`:

- `markdown-preview-repro.log`.
- `markdown-preview-validation.log` y `markdown-preview-final-ui.log`.
- `markdown-preview-build.log` y `markdown-preview-architecture.log`.
- `markdown-preview-captures/`: vista previa y fuente con ambos anchos.

No se inició WSL. Una nueva ejecución de CI remoto no se acredita en esta entrega.
