using OmniCore.Domain;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("/inicializar")]
    [InlineData("/initialize")]
    public void Initialization_slash_reaches_human_host_and_displays_sources_without_model_input(string command) => RunTuiTest(fx =>
    {
        var host = new InitializationTurnHostFixture(fx.Workspace);
        var before = fx.Decoded<UserInputReceived>().Count();
        fx.StartTui(turnHost: host);
        Type(fx, command);
        KeyWithEffect(fx, KeyCode.Enter, () => host.InitializationCalls == 1 && fx.App.Overlay is not null,
            "inicializar llega al Host humano y presenta resultado");
        fx.Wait(() => fx.Read(() => OverlayText(fx.App.Overlay!).Contains("Fuentes leídas", StringComparison.Ordinal)),
            "fuentes de inicialización visibles");
        var displayed = fx.Read(() => OverlayText(fx.App.Overlay!));
        Assert.Contains("creado", displayed, StringComparison.Ordinal);
        Assert.Contains("agents.md", displayed, StringComparison.Ordinal);
        Assert.Contains("MEMORY.md", displayed, StringComparison.Ordinal);
        Assert.Contains("SHA-256", displayed, StringComparison.Ordinal);
        Assert.Equal(0, host.ModelCalls);
        Assert.Equal(before, fx.Decoded<UserInputReceived>().Count());
        Assert.Equal("", fx.Read(() => fx.App.Composer!.Text));
    });

    [Fact]
    public void Initialization_autocomplete_exposes_the_localized_command_before_any_execution() => RunTuiTest(fx =>
    {
        var host = new InitializationTurnHostFixture(fx.Workspace);
        fx.StartTui(turnHost: host);
        Type(fx, "/inici");
        fx.Wait(() => fx.App.MainWindow!.SubViews.Any(view => view.Id == "omni-command-helper"),
            "autocomplete de inicializar visible");
        var rows = fx.Read(() => fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-command-helper")
            .SubViews.OfType<ListView>().Single().Source!.ToList().Cast<object>()
            .Select(item => item.ToString() ?? "").ToArray());
        Assert.Contains(rows, row => row.Contains("inicializar", StringComparison.Ordinal));
        Assert.Equal(0, host.InitializationCalls);
        Assert.Equal(0, host.ModelCalls);
    });

    [Fact]
    public void Initialization_invalid_arguments_show_usage_preserve_draft_and_never_call_model_or_initializer() => RunTuiTest(fx =>
    {
        var host = new InitializationTurnHostFixture(fx.Workspace);
        var before = fx.Decoded<UserInputReceived>().Count();
        fx.StartTui(turnHost: host);
        Type(fx, "/inicializar otro-proyecto");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "argumentos inválidos muestran ayuda");
        Assert.Contains("Uso: inicializar", fx.Read(() => OverlayText(fx.App.Overlay!)), StringComparison.Ordinal);
        Assert.Equal("/inicializar otro-proyecto", fx.Read(() => fx.App.Composer!.Text));
        Assert.Equal(0, host.InitializationCalls);
        Assert.Equal(0, host.ModelCalls);
        Assert.Equal(before, fx.Decoded<UserInputReceived>().Count());
    });

    [Fact]
    public void Initialization_result_keeps_all_sources_and_generated_block_accessible_by_scroll() => RunTuiTest(fx =>
    {
        var host = new InitializationTurnHostFixture(fx.Workspace) { SourceCount = 64 };
        fx.StartTui(turnHost: host, initialColumns: 80, initialRows: 25);
        Type(fx, "/inicializar");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "project-initialization-result", "resultado largo visible");
#pragma warning disable CS0618
        var detail = fx.Read(() => Descendants(fx.App.Overlay!).OfType<TextView>().Single(view => view.Id == "project-initialization-content"));
        Assert.True(detail.ReadOnly);
        Assert.True(detail.WordWrap);
        Assert.Contains("memory/source-63.md", fx.Read(() => detail.Text), StringComparison.Ordinal);
        Assert.Contains("LAST_GENERATED_LINE", fx.Read(() => detail.Text), StringComparison.Ordinal);
        fx.Invoke(() => detail.SetFocus());
        KeyWithEffect(fx, KeyCode.PageDown, () => detail.Viewport.Y > 0, "fuentes se desplazan sin truncarse");
        KeyWithEffect(fx, KeyCode.End | KeyCode.CtrlMask, () => detail.CurrentRow == detail.GetAllLines().Count - 1,
            "última línea generada accesible por teclado");
#pragma warning restore CS0618
        Assert.Equal(0, host.ModelCalls);
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Esc cierra revisión de inicialización");
        Assert.True(fx.Read(() => fx.App.Composer!.HasFocus));
    });

    private sealed class InitializationTurnHostFixture(string workspace) : ITuiTurnHost
    {
        public int InitializationCalls;
        public int ModelCalls;
        public int SourceCount = 1;
        public ProjectInitializationPlan InitializeProject(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref InitializationCalls);
            return new(workspace, Path.Combine(workspace, "agents.md"), null,
                "<!-- omnicore:project:start -->\nGenerated local project data.\nLAST_GENERATED_LINE\n<!-- omnicore:project:end -->",
                SourceCount == 1 ? [new("MEMORY.md", "memory", new string('a', 64))]
                    : Enumerable.Range(0, SourceCount).Select(index => new ProjectInitializationSource("memory/source-" + index + ".md", "memory", new string('a', 64))).ToArray(), []);
        }
        public Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ModelCalls);
            return Task.FromResult(0);
        }
    }
}
