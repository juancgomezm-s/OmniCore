# M4 — Matriz de evidencia (Context Management + TUI v0)

Fuente de verdad de criterios: `docs/architecture/arquitectura.md` §24 (M4) y el mandato de
cierre. Esta matriz se actualiza tras cada vertical; **no** declara M4 cerrado mientras queden
verticales pendientes. Cada fila enlaza el criterio con su evidencia directa (test o código).

Regla de evidencia: solo cuentan tests que corren en verde en la suite local. El único test
omitido (`FilesystemPatchToolTests`, creación de symlink denegada en Windows) es **previo a M4**
y queda fuera de la evidencia.

## V1 — verify-journal (núcleo + CLI)

| # | Criterio | Evidencia | Estado |
|---|----------|-----------|--------|
| 1.1 | Verificación read-only del journal: nunca muta la base (intento de escritura falla) | `JournalVerifier` abre con `Mode=ReadOnly` (`JournalVerifier.OpenReadOnly`); test de bytes idénticos antes/después: `JournalVerifierTests.Verifying_does_not_modify_the_journal_bytes` | ✅ |
| 1.2 | Detección de corrupción de envelope (event_id, timestamp, causation, correlation, schema_version, ids de run/task/lane/turn/plan/toolcall) | `JournalVerifierTests.Corrupted_envelope_fields_*` (una por campo, via SQL crudo sobre la base cerrada) | ✅ |
| 1.3 | Secuencia por sesión: arranque en 1, huecos, fuera de orden, seq no entero (SQLite dinámico) | `JournalVerifierTests.Sequence_*` (resync documentado: hueco/fuera-de-orden reanuda en `found+1`; seq no entero no resincroniza) | ✅ |
| 1.4 | Payload: evento desconocido, decode fallido, schema_version divergente | `JournalVerifierTests.Unknown_event_type_*`, `Payload_decode_failed_*`, `Schema_version_mismatch_*` | ✅ |
| 1.5 | Payload mutado o de tipo cruzado detectado sin metadata por tipo | Round-trip canónico: decode con el codec del envelope → re-encode → igualdad Ordinal contra el payload persistido. `JournalIssueCode.PayloadTampered`. Racional: todo writer del runtime persiste via codecs y redacta antes de escribir (`EventStream.Append`); un writer ajeno o una mutación rompe la igualdad. Test: `JournalVerifierTests.Payload_of_a_different_type_reports_payload_tampered`, `Runtime_events_verify_ok_without_false_positives` (escritura real via `EventStream` con unicode/HTML/decimales/fechas) | ✅ |
| 1.6 | Referencias a artifacts: malformadas, hash inválido, blob ausente, contenido corrupto, tamaño divergente | `JournalVerifierTests.Artifact_ref_*` (refs del envelope por hash; refs del payload por hash+size, p.ej. `ModelCompleted.ResponseArtifact`) | ✅ |
| 1.7 | Sondeo tipado de blobs sin cargar contenido: `FileArtifactStore.BlobProbe` con `BlobStatus` (Ok/Missing/Corrupted/SizeMismatch/LinkEscape/Unreadable) | `FileArtifactStore.cs` (`BlobProbe`/`Status`), tests a nivel de store en `FileArtifactStoreTests` + mapeo a códigos en `JournalVerifierTests` | ✅ |
| 1.7b | Exclusión documentada: LinkEscape/Unreadable no se prueban E2E aquí (creación de symlinks denegada en este entorno) | Cubierto a nivel de store (probe) y mapeo de códigos; **sin** tests omitidos nuevos | ✅ |
| 1.8 | CLI `omni verify-journal [ruta] [--session] [--artifacts] [--json] [--max-issues]` con códigos de salida 0/1/2 | `src/OmniCore.Cli/JournalCommands.cs`; dispatch en `CliApp.RunAsync`; tests: `JournalCommandsTests` (9): salida OK, corrupción→1, journal ausente→1, JSON con conteos y códigos, filtro `--session`, uso incorrecto→2, `--artifacts` apuntando a la raíz del flow ask, dispatch de `CliApp`, `--max-issues` (trunca listado, cuenta total) | ✅ |
| 1.9 | `omni doctor --verify-journal` (ADR-0001 §7) delega al mismo verificador con la raíz de artifacts del flow ask | `CliApp.RunDoctor` → `JournalCommands.VerifyJournal(["verify-journal", "--artifacts", ".omnicore-artifacts"])` | ✅ |
| 1.10 | Corrupción adversarial real: SQL crudo sobre segunda conexión tras cerrar el store (simula writer roto o tamppeo en disco) | Fixture `RawSql` de `JournalVerifierTests` (respeta `ux_events_session_seq`: seq temporal 99 para swaps, DELETE para huecos) | ✅ |

