# ADR-0002 — Event Store en SQLite desde M1; vectorial después con sqlite-vec

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §57, §59, §94 (M9), INV-012

## Contexto

La spec usa `InMemoryEventStore` hasta M9. Eso impide inspeccionar, comparar o reanudar ejecuciones reales durante M2–M8. Se evaluó JSONL y se propuso una base vectorial para conservar consultas indexadas sin perder ligereza.

Una base vectorial resuelve similitud semántica, no consultas exactas y ordenadas (replay, "eventos desde seq N", "por lane"), y exigiría un modelo de embeddings por evento y, en la mayoría de opciones, un servidor aparte.

## Medición (2026-09-24, esta máquina, .NET 10, Microsoft.Data.Sqlite, WAL + synchronous=NORMAL)

100 000 eventos de ~448 bytes en 100 sesiones:

| Operación | SQLite | JSONL |
|---|---|---|
| Escritura por evento | ~80–100 µs (≈10–12 k ev/s) | ~5 µs |
| Replay de sesión (1000 ev) | 0.7–3.3 ms | scan completo |
| Resume desde seq 900 | 0.07–0.23 ms | scan completo |
| Filtro por tipo / lane | 0.4–0.6 ms | ~10 ms (crece lineal) |
| Apertura | 13 ms (133 ms en frío) | — |
| Dependencia nativa | `e_sqlite3.dll` 1.9 MB | — |

Un agente produce como mucho cientos de eventos canónicos por segundo; la escritura tiene ~100× de margen.

## Decisión

1. `SqliteEventStore` es la implementación durable **desde M1**, detrás de `IEventStore` (Abstractions). El Engine nunca conoce SQLite.
2. `InMemoryEventStore` se mantiene para tests.
3. Tabla `events` con `id INTEGER PRIMARY KEY`, índice único `(session_id, seq)` e índices `(session_id, type)` y `(session_id, lane_id)`. Payload JSON en columna `TEXT`. No usar `WITHOUT ROWID` (filas grandes lo penalizan).
4. WAL + `synchronous=NORMAL`: durable ante caída del proceso; ante corte de energía se pueden perder los últimos commits, aceptable para un runtime local.
5. Eventos de alta frecuencia (tokens en streaming, heartbeats intermedios) **no** se persisten uno a uno: se coalescen (§63).
6. Búsqueda vectorial: **después** (M8+, memoria/RAG vía `IContextContributor`) con la extensión `sqlite-vec` en el mismo motor. Solo se vectoriza contenido curado (checkpoints, decisiones, resúmenes de `AgentResult`, artifacts), nunca eventos crudos. Verificar disponibilidad del paquete .NET al llegar.

## Consecuencias

- M9 deja de introducir persistencia; se enfoca en read models, `ContextSnapshot` persistido y protocolo.
- Un archivo `.omnicore/omnicore.db` por workspace (ubicación final a definir en M1).
- Exportar a JSONL queda como comando de diagnóstico opcional, no como formato canónico.
