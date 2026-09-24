# ADR-0013 — Versión de schema de evento separada de la versión del Omni Protocol

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §55, §61, §62
- **Diagrama:** [arquitectura §5](../architecture/arquitectura.md#5-event-schema-vs-omni-protocol)

## Contexto

`EngineEvent.ProtocolVersion` (spec §55) mezcla dos cosas: el schema durable de un evento persistido y el protocolo wire que usan CLI, Host y clientes. Una evolución del CLI o del transporte stdio no debería migrar el Event Store, y una migración interna del schema no debería forzar un cambio del protocolo.

## Decisión

### 1. Dos versionados independientes

| Capa | Identidad | Dónde vive | Cambia cuando |
|---|---|---|---|
| **Evento de dominio (durable)** | `EventType` (string estable, p. ej. `run.started`) + `EventSchemaVersion` (entero por tipo) | Domain; columnas `type` y `schema_version` en `events` | Cambia la forma persistida de ese tipo de evento |
| **Mensaje wire** | `ProtocolVersion` (`major.minor`) + `MessageType` (`event`, `command`, `response`, `error`, `hello`) + payload serializado | Protocol (DTOs propios) | Cambia lo que ven los clientes |

`EngineEvent.ProtocolVersion` **desaparece** del evento de dominio.

### 2. Flujo

```text
DomainEvent (schema vN) --[upcasters al leer]--> DomainEvent (schema actual) --[ProtocolMapper, en Host]--> WireEvent (protocolo negociado)
```

- **Upcasters:** `IEventUpcaster(EventType, fromVersion → toVersion)` se aplica **al leer**. El Event Store nunca se reescribe (append-only, ADR-0001).
- **Serialización:** JSON con System.Text.Json y source generation. La columna `type` actúa como discriminador; no se depende de nombres de tipos .NET.
- **Wire:** DTOs **separados** de los records de dominio, en `OmniCore.Protocol`. El mapper decide qué se expone; no todo evento de dominio tiene representación wire. Por ejemplo, `ProviderOpaque` nunca se expone.
- **Negociación:** el cliente envía `hello { supportedVersions }` y el Host elige la mayor versión común.
  - Dentro de un mismo `major`, un `minor` nuevo solo agrega campos o tipos opcionales.
  - Los clientes ignoran lo desconocido.

### 3. Reglas de evolución

| Cambio | Event Store | Protocolo |
|---|---|---|
| Campo nuevo opcional en un evento | `EventSchemaVersion`+1, upcaster trivial | Sin cambio; o `minor`+1 si se expone |
| Evento de dominio renombrado o reestructurado | Nuevo `EventSchemaVersion` + upcaster | Sin cambio si el mapper mantiene el wire |
| Nuevo formato de mensaje wire, framing o transporte | Sin cambio | `ProtocolVersion` +1 |
| Evento nuevo solo interno | Nuevo `EventType` | Sin cambio (no se mapea) |

## Precisiones (revisión integral, 2026-09-24)

- **Nombres de tipos:** el evento durable es `DomainEvent`; en el protocolo, `WireEvent` y `WireCommand`. `EngineEvent` (spec §55) desaparece como nombre.
- **`EventType`:** en minúsculas con puntos, derivado 1:1 de los nombres de la spec (`RunStarted` → `run.started`, `PlanItemCompleted` → `plan_item.completed`).
- **`CorrelationId`:** siempre es el `RunId`. El `TurnId` tiene su propio campo.
- **`CausationId`:** es un id tipado `EventId | CommandId`, y todo `WireCommand`/`CommandAck` lleva `CommandId`.
- **DTOs wire:** todos viven en `OmniCore.Protocol` con **sus propios tipos de id** (string) y enums propios. Nunca referencian Domain; `ProtocolMapper` (Host) los traduce.

## Clasificación

| Elemento | Categoría |
|---|---|
| `EventType` + `EventSchemaVersion` en el envelope y columnas, registro de upcasters (vacío en M1) | **Necesario desde M1** |
| Wire DTOs y `ProtocolMapper` para el transporte in-process (mínimo en M1; ADR-0019), negociación `hello` (M9) | **Contract now / implementation later** |
| Compatibilidad con JSON-RPC u otros framings | **Deferable** |
