using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

namespace OmniCore.Tests;

public sealed class M2ComponentTests
{
    // ---- HarnessPolicyResolver (ADR-0007 §3) ----
    [Fact]
    public async System.Threading.Tasks.Task HarnessPolicy_resolves_from_profile()
    {
        var profile = NewProfile("qwen", toolCall: 0.8, instruction: 0.7, plan: 0.8, repair: 0.7, multi: 0.7);
        var resolver = new HarnessPolicyResolver();

        var policy = resolver.Resolve(profile);

        Assert.Equal(OmniCore.Domain.ToolCallFormat.Native, policy.ToolCallFormat);
        Assert.Equal(OmniCore.Domain.ToolMode.Direct, policy.ToolMode);
        Assert.Equal(OmniCore.Domain.PlanControl.ModelDriven, policy.PlanControl);
        Assert.True(policy.RepairAttempts >= 3, "Buena recuperación → más reintentos");
    }

    [Fact]
    public async System.Threading.Tasks.Task Weak_profile_gets_guarded_harness()
    {
        var profile = NewProfile("pequeño", toolCall: 0.2, instruction: 0.3, plan: 0.2, repair: 0.2, multi: 0.2);
        var resolver = new HarnessPolicyResolver();

        var policy = resolver.Resolve(profile);

        Assert.Equal(OmniCore.Domain.ToolCallFormat.PromptedJson, policy.ToolCallFormat);
        Assert.Equal(OmniCore.Domain.PlanControl.RuntimeDriven, policy.PlanControl);
        Assert.True(policy.RepairAttempts < 2, "Poca recuperación → pocos reintentos");
    }

    // ---- ModeDefaultsPolicy (ADR-0037 §4) ----
    [Fact]
    public async System.Threading.Tasks.Task Autonomous_defaults_allow_act_writes_and_deny_plan()
    {
        var policy = OmniCore.Security.ModeDefaultsPolicy.Instance();

        Assert.Equal(OmniCore.Domain.PermissionDecision.Allow,
            policy.ForRunMode(OmniCore.Domain.RunMode.Act, OmniCore.Security.PermissionResource.WriteInsideWorkspace));
        Assert.Equal(OmniCore.Domain.PermissionDecision.Deny,
            policy.ForRunMode(OmniCore.Domain.RunMode.Plan, OmniCore.Security.PermissionResource.WriteInsideWorkspace));
        Assert.Equal(OmniCore.Domain.PermissionDecision.Allow,
            policy.ForRunMode(OmniCore.Domain.RunMode.Act, OmniCore.Security.PermissionResource.BuildTestProcess));
        Assert.Equal(OmniCore.Domain.PermissionDecision.Deny,
            policy.ForRunMode(OmniCore.Domain.RunMode.Plan, OmniCore.Security.PermissionResource.BuildTestProcess));
        Assert.Equal(OmniCore.Domain.PermissionDecision.Deny,
            policy.ForRunMode(OmniCore.Domain.RunMode.Act, OmniCore.Security.PermissionResource.SecretPaths));
    }

    // ---- OpenAiChatCompatibleProvider parse (ADR-0005/0011) ----
    [Fact]
    public async System.Threading.Tasks.Task ChatCompletion_parse_normalizes_text_and_tool_calls()
    {
        var json = "{" +
            "\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hola\",\"tool_calls\":" +
            "[{\"id\":\"call_1\",\"function\":{\"name\":\"filesystem.read\",\"arguments\":\"{}\"}}]},"
            + "\"finish_reason\":\"tool_calls\"}],"
            + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}";

        var response = OpenAiChatCompatibleProvider.ParseChatCompletion(json);

        Console.WriteLine("DEBUG content=" + response.Content.Count);
        Assert.Equal(2, response.Content.Count);
        Assert.True(response.Content[0] is OmniCore.Domain.TextBlock);
        Assert.True(response.Content[1] is OmniCore.Domain.ToolCallBlock call && call.ToolName == "filesystem.read");
        Assert.True(response.Content[1] is OmniCore.Domain.ToolCallBlock call2 && call2.ArgumentsJson == "{}");
        Assert.Equal(10, response.Usage.Input);
        Assert.Equal(5, response.Usage.Output);
    }

[Fact]
    public async System.Threading.Tasks.Task ChatCompletion_parse_empty_returns_end_turn()
    {
        var response = OpenAiChatCompatibleProvider.ParseChatCompletion("{\"choices\":[]}");
        Assert.Equal(OmniCore.Domain.StopReason.EndTurn, response.StopReason);
        Assert.Empty(response.Content);
    }

    [Fact]
    public async System.Threading.Tasks.Task ChatCompletion_parse_qwen_reasoning_and_length()
    {
        // Modelos Qwen exponen reasoning_content y finish_reason=length cuando se corta.
        var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"\","
            + "\"reasoning_content\":\"Pienso primero\"},\"finish_reason\":\"length\"}]}";

        var response = OpenAiChatCompatibleProvider.ParseChatCompletion(json);

        Assert.Equal(OmniCore.Domain.StopReason.MaxOutputTokens, response.StopReason);
        Assert.Single(response.Content);
        Assert.True(response.Content[0] is OmniCore.Domain.ReasoningBlock r
            && r.VisibleText == "Pienso primero");
    }

    private static OmniCore.Domain.EffectiveModelProfile NewProfile(string id, double toolCall, double instruction,
        double plan, double repair, double multi)
    {
        var traits = new Dictionary<string, double>
        {
            ["ToolCallReliability"] = toolCall,
            ["InstructionFollowing"] = instruction,
            ["PlanTrackingReliability"] = plan,
            ["ToolErrorRecovery"] = repair,
            ["MultiStepExecutionReliability"] = multi,
        };
        return new OmniCore.Domain.EffectiveModelProfile(id, 8192, 8192, 2048,
            ["text"], [OmniCore.Domain.ToolCallFormat.Native, OmniCore.Domain.ToolCallFormat.PromptedJson], true, traits);
    }
}