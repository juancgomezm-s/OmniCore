using OmniCore.Client;

namespace OmniCore.Cli;

/// <summary>
/// Renderer plain (ADR-0030 §4): imprime la conversación con roles semánticos, sin dependencias
/// visuales. En M1 sin TTY o con `--plain`; el estilo viene de la ClientProjection (roles),
/// y la localización de los títulos de rol usa los recursos es/en (ADR-0040).
/// </summary>
public sealed class PlainRenderer
{
    private readonly Localization _localization;

    private int _printedBlocks;

    private readonly HashSet<string> _printedInteractions = new(StringComparer.Ordinal);

    public PlainRenderer(string locale) => _localization = new Localization(locale);

    /// <summary>Imprime los bloques de conversación nuevos (los del snapshot ya mostrados se omiten).</summary>
    public void Render(ClientState state)
    {
        var blocks = state.Conversation.Blocks;
        for (var i = _printedBlocks; i < blocks.Count; i++)
        {
            Print(blocks[i]);
        }

        _printedBlocks = blocks.Count;
        foreach (var interaction in state.Overlays)
        {
            if (interaction.Id.Length == 0 || !_printedInteractions.Add(interaction.Id)) continue;
            Print(interaction);
        }
    }

    private void Print(InteractionOverlayModel interaction)
    {
        Console.WriteLine("[" + _localization.Resolve("plain.interaction.pending")
            + "] " + interaction.Title + " (#" + interaction.Id + ")");
        if (interaction.Subject.Length > 0) Console.WriteLine("  " + interaction.Subject);
        for (var i = 0; i < interaction.Options.Count; i++)
            Console.WriteLine("  [" + (i + 1) + "] " + interaction.Options[i]
                + " (" + interaction.OptionIds[i] + ")");
        Console.WriteLine("  " + _localization.Resolve("plain.interaction.resolve", "id", interaction.Id));
    }

    private void Print(ConversationBlock block)
    {
        var role = new LocalizedLine(_localization.Locale).RoleLabel(block.Role);
        switch (block.Role)
        {
            case ConversationRole.System:
                Console.WriteLine(block.Text);
                break;
            case ConversationRole.User:
                Console.WriteLine("✎ " + block.Text);
                break;
            case ConversationRole.Assistant:
                Console.WriteLine("◈ " + block.Text);
                break;
            default:
                Console.WriteLine("[" + role + "] " + block.Text);
                break;
        }
    }
}

/// <summary>Resuelve etiquetas locales de rol (es por defecto, ADR-0040).</summary>
public sealed class LocalizedLine
{
    private readonly string _locale;

    public LocalizedLine(string locale) => _locale = locale;

    public string RoleLabel(ConversationRole role)
    {
        var key = role switch
        {
            ConversationRole.System => "plain.role.system",
            ConversationRole.User => "plain.role.user",
            ConversationRole.Assistant => "plain.role.assistant",
            ConversationRole.Tool => "plain.role.tool",
            ConversationRole.Interaction => "plain.role.interaction",
            _ => null,
        };
        return key is null ? "?" : new Localization(_locale).Resolve(key);
    }
}
