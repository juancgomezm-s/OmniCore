namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;
using Task = System.Threading.Tasks.Task;

public sealed partial class CliEndToEndTests
{
    [Fact]
    public async Task Agent_system_message_reaches_native_Messages_and_edits_apply_to_new_turns()
    {
        await InIsolatedCli(async (workspace, config, _, provider) =>
        {
            ConfigureReasoningProtocol(config, provider.BaseUrl, "anthropic", 2048);
            var file = Path.Combine(config, "providers.yaml");
            var original = File.ReadAllText(file);
            void WritePrompt(string prompt) => File.WriteAllText(file, original.TrimEnd() + "\n    agentSystemPrompt: " + JsonSerializer.Serialize(prompt) + "\n");
            const string first = "ROLE_FIXTURE_ALPHA: Respond with concise teaching examples.\nUse Spanish.";
            const string second = "ROLE_FIXTURE_BETA: Give a brief summary.";
            WritePrompt(first);
            provider.RespondWith((_, _) => AnthropicTextResponse("fixture answer"));
            using var runtime = OmniCliRuntime.Create(workspace);
            var token = TestContext.Current.CancellationToken;
            var diagnostics = new List<string>();
            Assert.True(await runtime.ConversationAsync("first fixture question", diagnostics.Add, token) == 0, string.Join("\n", diagnostics));
            var firstSnapshot = ReadCurrentSessionEvents(workspace).OfType<TurnStarted>().Last().InstructionSnapshot;
            Assert.NotNull(firstSnapshot);
            Assert.Contains(first, firstSnapshot.ResolvedInstruction);
            WritePrompt(second);
            Assert.Equal(0, await runtime.ConversationAsync("second fixture question", diagnostics.Add, token));
            File.WriteAllText(file, original);
            Assert.Equal(0, await runtime.ConversationAsync("third fixture question", diagnostics.Add, token));
            Assert.Equal(3, provider.RequestCount);
            var systems = provider.RequestBodies.Select(item =>
            {
                using var request = JsonDocument.Parse(item.Body);
                return string.Join("\n", request.RootElement.GetProperty("system").EnumerateArray()
                    .Select(block => block.GetProperty("text").GetString()));
            }).ToArray();
            Assert.Contains(first, systems[0]);
            Assert.DoesNotContain(second, systems[0]);
            Assert.Contains(second, systems[1]);
            Assert.DoesNotContain(first, systems[1]);
            foreach (var system in systems.Take(2))
            {
                Assert.True(system.IndexOf("You are OmniCore", StringComparison.Ordinal) < system.IndexOf("ROLE_FIXTURE_", StringComparison.Ordinal));
                Assert.Contains("Host permissions", system);
                Assert.Contains("read and prefer localized filesystem.patch", system);
            }
            Assert.DoesNotContain("ROLE_FIXTURE_", systems[2]);
            Assert.DoesNotContain("User-configured agent message:", systems[2]);
            Assert.Contains(first, firstSnapshot.ResolvedInstruction);
            Assert.Single(ReadCurrentSessionEvents(workspace).OfType<TurnStarted>(), item => item.InstructionSnapshot?.ResolvedInstruction == firstSnapshot.ResolvedInstruction);
        });
    }
}
