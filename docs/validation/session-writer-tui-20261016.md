# Sesión ocupada y conservación del mensaje · 2026-10-16

## Causa confirmada

Varias ventanas del mismo espacio de trabajo pueden abrir la última conversación.
El Host conserva un bloqueo de escritura exclusivo por sesión durante su vida.
Una segunda ventana puede leer el historial, pero no escribir mientras la primera
mantenga el bloqueo. Es una exclusión real del sistema operativo, no un archivo
de bloqueo abandonado que deba borrarse.

La TUI vaciaba el editor al iniciar el envío, antes de que el Host admitiera el
mensaje. El rechazo `session_writer_owned` se convertía en `InvalidOperationException`
y aparecía en un aviso con un fragmento de traza sin utilidad. Abrir repetidamente
versiones de prueba sin cerrar las anteriores podía provocar esta situación.

Se reprodujo con dos runtimes reales, un journal SQLite temporal y un proveedor
HTTP local de prueba. Antes de la corrección se obtuvo el mismo diagnóstico de la
captura del usuario, incluido `frames: 548 | | |a|t|`. El aviso de configuración
`auth` obsoleta era adicional y no causaba el rechazo de escritura.

## Corrección

- La TUI pide admisión de escritura antes de vaciar el editor o mostrar actividad.
- Si la sesión está ocupada, conserva el mensaje y los adjuntos, y explica cómo
  continuar en cuatro idiomas mediante el código estable del Host.
- Aceptar el aviso consume la tecla y vuelve al chat; no cierra la aplicación ni
  vuelve a enviar el mensaje por propagación de Enter.
- El runtime también comprueba la escritura antes de resolver o ejecutar el
  proveedor. Devuelve el resultado recuperable `TurnExitCodes.SessionBusy` (5),
  sin convertir este caso conocido en una excepción.
- Cerrar el propietario libera el bloqueo real. La segunda ventana puede
  obtenerlo y reenviar en la misma sesión sin reiniciar la aplicación.
- Un error de almacenamiento durante esa comprobación se muestra sin perder el
  borrador ni terminar el bucle de la TUI.

La corrección conserva la exclusión entre escritores. No borra archivos de
bloqueo, interrumpe procesos ajenos, crea otra conversación automáticamente ni
reintenta mensajes sin una acción explícita del usuario.

## Pruebas y evidencia

| Comprobación | Resultado |
| --- | --- |
| Reproducción antes de corregir | Falla con el mismo diagnóstico que la captura. |
| Regresión de bloqueo, concurrencia, transporte y conversación | 76 aprobadas, 0 omitidas. |
| Revisión final de interfaz, reenvío y traducciones | 24 aprobadas, 0 omitidas. Incluye casos de la serie anterior. |
| Arquitectura | 62 aprobadas, 0 omitidas. |
| Compilación de la solución | Aprobada, 0 errores y 0 advertencias. |

`CliEndToEndTests.Occupied_session_rejects_input_without_provider_work_and_same_window_can_retry_after_owner_closes`
usa dos runtimes reales. Verifica rechazo sin eventos nuevos ni llamada HTTP,
liberación al cerrar el propietario y continuación en la misma sesión con un
único `UserInputReceived` para el mensaje reenviado.

`TuiWiringTests.SessionOwnership` ejercita Enter, aviso, editor, aceptación y
reenvío a 80×25 y 120×40 usando la TUI y su driver reales. Comprueba continuidad
del bucle y conservación del borrador, además del tratamiento de un fallo de
almacenamiento. El bloqueo se controla en ese test de interfaz; su exclusión real
se comprueba en la prueba de runtime y en las pruebas de journal/procesos.

Evidencia local en `C:/Users/juanc/.codex/omnicore-linux-20261014/`:

- `session-writer-repro.log`: reproducción que falla antes de la reparación.
- `session-writer-fixed.log`: primeras tres pruebas aprobadas tras la reparación.
- `session-writer-validation.log`: validación ampliada.
- `session-writer-final-ui.log`, `session-writer-architecture.log` y `session-writer-build.log`.
- `session-writer-captures/`: celdas del driver con datos de prueba.

No se usaron credenciales ni un modelo remoto para acreditar estas pruebas. No
se inició WSL ni se acredita una nueva ejecución remota de CI en esta entrega.
