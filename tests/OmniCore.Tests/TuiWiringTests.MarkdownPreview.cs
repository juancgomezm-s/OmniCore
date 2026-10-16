using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Terminal.Gui.Drivers;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(80, 40)]
    [InlineData(120, 40)]
    public void Markdown_only_examples_preview_in_chat_and_source_toggle_keeps_original_journal(int width, int height) => RunTuiTest(fx =>
    {
        const string source = "## Ejemplos Markdown\n```markdown\n## Encabezado 2\n**Texto en negrita** y _cursiva_\n~~Texto tachado~~\n```\n\n"
            + "```md\n| Dato | Estado |\n| --- | --- |\n| Uno | Listo |\n```\n\n"
            + "````markdown\n```csharp\nint count = 42;\n```\n````\nFIN_VISTA";
        var artifact = fx.Artifacts.PutText(source, "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
        new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)
            .Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, fx.Decoded<LaneCreated>().Last().LaneId, TurnId.New(), artifact));
        var sequence = fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!);
        fx.StartTui(initialColumns: width, initialRows: height);
        string Drawn(string marker)
        {
            var cells = CaptureConversationFrame(fx, width, height, marker);
            if (marker == "Texto en negrita y cursiva")
            {
                foreach (var (label, style) in new[]
                {
                    ("Texto en negrita", Terminal.Gui.Drawing.TextStyle.Bold),
                    ("cursiva", Terminal.Gui.Drawing.TextStyle.Italic),
                    ("Texto tachado", Terminal.Gui.Drawing.TextStyle.Strikethrough),
                })
                {
                    var row = Enumerable.Range(0, height).Single(y => string.Concat(
                        Enumerable.Range(0, width).Select(x => cells[y, x].Grapheme)).Contains(label, StringComparison.Ordinal));
                    var start = string.Concat(Enumerable.Range(0, width).Select(x => cells[row, x].Grapheme)).IndexOf(label, StringComparison.Ordinal);
                    Assert.All(Enumerable.Range(start, label.Length), x => Assert.True(cells[row, x].Attribute!.Value.Style.HasFlag(style), label));
                }
            }
            return string.Join('\n', Enumerable.Range(0, height).Select(y => string.Concat(
                Enumerable.Range(0, width).Select(x => cells[y, x].Grapheme))));
        }
        var preview = Drawn("Texto en negrita y cursiva");
        Assert.Contains("Encabezado 2", preview);
        Assert.Contains("Texto tachado", preview);
        Assert.Contains("Listo", preview);
        Assert.Contains("┼", preview);
        Assert.Contains("int count = 42;", preview);
        Assert.DoesNotContain("**Texto", preview);
        Assert.DoesNotContain("</> markdown", preview);
        Assert.DoesNotContain("</> md", preview);
        ExportPreferenceFrame(fx, width, height, "markdown-preview", "FIN_VISTA");

        Type(fx, "/fuente");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Conversation!.Text.Contains("**Texto en negrita**"), "source view exposes the original Markdown");
        fx.Invoke(() => fx.App.Conversation!.MoveHome());
        Assert.Contains("**Texto en negrita**", Drawn("**Texto en negrita**"));
        ExportPreferenceFrame(fx, width, height, "markdown-source", "**Texto en negrita**");
        Type(fx, "/fuente");
        KeyWithEffect(fx, KeyCode.Enter, () => !fx.App.Conversation!.Text.Contains("**Texto en negrita**"), "source toggle returns to rendered preview");
        Assert.Equal(sequence, fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!));
        Assert.Equal(source, fx.Artifacts.GetText(artifact.Hash));
    }, noColor: false);
}
