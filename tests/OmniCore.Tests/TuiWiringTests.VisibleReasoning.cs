namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Engine;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Reported_reasoning_is_visible_and_can_be_hidden_without_changing_the_answer() => RunTuiTest(fx =>
    {
        var reasoning = fx.Artifacts.PutText("{\"visibleContent\":{\"version\":1,\"blocks\":[{\"kind\":\"reasoning\",\"visibility\":\"Summarized\",\"text\":\"Public reasoning fixture\"}]}}",
            "application/vnd.omnicore.model-usage+json", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
        var answer = fx.Artifacts.PutText("Final answer fixture", "text/plain", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
        var stream = new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        stream.Append(new ModelStepCompleted(TurnId.New(), 0, new(0, 0, 0, 0, 0), StopReason.EndTurn,
            reasoning, "2026-10-27", null));
        var lane = fx.Decoded<LaneCreated>().Last().LaneId;
        stream.Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, lane, TurnId.New(), answer));
        fx.StartTui();
        fx.Wait(() => fx.App.Conversation!.Text.ToString()!.Contains("Public reasoning fixture", StringComparison.Ordinal), "visible reasoning");
        Type(fx, "/think off");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => !fx.App.ShowReportedReasoning, "hide reasoning");
        Assert.DoesNotContain("Public reasoning fixture", fx.App.Conversation!.Text.ToString());
        Assert.Contains("Final answer fixture", fx.App.Conversation!.Text.ToString());
        Type(fx, "/think on");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.ShowReportedReasoning, "show reasoning again");
        Assert.Contains("Public reasoning fixture", fx.App.Conversation!.Text.ToString());
        Assert.Contains("Final answer fixture", fx.App.Conversation!.Text.ToString());
    });
}
