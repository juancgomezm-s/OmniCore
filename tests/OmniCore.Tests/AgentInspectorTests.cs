using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class AgentInspectorTests
{
    internal static AgentLaneSnapshot Lane() => new("lane", "task", "parent", "Check the imported work", "Completed", "Completed",
        "profile", 1, "execution", "owner", "Completed", "delegation", "Returned", "model", "context-event", false,
        ResultId: "result", ResultDisposition: "Accepted", ResultSummary: "All tests passed (agent claim)", ResultOutcome: "Succeeded",
        ValidationDebts: [], Heartbeat: new("Unknown", null, null, "journal"));
    private static AgentsSnapshot Snapshot() => new("session", "run", 12, false, [Lane()]);

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Inspector_resources_and_palette_action_resolve_without_fallback(string locale)
    {
        var text = new Localization(locale);
        foreach (var key in AgentInspectorResources.Keys)
        {
            var resolved = text.ResolveDetailed(key);
            Assert.False(resolved.UsedFallback); Assert.Equal(locale, resolved.ResourceLocale); Assert.NotEqual(key, resolved.Text);
        }
        var entry = Assert.Single(CommandPalette.Build(null, text, new([]), false), entry => entry.Target.Id == "agent.inspect");
        var service = new KeyBindingService([new("F9", entry.Target, Source: KeyBindingSource.User, SourceId: "User")]);
        Assert.Equal("F9", Assert.Single(CommandPalette.Bindings(entry, service, new([]))).Definition.Keys);
    }

    [Fact]
    public void Inspector_rejects_foreign_runs_and_sessions_stale_and_invalid_data_and_clears_the_previous_capture()
    {
        var model = new AgentInspectorProjection("session", "run"); var current = Snapshot();
        Assert.True(model.Apply(current, 12));
        Assert.False(model.Apply(current with { RunId = "other" })); Assert.Equal(AgentInspectorStatus.ScopeChanged, model.Status); Assert.Null(model.Snapshot);
        Assert.False(model.Apply(current with { SessionId = "other" })); Assert.Null(model.Snapshot);
        Assert.False(model.Apply(current with { BasedOnJournalSequence = 11 })); Assert.Equal(AgentInspectorStatus.Stale, model.Status);
        Assert.False(model.Apply(current, 13)); Assert.Equal(AgentInspectorStatus.Stale, model.Status);
        Assert.False(model.Apply(current with { Lanes = [Lane(), Lane()] })); Assert.Equal(AgentInspectorStatus.Invalid, model.Status);
        Assert.False(model.Apply(current with { ProjectionUnavailable = true })); Assert.Equal(AgentInspectorStatus.Unavailable, model.Status);
        Assert.False(model.Apply(null)); Assert.Equal(AgentInspectorStatus.Unavailable, model.Status);
        Assert.True(model.Apply(current with { BasedOnJournalSequence = 13 }));
        model.Invalidate(); Assert.Null(model.Snapshot);
        Assert.False(model.Apply(current)); Assert.Equal(AgentInspectorStatus.Stale, model.Status);
        var missing = new AgentInspectorProjection(null, null);
        Assert.False(missing.Apply(current)); Assert.Equal(AgentInspectorStatus.NoSession, missing.Status);
        var malformed = AgentsJson.Decode("{\"sessionId\":\"session\",\"runId\":\"run\",\"basedOnJournalSequence\":13}")!;
        Assert.False(model.Apply(malformed)); Assert.Equal(AgentInspectorStatus.Invalid, model.Status);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("task")]
    [InlineData("lane")]
    [InlineData("execution")]
    public void Conflicting_reference_ownership_is_not_displayed_as_selected_Execution_provenance(string field)
    {
        var entry = new AgentTranscriptEntry(10, "tool receipt", "success", "event", "run", "task", "lane", ExecutionId: "execution");
        entry = field switch { "run" => entry with { RunId = "foreign" }, "task" => entry with { TaskId = "foreign" },
            "lane" => entry with { LaneId = "foreign" }, _ => entry with { ExecutionId = "foreign" } };
        var model = new AgentInspectorProjection("session", "run");
        Assert.False(model.Apply(Snapshot() with { Lanes = [Lane() with { Transcript = [entry] }] }));
        Assert.Equal(AgentInspectorStatus.Invalid, model.Status); Assert.Null(model.Snapshot);
    }

    [Theory]
    [InlineData(false, false, 0, false)]
    [InlineData(true, false, 1, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, true, 1, true)]
    public void Agent_success_or_acceptance_never_substitutes_for_verified_integration(bool integration, bool validation, int receipts, bool verified)
    {
        var lane = Lane() with { IntegrationVerified = integration, IntegrationValidationPassed = validation, ToolBackedEvidenceCount = receipts };
        var text = new Localization("en"); var detail = AgentInspectorPresentation.Describe(Snapshot(), lane, text);
        Assert.Contains(text.Resolve("agentInspector.claim"), detail);
        Assert.Contains(lane.ResultSummary!, detail);
        Assert.Contains(text.Resolve(verified ? "agentInspector.verified" : "agentInspector.unverified"), detail);
        Assert.DoesNotContain("agentInspector.", detail);
    }

    [Fact]
    public void Debt_missing_checks_ambiguous_identity_and_unknown_legacy_data_do_not_become_verified()
    {
        var lane = Lane() with { IntegrationVerified = true, IntegrationValidationPassed = true, ToolBackedEvidenceCount = 2,
            ValidationDebts = [new("debt-id", "EndToEnd", ["npm run e2e", "Unicode 😀 check"])] };
        var text = new Localization("es");
        var detail = AgentInspectorPresentation.Describe(Snapshot(), lane, text);
        Assert.Contains("debt-id", detail); Assert.Contains("npm run e2e", detail); Assert.Contains("Unicode 😀 check", detail);
        Assert.Contains(text.Resolve("agentInspector.debt_pending"), detail); Assert.DoesNotContain(text.Resolve("agentInspector.verified"), detail);
        foreach (var uncertain in new[] { lane with { ValidationDebts = null }, lane with { ExecutionAmbiguous = true, ValidationDebts = [] },
            lane with { ExecutionId = null, ValidationDebts = [] } })
            Assert.DoesNotContain(text.Resolve("agentInspector.verified"), AgentInspectorPresentation.Describe(Snapshot(), uncertain, text));
    }

    [Fact]
    public void Provenance_and_supervision_are_localized_and_ids_and_missing_data_are_preserved()
    {
        var lane = Lane() with { SupervisionState = "Failed · Wait (subject blocked; joins unresolved)",
            Transcript = [new(11, "tool receipt", "succeeded", "event-id", "run", "task", "lane", ExecutionId: "execution", ToolCallId: "call-id")] };
        var text = new Localization("fr"); var detail = AgentInspectorPresentation.Describe(Snapshot(), lane, text);
        Assert.Contains(text.Resolve("agentInspector.supervision." + lane.SupervisionState), detail);
        Assert.Contains("event-id", detail); Assert.Contains("call-id", detail); Assert.Contains("execution", detail);
        Assert.Contains(text.Resolve("agentInspector.unknown"), detail); // No capacity snapshot is unknown, not zero.
    }

    [Fact]
    public void Search_and_detail_limits_are_explicit_and_do_not_modify_the_source()
    {
        var model = new AgentInspectorProjection("session", "run");
        var lanes = Enumerable.Range(0, 513).Select(index => Lane() with { LaneId = "lane-" + index }).ToArray();
        Assert.True(model.Apply(Snapshot() with { Lanes = lanes }));
        Assert.Equal(512, model.Search("").Count); Assert.Single(model.Search("lane-511")); Assert.Empty(model.Search("lane-512"));
        Assert.Empty(model.Search(new string('x', 257)));
        var original = new string('x', 4000) + "😀";
        var lane = Lane() with { Objective = original, ValidationDebts = Enumerable.Range(0, 65).Select(index => new AgentValidationDebtSnapshot(
            "debt-" + index, "Local", Enumerable.Repeat(original, 33).ToArray())).ToArray() };
        var text = new Localization("en"); var detail = AgentInspectorPresentation.Describe(Snapshot(), lane, text);
        Assert.True(detail.Length < 33000); Assert.Contains(text.Resolve("agentInspector.detail_limited"), detail); Assert.Equal(original, lane.Objective);
    }
}
