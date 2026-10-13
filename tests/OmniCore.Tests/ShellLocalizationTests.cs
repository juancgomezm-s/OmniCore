using System.Text.RegularExpressions;
using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ShellLocalizationTests
{
    [Theory]
    [InlineData("es", "Aceptar", "Sí", "No")]
    [InlineData("en", "OK", "On", "Off")]
    [InlineData("fr", "OK", "Oui", "Non")]
    [InlineData("it", "OK", "Sì", "No")]
    public void Shared_shell_actions_and_toggle_states_use_the_selected_locale(string locale, string ok, string on, string off)
    {
        var text = new Localization(locale);
        Assert.Equal(ok, text.Resolve("shell.ok"));
        Assert.Equal(on, text.Resolve("shell.state.on"));
        Assert.Equal(off, text.Resolve("shell.state.off"));
    }

    [Theory]
    [InlineData("es", "Modelo:")]
    [InlineData("en", "Model:")]
    [InlineData("fr", "Modèle :")]
    [InlineData("it", "Modello:")]
    public void Header_localizes_its_model_label_and_reserves_space_for_the_model(string locale, string label)
    {
        var heading = OmniCore.Cli.TuiApp.SessionHeading("project", new string('x', 100), "fixture-model", 80, locale);
        Assert.Contains(label, heading);
        Assert.Contains("fixture-model", heading);
        Assert.DoesNotContain(new string('x', 33), heading);
        Assert.True(Terminal.Gui.Text.StringExtensions.GetColumns(heading, false) <= 80);
        Assert.Contains("! 1 ", StatusLinePresentation.From(new(null, 1, "act"), locale).Right);
    }

    [Fact]
    public void Every_shell_resource_has_four_translations_and_matching_placeholders()
    {
        foreach (var key in ShellUiResources.Keys)
        {
            var expected = Arguments(new Localization("es").Resolve(key));
            foreach (var locale in new[] { "es", "en", "fr", "it" })
            {
                var resolution = new Localization(locale).ResolveDetailed(key);
                Assert.False(resolution.UsedFallback, key + ":" + locale);
                Assert.Equal(locale, resolution.ResourceLocale);
                Assert.False(string.IsNullOrWhiteSpace(resolution.Text), key);
                Assert.Equal(expected, Arguments(resolution.Text));
            }
        }
    }

    [Theory]
    [InlineData("es", "Razonando", "agentes")]
    [InlineData("en", "Reasoning", "agents")]
    [InlineData("fr", "Raisonnement", "agents")]
    [InlineData("it", "Ragionamento", "agenti")]
    public void Activity_resolves_the_selected_locale_without_rewriting_observations(string locale, string phase, string children)
    {
        var activity = new ActivityPresentation(ChatActivityPhase.Reasoning, 2, false);
        Assert.Equal(phase + " · 2 " + children, activity.Label(locale));
        Assert.Equal(ChatActivityPhase.Reasoning, activity.Phase);
    }

    [Theory]
    [InlineData("es", "enviar")]
    [InlineData("en", "send")]
    [InlineData("fr", "envoyer")]
    [InlineData("it", "invia")]
    public void Composer_help_is_localized_on_one_line_at_terminal_widths(string locale, string send)
    {
        foreach (var width in new[] { 30, 55, 80, 120 })
        {
            var help = OmniCore.Cli.TuiApp.CompactHelp("act", locale, width);
            Assert.Contains(send, help);
            Assert.DoesNotContain("\n", help);
            Assert.True(Terminal.Gui.Text.StringExtensions.GetColumns(help, false) <= width, help);
        }
    }

    [Theory]
    [InlineData("fr", "Envoyer", "Annuler")]
    [InlineData("it", "Invia", "Annulla")]
    public void Questionnaire_buttons_are_localized_but_external_question_text_is_preserved(string locale, string send, string cancel)
    {
        var model = QuestionnairePresentationFactory.FromJson("""
            {"title":"Título del usuario","questions":[]}
            """, locale)!;
        Assert.Equal("Título del usuario", model.Title);
        Assert.Equal(send, model.SubmitLabel);
        Assert.Equal(cancel, model.CancelLabel);
    }

    private static string[] Arguments(string text) => Regex.Matches(text, @"\{[A-Za-z][A-Za-z0-9_]*\}")
        .Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("es", "Sólo finales de línea")]
    [InlineData("en", "Line endings only")]
    [InlineData("fr", "Fins de ligne uniquement")]
    [InlineData("it", "Solo terminazioni di riga")]
    public void Diff_notices_use_one_language_and_preserve_file_content(string locale, string expected)
    {
        var diff = new FileDiffSnapshot("s", "e", "file.txt", true, null, "original\r\n", "original\n");
        Assert.Equal(expected, Assert.Single(FileDiffPresentation.Build(diff, locale: locale).Rows).Text);
        Assert.Contains(FileDiffPresentation.Build(diff with { After = "cambió" }, locale: locale).Rows,
            row => row.Text == "+ cambió");
    }
}
