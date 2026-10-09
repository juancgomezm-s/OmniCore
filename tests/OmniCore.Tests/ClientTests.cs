using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Protocol;
using System.Text.Json;

namespace OmniCore.Tests;

// PlainRenderer captures the process-wide Console.Out.
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class ClientTests
{
    [Fact]
    public void Process_slash_arguments_are_not_joined_and_reparsed()
    {
        var arguments = new[]
        {
            "/orq-auth", "connect a module to the application's real entry point",
            "--verify-executable", "C:\\Program Files\\dotnet.exe",
            "--verify-argv-json", "[\"test\", \"tests/Connected Module.csproj\"]",
        };

        Assert.True(CommandLineParser.TryParseArguments(arguments, out var invocation));
        Assert.Equal("orq-auth", invocation!.Name);
        Assert.Equal(arguments[1..], invocation.Arguments);
    }

    [Fact]
    public void Process_parser_preserves_empty_arguments_quotes_and_windows_backslashes()
    {
        var arguments = new[] { "/core:explain", "", "C:\\tools\\a\"b.exe", "[\"literal\\\\path\"]" };

        Assert.True(CommandLineParser.TryParseArguments(arguments, out var invocation));
        Assert.Equal("core:explain", invocation!.Name);
        Assert.Equal(arguments[1..], invocation.Arguments);

        Assert.False(CommandLineParser.TryParseArguments(["/core:orq-auth \"unfinished"], out var malformed));
        Assert.Null(malformed);
    }

    [Theory]
    [MemberData(nameof(InvalidProcessSlashArguments))]
    public void Process_slash_argument_parser_rejects_invalid_prefixes(string[] arguments)
    {
        Assert.False(CommandLineParser.TryParseArguments(arguments, out var invocation));
        Assert.Null(invocation);
    }

    public static IEnumerable<object[]> InvalidProcessSlashArguments() => new object[][]
    {
        new object[] { Array.Empty<string>() },
        new object[] { new[] { "ask", "question" } },
        new object[] { new[] { "/", "question" } },
        new object[] { new[] { "/orq-auth objective", "--verify-executable", "dotnet" } },
    };

    [Fact]
    public async Task Projection_turns_sim_event_into_system_block()
    {
        var projection = new ClientProjection();
        var state = ClientState.Empty();
        var envelope = WireEnvelope.Event(Ids.NewV7(),
            "{" + JsonObj.Field("type", "sim.events") + "," + JsonObj.Field("run", "Completed")
            + "," + JsonObj.Field("exitCode", "0") + "}");

        var next = projection.Apply(state, envelope);

        Assert.Single(next.Conversation.Blocks);
        var block = next.Conversation.Blocks[0];
        Assert.Equal(ConversationRole.System, block.Role);
        Assert.Contains("Completed", block.Text);
    }

    [Fact]
    public void Plain_renderer_shows_effect_resolution_options_and_no_tty_guidance()
    {
        var projection = new ClientProjection(Localization.Spanish());
        var interactionId = Guid.CreateVersion7().ToString();
        var envelope = WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.Field("type", "interaction.requested") + ","
            + JsonObj.Field("interactionId", interactionId) + ","
            + JsonObj.Field("kind", "ReconciliationConflict") + ","
            + JsonObj.Field("options", "resolution_applied,resolution_not_applied") + ","
            + JsonObj.Field("subject", "{\"toolOrExecutable\":\"filesystem.patch\",\"target\":\"src/file.txt\"}") + "}");
        var state = projection.Apply(ClientState.Empty(), envelope);
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            new PlainRenderer("es").Render(state);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("filesystem.patch", output.ToString());
        Assert.Contains("src/file.txt", output.ToString());
        Assert.Contains("resolution_applied", output.ToString());
        Assert.Contains("omni resolve " + interactionId, output.ToString());
    }

    [Fact]
    public async Task Projection_ignores_non_event_messages()
    {
        var projection = new ClientProjection();
        var state = ClientState.Empty();
        var envelope = WireEnvelope.Hello();

        var next = projection.Apply(state, envelope);

        Assert.Empty(next.Conversation.Blocks);
    }

    [Fact]
    public async Task Projection_local_draft_updates_composer()
    {
        var projection = new ClientProjection();
        var state = ClientState.Empty();

        var next = projection.ApplyLocal(state, LocalAction.Draft("hola omni"));

        Assert.Equal("hola omni", next.Composer.Draft);
    }

    [Fact]
    public void Questionnaire_parser_uses_published_choice_ids_and_builds_typed_answers()
    {
        var questionnaire = new QuestionnaireOverlayModel("Preferencias", null,
            new QuestionnaireQuestionModel[]
            {
                new("approach", "Enfoque", null, QuestionnaireQuestionKind.SingleChoice,
                    new QuestionnaireChoiceModel[] { new("compat", "Compatibilidad") },
                    new QuestionnaireOtherModel("other", "Otro", null, true, 40), true),
                new("checks", "Validaciones", null, QuestionnaireQuestionKind.MultipleChoice,
                    new QuestionnaireChoiceModel[] { new("unit", "Unitarias"), new("lint", "Lint") },
                    null, false),
                new("notes", "Notas", null, QuestionnaireQuestionKind.FreeText,
                    Array.Empty<QuestionnaireChoiceModel>(), null, false, maxTextLength: 100),
            }, "Enviar", "Cancelar");
        var inputs = new Dictionary<string, QuestionnairePlainFormInput>
        {
            ["approach"] = new("other", otherText: "Mi enfoque"),
            ["checks"] = new("unit lint"),
            ["notes"] = new(text: "Conservar el comportamiento existente"),
        };

        var result = QuestionnairePlainFormParser.Parse(questionnaire, inputs);

        Assert.True(result.IsValid);
        var response = result.Response ?? throw new InvalidOperationException("Expected parsed questionnaire response.");
        Assert.False(response.Cancelled);
        Assert.Equal(3, response.Answers.Count);
        Assert.Equal(new[] { "other" }, response.Answers[0].SelectedOptionIds);
        Assert.Equal("Mi enfoque", response.Answers[0].OtherText);
        Assert.Equal(new[] { "unit", "lint" }, response.Answers[1].SelectedOptionIds);
        Assert.Equal("Conservar el comportamiento existente", response.Answers[2].Text);
    }

    [Fact]
    public void Questionnaire_parser_rejects_unknown_and_duplicate_choices()
    {
        var questionnaire = new QuestionnaireOverlayModel("Q", null,
            new QuestionnaireQuestionModel[]
            {
                new("q", "Pregunta", null, QuestionnaireQuestionKind.MultipleChoice,
                    new QuestionnaireChoiceModel[] { new("yes", "Sí") }, null, false),
            }, "Enviar", "Cancelar");

        var result = QuestionnairePlainFormParser.Parse(questionnaire,
            new Dictionary<string, QuestionnairePlainFormInput>
            {
                ["q"] = new("yes,yes,No"),
            });

        Assert.False(result.IsValid);
        Assert.Null(result.Response);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, error => error.Code == QuestionnaireValidationErrorCode.DuplicateChoiceId);
        Assert.Contains(result.Errors, error => error.Code == QuestionnaireValidationErrorCode.UnknownChoiceId);
        var spanish = new Localization("es").ResolveQuestionnaireValidationError(result.Errors[0]);
        var english = new Localization("en").ResolveQuestionnaireValidationError(result.Errors[0]);
        Assert.NotEqual(spanish, english);
        Assert.DoesNotContain("questionnaire.validation.", spanish);
        var tui = QuestionnaireTuiForm.Submit(questionnaire,
            new Dictionary<string, QuestionnairePlainFormInput> { ["q"] = new("yes,yes,No") });
        Assert.Equal(result.Errors.Select(error => error.Code), tui.Errors.Select(error => error.Code));
    }

    [Fact]
    public void Tui_layout_collapses_sidebar_at_configured_breakpoints()
    {
        Assert.Equal(TuiLayoutMode.Stacked, TuiLayoutModel.ForWidth(120).Mode);
        Assert.Equal(40, TuiLayoutModel.ForWidth(120).SidebarWidth);
        Assert.Equal(TuiLayoutMode.Tabbed, TuiLayoutModel.ForWidth(100).Mode);
        Assert.Equal(26, TuiLayoutModel.ForWidth(100).SidebarWidth);
        Assert.Equal(TuiLayoutMode.Overlay, TuiLayoutModel.ForWidth(80, false).Mode);
        Assert.False(TuiLayoutModel.ForWidth(80, false).SidebarVisible);
        Assert.True(TuiLayoutModel.ForWidth(80, true).SidebarVisible);
    }

    [Fact]
    public void Autocomplete_uses_command_catalog_and_workspace_paths()
    {
        var commands = ComposerAutocomplete.Complete("/pla", new[] { "plan", "cancel", "play" }, Array.Empty<string>());
        Assert.Equal(new[] { "/plan", "/play" }, commands.Select(item => item.Value));
        var files = ComposerAutocomplete.Complete("@src/", Array.Empty<string>(), new[] { "src/A.cs", "tests/A.cs" });
        Assert.Equal(new[] { "@src/A.cs" }, files.Select(item => item.Value));
    }

    [Fact]
    public void Status_line_never_invents_unknown_quota()
    {
        var status = StatusLinePresentation.From(StatusLineModel.Empty());
        Assert.Equal("—", status.Right);
    }

    [Fact]
    public void Deleting_policy_makes_exact_model_require_onboarding_again()
    {
        var key = new OmniCore.Host.ModelPolicyKeyDto("local", "model");
        var host = OmniCore.Host.ModelPolicyHost.Create(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            new[] { new OmniCore.Host.ModelRegistryModelDescriptor("model", "local", 4096, 3072, 1024) });
        Assert.Null(host.Get(key, CancellationToken.None));
        var draft = host.Draft(key, CancellationToken.None);
        var setup = ModelPolicySetupModel.From(key.ToString(), draft.RecommendedCategory, draft.Warnings);
        Assert.Contains(setup.Choices, choice => choice.Category == draft.RecommendedCategory && choice.Recommended);
        var policy = host.Set(key, 0, "ObserveOnly", null, CancellationToken.None);
        Assert.NotNull(host.Get(key, CancellationToken.None));
        host.Delete(key, policy.Revision, CancellationToken.None);
        Assert.Null(host.Get(key, CancellationToken.None));
        Assert.Equal("ObserveOnly", host.Draft(key, CancellationToken.None).RecommendedCategory);
    }

    [Fact]
    public void Host_autocomplete_query_returns_only_prefix_matched_workspace_files_and_host_commands()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-tui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "src", "Alpha.cs"), "");
        File.WriteAllText(Path.Combine(root, "src", "Beta.cs"), "");
        File.WriteAllText(Path.Combine(root, ".git", "Alpha.secret"), "");
        try
        {
            var server = OmniCore.Host.OmniHost.CreateInMemoryServer();
            server.ConfigureWorkspaceRoot(root);
            var commands = server.Query("commands", CancellationToken.None)!.Json;
            Assert.Contains("explain", commands);
            using (var catalog = JsonDocument.Parse(commands))
            {
                var workflow = Assert.Single(catalog.RootElement.GetProperty("catalog").GetProperty("commands")
                    .EnumerateArray(), command => command.GetProperty("id").GetString() == "core:orq-auth");
                Assert.False(workflow.GetProperty("enabled").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(workflow.GetProperty("disabledReason").GetString()));
            }
            var completion = server.Query("complete:src/Al", CancellationToken.None)!.Json;
            Assert.Contains("src/Alpha.cs", completion);
            Assert.DoesNotContain("Beta.cs", completion);
            Assert.DoesNotContain("Alpha.secret", completion);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Questionnaire_factory_builds_the_shared_plain_and_tui_presentation_model()
    {
        const string schema = "{\"title\":\"Question\",\"questions\":[{\"id\":\"q\",\"prompt\":\"Pick\",\"kind\":\"SingleChoice\",\"options\":[{\"id\":\"a\",\"label\":\"A\"}],\"other\":{\"optionId\":\"other\",\"label\":\"Otro\",\"textRequired\":true,\"maxTextLength\":20},\"required\":true}]}";
        var model = QuestionnairePresentationFactory.FromJson(schema);
        Assert.NotNull(model);
        Assert.Equal(QuestionnaireQuestionKind.SingleChoice, model!.Questions[0].Kind);
        Assert.Equal("other", model.Questions[0].Other!.OptionId);
        Assert.Equal("Enviar", model.SubmitLabel);
    }

    [Fact]
    public void Tui_sidebar_host_builds_session_plan_and_files_as_framework_free_models()
    {
        var state = ClientState.Empty();
        var widgets = new SidebarHost(new ISidebarWidget[]
        {
            new SessionSidebarWidget(new SessionWidgetData("session", "act")),
            new PlanSidebarWidget(new PlanWidgetData("PLAN 1/1", new[] { new WidgetRowModel("Work", ThemeRole.Active) })),
            new ChangedFilesSidebarWidget(new ChangedFileWidgetData(new[] { new WidgetRowModel("M A.cs", ThemeRole.Info) })),
        }).Build(state, WidgetSize.Normal);
        Assert.Equal(new[] { "core.session", "core.plan", "core.files" }, widgets.Select(item => item.Widget.Id));
        Assert.Equal("→", ThemeGlyphs.For(ThemeRole.Active));
    }

    [Fact]
    public void Tui_questionnaire_submission_matches_plain_typed_response()
    {
        var questionnaire = new QuestionnaireOverlayModel("Preferencias", null,
            new QuestionnaireQuestionModel[]
            {
                new("single", "Una", null, QuestionnaireQuestionKind.SingleChoice,
                    new[] { new QuestionnaireChoiceModel("a", "A") }, null, true),
                new("multi", "Varias", null, QuestionnaireQuestionKind.MultipleChoice,
                    new[] { new QuestionnaireChoiceModel("b", "B"), new QuestionnaireChoiceModel("c", "C") }, null, false),
                new("text", "Texto", null, QuestionnaireQuestionKind.FreeText,
                    Array.Empty<QuestionnaireChoiceModel>(), null, false),
                new("other", "Otro", null, QuestionnaireQuestionKind.SingleChoice,
                    new[] { new QuestionnaireChoiceModel("yes", "Sí") },
                    new QuestionnaireOtherModel("other-id", "Otro", null, true, 30), true),
            }, "Enviar", "Cancelar");
        var input = new Dictionary<string, QuestionnairePlainFormInput>
        {
            ["single"] = new("a"), ["multi"] = new("b c"), ["text"] = new(text: "respuesta"),
            ["other"] = new("other-id", otherText: "propia"),
        };
        var plain = QuestionnairePlainFormParser.Parse(questionnaire, input);
        var tui = QuestionnaireTuiForm.Submit(questionnaire, input);
        Assert.True(plain.IsValid);
        Assert.True(tui.IsValid);
        Assert.Equal(plain.Response!.Cancelled, tui.Response!.Cancelled);
        Assert.Equal(plain.Response.Answers.Select(a => (a.QuestionId, string.Join(",", a.SelectedOptionIds), a.Text, a.OtherText)),
            tui.Response.Answers.Select(a => (a.QuestionId, string.Join(",", a.SelectedOptionIds), a.Text, a.OtherText)));
    }

    [Fact]
    public void Questionnaire_parser_returns_structured_cancellation_without_answers()
    {
        var questionnaire = new QuestionnaireOverlayModel("Q", null,
            new QuestionnaireQuestionModel[]
            {
                new("required", "Obligatoria", null, QuestionnaireQuestionKind.FreeText,
                    Array.Empty<QuestionnaireChoiceModel>(), null, true),
            }, "Enviar", "Cancelar");

        var result = QuestionnairePlainFormParser.Parse(questionnaire,
            new Dictionary<string, QuestionnairePlainFormInput>(), cancelled: true);

        Assert.True(result.IsValid);
        Assert.True(result.Response!.Cancelled);
        Assert.Empty(result.Response.Answers);
    }

    [Fact]
    public void CliApp_messages_resolve_in_spanish_and_english()
    {
        var spanish = new Localization("es");
        var english = new Localization("en");

        Assert.Equal("omni sim: no existe el escenario escenarios/demo.yaml",
            spanish.Resolve("cli.sim.no_scenario", "path", "escenarios/demo.yaml"));
        Assert.Equal("omni sim: scenario does not exist: escenarios/demo.yaml",
            english.Resolve("cli.sim.no_scenario", "path", "escenarios/demo.yaml"));

        Assert.Equal("omni: intención asumida como pregunta → ask 'ayuda'",
            spanish.Resolve("cli.ask_assumed", "command", "ayuda"));
        Assert.Equal("omni: intent assumed as a question → ask 'ayuda'",
            english.Resolve("cli.ask_assumed", "command", "ayuda"));

        Assert.Equal("Workspace marcado como no confiable.", spanish.Resolve("cli.trust.revoked"));
        Assert.Equal("Workspace marked as untrusted.", english.Resolve("cli.trust.revoked"));
    }
}
