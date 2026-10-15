# ADR-0051: Ejecutar conversación desde input persistido en OmniCore

**Estado:** Aceptada para el bloque M9-E.  
**Fecha:** 2026-10-14

## Contexto

M9-D habilitó input humano confiable, pero su command sólo admitía el texto y abría/actualizaba un
Run; OmniCoder aún no podía usar su composer para ejecutar el pipeline real de OmniCore. Reenviar
`session.input` después de admitirlo duplicaría el efecto y no permitiría reconciliar una respuesta
perdida. Tampoco era válido presentar una admisión Accepted como respuesta final.

## Decisión

Agregar el command confiable `conversation.start` bajo la capability
`command.conversation.start.user`, manteniendo stdio 1.0 y el command genérico `session.input` sin
cambios. La ruta firmada conserva el proof M9-D y admite una sola entrada con modo y revisión
explícitos. Tras persistir el input y obtener el ACK, el Host fija `SessionId`, `RunId`, command ID,
hash y el intervalo de secuencias admitido; ejecuta la composición existente de
`OmniCliRuntime` sobre esos IDs, sin volver a enviar el input ni cambiar la preferencia User.

El ACK `Accepted` representa admisión durable, no finalización. El Host conserva un resultado de
ejecución mínimo y recuperable en el receipt asociado al mismo command, y lo sirve mediante
`conversation.result`/`conversation.result.lookup`; no persiste texto del prompt o respuesta en ese
receipt. El estado de Run y el contenido assistant siguen siendo los eventos y artifacts canónicos
del journal. Al resolver, el cliente fija/reproduce el high-water y deduplica por identidad del
evento antes de marcar el turno settled. Si el receipt queda ambiguo, el cliente consulta receipt y
replay sin reenviar el command. No hay una promesa general de exactly-once frente al proveedor.

El runtime trabaja con políticas/routing/consent existentes. Una entrada pendiente de interacción
devuelve `deferred`; fallo previo al turno devuelve un código seguro y estado terminal visible; los
errores de proveedor nunca se convierten en mensajes assistant. La clasificación
`model_tools_unsupported` puede mostrarse como diagnóstico accionable sin divulgar el mensaje crudo.
Cancelar señaliza el token y espera que el pipeline se desenrolle antes de persistir cancelación; las
consultas siguen disponibles durante la inferencia y los demás mutadores no pueden cruzar ese Run.
No se fabrican deltas, TTFT, usage ni coste a partir de una respuesta final o replay.

## Consecuencias y límites

- OmniCoder reutiliza su composer, `ChatBackendPort` y adapter existentes para un turno Core real;
  Pi permanece como backend legacy por workspace y no hay fallback silencioso.
- Este adapter sólo restaura y conversa con la Session Core actual. Crear/nombrar/compactar o elegir
  otra Session, seleccionar modelo desde el selector Pi, adjuntar archivos, ejecutar ORQ o responder
  interacciones del Host se rechaza como no soportado y no se presenta como acción operativa.
- La inferencia se ejecuta mediante providers y políticas ya configurados por el usuario. No se
  agregan credenciales ni un proveedor pagado.
- La respuesta completa se observa mediante journal/replay, no mediante push de tokens. Streaming,
  TTFT/telemetría exacta, `media.read`, autoridad humana fuera de Electron, lease coordinado con
  procesos externos, y `ConPTY` continúan pendientes.
- Los receipts son estado de reconciliación de transporte/ejecución vinculado al command, no un
  segundo journal ni prueba autónoma de finalización. `TurnCompleted`, Assistant artifact y los
  eventos terminales del Run siguen determinando el resultado visible.
- El generic wire, las rutas in-process/TUI y compatibilidad protocol/client net8/net10 se conservan.

Evidencia y límites de la entrega: [acta M9-E](../validation/m9-block-e-20261014.md).
