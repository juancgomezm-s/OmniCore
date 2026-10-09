using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Offline feature flows; no accounts, external models or background scheduler.</summary>
public sealed class DelegationAdmissionTests
{
    [Fact]
    public void Child_permission_ceiling_preserves_restrictive_process_and_network_rules()
    {
        var process = new ProcessRule("tool.exe", ["*"] , PermissionDecision.Allow);
        var processDeny = new ProcessRule("tool.exe", ["secret"] , PermissionDecision.Deny);
        var processAsk = new ProcessRule("ask.exe", ["*"] , PermissionDecision.Ask);
        var network = new NetworkRule("*.example.test", PermissionDecision.Allow);
        var networkDeny = new NetworkRule("secret.example.test", PermissionDecision.Deny);
        var networkAsk = new NetworkRule("review.example.test", PermissionDecision.Ask);
        var parent = PermissionScope.With([], [], [process, processDeny, processAsk],
            [network, networkDeny, networkAsk], [], false);
        var emptyChild = PermissionScope.Autonomous();
        var broadenedChild = PermissionScope.With([], [], [process], [network], [], false);
        var preservedChild = PermissionScope.With([], [], [process, processDeny,
            processAsk with { Decision = PermissionDecision.Deny }], [network, networkDeny,
            networkAsk with { Decision = PermissionDecision.Deny }], [], false);

        Assert.True(AgentPermissionScopeSubset.IsSubset(emptyChild, parent));
        Assert.False(AgentPermissionScopeSubset.IsSubset(broadenedChild, parent));
        Assert.True(AgentPermissionScopeSubset.IsSubset(preservedChild, parent));
    }

