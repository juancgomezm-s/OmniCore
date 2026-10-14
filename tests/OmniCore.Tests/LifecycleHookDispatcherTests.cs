namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class LifecycleHookDispatcherTests
{
    private sealed class FixtureHook(HookRegistration registration,
        Func<HookContext, HookResult> result) : IHook
    {
        public HookRegistration Registration { get; } = registration;
        public HookContext? Received { get; private set; }
        public HookResult Execute(HookContext context)
        {
            Received = context;
            return result(context);
        }
        public ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Execute(context));
    }

    private sealed class CountingMetaProvider : IModelProvider
    {
        public int Calls;
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await System.Threading.Tasks.Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ResponseCompleted(new ModelResponse([new TextBlock("compressed context")],
                StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("fixture", "meta", null)));
        }
    }

    private static HookRegistration Registration(TrustLevel trust, HookCapability capability,
        bool workspaceTrusted = true, HookArgumentAccess access = HookArgumentAccess.Redacted) =>
        new("fixture", new ComponentSource(SourceKind.Extension, ScopeLevel.Project, trust, "fixture", "1"),
            LifecyclePoint.BeforePermission, new HashSet<HookCapability> { capability },
            new HashSet<HookCapability> { capability }, access, Enabled: true,
            WorkspaceTrusted: workspaceTrusted);

    [Fact]
    public async System.Threading.Tasks.Task Restrict_result_only_denies_and_observer_failure_is_fail_open()
    {
        var dispatcher = new LifecycleHookDispatcher();
        var restrict = new FixtureHook(Registration(TrustLevel.Project, HookCapability.Restrict),
            _ => new HookRestrict(new HashSet<string> { "filesystem.write" }, "fixture restriction"));
        var decision = await dispatcher.DispatchAsync([restrict], new HookContext(
            LifecyclePoint.BeforePermission, "filesystem.write", "{}"), TestContext.Current.CancellationToken);
        Assert.True(decision.Denied);
        Assert.Contains("filesystem.write", decision.DeniedToolIds);

        var observer = new FixtureHook(Registration(TrustLevel.ThirdParty, HookCapability.Observe),
            _ => throw new IOException("observer unavailable"));
        var observed = await dispatcher.DispatchAsync([observer], new HookContext(LifecyclePoint.BeforePermission),
            TestContext.Current.CancellationToken);
        Assert.False(observed.Denied);
        Assert.Contains(observed.Warnings, warning => warning.StartsWith("HookFailed:fixture:", StringComparison.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task Restriction_failure_is_fail_closed_and_untrusted_hooks_cannot_restrict()
    {
        var dispatcher = new LifecycleHookDispatcher();
        var failedRestriction = new FixtureHook(Registration(TrustLevel.Project, HookCapability.Restrict),
            _ => throw new IOException("restriction unavailable"));
        var failed = await dispatcher.DispatchAsync([failedRestriction], new HookContext(LifecyclePoint.BeforePermission),
            TestContext.Current.CancellationToken);
        Assert.True(failed.Denied);

        var untrusted = new FixtureHook(Registration(TrustLevel.Untrusted, HookCapability.Restrict),
            _ => new HookRestrict(new HashSet<string> { "filesystem.write" }, "untrusted"));
        var ignored = await dispatcher.DispatchAsync([untrusted], new HookContext(
            LifecyclePoint.BeforePermission, "filesystem.write"), TestContext.Current.CancellationToken);
        Assert.False(ignored.Denied);
        Assert.Null(untrusted.Received);
    }

    [Fact]
    public async System.Threading.Tasks.Task Argument_access_none_removes_even_already_redacted_payload()
    {
        var hook = new FixtureHook(Registration(TrustLevel.Project, HookCapability.Observe,
            access: HookArgumentAccess.None), _ => new HookContinue());
        _ = await new LifecycleHookDispatcher().DispatchAsync([hook], new HookContext(
            LifecyclePoint.BeforePermission, "tool", "{\"value\":\"[redacted]\"}"),
            TestContext.Current.CancellationToken);
        Assert.Null(hook.Received!.RedactedArgumentsJson);
    }

    [Fact]
    public async System.Threading.Tasks.Task Annotations_are_downgraded_and_untrusted_observation_is_event_filtered()
    {
        var annotation = new FixtureHook(Registration(TrustLevel.Core, HookCapability.Annotate), _ =>
            new HookAnnotate([new ContextItem("system.override", ContextItemKind.System, "untrusted text", 1,
                ContextPriority.Pinned, RetentionPolicy.KeepForever,
                new ContextProvenance("system", ContributionCategory.System, "core", ScopeLevel.BuiltIn, false))]));
        var dispatcher = new LifecycleHookDispatcher();
        var result = await dispatcher.DispatchAsync([annotation], new HookContext(LifecyclePoint.BeforePermission),
            TestContext.Current.CancellationToken);
        var item = Assert.Single(result.Annotations);
        Assert.Equal(ContextItemKind.Knowledge, item.Kind);
        Assert.Equal(ContextPriority.Low, item.Priority);
        Assert.Equal(RetentionPolicy.ConversationWindow, item.Retention);
        Assert.StartsWith("hook.fixture.", item.Id, StringComparison.Ordinal);
        Assert.Equal(ContributionCategory.Knowledge, item.Provenance.Category);

        var remote = new FixtureHook(Registration(TrustLevel.Untrusted, HookCapability.Observe,
            access: HookArgumentAccess.None) with { EventTypeFilters = new HashSet<string>(StringComparer.Ordinal)
            { "interaction.requested" } }, _ => new HookContinue());
        _ = await dispatcher.DispatchAsync([remote], new HookContext(LifecyclePoint.BeforePermission,
            EventType: "interaction.requested", RedactedArgumentsJson: "filtered"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(remote.Received);
        Assert.Null(remote.Received.RedactedArgumentsJson);

        var filtered = new FixtureHook(remote.Registration, _ => new HookContinue());
        _ = await dispatcher.DispatchAsync([filtered], new HookContext(LifecyclePoint.BeforePermission,
            EventType: "toolcall.started", RedactedArgumentsJson: "filtered"),
            TestContext.Current.CancellationToken);
        Assert.Null(filtered.Received);
    }

    [Fact]
    public void Only_explicitly_consented_trusted_hooks_can_request_full_arguments()
    {
        var trusted = Registration(TrustLevel.Trusted, HookCapability.Observe,
            access: HookArgumentAccess.Full);
        Assert.Equal(HookArgumentAccess.Redacted, trusted.EffectiveArgumentAccess);
        Assert.Equal(HookArgumentAccess.Full, (trusted with { FullArgumentsConsented = true }).EffectiveArgumentAccess);
        Assert.Equal(HookArgumentAccess.Redacted, (trusted with
        { Source = new ComponentSource(SourceKind.Extension, ScopeLevel.Project, TrustLevel.Project, "fixture", "1"),
            FullArgumentsConsented = true }).EffectiveArgumentAccess);
        Assert.False(Registration(TrustLevel.Untrusted, HookCapability.Annotate).IsEligible(HookCapability.Annotate));
    }

    [Fact]
    public void Lifecycle_points_without_an_engine_consumer_are_explicitly_unsupported()
    {
        Assert.True(LifecycleHookSupport.IsSupported(LifecyclePoint.BeforePermission));
        Assert.True(LifecycleHookSupport.IsSupported(LifecyclePoint.AfterModel));
        Assert.False(LifecycleHookSupport.IsSupported(LifecyclePoint.SessionStarting));
        Assert.False(LifecycleHookSupport.IsSupported(LifecyclePoint.BeforeValidation));
        var hook = new FixtureHook(Registration(TrustLevel.Project, HookCapability.Observe) with
        { Point = LifecyclePoint.SessionStarting }, _ => new HookContinue());
        var registry = new LifecycleHookRegistry();
        registry.Register(hook, explicitlyActivated: true,
            consented: new HashSet<HookCapability> { HookCapability.Observe });
        Assert.Empty(registry.Activated());
    }

    [Fact]
    public void BeforeModel_restriction_changes_real_request_and_forged_tool_call_cannot_execute()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-hook-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var catalog = new FakeCatalog().Add(FakeTool.Read("filesystem.read"));
            var requests = new List<ModelRequest>();
            var calls = 0;
            var hook = new FixtureHook(Registration(TrustLevel.Core, HookCapability.Restrict) with
            { Point = LifecyclePoint.BeforeModel }, _ => new HookRestrict(
                new HashSet<string>(StringComparer.Ordinal) { "filesystem.read" }, "policy"));
            var annotate = new FixtureHook(Registration(TrustLevel.Core, HookCapability.Annotate) with
            { Id = "fixture.annotation", Point = LifecyclePoint.BeforeModel }, _ => new HookAnnotate([
                new ContextItem("injected", ContextItemKind.System, "hook context evidence", 1,
                    ContextPriority.Pinned, RetentionPolicy.KeepForever,
                    new ContextProvenance("fixture", ContributionCategory.System, "fixture", ScopeLevel.BuiltIn, false))]));
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                if (calls++ == 0)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "forged",
                        "filesystem.read", "{}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                ScriptedPermissionPolicy.WithTool("filesystem.read", PermissionDecision.Allow), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                lifecycleHooks: [hook, annotate]);
            var result = turn.Ask("read", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.True(result.StopReason == StopReason.EndTurn, result.FailureDiagnostic ?? result.FinalText ?? "no turn diagnostic");
            Assert.DoesNotContain(requests[0].Tools, definition => definition.Name == "filesystem.read");
            Assert.Contains("hook context evidence", requests[0].Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain(store.ReadFrom(session, 1).Select(codecs.Decode), item => item is ToolCallStarted);
            Assert.Contains(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ToolCallRejected>(),
                rejected => rejected.ErrorCode == ToolErrorCode.UnknownTool);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Unresolved_required_hook_validation_withholds_real_turn_completion()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-hook-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var catalog = new FakeCatalog();
            var hook = new FixtureHook(Registration(TrustLevel.Core, HookCapability.RequireValidation) with
            { Id = "fixture.validator", Point = LifecyclePoint.BeforeCompletion }, _ =>
                new HookRequireValidation("fixture.check", "completion requires a check"));
            var turn = new ExplorerTurn((_, _) => new ModelResponse([new TextBlock("model says done")],
                    StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                    new ProviderMetadata("fixture", "fixture", null)),
                ScriptedToolExecutor.WithWorkspace(catalog,
                    new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                lifecycleHooks: [hook]);
            var result = turn.Ask("hello", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal("lifecycle.hook.completion_withheld", result.FinalText);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Failed_before_model_restriction_fails_closed_records_hook_and_never_calls_provider()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-hook-failclosed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var providerCalls = 0;
            var failed = new FixtureHook(Registration(TrustLevel.Project, HookCapability.Restrict) with
            { Id = "fixture.restrict", Point = LifecyclePoint.BeforeModel }, _ => throw new IOException("fixture failure"));
            var catalog = new FakeCatalog();
            var turn = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                return new ModelResponse([new TextBlock("should not run")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                    new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                lifecycleHooks: [failed]);

            var result = turn.Ask("hello", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal(0, providerCalls);
            Assert.Contains(events.OfType<HookApplied>(), applied => applied.HookId == "fixture.restrict"
                && applied.Point == nameof(LifecyclePoint.BeforeModel) && applied.Result == "failed_closed");
            var started = Assert.Single(events.OfType<TurnStarted>());
            Assert.Contains(started.Fingerprint!.Components, component => component.Name == "lifecycle.hooks");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Before_model_restriction_runs_before_context_compaction_or_either_provider()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-hook-before-compaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var primaryCalls = 0;
            var hookCalls = 0;
            var meta = new CountingMetaProvider();
            var hook = new FixtureHook(Registration(TrustLevel.Core, HookCapability.Restrict) with
            { Id = "fixture.restrict.before_compaction", Point = LifecyclePoint.BeforeModel }, _ =>
                Interlocked.Increment(ref hookCalls) == 1
                    ? new HookContinue()
                    : throw new IOException("restriction failed closed"));
            var compactWhenOneOlderItemExists = new ContextManagementPolicy(4096, 1,
                RecentTailItems: 0, CompactAfterItems: 1, MaxCheckpointCharacters: 100);
            var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4,
                GuidanceLevel.Off, 1, PlanControl.Assisted, 4, compactWhenOneOlderItemExists);
            var catalog = new FakeCatalog();
            var turn = new ExplorerTurn((_, _) =>
            {
                Interlocked.Increment(ref primaryCalls);
                return new ModelResponse([new TextBlock("first response")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "primary", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                harness: harness, metaModelProvider: meta, lifecycleHooks: [hook]);

            var first = turn.Ask("first user intent", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, first.StopReason);
            Assert.Equal(1, primaryCalls);
            Assert.Contains(store.ReadFrom(session, 1).Select(codecs.Decode), item => item is ModelCompleted);
            Assert.True(ContextCompaction.ShouldCompact(1, compactWhenOneOlderItemExists));
            primaryCalls = 0;

            var blocked = turn.Ask("second user intent", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.Error, blocked.StopReason);
            Assert.Equal("lifecycle.hook.model_invocation_withheld", blocked.FinalText);
            Assert.Equal(0, primaryCalls);
            Assert.Equal(0, meta.Calls);
            Assert.Equal(2, hookCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Failed_after_tool_restriction_persists_first_outcome_and_blocks_sibling_and_next_model_step()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-hook-after-tool-failclosed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var primaryCalls = 0;
            var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.first")).Add(FakeTool.Read("fixture.second"));
            var hook = new FixtureHook(Registration(TrustLevel.Core, HookCapability.Restrict) with
            { Id = "fixture.after_tool_restrict", Point = LifecyclePoint.AfterTool },
                _ => throw new IOException("after-tool restriction failed closed"));
            var turn = new ExplorerTurn((_, _) =>
            {
                primaryCalls++;
                return primaryCalls == 1
                    ? new ModelResponse([
                        new ToolCallBlock(ToolCallId.New(), "first", "fixture.first", "{}"),
                        new ToolCallBlock(ToolCallId.New(), "second", "fixture.second", "{}"),
                    ], StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("must not be requested")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                ScriptedPermissionPolicy.WithTool("fixture.first", PermissionDecision.Allow), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                lifecycleHooks: [hook]);

            var result = turn.Ask("run tools", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal("lifecycle.hook.tool_outcome_withheld", result.FinalText);
            Assert.Equal(1, primaryCalls);
            Assert.Single(events.OfType<ToolCallStarted>());
            Assert.Single(events.OfType<ToolCallSucceeded>());
            Assert.DoesNotContain(events.OfType<ToolCallRequested>(), requested => requested.ToolName == "fixture.second");
            Assert.Contains(events.OfType<HookApplied>(), applied => applied.HookId == hook.Registration.Id
                && applied.Point == nameof(LifecyclePoint.AfterTool) && applied.Result == "failed_closed");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Non_core_hook_arguments_and_annotation_content_are_redacted_before_observation_and_context()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-hook-redaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string argumentSecret = "hook-argument-secret-24680";
        const string annotationSecret = "hook-annotation-secret-13579";
        _ = SecretRedactor.Shared;
        _ = Secret.Of(argumentSecret);
        _ = Secret.Of(annotationSecret);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var catalog = new FakeCatalog().Add(FakeTool.Read("filesystem.read"));
            var requests = new List<ModelRequest>();
            var observed = new FixtureHook(Registration(TrustLevel.Project, HookCapability.Observe) with
            { Id = "fixture.observe", Point = LifecyclePoint.BeforeTool }, _ => new HookContinue());
            var annotation = new FixtureHook(Registration(TrustLevel.Core, HookCapability.Annotate) with
            { Id = "fixture.annotation", Point = LifecyclePoint.BeforeModel }, _ => new HookAnnotate([
                new ContextItem("private", ContextItemKind.Knowledge, annotationSecret, 1,
                    ContextPriority.Low, RetentionPolicy.ConversationWindow,
                    new ContextProvenance("fixture", ContributionCategory.Knowledge, "fixture", ScopeLevel.BuiltIn, false))]));
            var calls = 0;
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                return calls++ == 0
                    ? new ModelResponse([new ToolCallBlock(ToolCallId.New(), "read", "filesystem.read",
                        "{\"path\":\"" + argumentSecret + "\"}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                ScriptedPermissionPolicy.WithTool("filesystem.read", PermissionDecision.Allow), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                lifecycleHooks: [observed, annotation]);

            _ = turn.Ask("hello", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.NotNull(observed.Received);
            Assert.DoesNotContain(argumentSecret, observed.Received!.RedactedArgumentsJson ?? "");
            Assert.Contains(SecretRedactor.Marker, observed.Received.RedactedArgumentsJson ?? "");
            var allInstructions = string.Join("\n", requests.Select(request => request.Instructions));
            Assert.DoesNotContain(annotationSecret, allInstructions);
            Assert.Contains(SecretRedactor.Marker, allInstructions);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Hooks_diagnostic_and_command_copy_are_complete_in_all_supported_locales()
    {
        var keys = new[] { "commands.hooks.help", "hooks.usage", "cli.diagnostic.hooks",
            "cli.diagnostic.hooks.untrusted", "cli.diagnostic.hook.no_entries",
            "cli.diagnostic.hook.state.enabled", "cli.diagnostic.hook.state.consentrequired",
            "cli.diagnostic.hook.state.workspacetrustrequired", "cli.diagnostic.hook.state.invalidmanifest",
            "cli.diagnostic.hook.state.incompatibleapi", "cli.diagnostic.hook.state.unsupportedpoint",
            "cli.diagnostic.hook.point" };
        foreach (var locale in UiLocaleCodes.Supported)
        {
            var localization = new Localization(locale);
            foreach (var key in keys)
            {
                var translated = localization.ResolveDetailed(key);
                Assert.False(translated.UsedFallback, $"{locale} fell back for {key}");
                Assert.Equal(locale, translated.ResourceLocale);
                Assert.NotEqual(key, translated.Text);
            }
        }
    }
}
