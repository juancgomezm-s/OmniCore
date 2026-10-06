using System.Diagnostics;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class M4ProcessRecoveryTests
{
    private const string PhaseVariable = "OMNICORE_M4_PROBE_PHASE";
    private const string RootVariable = "OMNICORE_M4_PROBE_ROOT";
    private const string Objective = "M4 durable objective; pending validation";

    [Fact]
    public async Task Long_session_checkpoint_and_pending_questionnaire_survive_a_new_process()
    {
        var phase = Environment.GetEnvironmentVariable(PhaseVariable);
        if (phase is not null)
        {
            var root = Environment.GetEnvironmentVariable(RootVariable)!;
            if (phase == "seed") Seed(root);
            else if (phase == "restore") Restore(root);
            else if (phase == "terminal")
            {
                Restore(root);
                var server = OmniHost.OpenPersistentServer(Path.Combine(root, "journal.db"));
                server.ConfigureWorkspaceRoot(root);
                Assert.Equal(0, OmniCore.Cli.TuiApp.Run(server));
                ((SqliteEventStore)server.AcquireStore()).Close();
            }
            else Assert.Fail("Unknown probe phase");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m4-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Preserve the independent-process journal and evidence for terminal validation.
        TestContext.Current.TestOutputHelper!.WriteLine("M4 evidence: " + directory);
        var first = await Child("seed", directory);
        var second = await Child("restore", directory);
        Assert.NotEqual(first, second);
        Assert.NotEqual(Environment.ProcessId, second);
        TestContext.Current.TestOutputHelper!.WriteLine("M4 evidence: " + directory);
    }

    private static async Task<int> Child(string phase, string root)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(M4ProcessRecoveryTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("*Long_session_checkpoint_and_pending_questionnaire_survive_a_new_process");
        start.ArgumentList.Add("-noColor");
        start.Environment[PhaseVariable] = phase;
        start.Environment[RootVariable] = root;
        using var child = Process.Start(start)!;
        var pid = child.Id;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var output = "";
        try { await child.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw;
        }
        finally
        {
            output = await stdout + await stderr;
            File.WriteAllText(Path.Combine(root, phase + "-process.log"), output);
        }
        Assert.True(child.ExitCode == 0, output);
        return pid;
    }

    private static void Seed(string root)
    {
        var elapsed = Stopwatch.StartNew();
        Progress(root, "seed-start", 0, 0, elapsed.Elapsed, TimeSpan.Zero);
        var journal = Path.Combine(root, "journal.db");
        var server = OmniHost.OpenPersistentServer(journal);
        Assert.Equal("ok", server.Send(OmniCore.Protocol.WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"session.input\",\"text\":\"" + Objective + "\"}"), CancellationToken.None).Status);
        var store = server.AcquireStore();
        var codecs = server.AcquireCodecs();
        var artifacts = OmniHost.CreateArtifactStore(root);
        var session = server.LastSessionId()!;
        var run = server.LastRunId()!;
        var events = store.ReadFrom(session, 1);
        var lane = events.Select(codecs.Decode).OfType<LaneCreated>().Single().LaneId;
        var stream = new EventStream(store, codecs, session);
        var plan = PlanId.New();
        stream.Append(new PlanCreated(plan, run, PlanItemId.New(), Objective));
        stream.Append(new PlanItemAdded(PlanItemId.New(), plan, "M4 pending tests", 2, null,
            Array.Empty<PlanItemId>(), true, new Dictionary<string, string>()));
        var tools = OmniHost.CreateExplorerTools();
        var executor = OmniHost.CreateExplorerExecutor(tools.Catalog(), root);
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off,
            1, PlanControl.RuntimeDriven, 4, new ContextManagementPolicy(1000, 180, 8, 24, 1200));
        var turn = new ExplorerTurn((_, _) => Response(string.Join(' ', Enumerable.Repeat("result", 60))),
            executor, tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("scripted", "harness", "tools", "policy", "none", "M4"),
            new ModelSelection(new ModelIdValue("scripted"), 3000, ToolMode.Direct, null), store,
            codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), harness,
            metaModelProvider: new SummaryProvider());
        for (var i = 0; i < 200; i++)
        {
            var before = elapsed.Elapsed;
            Assert.Equal(StopReason.EndTurn, turn.Ask("turn-" + i + " " + string.Join(' ', Enumerable.Repeat("question", 40)),
                "Keep this run focused", session, run, lane, Objective + "; M4 pending tests", CancellationToken.None).StopReason);
            if ((i + 1) % 10 == 0)
                Progress(root, "turn-completed", i + 1, store.CurrentSequence(session), elapsed.Elapsed, elapsed.Elapsed - before);
        }
        Progress(root, "questionnaire-start", 200, store.CurrentSequence(session), elapsed.Elapsed, TimeSpan.Zero);
        var schema = new QuestionnaireSchema("M4 restart questionnaire", null, new QuestionField[] {
            new("single", "Proceed?", null, QuestionKind.SingleChoice, new[] { new QuestionOption("yes", "Yes", null),
                new QuestionOption("no", "No", null) }, null, true, null, null, null) });
        Assert.True(new QuestionnaireInteractionService(store, codecs, artifacts)
            .Publish(stream, schema, InteractionId.New(), lane, null).Published);
        stream.Append(new RunAwaitingInput(run, lane));
        var all = store.ReadFrom(session, 1);
        File.WriteAllText(Path.Combine(root, "baseline.json"), JsonSerializer.Serialize(all.Select(e => e.PayloadJson).ToArray()));
        Validate(store, codecs, artifacts, session);
        Progress(root, "seed-validated", 200, store.CurrentSequence(session), elapsed.Elapsed, TimeSpan.Zero);
        ((SqliteEventStore)store).Close();
    }

    private static void Progress(string root, string phase, int turns, long sequence, TimeSpan elapsed, TimeSpan lastTurn)
    {
        using var file = new FileStream(Path.Combine(root, "seed-progress.jsonl"), FileMode.Append,
            FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, phase, turns, sequence,
            elapsedMs = elapsed.TotalMilliseconds, lastTurnMs = lastTurn.TotalMilliseconds, pid = Environment.ProcessId }));
        writer.Flush();
        file.Flush(flushToDisk: true);
    }

    private static void Restore(string root)
    {
        var server = OmniHost.OpenPersistentServer(Path.Combine(root, "journal.db"));
        var store = server.AcquireStore();
        var codecs = server.AcquireCodecs();
        var artifacts = OmniHost.CreateArtifactStore(root);
        var session = server.LastSessionId()!;
        Assert.NotNull(session);
        var baseline = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "baseline.json")))!;
        Assert.Equal(baseline, store.ReadFrom(session, 1).Select(e => e.PayloadJson));
        Validate(store, codecs, artifacts, session);
        var context = server.Query("context", CancellationToken.None)!.Json;
        var working = server.Query("workingState", CancellationToken.None)!.Json;
        Assert.Contains(Objective, working);
        Assert.Contains("M4 pending tests", working);
        using var doc = JsonDocument.Parse(context);
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("snapshot").ValueKind);
        File.WriteAllText(Path.Combine(root, "restored-context.json"), context);
        File.WriteAllText(Path.Combine(root, "restored-working-state.json"), working);
        Assert.Equal(baseline, store.ReadFrom(session, 1).Select(e => e.PayloadJson));
        ((SqliteEventStore)store).Close();
        var report = new JournalVerifier(codecs, new FileArtifactStore(root))
            .VerifyJournal(Path.Combine(root, "journal.db"));
        Assert.True(report.Ok, string.Join("; ", report.Issues.Select(issue => issue.ToString())));
        File.WriteAllText(Path.Combine(root, "verify-journal.txt"), report.SummaryLine());
    }

    private static void Validate(IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts, SessionId session)
    {
        var decoded = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
        var checkpoints = decoded.OfType<ContextCheckpointRecorded>().ToArray();
        Assert.NotEmpty(checkpoints);
        Assert.True(checkpoints.Zip(checkpoints.Skip(1)).All(pair => pair.First.ThroughEventSequence < pair.Second.ThroughEventSequence));
        using var checkpoint = JsonDocument.Parse(artifacts.GetText(checkpoints.Last().CheckpointArtifact.Hash)!);
        Assert.True(checkpoint.RootElement.GetProperty("compactedThroughItemIndex").GetInt32() > 0);
        Assert.Contains("durable M4 decision", checkpoint.RootElement.GetRawText());
        using var snapshot = JsonDocument.Parse(artifacts.GetText(decoded.OfType<TurnStarted>().Last().ContextSnapshotRef!.Hash)!);
        Assert.True(snapshot.RootElement.GetProperty("tokenCount").GetInt32() <= 3000);
        Assert.Contains("M4 pending tests", snapshot.RootElement.GetRawText());
        var pending = Assert.Single(new QuestionnaireInteractionService(store, codecs, artifacts).Pending(session));
        Assert.NotNull(pending.SchemaRef);
        Assert.Contains("M4 restart questionnaire", artifacts.GetText(pending.SchemaRef!.Hash)!);
        Assert.DoesNotContain(decoded, e => e is InteractionResolved);
    }

    private static ModelResponse Response(string text) => new(new ContentBlock[] { new TextBlock(text) },
        StopReason.EndTurn, new TokenUsage(10, 60, 0, 0, 0), null, new ProviderMetadata("scripted", "M4", null));
    private sealed class SummaryProvider : IModelProvider
    {
        public ProviderCapabilities Capabilities => ProviderCapabilities.Local();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ResponseCompleted(Response("Facts: durable M4 decision."));
        }
    }
}
