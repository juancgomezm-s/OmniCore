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
        "conversation.admission.session-security.v1",
        "command.result.lookup",
        "query.state",
        "query.sessionIdentity",
        "query.sessionSecurity",
        "query.sessionSecurityDefault",
        "query.modelsCatalog",
        "query.sessionModelSelection",
        "query.workingState",
        "query.commands",
        "query.sessionSidebar",
        "query.conversations",
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
        "query.extensionPlatform",
        "query.mcpConfiguration",
        "query.hookCatalog",
        "query.skillCatalog",
        "query.sessionMemory",
        "query.modePreferences",
        "query.reasoningPreferences",
        "query.workspaceStatus",
        "query.workspacePanel",
        "query.turnContext",
        "query.openRouterStatus",
        "query.uiPreferences",
    ];

    /// <summary>Capabilities available only to a stdio connection with a private user-action bootstrap.</summary>
    public static readonly string[] TrustedUserActionCapabilities =
    [
        "command.session.input.user",
        "command.conversation.start.user",
        "command.conversation.new.user",
        "command.conversation.resume.user",
        "command.conversation.rename.user",
        "command.conversation.compact.user",
        "command.conversation.alternative.user",
        "command.conversation.questionnaire.respond.user",
        "command.conversation.interaction.choice.respond.user",
        "command.conversation.weak-sandbox.respond.user",
        "command.conversation.interaction.continue.user",
        "command.invoke.user",
        "command.invoke.continue.user",
        "command.workflow.continue.user",
        "query.commandCatalog",
        "query.commandRecovery",
        "query.workflowRecovery",
        "command.session.security.set.user",
        "command.session.security.default.set.user",
        "command.session.model.select.user",
        "command.session.model.route.respond.user",
        "command.skills.activate.user",
        "command.skills.release.user",
        "command.memory.session.add.user",
        "command.memory.session.propose.user",
        "command.memory.session.forget.user",
        "command.memory.session.promote.user",
        "command.memory.session.reject.user",
        "command.delegation.cancel.user",
        "command.delegation.disposition.user",
        "command.delegation.mailbox.send.user",
        "command.process.control.user",
        "command.run.interrupt.user",
        "command.run.cancel.user",
        "command.run.mode.select.user",
        "command.attachment.upload.begin.user",
        "command.attachment.upload.chunk.user",
        "command.attachment.upload.finish.user",
        "command.external.memory.consent.user",
        "command.extensions.package.user",
        "query.humanActionScope",
        "query.processSessions",
        "query.worktreePreview",
    ];

    public static IReadOnlyList<NativeQueryDescriptor> NativeQueries { get; } =
    [
        new("native.catalog", "query.nativeCatalog", "Client API metadata", "connection", "pure", true),
        new("commandOutcome", "query.commandOutcome", "Metadata about the latest prompt expansion or workflow request", "session", "session-read", true),
        new("command.catalog", "query.commandCatalog", "Current canonical command catalog and invocation compare-and-set scope", "trusted-session", "session-read", false),
        new("command.recovery", "query.commandRecovery", "Bounded status of durable native command invocations", "trusted-session", "session-read", false),
        new("workflow.recovery", "query.workflowRecovery", "Durable compiled-workflow stage status and explicit continuation eligibility", "trusted-session", "session-read", false),
        new("process.sessions", "query.processSessions", "Passive, bounded process handles owned by the current Session and Run", "trusted-session", "session-read", false),
        new("process.output", "query.processSessions", "Passive, bounded output already retained for an exact owned process handle", "trusted-session", "session-read", false),
        new("worktree.preview", "query.worktreePreview", "Passive 3-way Git integration proposal for one exact Lane ID in the current Session and Run; returns its apply ownership token", "trusted-session", "session-read", false),
        new("state", "query.state", "Current runtime state", "session", "session-read", true),
        new("sessionIdentity", "query.sessionIdentity", "Current Session and Run identity", "session", "session-read", true),
        new("sessionSecurity", "query.sessionSecurity", "Durable Session security ceiling and revision", "session", "session-read", true),
        new("sessionSecurityDefault", "query.sessionSecurityDefault", "Configured default ceiling for new Sessions", "user", "configuration-read", true),
        new("models.catalog", "query.modelsCatalog", "Configured model identities and supported routes; no provider discovery", "workspace", "configuration-read", true),
        new("session.model.selection", "query.sessionModelSelection", "Current Session model binding and compare-and-set revisions", "trusted-session", "session-read", false),
        new("interaction.recovery", "query.interactionRecovery", "Explicitly recoverable accepted native choices", "trusted-session", "session-read", false),
        new("workingState", "query.workingState", "Current plan and working state", "session", "session-read", true),
        new("commands", "query.commands", "Effective human command catalog", "session", "workspace-read", true),
        new("sessionSidebar", "query.sessionSidebar", "Session sidebar and persisted artifacts", "session", "session-read", true),
        new("conversations", "query.conversations", "Sessions in the current workspace", "workspace", "workspace-read", true),
        new("changedFiles", "query.changedFiles", "Changed files and artifact references", "session", "session-read", true),
        new("agents", "query.agents", "Agent lanes and delegated tasks", "session", "session-read", true),
        new("agent.sources", "query.agentSources", "External agent instruction and memory sources", "workspace", "workspace-read", true),
        new("sessionObservability", "query.sessionObservability", "Session activity and measured usage; cursor is bounded", "session", "session-read", true),
        new("humanActionScope", "query.humanActionScope", "Current trusted-action scope on this native connection", "trusted-session", "session-read", false),
        new("tools", "query.toolCatalog", "Effective tool catalog and policy decisions", "session", "workspace-read", true),
        new("extensions", "query.extensionCatalog", "Installed extension inventory and consent metadata", "workspace", "workspace-read", true),
        new("extensions.packages", "query.extensionPlatform", "Local package lifecycle and exact consent", "user", "configuration-read", true),
        new("extensions.views", "query.extensionPlatform", "Available declarative contributions; no activation", "workspace", "workspace-read", true),
        new("extensions.configuration", "query.extensionPlatform", "Declared extension-owned configuration in the selected scope", "workspace", "configuration-read", true),
        new("extensions.context.catalog", "query.extensionPlatform", "Previewable context contributions; never activates code", "workspace", "configuration-read", true),
        new("extensions.context", "query.extensionPlatform", "Exact human-previewed data for the active Session and Run", "session", "configuration-read", false),
        new("extensions.notifications", "query.extensionPlatform", "Bounded UI-only notices; never model context", "session", "extension-read", false),
        new("extensions.view", "query.extensionPlatform", "Explicit extension view snapshot; may activate consented code", "session", "extension-read", false),
        new("mcp", "query.mcpConfiguration", "Configured MCP endpoints and trust metadata; does not probe endpoints", "workspace", "workspace-read", true),
        new("hooks", "query.hookCatalog", "Hook inventory and effective state", "workspace", "workspace-read", true),
        new("skills", "query.skillCatalog", "Skill inventory and activation metadata", "session", "workspace-read", true),
        new("memory.session", "query.sessionMemory", "Current Session memory and candidates", "session", "session-read", true),
        new("modePreference", "query.modePreferences", "User default mode preference", "user", "configuration-read", true),
        new("modeAuthority", "query.modePreferences", "Current mode authority and active scope", "session", "session-read", true),
        new("reasoningPreference", "query.reasoningPreferences", "User reasoning preference", "user", "configuration-read", true),
        new("runReasoningPreference", "query.reasoningPreferences", "Current Run reasoning selection", "session", "session-read", true),
        new("workspaceStatus", "query.workspaceStatus", "Workspace identity, trust and runtime status", "workspace", "workspace-read", true),
        new("workspace.panel", "query.workspacePanel", "Bounded files, Git, timeline and checkpoint read models", "workspace", "workspace-read", true),
        new("context", "query.turnContext", "Last persisted model context snapshot", "session", "session-read", true),
        new("turnFingerprint", "query.turnContext", "Effective current-turn capability fingerprint", "session", "session-read", true),
        new("openrouter", "query.openRouterStatus", "OpenRouter connection status", "user", "configuration-read", true),
        new("ui.locale", "query.uiPreferences", "Effective interface locale preference", "user", "configuration-read", true),
        new("sidebarSettings", "query.uiPreferences", "Sidebar preferences", "user", "configuration-read", true),
    ];

    public static bool IsSupportedQuery(string name) => name == "humanActionScope"
        ? true
        : NativeQueries.Any(query => query.Name == name || query.WireName == name)
            || name.StartsWith("sessionObservability:", StringComparison.Ordinal)
            || name.StartsWith("workspace.panel:", StringComparison.Ordinal)
            || name.StartsWith("extensions.view:", StringComparison.Ordinal)
            || name.StartsWith("extensions.configuration:", StringComparison.Ordinal)
            || name.StartsWith("extensions.notifications:", StringComparison.Ordinal)
            || name.StartsWith("process.sessions:", StringComparison.Ordinal)
            || name.StartsWith("process.output:", StringComparison.Ordinal)
            || name.StartsWith("worktree.preview:", StringComparison.Ordinal);

    public static bool TryGetQuery(string name, out NativeQueryDescriptor descriptor)
    {
        descriptor = NativeQueries.FirstOrDefault(query => query.Name == name || query.WireName == name
            || query.Name == "sessionObservability" && name.StartsWith("sessionObservability:", StringComparison.Ordinal)
            || query.Name == "workspace.panel" && name.StartsWith("workspace.panel:", StringComparison.Ordinal)
            || query.Name == "extensions.view" && name.StartsWith("extensions.view:", StringComparison.Ordinal)
            || query.Name == "extensions.configuration" && name.StartsWith("extensions.configuration:", StringComparison.Ordinal)
            || query.Name == "extensions.notifications" && name.StartsWith("extensions.notifications:", StringComparison.Ordinal)
            || query.Name == "process.sessions" && name.StartsWith("process.sessions:", StringComparison.Ordinal)
            || query.Name == "process.output" && name.StartsWith("process.output:", StringComparison.Ordinal)
            || query.Name == "worktree.preview" && name.StartsWith("worktree.preview:", StringComparison.Ordinal))!;
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
            + ",\"wireName\":" + Q(command.WireName ?? command.Name)
            + (command.Variant is null ? string.Empty : ",\"variant\":" + Q(command.Variant))
            + ",\"capability\":" + Q(command.Capability) + ",\"authority\":" + Q(command.Authority)
            + ",\"receipt\":" + Q(command.Receipt) + ",\"summary\":" + Q(command.Summary)
            + ",\"availability\":" + Q(capabilities.Contains(command.Capability) ? "available" : "unsupported") + "}"));
        var streams = "{\"id\":\"events.stream.v1\",\"capability\":\"events.stream.v1\",\"ackAfterConsume\":true,\"boundedWindow\":true,\"availability\":"
            + Q(capabilities.Contains("events.stream.v1") ? "available" : "unsupported") + "},"
            + "{\"id\":\"events.replay.page\",\"capability\":\"events.replay.page\",\"highWaterPinned\":true,\"cursorBounded\":true,\"availability\":"
            + Q(capabilities.Contains("events.replay.page") ? "available" : "unsupported") + "}";
        var artifacts = "{\"id\":\"artifact.read.chunk\",\"capability\":\"artifact.read.chunk\",\"boundedBytes\":true,\"availability\":"
            + Q(capabilities.Contains("artifact.read.chunk") ? "available" : "unsupported") + "}";
        var features = "[{\"id\":\"conversation.admission.session-security.v1\",\"availability\":"
            + Q(capabilities.Contains("conversation.admission.session-security.v1") ? "available" : "unsupported") + "}]";
        return "{\"apiVersion\":1,\"protocolVersion\":\"1.0\",\"queries\":[" + queries
            + "],\"commands\":[" + commands + "],\"eventApis\":[" + streams + "],\"artifactApis\":[" + artifacts + "]"
            + ",\"features\":" + features
            + ",\"excluded\":[{\"id\":\"diff.open\",\"availability\":\"not_exposed\",\"reason\":\"Legacy artifact diffs are not exposed; workspace.panel provides bounded workspace and checkpoint diffs.\"},"
            + "{\"id\":\"complete\",\"availability\":\"not_exposed\",\"reason\":\"Filesystem completion can read arbitrary paths and has no native scoped request DTO yet.\"},"
            + "{\"id\":\"humanActionScope\",\"availability\":\"trusted_connection_only\",\"capability\":\"query.humanActionScope\"}]}";
    }

    public static bool IsSupportedCommand(string name) => name is "sim" or "sim.resume" or "session.input"
        or "conversation.start" or "conversation.new" or "conversation.resume" or "conversation.rename" or "conversation.compact" or "conversation.alternative"
        or "session.security.set" or "session.security.default.set" or "session.model.select" or "session.model.route.respond"
        or "skills.activate" or "skills.release" or "memory.session.add" or "memory.session.propose"
        or "memory.session.forget" or "memory.session.promote" or "memory.session.reject"
        or "run.interrupt" or "run.cancel" or "run.mode.select"
        or "delegation.cancel" or "delegation.disposition" or "delegation.mailbox.send"
        or "attachment.upload.begin" or "attachment.upload.chunk" or "attachment.upload.finish"
        or "external.memory.consent" or "interaction.respond" or "interaction.continue"
        or "command.invoke.user" or "command.continue" or "workflow.continue" or "process.control" or "extensions.package";

    public static IReadOnlyList<NativeCommandDescriptor> NativeCommands { get; } =
    [
        new("extensions.package", "command.extensions.package.user", "native-confirmation+hmac+scope+package-hashes", "command.result.lookup", "Manage local extensions with explicit user consent; install never executes code"),
        new("sim", "command.sim", "generic", "command.result.lookup", "Submit a deterministic simulation scenario"),
        new("sim.resume", "command.sim.resume", "generic", "command.result.lookup", "Resume a deterministic simulation"),
        new("session.input", "command.session.input", "generic", "command.result.lookup", "Persist literal user input without inference"),
        new("conversation.start", "command.conversation.start.user", "native-confirmation+hmac+session-scope+security-revision-v1", "conversation.result.lookup", "Persist input and invoke the Core conversation pipeline"),
        new("conversation.new", "command.conversation.new.user", "native-confirmation+hmac+connection-scope+receipt", "command.result.lookup", "Create a blank Session with the configured security ceiling"),
        new("conversation.resume", "command.conversation.resume.user", "native-confirmation+hmac+workspace-session-scope+lease+receipt", "command.result.lookup", "Resume a Session in the current workspace"),
        new("conversation.rename", "command.conversation.rename.user", "native-confirmation+hmac+session-sequence-cas+receipt", "command.result.lookup", "Set the current Session title with compare-and-set"),
        new("conversation.compact", "command.conversation.compact.user", "native-confirmation+hmac+session-scope+receipt", "command.result.lookup", "Compact the current Session context while retaining history"),
        new("conversation.alternative", "command.conversation.alternative.user", "native-confirmation+hmac+session-scope+receipt", "command.result.lookup", "Create an isolated worktree and derived Session from an exact recoverable checkpoint"),
        new("interaction.respond", "command.conversation.questionnaire.respond.user", "native-confirmation+hmac+session-run-interaction-scope+policy-revision+receipt+resume", "command.result.lookup", "Answer a pending questionnaire and continue its existing Run")
            { WireName = "interaction.respond", Variant = "questionnaire" },
        new("interaction.respond.choice", "command.conversation.interaction.choice.respond.user", "native-confirmation+hmac+session-run-interaction-option+policy-revision+receipt+resume", "command.result.lookup", "Choose a server-defined option for a pending Permission, PlanApproval, or Session memory promotion and continue its existing Run")
            { WireName = "interaction.respond", Variant = "choice" },
        new("interaction.respond.weak-sandbox", "command.conversation.weak-sandbox.respond.user", "native-confirmation+hmac+session-run-task-lane-turn-toolcall-invocation+policy-revision+receipt+resume", "command.result.lookup", "Choose once or deny a proven pre-launch Weak sandbox fallback for the exact workflow verifier child call")
            { WireName = "interaction.respond", Variant = "weakSandboxConsent" },
        new("interaction.continue", "command.conversation.interaction.continue.user", "native-confirmation+hmac+session-run-interaction-policy-revision+accepted-receipt+explicit-resume", "command.result.lookup", "Explicitly resume an accepted choice whose continuation was not started") ,
        new("command.invoke.user", "command.invoke.user", "native-confirmation+hmac+session-command-catalog+source-fingerprint+model-routing-policy-cas+durable-admission", "command.result.lookup", "Invoke an exact Host-cataloged command in the trusted conversation pipeline"),
        new("command.continue", "command.invoke.continue.user", "native-confirmation+hmac+admitted-command+scope-cas+receipt+explicit-resume", "command.result.lookup", "Explicitly continue a durably admitted command that has not started"),
        new("workflow.continue", "command.workflow.continue.user", "native-confirmation+hmac+workflow-invocation+stage-journal+mode-policy-cas+explicit-supervision", "command.result.lookup", "Explicitly supervise a pending durable compiled workflow stage"),
        new("session.security.set", "command.session.security.set.user", "native-confirmation+hmac+session-revision", "command.result.lookup", "Set a revision-checked Session capability ceiling"),
        new("session.security.default.set", "command.session.security.default.set.user", "native-confirmation+hmac+user-revision", "command.result.lookup", "Set the configured security ceiling for new Sessions"),
        new("session.model.select", "command.session.model.select.user", "native-confirmation+hmac+session+catalog+selection+routing+security-revision", "command.result.lookup", "Select an exact configured model route for the current Session"),
        new("session.model.route.respond", "command.session.model.route.respond.user", "native-confirmation+hmac+session-model-route-consent+revision+receipt", "command.result.lookup", "Accept or reject the exact pending Session model route consent"),
        new("skills.activate", "command.skills.activate.user", "native-confirmation+hmac+session+activation-revision+manifest-package-cas+receipt", "command.result.lookup", "Activate the exact currently trusted skill package for this Session"),
        new("skills.release", "command.skills.release.user", "native-confirmation+hmac+session+activation-revision+durable-activation-cas+receipt", "command.result.lookup", "Release this Session's exact durable skill activation, including missing or changed sources"),
        new("memory.session.add", "command.memory.session.add.user", "native-confirmation+hmac+session+memory-revision+receipt", "command.result.lookup", "Add a user-authored memory candidate to the current Session"),
        new("memory.session.propose", "command.memory.session.propose.user", "native-confirmation+hmac+session+memory-revision+receipt", "command.result.lookup", "Propose a user-authored memory candidate in the current Session"),
        new("memory.session.forget", "command.memory.session.forget.user", "native-confirmation+hmac+session+memory-revision+receipt", "command.result.lookup", "Forget one exact memory record from the current Session"),
        new("memory.session.promote", "command.memory.session.promote.user", "native-confirmation+hmac+session+memory-candidate+revision+interaction+receipt", "command.result.lookup", "Approve the canonical pending promotion for a Session memory candidate"),
        new("memory.session.reject", "command.memory.session.reject.user", "native-confirmation+hmac+session+memory-candidate+revision+interaction+receipt", "command.result.lookup", "Reject one canonical pending Session memory candidate"),
        new("delegation.cancel", "command.delegation.cancel.user", "native-confirmation+hmac+session-run-delegation-policy-revision+receipt", "command.result.lookup", "Request cancellation of one exact child delegation in the active Run"),
        new("delegation.disposition", "command.delegation.disposition.user", "native-confirmation+hmac+session-run-delegation-result-policy-revision+receipt", "command.result.lookup", "Accept or request rework for one exact returned child result"),
        new("delegation.mailbox.send", "command.delegation.mailbox.send.user", "native-confirmation+hmac+session-run-delegation-policy-revision+receipt", "command.result.lookup", "Send a bounded supervisor message to one exact active child delegation"),
        new("process.control", "command.process.control.user", "native-confirmation+hmac+session-run-policy-revision+tool-receipt", "command.result.lookup", "Run one scoped process read, input/EOF, or stop through the Host tool pipeline"),
        new("worktree.integrate", "command.invoke.user", "native-confirmation+hmac+session-run-policy-revision+tool-receipt+proposal-cas", "command.result.lookup", "Apply one exact previewed Lane worktree proposal through the existing tool permission and reconciliation pipeline")
        { WireName = "command.invoke.user", Variant = "canonicalCommandId=core:worktree.integrate; arguments=[toolName,argumentsJson]" },
        new("worktree.cleanup", "command.invoke.user", "native-confirmation+hmac+session-run-policy-revision+tool-receipt+terminal-lane-ownership", "command.result.lookup", "Retire one exact terminal Lane worktree through the existing tool permission pipeline")
        { WireName = "command.invoke.user", Variant = "canonicalCommandId=core:worktree.cleanup; arguments=[toolName,argumentsJson]" },
        new("worktree.discard", "command.invoke.user", "native-confirmation+hmac+session-run-policy-revision+tool-receipt+terminal-lane-ownership+retained-tree", "command.result.lookup", "Archive and discard one exact reviewed terminal Lane worktree through the existing tool permission pipeline")
        { WireName = "command.invoke.user", Variant = "canonicalCommandId=core:worktree.discard; arguments=[toolName,argumentsJson]" },
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
    public string DispatchName(string requestedName) => (Name == "sessionObservability"
        && requestedName.StartsWith("sessionObservability:", StringComparison.Ordinal)
        || Name == "workspace.panel" && requestedName.StartsWith("workspace.panel:", StringComparison.Ordinal)
        || Name == "extensions.view" && requestedName.StartsWith("extensions.view:", StringComparison.Ordinal)
        || Name == "extensions.configuration" && requestedName.StartsWith("extensions.configuration:", StringComparison.Ordinal)
        || Name == "extensions.notifications" && requestedName.StartsWith("extensions.notifications:", StringComparison.Ordinal)
        || Name == "process.sessions" && requestedName.StartsWith("process.sessions:", StringComparison.Ordinal)
        || Name == "process.output" && requestedName.StartsWith("process.output:", StringComparison.Ordinal)
        || Name == "worktree.preview" && requestedName.StartsWith("worktree.preview:", StringComparison.Ordinal))
        ? requestedName : WireName;
}

/// <summary>Metadata for a Host command; authority remains enforced by Host policy and proof checks.</summary>
public sealed record NativeCommandDescriptor(string Name, string Capability, string Authority,
    string Receipt, string Summary)
{
    public string? WireName { get; init; }
    public string? Variant { get; init; }
}
