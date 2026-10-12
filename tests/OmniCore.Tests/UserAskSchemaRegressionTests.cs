using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

public sealed class UserAskSchemaRegressionTests
{
    private static readonly QuestionnaireSchema MixedSchema = new("Review preferences", "Fixture questionnaire",
        new QuestionField[]
        {
            new("style", "Which review style do you prefer?", null, QuestionKind.SingleChoice,
                new[] { new QuestionOption("inline", "Inline comments", null),
                    new QuestionOption("summary", "Summary first", null) }, null, true, null, null, null),
            new("checks", "Which checks should be included?", null, QuestionKind.MultipleChoice,
                new[] { new QuestionOption("tests", "Tests", null),
                    new QuestionOption("security", "Security review", null) },
                new OtherInput("other", "Other", null, true, 2000), true, 1, 3, null),
            new("note", "Anything else?", null, QuestionKind.FreeText, Array.Empty<QuestionOption>(),
                null, true, null, null, 2000),
        });

    [Fact]
    public void Input_schema_preserves_legacy_optional_fields_and_supports_nullable_values()
    {
        var descriptor = new UserAskTool().Descriptor;
        using var schema = JsonDocument.Parse(descriptor.InputSchema.ToString());
        var root = schema.RootElement;
        Assert.Equal(new[] { "title", "questions" }, Required(root));
        Assert.Contains("null", Types(root.GetProperty("properties").GetProperty("description")));

        var question = root.GetProperty("properties").GetProperty("questions").GetProperty("items");
        Assert.Equal(new[] { "id", "prompt", "kind" }, Required(question));
        var questionProperties = question.GetProperty("properties");
        Assert.Contains("null", Types(questionProperties.GetProperty("helpText")));
        Assert.Contains("null", Types(questionProperties.GetProperty("other")));
        Assert.Contains("null", Types(questionProperties.GetProperty("minSelections")));
        Assert.Contains("null", Types(questionProperties.GetProperty("maxSelections")));
        Assert.Contains("null", Types(questionProperties.GetProperty("maxTextLength")));
        Assert.Contains("array", Types(questionProperties.GetProperty("options")));
        Assert.Contains("null", Types(questionProperties.GetProperty("options")));
        Assert.Contains("null", Types(questionProperties.GetProperty("required")));

        var option = questionProperties.GetProperty("options").GetProperty("items");
        Assert.Equal(new[] { "id", "label" }, Required(option));
        Assert.Contains("null", Types(option.GetProperty("properties").GetProperty("description")));
        var other = questionProperties.GetProperty("other").GetProperty("properties");
        Assert.Equal(new[] { "optionId", "label" }, Required(questionProperties.GetProperty("other")));
        Assert.Contains("null", Types(other.GetProperty("placeholder")));
        Assert.Contains("null", Types(other.GetProperty("textRequired")));
        Assert.Contains("null", Types(other.GetProperty("maxTextLength")));

        var tool = new ToolDefinition(descriptor.Id.ToString(), descriptor.Description,
            descriptor.InputSchema.ToString());
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("schema-fixture"), 8192,
                ToolMode.Direct, null), Array.Empty<ModelMessage>(), null, new[] { tool },
            ToolChoice.Auto(), null, null, null, null);
        using var wire = JsonDocument.Parse(OpenAIResponsesProvider.BuildBody(request, new OpenAIResponsesOptions()));
        var wireSchema = wire.RootElement.GetProperty("tools")[0].GetProperty("parameters");
        Assert.Equal(Required(root), Required(wireSchema));
        var wireQuestionProperties = wireSchema.GetProperty("properties").GetProperty("questions")
            .GetProperty("items").GetProperty("properties");
        Assert.Equal(Types(questionProperties.GetProperty("maxSelections")),
            Types(wireQuestionProperties.GetProperty("maxSelections")));
    }

    [Fact]
    public void Tool_runtime_accepts_original_minimal_shape_and_nullable_complete_shape_but_keeps_semantic_rejections()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-user-ask-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tool = new UserAskTool();
            var catalog = new FakeCatalog().Add(tool);
            var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>(StringComparer.Ordinal)
            {
                ["user.ask"] = PermissionDecision.Allow,
            });
            var events = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(catalog, policy, payload =>
            {
                events.Add(payload);
                return VoidBox.Instance;
            });
            var context = new ToolExecutionContext(root, readRegistry: null,
                emitEvent: payload => events.Add(payload));

            var legacyMinimal = """
                {"title":"Minimal","questions":[{"id":"note","prompt":"Add a note","kind":"FreeText"}]}
                """;
            var minimal = runtime.Run(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id,
                    "minimal", legacyMinimal), new ToolPreparationContext(root, DateTimeOffset.UtcNow),
                context, userApprovesAsk: false, CancellationToken.None);
            Assert.True(minimal.Succeeded, "legacy_optional_shape_rejected:" + minimal.Summary);

            var nullableComplete = """
                {"title":"Nullable","description":null,"questions":[{"id":"note","prompt":"Add a note","helpText":null,"kind":"FreeText","options":[],"other":null,"required":true,"minSelections":null,"maxSelections":null,"maxTextLength":null}]}
                """;
            var nullable = runtime.Run(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id,
                    "nullable", nullableComplete), new ToolPreparationContext(root, DateTimeOffset.UtcNow),
                context, userApprovesAsk: false, CancellationToken.None);
            Assert.True(nullable.Succeeded, "nullable_optional_shape_rejected:" + nullable.Summary);

            var invalidBoundsId = ToolCallId.New();
            var invalidBounds = nullableComplete.Replace("\"maxSelections\":null", "\"maxSelections\":2",
                StringComparison.Ordinal);
            var rejectedBounds = runtime.Run(new ValidatedToolCall(invalidBoundsId, tool.Descriptor.Id,
                    "invalid-bounds", invalidBounds), new ToolPreparationContext(root, DateTimeOffset.UtcNow),
                context, userApprovesAsk: false, CancellationToken.None);
            Assert.False(rejectedBounds.Succeeded);
            Assert.Equal(ToolCallState.Rejected, rejectedBounds.FinalState);
            Assert.Contains(events.OfType<ToolCallRejected>(), item => item.ToolCallId == invalidBoundsId
                && item.ErrorCode == ToolErrorCode.InvalidArguments);

            var invalidOther = QuestionnaireCodec.EncodeSchema(new QuestionnaireSchema("Invalid Other", null,
                new[] { new QuestionField("checks", "Select checks", null, QuestionKind.MultipleChoice,
                    new[] { new QuestionOption("tests", "Tests", null) },
                    new OtherInput("other", "Other", null, true, 0), true, 1, 2, null) }));
            var invalidOtherId = ToolCallId.New();
            var rejectedOther = runtime.Run(new ValidatedToolCall(invalidOtherId, tool.Descriptor.Id,
                    "invalid-other", invalidOther), new ToolPreparationContext(root, DateTimeOffset.UtcNow),
                context, userApprovesAsk: false, CancellationToken.None);
            Assert.False(rejectedOther.Succeeded);
            Assert.Equal(ToolCallState.Rejected, rejectedOther.FinalState);
            Assert.Contains(events.OfType<ToolCallRejected>(), item => item.ToolCallId == invalidOtherId
                && item.ErrorCode == ToolErrorCode.InvalidArguments);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Durable_mixed_questionnaire_answers_resume_as_original_tool_result_after_reopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-user-ask-durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var blobs = Path.Combine(root, "artifacts");
        SqliteEventStore? store = null;
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(blobs);
            store = new SqliteEventStore(journal);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var planId = PlanId.New();
            new EventStream(store, codecs, session).Append(new PlanCreated(planId, run.RunId,
                PlanItemId.New(), "durable user.ask fixture"));
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                {
                    ["user.ask"] = PermissionDecision.Allow,
                }), root);
            var selection = new ModelSelection(new ModelIdValue("scripted-user-ask"), 8192,
                ToolMode.Direct, null);
            var fingerprint = new ExecutionFingerprint("scripted-user-ask", "schema", "tools", "context", "output", "M3");
            var calls = 0;
            var toolCallId = ToolCallId.New();
            string? returnedResult = null;
            ModelResponse Provider(ModelRequest request, CancellationToken _)
            {
                calls++;
                if (calls == 1)
                {
                    return new ModelResponse(new ContentBlock[]
                    {
                        new ToolCallBlock(toolCallId, "fixture-ask-call", "user.ask", QuestionnaireCodec.EncodeSchema(MixedSchema)),
                    }, StopReason.ToolUse, new TokenUsage(3, 2, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null));
                }

                var result = Assert.Single(request.Messages.SelectMany(message => message.Content)
                    .OfType<ToolResultBlock>());
                Assert.Equal(toolCallId, result.Id);
                returnedResult = Assert.Single(result.Content.OfType<TextBlock>()).Text;
                return new ModelResponse(new ContentBlock[] { new TextBlock("fixture answers acknowledged") },
                    StopReason.EndTurn, new TokenUsage(2, 1, 0, 0, 0), null,
                    new ProviderMetadata("fixture", "fixture", null));
            }

            QuestionnaireInteractionService NewQuestionnaires() =>
                new(store!, codecs, artifacts);
            ExplorerTurn NewTurn(QuestionnaireInteractionService questionnaires) => new(Provider, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()), fingerprint,
                selection, store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: questionnaires, recordEffectiveFingerprint: true);

            var suspended = NewTurn(NewQuestionnaires()).Ask("ask for review preferences", "system", session,
                run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            var pending = Assert.Single(NewQuestionnaires().Pending(session));
            Assert.NotNull(pending.SchemaRef);
            Assert.Equal(1, calls);

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(blobs);
            var serviceAfterPublishReopen = NewQuestionnaires();
            var restoredSchema = serviceAfterPublishReopen.SchemaFor(new EventStream(store, codecs, session), pending.InteractionId);
            Assert.NotNull(restoredSchema);
            Assert.Equal(new[] { QuestionKind.SingleChoice, QuestionKind.MultipleChoice, QuestionKind.FreeText },
                restoredSchema!.Questions.Select(question => question.Kind));

            var marker = "Fixture answer " + Guid.NewGuid().ToString("N");
            var answers = new QuestionAnswer[]
            {
                new("style", new[] { "inline" }, null, null),
                new("checks", new[] { "tests", "other" }, null, marker),
                new("note", Array.Empty<string>(), marker, null),
            };
            var stateFile = Path.Combine(root, "last-session.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId, new UTF8Encoding(false));
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            var accepted = server.RespondToQuestionnaire(pending.InteractionId, answers, cancelled: false);
            Assert.Equal("ok", accepted.Status);

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(blobs);
            var resumedService = NewQuestionnaires();
            var resolved = Assert.IsType<QuestionnaireAskOutcome>(resumedService.ResolvedOutcome(session, pending.InteractionId));
            Assert.True(resolved.IsAnswered);
            Assert.Contains(resolved.Answers!, answer => answer.Text == marker || answer.OtherText == marker);

            var resumed = NewTurn(resumedService).Ask("resume", "system", session,
                run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, resumed.StopReason);
            Assert.Equal(2, calls);
            Assert.Contains(marker, returnedResult ?? "", StringComparison.Ordinal);
            Assert.Contains("\"status\":\"answered\"", returnedResult ?? "", StringComparison.Ordinal);
        }
        finally
        {
            store?.Close();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Configured_luna_selects_user_ask_and_receives_the_durable_fixture_answer()
    {
        var optIn = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_USER_ASK");
        if (string.IsNullOrWhiteSpace(optIn))
            Assert.Skip("Set OMNICORE_LIVE_ACCEPTANCE_USER_ASK=luna to make the bounded live acceptance call.");
        if (optIn != "luna") throw new InvalidOperationException("Live user.ask opt-in must be exactly luna.");

        var startedAt = DateTimeOffset.UtcNow;
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "OmniCoreLiveUserAsk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var fixtureMarker = "UserAskFixture-" + Guid.NewGuid().ToString("N");
        var fixtureText = "The explicit fixture answer is " + fixtureMarker;
        var caseStatus = "failed";
        var toolRequested = false;
        var toolSchemaValid = false;
        var toolRuntimeExecuted = false;
        var interactionPublished = false;
        var responseAccepted = false;
        var journalReopened = false;
        var resultReturned = false;
        string? argumentHash = null;
        string? questionnaireKinds = null;
        SqliteEventStore? store = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var paths = ExistingLiveConnectionPaths();
            var registry = OmniHost.LoadUserModelRegistry(paths);
            const string modelId = "gpt-6-luna";
            var model = registry.Model(modelId)
                ?? throw new InvalidOperationException("configured_luna_model_missing");
            var providerDescriptor = registry.Provider(model.ProviderId)
                ?? throw new InvalidOperationException("configured_luna_provider_missing");
            var provider = ConnectConfiguredLuna(model, providerDescriptor, paths);
            var selection = new ModelSelection(new ModelIdValue(modelId), model.ContextWindow,
                ToolMode.Direct, null, maxOutputTokens: null);

            var askTool = new UserAskTool();
            var alternateTool = HostTools.Explorer().Catalog().Find(new ToolId("filesystem.list"))
                ?? throw new InvalidOperationException("live_user_ask_alternative_tool_missing");
            var toolDefinitions = new[]
            {
                new ToolDefinition(askTool.Descriptor.Id.ToString(), askTool.Descriptor.Description,
                    askTool.Descriptor.InputSchema.ToString()),
                new ToolDefinition(alternateTool.Descriptor.Id.ToString(), alternateTool.Descriptor.Description,
                    alternateTool.Descriptor.InputSchema.ToString()),
            };
            const string userPrompt = "I am configuring a code review workflow. Interview me with a short structured questionnaire: "
                + "ask my preferred review style as a single choice, which review checks I want as multiple choice with an Other option, "
                + "and an optional free-text note. Use clear non-empty option labels and IDs, valid selection bounds, and no credentials or approval questions.";
            var askRequest = new ModelRequest(selection,
                new[] { new ModelMessage(MessageRole.User, new ContentBlock[] { new TextBlock(userPrompt) }) },
                "Use user.ask only when a structured questionnaire is genuinely needed. The filesystem.list tool is an unrelated alternative; do not call it for this request.",
                toolDefinitions, ToolChoice.Auto(), null, null, null, null);
            var askResponse = await Complete(provider, askRequest, timeout.Token);
            var calls = askResponse.Content.OfType<ToolCallBlock>().ToArray();
            Assert.Single(calls);
            var call = calls[0];
            Assert.Equal("user.ask", call.ToolName);
            toolRequested = true;
            argumentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgumentsJson)))
                .ToLowerInvariant();

            var schema = QuestionnaireCodec.DecodeSchema(call.ArgumentsJson)
                ?? throw new InvalidOperationException("live_user_ask_schema_decode_failed");
            var validation = QuestionnaireValidator.ValidateSchema(schema, QuestionnaireLimits.Default());
            Assert.True(validation.Valid, "live_user_ask_schema_invalid:" + string.Join(",",
                validation.Errors.Select(error => error.Code.ToString())));
            Assert.Contains(schema.Questions, question => question.Kind == QuestionKind.SingleChoice);
            Assert.Contains(schema.Questions, question => question.Kind == QuestionKind.MultipleChoice
                && question.Other is not null);
            Assert.Contains(schema.Questions, question => question.Kind == QuestionKind.FreeText);
            toolSchemaValid = true;
            questionnaireKinds = string.Join(",", schema.Questions.Select(question => question.Kind.ToString())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

            store = new SqliteEventStore(Path.Combine(fixtureRoot, "journal.db"));
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(fixtureRoot, "artifacts"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var toolCatalog = new FakeCatalog().Add(askTool);
            var permissions = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>(StringComparer.Ordinal)
            {
                ["user.ask"] = PermissionDecision.Allow,
            });
            var pipelineEvents = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(toolCatalog, permissions, payload =>
            {
                pipelineEvents.Add(payload);
                return VoidBox.Instance;
            });
            var validatedCall = new ValidatedToolCall(call.Id, askTool.Descriptor.Id,
                call.ProviderCallId ?? "live-user-ask", call.ArgumentsJson);
            var runtimeOutcome = runtime.Run(validatedCall,
                new ToolPreparationContext(fixtureRoot, DateTimeOffset.UtcNow),
                new ToolExecutionContext(fixtureRoot, readRegistry: null,
                    emitEvent: payload => pipelineEvents.Add(payload),
                    artifacts: artifacts), userApprovesAsk: false, timeout.Token);
            Assert.True(runtimeOutcome.Succeeded, "live_user_ask_tool_runtime_rejected:" + runtimeOutcome.FinalState);
            Assert.Contains(pipelineEvents, payload => payload is ToolCallStarted);
            Assert.Contains(pipelineEvents, payload => payload is ToolCallSucceeded);
            toolRuntimeExecuted = true;

            var questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var interactionId = InteractionId.New();
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgumentsJson)))
                .ToLowerInvariant();
            var published = questionnaires.Publish(new EventStream(store, codecs, session), schema,
                interactionId, run.RootLane, "{\"tool\":\"user.ask\",\"argumentsHash\":\"" + digest + "\"}");
            Assert.True(published.Published, "live_user_ask_publish_rejected");
            interactionPublished = true;

            store.Close();
            store = new SqliteEventStore(Path.Combine(fixtureRoot, "journal.db"));
            artifacts = new FileArtifactStore(Path.Combine(fixtureRoot, "artifacts"));
            questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            Assert.NotNull(questionnaires.SchemaFor(new EventStream(store, codecs, session), interactionId));
            journalReopened = true;

            var answers = CreateFixtureAnswers(schema, fixtureText);
            Assert.True(QuestionnaireValidator.ValidateResponse(schema, answers, cancelled: false,
                QuestionnaireLimits.Default()).Valid);
            var stateFile = Path.Combine(fixtureRoot, "last-session.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId, new UTF8Encoding(false));
            var fixturePaths = new DefaultPlatformPaths(Path.Combine(fixtureRoot, "user-data"),
                Path.Combine(fixtureRoot, "user-config"));
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts,
                platformPathsFactory: () => fixturePaths);
            var responseAck = server.RespondToQuestionnaire(interactionId, answers, cancelled: false);
            Assert.Equal("ok", responseAck.Status);
            responseAccepted = true;

            store.Close();
            store = new SqliteEventStore(Path.Combine(fixtureRoot, "journal.db"));
            artifacts = new FileArtifactStore(Path.Combine(fixtureRoot, "artifacts"));
            questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var restored = questionnaires.ResolvedOutcome(session, interactionId)
                ?? throw new InvalidOperationException("live_user_ask_resolved_outcome_missing_after_reopen");
            Assert.True(restored.IsAnswered);
            Assert.Contains(restored.Answers!, answer => answer.Text == fixtureText || answer.OtherText == fixtureText);

            var resultJson = "{\"status\":\"answered\",\"answers\":"
                + QuestionnaireCodec.EncodeAnswers(restored.Answers!) + "}";
            var resultCall = new ModelMessage(MessageRole.Tool, new ContentBlock[]
            {
                new ToolResultBlock(call.Id, new ContentBlock[] { new TextBlock(resultJson) }, false),
            });
            var followUp = new ModelRequest(selection,
                new ModelMessage[]
                {
                    new(MessageRole.User, new ContentBlock[] { new TextBlock(userPrompt) }),
                    new(MessageRole.Assistant, askResponse.Content),
                    resultCall,
                    new(MessageRole.User, new ContentBlock[]
                    {
                        new TextBlock("Confirm that the questionnaire was answered and include the exact fixture free-text value from the tool result."),
                    }),
                },
                "Use only the conversation and the returned questionnaire result. Do not invent any answers.",
                Array.Empty<ToolDefinition>(), ToolChoice.None(), null, null, null, askResponse.State);
            var finalResponse = await Complete(provider, followUp, timeout.Token);
            var finalText = string.Concat(finalResponse.Content.OfType<TextBlock>().Select(block => block.Text));
            Assert.Contains(fixtureMarker, finalText, StringComparison.Ordinal);
            resultReturned = true;
            caseStatus = "success";

            WriteEvidence(new
            {
                caseId = "user.ask",
                modelId,
                status = caseStatus,
                invocationMode = "tool_choice_auto_with_unrelated_alternative",
                generationCalls = 2,
                modelRequestedUserAsk = toolRequested,
                argumentsSha256 = argumentHash,
                schemaValid = toolSchemaValid,
                questionKinds = questionnaireKinds,
                includesOtherChoice = true,
                toolRuntimeExecuted,
                interactionPublished,
                journalReopenedAfterPublication = journalReopened,
                fixtureResponseAccepted = responseAccepted,
                journalReopenedAfterResponse = true,
                toolResultReturnedToSameModelCall = resultReturned,
                fixtureTextReturnedByModel = true,
                startedAt,
                completedAt = DateTimeOffset.UtcNow,
            }, fixtureRoot);
        }
        catch (Exception failure)
        {
            WriteEvidence(new
            {
                caseId = "user.ask",
                modelId = "gpt-6-luna",
                status = caseStatus,
                modelRequestedUserAsk = toolRequested,
                argumentsSha256 = argumentHash,
                schemaValid = toolSchemaValid,
                questionKinds = questionnaireKinds,
                toolRuntimeExecuted,
                interactionPublished,
                journalReopenedAfterPublication = journalReopened,
                fixtureResponseAccepted = responseAccepted,
                toolResultReturnedToSameModelCall = resultReturned,
                failureCode = SafeFailureCode(failure),
                startedAt,
                completedAt = DateTimeOffset.UtcNow,
            }, fixtureRoot);
            throw new InvalidOperationException("live_user_ask_acceptance_failed:" + SafeFailureCode(failure));
        }
        finally
        {
            store?.Close();
        }
    }

    private static string[] Required(JsonElement schema) => schema.GetProperty("required")
        .EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static IReadOnlyList<QuestionAnswer> CreateFixtureAnswers(QuestionnaireSchema schema, string fixtureText)
    {
        var answers = new List<QuestionAnswer>();
        foreach (var question in schema.Questions)
        {
            switch (question.Kind)
            {
                case QuestionKind.SingleChoice:
                    var single = question.Options.FirstOrDefault()?.Id
                        ?? question.Other?.OptionId
                        ?? throw new InvalidOperationException("live_user_ask_single_choice_has_no_selectable_option");
                    answers.Add(new QuestionAnswer(question.Id, new[] { single }, null,
                        question.Other?.OptionId == single ? fixtureText : null));
                    break;
                case QuestionKind.MultipleChoice:
                    var minimum = Math.Max(1, question.MinSelections ?? 1);
                    var maximum = question.MaxSelections ?? int.MaxValue;
                    if (minimum > maximum)
                        throw new InvalidOperationException("live_user_ask_multi_choice_bounds_inconsistent");
                    var selected = question.Other is null
                        ? question.Options.Take(minimum).Select(option => option.Id).ToList()
                        : question.Options.Take(minimum - 1).Select(option => option.Id).ToList();
                    string? otherText = null;
                    if (question.Other is not null)
                    {
                        if (selected.Count >= maximum)
                            throw new InvalidOperationException("live_user_ask_other_selection_exceeds_maximum");
                        selected.Add(question.Other.OptionId);
                        otherText = fixtureText;
                    }
                    while (question.MinSelections is { } min && selected.Count < min)
                    {
                        var next = question.Options.FirstOrDefault(option => !selected.Contains(option.Id, StringComparer.Ordinal));
                        if (next is null) throw new InvalidOperationException("live_user_ask_multi_choice_minimum_unreachable");
                        selected.Add(next.Id);
                    }
                    if (selected.Count > maximum)
                        throw new InvalidOperationException("live_user_ask_multi_choice_bounds_inconsistent");
                    answers.Add(new QuestionAnswer(question.Id, selected, null, otherText));
                    break;
                case QuestionKind.FreeText:
                    answers.Add(new QuestionAnswer(question.Id, Array.Empty<string>(), fixtureText, null));
                    break;
                default:
                    throw new InvalidOperationException("live_user_ask_unknown_question_kind");
            }
        }
        return answers;
    }

    private static IPlatformPaths ExistingLiveConnectionPaths()
    {
        var data = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_DATA_DIR");
        var config = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(config)
            || !Path.IsPathFullyQualified(data) || !Path.IsPathFullyQualified(config)
            || !Directory.Exists(data) || !Directory.Exists(config))
            throw new InvalidOperationException("live_acceptance_existing_connection_paths_required");
        return new DefaultPlatformPaths(data, config);
    }

    private static IModelProvider ConnectConfiguredLuna(ModelDefinition model,
        ProviderDescriptor provider, IPlatformPaths paths)
    {
        if (model.Id != "gpt-6-luna" || !provider.SupportsNativeToolCalls
            || provider.Family != ProviderFamily.OpenAIResponses || provider.Profile != "codex"
            || provider.BillingMode != BillingMode.IncludedQuota)
            throw new InvalidOperationException("configured_luna_subscription_profile_mismatch");
        var auth = OmniHost.CreateChatGptAuth(paths);
        var status = auth.Status(CancellationToken.None);
        if (!status.LoggedIn || status.Expired || status.ExpiresAt is not { } expiry
            || expiry - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("configured_luna_subscription_not_fresh_enough_without_refresh");
        return OmniHost.ConnectProvider(provider, provider.BaseUrl, provider.Auth.SecretRef ?? "", "",
            subscription: new NoRefreshSubscriptionSource(auth));
    }

    private sealed class NoRefreshSubscriptionSource(ChatGptSubscriptionAuthProvider auth)
        : ISubscriptionCredentialSource
    {
        public ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = auth.Status(cancellationToken);
            if (!status.LoggedIn || status.Expired || status.ExpiresAt is not { } expiry
                || expiry - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(15))
                throw new InvalidOperationException("live_acceptance_subscription_expired_without_refresh");
            return auth.GetAsync(cancellationToken);
        }

        public ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<SubscriptionCredential>(new InvalidOperationException("live_acceptance_refresh_disabled"));
    }

    private static async Task<ModelResponse> Complete(IModelProvider provider, ModelRequest request,
        CancellationToken cancellationToken)
    {
        ModelResponse? response = null;
        await foreach (var item in provider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            if (item is ResponseCompleted completed) response = completed.Response;
        return response ?? throw new InvalidOperationException("live_user_ask_response_incomplete");
    }

    private static string SafeFailureCode(Exception exception) => exception switch
    {
        ModelProviderException provider => "provider_kind:" + new string(provider.Kind.Where(char.IsAsciiLetterOrDigit)
            .Take(48).ToArray()) + ";status_code:" + (provider.StatusCode?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "none"),
        ChatGptAuthException auth => "chatgpt_auth_kind:" + new string(auth.Kind.Where(char.IsAsciiLetterOrDigit)
            .Take(48).ToArray()),
        HttpRequestException => "transport_failure",
        OperationCanceledException => "bounded_timeout_or_cancellation",
        _ => "exception_type:" + exception.GetType().Name,
    };

    private static void WriteEvidence(object value, string fixtureRoot)
    {
        var directory = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) directory = Path.Combine(fixtureRoot, "evidence");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "user-ask-luna.json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static string[] Types(JsonElement schema)
    {
        var type = schema.GetProperty("type");
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : new[] { type.GetString()! };
    }
}
