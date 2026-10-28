# Modelo y consumo independientes de la mascota — 2026-10-28

La configuración de **Mascota: modelo y consumo**, dentro de **Modelos y ejecución**, permite guardar un modelo propio del mini chat. **Solo local** es opcional y está desactivado por defecto. Al desactivarlo se admiten también modelos por consumo. No se hereda el modelo principal ni se consultan sus autorizaciones de ruta o sus cuotas monetarias. La elección explícita usa los proveedores y la autenticación existentes.

El consumo se guarda en tablas propias de `companion/coordinator.db`: totales por modelo/proveedor y recibos de consulta. No impone cuotas. Los totales se actualizan en transacciones para compartirlos entre instancias y evitar duplicados. Un recibo sin respuesta permanece incompleto; no se supone que haya costado cero. Las estimaciones de tarifas y los costes reportados se muestran por separado. Caché y razonamiento son detalles incluidos en entrada/salida, no tokens extra que sumar.

## Validación focal

Compilación Release de una copia limpia de `main` más los archivos de esta entrega, .NET 10 y Terminal.Gui 2.6.0-develop.61, sin cambios ajenos del árbol de trabajo. **20 pruebas aprobadas**, sin fallos, registradas en `TestResults/companion-model-tests.trx`.

Se comprueban selección propia persistente, modelo remoto con la opción local desactivada, rechazo de respaldo remoto cuando se activa, deduplicación de consumo, precios y tokens ausentes, detalles de caché/razonamiento y contador sin límites monetarios. La prueba del contador registra ochenta consultas con estimaciones acumuladas de 80,000 USD sintéticos, sin aplicar bloqueos ni realizar solicitudes externas.

El recorrido de configuración TUI guarda primero un modelo por consumo y luego uno local, filtra la lista al activar la opción, conserva la sesión principal y muestra el consumo registrado. No llama al modelo al abrir o guardar ajustes. La suite incluye las pruebas previas de mini chat y coordinación.

## Recorrido completo con proveedor simulado

Se ejecutaron los comandos CLI compilados contra un servidor HTTP/SSE local declarado **MeteredCurrency**, con `auth: none`, en un perfil aislado. No se leyeron claves de un proveedor real ni se generaron cargos reales.

1. `pet model paid-pet --any-model --json` guardó el modelo propio.
2. `pet chat "Hola mascota" --json` recibió `Hola, soy tu mascota.`.
3. La solicitud HTTP eligió `paid-pet`, envió `tool_choice: none` y no incluyó ninguna definición de herramientas.
4. `pet usage --json` devolvió una consulta, una respuesta, 120 tokens de entrada, 30 de salida y una estimación de **0.00180 USD**, calculada con las tarifas sintéticas de 10/20 USD por millón. No inventó un coste informado por el proveedor.

Evidencia: `TestResults/companion-model-http/{selected,reply,usage,request}.json`. Son datos de prueba. El contador es informativo y no equivale a una factura ni garantiza conocer reintentos internos no reportados por el proveedor.

## Captura de configuración

`TestResults/companion-model-frames/settings-companion-model-config-120x40.svg` procede de las celdas del driver real de Terminal.Gui, con datos sintéticos y proveedor falso. La imagen PNG del mismo nombre sirve como vista previa. Se verifican la opción **Solo local**, el selector de modelo, el mensaje **Sin cuotas** y el contador propio.

La mascota sigue sin herramientas del agente principal. No se añadió un catálogo nuevo de herramientas: esa posibilidad queda separada para diseñar capacidades específicas de la mascota sin importar todo el conjunto de herramientas del agente.
