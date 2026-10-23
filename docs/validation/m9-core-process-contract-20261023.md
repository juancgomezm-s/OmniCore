# Contrato Core de procesos persistentes y publicación privada — 2026-10-23

Este documento describe el comportamiento que implementan Host, Stdio y las herramientas Core. No prescribe una interfaz de consumidor.

**Publicación comprobada:** `f02d3b5bea82ced53b2fa7ce6086023fdb80b2df` se integró
en `main` mediante fast-forward después de aprobar los cinco trabajos de la
[CI final](https://github.com/juancgomezm-s/OmniCore/actions/runs/40014136165).
El [PR #9](https://github.com/juancgomezm-s/OmniCore/pull/9) consta integrado en
ese mismo SHA. Las menciones a CI pendiente inferiores conservan el checkpoint
anterior a la publicación; no representan el resultado final. Este cierre del
bloque Core no cierra M9 global, M8 manual ni la integración de OmniCoder.

## Propiedad y ciclo de vida

Un handle es una capacidad aleatoria de 128 bits; nunca es un PID ni se acepta desde un identificador del sistema operativo. El Host lo liga a Session, Run, Lane raíz y generación de lease del escritor. `process.read`, `process.send_input` y `process.stop` comprueban esa misma identidad en preparación y ejecución. Un handle de otra Session, Run, Lane o lease se rechaza antes del efecto. Los procesos activos, detenidos sin verificar, inciertos, con límite de salida y con una propuesta sin resolver mantienen el Run retenido. Consultar no recupera ni inicia procesos.

`process.start` y los controles pasan por ToolRuntime y producen los eventos de llamada, atribución, barrera y resultado ordinarios. La acción nativa usa el comando firmado `core:process.control`; sus dos argumentos son el nombre exacto de la herramienta y su objeto JSON, y el fingerprint es SHA-256 de `omnicore-native-process-control-v1`. El payload está ligado a los campos de Session, Run, modelo, política, selección, routing y catálogo del snapshot. La aceptación del comando sólo confirma su admisión. El resultado de ejecución y su recibo terminal son evidencia separada; un resultado incierto bloquea el reenvío.

El recibo de una acción nativa completada se registra como `completed/native_tool_completed` y se correlaciona con la llamada de herramienta y su resultado durable. No se emite `NativeCommandInvocationFinished` para este camino tool-only; ese evento pertenece a los comandos que completan un Turn.

El comando puede ejecutarse mientras el Turn espera una respuesta del proveedor si el handle pertenece al Run activo y no hay otra continuación, interacción ni control en curso. También puede ejecutarse en el mismo Run quiescente cuando el modelo ya terminó su Turn, siempre que sigan vigentes la propiedad, el lease y la autoridad. Las consultas de lista y salida no crean un Turn, evento de entrada, llamada al modelo ni proceso.

## Lectura acotada y terminal

Las consultas son `process.sessions:<runId>` y `process.output:<runId>:<handle>:<afterSequence>`. La primera devuelve hasta 32 handles ordenados con estado, Lane, cursor, código de salida, pérdida de salida y capacidades actuales. La segunda valida el Run y Lane raíz, retorna chunks saneados y acotados, `lastDelivered`, `highWater`, `gap`, estado y código de salida. Un cursor sólo avanza por chunks que se entregaron. La consulta de salida nunca envía entrada, detiene un proceso ni publica una propuesta.

Después de un `RunCompleted` durable, el Host puede consultar el estado terminal y los bytes que aún conserva el buffer acotado del manager en memoria. El journal conserva el estado, el último número de secuencia, el código de salida y referencias protegidas de propuestas; no contiene el flujo completo de bytes. Si el proceso o Host se recupera y los bytes ya no están en memoria, el resultado informa estado desconocido/gap, sin inventar salida ni volver a ejecutar el proceso. Los estados `output_limit` y `unknown` permanecen sin resolver hasta una disposición verificable.

Ejemplo de una respuesta pasiva de sesiones:

```json
{
  "sessionId": "<session>",
  "runId": "<run>",
  "sequence": 84,
  "processes": [{
    "handle": "<opaque-32-hex>",
    "state": "exited",
    "runId": "<run>",
    "laneId": "<root-lane>",
    "lastOutputSequence": 3,
    "exitCode": 35,
    "outputGap": false,
    "rootLane": true,
    "canRead": false,
    "canInput": false,
    "canStop": false
  }]
}
```

Una respuesta de salida puede incluir, mientras exista el buffer en memoria, el chunk UTF-8 final `EOF-FINAL-ñ`. La respuesta posterior a un reinicio puede tener `gap: true` y `state: "unknown"`; ese estado no acredita bytes que ya se descartaron.

## Escritores privados

`process.exec`, `shell.exec` y `process.start` con `writePaths` declarados ejecutan en una copia privada creada por Host. El snapshot incluye sólo las rutas cubiertas por los claims de lectura/escritura aprobados; secretos, paths ignorados y contenido fuera de la concesión no se copian. Strong permite escribir en ese staging y no en el checkout. El Host escanea cambios, valida límites y preimágenes antes de publicar; conserva una propuesta inmutable en CAS con origen `process.exec` o `shell.exec`, claims, hash y tamaño. El lote completo se consume mediante `extension.batch.apply`, que revalida Run/Lane, origen, policy, prelectura, versiones, historia de mutación y lease antes de aplicar el lote bajo un único fence. La consulta nunca dispara la aplicación. Un ejecutable dentro del workspace se declara con ruta relativa que incluya separador (`./programa.exe`); el resolver no busca un basename desnudo en el workspace.

Una propuesta retenida es el resultado de la primera acción. El aplicador completo es una acción separada para el perfil normal; el camino autónomo sólo encadena la aplicación cuando el perfil vigente y todas las decisiones efectivas permiten hacerlo sin Ask. El journal mantiene estados de proceso y recibos de publicación separados. Sólo el receipt aplicado y las postimágenes verificadas liberan la propuesta; un fallo incierto conserva el estado y prohíbe relanzar el proceso.

## Evidencia local del corte

El build aislado del 23 de octubre incorporó `main` `916c392` y terminó sin errores ni advertencias. Los dos recorridos siguientes pasaron sobre la salida fresca de Tests, incluido su helper; la copia antigua del helper en el output de CLI no se utilizó:

- `Native_process_controls_cover_active_and_quiescent_run_then_passively_read_terminal_output`: 1/1, sin skips. Ejecuta el Host por stdio con autenticación, dos procesos Strong reales y cinco controles nativos durante y después del Turn. Comprueba recibos terminales, tres solicitudes al proveedor fixture, una sola entrada de usuario, `RunCompleted`, `RunSummaryRecorded` y salida final `EOF-FINAL-ñ` con exit 35. Las consultas posteriores no añaden eventos ni solicitudes al proveedor.
- `Strong_process_exec_publishes_one_private_batch_with_durable_file_history`: 1/1, sin skips. Recorre HostTools y ToolRuntime, proceso Strong en staging, propuesta protegida y `extension.batch.apply`; comprueba archivo ausente antes de aplicar, postimagen, historial SQLite y recibo aplicado. La propuesta deja de retener el Run sólo tras la publicación comprobada.

Luna bajo repitió ambos focos sin build: 2 seleccionados, 2 correctos, 0 fallidos y 0 omitidos. Revisó scope Session/Run/Lane, ausencia de efectos en consultas y vaciado saneado al EOF; no encontró un bloqueo nuevo. La DLL de Tests ejecutada tiene SHA-256 `6B8CC8500C2B5FA03942DB5D48B12501179DCFBEC63FAE578119DCC128C65A71`, coincidente con la entrega final congelada de construcción. Se descartaron como evidencia vigente los hashes de builds anteriores. El control de procesos huérfanos también pasó.

Esta evidencia usa respuestas HTTP fixture y efectos de sistema operativo reales; no acredita inferencia live. La CI completa del candidato sigue siendo un requisito separado antes de publicar en main.

## Límites de esta aceptación

- La lectura de salida es un buffer efímero acotado; no se afirma que el journal almacene stdout/stderr completo.
- El control de este recorrido acepta únicamente el Lane raíz. El control humano de procesos de Lane hijo queda pendiente.
- ConPTY Windows pasó localmente con AppContainer Strong real, entrada y salida por HPCON, resize y ACK `100x32`. Falta la CI del candidato corregido para acreditar la plataforma remota; no se exige ni cambia configuración del registro.
- Weak no se anuncia como ejecución persistente con árbol de procesos garantizado. La creación persistente sigue requiriendo el backend Strong.
- Este contrato Core no acredita la conexión del consumidor Electron ni cierra M9 global o el trabajo M8 manual.

## Correcciones después de la primera CI

La [CI de `3fe1ac8`](https://github.com/juancgomezm-s/OmniCore/actions/runs/40000580091) aprobó instalación y actualización npm en Windows/Linux y distribución Linux single-file/AOT. Detectó cinco fallos en la suite Linux y bloqueó Windows en ConPTY; no autorizó publicar ese SHA en main.

La causa comprobada de ConPTY era la duplicación de los streams redirigidos del padre al hijo aun con `bInheritHandles=false`. El launcher usa ahora `STARTF_USESTDHANDLES` con los tres campos estándar nulos para que Windows los conecte a HPCON. Es el caso descrito por los [mantenedores de Microsoft Terminal](https://github.com/microsoft/terminal/discussions/15814) y el patrón del [launcher productivo de Windows Terminal](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalConnection/ConptyConnection.cpp). La prueba captura primero un comando nativo y el helper sin AppContainer, y después el helper Strong, con entrada y ACK del resize exacto. También se alinearon el ciclo de vida de los extremos de pipes hasta completar `CreateProcess` y el tipo ABI `DWORD_PTR`/`nuint` del atributo. La hipótesis anterior del registro no resolvió el fallo y se retiró de la CI; ningún perfil local fue alterado.

Los demás focos corregidos pasan: upcaster `toolcall.succeeded` v4→v5 para journals anteriores; etiquetas nuevas de herramientas; rechazo canónico de mailbox por revisión de seguridad obsoleta; y respuesta de cuestionario normal y tras reinicio. La continuación de cuestionarios deriva el ToolCallId canónico del payload durable y lo contrasta con `ToolCallRequested` en el mismo Run/Turn/Task/Lane, sin exigirlo en un envelope de interacción que no lo lleva. Los fixtures arrancan la CLI fresca junto a Tests; el `source/bin` antiguo explicaba un falso fallo local posterior a la corrección, no los fallos de la primera CI, que sí compiló su CLI.

El build serial terminó con cero advertencias y errores; Tests de ese corte tiene SHA-256 `401A0C373D5FC76302700E781290BA414FEA6954450F279D5F0BBF049A7088E2`. Luna bajo validó ese mismo binario y sus dependencias sin recompilar: **8/8, cero fallos y cero omisiones**, incluidos ConPTY, ambos cuestionarios, los tres focos restantes de CI y ambos recorridos integrales de procesos/publicación. La revisión mantuvo las fronteras Session/Run/Lane y pasó el control de procesos huérfanos.

La [segunda CI, de `e8020e3`](https://github.com/juancgomezm-s/OmniCore/actions/runs/40006444811), aprobó Linux completo (5,246 casos, 40 omisiones por plataforma y cero fallos), ambos instaladores y paquetes. Windows aprobó ConPTY y 5,271 casos; su único fallo fue un bloqueo de `node.EXE` durante la limpieza de una prueba de extensiones. La reproducción focal no dejó Node activos y la inspección confirmó la barrera de cierre de job/hijos, pero detectó que la eliminación de su raíz privada ocultaba cualquier `IOException`.

El cleanup de Windows espera ahora un máximo de dos segundos exclusivamente para sharing/lock (Win32 32/33) después de confirmar el cierre del árbol; un bloqueo persistente produce `extension_runtime_cleanup_lock_timeout`. Si falla la barrera, conserva la raíz y propaga exactamente esa excepción, incluso si el cierre de stdin también falla. Las pruebas usan un bloqueo `FileShare.None` real transitorio y persistente, y una falla inyectada de control para comprobar la conservación del marcador de staging. Luna bajo aprobó los cuatro focos afectados sin recompilar: cero fallos y omisiones. Tests tiene SHA-256 `7AE17E59D11746DA01A899879FC5AEEB34C6B3E9C3DB8A8BD2CC29506EAE40ED` y Execution `7769C044DAF49859F5D6B6E1AAE44EA3B49FFD0214BD7E6E098EC81F7ABE0E6C`. La nueva CI del candidato completo sigue siendo requisito antes de publicar en main.
