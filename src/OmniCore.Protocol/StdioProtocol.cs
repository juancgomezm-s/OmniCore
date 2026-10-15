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
    ];

    public static bool IsSupportedQuery(string name) => name is "state" or "sessionIdentity" or "workingState" or "commands"
        or "sessionSidebar" or "changedFiles" or "agents" or "sessionObservability";

    public static bool IsSupportedCommand(string name) => name is "sim" or "sim.resume" or "session.input";
}