### Auditoría de seguridad/cableado V1

- **Read-only:** conexión SQLite `Mode=ReadOnly`; `FileArtifactStore` construido por el CLI no
  crea directorios (solo `PutText` lo hace y el verificador jamás lo llama); test de igualdad de
  bytes.
- **Secretos:** el journal se escribe ya redactado (ADR-0018 §4, `EventStream.RedactPayload`).
  El CLI pasa el `Detail` de cada issue por `PiiRedactor` por defensa en profundidad (un error
  de parse puede incluir fragmentos del payload redactado).
- **Sin efectos ocultos:** `verify-journal` no abre el servidor, no escribe journal ni blobs, no
  toca `WorkingState`.
- **Compilación:** `dotnet build OmniCore.slnx` 0 errores; suite completa 258 verdes + 1 skip
  preexistente (2026-09-28).

## V2 — GC seguro + purga de sesión + retención de auditoría

| # | Criterio | Evidencia | Estado |
|---|----------|-----------|--------|
| 2.1 | Purga de sesión con registro de auditoría **previo** al borrado | pendiente | ⬜ |
| 2.2 | GC mark-and-sweep de blobs: live-set = hashes referenciados por eventos; gracia configurable; nunca borra referenciados; dry-run | pendiente | ⬜ |
| 2.3 | Retención de auditoría (JSONL, ADR-0043) con purga que deja traza de sí misma | pendiente | ⬜ |
| 2.4 | CLI `omni gc` / `omni audit purge` con salida verificable | pendiente | ⬜ |

## V3 — Pipeline de contexto (Prune/Externalize/Compress/Compact)

| # | Criterio | Evidencia | Estado |
|---|----------|-----------|--------|
| 3.1 | `ContextCheckpoint` + `PathRef` en Domain (spec §28) | pendiente | ⬜ |
| 3.2 | Operaciones sobre la región de conversación, nunca sobre WorkingState | pendiente | ⬜ |
| 3.3 | Umbrales por tamaño (spec §40: inline/preview/summary) configurables y con test que demuestre el cambio de comportamiento | pendiente | ⬜ |
| 3.4 | Evento durable `context.compacted` con codec + refs a artifacts | pendiente | ⬜ |
| 3.5 | `MetaModelService` (spec §22) + implementación heurística determinista | pendiente | ⬜ |
| 3.6 | Sesión larga con contexto acotado (test de techo) | pendiente | ⬜ |

## V4 — TUI v0 usable

| # | Criterio | Evidencia | Estado |
|---|----------|-----------|--------|
| 4.1 | Questionnaire tipado en Domain + overlay + formulario stdin plano (equivalencia AC-12) | pendiente | ⬜ |
| 4.2 | Sidebar con sesión/plan/archivos+diff sobre `ClientProjection` | pendiente | ⬜ |
| 4.3 | CompletionEngine para `/` y `@` | pendiente | ⬜ |
| 4.4 | Preferencias > Models + ModelPolicySetup | pendiente | ⬜ |
| 4.5 | Responsive por ancho, ThemeRole | pendiente | ⬜ |

## V5 — Criterios e2e + cierre

| # | Criterio | Evidencia | Estado |
|---|----------|-----------|--------|
| 5.1 | WorkingState sobrevive a compactación (e2e) | pendiente | ⬜ |
| 5.2 | Política borrada re-dispara onboarding (store real) | pendiente | ⬜ |
| 5.3 | Igualdad questionnaire TUI vs stdin plano | pendiente | ⬜ |
| 5.4 | Matriz final actualizada + auditoría final | pendiente | ⬜ |
