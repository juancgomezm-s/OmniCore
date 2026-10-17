using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>Exercises durable mutation-budget reconstruction through the real ExplorerTurn/tool
/// pipeline across a persisted user.ask suspension and a new Turn in the same Run.</summary>
public sealed class SessionMutationBudgetResumeTests
{
    [Fact]
    public void Tightened_budget_survives_questionnaire_restart_and_resets_only_for_the_next_turn()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mutation-budget-resume-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var journal = Path.Combine(root, "journal.db");
        var blobs = Path.Combine(root, "artifacts");
        var stateFile = Path.Combine(root, "last-session.txt");
        var codecs = EventCodecs.Create();
        SqliteEventStore? store = new(journal);
        try
        {
            var artifacts = new FileArtifactStore(blobs);
            var session = SessionId.New();
            var canonicalWorkspace = ProjectIdentity.CanonicalWorkspacePath(workspace);
            var stream = new EventStream(store, codecs, session);
            stream.Append(new SessionCreated(session, WorkspaceId.Of(canonicalWorkspace).ToString(), canonicalWorkspace,
                ProfileId.New(), DateTimeOffset.UtcNow));
            var initialPolicy = SessionPolicy(session, revision: 1, files: 2, changedLines: 100);
            stream.Append(initialPolicy, DurabilityClass.Barrier);
            var run = TestRun.Open(stream, session, mode: RunMode.Act);
            File.WriteAllText(stateFile, session + "\n" + run.RunId, new UTF8Encoding(false));
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);

            var catalog = new HostTools(new PathBoundaryValidator(), new PlanService(),
                includeMutationTools: true).Catalog();
            var model = ModelPolicyPresets.FullAgent();
            var modelMutation = new FileMutationPolicy(FileMutationMode.Full, DestructiveActionPolicy.Ask,
                DestructiveActionPolicy.Ask, 8, 2000, 1.0, requirePriorRead: false,
                requireExpectedVersionToken: true, requirePostEditValidation: false,
                allowParallelMutations: false);
            var effective = new EffectiveModelPolicy(ModelPolicyKey.For("fixture", "budget-resume"), 1,
                model.Category, model.ToolPolicy, modelMutation, isFallback: false,
                model.MaxModelStepsPerTurn, model.MaxToolCallsPerTurn, model.AllowOrchestration,
                model.MaxGenerationSeconds);
            ModelCapabilityBoundary NewBoundary() => new(effective, ModelCapabilityBoundary.CoreTools,
                new FileReadRegistry(workspace), sessionSecurityPolicy: () => server.ReadSessionSecurityPolicy(session));

            var existing = "budget.txt";
            var firstWrite = ToolCallId.New();
            var askCall = ToolCallId.New();
            var replaceCall = ToolCallId.New();
            var extraCall = ToolCallId.New();
            var nextTurnWrite = ToolCallId.New();
            var firstContent = "first\n";
            var version = FilesystemPatchTool.VersionToken(Encoding.UTF8.GetBytes(firstContent));
            var schema = new QuestionnaireSchema("Confirm budget", null,
                [new QuestionField("continue", "Continue?", null, QuestionKind.SingleChoice,
                    [new QuestionOption("yes", "Yes", null)], null, true, null, null, null)]);
            var modelCalls = 0;
            ModelResponse Provider(ModelRequest request, CancellationToken _)
            {
                modelCalls++;
                return modelCalls switch
                {
                    1 => ToolResponse(new ToolCallBlock(firstWrite, "write-first", "filesystem.write",
                        WriteArguments(existing, firstContent))),
                    2 => ToolResponse(new ToolCallBlock(askCall, "ask-budget", "user.ask",
                        QuestionnaireCodec.EncodeSchema(schema))),
                    3 => new ModelResponse(new ContentBlock[]
                    {
                        new ToolCallBlock(replaceCall, "over-lines", "filesystem.write",
                            WriteArguments(existing, "first\nsecond\n", version)),
                        new ToolCallBlock(extraCall, "over-files", "filesystem.write",
                            WriteArguments("extra.txt", "extra\n")),
                    }, StopReason.ToolUse, Usage(), null, new ProviderMetadata("fixture", "fixture", null)),
                    4 => TextResponse(),
                    5 => ToolResponse(new ToolCallBlock(nextTurnWrite, "new-turn-write", "filesystem.write",
                        WriteArguments("next-turn.txt", "next\n"))),
                    6 => TextResponse(),
                    _ => throw new InvalidOperationException("Unexpected provider call " + modelCalls),
                };
            }

