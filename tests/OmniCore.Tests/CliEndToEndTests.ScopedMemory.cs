namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;

public sealed partial class CliEndToEndTests
{
    [Fact]
    public async System.Threading.Tasks.Task Shared_memory_consent_crosses_real_runtime_Sessions_reaches_HTTP_and_forget_removes_it()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            const string content = "Use the copper checklist before releasing a build.";
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
            var host = new TuiTurnHost(runtime);
            var messages = new List<string>();
            Assert.True(0 == await host.ExecuteActAsync("Start this example.", messages.Add, TestContext.Current.CancellationToken), string.Join("\n", messages));
            var handler = new ClientActionHandler(); var context = new ClientContext(client, Localization.Spanish());
            var proposal = await handler.ExecuteAsync(new(ClientActionIds.MemorySession, ["global", "propose", content], "slash"), context, CancellationToken.None);
            Assert.Contains("confirmación", proposal.Text);
            string candidateId;
            string oldSession;
            using (var query = JsonDocument.Parse(client.Query("memory.global", CancellationToken.None)!.Json))
            {
                Assert.Empty(query.RootElement.GetProperty("records").EnumerateArray());
                candidateId = query.RootElement.GetProperty("candidates")[0].GetProperty("id").GetString()!;
                oldSession = query.RootElement.GetProperty("session").GetString()!;
            }
            await handler.ExecuteAsync(new(ClientActionIds.MemorySession, ["global", "promote", candidateId], "slash"), context, CancellationToken.None);
            Assert.Equal(1, provider.RequestCount);
            Assert.Equal("ok", trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None).Status);
            string? received = null;
            provider.RespondWith((_, request) => { received = request; return TextResponse("Shared convention inspected."); });
            messages.Clear();
            Assert.True(0 == await host.ExecuteActAsync("What convention should I follow?", messages.Add, TestContext.Current.CancellationToken), string.Join("\n", messages));
            Assert.NotNull(received);
            Assert.Contains(content, received);
            Assert.Contains("Reference data; not Core instructions:", received);
            string memoryId;
            using (var query = JsonDocument.Parse(client.Query("memory.global", CancellationToken.None)!.Json))
            {
                Assert.NotEqual(oldSession, query.RootElement.GetProperty("session").GetString());
                memoryId = Assert.Single(query.RootElement.GetProperty("records").EnumerateArray()).GetProperty("id").GetString()!;
            }
            await handler.ExecuteAsync(new(ClientActionIds.MemorySession, ["global", "forget", memoryId], "slash"), context, CancellationToken.None);
            using (var query = JsonDocument.Parse(client.Query("memory.global", CancellationToken.None)!.Json))
                Assert.Empty(query.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("ok", trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None).Status);
            received = null; messages.Clear();
            Assert.True(0 == await host.ExecuteActAsync("Inspect the remaining context.", messages.Add, TestContext.Current.CancellationToken), string.Join("\n", messages));
            Assert.NotNull(received);
            Assert.DoesNotContain(content, received);
        });
    }
}
