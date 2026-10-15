# ADR-0054: Flujo continuo y backpressure de eventos stdio

**Estado:** Aceptada para M9-H.
**Fecha:** 2026-10-15

## Contexto

El replay paginado de M9-B permite recuperar el journal durable, pero OmniCoder solo observa cambios
al consultar otra página. Renombrar ese polling como push no satisface la actualización durante un
Run. Tampoco es válido retener eventos en una cola sin límite, bloquear commands/cancelación detrás
de un consumidor lento o confirmar recepción antes de que el consumidor aplique el evento.

## Decisión

Negociar opcionalmente `events.stream.v1` dentro de stdio 1.0. El consumidor abre una suscripción
para una Session y un cursor; el Host entrega páginas durables del journal seguidas por eventos
nuevos, sin esperar que termine el Run. `events.ready` confirma que la suscripción fue aceptada;
`nextSequence` y `highWater` se reportan en los controles de stream correspondientes. Cada frame
de evento se identifica por stream y Session y solo se confirma mediante ACK después de consumirlo.
Las páginas no tienen por qué exponer secuencias contiguas: el mapper puede filtrar eventos internos,
pero siempre avanza hasta `page.nextSequence`, incluso si no produjo frames visibles.

La ventana de créditos y el tamaño de frame se limitan en Host y Client. El Host deja de leer el
journal cuando no hay crédito; el journal, no una cola RAM, conserva los eventos. La lectura se
reanuda desde el cursor confirmado al devolver crédito. Si el cursor ya fue purgado o el journal no
puede completar el intervalo, se emite un diagnóstico de gap con cursor solicitado, siguiente
disponible y high-water. El consumidor reconcilia desde ese límite sin afirmar que recuperó el
historial perdido. ACK futuro, de otro stream/Session o mayor al frame efectivamente emitido no
añade créditos; ACK repetido no duplica crédito.

Un escritor único serializa respuestas y eventos stdout para que los frames no se intercalen. Las
queries y `run.cancel` conservan prioridad funcional: la ventana llena suspende únicamente la
lectura/emisión de eventos, no mantiene locks de mutación ni bloquea el procesamiento de comandos.
Stop/cancel hace join acotado del pump antes de confirmar detención o reutilizar el Host. Cada
suscripción tiene identidad y estado propios para impedir que una generación vieja avance cursor o
escriba frames de la nueva.

Conexión perdida o EOF descarta el estado volátil de transporte. Cuando el supervisor restablece la
conexión, el consumidor puede abrir una suscripción nueva desde el cursor durable de la misma
Session; el transporte no promete reconectar automáticamente. Nunca reenvía commands con resultado
ambiguo `Unknown` ni renueva prueba humana.
Cursor y ACK se cercan por conexión, workspace, generación, stream y Session. El consumidor .NET y
OmniCoder TypeScript prefiere stream cuando se negocia y conserva replay paginado solo si falta la
capability. El comando `omni stdio watch` requiere la capability y reporta el error si falta; no
ofrece fallback automático. La TUI in-process no consume este stream. OmniCoder aplica y proyecta el estado canónico en Main,
coalesce notificaciones IPC con una sola actualización pendiente, y confirma al Host solo tras
terminar el consumo. La UI de OmniCoder refleja esos eventos mientras el Run sigue activo.

El alcance es entrega de eventos canónicos durables: no introduce streaming de tokens del proveedor,
TTFT, contadores de uso/coste ni métricas inventadas. Se conservan stdio 1.0 legacy, DTOs/capabilities
opcionales, Protocol/Client net8+net10 y límites de presentación de ADR-0013/0019/0030/0038/0049–0051.

## Criterios de aceptación

- Un proceso Host real negocia stream y legacy; evento durable llega antes de que termine un Run.
- Paginación replay→live no pierde ni duplica eventos; Unicode y frames de hasta 1 MiB funcionan y
  los frames oversized se rechazan acotadamente.
- Ventana llena con ACK retenido no acumula memoria ni bloquea query/cancel; ACK inválido no cambia
  créditos; stop→rebind en el mismo Host no mezcla generaciones.
- Reapertura y una nueva suscripción tras reconectar reanudan la misma Session/cursor sin reejecutar
  un command ambiguo; gap y purga se muestran como historial incompleto. No se acredita
  reconexión automática ante EOF o muerte del Host.
- Electron→Host .NET→provider fixture local actualiza chat antes de la respuesta final y conserva la
  proyección tras reabrir el perfil aislado.

## Consecuencias y límites

- La garantía de durabilidad viene del journal; el canal ofrece entrega reanudable, no exactly-once
  general frente a clientes que no conservan cursor.
- Si no se negocia la capability, los clientes mantienen la ruta paginada existente sin anunciar
  streaming.
- La ventana acota frames no consumidos; el tamaño de cola de presentación también debe permanecer
  acotado antes del ACK.
- La validación de esta entrega es Windows. Linux/macOS, paridad de idiomas y aceptación visual
  manual siguen abiertos.
