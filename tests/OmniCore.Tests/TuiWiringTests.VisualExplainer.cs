using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 40)]
    public void Bundled_visual_explainer_can_be_activated_from_the_existing_skills_menu(int width, int height) => RunTuiTest(fx =>
    {
        string State()
        {
            using var query = JsonDocument.Parse(fx.Server.Query("skills", CancellationToken.None)!.Json);
            return query.RootElement.GetProperty("skills").EnumerateArray()
                .Single(row => row.GetProperty("id").GetString() == "visual-explainer").GetProperty("state").GetString()!;
        }
        Assert.Equal("Discovered", State());
        fx.StartTui(initialColumns: width, initialRows: height);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences opened");
        ChoosePreferenceCategory(fx, "extensions_skills");
        ClickId(fx, "preferences-skills-open");
        fx.Wait(() => fx.App.Overlay?.Id == "session-preferences", "skills opened");
        fx.Invoke(() =>
        {
            var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
            var index = list.Source!.ToList().Cast<object>().ToList()
                .FindIndex(row => row.ToString()!.Contains("visual-explainer", StringComparison.Ordinal));
            Assert.True(index >= 0); list.SelectedItem = index;
        });
        ExportPreferenceFrame(fx, width, height, "visual-skill-menu", "visual-explainer");
        ClickId(fx, "session-preferences-action");
        Assert.Equal("session-preferences-confirm", fx.App.Overlay!.Id);
        ClickId(fx, "session-preferences-confirm-yes");
        fx.Wait(() => State() == "Activated", "bundled skill activated in the real session");
        ClickId(fx, "session-preferences-action");
        ClickId(fx, "session-preferences-confirm-yes");
        fx.Wait(() => State() == "Discovered", "release removes the activation");
    }, noColor: false);

    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 40)]
    public void Visual_text_diagram_and_status_table_render_in_the_production_conversation(int width, int height) => RunTuiTest(fx =>
    {
        // An explicitly illustrative response fixture, not a claim of authenticated model generation.
        const string markdown = "## Ejemplo visual\n```text\n+------------------+\n| Petición usuario |\n+------------------+\n          |\n          v\n+------------------+\n| Modelo elegido   |\n+------------------+\n```\n\n"
            + "| Dato | Estado |\n| --- | --- |\n| Consumo | Desconocido |\n\nFin del ejemplo";
        var artifact = fx.Artifacts.PutText(markdown, "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
        new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)
            .Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, fx.Decoded<LaneCreated>().Last().LaneId, TurnId.New(), artifact));
        fx.StartTui(initialColumns: width, initialRows: height);
        string Drawn(string marker)
        {
            var frame = CaptureConversationFrame(fx, width, height, marker);
            return string.Join('\n', Enumerable.Range(0, height).Select(y => string.Concat(
                Enumerable.Range(0, width).Select(x => frame[y, x].Grapheme))));
        }
        var text = Drawn("Petición usuario");
        ExportPreferenceFrame(fx, width, height, "visual-diagram-start", "Petición usuario");
        fx.Invoke(() => fx.App.Conversation!.MoveEnd());
        text += Drawn("Fin del ejemplo");
        foreach (var label in new[] { "Petición usuario", "Modelo elegido", "+------------------+", "Desconocido" })
            Assert.Contains(label, text);
        Assert.DoesNotContain("```", text);
        ExportPreferenceFrame(fx, width, height, "visual-diagram-end", "Fin del ejemplo");
    }, noColor: false);
}
