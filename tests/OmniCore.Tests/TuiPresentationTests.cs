using OmniCore.Client;

namespace OmniCore.Tests;

/// <summary>Unit tests for the framework-free TUI presentation logic (ADR-0030–0033).</summary>
public sealed class TuiPresentationTests
{
    #region ComposerAutocomplete — / commands

    [Fact]
    public void SlashComplete_ReturnsPrefixMatchedCommands_CaseInsensitive()
    {
        var commands = new[] { "plan", "cancel", "play", "explain" };
        var result = ComposerAutocomplete.Complete("/pl", commands, Array.Empty<string>());

        Assert.Equal(new[] { "/plan", "/play" }, result.Select(r => r.Value));
        Assert.All(result, r => Assert.Equal(ThemeRole.Active, r.Role));
        Assert.All(result, r => Assert.Equal("command", r.Description));
    }

    [Fact]
    public void SlashComplete_OrdersAlphabetically_CaseInsensitive()
    {
        var commands = new[] { "zebra", "alpha", "beta", "gamma" };
        var result = ComposerAutocomplete.Complete("/a", commands, Array.Empty<string>());

        Assert.Equal(new[] { "/alpha" }, result.Select(r => r.Value));
    }

    [Fact]
    public void SlashComplete_EmptyPrefix_ReturnsAllCommandsSorted()
    {
        var commands = new[] { "plan", "cancel", "play" };
        var result = ComposerAutocomplete.Complete("/", commands, Array.Empty<string>());

        Assert.Equal(new[] { "/cancel", "/plan", "/play" }, result.Select(r => r.Value));
    }

