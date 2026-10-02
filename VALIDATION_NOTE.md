# Validation Note: M5.5 Phase A - Bug 4: Preservar la continuación opaca dentro del Turn

## Summary
Implemented fix for Bug 4 from `docs/architecture/gap-pre-m6.md`: `ExplorerTurn` now preserves opaque provider state (`ProviderState`) across model steps within a Turn, and preserves all assistant content blocks (reasoning, ProviderOpaqueBlock) instead of reconstructing only tool-call blocks.

## Changes Made

### `src/OmniCore.Host/ExplorerTurn.cs`

1. **Added continuation tracking variable** (line 260):
   ```csharp
   ProviderState? continuation = null;
   ```

2. **Pass continuation in ModelRequest** (line 284):
   ```csharp
   new ModelRequest(..., continuation);
   ```

3. **Capture continuation from response** (lines 329-332):
   ```csharp
   // Capture continuation state for the next step within this Turn.
   // Scoped to this Ask/Turn; not persisted to journal/artifacts.
   continuation = resolved.State;
   ```

4. **Preserve full assistant message** (lines 346-354):
   - Added all content blocks from model response (reasoning, opaque, tool calls) as a single assistant message
   - Uses new `RedactContentBlock` helper to properly redact while preserving ProviderOpaqueBlock unchanged

5. **Batch tool results** (lines 356-358, 557-560):
   - Execute all tool calls, collect results
   - Add single tool message with all ToolResultBlocks
   - Removed per-tool-call assistant message reconstruction

6. **Fixed user.ask handling** (lines 526-531):
   - No longer adds duplicate assistant message (already added above)
   - Only adds tool result message

7. **Added RedactContentBlock helper** (lines 1342-1361):
   - Single-block redaction preserving ProviderOpaqueBlock unchanged
   - Refactored RedactMessage to use this helper

### `tests/OmniCore.Tests/ContinuationPreservationTests.cs` (new file)

Added 5 focused tests:
1. **Continuation_state_is_forwarded_to_next_step_within_same_turn** - Verifies ProviderState flows to next step
2. **Reasoning_blocks_are_preserved_in_assistant_message** - Verifies ReasoningBlock preserved in conversation
3. **Continuation_does_not_leak_to_next_turn** - Verifies Turn-scoped isolation (new Turn starts fresh)
4. **Tool_semantics_unchanged_multiple_tool_calls_batched** - Verifies multiple tool calls in one response are batched correctly
5. **Assistant_message_preserves_block_order_and_types_in_next_step** - **Focal audit test**: Captures next step's ModelRequest and verifies type, identity/content, and order of ReasoningBlock, ProviderOpaqueBlock, ToolCallBlock; verifies associated tool result for ToolCallId

## Test Results

| Test Suite | Passed | Failed | Skipped |
|------------|--------|--------|---------|
| M2IntegrationTests | 34 | 0 | 0 |
| ArchitectureTests | 52 | 0 | 0 |
| ContinuationPreservationTests | 5 | 0 | 0 |
| Full OmniCore.Tests (independent rerun) | 1106 | 0 | 4 (symlink) |

The earlier agent run recorded one intermittent file-lock failure. An independent rerun after the focal audit test was added completed with 0 failures; do not treat the earlier intermittent failure as a stable pass/fail baseline.

## Acceptance Criteria Verification

✅ **Continuation forwarding**: `ModelResponse.State` captured and passed as `ModelRequest.Continuation` to next step within same Turn

✅ **Assistant block preservation**: Full `resolved.Content` (reasoning, opaque, tool calls) added as assistant message via `RedactContentBlock`

✅ **Block order and identity preservation (audit focal)**: Verified that ReasoningBlock, ProviderOpaqueBlock, and ToolCallBlock appear in correct order with correct content/identity in the next step's ModelRequest

✅ **Tool result association**: Verified that ToolResultBlock for the captured ToolCallId is present with correct content

✅ **No Turn leakage**: `continuation` variable is local to `Ask()` method, reset on each new Turn invocation

✅ **No persistence**: Continuation state never written to journal/artifacts/logs - only held in memory during Turn execution

✅ **Tool semantics unchanged**: Multiple tool calls batched correctly; user.ask suspension works; all existing M2 tests pass

✅ **Minimal patch**: Only modified `ExplorerTurn.cs`; no adapter, event schema, or contract changes

## Commands Run

```bash
dotnet restore
dotnet build OmniCore.slnx --no-restore
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --filter "FullyQualifiedName~M2IntegrationTests" --no-restore
dotnet test tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --filter "FullyQualifiedName~ContinuationPreservationTests" --no-restore
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
git diff --check
```

The independent audit reran `ContinuationPreservationTests` (5/5), `M2IntegrationTests` (34/34), `ArchitectureTests` (52/52), the full `OmniCore.Tests` suite (1106 passed, 0 failed, 4 skipped because symlink creation is unavailable), `dotnet build OmniCore.slnx --no-restore` (0 warnings/errors), and `git diff --check`; all passed.
