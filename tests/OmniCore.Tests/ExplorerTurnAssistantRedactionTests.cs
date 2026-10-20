using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Private canonical assistant text and visible reasoning reach subsequent calls
/// verbatim, including credential-like examples. Diagnostic redaction remains independent.</summary>
public sealed class ExplorerTurnAssistantRedactionTests
{
    private static readonly ExecutionFingerprint Fingerprint = new("scripted", "h", "t", "c", "o", "M3");
    private static readonly ModelSelection Selection = new(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null);
    private const string FixtureFileName = "fixture.txt";
    private const string FixtureContent = "explorer-assistant-redaction-fixture-payload";
    private const string Secret = "fixture_token_only_123";
    private const string OriginalText = "ordinary preamble Bearer " + Secret;
    private const string OriginalReasoning = "ordinary reasoning Bearer " + Secret;

    private sealed record ScriptedOutcome(
        List<ModelRequest> Requests,
        StopReason StopReason,
        SqliteEventStore Store,
        string Root,
        string Journal);

    [Fact]
    public void Second_request_retains_grouped_assistant_blocks_verbatim_with_independent_diagnostic_redaction()
    {
        var call = new ToolCallBlock(ToolCallId.New(), "call-redact-a", "filesystem.read",
            "{\"path\":\"" + FixtureFileName + "\"}");
        var outcome = RunTwoCallTurn(new ContentBlock[]
        {
            new TextBlock(OriginalText),
            new ReasoningBlock(OriginalReasoning, ReasoningVisibility.Full, null),
            call
        });
        try
        {
            // Turn shape first: a failure here is a broken fixture, not the redaction regression.
            Assert.Equal(StopReason.EndTurn, outcome.StopReason);
            Assert.Equal(2, outcome.Requests.Count);
            var flat = outcome.Requests[1].Messages.SelectMany(message => message.Content).ToArray();
            var results = flat.OfType<ToolResultBlock>().ToArray();
            var result = Assert.Single(results);
            Assert.False(result.IsError, "the real read-only catalog tool must have succeeded");
            Assert.Equal(call.Id, result.Id);
            Assert.Contains(result.Content.OfType<TextBlock>(),
                text => text.Text.Contains(FixtureContent, StringComparison.Ordinal));

            // Whole-response retention: exactly ONE assistant message with the three blocks in
            // original order text → reasoning → call, same id/arguments, before its tool result.
            var assistant = Assert.Single(outcome.Requests[1].Messages,
                message => message.Role == MessageRole.Assistant);
            var blocks = assistant.Content.ToArray();
            Assert.Equal(3, blocks.Length);
            Assert.IsType<TextBlock>(blocks[0]);
            Assert.IsType<ReasoningBlock>(blocks[1]);
            Assert.Equal(call, Assert.IsType<ToolCallBlock>(blocks[2]));
            Assert.True(Array.IndexOf(flat, blocks[0]) < Array.IndexOf(flat, result),
                "the grouped assistant message must precede the matching tool result");

            // Exact conversational fidelity, with no secret-like string replacement.
            var retainedText = Assert.IsType<TextBlock>(blocks[0]).Text;
            var retainedReasoning = Assert.IsType<ReasoningBlock>(blocks[1]).VisibleText;
            Assert.NotNull(retainedReasoning);
            var policy = new RedactionPolicy();
            Assert.Equal(OriginalText, retainedText);
            Assert.Equal(OriginalReasoning, retainedReasoning);

            // Private conversation retains synthetic examples; diagnostic copies remain sanitized.
            Assert.Contains(Secret, retainedText, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, policy.Redact(retainedText), StringComparison.Ordinal);
            Assert.Contains(Secret, retainedReasoning, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, policy.Redact(retainedReasoning!), StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, outcome.Requests[1].Instructions ?? string.Empty,
                StringComparison.Ordinal);

            // Non-vacuous redaction: the ordinary prose is untouched.
            Assert.Contains("ordinary preamble", retainedText, StringComparison.Ordinal);
            Assert.Contains("ordinary reasoning", retainedReasoning, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(outcome.Store, outcome.Root, outcome.Journal);
        }
    }

    /// <summary>One Ask, one shared scripted delegate, two model calls: a ToolUse response with
    /// a Bearer-bearing text preamble, a Bearer-bearing reasoning block and one real
    /// filesystem.read call first, then EndTurn. Captures both ModelRequests without logging any
    /// payload or secret.</summary>
    private static ScriptedOutcome RunTwoCallTurn(ContentBlock[] firstResponseBlocks)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-explorer-redact-" + Guid.NewGuid().ToString("N"));
        var journal = Path.Combine(root, "journal.db");
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            File.WriteAllText(Path.Combine(root, FixtureFileName), FixtureContent);
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var tools = OmniHost.CreateExplorerTools();
            var catalog = tools.Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                {
                    ["filesystem.read"] = PermissionDecision.Allow,
                }), root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var requests = new List<ModelRequest>();
            ModelResponse Ask(ModelRequest request, CancellationToken _)
            {
                requests.Add(request);
                return requests.Count == 1
                    ? new ModelResponse(firstResponseBlocks, StopReason.ToolUse,
                        new TokenUsage(2, 1, 0, 0, 0), null,
                        new ProviderMetadata("scripted", "test", null))
                    : new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
            }
            var turn = new OmniCore.Host.ExplorerTurn(Ask, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                Fingerprint, Selection, store, codecs, artifacts, new InMemoryAuditSink(),
                new RedactionPolicy());
            var result = turn.Ask("read the fixture and retain the whole assistant response", "system", session,
                run.RunId, run.RootLane, "", TestContext.Current.CancellationToken);
            return new ScriptedOutcome(requests, result.StopReason, store, root, journal);
        }
        catch
        {
            Cleanup(store, root, journal);
            throw;
        }
    }

    private static void Cleanup(SqliteEventStore? store, string root, string journal)
    {
        store?.Close();
        // Release only THIS journal's pooled connection (ClearPool, never ClearAllPools) so the
        // isolated GUID directory can be deleted; includes SQLite sidecars if any remain.
        using var connection = new SqliteConnection("DataSource=" + journal);
        SqliteConnection.ClearPool(connection);
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
