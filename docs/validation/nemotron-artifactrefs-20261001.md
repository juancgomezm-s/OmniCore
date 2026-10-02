# M5.5 Phase A Bug 2: ENVELOPE GENERATION HALF ONLY - Validation Report

## Summary
Fixed `EventStream.BuildEnvelope` in `src/OmniCore.Engine/EventStream.cs` to correctly extract and populate `ArtifactRefs` from typed domain event payloads. Previously, the envelope always carried an empty `ArtifactRef[]` despite payloads containing artifact references.

## Changes Made

### 1. New Helper: `src/OmniCore.Engine/ArtifactRefExtractor.cs`
- Pure, deterministic static class with `Extract(DomainEventPayload)` method
- Uses explicit pattern matching over known canonical event types (ADR-0013)
- No arbitrary JSON scanning - works on typed payload objects before encoding
- Handles scalar, optional, and list `ArtifactRef` fields
- **Deduplication rule**: References are deduplicated by `ArtifactRef.Id` (stable artifact identity) preserving order of first occurrence. This matches the envelope's purpose of providing an index without parsing the payload (ADR-0001 §3).

### 2. Modified: `src/OmniCore.Engine/EventStream.cs`
- `BuildEnvelope` now calls `ArtifactRefExtractor.Extract(payload)` before creating the envelope
- Both `Append` and `AppendBatch` paths benefit (both call `BuildEnvelope`)

### 3. Test Updates
- **New test file**: `tests/OmniCore.Tests/ArtifactRefsEnvelopeTests.cs` (24 tests)
  - Covers all event types carrying artifact refs: `RunValidationRejected`, `TurnStarted`, `ModelCompleted`, `UserInputReceived`, `AssistantMessageRecorded`, `InteractionRequested`, `InteractionResolved`, `ContextCheckpointRecorded`, `MetaModelInvocationStarted`, `MetaModelInvocationCompleted`, `TaskCompleted`, `LaneCompleted`
  - Covers events without refs: `RunCreated`, `RunStarted`, `TaskCreated`, `LaneCreated`, `ToolCallRequested`, `ToolCallSucceeded`, `PlanCreated`, `InteractionExpired`
  - Tests deduplication and order preservation
  - Tests `AppendBatch` path
  - Tests codec round-trip preservation
- **Updated existing test**: `JournalVerifierTests.Events_written_by_the_runtime_EventStream_verify_ok_without_false_positives`
  - Updated expected `ArtifactRefCount` from 2 → 4 (envelope refs + payload refs both verified)

## Events Now Populating Envelope ArtifactRefs

| Event Type | Payload Field(s) | Cardinality |
|------------|------------------|-------------|
| `run.validation_rejected` | `OutputArtifacts` | List |
| `turn.started` | `ContextSnapshotRef` | Optional scalar |
| `model.completed` | `ResponseArtifact` | Optional scalar |
| `user_input.received` | `ContentRef` | Optional scalar |
| `assistant_message.recorded` | `ContentRef` | Optional scalar |
| `interaction.requested` | `QuestionnaireSchemaRef` | Optional scalar |
| `interaction.resolved` | `AnswerRef` | Optional scalar |
| `context.checkpoint_recorded` | `CheckpointArtifact` | Required scalar |
| `meta_model.invocation_started` | `InputArtifact` | Required scalar |
| `meta_model.invocation_completed` | `OutputArtifact` | Required scalar |
| `task.completed` | `Result.ArtifactRefs` | List (via AgentResult) |
| `lane.completed` | `Result.ArtifactRefs` | List (via AgentResult) |

## Events Correctly Producing Empty ArtifactRefs
`run.created`, `run.started`, `task.created`, `lane.created`, `toolcall.requested`, `toolcall.succeeded`, `plan.created`, `interaction.expired`, and all other canonical events not listed above.

## Verification
- All 1089 existing tests pass (4 skipped due to symlink restrictions in CI environment)
- All 52 architecture tests pass (dependency graph verified)
- 24 new tests in `ArtifactRefsEnvelopeTests.cs` pass
- No changes to `SqliteEventStore.cs` or storage tests (owned by Qwen)
- No changes to Domain/UTC tests/roadmap/shared test files

## Compliance with Requirements
- ✅ Extracts references from supported typed domain event payloads
- ✅ Handles scalar/optional/list fields safely and deterministically
- ✅ Uses explicit typed matching (pattern matching), not arbitrary JSON scanning
- ✅ No secrets or artifact contents in the envelope
- ✅ Preserves order, deduplicates by stable artifact identity (`ArtifactId`)
- ✅ Documented exact deduplication rule in code comments
- ✅ Covers actual events containing response/context/questionnaire/answer/output refs
- ✅ Covers events without refs
- ✅ New test file uses real `EventStream` with `InMemoryEventStore` and schema codecs
- ✅ Production changes only in `Engine/EventStream.cs` and new `Engine/ArtifactRefExtractor.cs`
- ✅ No modifications to `SqliteEventStore.cs` or storage tests
- ✅ No modifications to Domain/UTC tests/roadmap/shared test files

## Remaining Integration Dependency
The SQLite integration test (`JournalVerifierTests.Events_written_by_the_runtime_EventStream_verify_ok_without_false_positives`) validates the combined envelope + payload artifact ref verification. This test passes with the updated expectation (4 total refs verified: 2 envelope + 2 payload). Full milestone closure requires manual validation per `docs/validation/m1-m4.md` criteria against user's local model and API key.