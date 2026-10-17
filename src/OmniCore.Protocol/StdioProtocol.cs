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
    public const string ArtifactReadOperation = "artifact.read";
    public const string EventStreamOperation = "events.stream";
    public const string EventAckOperation = "events.ack";
    public const string EventReadyOperation = "events.ready";
    public const string EventStopOperation = "events.stop";

    public static readonly string[] SupportedCapabilities =
    [
        "command.sim",
        "command.sim.resume",
        "command.session.input",
        "command.result.lookup",
        "query.state",
        "query.sessionIdentity",
        "query.sessionSecurity",
        "query.sessionSecurityDefault",
        "query.workingState",
        "query.commands",
        "query.sessionSidebar",
        "query.changedFiles",
        "query.agents",
        "query.agentSources",
        "query.sessionObservability",
        "events.replay",
        "events.replay.page",
        "events.stream.v1",
        "conversation.result.lookup",
        "artifact.read.chunk",
        "query.nativeCatalog",
        "query.commandOutcome",
        "query.toolCatalog",
        "query.extensionCatalog",
        "query.mcpConfiguration",
        "query.hookCatalog",
        "query.skillCatalog",
        "query.sessionMemory",
        "query.modePreferences",
        "query.reasoningPreferences",
        "query.workspaceStatus",
        "query.turnContext",
        "query.openRouterStatus",
        "query.uiPreferences",
    ];

    /// <summary>Capabilities available only to a stdio connection with a private user-action bootstrap.</summary>
    public static readonly string[] TrustedUserActionCapabilities =
    [
        "command.session.input.user",
        "command.conversation.start.user",
        "command.session.security.set.user",
        "command.session.security.default.set.user",
        "command.run.interrupt.user",
        "command.run.cancel.user",
        "command.run.mode.select.user",
        "command.attachment.upload.begin.user",
        "command.attachment.upload.chunk.user",
        "command.attachment.upload.finish.user",
        "command.external.memory.consent.user",
        "query.humanActionScope",
    ];

    public static IReadOnlyList<NativeQueryDescriptor> NativeQueries { get; } =
    [
        new("native.catalog", "query.nativeCatalog", "Client API metadata", "connection", "pure", true),
        new("commandOutcome", "query.commandOutcome", "Metadata about the latest prompt expansion or workflow request", "session", "session-read", true),
        new("state", "query.state", "Current runtime state", "session", "session-read", true),
        new("sessionIdentity", "query.sessionIdentity", "Current Session and Run identity", "session", "session-read", true),
        new("sessionSecurity", "query.sessionSecurity", "Durable Session security ceiling and revision", "session", "session-read", true),
        new("sessionSecurityDefault", "query.sessionSecurityDefault", "Configured default ceiling for new Sessions", "user", "configuration-read", true),
        new("workingState", "query.workingState", "Current plan and working state", "session", "session-read", true),
        new("commands", "query.commands", "Effective human command catalog", "session", "workspace-read", true),
        new("sessionSidebar", "query.sessionSidebar", "Session sidebar and persisted artifacts", "session", "session-read", true),
        new("changedFiles", "query.changedFiles", "Changed files and artifact references", "session", "session-read", true),
        new("agents", "query.agents", "Agent lanes and delegated tasks", "session", "session-read", true),
        new("agent.sources", "query.agentSources", "External agent instruction and memory sources", "workspace", "workspace-read", true),
        new("sessionObservability", "query.sessionObservability", "Session activity and measured usage; cursor is bounded", "session", "session-read", true),
        new("humanActionScope", "query.humanActionScope", "Current trusted-action scope on this native connection", "trusted-session", "session-read", false),
        new("tools", "query.toolCatalog", "Effective tool catalog and policy decisions", "session", "workspace-read", true),
        new("extensions", "query.extensionCatalog", "Installed extension inventory and consent metadata", "workspace", "workspace-read", true),
        new("mcp", "query.mcpConfiguration", "Configured MCP endpoints and trust metadata; does not probe endpoints", "workspace", "workspace-read", true),
        new("hooks", "query.hookCatalog", "Hook inventory and effective state", "workspace", "workspace-read", true),
        new("skills", "query.skillCatalog", "Skill inventory and activation metadata", "session", "workspace-read", true),
        new("memory.session", "query.sessionMemory", "Current Session memory and candidates", "session", "session-read", true),
        new("modePreference", "query.modePreferences", "User default mode preference", "user", "configuration-read", true),
        new("modeAuthority", "query.modePreferences", "Current mode authority and active scope", "session", "session-read", true),
        new("reasoningPreference", "query.reasoningPreferences", "User reasoning preference", "user", "configuration-read", true),
        new("runReasoningPreference", "query.reasoningPreferences", "Current Run reasoning selection", "session", "session-read", true),
        new("workspaceStatus", "query.workspaceStatus", "Workspace identity, trust and runtime status", "workspace", "workspace-read", true),
        new("context", "query.turnContext", "Last persisted model context snapshot", "session", "session-read", true),
        new("turnFingerprint", "query.turnContext", "Effective current-turn capability fingerprint", "session", "session-read", true),
        new("openrouter", "query.openRouterStatus", "OpenRouter connection status", "user", "configuration-read", true),
        new("ui.locale", "query.uiPreferences", "Effective interface locale preference", "user", "configuration-read", true),
        new("sidebarSettings", "query.uiPreferences", "Sidebar preferences", "user", "configuration-read", true),
    ];

    public static bool IsSupportedQuery(string name) => name == "humanActionScope"
        ? true
        : NativeQueries.Any(query => query.Name == name || query.WireName == name)
            || name.StartsWith("sessionObservability:", StringComparison.Ordinal);

    public static bool TryGetQuery(string name, out NativeQueryDescriptor descriptor)
    {
        descriptor = NativeQueries.FirstOrDefault(query => query.Name == name || query.WireName == name
            || query.Name == "sessionObservability" && name.StartsWith("sessionObservability:", StringComparison.Ordinal))!;
        return descriptor is not null;
    }

    public static string EncodeNativeCatalog(IEnumerable<string> negotiatedCapabilities)
    {
        var capabilities = negotiatedCapabilities.ToHashSet(StringComparer.Ordinal);
        static string Q(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
        var queries = string.Join(",", NativeQueries.Select(query => "{\"id\":" + Q(query.Name)
            + ",\"wireName\":" + Q(query.WireName) + ",\"resultName\":" + Q(query.ResultName)
            + ",\"capability\":" + Q(query.Capability) + ",\"summary\":" + Q(query.Summary)
            + ",\"scope\":" + Q(query.Scope) + ",\"effects\":" + Q(query.Effects)
            + ",\"safeToRefreshAutomatically\":" + (query.SafeToRefreshAutomatically ? "true" : "false")
            + ",\"availability\":" + Q(capabilities.Contains(query.Capability) ? "available" : "unsupported") + "}"));
        var commands = string.Join(",", NativeCommands.Select(command => "{\"id\":" + Q(command.Name)
            + ",\"capability\":" + Q(command.Capability) + ",\"authority\":" + Q(command.Authority)
            + ",\"receipt\":" + Q(command.Receipt) + ",\"summary\":" + Q(command.Summary)
            + ",\"availability\":" + Q(capabilities.Contains(command.Capability) ? "available" : "unsupported") + "}"));
        var streams = "{\"id\":\"events.stream.v1\",\"capability\":\"events.stream.v1\",\"ackAfterConsume\":true,\"boundedWindow\":true,\"availability\":"
            + Q(capabilities.Contains("events.stream.v1") ? "available" : "unsupported") + "},"
            + "{\"id\":\"events.replay.page\",\"capability\":\"events.replay.page\",\"highWaterPinned\":true,\"cursorBounded\":true,\"availability\":"
            + Q(capabilities.Contains("events.replay.page") ? "available" : "unsupported") + "}";
        var artifacts = "{\"id\":\"artifact.read.chunk\",\"capability\":\"artifact.read.chunk\",\"boundedBytes\":true,\"availability\":"
            + Q(capabilities.Contains("artifact.read.chunk") ? "available" : "unsupported") + "}";
        return "{\"apiVersion\":1,\"protocolVersion\":\"1.0\",\"queries\":[" + queries
            + "],\"commands\":[" + commands + "],\"eventApis\":[" + streams + "],\"artifactApis\":[" + artifacts + "]"
            + ",\"excluded\":[{\"id\":\"diff.open\",\"availability\":\"not_exposed\",\"reason\":\"Path-parameterized artifact reads require a dedicated bounded contract.\"},"
            + "{\"id\":\"complete\",\"availability\":\"not_exposed\",\"reason\":\"Filesystem completion can read arbitrary paths and has no native scoped request DTO yet.\"},"
            + "{\"id\":\"humanActionScope\",\"availability\":\"trusted_connection_only\",\"capability\":\"query.humanActionScope\"}]}";
    }

    public static bool IsSupportedCommand(string name) => name is "sim" or "sim.resume" or "session.input"
        or "conversation.start" or "session.security.set" or "session.security.default.set" or "run.interrupt" or "run.cancel" or "run.mode.select"
        or "attachment.upload.begin" or "attachment.upload.chunk" or "attachment.upload.finish"
        or "external.memory.consent";

    public static IReadOnlyList<NativeCommandDescriptor> NativeCommands { get; } =
    [
        new("sim", "command.sim", "generic", "command.result.lookup", "Submit a deterministic simulation scenario"),
        new("sim.resume", "command.sim.resume", "generic", "command.result.lookup", "Resume a deterministic simulation"),
        new("session.input", "command.session.input", "generic", "command.result.lookup", "Persist literal user input without inference"),
        new("conversation.start", "command.conversation.start.user", "native-confirmation+hmac+session-scope", "conversation.result.lookup", "Persist input and invoke the Core conversation pipeline"),
        new("session.security.set", "command.session.security.set.user", "native-confirmation+hmac+session-revision", "command.result.lookup", "Set a revision-checked Session capability ceiling"),
        new("session.security.default.set", "command.session.security.default.set.user", "native-confirmation+hmac+user-revision", "", "Set the configured security ceiling for new Sessions"),
        new("run.interrupt", "command.run.interrupt.user", "native-confirmation+hmac+run-scope", "command.result.lookup", "Interrupt the active Run"),
        new("run.cancel", "command.run.cancel.user", "native-confirmation+hmac+run-scope", "command.result.lookup", "Cancel the active Run"),
        new("run.mode.select", "command.run.mode.select.user", "native-confirmation+hmac+session-scope", "command.result.lookup", "Select the active Run mode"),
        new("attachment.upload.begin", "command.attachment.upload.begin.user", "native-confirmation+hmac+session-scope", "", "Begin bounded attachment upload"),
        new("attachment.upload.chunk", "command.attachment.upload.chunk.user", "native-confirmation+hmac+session-scope", "", "Upload a bounded attachment chunk"),
        new("attachment.upload.finish", "command.attachment.upload.finish.user", "native-confirmation+hmac+session-scope", "", "Finalize attachment upload"),
        new("external.memory.consent", "command.external.memory.consent.user", "native-confirmation+hmac+user-scope", "command.result.lookup", "Change external memory consent"),
    ];
}

/// <summary>Versioned, transport-neutral metadata for an explicit native Host query.</summary>
public sealed record NativeQueryDescriptor(string Name, string Capability, string Summary,
    string Scope, string Effects, bool SafeToRefreshAutomatically)
{
    public string WireName => Name == "ui.locale" ? "uiLocaleSettings" : Name;
    public string ResultName => WireName;
    public string DispatchName(string requestedName) => Name == "sessionObservability"
        && requestedName.StartsWith("sessionObservability:", StringComparison.Ordinal)
        ? requestedName : WireName;
}

/// <summary>Metadata for a Host command; authority remains enforced by Host policy and proof checks.</summary>
public sealed record NativeCommandDescriptor(string Name, string Capability, string Authority,
    string Receipt, string Summary);
