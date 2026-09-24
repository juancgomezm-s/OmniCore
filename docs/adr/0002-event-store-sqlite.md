# ADR-0002 — Event Store en SQLite desde M1 y política de durabilidad

- **Estado:** Aceptada — rev. 2 (2026-09-24)
- **Rev. 1:** SQLite desde M1 con WAL + `synchronous=NORMAL`, sin distinguir qué commits deben sobrevivir un corte de energía antes de un efecto lateral.
- **Spec:** §57, §59, §94 (M9), INV-012
- **Relacionado:** ADR-0001 (Canonical Journal), ADR-0004 (Effect Journal)

## Contexto

La spec usa `InMemoryEventStore` hasta M9, lo que impide inspeccionar, comparar o reanudar ejecuciones reales durante M2–M8. Se evaluó JSONL y se propuso una base vectorial. Una base vectorial resuelve similitud semántica, no consultas exactas y ordenadas (replay, "eventos desde seq N", "por lane"), y exigiría embeddings por evento.

## Medición (2026-09-24, esta máquina, .NET 10, Microsoft.Data.Sqlite, WAL + `synchronous=NORMAL`)

100 000 eventos de ~448 bytes en 100 sesiones:

| Operación | SQLite | JSONL |
|---|---|---|
| Escritura por evento | ~80–100 µs (≈10–12 k ev/s) | ~5 µs |
| Replay de sesión (1000 ev) | 0.7–3.3 ms | scan completo |
| Resume desde seq 900 | 0.07–0.23 ms | scan completo |
| Filtro por tipo / lane | 0.4–0.6 ms | ~10 ms (crece lineal) |
| Apertura | 13 ms (133 ms en frío) | — |
| Dependencia nativa | `e_sqlite3.dll` 1.9 MB | — |

## Decisión

### 1. Almacén

1. `SqliteEventStore` es la implementación durable **desde M1**, detrás de `IEventStore` (Abstractions). El Engine nunca conoce SQLite.
2. `InMemoryEventStore` se mantiene para tests.
3. La tabla `events` tiene:
   - `id INTEGER PRIMARY KEY` (sin `WITHOUT ROWID`, que penaliza filas grandes);
   - índice único `(session_id, seq)` e índices `(session_id, type)` y `(session_id, lane_id)`;
   - columnas `type` y `schema_version` (ADR-0013) y el payload JSON en una columna `TEXT`.
4. **Un solo escritor por sesión.** Las escrituras de una sesión se serializan en un canal. Entre procesos se usa un lease de sesión en la misma base; el diseño detallado es OAQ-2.
5. Los eventos de alta frecuencia (tokens en streaming, heartbeats intermedios) no son canónicos: se coalescen (spec §63).
6. La búsqueda vectorial llega después (M8+, memoria/RAG) con `sqlite-vec` en el mismo motor, y solo sobre contenido curado.

### 2. Semántica de durabilidad

No existe transacción ACID entre el Event Store, el filesystem, Git, los procesos y los servicios externos. OmniCore garantiza **orden** y **detectabilidad**, no atomicidad distribuida.

| Clase de commit | Configuración | Garantía | Se usa para |
|---|---|---|---|
| **Standard** | WAL + `synchronous=NORMAL` | Sobrevive a la caída del proceso. Ante corte de energía o caída del SO pueden perderse los últimos commits | La mayoría de eventos: progreso, proyecciones, eventos informativos |
| **Barrier** | Commit con `synchronous=FULL` en la conexión escritora (el pragma es por conexión y se alterna alrededor de ese commit) | Sobrevive también a corte de energía, porque el WAL queda sincronizado a disco antes de continuar | Todo evento que **precede a un efecto lateral**: `ToolCallStarted` de intents con efecto, `PermissionGranted` con lifetime mayor que `Once`, `IntegrationStarted` (ADR-0021) |

- **Regla:** ningún efecto lateral se inicia antes de que su intent esté confirmado con un commit **Barrier**.
- Una pérdida de commits Standard tras un corte de energía solo puede perder *progreso observado*, nunca la constancia de que se intentó un efecto.
- Costo esperado: un fsync por efecto (~1–10 ms en NVMe), despreciable frente al costo de la tool. Se mide en M3 (detalle de implementación).
- Los outcomes (`ToolCallSucceeded` / `ToolCallFailed`) son Standard. Si se pierden, la recuperación los trata como `UnknownEffect` y reconcilia (ADR-0004).

## Clasificación

| Elemento | Categoría |
|---|---|
| `IEventStore`, `SqliteEventStore`, `InMemoryEventStore`, esquema `events` | **Necesario desde M1** |
| Commit Barrier (el contrato `DurabilityClass` en `Append` existe desde M1; su uso real llega en M3 con efectos) | **Contract now / implementation later** |
| Lease entre procesos (OAQ-2), `sqlite-vec` | **Deferable** |