    [Fact]
    public void SlashComplete_ExactMatch_ReturnsSingleSuggestion()
    {
        var commands = new[] { "plan", "cancel" };
        var result = ComposerAutocomplete.Complete("/plan", commands, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal("/plan", result[0].Value);
    }

    [Fact]
    public void SlashComplete_NoMatch_ReturnsEmpty()
    {
        var commands = new[] { "plan", "cancel" };
        var result = ComposerAutocomplete.Complete("/xyz", commands, Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void SlashComplete_IgnoresNonSlashInput_ReturnsEmpty()
    {
        var commands = new[] { "plan", "cancel" };
        var result = ComposerAutocomplete.Complete("plain text", commands, Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void SlashComplete_EmptyDraft_ReturnsEmpty()
    {
        var commands = new[] { "plan", "cancel" };
        var result = ComposerAutocomplete.Complete("", commands, Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void SlashComplete_NullDraft_ReturnsEmpty()
    {
        var commands = new[] { "plan", "cancel" };
        var result = ComposerAutocomplete.Complete(null!, commands, Array.Empty<string>());

        Assert.Empty(result);
    }

    #endregion

    #region ComposerAutocomplete — @ workspace paths

    [Fact]
    public void AtComplete_ReturnsPrefixMatchedPaths_CaseInsensitive()
    {
        var paths = new[] { "src/A.cs", "src/B.cs", "tests/A.cs" };
        var result = ComposerAutocomplete.Complete("@src/", Array.Empty<string>(), paths);

        Assert.Equal(new[] { "@src/A.cs", "@src/B.cs" }, result.Select(r => r.Value));
        Assert.All(result, r => Assert.Equal(ThemeRole.Info, r.Role));
        Assert.All(result, r => Assert.Equal("workspace", r.Description));
    }

    [Fact]
    public void AtComplete_SubfolderPath_ReturnsMatchingFiles()
    {
        var paths = new[] { "src/utils/Helper.cs", "src/core/Engine.cs", "tests/Helper.cs" };
        var result = ComposerAutocomplete.Complete("@src/u", Array.Empty<string>(), paths);

        Assert.Equal(new[] { "@src/utils/Helper.cs" }, result.Select(r => r.Value));
    }

    [Fact]
    public void AtComplete_EmptyPrefix_ReturnsAllPathsSorted()
    {
        var paths = new[] { "zebra.cs", "alpha.cs", "beta.cs" };
        var result = ComposerAutocomplete.Complete("@", Array.Empty<string>(), paths);

        Assert.Equal(new[] { "@alpha.cs", "@beta.cs", "@zebra.cs" }, result.Select(r => r.Value));
    }

    [Fact]
    public void AtComplete_ExactMatch_ReturnsSingleSuggestion()
    {
        var paths = new[] { "src/A.cs", "src/B.cs" };
        var result = ComposerAutocomplete.Complete("@src/A.cs", Array.Empty<string>(), paths);

        Assert.Single(result);
        Assert.Equal("@src/A.cs", result[0].Value);
    }

    [Fact]
    public void AtComplete_NoMatch_ReturnsEmpty()
    {
        var paths = new[] { "src/A.cs", "src/B.cs" };
        var result = ComposerAutocomplete.Complete("@xyz", Array.Empty<string>(), paths);

        Assert.Empty(result);
    }

    [Fact]
    public void AtComplete_IgnoresNonAtInput_ReturnsEmpty()
    {
        var paths = new[] { "src/A.cs" };
        var result = ComposerAutocomplete.Complete("plain text", Array.Empty<string>(), paths);

        Assert.Empty(result);
    }

    #endregion

    #region TuiLayoutModel — width breakpoints

    [Fact]
    public void Layout_Width120OrMore_StackedMode_SidebarWidthClamped32To40()
    {
        var model120 = TuiLayoutModel.ForWidth(120);
        Assert.Equal(TuiLayoutMode.Stacked, model120.Mode);
        Assert.True(model120.SidebarVisible);
        Assert.Equal(40, model120.SidebarWidth); // 120/3 = 40, clamped to max 40

        var model150 = TuiLayoutModel.ForWidth(150);
        Assert.Equal(40, model150.SidebarWidth); // 150/3 = 50, clamped to max 40

        var model96 = TuiLayoutModel.ForWidth(96);
        Assert.Equal(TuiLayoutMode.Tabbed, model96.Mode); // Below 120
    }

    [Fact]
    public void Layout_Width90To119_TabbedMode_SidebarWidth26()
    {
        var model90 = TuiLayoutModel.ForWidth(90);
        Assert.Equal(TuiLayoutMode.Tabbed, model90.Mode);
        Assert.True(model90.SidebarVisible);
        Assert.Equal(26, model90.SidebarWidth);

        var model119 = TuiLayoutModel.ForWidth(119);
        Assert.Equal(TuiLayoutMode.Tabbed, model119.Mode);
        Assert.Equal(26, model119.SidebarWidth);
    }

    [Fact]
    public void Layout_WidthBelow90_OverlayMode_SidebarWidthClamped20To44()
    {
        var model89 = TuiLayoutModel.ForWidth(89);
        Assert.Equal(TuiLayoutMode.Overlay, model89.Mode);
        Assert.True(model89.SidebarVisible);
        Assert.Equal(44, model89.SidebarWidth); // 89-4 = 85, clamped to max 44

        var model50 = TuiLayoutModel.ForWidth(50);
        Assert.Equal(44, model50.SidebarWidth); // 50-4 = 46, clamped to max 44

        var model30 = TuiLayoutModel.ForWidth(30);
        Assert.Equal(26, model30.SidebarWidth); // 30-4 = 26, within 20-44

        var model25 = TuiLayoutModel.ForWidth(25);
        Assert.Equal(21, model25.SidebarWidth); // 25-4 = 21, within 20-44

        var model23 = TuiLayoutModel.ForWidth(23);
        Assert.Equal(20, model23.SidebarWidth); // 23-4 = 19, clamped to min 20
    }

    [Fact]
    public void Layout_SidebarRequestedFalse_HidesSidebarInOverlayMode()
    {
        var model = TuiLayoutModel.ForWidth(80, sidebarRequested: false);

        Assert.Equal(TuiLayoutMode.Overlay, model.Mode);
        Assert.False(model.SidebarVisible);
        Assert.Equal(0, model.SidebarWidth);
    }

    [Fact]
    public void Layout_SidebarRequestedFalse_HidesSidebarInAllModes()
    {
        var stacked = TuiLayoutModel.ForWidth(120, sidebarRequested: false);
        Assert.False(stacked.SidebarVisible);
        Assert.Equal(0, stacked.SidebarWidth);

        var tabbed = TuiLayoutModel.ForWidth(100, sidebarRequested: false);
        Assert.False(tabbed.SidebarVisible);
        Assert.Equal(0, tabbed.SidebarWidth);

        var overlay = TuiLayoutModel.ForWidth(80, sidebarRequested: false);
        Assert.False(overlay.SidebarVisible);
        Assert.Equal(0, overlay.SidebarWidth);
    }

    [Fact]
    public void Layout_BoundaryAt120_IsStackedNotTabbed()
    {
        var model = TuiLayoutModel.ForWidth(120);
        Assert.Equal(TuiLayoutMode.Stacked, model.Mode);
    }

    [Fact]
    public void Layout_BoundaryAt90_IsTabbedNotOverlay()
    {
        var model = TuiLayoutModel.ForWidth(90);
        Assert.Equal(TuiLayoutMode.Tabbed, model.Mode);
    }

    #endregion

    #region StatusLinePresentation

    [Fact]
    public void StatusLine_EmptyModel_ShowsEmDashForMissingQuota()
    {
        var model = StatusLineModel.Empty();
        var presentation = StatusLinePresentation.From(model);

        Assert.Equal("—", presentation.Right);
    }

    [Fact]
    public void StatusLine_QuotaProvided_ShowsQuota()
    {
        var model = new StatusLineModel("45%", null, "act");
        var presentation = StatusLinePresentation.From(model);

        Assert.Equal("45%", presentation.Right);
    }

    [Fact]
    public void StatusLine_WhitespaceQuota_TreatedAsMissing_ShowsEmDash()
    {
        var model = new StatusLineModel("   ", null, "act");
        var presentation = StatusLinePresentation.From(model);

        Assert.Equal("—", presentation.Right);
    }

    [Fact]
    public void StatusLine_PendingInteractions_AppendsToRight()
    {
        var model = new StatusLineModel("60%", 3, "act");
        var presentation = StatusLinePresentation.From(model);

        Assert.Contains("60%", presentation.Right);
        Assert.Contains("! 3 pending", presentation.Right);
    }

    [Fact]
    public void StatusLine_PendingInteractionsWithoutQuota_ShowsEmDashThenPending()
    {
        var model = new StatusLineModel(null, 2, "act");
        var presentation = StatusLinePresentation.From(model);

        Assert.StartsWith("—", presentation.Right);
        Assert.Contains("! 2 pending", presentation.Right);
    }

    [Fact]
    public void StatusLine_ModeShownOnLeft()
    {
        var model = new StatusLineModel(null, null, "ACT");
        var presentation = StatusLinePresentation.From(model);

        Assert.Equal("ACT", presentation.Left);
    }

    [Fact]
    public void StatusLine_SeparatesUltraCodeProductModeFromAppliedNativeReasoning()
    {
        var model = new StatusLineModel(null, null, "plan", "ultracode", "budget:4096");
        var presentation = StatusLinePresentation.From(model);

        Assert.Equal("plan · UltraCode · reasoning budget:4096", presentation.Left);
    }

    #endregion

    #region SidebarHost — widget ordering

    [Fact]
    public void SidebarHost_OrdersByAttentionThenPriorityThenIndex()
    {
        var state = ClientState.Empty();
        var widgets = new SidebarHost(new ISidebarWidget[]
        {
            new TestWidget("low", WidgetRelevance.Normal, 10),
            new TestWidget("high", WidgetRelevance.Attention, 10),
            new TestWidget("medium", WidgetRelevance.Normal, 50),
            new TestWidget("none", WidgetRelevance.None, 100),
        }).Build(state, WidgetSize.Normal);

        Assert.Equal(new[] { "high", "medium", "low" }, widgets.Select(w => w.Widget.Id));
    }

    [Fact]
    public void SidebarHost_ExcludesNoneRelevanceWidgets()
    {
        var state = ClientState.Empty();
        var widgets = new SidebarHost(new ISidebarWidget[]
        {
            new TestWidget("visible", WidgetRelevance.Normal, 10),
            new TestWidget("hidden", WidgetRelevance.None, 100),
        }).Build(state, WidgetSize.Normal);

        Assert.Single(widgets);
        Assert.Equal("visible", widgets[0].Widget.Id);
    }

    [Fact]
    public void SidebarHost_SameRelevanceAndPriority_PreservesRegistrationOrder()
    {
        var state = ClientState.Empty();
        var widgets = new SidebarHost(new ISidebarWidget[]
        {
            new TestWidget("first", WidgetRelevance.Normal, 50),
            new TestWidget("second", WidgetRelevance.Normal, 50),
            new TestWidget("third", WidgetRelevance.Normal, 50),
        }).Build(state, WidgetSize.Normal);

        Assert.Equal(new[] { "first", "second", "third" }, widgets.Select(w => w.Widget.Id));
    }

    #endregion

    #region SessionSidebarWidget

    [Fact]
    public void SessionWidget_AlwaysNormalRelevance()
    {
        var widget = new SessionSidebarWidget(new SessionWidgetData("session-1", "PLAN"));
        var relevance = widget.Evaluate(ClientState.Empty());

        Assert.Equal(WidgetRelevance.Normal, relevance);
    }

    [Fact]
    public void SessionWidget_BuildsListWithTitleAndMode()
    {
        var widget = new SessionSidebarWidget(new SessionWidgetData("MySession", "ACT", "00:05:30"));
        var model = widget.Build(ClientState.Empty(), WidgetSize.Normal);

        Assert.IsType<ListWidgetModel>(model);
        var list = (ListWidgetModel)model;
        Assert.Equal("SESSION", list.Title);
        Assert.Equal(2, list.Rows.Count);
        Assert.Equal("MySession", list.Rows[0].Text);
        Assert.Equal("ACT · 00:05:30", list.Rows[1].Text);
        Assert.Equal(ThemeRole.Muted, list.Rows[1].Role);
    }

    [Fact]
    public void SessionWidget_WithoutElapsed_OmitsElapsed()
    {
        var widget = new SessionSidebarWidget(new SessionWidgetData("MySession", "PLAN", null));
        var model = widget.Build(ClientState.Empty(), WidgetSize.Normal);

        var list = (ListWidgetModel)model;
        Assert.Equal("PLAN", list.Rows[1].Text); // No " · " suffix
    }

    #endregion

    #region PlanSidebarWidget

    [Fact]
    public void PlanWidget_EmptyItems_ReturnsNoneRelevance()
    {
        var widget = new PlanSidebarWidget(new PlanWidgetData("Empty", Array.Empty<WidgetRowModel>()));
        var relevance = widget.Evaluate(ClientState.Empty());

        Assert.Equal(WidgetRelevance.None, relevance);
    }

    [Fact]
    public void PlanWidget_WithItems_ReturnsNormalRelevance()
    {
        var widget = new PlanSidebarWidget(new PlanWidgetData("Plan", new[] { new WidgetRowModel("Step 1") }));
        var relevance = widget.Evaluate(ClientState.Empty());

        Assert.Equal(WidgetRelevance.Normal, relevance);
    }

    [Fact]
    public void PlanWidget_BuildsListWithSummaryAndItems()
    {
        var items = new[] { new WidgetRowModel("Step 1", ThemeRole.Active), new WidgetRowModel("Step 2") };
        var widget = new PlanSidebarWidget(new PlanWidgetData("Summary", items));
        var model = widget.Build(ClientState.Empty(), WidgetSize.Normal);

        var list = (ListWidgetModel)model;
        Assert.Equal("Summary", list.Title);
        Assert.Equal(items, list.Rows);
    }

    #endregion

    #region ChangedFilesSidebarWidget

    [Fact]
    public void ChangedFilesWidget_EmptyFiles_ReturnsNoneRelevance()
    {
        var widget = new ChangedFilesSidebarWidget(new ChangedFileWidgetData(Array.Empty<WidgetRowModel>()));
        var relevance = widget.Evaluate(ClientState.Empty());

        Assert.Equal(WidgetRelevance.None, relevance);
    }

    [Fact]
    public void ChangedFilesWidget_WithFiles_ReturnsNormalRelevance()
    {
        var widget = new ChangedFilesSidebarWidget(new ChangedFileWidgetData(new[] { new WidgetRowModel("M file.cs") }));
        var relevance = widget.Evaluate(ClientState.Empty());

        Assert.Equal(WidgetRelevance.Normal, relevance);
    }

    [Fact]
    public void ChangedFilesWidget_BuildsListWithTitleAndFiles()
    {
        var files = new[] { new WidgetRowModel("M A.cs"), new WidgetRowModel("A B.cs") };
        var widget = new ChangedFilesSidebarWidget(new ChangedFileWidgetData(files));
        var model = widget.Build(ClientState.Empty(), WidgetSize.Normal);

        var list = (ListWidgetModel)model;
        Assert.Equal("FILES", list.Title);
        Assert.Equal(files, list.Rows);
    }

    #endregion

    #region ThemeGlyphs

    [Theory]
    [InlineData(ThemeRole.Active, false, "→")]
    [InlineData(ThemeRole.Active, true, ">")]
    [InlineData(ThemeRole.Success, false, "✓")]
    [InlineData(ThemeRole.Success, true, "[x]")]
    [InlineData(ThemeRole.Attention, false, "◐")]
    [InlineData(ThemeRole.Attention, true, "!")]
    [InlineData(ThemeRole.Agent, false, "◆")]
    [InlineData(ThemeRole.Agent, true, "+")]
    [InlineData(ThemeRole.Info, false, "▪")]
    [InlineData(ThemeRole.Info, true, "-")]
    [InlineData(ThemeRole.Error, false, "✗")]
    [InlineData(ThemeRole.Error, true, "[!]")]
    [InlineData(ThemeRole.Muted, false, "○")]
    [InlineData(ThemeRole.Muted, true, "o")]
    [InlineData(ThemeRole.Primary, false, " ")]
    [InlineData(ThemeRole.Primary, true, " ")]
    public void ThemeGlyphs_ReturnsExpectedGlyph(ThemeRole role, bool ascii, string expected)
    {
        Assert.Equal(expected, ThemeGlyphs.For(role, ascii));
    }

    #endregion

    #region ModelPolicySetupModel

    [Fact]
    public void ModelPolicySetupModel_CreatesFourChoices_OneRecommended()
    {
        var model = ModelPolicySetupModel.From("local/model", "ScopedCoder", new[] { "Warning 1" });

        Assert.Equal("local/model", model.ModelIdentity);
        Assert.Equal("ScopedCoder", model.RecommendedCategory);
        Assert.Single(model.Warnings);
        Assert.Equal(4, model.Choices.Count);

        var recommended = model.Choices.Single(c => c.Recommended);
        Assert.Equal("ScopedCoder", recommended.Category);
        Assert.All(model.Choices.Where(c => !c.Recommended), c => Assert.False(c.Recommended));
    }

    [Fact]
    public void ModelPolicySetupModel_ChoiceDescriptionsMatchSpec()
    {
        var model = ModelPolicySetupModel.From("test", "ObserveOnly", Array.Empty<string>());

        var observe = model.Choices.Single(c => c.Category == "ObserveOnly");
        Assert.Equal("Solo leer, buscar y proponer planes.", observe.Description);

        var patch = model.Choices.Single(c => c.Category == "PatchOnly");
        Assert.Equal("Aplicar parches localizados a archivos leídos.", patch.Description);

        var scoped = model.Choices.Single(c => c.Category == "ScopedCoder");
        Assert.Equal("Editar en el alcance de la tarea y validar.", scoped.Description);

        var full = model.Choices.Single(c => c.Category == "FullAgent");
        Assert.Equal("Autonomía completa dentro de los límites del runtime.", full.Description);
    }

    #endregion

    #region QuestionnaireTuiForm

    [Fact]
    public void QuestionnaireTuiForm_DelegatesToPlainParser()
    {
        var questionnaire = new QuestionnaireOverlayModel("Q", null,
            new[] { new QuestionnaireQuestionModel("q", "Pregunta", null, QuestionnaireQuestionKind.FreeText, Array.Empty<QuestionnaireChoiceModel>(), null, false) },
            "Enviar", "Cancelar");

        var inputs = new Dictionary<string, QuestionnairePlainFormInput>
        {
            ["q"] = new(text: "respuesta"),
        };

        var plain = QuestionnairePlainFormParser.Parse(questionnaire, inputs);
        var tui = QuestionnaireTuiForm.Submit(questionnaire, inputs);

        Assert.Equal(plain.IsValid, tui.IsValid);
        Assert.Equal(plain.Response?.Cancelled, tui.Response?.Cancelled);
        Assert.Equal(plain.Response?.Answers.Select(a => a.Text), tui.Response?.Answers.Select(a => a.Text));
    }

    #endregion

    // Helper test widget for SidebarHost ordering tests
    private sealed class TestWidget : ISidebarWidget
    {
        private readonly WidgetRelevance _relevance;
        private readonly int _priority;

        public TestWidget(string id, WidgetRelevance relevance, int priority)
        {
            Id = id;
            _relevance = relevance;
            _priority = priority;
        }

        public string Id { get; }
        public string Title => Id;
        public ThemeRole Accent => ThemeRole.Primary;
        public int DefaultPriority => _priority;
        public WidgetRelevance Evaluate(ClientState state) => _relevance;
        public WidgetModel Build(ClientState state, WidgetSize size) => new ListWidgetModel(Id, Array.Empty<WidgetRowModel>());
    }
}