            ExplorerTurn NewTurn(QuestionnaireInteractionService questionnaires, ModelCapabilityBoundary boundary) =>
                new(Provider, ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy(
                    new Dictionary<string, PermissionDecision>
                    {
                        ["filesystem.write"] = PermissionDecision.Allow,
                        ["user.ask"] = PermissionDecision.Allow,
                    }), workspace, boundary), catalog,
                    new ContextMaterializer(new FakeTokenCounter(), []),
                    new ExecutionFingerprint("fixture", "schema", "tools", "context", "output", "budget"),
                    new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                    store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                    boundary: boundary, questionnaires: questionnaires, recordEffectiveFingerprint: true,
                    sessionSecurityPolicy: () => server.ReadSessionSecurityPolicy(session));

            var boundary = NewBoundary();
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var suspended = NewTurn(service, boundary).Ask("write once, then ask", "system", session,
                run.RunId, run.RootLane, "", TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            var interaction = Assert.Single(service.Pending(session));
            Assert.Equal(2, modelCalls);
            Assert.Equal(firstContent, File.ReadAllText(Path.Combine(workspace, existing)));
            Assert.False(File.Exists(Path.Combine(workspace, "extra.txt")));

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(blobs);
            var tightened = SessionPolicy(session, revision: 2, files: 1, changedLines: 1,
                source: SessionSecurityPolicySource.UserSelection);
            new EventStream(store, codecs, session).Append(tightened, DurabilityClass.Barrier);
            server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(interaction.InteractionId,
                [new QuestionAnswer("continue", ["yes"], null, null)], cancelled: false).Status);

            service = new QuestionnaireInteractionService(store, codecs, artifacts);
            boundary = NewBoundary(); // New in-memory state: the same durable Run/Turn must restore its charge.
            var resumed = NewTurn(service, boundary).Ask("resume", "system", session, run.RunId,
                run.RootLane, "", TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, resumed.StopReason);
            Assert.Equal(4, modelCalls);

            Assert.Equal(firstContent, File.ReadAllText(Path.Combine(workspace, existing)));
            Assert.False(File.Exists(Path.Combine(workspace, "extra.txt")));
            var resumedEvents = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            foreach (var call in new[] { replaceCall, extraCall })
            {
                Assert.Contains(resumedEvents.OfType<ToolCallFailed>(), item => item.ToolCallId == call
                    && item.EffectOutcome == EffectOutcome.None
                    && item.ErrorCode == ToolErrorCode.LimitExceeded
                    && item.Cause.Contains(MutationLedger.LimitExceededCode, StringComparison.Ordinal));
                Assert.DoesNotContain(resumedEvents.OfType<ToolCallSucceeded>(), item => item.ToolCallId == call);
            }
            Assert.Single(resumedEvents.OfType<MutationBudgetCharged>(), item => item.ToolCallId == firstWrite);
            Assert.Equal(1, boundary.ReadRegistry().Ledger.FilesTouchedThisTurn);
            Assert.Equal(1, boundary.ReadRegistry().Ledger.ChangedLinesThisTurn);

            // A genuinely new Turn receives its own per-Turn allowance, while Run totals retain
            // both successful writes, including the one restored from before restart.
            var next = NewTurn(service, boundary).Ask("next turn", "system", session, run.RunId,
                run.RootLane, "", TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, next.StopReason);
            Assert.Equal("next\n", File.ReadAllText(Path.Combine(workspace, "next-turn.txt")));
            Assert.Equal(1, boundary.ReadRegistry().Ledger.FilesTouchedThisTurn);
            Assert.Equal(1, boundary.ReadRegistry().Ledger.ChangedLinesThisTurn);
            Assert.Equal(2, boundary.ReadRegistry().Ledger.FilesTouchedThisRun);
            Assert.Equal(2, boundary.ReadRegistry().Ledger.ChangedLinesThisRun);
            Assert.Equal(2, boundary.ReadRegistry().Ledger.MutationsThisRun);
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
        }
        finally
        {
            store?.Close();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static SessionSecurityPolicySet SessionPolicy(SessionId session, long revision, int files,
        int changedLines, SessionSecurityPolicySource source = SessionSecurityPolicySource.UserDefault)
    {
        var policy = SessionSecurityPolicySet.FromPreset(session, revision, ModelPolicyCategory.FullAgent,
            SessionSecurityProvenance.Native, source, DateTimeOffset.UtcNow) with
        {
            MaxFilesPerTurn = files,
            MaxChangedLinesPerTurn = changedLines,
            RequirePriorRead = false,
            RequirePostEditValidation = false,
        };
        policy.Validate();
        return policy;
    }

    private static string WriteArguments(string path, string content, string? expectedVersion = null) =>
        JsonSerializer.Serialize(new { path, content, expectedVersion });

    private static ModelResponse ToolResponse(ToolCallBlock tool) =>
        new([tool], StopReason.ToolUse, Usage(), null, new ProviderMetadata("fixture", "fixture", null));

    private static ModelResponse TextResponse() =>
        new([new TextBlock("completed")], StopReason.EndTurn, Usage(), null,
            new ProviderMetadata("fixture", "fixture", null));

    private static TokenUsage Usage() => new(3, 2, 0, 0, 0);
}
