namespace OmniCore.Client;

using OmniCore.Protocol;

/// <summary>
/// Proyecciones de presentación del cliente (ADR-0030 §3, ADR-0033). El ClientProjection es un
/// reducer puro: (ClientState, WireEvent | QueryResult | LocalAction) → ClientState. No conoce
/// terminales ni frameworks visuales; solo produce modelos de presentación con roles semánticos.
/// </summary>

/// <summary>Estado completo del cliente para todos los renderers (ADR-0030 §3).</summary>
public sealed class ClientState
{
    public HeaderModel Header { get; }

    public ConversationModel Conversation { get; }

    public SidebarModel Sidebar { get; }

    public ComposerModel Composer { get; }

    public StatusLineModel StatusLine { get; }

    public IReadOnlyList<InteractionOverlayModel> Overlays { get; }

    public ConnectionState Connection { get; }

    public ClientState(HeaderModel header, ConversationModel conversation, SidebarModel sidebar,
        ComposerModel composer, StatusLineModel statusLine, IReadOnlyList<InteractionOverlayModel> overlays,
        ConnectionState connection)
    {
        Header = header;
        Conversation = conversation;
        Sidebar = sidebar;
        Composer = composer;
        StatusLine = statusLine;
        Overlays = overlays;
        Connection = connection;
    }

    public static ClientState Empty() =>
        new ClientState(
            HeaderModel.Empty(),
            ConversationModel.Empty(),
            SidebarModel.Empty(),
            ComposerModel.Empty(),
            StatusLineModel.Empty(),
            new InteractionOverlayModel[0],
            new ConnectionState(ClientStatus.Connected));
}

/// <summary>Estado de la conexión del cliente.</summary>
public enum ClientStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

public sealed class ConnectionState
{
    public ClientStatus Status { get; }

    public ConnectionState(ClientStatus status) => Status = status;
}

/// <summary>Header: cwd dominante + git compacto (ADR-0031).</summary>
public sealed class HeaderModel
{
    public string WorkingDirectory { get; }

    public string? GitBranch { get; }

    public string? GitDirty { get; }

    public HeaderModel(string workingDirectory, string? gitBranch, string? gitDirty)
    {
        WorkingDirectory = workingDirectory;
        GitBranch = gitBranch;
        GitDirty = gitDirty;
    }

    public static HeaderModel Empty() => new(string.Empty, null, null);
}

/// <summary>Fila de la conversación con su rol semántico.</summary>
public sealed class ConversationBlock
{
    public string Id { get; }

    public ConversationRole Role { get; }

    public string Text { get; }

    public string? ToolName { get; }

    public ConversationBlock(string id, ConversationRole role, string text, string? toolName)
    {
        Id = id;
        Role = role;
        Text = text;
        ToolName = toolName;
    }
}

/// <summary>Rol semántico de un bloque de conversación.</summary>
public enum ConversationRole
{
    User,
    Assistant,
    Tool,
    System,
    Interaction,
}

public sealed class ConversationModel
{
    public IReadOnlyList<ConversationBlock> Blocks { get; }

    public ConversationModel(IReadOnlyList<ConversationBlock> blocks) => Blocks = blocks;

    public static ConversationModel Empty() => new(new ConversationBlock[0]);
}

/// <summary>Modelo del sidebar con widgets y relevancia (ADR-0032).</summary>
public sealed class SidebarModel
{
    public IReadOnlyList<string> WidgetIds { get; }

    public SidebarModel(IReadOnlyList<string> widgetIds) => WidgetIds = widgetIds;

    public static SidebarModel Empty() => new(new string[0]);
}

/// <summary>Composer con input en partes (ADR-0033).</summary>
public sealed class ComposerModel
{
    public string Placeholder { get; }

    public string Draft { get; }

    public ComposerModel(string placeholder, string draft)
    {
        Placeholder = placeholder;
        Draft = draft;
    }

    public static ComposerModel Empty() => new("Escribe un objetivo o /comando (es)", string.Empty);
}

/// <summary>Status line con métricas; sin cuota informada muestra — (ADR-0031).</summary>
public sealed class StatusLineModel
{
    public string? Quota { get; }

    public int? PendingInteractions { get; }

    public string Mode { get; }

    public StatusLineModel(string? quota, int? pendingInteractions, string mode)
    {
        Quota = quota;
        PendingInteractions = pendingInteractions;
        Mode = mode;
    }

    public static StatusLineModel Empty() => new(null, null, "act");
}

/// <summary>Overlay de interacción pendiente (ADR-0034).</summary>
public sealed class InteractionOverlayModel
{
    public string Title { get; }

    public IReadOnlyList<string> Options { get; }

    public InteractionOverlayModel(string title, IReadOnlyList<string> options)
    {
        Title = title;
        Options = options;
    }
}

/// <summary>Acciones locales del cliente (recortar, cambiar modo) que reducen el estado.</summary>
public sealed class LocalAction
{
    public string Name { get; }

    public string? Arg { get; }

    private LocalAction(string name, string? arg)
    {
        Name = name;
        Arg = arg;
    }

    public static LocalAction Mode(string mode) => new("mode", mode);

    public static LocalAction Draft(string text) => new("draft", text);
}

/// <summary>
/// Reducer puro del cliente (ADR-0030 §3): consume eventos wire y acciones locales y produce
/// el siguiente <c>ClientState</c>. Testeable sin terminal. Traduce los eventos del server a
/// bloques de conversación semánticos (ADR-0033).
/// </summary>
public sealed class ClientProjection
{
    public ClientState Apply(ClientState state, WireEnvelope evt)
    {
        if (evt.MessageType != OmniCore.Protocol.MessageTypes.Event)
        {
            return state;
        }

        var fields = OmniCore.Protocol.JsonObj.Parse(evt.PayloadJson);
        var type = fields.TryGetValue("type", out var t) ? t : null;
        var block = ToBlock(type, fields);
        if (block is null)
        {
            return state;
        }

        var existing = state.Conversation.Blocks;
        var next = new ConversationBlock[existing.Count + 1];
        for (var i = 0; i < existing.Count; i++)
        {
            next[i] = existing[i];
        }

        next[existing.Count] = block;
        var conversation = new ConversationModel(next);
        return new ClientState(state.Header, conversation, state.Sidebar, state.Composer, state.StatusLine,
            state.Overlays, state.Connection);
    }

    public ClientState ApplyLocal(ClientState state, LocalAction action)
    {
        if (action.Name == "draft")
        {
            return new ClientState(state.Header, state.Conversation,
                state.Sidebar, new ComposerModel(state.Composer.Placeholder, action.Arg ?? ""),
                state.StatusLine, state.Overlays, state.Connection);
        }

        return state;
    }

    private static ConversationBlock? ToBlock(string? type, Dictionary<string, string> fields)
    {
        if (type == "sim.events")
        {
            var run = fields.TryGetValue("run", out var r) ? r! : "?";
            var exit = fields.TryGetValue("exitCode", out var e) ? e! : "?";
            return new ConversationBlock("sim.events", ConversationRole.System,
                "Simulación: Run terminó en " + run + " (exit " + exit + ")", null);
        }

        if (type == "sim.resumed")
        {
            var reconciled = fields.TryGetValue("reconciled", out var n) ? n! : "0";
            return new ConversationBlock("sim.resumed", ConversationRole.System,
                "Resume: toolcall reconciliada sin duplicar efecto (" + reconciled + ")", null);
        }

        return null;
    }
}