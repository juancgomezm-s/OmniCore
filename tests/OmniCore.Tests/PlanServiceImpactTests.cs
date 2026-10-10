using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class PlanServiceImpactTests
{
    private static readonly MutationCause Cause = MutationCause.Model;

    [Fact]
    public void Structural_mutations_revise_first_with_impact_and_replay_revision()
    {
        var add = new Fixture();
        AssertStructural(add, PlanMutation.Add(2, "new", null, Array.Empty<PlanItemId>(), Cause),
            MutationImpact.Minor, "plan_item.added");

        var split = new Fixture();
        AssertStructural(split, PlanMutation.Split(split.Root, new[] { "one", "two" }, Cause),
            MutationImpact.Minor, "plan_item.added");

        var reorder = new Fixture();
        var a = reorder.AddItem("A", 2);
        var b = reorder.AddItem("B", 3);
        AssertStructural(reorder, PlanMutation.Reorder(new[] { reorder.Root, b, a }, Cause),
            MutationImpact.Minor, "plan_item.reordered");

        var revise = new Fixture();
        var child = revise.AddItem("child", 2);
        AssertStructural(revise, PlanMutation.Revise(child, Cause, "revised child"),
            MutationImpact.Minor, "plan_item.updated");

        var skip = new Fixture();
        var required = skip.AddItem("required", 2);
        AssertStructural(skip, PlanMutation.Skip(required, Cause, "not applicable"),
            MutationImpact.RequiredSkip, "plan_item.skipped", approved: true);
    }

    [Fact]
    public void Non_structural_mutations_do_not_emit_plan_revised()
    {
        AssertNonStructural(PlanItemState.Pending, (f, item) => PlanMutation.Start(item, Cause), "plan_item.started");
        AssertNonStructural(PlanItemState.InProgress, (f, item) => PlanMutation.Complete(item, Cause, "done"), "plan_item.completed");
        AssertNonStructural(PlanItemState.InProgress, (f, item) => PlanMutation.Block(item, Cause, "blocked"), "plan_item.blocked");
        AssertNonStructural(PlanItemState.Blocked, (f, item) => PlanMutation.Unblock(item, Cause, "clear"), "plan_item.unblocked");
        AssertNonStructural(PlanItemState.InProgress, (f, item) => PlanMutation.Fail(item, Cause, "failed"), "plan_item.failed");
        AssertNonStructural(PlanItemState.InProgress, (f, item) => PlanMutation.Link(item, f.CreateTask(TaskState.Ready), LinkRole.Implements, true, Cause), "plan_item.linked");
        AssertNonStructural(PlanItemState.Pending, (f, item) => PlanMutation.Update(item, Cause, "updated"), "plan_item.updated", nonRoot: true);
    }

    [Fact]
    public void Invalid_state_transitions_are_rejected_without_events()
    {
        AssertRejected(PlanItemState.Pending, item => PlanMutation.Complete(item, Cause, "done"));
        AssertRejected(PlanItemState.Completed, item => PlanMutation.Start(item, Cause));
        AssertRejected(PlanItemState.InProgress, item => PlanMutation.Unblock(item, Cause, "clear"));

        var fixture = new Fixture();
        var item = fixture.AddItem("item", 2);
        fixture.SetState(item, PlanItemState.InProgress);
        var result = fixture.Apply(PlanMutation.Cancel(item, Cause, "stop"), approved: true);
        Assert.True(result.Accepted);
        Assert.IsType<PlanItemCancelled>(result.Events.Single());
        fixture.Append(result);
    }

    [Fact]
    public void Complete_requires_all_required_linked_tasks_but_ignores_optional_links()
    {
        var required = new Fixture();
        var item = required.AddItem("work", 2);
        required.SetState(item, PlanItemState.InProgress);
        var unfinished = required.CreateTask(TaskState.Ready);
        required.Link(item, unfinished, required: true);
        var rejected = required.Apply(PlanMutation.Complete(item, Cause, "done"));
        Assert.False(rejected.Accepted);
        Assert.Contains("aún no está Completed", rejected.Reason!);
        Assert.Empty(rejected.Events);

        var completed = new Fixture();
        var completedItem = completed.AddItem("work", 2);
        completed.SetState(completedItem, PlanItemState.InProgress);
        var completedTask = completed.CreateTask(TaskState.Completed);
        completed.Link(completedItem, completedTask, required: true);
        var accepted = completed.Apply(PlanMutation.Complete(completedItem, Cause, "done"));
        Assert.True(accepted.Accepted);
        completed.Append(accepted);

        var optional = new Fixture();
        var optionalItem = optional.AddItem("work", 2);
        optional.SetState(optionalItem, PlanItemState.InProgress);
        var optionalTask = optional.CreateTask(TaskState.Ready);
        optional.Link(optionalItem, optionalTask, required: false);
        var optionalAccepted = optional.Apply(PlanMutation.Complete(optionalItem, Cause, "done"));
        Assert.True(optionalAccepted.Accepted);
        optional.Append(optionalAccepted);
    }

    [Fact]
    public void Skipping_an_item_skips_its_never_started_tasks_so_the_task_gate_can_pass()
    {
        // ADR-0036 §2: lo que el Plan descarta y nunca arrancó también termina en el TaskGraph.
        var fixture = new Fixture();
        var item = fixture.AddItem("optional", 2, required: false);
        var pending = fixture.CreateTask(TaskState.Pending);
        var ready = fixture.CreateTask(TaskState.Ready);
        var running = fixture.CreateTask(TaskState.Running);
        var blocked = fixture.CreateTask(TaskState.Blocked);
        var shared = fixture.CreateTask(TaskState.Pending);
        var other = fixture.AddItem("other", 3);
        foreach (var task in new[] { pending, ready, running, blocked, shared }) fixture.Link(item, task, required: true);
        fixture.Link(other, shared, required: true);

        var result = fixture.Apply(PlanMutation.Skip(item, Cause, "ya no aplica"));

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(new[] { pending, ready }, result.Events.OfType<TaskSkipped>().Select(evt => evt.TaskId));
        Assert.All(result.Events.OfType<TaskSkipped>(), evt => Assert.Contains("ya no aplica", evt.Reason));
        fixture.Append(result);
        Assert.Equal(TaskState.Skipped, fixture.Tasks.StateOf(pending));
        Assert.Equal(TaskState.Skipped, fixture.Tasks.StateOf(ready));
        // La Task en curso, la bloqueada y la que otro item vivo necesita no se tocan.
        Assert.Equal(TaskState.Running, fixture.Tasks.StateOf(running));
        Assert.Equal(TaskState.Blocked, fixture.Tasks.StateOf(blocked));
        Assert.Equal(TaskState.Pending, fixture.Tasks.StateOf(shared));
    }

    [Fact]
    public void Skipping_a_required_item_with_pending_tasks_waits_for_approval_and_skips_them_with_the_revision()
    {
        var fixture = new Fixture();
        var item = fixture.AddItem("required", 2);
        var pending = fixture.CreateTask(TaskState.Pending);
        fixture.Link(item, pending, required: true);

        var unapproved = fixture.Apply(PlanMutation.Skip(item, Cause, "no aplica"));
        Assert.False(unapproved.Accepted);
        Assert.DoesNotContain(unapproved.Events, evt => evt is TaskSkipped);

        var approved = fixture.Apply(PlanMutation.Skip(item, Cause, "no aplica"), approved: true);
        Assert.True(approved.Accepted, approved.Reason);
        Assert.IsType<PlanRevised>(approved.Events[0]);
        Assert.Single(approved.Events.OfType<TaskSkipped>());
        fixture.Append(approved);
        Assert.Equal(TaskState.Skipped, fixture.Tasks.StateOf(pending));
        Assert.DoesNotContain(fixture.Tasks.Tasks(), task => task.State is TaskState.Pending or TaskState.Ready);
    }

    [Fact]
    public void Skip_non_required_is_automatic_and_required_skip_needs_reason_and_approval()
    {
        var optional = new Fixture();
        var optionalItem = optional.AddItem("optional", 2, required: false);
        var skipOptional = optional.Apply(PlanMutation.Skip(optionalItem, Cause, "not needed"));
        Assert.True(skipOptional.Accepted);
        Assert.False(skipOptional.NeedsApproval);
        Assert.IsType<PlanItemSkipped>(skipOptional.Events.Single());
        Assert.Equal(0, skipOptional.RevisionDelta);
        optional.Append(skipOptional);

        var required = new Fixture();
        var requiredItem = required.AddItem("required", 2);
        var needsApproval = required.Apply(PlanMutation.Skip(requiredItem, Cause, "not needed"));
        Assert.False(needsApproval.Accepted);
        Assert.True(needsApproval.NeedsApproval);
        Assert.Equal(MutationImpact.RequiredSkip, needsApproval.Impact);
        Assert.Empty(needsApproval.Events);

        var noReason = required.Apply(PlanMutation.Skip(requiredItem, Cause, ""), approved: true);
        Assert.False(noReason.Accepted);
        Assert.False(noReason.NeedsApproval);
        Assert.Empty(noReason.Events);

        var approved = required.Apply(PlanMutation.Skip(requiredItem, Cause, "not needed"), approved: true);
        Assert.True(approved.Accepted);
        Assert.IsType<PlanRevised>(approved.Events[0]);
        AssertStructuralResult(required, approved, MutationImpact.RequiredSkip, "plan_item.skipped");
    }

    [Fact]
    public void Cancel_required_item_requires_approval_and_then_emits_cancelled()
    {
        var fixture = new Fixture();
        var item = fixture.AddItem("required", 2);
        var waiting = fixture.Apply(PlanMutation.Cancel(item, Cause, "stop"));
        Assert.False(waiting.Accepted);
        Assert.True(waiting.NeedsApproval);
        Assert.Equal(MutationImpact.RequiredCancel, waiting.Impact);
        Assert.Empty(waiting.Events);

        var approved = fixture.Apply(PlanMutation.Cancel(item, Cause, "stop"), approved: true);
        Assert.True(approved.Accepted);
        Assert.False(approved.NeedsApproval);
        Assert.Equal(0, approved.RevisionDelta);
        Assert.Single(approved.Events);
        Assert.IsType<PlanItemCancelled>(approved.Events[0]);
        fixture.Append(approved);
    }

    [Fact]
    public void Root_changes_are_scope_expansion_but_non_root_changes_are_not()
    {
        var fixture = new Fixture();
        var rootMutation = PlanMutation.Revise(fixture.Root, Cause, "new objective");
        var rootNeedsApproval = fixture.Apply(rootMutation);
        Assert.False(rootNeedsApproval.Accepted);
        Assert.True(rootNeedsApproval.NeedsApproval);
        Assert.Equal(MutationImpact.ScopeExpansion, rootNeedsApproval.Impact);
        Assert.Empty(rootNeedsApproval.Events);
        var rootApproved = fixture.Apply(rootMutation, approved: true);
        Assert.True(rootApproved.Accepted);
        AssertStructuralResult(fixture, rootApproved, MutationImpact.ScopeExpansion, "plan_item.updated");

        var rootUpdate = new Fixture();
        var updateRoot = PlanMutation.Update(rootUpdate.Root, Cause, "updated objective");
        var rootUpdateNeedsApproval = rootUpdate.Apply(updateRoot);
        Assert.False(rootUpdateNeedsApproval.Accepted);
        Assert.True(rootUpdateNeedsApproval.NeedsApproval);
        Assert.Equal(MutationImpact.ScopeExpansion, rootUpdateNeedsApproval.Impact);
        Assert.Empty(rootUpdateNeedsApproval.Events);
        var approvedRootUpdate = rootUpdate.Apply(updateRoot, approved: true);
        Assert.True(approvedRootUpdate.Accepted);
        Assert.Equal(0, approvedRootUpdate.RevisionDelta);
        Assert.IsType<PlanItemUpdated>(approvedRootUpdate.Events.Single());
        rootUpdate.Append(approvedRootUpdate);

        var update = new Fixture();
        var child = update.AddItem("child", 2);
        var result = update.Apply(PlanMutation.Update(child, Cause, "updated child"));
        Assert.True(result.Accepted);
        Assert.False(result.NeedsApproval);
        Assert.Equal(0, result.RevisionDelta);
        update.Append(result);
    }

    [Fact]
    public void Revise_reopens_failed_item_to_ready_with_moderate_revision()
    {
        var fixture = new Fixture();
        var item = fixture.AddItem("failed", 2);
        fixture.SetState(item, PlanItemState.Failed);
        var result = fixture.Apply(PlanMutation.Revise(item, Cause, "try again"));
        Assert.True(result.Accepted);
        Assert.Equal(MutationImpact.Moderate, result.Impact);
        Assert.IsType<PlanRevised>(result.Events[0]);
        Assert.IsType<PlanItemReopened>(result.Events[1]);
        Assert.Equal(PlanItemState.Ready, PlanProjection.Replay(EventCodecs.Create(), AppendAndRead(fixture, result)).Item(item)!.State);
    }

    [Fact]
    public void Split_requires_two_nonempty_parts_rejects_terminal_items_and_makes_parent_a_container()
    {
        var fixture = new Fixture();
        var tooFew = fixture.Apply(PlanMutation.Split(fixture.Root, new[] { "one" }, Cause));
        AssertRejectedResult(tooFew);
        var emptyPart = fixture.Apply(PlanMutation.Split(fixture.Root, new[] { "one", " " }, Cause));
        AssertRejectedResult(emptyPart);

        var terminal = new Fixture();
        terminal.SetState(terminal.Root, PlanItemState.Completed);
        AssertRejectedResult(terminal.Apply(PlanMutation.Split(terminal.Root, new[] { "one", "two" }, Cause)));

        var split = new Fixture();
        var result = split.Apply(PlanMutation.Split(split.Root, new[] { "one", "two" }, Cause));
        Assert.True(result.Accepted);
        Assert.Equal(2, result.Events.OfType<PlanItemAdded>().Count());
        Assert.All(result.Events.OfType<PlanItemAdded>(), child => Assert.Equal(split.Root, child.ParentId));
        split.Append(result);
        AssertRejectedResult(split.Apply(PlanMutation.Start(split.Root, Cause)), "contenedor");
        AssertRejectedResult(split.Apply(PlanMutation.Complete(split.Root, Cause, "done")), "contenedor");
    }

    [Fact]
    public void Reorder_requires_permutation_and_cannot_move_in_progress_or_terminal_items()
    {
        var fixture = new Fixture();
        var a = fixture.AddItem("A", 2);
        var b = fixture.AddItem("B", 3);
        AssertRejectedResult(fixture.Apply(PlanMutation.Reorder(new[] { fixture.Root, a }, Cause)));
        AssertRejectedResult(fixture.Apply(PlanMutation.Reorder(new[] { fixture.Root, a, a }, Cause)));
        AssertRejectedResult(fixture.Apply(PlanMutation.Reorder(new[] { fixture.Root, a, PlanItemId.New() }, Cause)));

        var reordered = fixture.Apply(PlanMutation.Reorder(new[] { fixture.Root, b, a }, Cause));
        Assert.True(reordered.Accepted);
        fixture.Append(reordered);

        var locked = new Fixture();
        var active = locked.AddItem("active", 2);
        var pending = locked.AddItem("pending", 3);
        locked.SetState(active, PlanItemState.InProgress);
        AssertRejectedResult(locked.Apply(PlanMutation.Reorder(new[] { locked.Root, pending, active }, Cause)));
        locked.SetState(active, PlanItemState.Completed);
        AssertRejectedResult(locked.Apply(PlanMutation.Reorder(new[] { locked.Root, pending, active }, Cause)));
    }

    [Fact]
    public void Add_rejects_empty_text_unknown_or_terminal_parent_and_unknown_dependency()
    {
        var fixture = new Fixture();
        AssertRejectedResult(fixture.Apply(PlanMutation.Add(2, " ", null, Array.Empty<PlanItemId>(), Cause)));
        AssertRejectedResult(fixture.Apply(PlanMutation.Add(2, "new", PlanItemId.New(), Array.Empty<PlanItemId>(), Cause)));
        var terminalParent = fixture.AddItem("terminal parent", 2);
        fixture.SetState(terminalParent, PlanItemState.Completed);
        AssertRejectedResult(fixture.Apply(PlanMutation.Add(3, "new", terminalParent, Array.Empty<PlanItemId>(), Cause)));
        AssertRejectedResult(fixture.Apply(PlanMutation.Add(3, "new", null, new[] { PlanItemId.New() }, Cause)));
    }

    [Fact]
    public void Link_and_unlink_validate_task_item_and_duplicate_relationships()
    {
        var fixture = new Fixture();
        var item = fixture.AddItem("item", 2);
        AssertRejectedResult(fixture.Apply(PlanMutation.Link(item, TaskId.New(), LinkRole.Implements, true, Cause)));
        var task = fixture.CreateTask(TaskState.Ready);
        var link = fixture.Apply(PlanMutation.Link(item, task, LinkRole.Implements, true, Cause));
        Assert.True(link.Accepted);
        fixture.Append(link);
        AssertRejectedResult(fixture.Apply(PlanMutation.Link(item, task, LinkRole.Implements, true, Cause)));
        AssertRejectedResult(fixture.Apply(PlanMutation.Unlink(item, TaskId.New(), Cause)));

        var terminal = new Fixture();
        var terminalItem = terminal.AddItem("terminal", 2);
        terminal.SetState(terminalItem, PlanItemState.Completed);
        var terminalTask = terminal.CreateTask(TaskState.Ready);
        AssertRejectedResult(terminal.Apply(PlanMutation.Link(terminalItem, terminalTask,
            LinkRole.Implements, true, Cause)));
        var unlink = fixture.Apply(PlanMutation.Unlink(item, task, Cause));
        Assert.True(unlink.Accepted);
        fixture.Append(unlink);
    }

    [Fact]
    public void Every_accepted_structural_result_is_validated_by_event_stream()
    {
        var fixturesAndMutations = new List<(Fixture Fixture, PlanMutation Mutation, bool Approved)>
        {
            (new Fixture(), PlanMutation.Add(2, "new", null, Array.Empty<PlanItemId>(), Cause), false),
        };
        var split = new Fixture();
        fixturesAndMutations.Add((split, PlanMutation.Split(split.Root, new[] { "a", "b" }, Cause), false));
        var reorder = new Fixture();
        var first = reorder.AddItem("first", 2);
        var second = reorder.AddItem("second", 3);
        fixturesAndMutations.Add((reorder, PlanMutation.Reorder(new[] { reorder.Root, second, first }, Cause), false));
        var revise = new Fixture();
        var child = revise.AddItem("child", 2);
        fixturesAndMutations.Add((revise, PlanMutation.Revise(child, Cause, "changed"), false));
        var skip = new Fixture();
        var required = skip.AddItem("required", 2);
        fixturesAndMutations.Add((skip, PlanMutation.Skip(required, Cause, "reason"), true));

        foreach (var pair in fixturesAndMutations)
        {
            var result = pair.Fixture.Apply(pair.Mutation, pair.Approved);
            Assert.True(result.Accepted, result.Reason);
            var revised = Assert.IsType<PlanRevised>(result.Events[0]);
            Assert.Equal(pair.Fixture.Plan.Revision() + 1, revised.Revision);
            Assert.Equal(1, result.RevisionDelta);
            pair.Fixture.Append(result);
            var replay = PlanProjection.Replay(EventCodecs.Create(), pair.Fixture.Events);
            Assert.Equal(revised.Revision, replay.Revision());
        }
    }

    private static void AssertStructural(Fixture fixture, PlanMutation mutation, MutationImpact impact,
        string payloadType, bool approved = false)
    {
        var result = fixture.Apply(mutation, approved);
        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(impact, result.Impact);
        Assert.Equal(1, result.RevisionDelta);
        var revised = Assert.IsType<PlanRevised>(result.Events[0]);
        Assert.Equal(fixture.Plan.Revision() + 1, revised.Revision);
        Assert.Equal(payloadType, result.Events[1].Type().ToString());
        AssertStructuralResult(fixture, result, impact, payloadType);
    }

    private static void AssertStructuralResult(Fixture fixture, PlanServiceResult result, MutationImpact impact,
        string payloadType)
    {
        Assert.Equal(impact, result.Impact);
        Assert.Equal(1, result.RevisionDelta);
        Assert.IsType<PlanRevised>(result.Events[0]);
        Assert.Contains(result.Events, evt => evt.Type().ToString() == payloadType);
        fixture.Append(result);
        var replay = PlanProjection.Replay(EventCodecs.Create(), fixture.Events);
        Assert.Equal(fixture.Plan.Revision(), replay.Revision());
    }

    private static void AssertNonStructural(PlanItemState state,
        Func<Fixture, PlanItemId, PlanMutation> mutationFactory, string expectedEvent, bool nonRoot = false)
    {
        var fixture = new Fixture();
        var item = nonRoot ? fixture.AddItem("child", 2) : fixture.Root;
        fixture.SetState(item, state);
        var mutation = mutationFactory(fixture, item);
        var result = fixture.Apply(mutation);
        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(0, result.RevisionDelta);
        Assert.DoesNotContain(result.Events, evt => evt is PlanRevised);
        Assert.Equal(expectedEvent, result.Events.Single().Type().ToString());
        fixture.Append(result);
    }

    private static void AssertRejected(PlanItemState state, Func<PlanItemId, PlanMutation> mutationFactory)
    {
        var fixture = new Fixture();
        fixture.SetState(fixture.Root, state);
        AssertRejectedResult(fixture.Apply(mutationFactory(fixture.Root)));
    }

    private static void AssertRejectedResult(PlanServiceResult result, string? reasonPart = null)
    {
        Assert.False(result.Accepted);
        Assert.False(result.NeedsApproval);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Empty(result.Events);
        if (reasonPart is not null)
        {
            Assert.Contains(reasonPart, result.Reason!, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<DomainEvent> AppendAndRead(Fixture fixture, PlanServiceResult result)
    {
        fixture.Append(result);
        return fixture.Events;
    }

    private sealed class Fixture
    {
        private readonly InMemoryEventStore _store = new();
        private readonly EventCodecs _codecs = EventCodecs.Create();
        private readonly SessionId _session = SessionId.New();
        private readonly EventStream _stream;
        private readonly RunId _runId;
        private readonly PlanId _planId = PlanId.New();
        public PlanItemId Root { get; } = PlanItemId.New();

        public Fixture()
        {
            _stream = new EventStream(_store, _codecs, _session);
            _runId = TestRun.Open(_stream, _session).RunId;
            _stream.Append(new PlanCreated(_planId, _runId, Root, "objective"));
        }

        public IReadOnlyList<DomainEvent> Events => _store.ReadFrom(_session, 1);
        public PlanProjection Plan => PlanProjection.Replay(_codecs, Events);
        public TaskGraphProjection Tasks => TaskGraphProjection.Replay(_codecs, Events);
        public LaneProjection Lanes => LaneProjection.Replay(_codecs, Events);

        public PlanItemId AddItem(string description, int order, PlanItemId? parent = null, bool required = true,
            IReadOnlyList<PlanItemId>? dependencies = null)
        {
            var item = PlanItemId.New();
            _stream.Append(new PlanRevised(_planId, _runId, Plan.Revision() + 1, "[]", MutationImpact.Minor));
            _stream.Append(new PlanItemAdded(item, _planId, description, order, parent, dependencies ?? Array.Empty<PlanItemId>(),
                required, new Dictionary<string, string>()));
            return item;
        }

        public void SetState(PlanItemId item, PlanItemState state)
        {
            var current = Plan.Item(item)!.State;
            if (state == current)
            {
                return;
            }

            if (current == PlanItemState.Pending && state == PlanItemState.Ready)
            {
                _stream.Append(new PlanItemReady(item));
                return;
            }

            if (state is PlanItemState.InProgress or PlanItemState.Blocked or PlanItemState.Completed
                or PlanItemState.Failed)
            {
                if (current == PlanItemState.Pending)
                {
                    _stream.Append(new PlanItemStarted(item));
                    current = PlanItemState.InProgress;
                }
                else if (current == PlanItemState.Ready && state != PlanItemState.Ready)
                {
                    _stream.Append(new PlanItemStarted(item));
                    current = PlanItemState.InProgress;
                }

                if (state == PlanItemState.Blocked && current == PlanItemState.InProgress)
                {
                    _stream.Append(new PlanItemBlocked(item, "blocked"));
                }
                else if (state == PlanItemState.Completed && current == PlanItemState.InProgress)
                {
                    _stream.Append(new PlanItemCompleted(item, "done"));
                }
                else if (state == PlanItemState.Failed && current == PlanItemState.InProgress)
                {
                    _stream.Append(new PlanItemFailed(item, "failed"));
                }

                return;
            }

            if (state == PlanItemState.Skipped)
            {
                _stream.Append(new PlanItemSkipped(item, "skip"));
                return;
            }

            if (state == PlanItemState.Cancelled)
            {
                _stream.Append(new PlanItemCancelled(item, "cancel"));
                return;
            }

            throw new InvalidOperationException("unsupported fixture state " + state);
        }

        public TaskId CreateTask(TaskState state)
        {
            var task = TaskId.New();
            _stream.Append(new TaskCreated(task, _runId, "task", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null)));
            if (state == TaskState.Pending)
            {
                return task;
            }

            _stream.Append(new TaskReady(task));
            if (state == TaskState.Ready)
            {
                return task;
            }

            var lane = LaneId.New();
            _stream.Append(new LaneCreated(lane, task, ProfileId.New()));
            _stream.Append(new LaneStarted(lane));
            _stream.Append(new TaskStarted(task, lane));
            if (state == TaskState.Running)
            {
                return task;
            }

            if (state == TaskState.Blocked)
            {
                _stream.Append(new TaskBlocked(task, "blocked"));
            }
            else if (state == TaskState.Completed)
            {
                _stream.Append(new TaskCompleted(task, null));
            }
            else if (state == TaskState.Failed)
            {
                _stream.Append(new TaskFailed(task, "failed"));
            }
            else
            {
                throw new InvalidOperationException("unsupported task state " + state);
            }

            return task;
        }

        public void Link(PlanItemId item, TaskId task, bool required) =>
            _stream.Append(new PlanItemLinked(item, new PlanItemLink(task, required, LinkRole.Implements)));

        public PlanServiceResult Apply(PlanMutation mutation, bool approved = false) =>
            new PlanService().Apply(Plan, Tasks, Lanes, mutation, approved);

        public void Append(PlanServiceResult result)
        {
            foreach (var evt in result.Events)
            {
                _stream.Append(evt);
            }
        }
    }
}
