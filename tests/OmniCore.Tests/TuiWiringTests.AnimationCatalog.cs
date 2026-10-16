using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void Animation_picker_previews_without_saving_and_searches_the_whole_catalog(int width) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: width, initialRows: 40);
        fx.Invoke(() => fx.App.Composer!.Text = "borrador conservado");
        var count = fx.Server.SubscribeSince(0).Count;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences");
        ChoosePreferenceCategory(fx, "general"); ClickId(fx, "preferences-animations-open");
        ClickId(fx, "animation-thinking");
        fx.Wait(() => fx.App.Overlay?.Id == "animation-picker", "animation list");
        var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
        Assert.Equal(31, list.Source!.Count);
        fx.Invoke(() => { list.SelectedItem = AnimationCatalog.Names.ToList().IndexOf("text-scramble"); list.SetFocus(); });
        Assert.Equal("orbit-dots", new AnimationSettingsStore(Path.Combine(fx.Root, "tui-animations.json")).Load().Reasoning);
        fx.Invoke(() => { for (var tick = 0; tick < 8; tick++) fx.App.AnimateActivity(); });
        ExportPreferenceFrame(fx, width, 40, "animation-symbol-pulse", "Elegir animación");
        ClickId(fx, "animation-choice-cancel");
        Assert.Equal("orbit-dots", new AnimationSettingsStore(Path.Combine(fx.Root, "tui-animations.json")).Load().Reasoning);
        ClickId(fx, "animation-thinking");
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<TextField>().Single().Text = "fuego");
        fx.Wait(() => Descendants(fx.App.Overlay!).OfType<ListView>().Single().Source!.Count == 2, "localized search");
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<ListView>().Single().SelectedItem = 1);
        ExportPreferenceFrame(fx, width, 40, "animation-fire", "Elegir animación");
        ClickId(fx, "animation-choose");
        Assert.Equal("fire3", new AnimationSettingsStore(Path.Combine(fx.Root, "tui-animations.json")).Load().Reasoning);
        Assert.Equal("borrador conservado", fx.App.Composer!.Text);
        Assert.Equal(count, fx.Server.SubscribeSince(0).Count);
    }, noColor: false);

    [Fact]
    public void Live_text_pulse_and_multiline_effect_use_the_observed_label_and_preserve_the_composer() => RunTuiTest(fx =>
    {
        var client = new AnimationObservationClient(fx.Server);
        fx.StartTui(client: client, initialColumns: 100, initialRows: 40);
        fx.Wait(() => fx.App.Activity!.Visible, "observed activity");
        var composerFrame = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-composer");
        var originalY = composerFrame.Frame.Y;
        Type(fx, "/animation thinking:fire3");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Composer!.Text == "", "multiline effect selected");
        fx.Wait(() => fx.App.Activity!.Frame.Height == 3, "three native rows");
        Assert.Equal(originalY - 2, composerFrame.Frame.Y);
        Type(fx, "/animation thinking:text-scramble");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Composer!.Text == "", "text pulse selected");
        fx.Wait(() => composerFrame.Frame.Y == originalY, "one-line geometry restored");
        Type(fx, "borrador sin enviar");
        var label = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-activity-label");
        Assert.Equal("Razonando", label.Text);
        var sawSymbol = false;
        fx.Invoke(() =>
        {
            for (var tick = 0; tick < 30; tick++)
            {
                fx.App.AnimateActivity();
                sawSymbol |= label.SubViews.Any(cell => cell.Visible && new[] { "†", "‡", "§", "※", "✧" }.Contains(cell.Text));
            }
        });
        Assert.True(sawSymbol);
        client.Phase = ChatActivityPhase.WaitingForApproval;
        fx.Wait(() => label.Text.Contains("Requiere tu respuesta"), "actual approval label");
        var frozen = label.SubViews.Where(cell => cell.Visible).Select(cell => cell.Text).ToArray();
        fx.Invoke(() => { for (var tick = 0; tick < 8; tick++) fx.App.AnimateActivity(); });
        Assert.Equal(frozen, label.SubViews.Where(cell => cell.Visible).Select(cell => cell.Text));
        Assert.Equal("borrador sin enviar", fx.App.Composer!.Text);
        fx.Invoke(() =>
        {
            var textView = Assert.IsType<ActivityView>(label);
            textView.Render("text-pulse", 2, true, false, label: "界A");
            label.Layout();
            Assert.Equal(2, label.SubViews.First().Frame.Width);
            Assert.Equal(2, label.SubViews.Skip(1).First().Frame.X);
            textView.Render("text-pulse", 5, true, false, label: "界A");
            label.Layout();
            Assert.Equal(2, label.SubViews.First().Frame.Width);
        });
    }, noColor: false);
}
