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

    public string ProductEffort { get; }

    /// <summary>Native reasoning value applied to the most recently started model step; null means none reported.</summary>
    public string? AppliedReasoning { get; }

    public StatusLineModel(string? quota, int? pendingInteractions, string mode, string productEffort = "standard",
        string? appliedReasoning = null)
    {
        Quota = quota;
        PendingInteractions = pendingInteractions;
        Mode = mode;
        ProductEffort = productEffort;
        AppliedReasoning = appliedReasoning;
    }

    public static StatusLineModel Empty() => new(null, null, "act");
}

/// <summary>Overlay de interacción pendiente (ADR-0034): opciones decididas por el servidor.</summary>
public sealed class InteractionOverlayModel
{
    /// <summary>Id de la interacción (para responderla y quitar el overlay al resolverse).</summary>
    public string Id { get; }

    /// <summary>Tipo de interacción tal como lo envía el servidor (Permission, PlanApproval…).</summary>
    public string Kind { get; }

    public string Title { get; }

    /// <summary>Resumen seguro del sujeto de la interacción (tool, destino y motivo).</summary>
    public string Subject { get; }

    /// <summary>Etiquetas localizadas de las opciones, en el orden del servidor.</summary>
    public IReadOnlyList<string> Options { get; }

    /// <summary>Ids de las opciones (lo que se envía en <c>interaction.respond</c>).</summary>
    public IReadOnlyList<string> OptionIds { get; }

    public string DefaultOptionId { get; }

    public QuestionnaireOverlayModel? Questionnaire { get; }

    public InteractionOverlayModel(string title, IReadOnlyList<string> options)
        : this(string.Empty, string.Empty, title, string.Empty, options, options)
    {
    }

    public InteractionOverlayModel(string id, string kind, string title, IReadOnlyList<string> options,
        IReadOnlyList<string> optionIds)
        : this(id, kind, title, string.Empty, options, optionIds)
    {
    }

