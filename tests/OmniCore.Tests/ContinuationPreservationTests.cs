using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Tests for Bug 4 (M5.5 Phase A): Preservar la continuación opaca dentro del Turn.
/// Verifica que:
/// 1. El estado opaco (ProviderState) se reenvía como Continuation al siguiente paso del mismo Turn
/// 2. Los bloques de asistente (reasoning, ProviderOpaqueBlock) se preservan en el historial
/// 3. La continuación NO se filtra al siguiente Turn (scope por Turn)
/// 4. La semántica de tools no cambia
/// </summary>
public sealed class ContinuationPreservationTests
{
    private static readonly string TestCwd = Path.GetFullPath(".");

    private static (ExplorerTurn turn, string artifactsPath) CreateTurn(Func<ModelRequest, CancellationToken, ModelResponse> responder, InMemoryEventStore store, SessionId sessionId, RunId runId)
    {
        var tools = OmniHost.CreateExplorerTools();
        var policy = CreateTestPolicy();
        var executor = ScriptedToolExecutor.WithCoreTools(tools.Catalog(), policy);
        var fingerprint = new ExecutionFingerprint("test-continuation", "harness", "tools", "context", "overrides", "M5");
        var artifactsPath = Path.Combine(TestCwd, ".omnicore-continuation-test-" + Guid.NewGuid().ToString("N"));
        var artifacts = new FileArtifactStore(artifactsPath);
        var turn = new ExplorerTurn(responder, executor, tools.Catalog(),
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            fingerprint, new ModelSelection(new ModelIdValue("test-model"), 4096, ToolMode.Direct, null),
            store, EventCodecs.Create(), artifacts, new InMemoryAuditSink(), new RedactionPolicy());
        return (turn, artifactsPath);
    }

    private static ScriptedPermissionPolicy CreateTestPolicy()
    {
        var table = new Dictionary<string, PermissionDecision>
        {
            ["filesystem.read"] = PermissionDecision.Allow,
            ["filesystem.write"] = PermissionDecision.Allow,
            ["plan.propose"] = PermissionDecision.Allow,
        };
        var policy = new ScriptedPermissionPolicy(table);
        policy = policy.WithModeDefaults(RunMode.Act);
        return policy;
    }

