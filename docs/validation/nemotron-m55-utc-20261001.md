# Validación M5.5 Phase A — Bug 1: UTC Envelope Timestamps

**Fecha:** 2026-10-01
**Branch:** `codex/nemotron-m55-utc`
**Base:** `2e3277c`
**Modelo/Provider:** OpenRouter `nvidia/nemotron-3-ultra-550b-a55b:free`

---

## Resumen del cambio

Se corrigió el **Bug 1** del análisis pre-M6 (`docs/architecture/gap-pre-m6.md`): *"Timestamp no UTC en el envelope (ADR-0001)"*.

**Cambio único en producción:**
- `src/OmniCore.Domain/Events.cs`: `DomainEvent.Create` ahora usa `DateTimeOffset.UtcNow` en lugar de `DateTimeOffset.Now`.

**Nuevos tests de regresión:**
- `tests/OmniCore.Tests/UtcEnvelopeTests.cs` (9 tests)

---

## Criterios de aceptación verificados

### (1) `DomainEvent.Create` produce timestamps UTC con offset cero e instante correcto

| Test | Verificación |
|------|--------------|
| `DomainEvent_Create_produces_utc_timestamp_with_zero_offset` | Offset = `TimeSpan.Zero`; instante entre `before` y `after` (±1s) |
| `DomainEvent_Create_multiple_events_have_monotonic_timestamps` | Secuencia monótona; todos con offset cero |
| `EventStream_Append_produces_utc_timestamps_via_DomainEvent_Create` | EventStream → BuildEnvelope → Create → UTC |

### (2) EventStream/SQLite persiste y preserva UTC, IDs, secuencia y orden tras close/reopen

| Test | Verificación |
|------|--------------|
| `Sqlite_round_trip_preserves_utc_timestamp_and_envelope_fields` | Escritura → close → reopen → lectura: timestamp, EventId, Sequence, Type, SchemaVersion, Causation, CorrelationId, RunId, TaskId, LaneId, TurnId, PlanItemId, ToolCallId, PayloadJson idénticos |
| `Sqlite_timestamps_are_culture_invariant_on_write_and_read` | Escritura con `es-MX`, lectura con `en-US`: instante y offset idénticos |
| `Sequence_and_ordering_preserved_after_close_reopen` | Dos escrituras en procesos simulados → secuencia 1..5 contigua, orden conservado, offsets cero |

### (3) `DomainEvent.Stored` y replay histórico preservan offset e instante no-cero sin reescritura ni migración

| Test | Verificación |
|------|--------------|
| `DomainEvent_Stored_preserves_supplied_nonzero_offset_and_instant` | Offset `-05:00` suministrado → leído idéntico |
| `DomainEvent_Stored_preserves_utc_timestamp_exactly` | UTC histórico → leído idéntico |
| `Sqlite_round_trip_preserves_historical_nonzero_offset_without_migration` | Inserción directa con offset `-03:00` → lectura preserva offset e instante **sin conversión a UTC** |

---

## Evidencia de ejecución

```
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --filter "FullyQualifiedName~OmniCore.Tests.UtcEnvelopeTests"
  total: 9, correcto: 9, error: 0, omitido: 0

dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj
  total: 1069, correcto: 1065, omitido: 4 (symlinks), error: 0

dotnet test tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj
  total: 52, correcto: 52, error: 0
```

---

## Archivos modificados

| Archivo | Tipo | Descripción |
|---------|------|-------------|
| `src/OmniCore.Domain/Events.cs` | **Producción** | `DateTimeOffset.Now` → `DateTimeOffset.UtcNow` en `DomainEvent.Create` |
| `tests/OmniCore.Tests/UtcEnvelopeTests.cs` | **Tests nuevos** | 9 tests enfocados (UTC, SQLite round-trip, replay histórico, cultura, orden) |
| `docs/validation/nemotron-m55-utc-20261001.md` | **Documentación** | Este reporte de validación |

---

## Limitaciones y alcances

- **No se tocó:** `DateTimeOffset.Now` en otros sitios (timeouts, deadlines, auditoría, `Projections.cs`, `SimulationEngine.cs`, `LocalModelHost.cs`, `ExplorerTurn.cs`, `MaintenanceCommandHost.cs`, `ScriptedToolExecutor.cs`, `SystemProcessRuntime.cs`) — son usos operacionales legítimos, no timestamps de envelope.
- **No se implementó:** reloj abstraído (`IClock`), upcasters de timestamp, migración de journal — fuera del alcance del Bug 1.
- **No se cerró M5.5:** este fix cubre solo el Bug 1 de 9 (fase A). Los Bugs 2–9 y la fase B (contratos) quedan pendientes.

---

## Conformidad con ADRs

- **ADR-0001 §3:** `Timestamp` en envelope = UTC ✔
- **ADR-0001 §5:** Estado en eventos, inmutabilidad de artifacts — preservada ✔
- **ADR-0013:** Envelope con causación, correlación, IDs de entidad — round-trip verificado ✔
- **ADR-0036:** Actividad de Lane derivada, no persistida — no afectada ✔
- **ADR-0039:** Configuración fuera del repo, tests en paths temporales — respetado ✔
- **ADR-0040:** Textos visibles en español; prompts al modelo en inglés — respetado ✔

---

## Commit propuesto

```bash
git add src/OmniCore.Domain/Events.cs tests/OmniCore.Tests/UtcEnvelopeTests.cs docs/validation/nemotron-m55-utc-20261001.md
git commit -m "fix: UTC timestamps in DomainEvent.Create envelope (M5.5 Bug 1)

- DomainEvent.Create now uses DateTimeOffset.UtcNow (ADR-0001 §3)
- New UtcEnvelopeTests verify: UTC creation, SQLite round-trip preservation,
  historical replay with nonzero offsets, culture invariance, sequence ordering
- All 1117 tests pass (1065 + 52 arch, 4 skipped symlinks)"
```