    [Fact]
    public void Root_identity_and_queued_child_packet_survive_reopen_without_fabricating_worker_progress()
    {
        using var fx = new Fixture();
        fx.Ask("Información española: acción, niño, pingüino.");
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>());
        var source = fx.Source();
        Assert.Equal(root.ExecutionId, source.ExecutionId);
        var request = fx.Request();
        var before = fx.Store.CurrentSequence(fx.Session);
        var command = DelegationCommands.Create(request, Ids.NewV7());
        var ack = fx.Server.SendUserAction(command, TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        Assert.Equal(before + 1, ack.FirstSeq);
        Assert.Equal(before + 4, ack.LastSeq);
        var batch = fx.Store.ReadFrom(fx.Session, before + 1);
        Assert.Equal(4, batch.Count);
        Assert.All(batch, e => Assert.Equal(new CommandCausation(CommandId.Parse(command.MessageId)), e.Causation));
        var delegation = Assert.IsType<DelegationCreated>(fx.Codecs.Decode(batch[3])).Delegation;
        Assert.Equal(root.ExecutionId, batch[3].ExecutionId);
        Assert.All(batch.Take(3), e => { Assert.Equal(delegation.ChildTaskId, e.TaskId); Assert.Equal(delegation.ChildLaneId, e.LaneId); Assert.Null(e.ExecutionId); });
        Assert.Equal(TaskState.Ready, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).StateOf(delegation.ChildTaskId));
        Assert.Equal(LaneState.Queued, LaneProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).StateOf(delegation.ChildLaneId));
        Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>());
        Assert.Empty(fx.Payloads.OfType<DelegationAccepted>());
        Assert.True(fx.Artifacts.Verify(delegation.PacketRef.Hash, delegation.PacketRef.Size));
        using var packet = JsonDocument.Parse(fx.Artifacts.GetText(delegation.PacketRef.Hash)!);
        Assert.Contains("acción", packet.RootElement.GetProperty("Facts")[0].GetProperty("Content").GetString()!);
        Assert.Throws<InvalidDataException>(() => new ContextInheritanceService(fx.Store, fx.Codecs, fx.Artifacts)
            .CreateDelegationProjection(fx.Session, delegation.DelegationId));
        fx.Reopen();
        var count = fx.Store.CurrentSequence(fx.Session);
        var snapshot = AgentsJson.Decode(fx.Server.Query("agents", CancellationToken.None)!.Json)!;
        Assert.NotNull(snapshot.Capacity);
        Assert.Equal(0, snapshot.Capacity.Active); // process-local leases are never adopted after reopen
        Assert.Equal(0, snapshot.Capacity.Waiting);
        var principal = Assert.Single(snapshot.Lanes, lane => lane.ParentTaskId is null);
        Assert.NotNull(principal.Budget);
        Assert.True(principal.Budget.TurnsUsed >= 1);
        Assert.NotNull(principal.SelectableContextItemIds);
        Assert.Contains(request.SelectedItemIds[0], principal.SelectableContextItemIds);
        Assert.DoesNotContain("system", principal.SelectableContextItemIds);
        var queued = Assert.Single(snapshot.Lanes, l => l.DelegationId is not null);
        Assert.Equal(delegation.ChildLaneId.ToString(), queued.LaneId);
        Assert.Equal("Queued", queued.LaneState);
        Assert.Equal("Created", queued.DelegationState);
        Assert.Null(queued.ExecutionId);
        Assert.Null(queued.Model);
        Assert.NotNull(queued.Budget);
        Assert.Equal(request.MaxTokens, queued.Budget.MaxTokens);
        Assert.Equal(0, queued.PendingMailboxMessages);
        Assert.Equal(0, queued.PendingWakeRequests);
        Assert.Contains("en cola · sin worker", AgentPresentation.Describe(snapshot, "es"));
        Assert.Equal(count, fx.Store.CurrentSequence(fx.Session));
        fx.Ask("Continúa sólo el principal");
        Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>());
        Assert.Equal(root.ExecutionId, fx.Source().ExecutionId);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Untrusted_origin_and_reused_command_identity_do_not_allocate_another_child()
    {
        using var fx = new Fixture(); fx.Ask();
        var command = DelegationCommands.Create(fx.Request(), Ids.NewV7());
        var before = fx.Store.CurrentSequence(fx.Session);
        var untrusted = fx.Server.Send(command, CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, untrusted.Outcome?.Kind);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, fx.Server.SendUserAction(command, CancellationToken.None).Outcome?.Kind);
        before = fx.Store.CurrentSequence(fx.Session);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, fx.Server.SendUserAction(command, CancellationToken.None).Outcome?.Kind);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        Assert.Single(fx.Payloads.OfType<DelegationCreated>());
    }

    [Theory]
    [InlineData("mode", "DelegationRequiresOrq")]
    [InlineData("limits", "CoordinationLimitsUnavailable")]
    [InlineData("agents", "AgentOrDepthLimit")]
    [InlineData("depth", "AgentOrDepthLimit")]
    [InlineData("turns", "CoordinationBudgetLimit")]
    [InlineData("tools", "CoordinationBudgetLimit")]
    [InlineData("cost", "CoordinationBudgetLimit")]
    [InlineData("writes", "ChildPermissionCeilingExceedsParent")]
    [InlineData("reads", "ChildPermissionCeilingExceedsParent")]
    public void Admission_fails_closed_without_writing_or_launching(string gate, string reason)
    {
        using var fx = new Fixture(); fx.Ask();
        if (gate is "mode" or "limits")
        {
            var mode = gate == "mode" ? "act" : "orq";
            Assert.Equal("ok", fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"run.mode.select\",\"mode\":\"" + mode + "\"}"), CancellationToken.None).Status);
        }
        if (gate is "agents" or "depth") fx.Grant(gate == "agents" ? 1 : 3, gate == "depth" ? 0 : 1);
        var request = fx.Request();
        request = gate switch {
            "turns" => request with { MaxTurns = 40 }, "tools" => request with { MaxToolCalls = 40 },
            "cost" => request with { MaxCostUsd = 40 }, "writes" => request with { ProfileId = fx.Writer.Id.ToString() },
            "reads" => request with { ProfileId = fx.ForeignReader.Id.ToString() }, _ => request,
        };
        var before = fx.Store.CurrentSequence(fx.Session);
        var ack = fx.Server.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, ack.Outcome?.Kind);
        Assert.Equal(reason, ack.Outcome?.Reason);
        Assert.Null(ack.FirstSeq);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        Assert.Empty(fx.Payloads.OfType<DelegationCreated>());
        Assert.Equal(1, fx.ProviderCalls);
    }

    [Fact]
    public void Selection_errors_and_combined_reservations_are_checked_before_any_child_is_written()
    {
        using var fx = new Fixture(); fx.Ask();
        var request = fx.Request();
        var before = fx.Store.CurrentSequence(fx.Session);
        var invalid = fx.Server.SendUserAction(DelegationCommands.Create(request with { SelectedItemIds = new[] { "system" } }, Ids.NewV7()), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, invalid.Outcome?.Kind);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        var large = request with { MaxTurns = 15, MaxToolCalls = 15, MaxCostUsd = 0.75m };
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, fx.Server.SendUserAction(DelegationCommands.Create(large, Ids.NewV7()), CancellationToken.None).Outcome?.Kind);
        before = fx.Store.CurrentSequence(fx.Session);
        var over = fx.Server.SendUserAction(DelegationCommands.Create(large, Ids.NewV7()), CancellationToken.None);
        Assert.Equal("CoordinationBudgetLimit", over.Outcome?.Reason);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        Assert.Single(fx.Payloads.OfType<DelegationCreated>());
    }

    [Fact]
    public void Multiple_root_executors_are_not_resolved_by_latest_event_and_provider_is_not_called()
    {
        using var fx = new Fixture(); fx.Ask();
        var duplicate = ExecutionId.New();
        using (ExecutionScope.Begin(new(fx.Run, fx.RootTask, fx.Lane, ExecutionId: duplicate)))
            new EventStream(fx.Store, fx.Codecs, fx.Session).Append(new AgentExecutionStarted(duplicate, fx.Lane,
                fx.Profile.Id, null, ExecutionRelation.Awaited, ExecutionSupervision.Managed));
        var before = fx.Store.CurrentSequence(fx.Session);
        var result = fx.Server.ExecuteExplorerTurn(fx.Session, fx.Run,
            token => fx.Explorer().Ask("ambiguous", "system", fx.Session, fx.Run, fx.Lane, "state", token), CancellationToken.None);
        Assert.IsType<InvalidDataException>(result.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, result.Ack.Outcome?.Kind);
        Assert.Equal(1, fx.ProviderCalls);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        var snapshot = AgentsJson.Decode(fx.Server.Query("agents", CancellationToken.None)!.Json)!;
        Assert.True(Assert.Single(snapshot.Lanes).ExecutionAmbiguous);
        Assert.Null(Assert.Single(snapshot.Lanes).ExecutionId);
    }

    [Fact]
    public void Client_rejects_foreign_and_stale_snapshots_and_clears_them_on_session_change()
    {
        var projection = new AgentsProjection(); projection.Activate("one");
        var current = new AgentsSnapshot("one", "run", 12, false, Array.Empty<AgentLaneSnapshot>());
        // Additive read-side fields remain compatible; mutation requests stay strict.
        var futureJson = AgentsJson.Encode(current).TrimEnd('}') + ",\"futureMetric\":true}";
        Assert.Equal("one", AgentsJson.Decode(futureJson)!.SessionId);
        Assert.Throws<JsonException>(() => AgentsJson.DecodeRequest("{\"unknownGrant\":true}"));
        Assert.True(projection.Apply(current));
        Assert.False(projection.Apply(current with { SessionId = "other", BasedOnJournalSequence = 13 }));
        Assert.False(projection.Apply(current with { BasedOnJournalSequence = 11 }));
        Assert.Equal(current, projection.Snapshot);
        projection.Activate("other"); Assert.Null(projection.Snapshot);
        Assert.Contains("Sin sesión", AgentPresentation.Describe(projection.Snapshot, "es"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Atomic_queue_write_failure_reports_whether_the_batch_committed(bool afterCommit)
    {
        using var fx = new Fixture(); fx.Ask();
        var request = fx.Request();
        fx.UseFaultStore(afterCommit);
        var before = fx.Store.CurrentSequence(fx.Session);
        var ack = fx.Server.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()), CancellationToken.None);
        Assert.Equal("error", ack.Status);
        Assert.Equal(afterCommit ? RuntimeCommandOutcomeKind.Accepted : RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        Assert.Equal(before + (afterCommit ? 4 : 0), fx.Store.CurrentSequence(fx.Session));
        Assert.Equal(afterCommit ? 1 : 0, fx.Payloads.OfType<DelegationCreated>().Count());
        Assert.Equal(1, fx.ProviderCalls);
    }

    [Theory]
    [InlineData(false, "CoordinationBudgetLimit")]
    [InlineData(true, "SpendAccountingUnavailable")]
    public void Meta_consumption_is_included_and_unknown_usage_is_not_treated_as_free(bool unknown, string reason)
    {
        using var fx = new Fixture(); fx.Ask();
        var id = "context-fixture";
        var input = fx.Artifacts.PutText("context input", "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
        using (ExecutionScope.Begin(new(fx.Run, fx.RootTask, fx.Lane)))
            new EventStream(fx.Store, fx.Codecs, fx.Session).AppendBatch(new DomainEventPayload[] {
                new MetaModelInvocationStarted(id, fx.Run, "summary", "fixture", input),
                new MetaModelInvocationFailed(id, fx.Run, "summary", "fixture", "fixture failure",
                    unknown ? null : new TokenUsage(1, 1, 0, 0, 0), unknown ? null : 0.75m, TokenUsageFields.All),
            }, DurabilityClass.Barrier);
        var before = fx.Store.CurrentSequence(fx.Session);
        var ack = fx.Server.SendUserAction(DelegationCommands.Create(fx.Request() with { MaxCostUsd = 0.5m }, Ids.NewV7()), CancellationToken.None);
        Assert.Equal(reason, ack.Outcome?.Reason);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        Assert.Empty(fx.Payloads.OfType<DelegationCreated>());
    }

    internal sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-admission-" + Guid.NewGuid().ToString("N"));
        internal string WorkspaceRoot => Root;
        private string Journal => Path.Combine(Root, "journal.db");
        private string StateFile => Path.Combine(Root, "last.txt");
        internal SqliteEventStore Store { get; private set; }
        internal IEventStore RuntimeStore { get; private set; }
        internal IEventCodecRegistry Codecs { get; } = EventCodecs.Create();
        internal FileArtifactStore Artifacts { get; }
        internal SqliteSpendReservationStore Reservations { get; }
        internal OmniServer Server { get; private set; }
        internal MailboxAckFaultStore? MailboxAckFault { get; }
        internal AgentProfile Profile { get; } = new(ProfileId.New(), "reader", 1, PermissionScope.With(["**"], [], [], [], [], false), []);
        internal AgentProfile Writer { get; } = new(ProfileId.New(), "writer", 1, PermissionScope.With(["**"], ["**"], [], [], [], false), []);
        internal AgentProfile ForeignReader { get; } = new(ProfileId.New(), "outside", 1, PermissionScope.With(["outside/**"], [], [], [], [], false), []);
        internal AgentProfile RootProfile { get; }
        internal ModelRoute MailboxRoute { get; } = new("fixture", "http://fixture", ProviderFamily.OpenAiChatCompatible,
            null, "fixture");
        private readonly bool _authorizeMailboxRoute;
        internal SessionId Session { get; }
        internal RunId Run { get; }
        internal TaskId RootTask { get; }
        internal LaneId Lane { get; }
        internal int ProviderCalls;
        internal IEnumerable<DomainEventPayload> Payloads => Store.ReadFrom(Session, 1).Select(Codecs.Decode);
        internal Fixture(bool authorizeMailboxRoute = false, bool failMailboxAckBatches = false,
            bool rootProfileIsWriter = false)
        {
            _authorizeMailboxRoute = authorizeMailboxRoute;
            RootProfile = rootProfileIsWriter ? Writer : Profile;
            Directory.CreateDirectory(Root);
            Store = new SqliteEventStore(Journal); Artifacts = new FileArtifactStore(Root);
            Reservations = new SqliteSpendReservationStore(Path.Combine(Root, "reservations.db"));
            if (failMailboxAckBatches)
            {
                MailboxAckFault = new MailboxAckFaultStore(Store, Codecs);
                RuntimeStore = MailboxAckFault;
            }
            else RuntimeStore = Store;
            Server = NewServer(RuntimeStore);
            Assert.Equal("ok", Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"mode\":\"orq\",\"text\":\"Principal español\"}"), CancellationToken.None).Status);
            Session = Server.LastSessionId()!; Run = Server.LastRunId()!; Lane = Server.LastLaneId()!;
            RootTask = Payloads.OfType<RunCreated>().Single().RootTask;
            Grant();
        }
        private OmniServer NewServer(IEventStore? writeStore = null)
        {
            var server = new OmniServer(writeStore ?? RuntimeStore, Codecs, new InMemoryAuditSink(), StateFile, Artifacts);
            server.ConfigureAgentProfiles(new(new AgentProfileRegistry([Profile, Writer, ForeignReader]), RootProfile));
            if (_authorizeMailboxRoute)
                server.ConfigureNewSessionRoutingPolicy(new SessionRoutingPolicy(1,
                    [AuthorizedModelRoute.From(MailboxRoute, BillingMode.Local)], [BillingMode.Local], false, null, "fixture"));
            return server;
        }
        internal void Grant(int maxAgents = 3, int maxDepth = 1)
        {
            var payload = "{\"cmd\":\"run.mode.select\",\"mode\":\"orq\",\"effort\":\"ultracode\",\"adaptive\":true,"
                + "\"allowedModes\":\"plan,act,orq\",\"maxAgents\":" + maxAgents + ",\"maxDepth\":" + maxDepth
                + ",\"maxTurns\":20,\"maxToolCalls\":20,\"maxElapsedSeconds\":3600,\"maxSpendUsd\":1}";
            var ack = Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
            Assert.Equal("ok", ack.Status);
        }
        internal ExplorerTurn Explorer(Func<ModelRequest, CancellationToken, ModelResponse>? complete = null)
        {
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog, new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]),
                RootProfile, new PathBoundaryValidator(), Root), Root);
            return new ExplorerTurn(complete ?? ((_, _) => {
                ProviderCalls++;
                return new ModelResponse([new TextBlock("Respuesta española")], StopReason.EndTurn, new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null));
            }), executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []), new("fixture", "h", "t", "c", "o", "fixture"),
                new(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null, maxOutputTokens: 256), RuntimeStore, Codecs, Artifacts,
                new InMemoryAuditSink(), new RedactionPolicy(), pricing: new ModelPricing(0m, 0m), modelContextCapacity: 8192,
                spendReservations: Reservations, maximumGenerationRequestAttempts: 1);
        }
        internal ExplorerTurn ExplorerWithMailbox(Func<ModelRequest, CancellationToken, ModelResponse> complete,
            Func<ToolCallId, CancellationToken, Task<string?>> receiveMailbox)
        {
            var tools = HostTools.DelegatedReaderWithMailbox();
            var policyKey = ModelPolicyKey.For("fixture", "fixture");
            var effectivePolicy = EffectiveModelPolicy.Resolve(policyKey,
                new StoredModelPolicy(policyKey, 1, ModelPolicyPresets.ObserveOnly(),
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 16, GuidanceLevel.Full, 2,
                    PlanControl.ModelDriven, 6));
            var boundary = OmniCliRuntime.CreateBoundary(effectivePolicy, Root, tools.Catalog());
            Assert.True(boundary.IsToolVisible("core.agents.mailbox.receive"),
                "The effective read-only policy must classify mailbox waiting as a no-effect capability.");
            var executor = ScriptedToolExecutor.WithWorkspace(tools.Catalog(), new AgentProfilePermissionPolicy(
                new ScriptedPermissionPolicy([]), Profile, new PathBoundaryValidator(), Root), Root,
                boundary: boundary, receiveMailbox: receiveMailbox);
            return new ExplorerTurn(complete, executor, tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), []),
                new("fixture", "h", "t", "c", "o", "fixture"),
                new(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null, maxOutputTokens: 256), RuntimeStore, Codecs,
                Artifacts, new InMemoryAuditSink(), new RedactionPolicy(), pricing: new ModelPricing(0m, 0m),
                modelContextCapacity: 8192, spendReservations: Reservations, maximumGenerationRequestAttempts: 1,
                mailboxDeliveryCompleted: Server.ResolveMailboxDelivery);
        }
        internal void Ask(string question = "Información española: acción, niño, pingüino.")
        {
            var result = Server.ExecuteExplorerTurn(Session, Run,
                token => Explorer().Ask(question, "system", Session, Run, Lane, "state", token), CancellationToken.None);
            Assert.Null(result.Failure);
            Assert.Equal(StopReason.EndTurn, result.Result?.StopReason);
        }
        internal DomainEvent Source() => Store.ReadFrom(Session, 1).Last(e => Codecs.Decode(e) is ModelStepStarted);
        internal DelegationCreateRequest Request()
        {
            var source = Source();
            var reference = ((ModelStepStarted)Codecs.Decode(source)).ContextSnapshotRef!;
            using var json = JsonDocument.Parse(Artifacts.GetText(reference.Hash)!);
            var id = json.RootElement.GetProperty("items").EnumerateArray().Last(i => i.GetProperty("kind").GetString() == "UserMessage").GetProperty("id").GetString()!;
            return new(Profile.Id.ToString(), source.EventId.ToString(), "Revisar sin modificar archivos", new[] { id }, 8192, 2, 4, 4096, 0m);
        }
        internal void Reopen() { Close(); Store = new SqliteEventStore(Journal); RuntimeStore = Store; Server = NewServer(); }
        internal (SqliteEventStore Store, OmniServer Server) OpenSnapshotServer()
        {
            var snapshotPath = Path.Combine(Root, "recovery-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Journal, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
                source.Close();
                destination.Close();
                SqliteConnection.ClearPool(source);
                SqliteConnection.ClearPool(destination);
            }
            var store = new SqliteEventStore(snapshotPath);
            return (store, NewServer(store));
        }
        internal static void CloseAdditionalStore(SqliteEventStore store)
        {
            var connection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
        }
        internal void UseFaultStore(bool afterCommit) => Server = NewServer(new FaultStore(Store, afterCommit));
        internal void UseServerStore(IEventStore writeStore) => Server = NewServer(writeStore);
        private void Close() { var connection = (SqliteConnection)Store.Connection; Store.Close(); SqliteConnection.ClearPool(connection); connection.Dispose(); }
        public void Dispose() { Close(); Directory.Delete(Root, true); }
    }

    private sealed class FaultStore(IEventStore inner, bool afterCommit) : IEventStore
    {
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken cancellationToken)
            => inner.Append(session, evt, durability, cancellationToken);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events, DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (afterCommit) inner.AppendBatch(session, events, durability, cancellationToken);
            throw new IOException("fixture queue commit failure");
        }
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => inner.ReadFrom(session, from);
    }

    [Fact]
    public void A_child_within_the_parent_writer_ceiling_uses_the_exclusive_writer_lease_and_real_tool_pipeline()
    {
        using var fx = new Fixture(rootProfileIsWriter: true);
        fx.Ask("Implementa la integración solicitada en el workspace.");
        var request = fx.Request() with { ProfileId = fx.Writer.Id.ToString(), MaxTokens = 16384 };
        var queued = fx.Server.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, queued.Outcome?.Kind);
        var delegation = fx.Payloads.OfType<DelegationCreated>().Single().Delegation;
        var modelSteps = 0;
        ExplorerTurn.TurnResult? childTurn = null;
        var writerLeaseObserved = false;
        var filePath = Path.Combine(fx.WorkspaceRoot, "workflow-child.txt");
        var executed = fx.Server.ExecuteDelegation(delegation.DelegationId, (work, token) =>
        {
            Assert.False(AgentPermissionScopeSubset.IsReadOnly(work.Profile.PermissionCeiling));
            writerLeaseObserved = AgentCapacity.For(fx.Store).ReadSnapshot(work.Session, work.Run).WriterActive;
            var tools = HostTools.DelegatedAgent(work.Profile);
            var key = ModelPolicyKey.For("fixture", "fixture");
            var effective = EffectiveModelPolicy.Resolve(key,
                new StoredModelPolicy(key, 1, ModelPolicyPresets.FullAgent(), DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 16, GuidanceLevel.Full, 2,
                    PlanControl.ModelDriven, 6));
            var boundary = OmniCliRuntime.CreateBoundary(effective, fx.WorkspaceRoot, tools.Catalog());
            var executor = ScriptedToolExecutor.WithWorkspace(tools.Catalog(),
                new AgentProfilePermissionPolicy(
                    ScriptedPermissionPolicy.WithTool("filesystem.write", PermissionDecision.Allow),
                    work.Profile, new PathBoundaryValidator(), fx.WorkspaceRoot), fx.WorkspaceRoot, boundary,
                (toolCall, receiveToken) => fx.Server.ReceiveMailboxMessageAsync(fx.Session, fx.Run,
                    fx.MailboxRoute, BillingMode.Local, toolCall, receiveToken));
            var turn = new ExplorerTurn((_, _) => Interlocked.Increment(ref modelSteps) == 1
                    ? new ModelResponse([new ToolCallBlock(ToolCallId.New(), "write-integration",
                        "filesystem.write", "{\"path\":\"workflow-child.txt\",\"content\":\"connected\"}")],
                        StopReason.ToolUse, new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("Integration written.")], StopReason.EndTurn,
                        new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null)),
                executor, tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), []),
                new("fixture", "h", "t", "c", "o", "fixture"),
                new(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null, maxOutputTokens: 256),
                fx.RuntimeStore, fx.Codecs, fx.Artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(0m, 0m), modelContextCapacity: 8192, spendReservations: fx.Reservations,
                maximumGenerationRequestAttempts: 1);
            childTurn = turn.Ask(work.Objective, "system", work.Session, work.Run,
                work.Delegation.ChildLaneId, "", token);
            return childTurn;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, executed.Outcome?.Kind);
        Assert.True(writerLeaseObserved, "The child must hold the exclusive writer lease during dispatch.");
        Assert.False(AgentCapacity.For(fx.Store).ReadSnapshot(fx.Session, fx.Run).WriterActive,
            "The exclusive writer lease must be released after the child turn settles.");
        Assert.True(File.Exists(filePath), "write receipt missing; child failures: "
            + string.Join(" | ", fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode)
                .OfType<ToolCallFailed>().Select(item => item.Cause + ":" + item.ErrorCode))
            + "; turn=" + childTurn?.StopReason + "; tools=" + string.Join(" | ",
                childTurn?.ToolCalls.Select(call => call.ToolName + ":" + call.Succeeded + ":" + call.Summary) ?? [])
            + "; final=" + childTurn?.FinalText
            + "; events=" + string.Join(",", fx.Store.ReadFrom(fx.Session, 1)
                .Where(evt => evt.LaneId == delegation.ChildLaneId).Select(fx.Codecs.Decode)
                .Select(payload => payload.GetType().Name)));
        Assert.Equal("connected", File.ReadAllText(filePath));
        var childLane = Assert.Single(AgentsJson.Decode(fx.Server.Query("agents", CancellationToken.None)!.Json)!.Lanes,
            lane => lane.DelegationId == delegation.DelegationId.ToString());
        Assert.Equal("Completed", childLane.ExecutionState);
        Assert.Null(childLane.ResultDisposition);
        Assert.Equal(TaskState.Running, TaskGraphProjection.Replay(fx.Codecs,
            fx.Store.ReadFrom(fx.Session, 1)).StateOf(delegation.ChildTaskId));
        var writeEvents = fx.Store.ReadFrom(fx.Session, 1).Where(evt => evt.LaneId == delegation.ChildLaneId)
            .Select(fx.Codecs.Decode).ToArray();
        Assert.Contains(writeEvents, evt => evt is ToolCallSucceeded succeeded
            && succeeded.ToolCallId == Assert.Single(writeEvents.OfType<ToolCallRequested>()).ToolCallId);
        Assert.Null(ExecutionScope.Current);
    }

    internal sealed class MailboxAckFaultStore(IEventStore inner, IEventCodecRegistry codecs) : IEventStore
    {
        internal bool FailAcknowledgementBatches { get; set; } = true;
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken cancellationToken)
            => inner.Append(session, evt, durability, cancellationToken);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (FailAcknowledgementBatches && events.Select(codecs.Decode)
                .Any(payload => payload is ExecutionMailboxMessageAcknowledged))
                throw new IOException("simulated process failure while committing mailbox ACK batch");
            inner.AppendBatch(session, events, durability, cancellationToken);
        }
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => inner.ReadFrom(session, from);
    }
}
