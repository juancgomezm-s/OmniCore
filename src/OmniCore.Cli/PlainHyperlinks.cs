using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using OmniCore.Client;

namespace OmniCore.Cli;

/// <summary>OSC 8 links for interactive terminal output; redirected output stays literal.</summary>
internal static class PlainHyperlinks
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation()
        .UseAutoLinks(new Markdig.Extensions.AutoLinks.AutoLinkOptions { AllowDomainWithoutPeriod = true }).Build();
    internal static bool Interactive => !Console.IsOutputRedirected
        && !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);

    internal static string Render(string text, bool enabled)
    {
        if (!enabled || text.Length > MarkdownRenderer.MaxLength) return text;
        var output = new StringBuilder();
        var position = 0;
        foreach (var link in Targets(text))
        {
            if (link.Span.Start < position) continue;
            WebLinkTarget.TryParse(link.Target, out var uri);
            output.Append(text.AsSpan(position, link.Span.Start - position));
            output.Append("\u001b]8;;").Append(uri!.AbsoluteUri).Append("\u001b\\");
            output.Append(text.AsSpan(link.Span.Start, link.Span.Length));
            output.Append("\u001b]8;;\u001b\\");
            position = link.Span.End + 1;
        }
        return output.Append(text.AsSpan(position)).ToString();
    }

    internal static IReadOnlyList<ConversationSpan> AnnotateLiteral(string text, ConversationStyle style)
    {
        var spans = new List<ConversationSpan>();
        var position = 0;
        foreach (var link in Targets(text))
        {
            if (link.Span.Start < position) continue;
            spans.Add(new(text[position..link.Span.Start], style));
            spans.Add(new(text.Substring(link.Span.Start, link.Span.Length), style,
                Terminal.Gui.Drawing.TextStyle.Underline, link.Target));
            position = link.Span.End + 1;
        }
        spans.Add(new(text[position..], style));
        return spans;
    }

    private static IEnumerable<(SourceSpan Span, string? Target)> Targets(string text)
    {
        if (text.Length > MarkdownRenderer.MaxLength) return [];
        return Markdown.Parse(text, Pipeline).Descendants().Select(node => node switch
        {
            LinkInline { IsImage: false } link => (Span: link.Span, Target: link.Url),
            AutolinkInline auto => (Span: auto.Span, Target: (string?)auto.Url),
            _ => (Span: default(Markdig.Syntax.SourceSpan), Target: (string?)null),
        }).Where(link => WebLinkTarget.TryParse(link.Target, out _) && link.Span.Start >= 0
            && link.Span.End < text.Length).OrderBy(link => link.Span.Start);
    }
}
