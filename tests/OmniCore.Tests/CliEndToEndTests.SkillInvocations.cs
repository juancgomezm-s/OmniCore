namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;
using Task = System.Threading.Tasks.Task;

public sealed partial class CliEndToEndTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_runtime_human_skill_name_without_slash_reaches_first_HTTP_request(bool conversational)
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            const string marker = "SKILL_FIRST_REQUEST_FIXTURE_B84A";
            var package = Path.Combine(workspace, ".omnicore", "skills", "fixture-invocation");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "skill.yaml"),
                "id: fixture-invocation\nversion: 1\nbudgetTokens: 128\nactivation:\n  explicit: true\n");
            File.WriteAllText(Path.Combine(package, "instructions.md"), marker);
            Assert.Equal(0, (await Run("trust")).Code);
            using var runtime = OmniCliRuntime.Create(workspace);
            var diagnostics = new List<string>();
            var token = TestContext.Current.CancellationToken;
            var first = conversational
                ? await runtime.ConversationAsync("Usa fixture-invocation para explicar este proyecto.", diagnostics.Add, token)
                : await runtime.AskAsync("Usa fixture-invocation para explicar este proyecto.", diagnostics.Add, token);
            Assert.True(first == 0, string.Join("\n", diagnostics));
            Assert.Equal(1, provider.RequestCount);
            using (var request = JsonDocument.Parse(Assert.Single(provider.RequestBodies).Body))
            {
                var body = string.Join("\n", request.RootElement.GetProperty("messages").EnumerateArray()
                    .Select(message => message.GetProperty("content").ToString()));
                Assert.Equal(1, body.Split(marker, StringSplitOptions.None).Length - 1);
            }
            Assert.Single(ReadCurrentSessionEvents(workspace).OfType<SkillActivationChanged>(),
                activation => activation.Activated && activation.SkillId == "fixture-invocation");
            var second = conversational
                ? await runtime.ConversationAsync("Repite fixture-invocation.", diagnostics.Add, token)
                : await runtime.AskAsync("Repite fixture-invocation.", diagnostics.Add, token);
            Assert.True(second == 0, string.Join("\n", diagnostics));
            Assert.Equal(2, provider.RequestCount);
            Assert.Single(ReadCurrentSessionEvents(workspace).OfType<SkillActivationChanged>(),
                activation => activation.Activated && activation.SkillId == "fixture-invocation");
        });
    }

    [Fact]
    public async Task Fresh_CLI_slash_skill_command_reaches_the_first_HTTP_request()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            const string marker = "SKILL_SLASH_FIRST_REQUEST_FIXTURE_F209";
            var package = Path.Combine(workspace, ".omnicore", "skills", "fixture-invocation");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "skill.yaml"),
                "id: fixture-invocation\nversion: 1\nbudgetTokens: 128\nactivation:\n  explicit: true\n");
            File.WriteAllText(Path.Combine(package, "instructions.md"), marker);
            Assert.Equal(0, (await Run("trust")).Code);
            var result = await Run("/skill-fixture-invocation", "Explica este proyecto");
            Assert.True(result.Code == 0, result.Output);
            Assert.Equal(1, provider.RequestCount);
            using var request = JsonDocument.Parse(Assert.Single(provider.RequestBodies).Body);
            var body = string.Join("\n", request.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("content").ToString()));
            Assert.Equal(1, body.Split(marker, StringSplitOptions.None).Length - 1);
            Assert.Single(ReadCurrentSessionEvents(workspace).OfType<SkillActivationChanged>(),
                activation => activation.Activated && activation.SkillId == "fixture-invocation");
        });
    }
    [Fact]
    public async Task A_second_CLI_slash_invocation_revalidates_a_changed_active_package()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            const string firstMarker = "SKILL_SLASH_OLD_PACKAGE_D10A";
            const string secondMarker = "SKILL_SLASH_NEW_PACKAGE_D10A";
            var package = Path.Combine(workspace, ".omnicore", "skills", "fixture-invocation");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "skill.yaml"),
                "id: fixture-invocation\nversion: 1\nbudgetTokens: 128\nactivation:\n  explicit: true\n");
            var instructionsPath = Path.Combine(package, "instructions.md");
            File.WriteAllText(instructionsPath, firstMarker);
            Assert.Equal(0, (await Run("trust")).Code);
            var first = await Run("/skill-fixture-invocation", "Explica este proyecto");
            Assert.True(first.Code == 0, first.Output);
            File.WriteAllText(instructionsPath, secondMarker);
            var second = await Run("/skill-fixture-invocation", "Explica de nuevo con instrucciones actuales");
            Assert.True(second.Code == 0, second.Output);
            Assert.Equal(2, provider.RequestCount);
            using var request = JsonDocument.Parse(provider.RequestBodies[1].Body);
            var body = string.Join("\n", request.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("content").ToString()));
            Assert.Equal(1, body.Split(secondMarker, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain(firstMarker, body, StringComparison.Ordinal);
            Assert.Equal(2, ReadCurrentSessionEvents(workspace).OfType<SkillActivationChanged>()
                .Count(activation => activation.Activated && activation.SkillId == "fixture-invocation"));
        });
    }
    [Fact]
    public async Task Runtime_ambiguous_skill_display_name_rejects_before_provider_dispatch()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            foreach (var id in new[] { "fixture-first", "fixture-second" })
            {
                var package = Path.Combine(workspace, ".omnicore", "skills", id);
                Directory.CreateDirectory(package);
                File.WriteAllText(Path.Combine(package, "skill.yaml"), "id: " + id
                    + "\nversion: 1\ndisplayName: Fixture review\nbudgetTokens: 128\nactivation:\n  explicit: true\n");
                File.WriteAllText(Path.Combine(package, "instructions.md"), "Bounded fixture instructions");
            }
            Assert.Equal(0, (await Run("trust")).Code);
            using var runtime = OmniCliRuntime.Create(workspace);
            var diagnostics = new List<string>();
            Assert.NotEqual(0, await runtime.ConversationAsync("Usa Fixture review.", diagnostics.Add,
                TestContext.Current.CancellationToken));
            Assert.Equal(0, provider.RequestCount);
            Assert.Contains(diagnostics, text => text.Contains("Ambiguous", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(ReadCurrentSessionEvents(workspace).OfType<SkillActivationChanged>());
        });
    }
}
