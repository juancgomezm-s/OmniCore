namespace OmniCore.Protocol;

/// <summary>Versioned JSON-lines contract for the first CLI↔Host stdio transport block.</summary>
public static class StdioProtocol
{
    public const int MaximumFrameBytes = 1024 * 1024;
    public const string QueryOperation = "query";
    public const string SubscribeOperation = "subscribe";
    public const string CommandOperation = "command";
    public const string CommandResultOperation = "command.result";
    public const string ReplayPageOperation = "events.replay.page";
    public const string ConversationResultOperation = "conversation.result";

    public static readonly string[] SupportedCapabilities =
    [
        "command.sim",
        "command.sim.resume",
        "command.session.input",
        "command.result.lookup",
        "query.state",
        "query.sessionIdentity",
        "query.workingState",
        "query.commands",
        "query.sessionSidebar",
        "query.changedFiles",
        "query.agents",
        "query.sessionObservability",
        "events.replay",
        "events.replay.page",
        "conversation.result.lookup",
    ];

    /// <summary>Capabilities available only to a stdio connection with a private user-action bootstrap.</summary>
    public static readonly string[] TrustedUserActionCapabilities =
    [
        "command.session.input.user",
        "command.conversation.start.user",
        "command.run.interrupt.user",
        "command.run.cancel.user",
        "command.run.mode.select.user",
        "query.humanActionScope",
    ];

    public static bool IsSupportedQuery(string name) => name is "state" or "sessionIdentity" or "workingState" or "commands"
        or "sessionSidebar" or "changedFiles" or "agents" or "sessionObservability" or "humanActionScope";

    public static bool IsSupportedCommand(string name) => name is "sim" or "sim.resume" or "session.input"
        or "conversation.start" or "run.interrupt" or "run.cancel" or "run.mode.select";
}
