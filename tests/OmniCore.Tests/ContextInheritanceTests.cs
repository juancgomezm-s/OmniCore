using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Host;
using Fixture = OmniCore.Tests.LaneContextIsolationTests.Fixture;

namespace OmniCore.Tests;

public sealed class ContextInheritanceTests
{
    [Fact]
    public async System.Threading.Tasks.Task Selected_projection_reaches_real_child_request_without_importing_parent_transcript_or_authority()
    {
        using var fx = new Fixture();
        var child = fx.Child();
        var source = ParentSnapshot(fx);
        fx.Reopen();
        var before = fx.Store.CurrentSequence(fx.Session);
        var contributor = Service(fx).CreateSelectedProjection(fx.Session, fx.Run.RunId, fx.Run.RootLane,
            child.Lane, source, new ContextInheritancePolicy(new[] { "conversation-000000" }));
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session)); // selection is read-only
        ModelRequest? received = null;
        var explorer = fx.Explorer((request, _) => { received = request; return Response(); }, contributors: new[] { contributor });
        Assert.Equal(StopReason.EndTurn, explorer.Ask("child current intent", "child system", fx.Session,
            fx.Run.RunId, child.Lane, "fresh child state", TestContext.Current.CancellationToken).StopReason);
        Assert.Contains("selected principal fact", received!.Instructions);
        Assert.DoesNotContain("other parent input", received.Instructions);
        Assert.DoesNotContain("parent private working state", received.Instructions);
        Assert.DoesNotContain("parent system", received.Instructions);
        Assert.Contains("fresh child state", received.Instructions);
        Assert.Contains(received.Messages.SelectMany(message => message.Content).OfType<TextBlock>(), text => text.Text == "child current intent");
        Assert.DoesNotContain(received.Messages.SelectMany(message => message.Content).OfType<TextBlock>(), text => text.Text.Contains("principal"));
        var item = Assert.Single(await contributor.GetContextAsync(Request(fx, child.Task, child.Lane),
            TestContext.Current.CancellationToken));
        Assert.False(item.PreserveWhenTrimming);
        Assert.Equal(ContextPriority.Normal, item.Priority);
        Assert.Equal(RetentionPolicy.ConversationWindow, item.Retention);
        Assert.DoesNotContain(item.Provenance.Refs!, reference => reference.Contains(source.Hash.Value));
        Assert.Contains(item.Provenance.Refs!, reference => reference.StartsWith("source-event="));
    }

    [Fact]
    public async System.Threading.Tasks.Task Default_policy_inherits_nothing_and_explicit_ids_are_immutable_unique_and_required()
    {
        var ids = new[] { "conversation-000000" };
        var policy = new ContextInheritancePolicy(ids);
        ids[0] = "system-prompt";
        Assert.Equal("conversation-000000", Assert.Single(policy.SelectedItemIds));
        Assert.Throws<ArgumentException>(() => new ContextInheritancePolicy(new[] { "id", "id" }));
        Assert.Throws<ArgumentException>(() => new ContextInheritancePolicy(new[] { " " }));
        using var fx = new Fixture();
        var child = fx.Child();
        var contributor = Service(fx).CreateSelectedProjection(fx.Session, fx.Run.RunId, fx.Run.RootLane,
            child.Lane, ParentSnapshot(fx), ContextInheritancePolicy.Default);
        Assert.Empty(await contributor.GetContextAsync(Request(fx, child.Task, child.Lane),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("system-prompt")]
    [InlineData("working-state")]
    [InlineData("absent-item")]
    public void System_working_state_and_nonexistent_items_cannot_be_selected(string id)
    {
        using var fx = new Fixture();
        var child = fx.Child();
        var source = ParentSnapshot(fx);
        Assert.Throws<InvalidDataException>(() => Service(fx).CreateSelectedProjection(fx.Session, fx.Run.RunId,
            fx.Run.RootLane, child.Lane, source, new ContextInheritancePolicy(new[] { id })));
    }

    [Theory]
    [InlineData(ContextItemKind.Decision, ScopeLevel.Lane, ContributionCategory.Task, false)]
    [InlineData(ContextItemKind.Decision, ScopeLevel.Session, ContributionCategory.Task, true)]
    [InlineData(ContextItemKind.ToolResult, ScopeLevel.Run, ContributionCategory.ToolObservations, false)]
    [InlineData(ContextItemKind.Summary, ScopeLevel.Run, ContributionCategory.Checkpoint, false)]
    [InlineData(ContextItemKind.File, ScopeLevel.Workspace, ContributionCategory.File, false)]
    [InlineData(ContextItemKind.Memory, ScopeLevel.Session, ContributionCategory.Memory, false)]
    public void Private_sensitive_tool_checkpoint_file_and_memory_items_do_not_cross_this_boundary(
        ContextItemKind kind, ScopeLevel scope, ContributionCategory category, bool sensitive)
    {
        using var fx = new Fixture();
        var child = fx.Child();
        var contributor = new TestContributor(new ContextItem("not-shared", kind, "private source data", 0,
            ContextPriority.Normal, RetentionPolicy.ConversationWindow,
            new ContextProvenance("fixture", category, "fixture", scope, sensitive)));
        var source = ParentSnapshot(fx, contributor);
        Assert.Throws<InvalidDataException>(() => Service(fx).CreateSelectedProjection(fx.Session, fx.Run.RunId,
            fx.Run.RootLane, child.Lane, source, new ContextInheritancePolicy(new[] { "not-shared" })));
    }

    [Fact]
    public void Snapshot_must_be_canonically_rooted_in_the_parent_and_cannot_be_reused_for_a_sibling_lineage()
    {
        using var fx = new Fixture();
        var child = fx.Child(); var sibling = fx.Child();
        var source = ParentSnapshot(fx);
        var policy = new ContextInheritancePolicy(new[] { "conversation-000000" });
        Assert.Throws<InvalidDataException>(() => Service(fx).CreateSelectedProjection(fx.Session, fx.Run.RunId,
            sibling.Lane, child.Lane, source, policy));
        Assert.Throws<InvalidDataException>(() => Service(fx).CreateSelectedProjection(fx.Session, fx.Run.RunId,
            fx.Run.RootLane, child.Lane, source with { Id = ArtifactId.New() }, policy));
        Assert.Throws<InvalidDataException>(() => Service(fx).CreateSelectedProjection(SessionId.New(), fx.Run.RunId,
            fx.Run.RootLane, child.Lane, source, policy));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("run")]
    [InlineData("task")]
    [InlineData("lane")]
    [InlineData("sequence")]
    public async System.Threading.Tasks.Task Projection_is_consumable_only_by_its_exact_child_scope(string mismatch)
    {
        using var fx = new Fixture();
        var child = fx.Child();
        var contributor = Service(fx).CreateSelectedProjection(fx.Session, fx.Run.RunId, fx.Run.RootLane,
            child.Lane, ParentSnapshot(fx), new ContextInheritancePolicy(new[] { "conversation-000000" }));
        var request = new MaterializeRequest(mismatch == "session" ? SessionId.New() : fx.Session,
            mismatch == "run" ? RunId.New() : fx.Run.RunId,
            mismatch == "task" ? TaskId.New() : child.Task, mismatch == "lane" ? LaneId.New() : child.Lane,
            TurnId.New(), mismatch == "sequence" ? 0 : fx.Store.CurrentSequence(fx.Session), Fingerprint());
        Assert.Empty(await contributor.GetContextAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Inherited_first_parent_input_is_not_pinned_and_cannot_evict_current_child_intent()
    {
        using var fx = new Fixture();
        var child = fx.Child();
        var source = ParentSnapshot(fx, first: string.Join(' ', Enumerable.Repeat("old-parent-context", 1000)));
        var contributor = Service(fx).CreateSelectedProjection(fx.Session, fx.Run.RunId, fx.Run.RootLane,
            child.Lane, source, new ContextInheritancePolicy(new[] { "conversation-000000" }));
        ModelRequest? received = null;
        var explorer = fx.Explorer((request, _) => { received = request; return Response(); },
            contributors: new[] { contributor }, budget: 500);
        Assert.Equal(StopReason.EndTurn, explorer.Ask("CURRENT CHILD INTENT", "system", fx.Session,
            fx.Run.RunId, child.Lane, "CURRENT CHILD STATE", TestContext.Current.CancellationToken).StopReason);
        Assert.Contains(received!.Messages.SelectMany(message => message.Content).OfType<TextBlock>(), text => text.Text == "CURRENT CHILD INTENT");
        Assert.Contains("CURRENT CHILD STATE", received.Instructions);
        var childSnapshot = fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<ModelStepStarted>().Last().ContextSnapshotRef!;
        using var json = System.Text.Json.JsonDocument.Parse(fx.Artifacts.GetText(childSnapshot.Hash)!);
        Assert.True(json.RootElement.GetProperty("tokenCount").GetInt32() <= 500);
    }

    private static ContextInheritanceService Service(Fixture fx) => new(fx.Store, fx.Codecs, fx.Artifacts);
    private static ExecutionFingerprint Fingerprint() => new("scripted", "h", "t", "c", "o", "fixture");
    private static MaterializeRequest Request(Fixture fx, TaskId task, LaneId lane) => new(fx.Session,
        fx.Run.RunId, task, lane, TurnId.New(), fx.Store.CurrentSequence(fx.Session), Fingerprint());
    private static ModelResponse Response() => new(new ContentBlock[] { new TextBlock("parent answer") },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
    private static ArtifactRef ParentSnapshot(Fixture fx, IContextContributor? extra = null, string first = "selected principal fact")
    {
        var explorer = fx.Explorer(contributors: extra is null ? null : new[] { extra });
        foreach (var text in new[] { first, "other parent input" })
            Assert.Equal(StopReason.EndTurn, explorer.Ask(text, "parent system", fx.Session, fx.Run.RunId,
                fx.Run.RootLane, "parent private working state", TestContext.Current.CancellationToken).StopReason);
        return fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<ModelStepStarted>().Last().ContextSnapshotRef!;
    }
    private sealed class TestContributor(ContextItem item) : IContextContributor
    {
        public System.Threading.Tasks.Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
            CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(new[] { item });
    }
}
