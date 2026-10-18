using System.Text.Json;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class CliEndToEndTests
{
    [Theory]
    [InlineData("chat", 2048, "high", null, "capability")]
    [InlineData("responses", 2048, "high", "high", null)]
    [InlineData("anthropic", 2048, "budget", "budget", null)]
    [InlineData("anthropic", 1100, "budget", null, "output-reserve")]
    public async Task Automatic_ultracode_reasoning_follows_the_configured_protocol(string protocol,
        int outputLimit, string expectedRequested, string? expectedApplied, string? expectedReduction)
    {
        await InIsolatedCli(async (workspace, config, _, provider) =>
        {
            ConfigureReasoningProtocol(config, provider.BaseUrl, protocol, outputLimit);
            var bodies = new List<string>();
            provider.RespondWith((_, body) =>
            {
                bodies.Add(body);
                return protocol == "anthropic"
                    ? AnthropicTextResponse("respuesta local")
                    : protocol == "responses"
                        ? ResponsesTextResponse("respuesta local")
                        : TextResponse("respuesta local");
            });

            using var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            var setupDiagnostics = new List<string>();
            var setupCode = await host.ExecuteAsync("turno inicial", setupDiagnostics.Add,
                TestContext.Current.CancellationToken);
            Assert.True(setupCode == 0, "Setup turn failed: " + string.Join(" | ", setupDiagnostics));
            SelectUltraCodeAct(runtime);

            var diagnostics = new List<string>();
            Assert.Equal(0, await host.ExecuteAsync("turno con UltraCode", diagnostics.Add,
                TestContext.Current.CancellationToken));
            Assert.Equal(2, provider.RequestCount);

            var events = ReadCurrentSessionEvents(workspace);
            var startedTurn = events.OfType<TurnStarted>().Last();
            var resolution = Assert.IsType<ReasoningResolution>(startedTurn.ReasoningResolution);
            Assert.Equal(ReasoningSelectionSource.UltraCode, resolution.Source);
            Assert.Equal(new ReasoningRequest(expectedRequested, expectedRequested == "budget" ? 1024 : null),
                resolution.RequestedRequest);
            Assert.Equal(expectedApplied is null ? null
                : new ReasoningRequest(expectedApplied, expectedApplied == "budget" ? 1024 : null),
                resolution.AppliedRequest);
            Assert.Equal(expectedReduction switch
            {
                "capability" => new[] { ReasoningReduction.Capability },
                "output-reserve" => new[] { ReasoningReduction.OutputReserve },
                _ => Array.Empty<ReasoningReduction>(),
            }, resolution.Reductions);

            var step = Assert.Single(events.OfType<ModelStepStarted>(), item => item.TurnId == startedTurn.TurnId);
            Assert.True(resolution.IsEquivalentTo(step.ReasoningResolution));
            using var request = JsonDocument.Parse(bodies[1]);
            var body = request.RootElement;
            if (expectedApplied is null)
            {
                Assert.False(body.TryGetProperty("reasoning", out var reasoningField));
                Assert.False(body.TryGetProperty("reasoning_effort", out var reasoningEffortField));
                Assert.Contains(diagnostics, line => line.Contains("host.reasoning.ultracode_unavailable",
                    StringComparison.Ordinal));
            }
            else if (protocol == "responses")
            {
                Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
            }
            else
            {
                var thinking = body.GetProperty("thinking");
                Assert.Equal("enabled", thinking.GetProperty("type").GetString());
                Assert.Equal(1024, thinking.GetProperty("budget_tokens").GetInt32());
            }
        });
    }

    [Theory]
    [InlineData("user-default")]
    [InlineData("turn-boost")]
    public async Task Explicit_chat_reasoning_preferences_remain_fail_closed(string selection)
    {
        await InIsolatedCli(async (workspace, config, _, provider) =>
        {
            ConfigureReasoningProtocol(config, provider.BaseUrl, "chat", 2048);
            provider.RespondWith((_, _) => TextResponse("setup complete"));
            using var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(runtime.Connect(
                TestContext.Current.CancellationToken));
            if (selection == "user-default")
            {
                var selected = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(),
                    "{\"cmd\":\"reasoning.default.set\",\"kind\":\"high\",\"expectedRevision\":0}"),
                    TestContext.Current.CancellationToken);
                Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);
                var diagnostics = new List<string>();
                Assert.Equal(1, await host.ExecuteAsync("explicit preference", diagnostics.Add,
                    TestContext.Current.CancellationToken));
                Assert.Contains(diagnostics, line => line.Contains("host.reasoning.route_conflict",
                    StringComparison.Ordinal));
                Assert.Equal(0, provider.RequestCount);
                return;
            }

            Assert.Equal(0, await host.ExecuteAsync("turno base", _ => { },
                TestContext.Current.CancellationToken));
            Assert.True(host.SetNextTurnReasoningBoost("high", null));
            var boostDiagnostics = new List<string>();
            Assert.Equal(1, await host.ExecuteAsync("turno con boost explícito", boostDiagnostics.Add,
                TestContext.Current.CancellationToken));
            Assert.Contains(boostDiagnostics, line => line.Contains("host.reasoning.route_conflict",
                StringComparison.Ordinal));
            Assert.Equal(1, provider.RequestCount);
        });
    }

    private static void ConfigureReasoningProtocol(string config, string baseUrl, string protocol, int outputLimit)
    {
        var family = protocol switch
        {
            "chat" => "OpenAiChatCompatible",
            "responses" => "OpenAIResponses",
            "anthropic" => "AnthropicMessages",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
        File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
            providers:
              scripted:
                family: {{family}}
                baseUrl: {{baseUrl}}
                auth: none
                billingMode: Local
            """);
        var reasoning = protocol switch
        {
            "anthropic" => "reasoning: { supported: true, effortLevels: [budget], ultraCodeBudgetTokens: 1024, ultraCodeOutputReserveTokens: 256 }",
            "responses" => "reasoning: { supported: true, effortLevels: [high] }",
            _ => "reasoning: { supported: true, effortLevels: [high] }",
        };
        File.WriteAllText(Path.Combine(config, "models.yaml"), $$"""
            models:
              scripted-model:
                provider: scripted
                context: 8192
                maxOutput: {{outputLimit}}
                {{reasoning}}
            """);
    }

    private static void SelectUltraCodeAct(OmniCliRuntime runtime)
    {
        var client = runtime.Connect(TestContext.Current.CancellationToken);
        var trusted = Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select",
            mode = "act",
            effort = "ultracode",
            adaptive = true,
            allowedModes = "plan,act,orq",
            maxAgents = 1,
            maxDepth = 1,
            maxTurns = 3,
            maxToolCalls = 8,
            maxElapsedSeconds = 90,
        })), TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
    }

    private static string AnthropicTextResponse(string text)
    {
        var encoded = JsonSerializer.Serialize(text);
        return string.Join("\n", new[]
        {
            "event: message_start", "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}", "",
            "event: content_block_start", "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}", "",
            "event: content_block_delta", "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + encoded + "}}", "",
            "event: content_block_stop", "data: {\"type\":\"content_block_stop\",\"index\":0}", "",
            "event: message_delta", "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}", "",
            "event: message_stop", "data: {\"type\":\"message_stop\"}", "",
        });
    }
}
