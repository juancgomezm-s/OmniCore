namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Memory;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 40)]
    public void Scoped_memory_command_opens_readable_consent_and_Escape_rejects_without_saving(int width, int height) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: width, initialRows: height);
        fx.Invoke(() => fx.App.Composer!.Text = "/memory global propose A shared TUI convention.");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.SubViews.Any(view => view.Id == "memory-promotion-content") == true,
            "the memory command opens consent");
        fx.Read(() =>
        {
#pragma warning disable CS0618 // Inspect the exact pinned, read-only consent control.
            var body = Assert.Single(fx.App.Overlay!.SubViews.OfType<TextView>());
#pragma warning restore CS0618
            Assert.Equal("memory-promotion-content", body.Id);
            Assert.True(body.ReadOnly);
            Assert.True(body.WordWrap);
            Assert.Contains("A shared TUI convention.", body.Text.ToString());
            Assert.Contains("Global", body.Text.ToString());
            Assert.DoesNotContain("memory.scoped.promotion", body.Text.ToString());
        });
        using (var before = JsonDocument.Parse(fx.Server.Query("memory.global", CancellationToken.None)!.Json))
            Assert.Empty(before.RootElement.GetProperty("records").EnumerateArray());
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Escape rejects and returns to chat");
        using var after = JsonDocument.Parse(fx.Server.Query("memory.global", CancellationToken.None)!.Json);
        Assert.Empty(after.RootElement.GetProperty("records").EnumerateArray());
        Assert.Equal("rejected", after.RootElement.GetProperty("candidates")[0].GetProperty("decision").GetString());
    });
}
