namespace OmniCore.Client;

/// <summary>UI-independent decisions and models used by the Terminal.Gui renderer (ADR-0030–0033).</summary>
public enum TuiLayoutMode { Stacked, Tabbed, Overlay }
public enum ThemeRole { Active, Success, Attention, Agent, Info, Error, Muted, Primary }
public enum WidgetRelevance { None, Low, Normal, Attention }
public enum WidgetSize { Compact, Normal, Expanded }

public static class ThemeGlyphs
{
    public static string For(ThemeRole role, bool ascii = false) => role switch
    {
        ThemeRole.Active => ascii ? ">" : "→",
        ThemeRole.Success => ascii ? "[x]" : "✓",
        ThemeRole.Attention => ascii ? "!" : "◐",
        ThemeRole.Agent => ascii ? "+" : "◆",
        ThemeRole.Info => ascii ? "-" : "▪",
        ThemeRole.Error => ascii ? "[!]" : "✗",
        ThemeRole.Muted => ascii ? "o" : "○",
        _ => " ",
    };
}

public sealed record TuiLayoutModel(TuiLayoutMode Mode, bool SidebarVisible, int SidebarWidth)
{
    public static TuiLayoutModel ForWidth(int width, bool sidebarRequested = true)
    {
        if (width >= 120) return new(TuiLayoutMode.Stacked, sidebarRequested, sidebarRequested ? Math.Clamp(width / 3, 32, 40) : 0);
        if (width >= 90) return new(TuiLayoutMode.Tabbed, sidebarRequested, sidebarRequested ? 26 : 0);
        return new(TuiLayoutMode.Overlay, sidebarRequested,
            sidebarRequested ? Math.Max(20, Math.Min(44, width - 4)) : 0);
    }
}

public sealed record StatusLinePresentation(string Left, string Right)
{
    /// <summary>Formats only reported data; unknown/not-supported quota is an em dash, never an estimate.</summary>
    public static StatusLinePresentation From(StatusLineModel model)
    {
        var left = model.Mode + (StringComparer.OrdinalIgnoreCase.Equals(model.ProductEffort, "ultracode")
            ? " · UltraCode" : "");
        if (!string.IsNullOrWhiteSpace(model.AppliedReasoning))
            left += " · reasoning " + model.AppliedReasoning;
        var right = string.IsNullOrWhiteSpace(model.Quota) ? "—" : model.Quota;
        if (model.PendingInteractions is > 0) right += " · ! " + model.PendingInteractions + " pending";
        return new(left, right);
    }
}

public sealed record AutocompleteSuggestion(string Value, string Description, ThemeRole Role = ThemeRole.Primary);

/// <summary>Pure autocomplete provider: / uses the Host's command catalog; @ uses workspace file suggestions.</summary>
public static class ComposerAutocomplete
{
    public static IReadOnlyList<AutocompleteSuggestion> Complete(string draft,
        IEnumerable<string> commandNames, IEnumerable<string> workspacePaths)
    {
        if (string.IsNullOrEmpty(draft)) return Array.Empty<AutocompleteSuggestion>();
        var marker = draft[0];
        var prefix = draft.Length > 1 ? draft[1..] : string.Empty;
        if (marker == '/')
            return commandNames.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new AutocompleteSuggestion("/" + name, "command", ThemeRole.Active)).ToArray();
        if (marker == '@')
            return workspacePaths.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new AutocompleteSuggestion("@" + path, "workspace", ThemeRole.Info)).ToArray();
        return Array.Empty<AutocompleteSuggestion>();
    }
}

/// <summary>Declarative rows rendered generically by Cli; no framework types cross into Client.</summary>
public abstract record WidgetModel;
public sealed record WidgetRowModel(string Text, ThemeRole Role = ThemeRole.Primary);
public sealed record ListWidgetModel(string Title, IReadOnlyList<WidgetRowModel> Rows) : WidgetModel;
public sealed record SessionWidgetData(string Title, string Mode, string? Elapsed = null);
public sealed record PlanWidgetData(string Summary, IReadOnlyList<WidgetRowModel> Items);
public sealed record ChangedFileWidgetData(IReadOnlyList<WidgetRowModel> Files);

public interface ISidebarWidget
{
    string Id { get; }
    string Title { get; }
    ThemeRole Accent { get; }
    int DefaultPriority { get; }
    WidgetRelevance Evaluate(ClientState state);
    WidgetModel Build(ClientState state, WidgetSize size);
}

