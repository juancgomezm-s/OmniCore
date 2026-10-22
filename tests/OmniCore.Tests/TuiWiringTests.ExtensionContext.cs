using System.Text.Json;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es", 80, 25)]
    [InlineData("en", 120, 35)]
    [InlineData("fr", 80, 25)]
    [InlineData("it", 120, 35)]
    public void Extension_context_requires_preview_and_human_click_without_editing_composer(string locale, int width, int height) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        var packages = new ExtensionPackageManager(paths);
        packages.Install(ExtensionPlatformTests.ContextExample(fx.Root, fx.Workspace), CancellationToken.None);
        packages.Change("test.files", "enable", true);
        fx.StartTui(initialColumns: width, initialRows: height, localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(() => fx.App.Composer!.Text = "My draft must survive");
        fx.Invoke(() => fx.App.ShowExtensionContext());
        var approve = Descendants(fx.App.Overlay!).OfType<Button>().Single(button => button.Id == "extension-context-approve");
        Assert.False(fx.Read(() => approve.Enabled));
        Assert.Equal("null", ExtensionPlatformTests.PreviewCalls(fx.Server));
        ExtensionContextPreview[] Read() => JsonSerializer.Deserialize(fx.Server.Query("extensions.context", CancellationToken.None)!.Json,
            ExtensionJson.Default.ExtensionContextPreviewArray)!;
        ClickId(fx, "extension-context-preview");
        fx.Wait(() => approve.Enabled, "Preview permits a separate human decision");
        Assert.False(Assert.Single(Read()).Approved);
#pragma warning disable CS0618
        Assert.Contains("External test data", fx.Read(() => Descendants(fx.App.Overlay!).OfType<TextView>().Single().Text.ToString()));
#pragma warning restore CS0618
        ExportPreferenceFrame(fx, width, height, "extension-context-" + locale, new Localization(locale).Resolve("extension.context.preview"));
        ClickId(fx, "extension-context-approve");
        Assert.True(Assert.Single(Read()).Approved);
        Assert.False(fx.Read(() => approve.Enabled));
        ClickId(fx, "extension-context-revoke");
        Assert.Empty(Read());
        Assert.Equal("1", ExtensionPlatformTests.PreviewCalls(fx.Server));
        _ = fx.Server.Query("extensions.view:" + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(new("test.files", "files"))), CancellationToken.None);
        fx.Wait(() => fx.App.ProjectionState.Conversation.Blocks.Any(block => block.Id.StartsWith("extension-notification:", StringComparison.Ordinal)), "Core notice reaches the UI only");
        Assert.Single(fx.Read(() => fx.App.ProjectionState.Conversation.Blocks.Where(block => block.Id.StartsWith("extension-notification:", StringComparison.Ordinal)).ToArray()));
        Assert.Equal("My draft must survive", fx.Read(() => fx.App.Composer!.Text.ToString()));
        Assert.DoesNotContain(fx.Server.SubscribeSince(1), envelope => envelope.PayloadJson.Contains("UI ONLY", StringComparison.Ordinal));
    });
}
