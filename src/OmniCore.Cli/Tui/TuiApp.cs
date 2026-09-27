using OmniCore.Client;

namespace OmniCore.Cli;

/// <summary>
/// TUI v0 desacoplada (ADR-0030/0031). NOTA: en este toolchain (runtime basado en JVM), el
/// ensamblado Terminal.Gui 2.5.0 no es enlazable como tipos compilables, así que la TUI de M1
/// dibuja las 4 zonas (header, conversación + sidebar, composer, status line) con escape ANSI
/// reutilizando EXACTAMENTE los modelos de la ClientProjection. El contrato con el DOMINO no
/// cambia: todos los renderers (TUI, plain, JSON) leen el mismo estado (ADR-0030 §3).
/// </summary>
public sealed class TuiApp
{
    /// <summary>Renderiza el estado actual como frame único en la terminal.</summary>
    public static string Render(ClientState state, string locale)
    {
        var lines = new List<string>();
        lines.Add(RenderHeader(state.Header));
        lines.AddRange(RenderConversation(state.Conversation));
        lines.Add(RenderSidebar(state.Sidebar));
        lines.Add(RenderStatusLine(state.StatusLine, locale));
        return string.Join("\n", lines);
    }

    private static string RenderHeader(HeaderModel header)
    {
        var dir = header.WorkingDirectory.Length == 0 ? "." : header.WorkingDirectory;
        var git = header.GitBranch is null ? "" : " [git " + header.GitBranch + "]";
        return "\u001b[1m" + dir + git + "\u001b[0m";
    }

    private static IReadOnlyList<string> RenderConversation(ConversationModel conversation)
    {
        var lines = new List<string>();
        foreach (var block in conversation.Blocks)
        {
            lines.Add(ConversationLine(block));
        }

        return lines;
    }

    private static string ConversationLine(ConversationBlock block)
    {
        switch (block.Role)
        {
            case ConversationRole.User:
                return "\u001b[36m\u25ce " + block.Text + "\u001b[0m";
            case ConversationRole.Assistant:
                return "\u001b[32m\u2666 " + block.Text + "\u001b[0m";
            case ConversationRole.System:
                return "\u001b[90m" + block.Text + "\u001b[0m";
            case ConversationRole.Tool:
                return "\u001b[33m[" + (block.ToolName ?? "tool") + "] " + block.Text + "\u001b[0m";
            case ConversationRole.Interaction:
                return "\u001b[35m? " + block.Text + "\u001b[0m";
            default:
                return block.Text;
        }
    }

    private static string RenderSidebar(SidebarModel sidebar)
    {
        if (sidebar.WidgetIds.Count == 0)
        {
            return "";
        }

        return "\u001b[2m— sidebar: " + string.Join(", ", sidebar.WidgetIds) + "\u001b[0m";
    }

    private static string RenderStatusLine(StatusLineModel status, string locale)
    {
        var quota = status.Quota ?? "\u2014";
        var pending = status.PendingInteractions is > 0
            ? "  ! " + status.PendingInteractions + " pendientes"
            : "";
        return "\u001b[7m" + status.Mode + "  " + quota + pending + "\u001b[0m";
    }
}