/// <summary>Fixed session slot followed by declarative widgets ordered by attention, then priority.</summary>
public sealed class SidebarHost
{
    private readonly IReadOnlyList<ISidebarWidget> _widgets;
    public SidebarHost(IEnumerable<ISidebarWidget> widgets) => _widgets = widgets.ToArray();
    public IReadOnlyList<(ISidebarWidget Widget, WidgetModel Model)> Build(ClientState state, WidgetSize size) =>
        _widgets.Select((widget, index) => (widget, index, relevance: widget.Evaluate(state)))
            .Where(item => item.relevance != WidgetRelevance.None)
            .OrderByDescending(item => item.widget.Id == "core.session")
            .ThenByDescending(item => item.relevance == WidgetRelevance.Attention)
            .ThenByDescending(item => item.widget.DefaultPriority)
            .ThenBy(item => item.index)
            .Select(item => (item.widget, item.widget.Build(state, size))).ToArray();
}

public sealed class SessionSidebarWidget : ISidebarWidget
{
    private readonly SessionWidgetData _data;
    public SessionSidebarWidget(SessionWidgetData data) => _data = data;
    public string Id => "core.session";
    public string Title => "SESSION";
    public ThemeRole Accent => ThemeRole.Primary;
    public int DefaultPriority => 100;
    public WidgetRelevance Evaluate(ClientState state) => WidgetRelevance.Normal;
    public WidgetModel Build(ClientState state, WidgetSize size) => new ListWidgetModel(Title,
        new[] { new WidgetRowModel(_data.Title), new WidgetRowModel(_data.Mode + (_data.Elapsed is null ? "" : " · " + _data.Elapsed), ThemeRole.Muted) });
}

public sealed class PlanSidebarWidget : ISidebarWidget
{
    private readonly PlanWidgetData _data;
    public PlanSidebarWidget(PlanWidgetData data) => _data = data;
    public string Id => "core.plan";
    public string Title => "PLAN";
    public ThemeRole Accent => ThemeRole.Active;
    public int DefaultPriority => 80;
    public WidgetRelevance Evaluate(ClientState state) => _data.Items.Count == 0 ? WidgetRelevance.None
        : _data.Items.Any(row => row.Role is ThemeRole.Attention or ThemeRole.Error) ? WidgetRelevance.Attention : WidgetRelevance.Normal;
    public WidgetModel Build(ClientState state, WidgetSize size) => new ListWidgetModel(_data.Summary, _data.Items);
}

public sealed class ChangedFilesSidebarWidget : ISidebarWidget
{
    private readonly ChangedFileWidgetData _data;
    public ChangedFilesSidebarWidget(ChangedFileWidgetData data) => _data = data;
    public string Id => "core.files";
    public string Title => "FILES";
    public ThemeRole Accent => ThemeRole.Info;
    public int DefaultPriority => 50;
    public WidgetRelevance Evaluate(ClientState state) => _data.Files.Count == 0 ? WidgetRelevance.None : WidgetRelevance.Normal;
    public WidgetModel Build(ClientState state, WidgetSize size) => new ListWidgetModel(Title, _data.Files);
}

/// <summary>Presentation representation of an exact model policy draft; recommendation is visible but never persisted automatically.</summary>
public sealed record ModelPolicyChoice(string Category, string Description, bool Recommended);
public sealed record ModelPolicySetupModel(string ModelIdentity, IReadOnlyList<string> Warnings,
    IReadOnlyList<ModelPolicyChoice> Choices, string RecommendedCategory)
{
    public static ModelPolicySetupModel From(string identity, string recommended, IReadOnlyList<string> warnings) =>
        new(identity, warnings, new[]
        {
            new ModelPolicyChoice("ObserveOnly", "Solo leer, buscar y proponer planes.", recommended == "ObserveOnly"),
            new ModelPolicyChoice("PatchOnly", "Aplicar parches localizados a archivos leídos.", recommended == "PatchOnly"),
            new ModelPolicyChoice("ScopedCoder", "Editar en el alcance de la tarea y validar.", recommended == "ScopedCoder"),
            new ModelPolicyChoice("FullAgent", "Autonomía completa dentro de los límites del runtime.", recommended == "FullAgent"),
        }, recommended);
}

/// <summary>Converts interactive TUI control values using the identical parser/validation used by plain form.</summary>
public static class QuestionnaireTuiForm
{
    public static QuestionnaireParseResult Submit(QuestionnaireOverlayModel model,
        IReadOnlyDictionary<string, QuestionnairePlainFormInput> answers, bool cancelled = false) =>
        QuestionnairePlainFormParser.Parse(model, answers, cancelled);
}
