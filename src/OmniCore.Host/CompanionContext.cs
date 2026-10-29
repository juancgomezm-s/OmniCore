using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;

namespace OmniCore.Host;

public static class CompanionContext
{
    public static long Capacity(bool local, long declared) => local ? Math.Min(32768, declared > 0 ? declared : 32768) : declared > 0 ? declared : 32768;
    public static int ResponseReserve(long capacity, long declaredOutput) => (int)Math.Min(declaredOutput > 0 ? declaredOutput : capacity / 8,
        Math.Max(128, capacity / 8));
    public static async Task<(List<ModelMessage> Messages, string Summary)> Fit(List<ModelMessage> source, string instruction,
        IReadOnlyList<ToolDefinition> tools, long capacity, string summary, ITokenCounter counter, CancellationToken token)
    {
        var messages = source.ToList();
        async Task<int> Count()
        {
            var text = instruction + summary + string.Join("\n", messages.Select(m => m.Role + ":" + string.Join("\n", m.Content.Select(Block))))
                + string.Join("\n", tools.Select(t => t.Name + t.Description + t.InputSchemaJson));
            return await counter.CountAsync(new ContextItem("companion", ContextItemKind.UserMessage, text, 0, ContextPriority.Normal,
                RetentionPolicy.KeepForever, new("companion", ContributionCategory.System, "host", ScopeLevel.Run, false)), token).ConfigureAwait(false);
        }
        var measured = await Count().ConfigureAwait(false);
        while (measured > capacity)
        {
            // Remove complete old exchanges, retaining the latest user and all of its tool pairs.
            var nextUser = messages.FindIndex(1, m => m.Role == MessageRole.User);
            if (nextUser < 1) throw new InvalidOperationException("Esta entrada y sus herramientas superan la ventana real del modelo. Divide el mensaje o elige un modelo de mayor contexto.");
            var textSizes = messages.Select(m => string.Join("\n", m.Content.Select(Block)).Length).ToArray();
            var needed = (measured - capacity) * Math.Max(1d, textSizes.Sum(size => (long)size) / (double)measured);
            long removedSize = textSizes.Take(nextUser).Sum(size => (long)size);
            while (removedSize < needed)
            {
                var following = messages.FindIndex(nextUser + 1, m => m.Role == MessageRole.User);
                if (following < 0) break;
                removedSize += textSizes.Skip(nextUser).Take(following - nextUser).Sum(size => (long)size); nextUser = following;
            }
            var older = messages.Take(nextUser).Select(m => m.Role + ": " + string.Join(" ", m.Content.OfType<TextBlock>().Select(t => t.Text)));
            var excerpt = string.Join("\n", older.Select(t => t.Length <= 320 ? t : t[..240] + " […] " + t[^64..]));
            summary = (summary + "\n" + excerpt).Trim();
            var maxSummaryChars = (int)Math.Clamp(capacity / 4, 512, 20000);
            if (summary.Length > maxSummaryChars) summary = "[Extractos anteriores en el historial local]\n" + summary[^maxSummaryChars..];
            messages.RemoveRange(0, nextUser);
            measured = await Count().ConfigureAwait(false);
        }
        return (messages, summary);
    }
    private static string Block(ContentBlock block) => block switch
    {
        TextBlock text => text.Text,
        ToolCallBlock call => call.ToolName + call.ArgumentsJson,
        ToolResultBlock result => result.Id + ":" + result.IsError + ":" + string.Join("\n", result.Content.Select(Block)),
        _ => block.ToString() ?? ""
    };
}
