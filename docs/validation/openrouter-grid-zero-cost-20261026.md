# Catálogo OpenRouter y bloqueo de una ruta de costo cero — 2026-10-26

## Resultado

El catálogo TUI usa un TableView con encabezados Compañía, Modelo, Contexto y
Costo (USD/M). La leyenda define entrada/salida por millón de tokens; por ejemplo,
`0.10/10.00 USD`. La conversión usa decimal e incluye ambas tarifas. Una tarifa
ausente, negativa, inválida o no representable aparece como `—`, nunca como gratis.

La TUI ordena por compañía e ID todo el catálogo local acotado a 1000 modelos antes
de paginar; el registro conserva el ID exacto de la fila ordenada. Abre sin red,
actualiza sólo mediante acción explícita y conserva búsqueda y registro individual.
El título que mostraba `openrouter.catalog.title` tiene recursos propios es/en/fr/it.
Los nombres e identificadores externos se conservan y se eliminan controles de terminal.

## Causa del bloqueo de presupuesto

La conversación real había usado Qwen local y ChatGPT con consumo no reportado.
Después se autorizó `nvidia/nemotron-3.5-super-vl:free`, registrado con tarifas de
entrada y salida iguales a cero. La ruta conserva BillingMode.MeteredCurrency.
El control anterior rechazaba el historial incompleto antes de llamar al modelo.
La corrección previa para Local/IncludedQuota no cubría esta situación.

La admisión permite una invocación de salida textual cuando su cota monetaria
conservadora es exactamente cero: requiere precios completos, límites de contexto,
salida e intentos conocidos. No se decide por el nombre o sufijo del modelo.
No reserva dinero ni aplica los topes monetarios a esa invocación sin exposición
adicional. Conserva los controles de tokens, herramientas, delegación, UltraCode,
permisos y autorización de ruta. Los costos históricos desconocidos permanecen
desconocidos; volver a una ruta de pago sigue bloqueado por esa misma evidencia.
Imágenes, otras modalidades y cotas incompletas no reciben esta excepción.

Al reabrir, la recuperación retira una interacción monetaria obsoleta únicamente
si todas las rutas autorizadas son no monetarias o tienen tarifas actuales cero
verificables y la identidad exacta autorizada. Una ruta cambiada o de pago impide
esa recuperación. Se añade InteractionExpired; no se fabrica consentimiento ni se
reescribe el historial, y no se reenvía el input automáticamente.

## Verificación

- Compilación Release: 0 errores y 0 advertencias.
- Grid, catálogo y selector: 24 PASS, 0 FAIL, 0 SKIP. Incluye ocho recorridos con
  Terminal.Gui real, 80×25 y 120×40 en es/en/fr/it, encabezados y precios visibles,
  registro de la fila ordenada, conversión, valores desconocidos y agrupación
  previa a paginar más de 100 modelos.
- Regresión de costo cero y recuperación: 20 PASS, 0 FAIL, 0 SKIP.
- Regresión monetaria, límites diarios/sesión, concurrencia, continuidad y cotas:
  105 PASS, 0 FAIL, 0 SKIP. Las tres series tienen 149 casos únicos.
- Snapshot SQLite de la conversación afectada: 156 eventos originales y 94 blobs
  copiados. La recuperación conserva todos los eventos originales, expira un aviso
  obsoleto, no agrega una respuesta de usuario ni un fallo/cancelación de Run y
  no informa problemas de recuperación. No modifica el journal de producción.
- Cero solicitudes de inferencia OpenRouter en esta validación. Los fixtures
  verifican admisión; la respuesta real del proveedor conserva aceptación manual.

Logs y frames RGB sintéticos en `%TEMP%/omnicore-openrouter-grid-frames-20261026/`,
`omnicore-combined-grid-tests.log`, `omnicore-zero-cost-focused.log` y
`omnicore-zero-cost-regression.log`. El snapshot y el probe local se conservan en
`omnicore-zero-cost-real-history-20261026/` y
`omnicore-zero-cost-recovery-probe-20261026/`.

Trabajo aislado en `codex/openrouter-catalog-grid` desde `916c392`. El checkout
principal y el worktree M9 se conservan; no se atribuye esta prueba al cierre M9/M10
ni a una ejecución nueva de CI remoto.

## Seguimiento: mantenimiento de modelos visibles

La solicitud posterior señala que falta el acceso para escoger qué modelos se
muestran en el menú. El mantenimiento previo existía bajo «Activar modelos», pero
el nuevo catálogo no lo exponía. Ahora se llama «Modelos visibles en F3» y aparece
en Preferencias → Modelos y perfiles y desde el catálogo OpenRouter. F3 ofrece
«Mostrar/ocultar modelos · Ctrl+M».

El grid incorpora una columna F3 con `[✓]`/`[ ]`, un botón para mostrar/ocultar el
seleccionado y Espacio sobre su fila. Los modelos con salida exclusivamente no
textual muestran `—` y no ofrecen activar su visibilidad en el selector de texto.
El mantenimiento incluye Mostrar resultados/Ocultar resultados sobre la búsqueda
actual, incluso para modelos OpenRouter todavía no registrados. Guarda una sola
transacción atómica por lote en las preferencias User existentes; las identidades
incluyen proveedor y modelo. No registra modelos, elige una ruta, cambia el modelo
activo, altera permisos ni envía inferencias.

Se verificaron 33 casos únicos de visibilidad, catálogo y selector: la primera serie
tuvo 32 PASS y un fallo en el tipo de excepción esperado por el fixture de rollback;
se corrigió esa expectativa y el caso pasó aislado. Tras ajustar geometría y
excluir salida no textual, los 16 recorridos afectados pasaron sin omisiones.
Incluyen persistencia/reapertura, rollback de un lote fallido, aislamiento por
proveedor, edición individual y filtrada, F3, borrador/modelo activo/eventos intactos,
80×25/120×40 y recursos es/en/fr/it. Build Release sin errores ni advertencias.
Los frames RGB sintéticos y logs están en `%TEMP%/omnicore-model-visibility-frames-20261026/`
y `omnicore-model-visibility-{tests,atomic,layout,build}.log`.
