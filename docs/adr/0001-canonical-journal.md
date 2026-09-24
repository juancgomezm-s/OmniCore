# ADR-0001 — Canonical Journal: Event Store + artifacts inmutables

- **Estado:** Aceptada — rev. 2 (2026-09-24)
- **Rev. 1:** "los eventos son la única fuente de verdad". Era incompleta: ContextSnapshots, outputs de modelo y tools, patches y estado opaco de providers se externalizan a artifacts, y también forman parte de la historia.
- **Spec:** §5–§12, §29, §40, §55–§59, INV-006, INV-008, INV-009
- **Diagrama:** [arquitectura §4](../architecture/arquitectura.md#4-canonical-journal)

## Decisión

### 1. Definición

```text
Canonical Journal = Event Store append-only (índice temporal y causal)
                  + Artifact Store inmutable y content-addressed (contenido referenciado por eventos)
```

**Regla central: estado en eventos, contenido en artifacts.**

- **Eventos:** pequeños y estructurados. Contienen todo lo necesario para reconstruir *estado*: Session, Run, Plan, TaskGraph, Lane, Turn, ciclo de vida de ToolCalls y permisos.
- **Artifacts:** contenido voluminoso u opaco, referenciado por `ArtifactRef` desde un evento. Incluye respuestas completas de modelo, ContextSnapshots, outputs de tools y procesos, patches y `ProviderState`.
- **Consecuencia:** perder o corromper un artifact **nunca** impide reconstruir estado. Solo degrada la inspección o el replay de contenido.

### 2. Qué se reconstruye desde el Journal

| Proyección | Fuente | ¿Requiere artifacts? |
|---|---|---|
| Session, Run, TaskGraph, Lane, Turn, Plan | eventos | No |
| WorkingState (ADR-0016) | eventos | No |
| Read models / UI (`/plan`, `/tasks`, `/lanes`) | eventos | No |
| Context projections (qué recibió cada Turn) | eventos + `ContextSnapshot` | Sí |
| Replay / debugging de un Turn | eventos + snapshot + respuesta + outputs | Sí |

Replay significa **re-proyectar** lo registrado; nunca se vuelve a inferir con el modelo.

### 3. Envelope de evento de dominio

| Campo | Notas |
|---|---|
| `SessionId`, `Sequence` | `Sequence` es por sesión, `long`, monotónica y sin huecos; empieza en 1 |
| `EventId` | UUIDv7 (`Guid.CreateVersion7`) |
| `EventType`, `EventSchemaVersion` | versión del schema durable, no del protocolo (ADR-0013) |
| `Timestamp` | UTC |
| `CausationId` | evento o comando que lo originó (índice causal) |
| `CorrelationId` | Run o Turn al que pertenece la cadena causal |
| `RunId?`, `TaskId?`, `LaneId?`, `TurnId?`, `PlanItemId?`, `ToolCallId?` | según el evento |
| `ArtifactRefs[]` | lista explícita, para indexar referencias sin parsear el payload |
| `Payload` | JSON |

- Un único escritor por sesión asigna `Sequence` (ADR-0002).
- Los cambios de estado se validan contra la máquina de estados **antes** de emitir el evento, así que un evento persistido nunca es inválido.

### 4. `ArtifactRef` y `ContentHash`

```csharp
public sealed record ArtifactRef(
    ArtifactId Id,          // identidad lógica (UUIDv7) con metadata propia
    ContentHash Hash,       // identidad física del blob
    long Size,
    string MediaType,
    ArtifactKind Kind,
    Sensitivity Sensitivity);

public readonly record struct ContentHash(string Algorithm, string Value); // "sha256", hex en minúsculas
```

Varios `ArtifactId` pueden apuntar al mismo blob, lo que da deduplicación. La metadata lógica (kind, sensibilidad, evento creador) es por artifact; el contenido es por hash.

### 5. Almacenamiento content-addressed

- **Rutas:** los blobs viven en `blobs/sha256/<2 hex>/<2 hex>/<hash>` dentro del directorio de datos del workspace (`%LOCALAPPDATA%\OmniCore\workspaces\<WorkspaceId>\`, ADR-0022 §3).
- **Escritura:** archivo temporal → hash en streaming → flush + fsync → rename atómico. Si el blob ya existe, se verifica el tamaño y se reutiliza.
- **Metadata** en el mismo SQLite del Event Store:
  - `artifacts(artifact_id, hash, size, media_type, kind, sensitivity, created_session, created_seq, state)`
  - `artifact_refs(artifact_id, session_id, seq)`
- **Orden obligatorio:** primero el blob durable, después el evento que lo referencia. Nunca se persiste un evento con una referencia a un blob no escrito.

### 6. Inmutabilidad

- Un blob se escribe una sola vez y nunca se modifica; su nombre **es** su hash.
- La metadata de un artifact es inmutable salvo `state` (`Available → Redacted | Purged`). Ese campo solo cambia por evento canónico.

### 7. Integridad y detección de corrupción

- Toda lectura verifica el hash en streaming. Un mismatch da `ArtifactCorrupted` y un blob ausente da `ArtifactMissing`; ambos son errores tipados (spec §71).
- `omni doctor --verify-journal` recorre `artifact_refs`, verifica existencia y hash, y lista las sesiones con contenido degradado.
- Las proyecciones de contenido se marcan `Degraded` en lugar de fallar el Run. El estado nunca depende de artifacts (§1).

### 8. Retención y garbage collection

- **La unidad de retención es la Session.** Mientras la sesión exista, todo artifact referenciado por sus eventos se conserva. **Un artifact referenciado por historia canónica no puede desaparecer arbitrariamente.** Hay dos únicas salidas explícitas:
  - **Evento `ArtifactRedacted`:** elimina el contenido por seguridad, por ejemplo si se filtró un secreto. Conserva hash y metadata, y las lecturas devuelven `Redacted`.
  - **`omni session purge <id>`:** borra la sesión completa, eventos y referencias. Sus artifacts quedan sin referencias y el GC los recoge. Los registros de **auditoría** de esa sesión (metadata redactada) **sobreviven** a la purga (ADR-0043).
- **GC mark-and-sweep:**
  - El conjunto vivo es todo lo referenciado por alguna sesión retenida, más las escrituras en curso protegidas por un lease.
  - Se borran los blobs fuera del conjunto vivo con más antigüedad que un periodo de gracia (24 h por defecto).
  - Nunca se borra un blob referenciado.
- **Blobs huérfanos** (escritos pero sin evento confirmado por un crash): se recolectan tras la gracia.

### 9. Sensibilidad

- `Sensitivity = Normal | Sensitive`. Son `Sensitive` el `ProviderState` opaco (ADR-0005) y los outputs marcados por la política de secretos (ADR-0018).
- El contenido `Sensitive` nunca va a logs ni a telemetría.

## Clasificación

| Elemento | Categoría |
|---|---|
| Envelope, `Sequence`, `CausationId`/`CorrelationId`, tipos `ArtifactRef`/`ContentHash`, regla estado-en-eventos | **Necesario desde M1** |
| CAS en disco (versión simple en M2), GC (M4), `verify-journal` (M4), `ArtifactRedacted`, cifrado en reposo de `Sensitive` | **Contract now / implementation later** |
| Deduplicación entre workspaces | **Fully deferable** |
