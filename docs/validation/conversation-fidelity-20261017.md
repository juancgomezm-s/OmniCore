# Conversación literal y compactación anunciada — 2026-10-17

Decisión del usuario: **automática con aviso y originales intactos**.

## Contrato

- La compactación se evalúa antes de cada solicitud, al 95 % del presupuesto de entrada efectivo o por la cadencia del perfil. El resumen se guarda como checkpoint separado.
- Un checkpoint automático confirmado produce un aviso localizado en el transcript; no abre un modal ni exige reenviar el mensaje. El aviso no se incorpora como mensaje del usuario al proveedor. Su evento no se duplica al reducirlo dos veces y se excluyen los avisos de lanes secundarios.
- Los mensajes que siguen en el contexto son indivisibles: no se abrevian ni se reemplazan argumentos o resultados para hacerlos caber. Si el contenido protegido no cabe después del resumen, la solicitud se detiene explícitamente antes de llamar al proveedor.
- El journal y el CAS privados conservan originales literales, incluidos Unicode, espacios, CRLF y ejemplos parecidos a credenciales. Esto incluye entradas en cola, correcciones durante el turno, respuestas y resultados autorizados de herramientas.
- Los filtros de diagnóstico, auditoría, errores de autenticación, permisos de herramientas y protección de rutas de secretos siguen activos. El almacenamiento protegido de credenciales y del estado opaco de proveedor no cambia el texto de la conversación.
- `conversation: {autoCompact: false}` en el `settings.yaml` de usuario permite pasar a compactación explícita. El valor predeterminado es automático; `/compactar` sigue disponible.

## Correcciones

Se separaron los payloads canónicos de los de diagnóstico en EventStream y los codecs; se conservaron los artifacts privados sin sustituciones; se eliminaron las máscaras de mensajes y resultados en el recorrido de ExplorerTurn y de la presentación de conversación. El contador de presión usa mensajes íntegros. Los candidatos a nuevos resúmenes excluyen los ya compactados: de otro modo el segundo ciclo de presión podía intentar resumir sólo elementos ya resumidos y terminar en desbordamiento.

La política de contexto y su fingerprint incluyen el modo de compactación y la nueva semántica literal, para distinguir configuraciones durante replay.

## Validación

Los casos de regresión usan SQLite, CAS y serialización HTTP reales con proveedores controlados, sin llamadas de pago. Comprueban reapertura, original frente a resumen, aviso, inputs pendientes y fidelidad del cuerpo JSON que se entrega al transporte. Los valores parecidos a credenciales son exclusivamente sintéticos.

| Comprobación | Resultado |
|---|---|
| Compilación Release | Correcta; 0 advertencias, 0 errores |
| Regresión de Core y cliente | 316 casos: 312 aprobados, 0 fallos, 4 omitidos por permisos de symlink en Windows |
| Arquitectura | 62 aprobados, 0 fallos, 0 omitidos |

La corrida ampliada incluye el escenario existente de **200 turnos** con checkpoints, conservación del estado vigente, aislamiento de contexto entre lanes, autorización de artifacts y redacción de errores de proveedor. Las pruebas de colas verifican el texto original, FIFO, idempotencia y promoción tras reabrir SQLite. Los filtros de diagnóstico siguen teniendo casos negativos independientes.

Se usó el runner oficial xUnit v3 incluido en los binarios (`-parallel none -reporter verbose -filterVSTest ...`). No se acreditan los intentos anteriores interrumpidos del lanzador `dotnet test`. Los logs y XML de la corrida efectiva quedaron en `~/.codex/omnicore-linux-20261014/conversation-fidelity-{focused,architecture}-20261017.log` y `conversation-fidelity-{focused,architecture}.xml`.

Los tests que antes exigían sustituir el texto privado ahora comprueban fidelidad literal; sus comprobaciones de permisos, integridad, aislamiento y protección de diagnósticos permanecen activas. No se deshabilitaron pruebas.

## Límites

No se pueden reconstruir textos que una versión anterior ya guardó con máscaras: la conservación literal se aplica a los mensajes nuevos. Las pruebas HTTP controladas no se presentan como una nueva inferencia en Luna u OpenRouter. No se arrancó WSL ni se cerró la TUI que el usuario tenía abierta.
