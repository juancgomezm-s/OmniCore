namespace OmniCore.Host;

using OmniCore.Protocol;

/// <summary>CommandService del Host: expande PromptCommands registrados a input tipado.</summary>
public sealed class CommandService
{
    public PromptExpanded Expand(CommandInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!string.Equals(invocation.Name, "explain", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("command.prompt_not_found:" + invocation.Name);
        var target = invocation.Arguments.Count == 0 ? "." : string.Join(" ", invocation.Arguments);
        var prompt = "Explain the repository content at the path " + target
            + ". Ground the explanation in files you inspect; distinguish observed facts from inference."
            + " Respond in Spanish unless the user explicitly requests another language.";
        return new PromptExpanded(prompt, "PromptCommand(core:explain@1)");
    }
}
