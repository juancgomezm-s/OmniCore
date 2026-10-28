namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Host;
using OmniCore.Protocol;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class WorkspaceNavigationRuntimeTests
{
    [Fact]
    public void Opening_another_workspace_keeps_the_original_session_and_uses_an_independent_host()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-workspace-navigation-" + Guid.NewGuid().ToString("N"));
        var originalData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var originalConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "first"));
            Directory.CreateDirectory(Path.Combine(root, "second"));
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            using var first = OmniCliRuntime.Create(Path.Combine(root, "first"));
            var firstClient = first.Connect(TestContext.Current.CancellationToken);
            var input = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"first conversation\"}");
            Assert.Equal("ok", firstClient.Send(input, TestContext.Current.CancellationToken).Status);
            var firstServer = Assert.IsType<OmniServer>(firstClient);
            var firstSession = firstServer.LastSessionId();
            Assert.NotNull(firstSession);
            using var second = first.OpenWorkspace(Path.Combine(root, "second"), TestContext.Current.CancellationToken);
            var secondClient = second.Connect(TestContext.Current.CancellationToken);
            Assert.NotSame(firstClient, secondClient);
            Assert.Equal("ok", secondClient.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"second conversation\"}"), TestContext.Current.CancellationToken).Status);
            Assert.NotEqual(firstSession, Assert.IsType<OmniServer>(secondClient).LastSessionId());
            Assert.Equal(firstSession, firstServer.LastSessionId());
            Assert.Equal(originalDirectory, Environment.CurrentDirectory);
            Assert.DoesNotContain(firstClient.SubscribeSince(0), envelope => envelope.PayloadJson.Contains("model_step.started", StringComparison.Ordinal));
            Assert.DoesNotContain(secondClient.SubscribeSince(0), envelope => envelope.PayloadJson.Contains("model_step.started", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", originalData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", originalConfig);
            SqliteConnection.ClearAllPools();
            var safeRoot = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), safeRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (Directory.Exists(safeRoot)) Directory.Delete(safeRoot, recursive: true);
        }
    }
}
