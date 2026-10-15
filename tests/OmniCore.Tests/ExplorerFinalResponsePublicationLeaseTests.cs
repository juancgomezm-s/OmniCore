// Offline integration regression: real SQLite/CAS, scripted provider, no credentials or network.
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ExplorerFinalResponsePublicationLeaseTests
{
    [Fact]
    public void Provider_reported_image_cost_survives_journal_and_remains_separate_from_token_usage()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-image-reported-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var turn = TurnId.New();
            var run = RunId.New();
            var usage = new TokenUsage(0, 0, 0, 0, 0);
            const decimal reportedCost = 0.0142m;
            var response = ExplorerTurn.EncodeUsageResponse("", usage, reportedCost, run,
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                reportedProviderCostUsd: reportedCost);
            var responseRef = artifacts.PutText(response, "application/vnd.omnicore.model-usage+json",
                ArtifactKind.ModelResponse, Sensitivity.Sensitive);
            stream.Append(new ModelStepStarted(turn, 0, "image-model", 4096, "Direct", null, null, null));
            stream.Append(new ModelStepCompleted(turn, 0, usage, StopReason.EndTurn, responseRef,
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                reportedCost, TokenUsageFields.None, ReportedProviderCostUsd: reportedCost));

            var measurement = SessionUsageReporter.ReadConversation(store, codecs, artifacts, session);
            Assert.Equal(MetricAvailability.Unknown, measurement.Tokens.Availability);
            Assert.Equal(MetricAvailability.Reported, measurement.Cost.Availability);
            Assert.Equal(new Money(reportedCost, "USD"), measurement.Cost.Value);
            Assert.Equal("journal:provider-reported-usd-cost", measurement.Cost.Source);

            store.Close();
            store = new SqliteEventStore(journal);
            var reopened = Assert.Single(store.ReadFrom(session, 1)
                .Select(codecs.Decode).OfType<ModelStepCompleted>());
            Assert.Equal(reportedCost, reopened.ReportedProviderCostUsd);
            Assert.Equal(reportedCost, reopened.CostUsd);
            var afterRestart = SessionUsageReporter.ReadConversation(store, codecs, artifacts, session);
            Assert.Equal(MetricAvailability.Reported, afterRestart.Cost.Availability);
            Assert.Equal(new Money(reportedCost, "USD"), afterRestart.Cost.Value);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Image_only_response_is_decoded_stored_as_binary_journal_rooted_and_mapped_without_text_fallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-generated-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route, outputModalities: ["text", "image"]);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
            var turn = new ExplorerTurn((_, _) => new ModelResponse([new ImageBlock("image/png", png)],
                    StopReason.EndTurn, new TokenUsage(7, 5, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null),
                    AdditionalCostUpperBoundUsd: 0.08m), executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), maximumGenerationRequestAttempts: 1);

            var result = turn.Ask("draw", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.True(result.StopReason == StopReason.EndTurn, result.FailureDiagnostic ?? result.FinalText ?? result.StopReason.ToString());
            Assert.Null(result.FinalText);
            var events = store.ReadFrom(session, 1).ToArray();
            var recorded = Assert.Single(events.Select(codecs.Decode).OfType<AssistantImagesRecorded>());
            var image = Assert.Single(recorded.Images);
            Assert.Equal("image/png", image.Artifact.MediaType);
            Assert.Equal(1, image.Width);
            Assert.Equal(1, image.Height);
            Assert.Equal("fixture-provider", image.ProviderId);
            Assert.Equal(0, image.ModelStepIndex);
            Assert.Equal(png, Assert.IsAssignableFrom<IBinaryArtifactStore>(artifacts).GetBytes(image.Artifact.Hash, image.Artifact.Size));
            Assert.Single(recorded.OrderedParts!);
            Assert.Equal("image", recorded.OrderedParts![0].Kind);
            var step = Assert.Single(events.Select(codecs.Decode).OfType<ModelStepCompleted>());
            Assert.True(step.CostIncomplete);
            Assert.Equal(0.000012m, step.CostUsd); // image cap is not measured token spend
            var usageMeasurement = SessionUsageReporter.ReadConversation(store, codecs, artifacts, session);
            Assert.Equal(new TokenTotals(7, 5, 0, 0), usageMeasurement.Tokens.Value);
            Assert.Equal(MetricAvailability.Unknown, usageMeasurement.Cost.Availability);
            using (var stepJson = System.Text.Json.JsonDocument.Parse(artifacts.GetText(step.ResponseArtifact!.Hash)!))
            {
                var block = Assert.Single(stepJson.RootElement.GetProperty("visibleContent").GetProperty("blocks").EnumerateArray());
                Assert.Equal("image", block.GetProperty("kind").GetString());
                Assert.Equal(image.Artifact.Hash.ToString(), block.GetProperty("hash").GetString());
            }
            var replay = turn.LoadConversation(new EventStream(store, codecs, session), run.RunId,
                step.TurnId, run.RootLane);
            var restored = Assert.IsType<ImageBlock>(Assert.Single(replay.Where(message => message.Role == MessageRole.Assistant)
                .SelectMany(message => message.Content)));
            Assert.Equal(png, restored.Bytes.ToArray());
            Assert.Contains(events.SelectMany(evt => evt.ArtifactRefs), reference => reference.Hash == image.Artifact.Hash);
            var wireEvent = Assert.Single(events, evt => evt.Type.ToString() == "assistant_images.recorded");
            var wire = Assert.Single(new ProtocolMapper(codecs, artifacts).Map([wireEvent]));
            var fields = JsonObj.Parse(wire.PayloadJson);
            using var mappedImages = System.Text.Json.JsonDocument.Parse(fields["images"]);
            Assert.Equal(image.Artifact.Hash.Value, mappedImages.RootElement[0].GetProperty("hash").GetString());

            store.Close();
            store = new SqliteEventStore(journal);
            var reopened = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<AssistantImagesRecorded>());
            Assert.Equal(image.Artifact.Hash, Assert.Single(reopened.Images).Artifact.Hash);
            var gc = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false, DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.True(gc.LiveReferenced > 0);
            Assert.Equal(png, Assert.IsAssignableFrom<IBinaryArtifactStore>(artifacts).GetBytes(image.Artifact.Hash, image.Artifact.Size));
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Generated_image_validator_rejects_mime_mismatch_and_other_valid_image_formats()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
        Assert.Throws<InvalidDataException>(() => GeneratedImageValidator.Decode("image/jpeg", png));
        var gif = Convert.FromBase64String("R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs=");
        Assert.Throws<InvalidDataException>(() => GeneratedImageValidator.Decode("image/gif", gif));
        Assert.Throws<InvalidDataException>(() => GeneratedImageValidator.Decode("image/png", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
    }

    [Fact]
    public void Resume_after_model_step_checkpoint_publishes_image_without_calling_provider_again()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-generated-image-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible, OpenRouterImageGenerationProvider.ProfileName);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route, maxOutputTokens: 1024, outputModalities: ["text", "image"],
                imageGenerationMaxCostUsd: 0.08m);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
            var providerCalls = 0;
            var crashedStore = new CrashAfterImageStepStore(store, codecs);
            var first = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                return new ModelResponse([new ImageBlock("image/png", png)], StopReason.EndTurn,
                    new TokenUsage(0, 0, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null), TokenUsageFields.None,
                    AdditionalCostUpperBoundUsd: 0.08m);
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                crashedStore, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), modelContextCapacity: 8192,
                maximumGenerationRequestAttempts: 1, enforceDefaultSpendCaps: true,
                sessionCapUsd: 1m, dailyCapUsd: 1m);

            var interrupted = first.Ask("draw", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.Error, interrupted.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Single(store.ReadFrom(session, 1), evt => evt.Type.ToString() == "model_step.completed");
            Assert.DoesNotContain(store.ReadFrom(session, 1), evt => evt.Type.ToString() == "assistant_images.recorded");

            store.Close();
            store = new SqliteEventStore(journal);
            var resumed = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                throw new InvalidOperationException("provider must not be called while recovering durable output");
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), modelContextCapacity: 8192,
                maximumGenerationRequestAttempts: 1, enforceDefaultSpendCaps: true,
                sessionCapUsd: 1m, dailyCapUsd: 1m);

            var recovered = resumed.Ask("ignored while resuming", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, recovered.StopReason);
            Assert.Equal(1, providerCalls);
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var output = Assert.Single(events.OfType<AssistantImagesRecorded>());
            var image = Assert.Single(output.Images);
            Assert.Equal(png, Assert.IsAssignableFrom<IBinaryArtifactStore>(artifacts).GetBytes(image.Artifact.Hash, image.Artifact.Size));
            Assert.Single(events.OfType<ModelCompleted>());
            Assert.Single(events.OfType<TurnCompleted>());
            var measurement = SessionUsageReporter.ReadConversation(store, codecs, artifacts, session);
            Assert.Equal(MetricAvailability.Unknown, measurement.Cost.Availability);
            Assert.Equal(MetricAvailability.Unknown, measurement.Tokens.Availability);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resume_after_image_publication_before_turn_completed_does_not_duplicate_result_or_call_provider()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-generated-image-published-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route, outputModalities: ["text", "image"]);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
            var providerCalls = 0;
            var crashedStore = new CrashAfterImagePublicationStore(store, codecs);
            var first = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                return new ModelResponse([new ImageBlock("image/png", png)], StopReason.EndTurn,
                    new TokenUsage(7, 5, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                crashedStore, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                maximumGenerationRequestAttempts: 1);

            var interrupted = first.Ask("draw", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.Error, interrupted.StopReason);
            Assert.Equal(1, providerCalls);
            var publishedBeforeRestart = store.ReadFrom(session, 1).ToArray();
            var originalOutput = Assert.Single(publishedBeforeRestart.Select(codecs.Decode).OfType<AssistantImagesRecorded>());
            Assert.DoesNotContain(publishedBeforeRestart, evt => evt.Type.ToString() == "turn.completed");

            store.Close();
            store = new SqliteEventStore(journal);
            var resumed = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                throw new InvalidOperationException("provider must not be called after final result publication");
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                maximumGenerationRequestAttempts: 1);

            var completed = resumed.Ask("ignored while resuming", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, completed.StopReason);
            Assert.Equal(1, providerCalls);
            var afterRecovery = store.ReadFrom(session, 1).ToArray();
            Assert.Equal(1, afterRecovery.Count(evt => evt.Type.ToString() == "assistant_images.recorded"));
            Assert.Equal(1, afterRecovery.Count(evt => evt.Type.ToString() == "model.completed"));
            Assert.Equal(1, afterRecovery.Count(evt => evt.Type.ToString() == "turn.completed"));
            var recoveredOutput = Assert.Single(afterRecovery.Select(codecs.Decode).OfType<AssistantImagesRecorded>());
            Assert.Equal(Assert.Single(originalOutput.Images).Artifact.Hash,
                Assert.Single(recoveredOutput.Images).Artifact.Hash);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Mixed_final_response_persists_and_maps_exact_text_image_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mixed-generated-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(Path.Combine(root, "journal.db"));
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route, outputModalities: ["text", "image"]);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
            var turn = new ExplorerTurn((_, _) => new ModelResponse(
                    [new TextBlock("before"), new ImageBlock("image/png", png), new TextBlock("after")],
                    StopReason.EndTurn, new TokenUsage(2, 2, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null)), executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                maximumGenerationRequestAttempts: 1);

            var result = turn.Ask("draw", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            var recorded = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<AssistantImagesRecorded>());
            Assert.Equal(new[] { "text", "image", "text" }, recorded.OrderedParts!.Select(part => part.Kind));
            Assert.Equal(new[] { 0, 1, 2 }, recorded.OrderedParts!.Select(part => part.OutputIndex));
            var binary = Assert.IsAssignableFrom<IBinaryArtifactStore>(artifacts);
            var orderedText = recorded.OrderedParts!.Where(part => part.Kind == "text")
                .Select(part => artifacts.GetText(part.Artifact.Hash)).ToArray();
            Assert.Equal(new[] { "before", "after" }, orderedText);
            Assert.Equal(png, binary.GetBytes(recorded.OrderedParts![1].Artifact.Hash,
                recorded.OrderedParts![1].Artifact.Size));
            var envelope = Assert.Single(store.ReadFrom(session, 1), evt => evt.Type.ToString() == "assistant_images.recorded");
            var mapped = Assert.Single(new ProtocolMapper(codecs, artifacts).Map([envelope]));
            using var parts = System.Text.Json.JsonDocument.Parse(JsonObj.Parse(mapped.PayloadJson)["parts"]);
            Assert.Equal("text", parts.RootElement[0].GetProperty("kind").GetString());
            Assert.Equal("image", parts.RootElement[1].GetProperty("kind").GetString());
            Assert.Equal("text", parts.RootElement[2].GetProperty("kind").GetString());
        }
        finally
        {
            store?.Close();
            var journal = Path.Combine(root, "journal.db");
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Final_CAS_refs_and_terminal_event_batch_share_lease_reopen_and_count_usage_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-final-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var innerArtifacts = new FileArtifactStore(root);
            var artifacts = new LeaseObservingArtifacts(innerArtifacts, root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route);
            var eventObserver = new LeaseObservingEvents(store, root);
            var providerCalls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                Assert.True(artifacts.TryOpenLeaseProbe()); // no GC lease held during provider work
                return new ModelResponse(new ContentBlock[] { new TextBlock("final answer") }, StopReason.EndTurn,
                    new TokenUsage(7, 5, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                eventObserver, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                maximumGenerationRequestAttempts: 1);

            var result = turn.Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal("final answer", result.FinalText);
            Assert.Equal(1, providerCalls);
            Assert.True(artifacts.TryOpenLeaseProbe());
            Assert.True(eventObserver.FinalBatchLeaseHeldBeforeCommit);
            Assert.True(eventObserver.FinalBatchLeaseHeldAfterCommit);
            Assert.Equal(new[] { "model.completed", "assistant_message.recorded" }, eventObserver.FinalBatchTypes);

            var step = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>());
            var completed = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelCompleted>());
            var assistant = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<AssistantMessageRecorded>());
            Assert.Equal(7, step.Usage.Input);
            Assert.Equal(5, step.Usage.Output);
            Assert.NotNull(step.ResponseArtifact);
            Assert.NotNull(completed.ResponseArtifact);
            Assert.NotNull(assistant.ContentRef);

            var stepReference = step.ResponseArtifact!;
            var usageReference = completed.ResponseArtifact!;
            var textReference = assistant.ContentRef!;
            Assert.Equal("final answer", innerArtifacts.GetText(textReference.Hash));
            Assert.Contains("\"input\":7", innerArtifacts.GetText(usageReference.Hash));
            Assert.Contains("\"output\":5", innerArtifacts.GetText(usageReference.Hash));
            Assert.True(innerArtifacts.Verify(stepReference.Hash, stepReference.Size));
            Assert.True(innerArtifacts.Verify(usageReference.Hash, usageReference.Size));
            Assert.True(innerArtifacts.Verify(textReference.Hash, textReference.Size));

            var finalPublications = artifacts.Observed.Where(item =>
                item.Reference.Hash == usageReference.Hash || item.Reference.Hash == textReference.Hash).ToArray();
            Assert.Collection(finalPublications,
                item => Assert.True(item.LeaseHeldAfterPut),
                item => Assert.True(item.LeaseHeldAfterPut));

            var usageBeforeReopen = SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session);
            Assert.Equal(1, usageBeforeReopen.ModelInvocations);
            Assert.Equal(0, usageBeforeReopen.IncompleteInvocations);
            Assert.Equal(new TokenTotals(7, 5, 0, 0), usageBeforeReopen.Tokens.Value);

            store.Close();
            store = new SqliteEventStore(journal);
            var usageAfterReopen = SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session);
            var usageRepeated = SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session);
            Assert.Equal(1, usageAfterReopen.ModelInvocations);
            Assert.Equal(0, usageAfterReopen.IncompleteInvocations);
            Assert.Equal(new TokenTotals(7, 5, 0, 0), usageAfterReopen.Tokens.Value);
            Assert.Equal(usageAfterReopen, usageRepeated);

            var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.True(sweep.LiveReferenced >= 3);
            Assert.True(innerArtifacts.Verify(usageReference.Hash, usageReference.Size));
            Assert.True(innerArtifacts.Verify(textReference.Hash, textReference.Size));
            Assert.Equal("final answer", innerArtifacts.GetText(textReference.Hash));
            Assert.Contains("\"input\":7", innerArtifacts.GetText(usageReference.Hash));
            Assert.Equal(usageAfterReopen,
                SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session));
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Failed_final_batch_keeps_step_usage_but_never_publishes_terminal_refs_and_gc_collects_only_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-final-publication-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var innerArtifacts = new FileArtifactStore(root);
            var artifacts = new LeaseObservingArtifacts(innerArtifacts, root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route);
            var failingEvents = new LeaseObservingEvents(store, root, failFinalBatch: true);
            var providerCalls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                Assert.True(artifacts.TryOpenLeaseProbe());
                return new ModelResponse(new ContentBlock[] { new TextBlock("final answer") }, StopReason.EndTurn,
                    new TokenUsage(7, 5, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                failingEvents, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                maximumGenerationRequestAttempts: 1);

            var result = turn.Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.True(failingEvents.FinalBatchLeaseHeldBeforeCommit);
            Assert.False(failingEvents.FinalBatchDelegated);
            Assert.True(artifacts.TryOpenLeaseProbe());
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var step = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(7, step.Usage.Input);
            Assert.Equal(5, step.Usage.Output);
            Assert.NotNull(step.ResponseArtifact);
            Assert.Empty(events.OfType<ModelCompleted>());
            Assert.Empty(events.OfType<AssistantMessageRecorded>());
            Assert.Single(events.OfType<TurnAbandoned>());

            var finalUsage = Assert.Single(artifacts.Observed, item =>
                item.Reference.Kind == ArtifactKind.ModelResponse && item.Reference.MediaType == "application/vnd.omnicore.model-usage+json"
                && item.Reference.Hash != step.ResponseArtifact!.Hash);
            var finalText = Assert.Single(artifacts.Observed, item =>
                item.Reference.Kind == ArtifactKind.ModelResponse && item.Reference.MediaType == "text/markdown");
            Assert.True(finalUsage.LeaseHeldAfterPut);
            Assert.True(finalText.LeaseHeldAfterPut);
            Assert.True(innerArtifacts.Verify(step.ResponseArtifact!.Hash, step.ResponseArtifact.Size));

            var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.False(innerArtifacts.Verify(finalUsage.Reference.Hash, finalUsage.Reference.Size));
            Assert.False(innerArtifacts.Verify(finalText.Reference.Hash, finalText.Reference.Size));
            Assert.True(innerArtifacts.Verify(step.ResponseArtifact.Hash, step.ResponseArtifact.Size));
            Assert.Contains("\"input\":7", innerArtifacts.GetText(step.ResponseArtifact.Hash));
            Assert.Equal(1, SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session).ModelInvocations);
            Assert.Equal(0, SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session).IncompleteInvocations);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Invalid_usage_budget_guard_records_diagnostic_final_text_as_assistant_message_not_model_completed()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-final-publication-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var innerArtifacts = new FileArtifactStore(root);
            var artifacts = new LeaseObservingArtifacts(innerArtifacts, root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route);
            var eventObserver = new LeaseObservingEvents(store, root);
            var providerCalls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                Assert.True(artifacts.TryOpenLeaseProbe());
                return new ModelResponse(new ContentBlock[] { new TextBlock("answer") }, StopReason.EndTurn,
                    new TokenUsage(7, -1, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                eventObserver, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: true,
                maximumGenerationRequestAttempts: 1);

            var result = turn.Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Null(result.ResponseArtifactId);
            Assert.StartsWith("Presupuesto agotado:", result.FinalText);
            Assert.Equal(1, providerCalls);
            Assert.True(artifacts.TryOpenLeaseProbe());
            Assert.True(eventObserver.FinalBatchLeaseHeldBeforeCommit);
            Assert.True(eventObserver.FinalBatchLeaseHeldAfterCommit);
            Assert.Equal(new[] { "assistant_message.recorded" }, eventObserver.FinalBatchTypes);

            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var step = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(7, step.Usage.Input);
            Assert.Equal(-1, step.Usage.Output);
            Assert.NotNull(step.ResponseArtifact);
            Assert.Empty(events.OfType<ModelCompleted>());
            var assistant = Assert.Single(events.OfType<AssistantMessageRecorded>());
            Assert.NotNull(assistant.ContentRef);

            var stepReference = step.ResponseArtifact!;
            var textReference = assistant.ContentRef!;
            Assert.Equal(result.FinalText, innerArtifacts.GetText(textReference.Hash));
            Assert.True(innerArtifacts.Verify(stepReference.Hash, stepReference.Size));
            Assert.True(innerArtifacts.Verify(textReference.Hash, textReference.Size));

            var finalPublications = artifacts.Observed.Where(item =>
                item.Reference.Hash == textReference.Hash).ToArray();
            Assert.Collection(finalPublications,
                item => Assert.True(item.LeaseHeldAfterPut));

            store.Close();
            store = new SqliteEventStore(journal);
            var reopened = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var reopenedAssistant = Assert.Single(reopened.OfType<AssistantMessageRecorded>());
            Assert.Equal(result.FinalText, innerArtifacts.GetText(reopenedAssistant.ContentRef!.Hash));
            Assert.Empty(reopened.OfType<ModelCompleted>());
            var sequenceBeforeRead = store.CurrentSequence(session);
            var usageBeforeGc = SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session);
            new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.True(innerArtifacts.Verify(textReference.Hash, textReference.Size));
            Assert.True(innerArtifacts.Verify(stepReference.Hash, stepReference.Size));
            Assert.Equal(result.FinalText, innerArtifacts.GetText(textReference.Hash));
            Assert.Contains("\"output\":-1", innerArtifacts.GetText(stepReference.Hash));
            Assert.Equal(usageBeforeGc, SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session));
            Assert.Equal(sequenceBeforeRead, store.CurrentSequence(session));
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LeaseObservingArtifacts(IArtifactStore inner, string root)
        : IArtifactStore, IArtifactPublicationLease
    {
        public List<PublicationObservation> Observed { get; } = new();

        public IDisposable AcquirePublicationLease(CancellationToken cancellationToken) =>
            ((IArtifactPublicationLease)inner).AcquirePublicationLease(cancellationToken);

        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            var reference = inner.PutText(content, mediaType, kind, sensitivity);
            if (kind == ArtifactKind.ModelResponse)
                Observed.Add(new PublicationObservation(reference, LeaseHeld(root)));
            return reference;
        }

        public string? GetText(ContentHash hash) => inner.GetText(hash);
        public bool Verify(ContentHash hash, long expectedSize) => inner.Verify(hash, expectedSize);
        public bool TryOpenLeaseProbe() => !LeaseHeld(root);

        private static bool LeaseHeld(string root)
        {
            try
            {
                using var probe = new FileStream(Path.Combine(root, ".artifact-gc.lease"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }
    }

    private sealed record PublicationObservation(ArtifactRef Reference, bool LeaseHeldAfterPut);

    private sealed class CrashAfterImageStepStore(IEventStore inner, EventCodecs codecs)
        : IEventStore, IWorkspaceJournalReader
    {
        private bool _crashed;

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            ThrowIfCrashed();
            inner.Append(sessionId, evt, durability, cancellationToken);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            ThrowIfCrashed();
            if (events.Any(evt => codecs.Decode(evt) is ModelCompleted
                or AssistantMessageRecorded or AssistantImagesRecorded))
            {
                _crashed = true;
                throw new IOException("fixture process crash after durable ModelStepCompleted");
            }
            inner.AppendBatch(sessionId, events, durability, cancellationToken);
        }

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadEvents(EventType type) => ((IWorkspaceJournalReader)inner).ReadEvents(type);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) => inner.ReadFrom(sessionId, fromSequenceInclusive);
        private void ThrowIfCrashed()
        {
            if (_crashed) throw new IOException("fixture process is no longer running");
        }
    }

    private sealed class CrashAfterImagePublicationStore(IEventStore inner, EventCodecs codecs)
        : IEventStore, IWorkspaceJournalReader
    {
        private bool _crashed;

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            ThrowIfCrashed();
            inner.Append(sessionId, evt, durability, cancellationToken);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            ThrowIfCrashed();
            var publishesImage = events.Any(evt => codecs.Decode(evt) is AssistantImagesRecorded);
            inner.AppendBatch(sessionId, events, durability, cancellationToken);
            if (publishesImage)
            {
                _crashed = true;
                throw new IOException("fixture process crash after final response batch commit");
            }
        }

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadEvents(EventType type) => ((IWorkspaceJournalReader)inner).ReadEvents(type);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) => inner.ReadFrom(sessionId, fromSequenceInclusive);

        private void ThrowIfCrashed()
        {
            if (_crashed) throw new IOException("fixture process is no longer running");
        }
    }

    private sealed class LeaseObservingEvents(IEventStore inner, string root, bool failFinalBatch = false)
        : IEventStore, IWorkspaceJournalReader
    {
        public bool FinalBatchLeaseHeldBeforeCommit { get; private set; }
        public bool FinalBatchLeaseHeldAfterCommit { get; private set; }
        public bool FinalBatchDelegated { get; private set; }
        public IReadOnlyList<string> FinalBatchTypes { get; private set; } = Array.Empty<string>();

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => inner.Append(sessionId, evt, durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            var finalTypes = events.Select(evt => evt.Type.ToString())
                .Where(type => type is "model.completed" or "assistant_message.recorded").ToArray();
            if (finalTypes.Length == 0)
            {
                inner.AppendBatch(sessionId, events, durability, cancellationToken);
                return;
            }

            FinalBatchTypes = finalTypes;
            FinalBatchLeaseHeldBeforeCommit = LeaseHeld(root);
            if (failFinalBatch)
                throw new InvalidOperationException("fixture final response append failure");

            inner.AppendBatch(sessionId, events, durability, cancellationToken);
            FinalBatchDelegated = true;
            FinalBatchLeaseHeldAfterCommit = LeaseHeld(root);
        }

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadEvents(EventType type) =>
            ((IWorkspaceJournalReader)inner).ReadEvents(type);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            inner.ReadFrom(sessionId, fromSequenceInclusive);

        private static bool LeaseHeld(string root)
        {
            try
            {
                using var probe = new FileStream(Path.Combine(root, ".artifact-gc.lease"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }
    }
}