    public InteractionOverlayModel(string id, string kind, string title, string subject,
        IReadOnlyList<string> options, IReadOnlyList<string> optionIds,
        QuestionnaireOverlayModel? questionnaire = null, string? defaultOptionId = null)
    {
        Id = id;
        Kind = kind;
        Title = title;
        Subject = subject;
        Options = options;
        OptionIds = optionIds;
        Questionnaire = questionnaire;
        DefaultOptionId = defaultOptionId ?? optionIds.FirstOrDefault() ?? string.Empty;
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
/// Reducer puro del cliente (ADR-0030 §3): consume eventos del protocolo y acciones locales y
/// produce el siguiente <c>ClientState</c>. Testeable sin terminal. Traduce los eventos del
/// servidor (los de dominio mapeados por el Host y sus notificaciones) a bloques de conversación
/// semánticos, overlays de interacción y status line (ADR-0033, ADR-0034, ADR-0031). Los textos
/// visibles salen de <see cref="Localization"/> (ADR-0040).
/// </summary>
public sealed class ClientProjection
{
    private readonly Localization _text;

    public ClientProjection() : this(Localization.Spanish())
    {
    }

    public ClientProjection(Localization text) => _text = text;

    public ClientState Apply(ClientState state, WireEnvelope evt)
    {
        if (evt.MessageType != OmniCore.Protocol.MessageTypes.Event)
        {
            return state;
        }

        var f = OmniCore.Protocol.JsonObj.Parse(evt.PayloadJson);
        var type = Get(f, "type");
        switch (type)
        {
            case "run.created":
                return WithStatus(Append(state, Block(evt, ConversationRole.System,
                    _text.Resolve("run.created", "objective", Get(f, "objective")))), Get(f, "mode"), null,
                    replaceAppliedReasoning: true);
            case "run.mode_changed":
                return WithStatus(Append(state, Block(evt, ConversationRole.System,
                    _text.Resolve("run.mode_changed", "mode", _text.Resolve("status.mode." + Get(f, "to"), null, null)))),
                    Get(f, "to"), null);
            case "run.mode_authority_selected":
                return WithStatus(state, Get(f, "mode"), null, Get(f, "effort"));
            case "run.mode_authority_revoked":
                return WithStatus(state, null, null, "standard");
            case "model_step.started":
            {
                var kind = Get(f, "reasoningKind");
                var budget = Get(f, "reasoningBudgetTokens");
                var applied = kind.Length == 0 ? null : kind + (budget.Length == 0 ? "" : ":" + budget);
                return WithStatus(state, null, null, appliedReasoning: applied, replaceAppliedReasoning: true);
            }
            case "run.awaiting_input":
                return Append(state, Block(evt, ConversationRole.System, _text.Resolve("run.awaiting_input", null, null)));
            case "run.completed":
                return Append(state, Block(evt, ConversationRole.System,
                    _text.Resolve("run.completed." + Get(f, "outcome").ToLowerInvariant(), null, null)));
            case "run.failed":
                return Append(state, Block(evt, ConversationRole.System, _text.Resolve("run.failed", "cause", Get(f, "cause"))));
            case "run.cancelled":
                return Append(state, Block(evt, ConversationRole.System, _text.Resolve("run.cancelled", null, null)));
            case "user_input.received":
                return Append(state, Block(evt, ConversationRole.User, Get(f, "text")));
            case "assistant_message.recorded":
                return Get(f, "text").Length == 0 ? state : Append(state, Block(evt, ConversationRole.Assistant, Get(f, "text")));
            case "toolcall.requested":
                return Append(state, new ConversationBlock(evt.MessageId, ConversationRole.Tool,
                    _text.Resolve("tool.requested", "tool", Get(f, "tool")), Get(f, "tool")));
            case "toolcall.failed":
            case "toolcall.rejected":
            case "toolcall.permission_denied":
            case "toolcall.cancelled":
                return Append(state, Block(evt, ConversationRole.Tool, _text.Resolve("tool.failed", "cause", Get(f, "cause"))));
            case "interaction.requested":
                return WithOverlays(state, state.Overlays.Append(Overlay(f)).ToArray());
            case "interaction.resolved":
            case "interaction.expired":
                return WithOverlays(state, state.Overlays.Where(o => o.Id != Get(f, "interactionId")).ToArray());
            case "sim.events":
                return Append(state, new ConversationBlock("sim.events", ConversationRole.System,
                    _text.Resolve("sim.finished", "run", Get(f, "run")).Replace("{exit}", Get(f, "exitCode")), null));
            case "sim.resumed":
                return Append(state, new ConversationBlock("sim.resumed", ConversationRole.System,
                    _text.Resolve("sim.resumed", "count", Get(f, "reconciled")), null));
            default:
                return state;
        }
    }

    /// <summary>Applies Host read-model queries through the same reducer used for wire events.</summary>
    public ClientState ApplyQuery(ClientState state, SessionQueryResult query)
    {
        if (query.Name == "workspaceStatus")
        {
            var fields = JsonObj.Parse(query.Json);
            var directory = fields.TryGetValue("workingDirectory", out var value) ? value ?? "" : "";
            return new ClientState(new HeaderModel(directory, null, null), state.Conversation,
                new SidebarModel(new[] { "core.session", "core.plan", "core.files" }), state.Composer,
                state.StatusLine, state.Overlays, state.Connection);
        }
        return state;
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

    private InteractionOverlayModel Overlay(Dictionary<string, string> f)
    {
        var kind = Get(f, "kind");
        var prefix = "interaction." + Snake(kind) + ".";
        var ids = Get(f, "options").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var labels = ids.Select(id => Label(prefix + id, id)).ToArray();
        var subject = SubjectText(Get(f, "subject"));
        var questionnaire = kind == "Question"
            ? QuestionnairePresentationFactory.FromJson(Get(f, "questionnaire"), _text.Locale)
            : null;
        var title = kind == "BudgetExceeded" && ids.Contains("allow_quota", StringComparer.Ordinal)
            ? Label("interaction.included_quota.title", kind) : Label(prefix + "title", kind);
        return new InteractionOverlayModel(Get(f, "interactionId"), kind, title,
            subject, labels, ids, questionnaire, Get(f, "defaultOption"));
    }

    private string SubjectText(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            var parts = new List<string>();
            AddLocalized("operation");
            Add("toolOrExecutable");
            Add("target");
            Add("detail");
            AddLocalized("reason");
            if (root.TryGetProperty("details", out var details) && details.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                    if (detail.ValueKind == System.Text.Json.JsonValueKind.Object
                        && detail.TryGetProperty("value", out var value) && value.GetString() is { Length: > 0 } text)
                        parts.Add(text);
            }
            return string.Join(" · ", parts);

            void Add(string name)
            {
                if (root.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text)
                    parts.Add(text);
            }

            void AddLocalized(string name)
            {
                if (root.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text)
                    parts.Add(Label(text, text));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Texto localizado de una clave; si no existe, el valor técnico (nunca la clave cruda).</summary>
    private string Label(string key, string fallback)
    {
        var text = _text.Resolve(key, null, null);
        return text == key ? fallback : text;
    }

    private static string Snake(string pascal)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            if (char.IsUpper(pascal[i]) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(pascal[i]));
        }

        return builder.ToString();
    }

    private static string Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : string.Empty;

    private static ConversationBlock Block(WireEnvelope evt, ConversationRole role, string text) =>
        new(evt.MessageId, role, text, null);

    private static ClientState Append(ClientState state, ConversationBlock block) =>
        new(state.Header, new ConversationModel(state.Conversation.Blocks.Append(block).ToArray()), state.Sidebar,
            state.Composer, state.StatusLine, state.Overlays, state.Connection);

    private static ClientState WithStatus(ClientState state, string? mode, int? pending, string? effort = null,
        string? appliedReasoning = null, bool replaceAppliedReasoning = false) =>
        new(state.Header, state.Conversation, state.Sidebar, state.Composer,
            new StatusLineModel(state.StatusLine.Quota, pending ?? state.StatusLine.PendingInteractions,
                string.IsNullOrEmpty(mode) ? state.StatusLine.Mode : mode!,
                string.IsNullOrEmpty(effort) ? state.StatusLine.ProductEffort : effort!,
                replaceAppliedReasoning ? appliedReasoning : state.StatusLine.AppliedReasoning),
            state.Overlays, state.Connection);

    /// <summary>Los overlays y el contador de pendientes de la status line cambian juntos.</summary>
    private static ClientState WithOverlays(ClientState state, IReadOnlyList<InteractionOverlayModel> overlays) =>
        new(state.Header, state.Conversation, state.Sidebar, state.Composer,
            new StatusLineModel(state.StatusLine.Quota, overlays.Count, state.StatusLine.Mode,
                state.StatusLine.ProductEffort, state.StatusLine.AppliedReasoning),
            overlays, state.Connection);
}
