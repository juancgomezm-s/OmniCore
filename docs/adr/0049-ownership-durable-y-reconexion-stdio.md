# ADR-0049: Ownership durable por Session y recuperación stdio de M9-B

**Estado:** Aceptada para el bloque M9-B, pendiente de revisión e integración en `main`  
**Fecha:** 2026-10-14

## Contexto

M9-A ya separa el Host mediante stdio y reusa el journal SQLite. Con dos procesos, la protección
local de `EventStream` no evita proyecciones de escritura concurrentes, doble ejecución tras una
respuesta perdida, ni replay incompleto al purgar o paginar un historial grande. El contrato wire
no puede inventar autoridad humana ni confundir un recibo ambiguo con una operación segura de
reintentar.

## Decisión

### Writer ownership

Un `SqliteEventStore` adquiere un lock de sistema operativo por `SessionId`, en un path permanente
adyacente al journal. La plataforma abre el archivo con sharing exclusivo; el archivo nunca se
elimina ni reemplaza mientras otros procesos puedan haber abierto ese path. El lease no expira por
reloj y se libera al cerrar el store o terminar el proceso. Si el owner muere, el SO libera su handle
y un nuevo Host puede adquirir el mismo archivo.

El Host adquiere ownership antes de recuperación que muta estado, de cada command escritor y de los
writers directos de mantenimiento/worktree/undo. Una ruta reader→writer vuelve a leer el puntero de
sesión bajo ownership y reconstruye su proyección. Si otro proceso ya posee la Session, los queries
siguen disponibles, el startup evita recovery escritor, y la mutación devuelve busy/falla cerrada.
El rollback de una adquisición múltiple sólo libera handles nuevos, nunca locks que el mismo store
ya tenía. Los journals SQLite mantienen sus límites de schema durable; `ProtocolVersion` continúa
siendo una dimensión wire independiente (ADR-0013).

### Receipt y resultado ambiguo

Antes de ejecutar un command wire soportado, el Host confirma en SQLite un receipt con ID y hash
SHA-256 del payload. La tabla es workspace-journal scoped y el ID tiene unicidad global dentro de
ese journal; el receipt no almacena el texto del command ni secretos. Al finalizar, registra sólo
status/outcome tipados, secuencias y la Session real que creó o utilizó. Un ID con otro hash es
rechazado. Un ID con receipt Completed devuelve el ack guardado sin volver a ejecutar.

Una reserva que persiste sin finalización se trata como Unknown. Nunca se despacha otra vez de forma
automática: el proceso puede haber muerto antes de cualquier efecto, entre commits de eventos o
después de un efecto externo. `command.result` permite consultar el hash/ID, pero no convierte
Unknown en éxito. Esto evita doble ejecución, aunque no crea transacción atómica entre journal,
efectos externos y ACK, ni promete exactly-once universal. Los receipts sobreviven a la purga de
eventos y mantienen la protección del ID; los tombstones de Session conservan high-water para
detectar que un cursor ya no tiene historia completa.

### Replay y Client

La operación legacy `subscribe` conserva el comportamiento 1.0 de full replay o error. La nueva
capability `events.replay.page` separa el paging del contrato anterior: cada request nombra Session,
cursor, page size bounded y opcional high-water. El primer SQL snapshot fija high-water; todas las
páginas siguientes lo reutilizan, consultan con `LIMIT`, e informan gap ante secuencias perdidas,
purga o cursor incompleto. El Host reduce el número de filas hasta que el envelope final cabe en el
límite UTF-8 de 1 MiB; un evento indivisible que exceda el frame produce `frame_too_large`.

El Client conserva la negociación: pagina sólo con `events.replay.page`, cae a legacy `events.replay`
cuando la versión 1.0 remota sólo ofrece ese contrato y rechaza explícitamente operaciones no
anunciadas. Reconnect hace lookup del receipt completado y luego replay desde la Session/cursor
reportados; no reenvía commands para “probar” si se ejecutaron. Un Host sin Session responde replay
vacío y Complete.

## Seguridad y autoridad

El stdio command genérico no implementa el equivalente externo de `ITrustedUserActionClient`. No
acepta un booleano “human”, una capability auto-declarada por renderer o un campo del payload como
prueba de persona autorizada. Las operaciones que necesiten origen humano externo se mantienen
rechazadas. La capability sólo anuncia handlers construidos para el backend actual.

## Consecuencias y límites

- Conserva rutas in-process/TUI, el wire 1.0 legacy y compatibilidad multi-target net8/net10.
- El lock depende de sharing-lock semantics del sistema operativo y almacenamiento local; el bloque
  se ha probado en Windows. Linux, macOS y network filesystems no están verificados.
- `SubscribeSince` por páginas es replay request/reply, no push; no agrega stream de eventos, heartbeat
  de lease, supervisor Electron ni binding TypeScript.
- Unknown es recuperable como incertidumbre explícita, no como resultado final. No se presume atomicidad
  de operaciones de proveedor/archivos, ni exactamente-una-vez para todo el runtime.
- `media.read`, procesos persistentes/ConPTY, autorización humana externa y aceptación multi-OS quedan
  fuera de M9-B. M9 permanece abierto.

## Evidencia

Implementación y pruebas focales: [acta M9-B del 2026-10-14](../validation/m9-block-b-20261014.md).
