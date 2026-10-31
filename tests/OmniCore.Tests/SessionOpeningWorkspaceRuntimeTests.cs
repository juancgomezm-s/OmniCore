namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Cli;
using OmniCore.Host;
using OmniCore.Protocol;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class SessionOpeningWorkspaceRuntimeTests
{
    [Fact]
    public void SessionOpening_switch_and_reopen_keep_two_sessions_per_directory_and_recover_without_Tui_selection()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-session-opening-workspaces-" + Guid.NewGuid().ToString("N"));
        var priorData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var priorConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var priorDirectory = Environment.CurrentDirectory;
        string[] agentHomeVariables = ["OMNICORE_AGENT_HOME", "CODEX_HOME", "CLAUDE_CONFIG_DIR", "OPENCODE_CONFIG_DIR", "XDG_CONFIG_HOME"];
        var priorAgentHomes = agentHomeVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        OmniCliRuntime? first = null;
        try
        {
            var firstDirectory = Path.Combine(root, "first");
            var secondDirectory = Path.Combine(root, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            foreach (var variable in agentHomeVariables)
            {
                var isolatedHome = Path.Combine(root, "isolated-agent-home", variable);
                Directory.CreateDirectory(isolatedHome);
                Environment.SetEnvironmentVariable(variable, isolatedHome);
            }
            first = OmniCliRuntime.Create(firstDirectory);
            var firstServer = Assert.IsType<OmniServer>(first.Connect(TestContext.Current.CancellationToken));
            Assert.Equal("ok", SessionCommand(firstServer, "conversation.new").Status);
            var firstSession = firstServer.LastSessionId()!;
            Assert.Equal("ok", SessionCommand(firstServer, "conversation.new").Status);
            var secondSession = firstServer.LastSessionId()!;
            Assert.NotEqual(firstSession, secondSession);
            var firstRows = ConversationJson.Decode(firstServer.Query("conversations", TestContext.Current.CancellationToken)!.Json)!.Conversations;
            Assert.Equal(2, firstRows.Count);
            var originalSequence = firstServer.AcquireStore().CurrentSequence(secondSession);

            using var second = first.OpenWorkspace(secondDirectory, TestContext.Current.CancellationToken);
            var secondServer = Assert.IsType<OmniServer>(second.Connect(TestContext.Current.CancellationToken));
            Assert.Empty(ConversationJson.Decode(secondServer.Query("conversations", TestContext.Current.CancellationToken)!.Json)!.Conversations);
            Assert.Equal("ok", SessionCommand(secondServer, "conversation.new").Status);
            var otherDirectorySession = secondServer.LastSessionId()!;
            Assert.NotEqual(firstSession, otherDirectorySession);
            Assert.NotEqual(secondSession, otherDirectorySession);
            Assert.Equal(secondSession, firstServer.LastSessionId());
            Assert.Equal(originalSequence, firstServer.AcquireStore().CurrentSequence(secondSession));
            Assert.Equal(priorDirectory, Environment.CurrentDirectory);

            // Release the former writer as the CLI navigation loop does before a later re-open.
            first.Dispose(); first = null;
            using (var reopened = second.OpenWorkspace(firstDirectory, TestContext.Current.CancellationToken))
            {
                var recovered = Assert.IsType<OmniServer>(reopened.Connect(TestContext.Current.CancellationToken));
                Assert.Equal(secondSession, recovered.LastSessionId());
                Assert.Equal(originalSequence, recovered.AcquireStore().CurrentSequence(secondSession));
                Assert.Equal(2, ConversationJson.Decode(recovered.Query("conversations", TestContext.Current.CancellationToken)!.Json)!.Conversations.Count);
                using var policies = ModelPolicyHost.Create(Path.Combine(root, "policies"), Array.Empty<ModelRegistryModelDescriptor>());
                var app = new TuiApp(recovered, policies: policies,
                    animationSettingsPath: Path.Combine(root, "presentation", "animation.json"),
                    keyBindingsConfigDirectory: Path.Combine(root, "keyboard"), selectConversationOnOpen: true);
                Assert.True(app.ConversationSelectionPending);
                Assert.Empty(app.ProjectionState.Conversation.Blocks);
                Assert.Equal(secondSession, recovered.LastSessionId());
                Assert.Equal(originalSequence, recovered.AcquireStore().CurrentSequence(secondSession));
                Assert.Equal(otherDirectorySession, secondServer.LastSessionId());
                Assert.Equal("ok", SessionCommand(recovered, "conversation.resume", firstSession.ToString()).Status);
                Assert.Equal(firstSession, recovered.LastSessionId());
                Assert.DoesNotContain(recovered.SubscribeSince(0), envelope => envelope.PayloadJson.Contains("model_step.started", StringComparison.Ordinal));
            }
            using (var reopenedAgain = second.OpenWorkspace(firstDirectory, TestContext.Current.CancellationToken))
            {
                var recovered = Assert.IsType<OmniServer>(reopenedAgain.Connect(TestContext.Current.CancellationToken));
                Assert.Equal(firstSession, recovered.LastSessionId());
                Assert.Equal(2, ConversationJson.Decode(recovered.Query("conversations", TestContext.Current.CancellationToken)!.Json)!.Conversations.Count);
            }
            Assert.Equal(otherDirectorySession, secondServer.LastSessionId());
            Assert.Single(ConversationJson.Decode(secondServer.Query("conversations", TestContext.Current.CancellationToken)!.Json)!.Conversations);
        }
        finally
        {
            first?.Dispose();
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", priorData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", priorConfig);
            foreach (var variable in agentHomeVariables)
                Environment.SetEnvironmentVariable(variable, priorAgentHomes[variable]);
            SqliteConnection.ClearAllPools();
            var safeRoot = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), safeRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (Directory.Exists(safeRoot)) Directory.Delete(safeRoot, recursive: true);
        }
    }

    private static CommandAck SessionCommand(OmniServer server, string name, string? session = null) => server.SendUserAction(
        WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", name)
            + (session is null ? "" : "," + JsonObj.Field("sessionId", session)) + "}"), TestContext.Current.CancellationToken);
}