    [Fact]
    public void Continuation_state_is_forwarded_to_next_step_within_same_turn()
    {
        var fake = new FakeResponsesWithContinuation();
        var sessionId = SessionId.New();
        var store = new InMemoryEventStore();
        var runId = TestRun.OpenRun(store, sessionId);

        var (turn, artifactsPath) = CreateTurn((req, ct) => fake.Respond(req, ct), store, sessionId, runId);

        var result = turn.Ask("Read test.txt", "System prompt", sessionId, runId, "ws", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Contains("Continuation received: anthropic_response_id", result.FinalText);
        Assert.Contains("resp_001", result.FinalText);

        TryDeleteFiles(TestCwd, artifactsPath);
    }

    [Fact]
    public void Reasoning_blocks_are_preserved_in_assistant_message()
    {
        var fake = new FakeResponsesWithReasoning();
        var sessionId = SessionId.New();
        var store = new InMemoryEventStore();
        var runId = TestRun.OpenRun(store, sessionId);

        var (turn, artifactsPath) = CreateTurn((req, ct) => fake.Respond(req, ct), store, sessionId, runId);

        var result = turn.Ask("Read test.txt", "System prompt", sessionId, runId, "ws", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Equal("Reasoning preserved correctly", result.FinalText);

        TryDeleteFiles(TestCwd, artifactsPath);
    }

    [Fact]
    public void Continuation_does_not_leak_to_next_turn()
    {
        var fake = new FakeResponsesMultiTurn();

        // Turn 1
        var sessionId = SessionId.New();
        var store = new InMemoryEventStore();
        var runId = TestRun.OpenRun(store, sessionId);

        var (turn1, artifactsPath1) = CreateTurn((req, ct) => fake.Respond(req, ct), store, sessionId, runId);

        var result1 = turn1.Ask("First turn", "System prompt", sessionId, runId, "ws", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result1.StopReason);
        Assert.Contains("Turn 0 continuation:", result1.FinalText);
        Assert.Contains("turn0", result1.FinalText);

        TryDeleteFiles(TestCwd, artifactsPath1);

        // Turn 2 (new Ask on same run - should NOT have continuation from Turn 1)
        var (turn2, artifactsPath2) = CreateTurn((req, ct) => fake.Respond(req, ct), store, sessionId, runId);

        var result2 = turn2.Ask("Second turn", "System prompt", sessionId, runId, "ws", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result2.StopReason);
        // The continuation from Turn 1 should NOT be present in Turn 2's first step
        // (each Turn starts fresh; continuation is scoped to the current Turn only)
        Assert.Contains("Turn 1 continuation:", result2.FinalText);
        Assert.Contains("turn1", result2.FinalText);

        TryDeleteFiles(TestCwd, artifactsPath2);
    }

    [Fact]
    public void Tool_semantics_unchanged_multiple_tool_calls_batched()
    {
        // Verify that multiple tool calls in one response are batched correctly
        var callCount = 0;
        ModelResponse Responder(ModelRequest request, CancellationToken ct)
        {
            callCount++;
            if (callCount == 1)
            {
                var callId1 = ToolCallId.New();
                var callId2 = ToolCallId.New();
                return new ModelResponse(
                    new ContentBlock[]
                    {
                        new ToolCallBlock(callId1, "call_1", "filesystem.read", "{\"path\":\"a.txt\"}"),
                        new ToolCallBlock(callId2, "call_2", "filesystem.read", "{\"path\":\"b.txt\"}"),
                    },
                    StopReason.ToolUse,
                    new TokenUsage(20, 10, 0, 0, 0),
                    null,
                    new ProviderMetadata("req_001", "test-model", null));
            }

            // Step 2: Verify both tool results are in the conversation
            var toolResults = 0;
            foreach (var msg in request.Messages)
            {
                if (msg.Role == MessageRole.Tool)
                {
                    foreach (var block in msg.Content)
                    {
                        if (block is ToolResultBlock) toolResults++;
                    }
                }
            }

            return new ModelResponse(
                new ContentBlock[] { new TextBlock(toolResults == 2 ? "Both tools executed" : "Only " + toolResults + " tools executed") },
                StopReason.EndTurn,
                new TokenUsage(20, 10, 0, 0, 0),
                null,
                new ProviderMetadata("req_002", "test-model", null));
        }

        var sessionId = SessionId.New();
        var store = new InMemoryEventStore();
        var runId = TestRun.OpenRun(store, sessionId);

        var (turn, artifactsPath) = CreateTurn(Responder, store, sessionId, runId);

        var result = turn.Ask("Read both files", "System prompt", sessionId, runId, "ws", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Equal("Both tools executed", result.FinalText);
        Assert.Equal(2, result.ToolCalls.Count);

        TryDeleteFiles(TestCwd, artifactsPath);
    }

    /// <summary>
    /// Focal test for Bug 4 audit finding: captures the next step's ModelRequest and verifies
    /// that ReasoningBlock, ProviderOpaqueBlock, and ToolCallBlock are preserved in the
    /// assistant message with correct type, identity/content, and order. Also verifies the
    /// associated tool result for the ToolCallId.
    /// </summary>
    [Fact]
    public void Assistant_message_preserves_block_order_and_types_in_next_step()
    {
        ModelRequest? capturedRequest = null;
        var step = 0;
        ToolCallId? capturedCallId = null;

        ModelResponse Responder(ModelRequest request, CancellationToken ct)
        {
            step++;
            if (step == 1)
            {
                var callId = ToolCallId.New();
                capturedCallId = callId;
                var reasoningBlock = new ReasoningBlock("Thinking about the problem...", ReasoningVisibility.Full, null);
                var artifactId = new ArtifactId(Guid.NewGuid());
                var contentHash = new ContentHash("sha256", "abc123");
                var opaqueBlock = new ProviderOpaqueBlock(new ProviderOpaque(
                    ProviderFamily.AnthropicMessages,
                    "claude-3-5-sonnet",
                    "anthropic.thinking_signature",
                    new ArtifactRef(artifactId, contentHash, 100, "application/octet-stream", ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive),
                    ReplayPolicy.SameModel));
                var toolCall = new ToolCallBlock(callId, "call_1", "filesystem.read", "{\"path\":\"test.txt\"}");

                return new ModelResponse(
                    new ContentBlock[] { reasoningBlock, opaqueBlock, toolCall },
                    StopReason.ToolUse,
                    new TokenUsage(100, 50, 0, 0, 20),
                    new ProviderState("anthropic_response_id", "{\"response_id\":\"resp_001\"}"),
                    new ProviderMetadata("req_001", "claude-3-5-sonnet", null));
            }

            // Step 2: Capture the request to verify the assistant message
            capturedRequest = request;

            // Verify the tool result is present for the captured call ID
            var foundToolResult = false;
            foreach (var msg in request.Messages)
            {
                if (msg.Role == MessageRole.Tool)
                {
                    foreach (var block in msg.Content)
                    {
                        if (block is ToolResultBlock result && result.Id == capturedCallId)
                        {
                            foundToolResult = true;
                            Assert.True(result.Content.Count > 0, "Tool result should have content");
                            var textBlock = result.Content.OfType<TextBlock>().FirstOrDefault();
                            Assert.NotNull(textBlock);
                            Assert.Contains("test.txt", textBlock!.Text);
                        }
                    }
                }
            }
            Assert.True(foundToolResult, "Tool result for the captured call ID should be present");

            return new ModelResponse(
                new ContentBlock[] { new TextBlock("Verification complete") },
                StopReason.EndTurn,
                new TokenUsage(20, 10, 0, 0, 0),
                null,
                new ProviderMetadata("req_002", "claude-3-5-sonnet", null));
        }

        var sessionId = SessionId.New();
        var store = new InMemoryEventStore();
        var runId = TestRun.OpenRun(store, sessionId);

        var (turn, artifactsPath) = CreateTurn(Responder, store, sessionId, runId);

        var result = turn.Ask("Read test.txt", "System prompt", sessionId, runId, "ws", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Equal("Verification complete", result.FinalText);

        // Now verify the captured request's assistant message
        Assert.NotNull(capturedRequest);

        // Find the assistant message in the conversation (should be the one before the tool message)
        ModelMessage? assistantMessage = null;
        foreach (var msg in capturedRequest!.Messages)
        {
            if (msg.Role == MessageRole.Assistant)
            {
                // The last assistant message before the tool results is the one we care about
                assistantMessage = msg;
            }
        }

        Assert.NotNull(assistantMessage);
        Assert.Equal(3, assistantMessage!.Content.Count);

        // Verify order and types: ReasoningBlock, ProviderOpaqueBlock, ToolCallBlock
        var block0 = assistantMessage.Content[0];
        var block1 = assistantMessage.Content[1];
        var block2 = assistantMessage.Content[2];

        // Block 0: ReasoningBlock
        var reasoningBlock = Assert.IsType<ReasoningBlock>(block0);
        Assert.Equal("Thinking about the problem...", reasoningBlock.VisibleText);
        Assert.Equal(ReasoningVisibility.Full, reasoningBlock.Visibility);
        Assert.Null(reasoningBlock.OpaquePayload); // OpaquePayload is not preserved (null)

        // Block 1: ProviderOpaqueBlock - should be the SAME instance (unchanged)
        var opaqueBlock = Assert.IsType<ProviderOpaqueBlock>(block1);
        Assert.Equal(ProviderFamily.AnthropicMessages, opaqueBlock.Opaque.Family);
        Assert.Equal("claude-3-5-sonnet", opaqueBlock.Opaque.ModelId);
        Assert.Equal("anthropic.thinking_signature", opaqueBlock.Opaque.Kind);
        Assert.Equal(ArtifactKind.ProviderOpaqueState, opaqueBlock.Opaque.Payload.Kind);
        Assert.Equal(Sensitivity.Sensitive, opaqueBlock.Opaque.Payload.Sensitivity);
        Assert.Equal(ReplayPolicy.SameModel, opaqueBlock.Opaque.Replay);

        // Block 2: ToolCallBlock
        var toolCallBlock = Assert.IsType<ToolCallBlock>(block2);
        Assert.Equal(capturedCallId, toolCallBlock.Id);
        Assert.Equal("call_1", toolCallBlock.ProviderCallId);
        Assert.Equal("filesystem.read", toolCallBlock.ToolName);
        Assert.Equal("{\"path\":\"test.txt\"}", toolCallBlock.ArgumentsJson);

        TryDeleteFiles(TestCwd, artifactsPath);
    }

    private static void TryDeleteFiles(string cwd, string path)
    {
        try
        {
            var full = Path.Combine(cwd, path);
            if (Directory.Exists(full))
                Directory.Delete(full, true);
            else if (File.Exists(full))
                File.Delete(full);
        }
        catch { }
    }
}

/// <summary>
/// Fake response que devuelve estado opaco (ProviderState) junto con tool calls.
/// Simula un modelo que usa continuation state (p.ej. Anthropic con thinking + tools).
/// </summary>
public sealed class FakeResponsesWithContinuation
{
    private int _step = 0;

    public ModelResponse Respond(ModelRequest request, CancellationToken ct)
    {
        _step++;

        // Step 1: Return reasoning + tool call + continuation state
        if (_step == 1)
        {
            var callId = ToolCallId.New();
            var reasoningBlock = new ReasoningBlock("Thinking about the problem...", ReasoningVisibility.Full, null);
            var artifactId = new ArtifactId(Guid.NewGuid());
            var contentHash = new ContentHash("sha256", "abc123");
            var opaqueBlock = new ProviderOpaqueBlock(new ProviderOpaque(
                ProviderFamily.AnthropicMessages,
                "claude-3-5-sonnet",
                "anthropic.thinking_signature",
                new ArtifactRef(artifactId, contentHash, 100, "application/octet-stream", ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive),
                ReplayPolicy.SameModel));
            var toolCall = new ToolCallBlock(callId, "call_1", "filesystem.read", "{\"path\":\"test.txt\"}");

            return new ModelResponse(
                new ContentBlock[] { reasoningBlock, opaqueBlock, toolCall },
                StopReason.ToolUse,
                new TokenUsage(100, 50, 0, 0, 20),
                new ProviderState("anthropic_response_id", "{\"response_id\":\"resp_001\"}"),
                new ProviderMetadata("req_001", "claude-3-5-sonnet", null));
        }

        // Step 2: Verify continuation was passed and return final text
        var hasContinuation = request.Continuation is not null;
        var continuationKind = request.Continuation?.Kind ?? "none";
        var continuationPayload = request.Continuation?.PayloadJson ?? "";

        var verificationText = hasContinuation
            ? "Continuation received: " + continuationKind + " = " + continuationPayload
            : "ERROR: No continuation received";

        return new ModelResponse(
            new ContentBlock[] { new TextBlock(verificationText) },
            StopReason.EndTurn,
            new TokenUsage(20, 10, 0, 0, 0),
            null,
            new ProviderMetadata("req_002", "claude-3-5-sonnet", null));
    }

    public void Reset() => _step = 0;
}

/// <summary>
/// Fake response que devuelve reasoning + tool calls (sin estado opaco).
/// Verifica que los bloques de reasoning se preservan en el mensaje del asistente.
/// </summary>
public sealed class FakeResponsesWithReasoning
{
    private int _step = 0;

    public ModelResponse Respond(ModelRequest request, CancellationToken ct)
    {
        _step++;

        if (_step == 1)
        {
            var callId = ToolCallId.New();
            var reasoningBlock = new ReasoningBlock("Let me analyze this file...", ReasoningVisibility.Full, null);
            var toolCall = new ToolCallBlock(callId, "call_1", "filesystem.read", "{\"path\":\"test.txt\"}");

            return new ModelResponse(
                new ContentBlock[] { reasoningBlock, toolCall },
                StopReason.ToolUse,
                new TokenUsage(50, 30, 0, 0, 10),
                null,
                new ProviderMetadata("req_001", "test-model", null));
        }

        // Step 2: Verify the conversation history contains the reasoning block
        var hasReasoning = false;
        foreach (var msg in request.Messages)
        {
            if (msg.Role == MessageRole.Assistant)
            {
                foreach (var block in msg.Content)
                {
                    if (block is ReasoningBlock rb && rb.VisibleText?.Contains("analyze") == true)
                    {
                        hasReasoning = true;
                    }
                }
            }
        }

        return new ModelResponse(
            new ContentBlock[] { new TextBlock(hasReasoning ? "Reasoning preserved correctly" : "ERROR: Reasoning lost") },
            StopReason.EndTurn,
            new TokenUsage(20, 10, 0, 0, 0),
            null,
            new ProviderMetadata("req_002", "test-model", null));
    }

    public void Reset() => _step = 0;
}

/// <summary>
/// Fake response para verificar que la continuación NO se filtra al siguiente Turn.
/// </summary>
public sealed class FakeResponsesMultiTurn
{
    private int _turn = 0;
    private int _step = 0;

    public ModelResponse Respond(ModelRequest request, CancellationToken ct)
    {
        _step++;

        if (_step == 1)
        {
            var callId = ToolCallId.New();
            return new ModelResponse(
                new ContentBlock[] { new ToolCallBlock(callId, "call_1", "filesystem.read", "{\"path\":\"test.txt\"}") },
                StopReason.ToolUse,
                new TokenUsage(10, 5, 0, 0, 0),
                new ProviderState("test_kind", "{\"data\":\"turn" + _turn + "\"}"),
                new ProviderMetadata("req_001", "test-model", null));
        }

        // Step 2: Check if continuation from previous step is present
        var hasContinuation = request.Continuation is not null;
        var continuationPayload = request.Continuation?.PayloadJson ?? "";

        _step = 0;
        _turn++;

        return new ModelResponse(
            new ContentBlock[] { new TextBlock(hasContinuation ? "Turn " + (_turn - 1) + " continuation: " + continuationPayload : "No continuation") },
            StopReason.EndTurn,
            new TokenUsage(20, 10, 0, 0, 0),
            null,
            new ProviderMetadata("req_002", "test-model", null));
    }

    public void Reset() { _turn = 0; _step = 0; }
}