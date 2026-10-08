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

public sealed class ConversationHistoryCanonicalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Canonical_assistant_text_reaches_next_request_without_usage_summary(bool reopen)
    {
        using var fixture = new Fixture();
        fixture.RecordAnswer(canonical: true, legacy: false);
        if (reopen) fixture.Reopen();
        ModelRequest? submitted = null;
        var turn = fixture.CreateTurn((request, _) =>
        {
            submitted = request;
            return Response("continued");
        });
        Assert.Equal(StopReason.EndTurn, turn.Ask("continúa", "system", fixture.Session,
            fixture.Run.RunId, fixture.Run.RootLane, "", TestContext.Current.CancellationToken).StopReason);
        var assistant = Assert.Single(submitted!.Messages, message => message.Role == MessageRole.Assistant);
        Assert.Equal("Decisión canónica: PostgreSQL.", Assert.IsType<TextBlock>(Assert.Single(assistant.Content)).Text);
    }

    [Fact]
    public void Canonical_text_takes_precedence_over_legacy_usage_without_duplicate_answer()
    {
        using var fixture = new Fixture();
        fixture.RecordAnswer(canonical: true, legacy: true);
        var assistant = Assert.Single(fixture.History(), message => message.Role == MessageRole.Assistant);
        Assert.Equal("Decisión canónica: PostgreSQL.", Assert.IsType<TextBlock>(Assert.Single(assistant.Content)).Text);
    }

    [Fact]
    public void Historical_journal_without_canonical_message_keeps_legacy_answer()
    {
        using var fixture = new Fixture();
        fixture.RecordAnswer(canonical: false, legacy: true);
        var assistant = Assert.Single(fixture.History(), message => message.Role == MessageRole.Assistant);
        Assert.Equal("legacy answer", Assert.IsType<TextBlock>(Assert.Single(assistant.Content)).Text);
    }

    [Fact]
    public void Equal_text_in_distinct_turns_is_not_deduplicated_as_one_answer()
    {
        using var fixture = new Fixture();
        fixture.RecordAnswer(canonical: true, legacy: true);
        fixture.RecordAnswer(canonical: true, legacy: true);
        var answers = fixture.History().Where(message => message.Role == MessageRole.Assistant).ToArray();
        Assert.Equal(2, answers.Length);
        Assert.All(answers, answer => Assert.Equal("Decisión canónica: PostgreSQL.",
            Assert.IsType<TextBlock>(Assert.Single(answer.Content)).Text));
    }

    [Fact]
    public void Canonical_history_crosses_runs_but_not_sessions()
    {
        using var fixture = new Fixture();
        fixture.RecordAnswer(canonical: true, legacy: false);
        var next = TestRun.Open(fixture.Store, fixture.Session);
        var other = TestRun.Open(fixture.Store, SessionId.New());
        var turn = fixture.CreateTurn((_, _) => Response("continued"));
        Assert.Single(turn.LoadConversation(new EventStream(fixture.Store, fixture.Codecs, fixture.Session),
            next.RunId), message => message.Role == MessageRole.Assistant);
        Assert.Empty(turn.LoadConversation(new EventStream(fixture.Store, fixture.Codecs, other.SessionId), other.RunId));
    }

    [Fact]
    public void Invalid_usage_diagnostic_recorded_in_chat_survives_reopen_in_history()
    {
        using var fixture = new Fixture();
        var turn = fixture.CreateTurn((_, _) => new ModelResponse(new ContentBlock[] { new TextBlock("answer") },
            StopReason.EndTurn, new TokenUsage(7, -1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null)),
            enforceCaps: true);
        var result = turn.Ask("ask", "system", fixture.Session, fixture.Run.RunId, fixture.Run.RootLane, "",
            TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.NotNull(result.FinalText);
        Assert.Empty(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).OfType<ModelCompleted>());
        Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).OfType<AssistantMessageRecorded>());
        fixture.Reopen();
        var assistant = Assert.Single(fixture.History(), message => message.Role == MessageRole.Assistant);
        Assert.Equal(result.FinalText, Assert.IsType<TextBlock>(Assert.Single(assistant.Content)).Text);
    }

    private static ModelResponse Response(string text) => new(new ContentBlock[] { new TextBlock(text) },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-context-history-" + Guid.NewGuid().ToString("N"));
        private readonly string _journal;
        private readonly FileArtifactStore _artifacts;
        public SqliteEventStore Store { get; private set; }
        public IEventCodecRegistry Codecs { get; } = EventCodecs.Create();
        public SessionId Session { get; } = SessionId.New();
        public TestRun.Opened Run { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            _journal = Path.Combine(_root, "journal.db");
            Store = new SqliteEventStore(_journal);
            _artifacts = new FileArtifactStore(Path.Combine(_root, "blobs"));
            Run = TestRun.Open(Store, Session);
        }

        public void RecordAnswer(bool canonical, bool legacy)
        {
            var stream = new EventStream(Store, Codecs, Session);
            var turn = TurnId.New();
            stream.Append(new TurnStarted(turn, Run.RootLane));
            if (legacy)
            {
                using var scope = ExecutionScope.Begin(new ExecutionScopeState(Run.RunId, Run.RootTask, Run.RootLane, turn));
                stream.Append(new ModelCompleted(turn, _artifacts.PutText("legacy answer", "text/plain",
                    ArtifactKind.ModelResponse, Sensitivity.Sensitive)));
            }
            if (canonical) stream.Append(new AssistantMessageRecorded(Run.RunId, Run.RootLane, turn,
                _artifacts.PutText("Decisión canónica: PostgreSQL.", "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Sensitive)));
            stream.Append(new TurnCompleted(turn));
        }

        public ExplorerTurn CreateTurn(Func<ModelRequest, CancellationToken, ModelResponse> provider, bool enforceCaps = false)
        {
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), _root);
            return new ExplorerTurn(provider, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                Store, Codecs, _artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: enforceCaps ? new ModelPricing(1m, 1m) : null, enforceDefaultSpendCaps: enforceCaps);
        }

        public IReadOnlyList<ModelMessage> History() => CreateTurn((_, _) => Response("continued"))
            .LoadConversation(new EventStream(Store, Codecs, Session), Run.RunId);

        public void Reopen()
        {
            Store.Close();
            Store = new SqliteEventStore(_journal);
        }

        public void Dispose()
        {
            Store.Close();
            using var connection = new SqliteConnection("DataSource=" + _journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
