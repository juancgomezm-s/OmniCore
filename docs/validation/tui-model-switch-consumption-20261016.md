# Cambio de modelo y bloqueos de consumo — 2026-10-16

## Evidencia y causas

Se revisó en modo de sólo lectura el journal de la conversación mostrada por el
usuario. El Run `01a14641-784e-7e90-9139-63838c158211` no tenía límite monetario
propio. La política inicial de su sesión, originada en `local-qwen`, conservaba
un límite de **5 USD**. Después se autorizó una ruta `chatgpt` con facturación
`IncludedQuota`. La admisión monetaria seguía aplicando el límite de la sesión
y emitió `BudgetExceeded`: «uso histórico incompleto: no se puede hacer cumplir
el tope». La ausencia de medición se estaba usando para bloquear una ruta que
no factura USD por invocación.

El aviso anterior de esa misma ejecución era `ModelRouteConsent`, no una
activación de UltraCode. La presentación técnica de «ruta» y `IncludedQuota`
no explicaba con claridad que se estaba cambiando de proveedor.

Había también un problema de presentación: al reconstruir la conversación,
`model_step.started` de invocaciones anteriores podía volver a fijar el modelo
del encabezado, sobreescribiendo la selección explícita del usuario. Así podía
verse Qwen mientras se intentaba utilizar ChatGPT.

## Comportamiento corregido

| Dato | Al cambiar de modelo |
| --- | --- |
| Historial, objetivo y plan de la conversación | Se conservan. `/limpiar` sigue afectando sólo a la vista. |
| Permisos, autonomía y autorizaciones existentes | Se conservan sus ámbitos; elegir un modelo no amplía permisos. |
| Modelo mostrado | La elección explícita prevalece sobre eventos anteriores. Una observación posterior de una invocación real puede actualizarlo. |
| Contexto, herramientas y credenciales del modelo | Se resuelven para el destino con los contratos existentes. |
| Razonamiento solicitado | Se valida contra las capacidades del destino; una petición incompatible no se envía. |
| Límite monetario de Run/sesión/día | No se aplica a `Local` o `IncludedQuota`. Se conserva en el journal para rutas monetarias. |
| Consumo histórico y mediciones incompletas | Se conservan; desconocido no se convierte en cero. |
| Cuota de suscripción por debajo del 10 % | Se muestra como dato; no abre una aprobación por cada pregunta normal. |

`ExplorerTurn` recibe ahora la facturación declarada del proveedor. Desactiva
los controles monetarios no aplicables sin borrar los límites originales ni
los límites de turnos, herramientas y tokens. La facturación desconocida conserva
la admisión conservadora cuando existe un presupuesto explícito.

Se retiró la creación de interacciones por el umbral artificial de cuota baja
de suscripción. Las mediciones siguen consultándose y pueden suprimir trabajo
auxiliar opcional; los rechazos y límites reales del proveedor no se ocultan.
Los caminos de resolución de interacciones ya persistidas siguen siendo
compatibles y no se autoaprueban.

El aviso de cambio de proveedor identifica el modelo y proveedor, explica si
usa suscripción, créditos o cobro por uso, y ofrece «Usar en esta conversación»
o «Volver». Mantiene la autorización explícita, su denegación predeterminada y
la correlación con la misma solicitud. No inicia un flujo de agentes.

## Validación

- **125 pruebas aprobadas**, 0 fallidas y 0 omitidas: admisión monetaria,
  sesiones/rutas, preferencias de razonamiento, selector de modelos y consumo.
- **62 pruebas de arquitectura aprobadas**, 0 fallidas y 0 omitidas.
- Publicación Release completada; `git diff --check` sin errores.
- Reproducción con Host real, HTTP local de prueba, journal SQLite y una sesión
  que conserva 5 USD después de quitar el tope de la configuración: dos preguntas
  se completan tanto con `Local` como con `IncludedQuota`, incluido un caso con
  5 % de cuota restante y costo desconocido. No se usó una cuenta real ni se
  acredita aquí una respuesta autenticada de ChatGPT.
- Pruebas del engine con uso incompleto, herramientas y límites monetarios de
  Run/sesión/día: local y suscripción continúan; al volver a una ruta monetaria
  el límite vuelve a aplicarse. Los límites originales permanecen intactos.
- Las nueve variantes de cuota baja, caducada, desconocida, de otro proveedor,
  otra sesión y consulta actualizada ahora verifican cuatro preguntas reales al
  servidor de prueba sin diálogos artificiales. Se actualizó su comportamiento
  esperado porque se retiró deliberadamente ese bloqueo; no se omitieron casos.
- Entradas reales del driver comprueban seleccionar un modelo, limpiar sólo la
  vista, retomar la misma conversación y releer el journal sin recuperar el modelo
  viejo en el encabezado. Una invocación posterior sigue actualizando el modelo.
- Se conserva la prueba de que aceptar un cambio de proveedor reanuda una sola
  solicitud y que denegarlo no genera una llamada. Se exportó el diálogo desde
  las celdas del driver para revisar su presentación.

Logs, TRX, capturas y publicación locales: prefijo `model-switch` bajo
`C:/Users/juanc/.codex/omnicore-linux-20261014/`, y `TestResults/` del worktree.
Esta entrega no acredita una nueva ejecución de CI ni arranca WSL.
