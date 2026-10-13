using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class ConversationHyperlinkTests
{
    [Fact]
    public void Hyperlink_literal_notices_preserve_text_and_code_while_exposing_web_targets()
    {
        const string source = "Abre http://localhost:8000/help\n\n`https://code.example.com`";
        var rows = ConversationPresentation.Render([new ConversationBlock("s", ConversationRole.System, source, null)], "es");
        Assert.Equal(source, string.Join('\n', rows.Skip(1).Select(row => string.Concat(row.Select(span => span.Text)))));
        var links = rows.SelectMany(row => row).Where(span => span.LinkTarget is not null);
        Assert.Equal("http://localhost:8000/help", Assert.Single(links).LinkTarget);
    }

    [Theory]
    [InlineData("[**Guía** para *niños*](https://example.com/a?q=1&b=2)")]
    [InlineData("<https://example.com/a?q=1&b=2>")]
    [InlineData("https://example.com/a?q=1&b=2")]
    public void Hyperlink_destination_survives_markdown_and_card_wrapping(string markdown)
    {
        var block = new ConversationBlock("a", ConversationRole.Assistant, markdown, null);
        var rows = ConversationPresentation.RenderCards([block], "es", 19);
        var linked = rows.SelectMany(row => row).Where(span => span.LinkTarget is not null).ToArray();
        Assert.NotEmpty(linked);
        Assert.All(linked, span => Assert.Equal("https://example.com/a?q=1&b=2", span.LinkTarget));
        Assert.True(rows.Count(row => row.Any(span => span.LinkTarget is not null)) > 1);
        Assert.Equal(markdown, block.Text);
    }

    [Fact]
    public void Hyperlink_table_header_and_body_keep_their_targets()
    {
        var rows = MarkdownRenderer.Render("| [Guía](https://example.com/guide) |\n| --- |\n| [Ayuda](https://example.com/help) |", 35);
        Assert.Contains(rows.SelectMany(row => row), span => span.LinkTarget == "https://example.com/guide");
        Assert.Contains(rows.SelectMany(row => row), span => span.LinkTarget == "https://example.com/help");
    }

    [Theory]
    [InlineData("[No](javascript:alert(1))")]
    [InlineData("[No](file:///C:/Windows/test.exe)")]
    [InlineData("[No](https://user:password@example.com)")]
    [InlineData("`https://example.com`")]
    [InlineData("```text\nhttps://example.com\n```")]
    public void Hyperlink_unsafe_targets_and_literal_code_are_not_active(string markdown) =>
        Assert.All(MarkdownRenderer.Render(markdown).SelectMany(row => row), span => Assert.Null(span.LinkTarget));

    [Theory]
    [InlineData("http://localhost:8000/help", true)]
    [InlineData("https://example.com/a?q=1&b=2#details", true)]
    [InlineData("https://example.com/\u001b]8;;bad", false)]
    [InlineData("ms-settings:test", false)]
    [InlineData("/relative/file", false)]
    public async Task Hyperlink_host_validates_before_launching_and_preserves_the_exact_destination(string target, bool allowed)
    {
        var browser = new FakeBrowserLauncher();
        Assert.Equal(allowed, WebLinkTarget.TryParse(target, out _));
        Assert.Equal(allowed, await new WebLinkHost(browser).OpenAsync(target, CancellationToken.None));
        if (allowed) Assert.Equal(target, Assert.Single(browser.Launched).OriginalString);
        else Assert.Empty(browser.Launched);
    }

    [Fact]
    public async Task Hyperlink_browser_failure_is_reported_and_cancellation_is_preserved()
    {
        Assert.False(await new WebLinkHost(new FakeBrowserLauncher(false)).OpenAsync("https://example.com", CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new WebLinkHost(new FakeBrowserLauncher()).OpenAsync("https://example.com", cancelled.Token));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("file:///C:/Windows/test.exe")]
    [InlineData("https://user:password@example.com")]
    public async Task Hyperlink_support_does_not_relax_the_OAuth_browser_launcher(string target)
    {
        var result = await new SystemBrowserLauncher().LaunchAsync(new Uri(target), CancellationToken.None);
        Assert.False(result.Launched);
        Assert.Equal(BrowserLaunchFailure.UnsupportedUrl, result.Failure);
    }

    [Fact]
    public void Hyperlink_plain_output_has_OSC8_only_when_enabled_and_never_for_code_or_unsafe_schemes()
    {
        const string markdown = "[Guía](https://example.com/a?q=1&b=2) `https://code.example.com`\n\nhttps://bare.example.com\n\n[No](javascript:alert(1))";
        Assert.Equal(markdown, PlainHyperlinks.Render(markdown, enabled: false));
        var rendered = PlainHyperlinks.Render(markdown, enabled: true);
        Assert.Contains("\u001b]8;;https://example.com/a?q=1&b=2\u001b\\", rendered);
        Assert.Contains("\u001b]8;;https://bare.example.com/\u001b\\", rendered);
        Assert.DoesNotContain("\u001b]8;;https://code.example", rendered);
        Assert.DoesNotContain("\u001b]8;;javascript:", rendered);
        Assert.Equal(markdown, System.Text.RegularExpressions.Regex.Replace(rendered, "\u001b\\]8;;.*?\u001b\\\\", ""));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Hyperlink_errors_have_native_resources_in_every_ui_language(string locale)
    {
        var text = new Localization(locale).ResolveDetailed("links.openFailed");
        Assert.False(text.UsedFallback);
        Assert.Equal(locale, text.ResourceLocale);
        Assert.NotEqual("links.openFailed", text.Text);
    }
}
