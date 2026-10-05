using OmniCore.Client;

namespace OmniCore.Cli;

internal enum ConversationStyle
{
    Text, Heading, Muted, InlineCode, Code, CodeHeader,
    SyntaxComment, SyntaxKeyword, SyntaxFunction, SyntaxVariable,
    SyntaxString, SyntaxNumber, SyntaxType, SyntaxPunctuation,
}
internal sealed record ConversationSpan(string Text, ConversationStyle Style);

/// <summary>Small, deterministic Markdown subset for terminal conversations; never modifies journal content.</summary>
internal static class ConversationPresentation
{
    internal static IReadOnlyList<IReadOnlyList<ConversationSpan>> Render(IReadOnlyList<ConversationBlock> blocks, string locale)
    {
        var rows = new List<IReadOnlyList<ConversationSpan>>();
        foreach (var block in blocks)
        {
            if (rows.Count > 0) rows.Add(Array.Empty<ConversationSpan>());
            var label = block.Role switch
            {
                ConversationRole.User => locale == "en" ? "YOU" : "TÚ",
                ConversationRole.Assistant => "OMNICORE",
                ConversationRole.Tool => "▪ " + (block.ToolName ?? "tool"),
                ConversationRole.Interaction => "!",
                _ => "○",
            };
            rows.Add(new[] { new ConversationSpan(label, block.Role == ConversationRole.Assistant
                ? ConversationStyle.Heading : ConversationStyle.Muted) });
            if (block.Role == ConversationRole.Assistant)
            {
                rows.AddRange(MarkdownRenderer.Render(block.Text));
                continue;
            }
            foreach (var source in block.Text.Replace("\r\n", "\n").Split('\n'))
                rows.Add(new[] { new ConversationSpan(source, block.Role == ConversationRole.System
                    ? ConversationStyle.Muted : ConversationStyle.Text) });
        }
        return rows;
    }
}
