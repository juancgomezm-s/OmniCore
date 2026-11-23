using System.Text.Json;
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

/// <summary>Scripted generation, real SQLite/CAS and next-Turn context; no authenticated provider.</summary>
public sealed class ExplorerVisibleResponseIntegrationTests
{
    [Theory]
    [InlineData("pending")]
    [InlineData("completed")]
    [InlineData("published")]
    [InlineData("reasoning")]
    [InlineData("tools")]
    [InlineData("exhausted")]
    [InlineData("empty")]
    [InlineData("whitespace-final")]
    public void Resume_preserves_the_final_pass_and_never_regenerates_a_completed_final(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-final-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SqliteEventStore(Path.Combine(root, "journal.db"));
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var requests = new List<ModelRequest>();
            var pause = scenario == "pending";
            ExplorerTurn Create() => new((request, _) =>
            {
                requests.Add(request);
                if (requests.Count == 1 || scenario == "reasoning")
                    return CompanionToolsTests.Response([new ReasoningBlock("private fixture", ReasoningVisibility.Full, null)]);
                if (scenario == "tools") return CompanionToolsTests.Response([
                    new ToolCallBlock(ToolCallId.New(), "unwanted", "filesystem.list", "{}")]);
                if (scenario == "exhausted") return CompanionToolsTests.Response([]) with { StopReason = StopReason.MaxOutputTokens };
                if (scenario == "empty") return CompanionToolsTests.Response([]);
                if (scenario == "whitespace-final") return CompanionToolsTests.Response([new TextBlock(" \n ")]);
                return CompanionToolsTests.Response([new TextBlock("Recovered answer")]);
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "M10"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), billingMode: BillingMode.Local,
                maxModelStepsPerTurn: 4,
                quotaAdmission: (_, _, _, _, step) => pause && step == 1 ? InteractionId.New() : null);
            var original = Create().Ask("Question", "system", session, run.RunId, run.RootLane, "", TestContext.Current.CancellationToken);
            if (pause) Assert.Equal(StopReason.InputRequired, original.StopReason);
            else
            {
                // Simulate a crash after the completed step (or after publication),
                // only in this disposable journal. No provider effects are replayed.
                var events = store.ReadFrom(session, 1);
                var cutoff = scenario == "published"
                    ? events.Last(evt => codecs.Decode(evt) is AssistantMessageRecorded).Sequence
                    : events.Last(evt => codecs.Decode(evt) is ModelStepCompleted).Sequence;
                using var command = store.Connection.CreateCommand();
                command.CommandText = "DELETE FROM events WHERE seq > $cutoff";
                var parameter = command.CreateParameter(); parameter.ParameterName = "$cutoff"; parameter.Value = cutoff;
                command.Parameters.Add(parameter); command.ExecuteNonQuery();
            }
            pause = false;
            var resumed = Create().Ask("", "system", session, run.RunId, run.RootLane, "", TestContext.Current.CancellationToken);
            Assert.Equal(2, requests.Count);
            Assert.True(requests[1].FinalAnswerOnly);
            Assert.Empty(requests[1].Tools);
            Assert.Null(requests[1].Continuation);
            var finalEvents = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Equal(2, finalEvents.OfType<ModelStepStarted>().Count());
            Assert.Single(finalEvents.OfType<ModelStepStarted>(), step => step.FinalAnswerOnly);
            Assert.Empty(finalEvents.OfType<ToolCallRequested>());
            if (scenario is "pending" or "completed" or "published")
            {
                Assert.Equal("Recovered answer", resumed.FinalText);
                Assert.Single(finalEvents.OfType<AssistantMessageRecorded>());
                Assert.Single(finalEvents.OfType<TurnCompleted>());
            }
            else
            {
                Assert.Null(resumed.FinalText);
                Assert.Equal(original.FailureDiagnostic, resumed.FailureDiagnostic);
                Assert.Single(finalEvents.OfType<TurnAbandoned>());
                Assert.Empty(finalEvents.OfType<TurnCompleted>());
            }
        }
        finally
        {
            var connection = (SqliteConnection)store.Connection;
            store.Close(); SqliteConnection.ClearPool(connection); connection.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("empty", "cli.runtime.turn.reasoning_only")]
    [InlineData("whitespace", "cli.runtime.turn.empty_response")]
    [InlineData("no-content", "cli.runtime.turn.empty_response")]
    [InlineData("exhausted", "cli.runtime.turn.output_exhausted_without_answer")]
    [InlineData("truncated-tool", "cli.runtime.turn.output_exhausted_without_answer")]
    [InlineData("step-limit", "cli.runtime.turn.step_limit_without_answer")]
    [InlineData("recover", null)]
    [InlineData("llama", null)]
    [InlineData("reserved", null)]
    [InlineData("unwanted-tool", "cli.runtime.turn.tools_in_final_answer")]
    [InlineData("ignores-final", "cli.runtime.turn.reasoning_only")]
    public void Local_harness_distinguishes_missing_answer_and_step_limit_and_finalizes_without_tools(string scenario, string? diagnostic)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-final-answer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SqliteEventStore(Path.Combine(root, "journal.db"));
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var requests = new List<ModelRequest>();
            var first = scenario is "step-limit" or "reserved" or "truncated-tool"
                ? CompanionToolsTests.Response([new ToolCallBlock(ToolCallId.New(), "read-fixture", "filesystem.list", "{\"path\":\".\"}")])
                : CompanionToolsTests.Response([new ReasoningBlock("private draft only", ReasoningVisibility.Full, null)]);
            var last = scenario == "unwanted-tool"
                ? CompanionToolsTests.Response([new ToolCallBlock(ToolCallId.New(), "unwanted", "filesystem.list", "{\"path\":\".\"}")])
                : CompanionToolsTests.Response([new TextBlock("Respuesta visible")]);
            if (scenario == "whitespace") first = first with { Content = [new TextBlock(" \n\t")] };
            if (scenario == "no-content") first = first with { Content = [] };
            if (scenario is "exhausted" or "truncated-tool") first = first with { StopReason = StopReason.MaxOutputTokens };
            if (scenario == "ignores-final") last = first;
            var allowsFinalPass = scenario is "recover" or "llama" or "reserved" or "unwanted-tool" or "ignores-final";
            using var handler = new ReasoningOnlyUntilFinalHandler();
            var provider = new OpenAiChatCompatibleProvider(new ProviderDescriptor("fixture",
                ProviderFamily.OpenAiChatCompatible, "http://127.0.0.1:9999/v1", AuthConfig.None(), false, false, true)
                { BillingMode = BillingMode.Local, Profile = OpenAiChatCompatibleProvider.LlamaCppProfileName },
                new NoFixtureSecrets(), () => new HttpClient(handler, false));
            var turn = new ExplorerTurn((request, token) =>
            {
                requests.Add(request);
                return scenario == "llama" ? provider.Complete(request, token) : requests.Count == 1 ? first : last;
            },
                executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "M10"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), billingMode: BillingMode.Local,
                maxModelStepsPerTurn: allowsFinalPass ? 2 : 1);
            var result = turn.Ask("Resume el proyecto", "system", session, run.RunId, run.RootLane, "", TestContext.Current.CancellationToken);
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Equal(allowsFinalPass ? 2 : 1, requests.Count);
            Assert.False(requests[0].FinalAnswerOnly);
            if (allowsFinalPass)
            {
                Assert.True(requests[1].FinalAnswerOnly);
                Assert.Empty(requests[1].Tools);
                Assert.Equal("none", requests[1].ToolChoice.Mode);
                Assert.Contains("normal assistant response", requests[1].Instructions);
                Assert.Equal(requests[0].Model.Model, requests[1].Model.Model);
                if (scenario == "reserved") Assert.Single(result.ToolCalls);
                else Assert.Empty(result.ToolCalls);
            }
            if (diagnostic is null)
            {
                Assert.Equal(StopReason.EndTurn, result.StopReason);
                Assert.Equal("Respuesta visible", result.FinalText);
                Assert.Single(events.OfType<TurnCompleted>());
            }
            else
            {
                Assert.Equal(scenario is "exhausted" or "truncated-tool" ? StopReason.MaxOutputTokens : StopReason.Error, result.StopReason);
                Assert.Null(result.FinalText);
                Assert.Equal(diagnostic, result.FailureDiagnostic);
                Assert.Equal(diagnostic, Assert.Single(events.OfType<TurnAbandoned>()).Reason);
                Assert.Empty(events.OfType<TurnCompleted>());
                if (scenario == "truncated-tool") Assert.Empty(events.OfType<ToolCallRequested>());
            }
        }
        finally
        {
            var connection = (SqliteConnection)store.Connection;
            store.Close(); SqliteConnection.ClearPool(connection); connection.Dispose();
            Directory.Delete(root, true);
        }
    }

    private sealed class NoFixtureSecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => Secret.Of("");
    }

    // Reproduces the wire contract of the reported failure: a clean stop with
    // reasoning_content only, including on repeated text-only closing instructions.
    private sealed class ReasoningOnlyUntilFinalHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var final = body.RootElement.TryGetProperty("chat_template_kwargs", out var kwargs)
                && !kwargs.GetProperty("enable_thinking").GetBoolean();
            if (final)
            {
                Assert.False(body.RootElement.TryGetProperty("tools", out _));
                Assert.Equal("none", body.RootElement.GetProperty("tool_choice").GetString());
            }
            var delta = final ? "\"content\":\"Respuesta visible\"" : "\"reasoning_content\":\"private fixture draft\"";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{" + delta
                    + "},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                    System.Text.Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    [Theory]
    [InlineData("first ", "second")]
    [InlineData("| A | B |\n", "|---|---|\n| 1 | 2 |")]
    [InlineData("```cs\n", "int value = 42;\n```")]
    [InlineData("secret sk-super-secret-value ", "safe tail")]
    [InlineData("split sk-", "super-secret-value tail")]
    [InlineData("complete answer", "")]
    public void Full_visible_answer_survives_terminal_artifacts_reopen_and_next_turn_context(
        string first, string second)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-visible-answer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root, new SecretRedactor());
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var requests = new List<ModelRequest>();
            var redaction = new RedactionPolicy();
            var expected = first + second;
            var response = new ModelResponse([
                new ReasoningBlock("private fixture reasoning", ReasoningVisibility.Full, null),
                new TextBlock(first), new TextBlock(second)], StopReason.EndTurn,
                new TokenUsage(7, 5, 0, 0, 0), null, new ProviderMetadata("fixture", "scripted", null));
            ExplorerTurn MakeTurn(ModelResponse answer) => new((request, _) =>
            {
                requests.Add(request);
                return answer;
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store!, codecs, artifacts, new InMemoryAuditSink(), redaction,
                maximumGenerationRequestAttempts: 1);

            var result = MakeTurn(response).Ask("first question", "system", session, run.RunId,
                run.RootLane, "", TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(expected, result.FinalText);
            var events = store.ReadFrom(session, 1);
            var firstTurn = Assert.Single(events.Select(codecs.Decode).OfType<TurnStarted>());
            var assistant = Assert.Single(events.Select(codecs.Decode).OfType<AssistantMessageRecorded>());
            var textReference = Assert.IsType<ArtifactRef>(assistant.ContentRef);
            var summary = Assert.Single(events.Select(codecs.Decode).OfType<ModelCompleted>());
            var step = Assert.Single(events.Select(codecs.Decode).OfType<ModelStepCompleted>());
            Assert.Equal(expected, artifacts.GetText(textReference.Hash));
            foreach (var reference in new[] { summary.ResponseArtifact!, step.ResponseArtifact! })
            {
                using var json = JsonDocument.Parse(artifacts.GetText(reference.Hash)!);
                Assert.Equal(expected, json.RootElement.GetProperty("response").GetString());
            }
            using (var stepJson = JsonDocument.Parse(artifacts.GetText(step.ResponseArtifact!.Hash)!))
            {
                var visibleText = string.Concat(stepJson.RootElement.GetProperty("visibleContent")
                    .GetProperty("blocks").EnumerateArray()
                    .Where(block => block.GetProperty("kind").GetString() == "text")
                    .Select(block => block.GetProperty("text").GetString()));
                Assert.Equal(expected, visibleText);
            }
            Assert.Equal(firstTurn.TurnId, assistant.TurnId);
            Assert.Equal(firstTurn.TurnId, summary.TurnId);
            Assert.Single(events.Select(codecs.Decode).OfType<TurnCompleted>());

            var connection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root, new SecretRedactor());
            Assert.Equal(expected, artifacts.GetText(textReference.Hash));
            var next = MakeTurn(response with { Content = [new TextBlock("continued")] }).Ask(
                "next question", "system", session, run.RunId, run.RootLane, "",
                TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, next.StopReason);
            Assert.Equal(2, requests.Count);
            var priorAnswer = Assert.Single(requests[1].Messages, message => message.Role == MessageRole.Assistant);
            Assert.Equal(expected, string.Concat(priorAnswer.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.DoesNotContain("private fixture reasoning", priorAnswer.Content.OfType<TextBlock>()
                .Select(block => block.Text));
            Assert.Equal(2, store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>()
                .Select(turn => turn.TurnId).Distinct().Count());
        }
        finally
        {
            if (store is not null)
            {
                var connection = (SqliteConnection)store.Connection;
                store.Close();
                SqliteConnection.ClearPool(connection);
                connection.Dispose();
            }
            Directory.Delete(root, true);
        }
    }
}
