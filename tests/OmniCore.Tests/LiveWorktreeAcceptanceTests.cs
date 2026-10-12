namespace OmniCore.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>
/// Explicit live acceptance for worktree.integrate. No model traffic occurs unless the caller
/// sets OMNICORE_LIVE_ACCEPTANCE_WORKTREE_MODEL to exactly "luna" or "qwen".
/// </summary>
public sealed class LiveWorktreeAcceptanceTests
{
    private const string LunaModelId = "gpt-6-luna";
    private const string QwenModelId = "qwen38-27b-abl-v9-best-q4kxl";

    [Fact]
    public async System.Threading.Tasks.Task Offline_fixture_runs_real_worktree_coordinator_through_tool_runtime()
    {
        var fixture = GitFixture.Create();
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        Assert.True(created.Succeeded, "offline_fixture_worktree_create_failed:" + created.Error);
        var identity = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(identity.WorktreePath, "integration-target.txt"),
            "offline accepted " + fixture.Marker + "\n", new UTF8Encoding(false));
        var previewResult = await store.PreviewIntegrationAsync(new(identity), CancellationToken.None);
        Assert.True(previewResult.Succeeded, "offline_fixture_preview_failed:" + previewResult.Error);
        var preview = Assert.IsType<WorktreeIntegrationPreview>(previewResult.Preview);
        var paths = preview.Changes.Select(change => change.RelativePath).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "integration-target.txt" }, paths);

        var artifacts = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var tool = new WorktreeIntegrationApplyTool(new PathBoundaryValidator(), coordinator);
        var catalog = new FakeCatalog().Add(tool);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>(StringComparer.Ordinal)
        {
            ["worktree.integrate"] = PermissionDecision.Allow,
        });
        var arguments = JsonSerializer.Serialize(new
        {
            ownershipId = identity.OwnershipId,
            proposalId = preview.ProposalId,
            paths,
        });
        Assert.True(IsExactFixtureCall(arguments, identity.OwnershipId, preview.ProposalId, paths));
        var events = new List<DomainEventPayload>();
        var runtime = new ToolRuntime(catalog, policy, payload =>
        {
            events.Add(payload);
            return VoidBox.Instance;
        });
        var validated = new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id,
            "offline-worktree-acceptance", arguments);
        var outcome = runtime.Run(validated, new ToolPreparationContext(fixture.Repository, DateTimeOffset.UtcNow),
            new ToolExecutionContext(fixture.Repository, readRegistry: null, emitEvent: payload => events.Add(payload), artifacts: artifacts,
                workspaceWriteLeases: HostWorkspaceWriteLeases.Shared), userApprovesAsk: false,
            CancellationToken.None);

        Assert.True(outcome.Succeeded, "offline_fixture_tool_runtime_failed:" + outcome.FinalState);
        Assert.Equal("offline accepted " + fixture.Marker + "\n",
            File.ReadAllText(Path.Combine(fixture.Repository, "integration-target.txt")));
        var started = Assert.Single(events.OfType<ToolCallStarted>());
        Assert.NotNull(started.BeforeStateRef);
        Assert.True(artifacts.Verify(started.BeforeStateRef!.Hash, started.BeforeStateRef.Size));
        Assert.Contains(events.OfType<WorktreeIntegrationStateRecorded>(), item =>
            item.State == WorktreeIntegrationState.Completed && item.ProposalId == preview.ProposalId
            && item.CompleteLaneResult);
    }

    [Fact]
    public async System.Threading.Tasks.Task Configured_model_requests_and_executes_real_worktree_integration_in_isolated_repo()
    {
        var alias = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_WORKTREE_MODEL");
        if (string.IsNullOrWhiteSpace(alias)) Assert.Skip("Set OMNICORE_LIVE_ACCEPTANCE_WORKTREE_MODEL=luna or qwen to opt in; no live request was made.");
        if (alias is not ("luna" or "qwen")) throw new InvalidOperationException("Opt-in model must be exactly luna or qwen.");

        var evidenceStartedAt = DateTimeOffset.UtcNow;
        string? fixtureRoot = null;
        var modelReturnedExactTool = false;
        var schemaGateValid = false;
        var pipelineExecuted = false;
        var resultReturnedToModel = false;
        string? argumentHash = null;
        try
        {
        using var totalBudget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var modelId = alias == "luna" ? LunaModelId : QwenModelId;
        // The test assembly isolates default paths globally. Explicit, existing paths are
        // required for this opt-in connection only; never undo isolation for other tests.
        var paths = ExistingLiveConnectionPaths();
        var registry = OmniHost.LoadUserModelRegistry(paths);
        var model = registry.Model(modelId) ?? throw new InvalidOperationException("configured_acceptance_model_missing");
        var providerDescriptor = registry.Provider(model.ProviderId)
            ?? throw new InvalidOperationException("configured_acceptance_provider_missing");
        var provider = ConnectConfiguredProvider(alias!, modelId, model, providerDescriptor, paths);

        var fixture = GitFixture.Create(); // Preserved for inspection; never removes or cleans user data.
        fixtureRoot = fixture.Root;
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var created = await store.CreateAsync(fixture.Request(), totalBudget.Token);
        Assert.True(created.Succeeded, "temporary_git_worktree_creation_failed:" + created.Error);
        var identity = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(identity.WorktreePath, "integration-target.txt"),
            "accepted " + fixture.Marker + "\n", new UTF8Encoding(false));
        var previewResult = await store.PreviewIntegrationAsync(new(identity), totalBudget.Token);
        Assert.True(previewResult.Succeeded, "temporary_git_worktree_preview_failed:" + previewResult.Error);
        var preview = Assert.IsType<WorktreeIntegrationPreview>(previewResult.Preview);
        var pathsToIntegrate = preview.Changes.Select(change => change.RelativePath)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "integration-target.txt" }, pathsToIntegrate);

        var artifactStore = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifactStore, store);
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(),
            includeSimulationTools: false, includeMutationTools: true, includeProcessTools: false,
            processSandboxStrength: OmniCore.Sandbox.SandboxStrength.Strong,
            artifactReadTool: null, includeControlTools: false, includeMailboxReceive: false,
            includeShellTool: false, worktreeIntegrationCoordinator: coordinator);
        var catalog = hostTools.Catalog();
        var tool = Assert.IsType<WorktreeIntegrationApplyTool>(catalog.Find(new ToolId("worktree.integrate")));
        var allDecisions = catalog.RegisteredTools().ToDictionary(
            item => item.Descriptor.Id.ToString(), _ => PermissionDecision.Deny, StringComparer.Ordinal);
        allDecisions["worktree.integrate"] = PermissionDecision.Allow;
        var permissionPolicy = new ScriptedPermissionPolicy(allDecisions);
        var toolDefinition = new ToolDefinition(tool.Descriptor.Id.ToString(), tool.Descriptor.Description,
            tool.Descriptor.InputSchema.ToString());
        var prompt = "Call worktree.integrate exactly once with the ownershipId, proposalId, and complete paths "
            + "given here; do not call any other tool and do not claim success before the returned result. "
            + "ownershipId=" + identity.OwnershipId + "; proposalId=" + preview.ProposalId
            + "; paths=" + JsonSerializer.Serialize(pathsToIntegrate) + ".";
        var request = new ModelRequest(
            new ModelSelection(new ModelIdValue(modelId), model.ContextWindow, ToolMode.Direct,
                reasoning: null, maxOutputTokens: alias == "luna" ? null : model.MaxOutputTokens),
            [new ModelMessage(MessageRole.User, [new TextBlock(prompt)])],
            "Only the supplied tool may be called. Use exact fixture values from the user message. Do not access external resources.",
            catalog.RegisteredTools().Select(item => new ToolDefinition(item.Descriptor.Id.ToString(),
                item.Descriptor.Description, item.Descriptor.InputSchema.ToString())).ToArray(),
            ToolChoice.Auto(), null, null, null, null);

        var response = await Complete(provider, request, totalBudget.Token);
        var calls = response.Content.OfType<ToolCallBlock>().ToArray();
        Assert.Single(calls);
        var modelCall = calls[0];
        Assert.Equal("worktree.integrate", modelCall.ToolName);
        modelReturnedExactTool = true;
        argumentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelCall.ArgumentsJson))).ToLowerInvariant();
        Assert.True(IsExactFixtureCall(modelCall.ArgumentsJson, identity.OwnershipId,
            preview.ProposalId, pathsToIntegrate), "model_arguments_outside_exact_fixture_gate");
        schemaGateValid = true;

        var callId = modelCall.Id;
        var validated = new ValidatedToolCall(callId, tool.Descriptor.Id,
            modelCall.ProviderCallId ?? "live-worktree-acceptance", modelCall.ArgumentsJson);
        var events = new List<DomainEventPayload>();
        var runtime = new ToolRuntime(catalog, permissionPolicy, payload =>
        {
            events.Add(payload);
            return VoidBox.Instance;
        });
        var context = new ToolExecutionContext(fixture.Repository, readRegistry: null,
            emitEvent: payload => events.Add(payload), artifacts: artifactStore,
            workspaceWriteLeases: HostWorkspaceWriteLeases.Shared);
        var outcome = runtime.Run(validated, new ToolPreparationContext(fixture.Repository, DateTimeOffset.UtcNow),
            context, userApprovesAsk: false, totalBudget.Token);
        pipelineExecuted = events.Any(item => item is ToolCallStarted or ToolCallSucceeded);
        schemaGateValid = events.Any(item => item is ToolCallPrepared);

        Assert.True(outcome.Succeeded, "tool_runtime_integration_failed:" + outcome.FinalState);
        Assert.Equal(EffectOutcome.Applied, outcome.Effect);
        Assert.Equal("accepted " + fixture.Marker + "\n",
            File.ReadAllText(Path.Combine(fixture.Repository, "integration-target.txt")));
        Assert.Equal(fixture.HeadBefore, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Empty(fixture.Git("diff", "--cached", "--name-only").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var started = Assert.Single(events.OfType<ToolCallStarted>());
        Assert.NotNull(started.BeforeStateRef);
        Assert.True(artifactStore.Verify(started.BeforeStateRef!.Hash, started.BeforeStateRef.Size));
        var completed = Assert.Single(events.OfType<WorktreeIntegrationStateRecorded>(), item =>
            item.State == WorktreeIntegrationState.Completed);
        Assert.True(completed.CompleteLaneResult);
        Assert.Equal(preview.ProposalId, completed.ProposalId);

        var receipt = "INTEGRATION-RESULT-" + Guid.NewGuid().ToString("N");
        var followUp = new ModelRequest(request.Model,
            [new ModelMessage(MessageRole.User, [new TextBlock(prompt)]),
                new ModelMessage(MessageRole.Assistant, response.Content),
                new ModelMessage(MessageRole.Tool, [new ToolResultBlock(modelCall.Id,
                    [new TextBlock(JsonSerializer.Serialize(new { summary = outcome.Summary,
                        output = outcome.Preview, verificationReceipt = receipt }))], false)])],
            "Describe the actual integration result and include its exact verificationReceipt. No more tool calls.",
            [], ToolChoice.None(), null, null, null, response.State);
        var roundTrip = await Complete(provider, followUp, totalBudget.Token);
        Assert.Empty(roundTrip.Content.OfType<ToolCallBlock>());
        Assert.Contains(receipt, string.Concat(roundTrip.Content.OfType<TextBlock>().Select(item => item.Text)), StringComparison.Ordinal);
        resultReturnedToModel = true;

        var evidenceDirectory = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) evidenceDirectory = Path.Combine(fixture.Root, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var evidence = new
        {
            model = alias,
            modelId,
            caseId = "worktree.integrate",
            status = "success",
            modelRequestedExactTool = modelReturnedExactTool,
            schemaAndFixtureGateValid = true,
            argumentsSha256 = argumentHash,
            pipelineExecuted = true,
            pipelineState = outcome.FinalState.ToString(),
            effect = outcome.Effect.ToString(),
            effectVerified = true,
            hostIntegrationState = completed.State.ToString(),
            integratedPaths = pathsToIntegrate,
            protectedManifestVerified = true,
            resultReturnedToModel,
            generationInvocations = 2,
            invocationMode = "auto_with_host_catalog_fixture_only",
            startedAt = evidenceStartedAt,
            completedAt = DateTimeOffset.UtcNow,
            fixtureRepositoryPreserved = fixture.Repository,
        };
        File.WriteAllText(Path.Combine(evidenceDirectory, "worktree-integrate-" + alias + ".json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch (Exception failure)
        {
            if (fixtureRoot is not null)
            {
                var failureEvidenceDirectory = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_EVIDENCE_DIR");
                if (string.IsNullOrWhiteSpace(failureEvidenceDirectory))
                    failureEvidenceDirectory = Path.Combine(fixtureRoot, "evidence");
                Directory.CreateDirectory(failureEvidenceDirectory);
                var failureEvidence = new
                {
                    model = alias,
                    modelId = alias == "luna" ? LunaModelId : QwenModelId,
                    caseId = "worktree.integrate",
                    status = "failed",
                    modelRequestedExactTool = modelReturnedExactTool,
                    schemaAndFixtureGateValid = schemaGateValid,
                    argumentsSha256 = argumentHash,
                    pipelineExecuted,
                    resultReturnedToModel,
                    effectVerified = false,
                    failureCode = SafeFailureCode(failure),
                    startedAt = evidenceStartedAt,
                    completedAt = DateTimeOffset.UtcNow,
                    fixtureRepositoryPreserved = fixtureRoot,
                };
                File.WriteAllText(Path.Combine(failureEvidenceDirectory,
                        "worktree-integrate-" + alias + ".json"),
                    JsonSerializer.Serialize(failureEvidence, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            throw new InvalidOperationException("live_worktree_acceptance_failed:" + SafeFailureCode(failure));
        }
    }

    private static IPlatformPaths ExistingLiveConnectionPaths()
    {
        var data = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_DATA_DIR");
        var config = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(config)
            || !Path.IsPathFullyQualified(data) || !Path.IsPathFullyQualified(config)
            || !Directory.Exists(data) || !Directory.Exists(config))
            throw new InvalidOperationException("live_acceptance_existing_connection_paths_required");
        return new DefaultPlatformPaths(data, config);
    }

    private static bool IsExactFixtureCall(string json, string owner, string proposal, string[] expectedPaths)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || !root.TryGetProperty("ownershipId", out var ownerNode) || ownerNode.ValueKind != JsonValueKind.String
                || ownerNode.GetString() != owner
                || !root.TryGetProperty("proposalId", out var proposalNode) || proposalNode.ValueKind != JsonValueKind.String
                || proposalNode.GetString() != proposal
                || !root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array
                || paths.GetArrayLength() != expectedPaths.Length) return false;
            return paths.EnumerateArray().Select(node => node.ValueKind == JsonValueKind.String ? node.GetString() : null)
                .SequenceEqual(expectedPaths, StringComparer.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private static async Task<ModelResponse> Complete(IModelProvider provider, ModelRequest request,
        CancellationToken cancellationToken)
    {
        ModelResponse? response = null;
        await foreach (var item in provider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            if (item is ResponseCompleted completed) response = completed.Response;
        return response ?? throw new InvalidOperationException("live_acceptance_response_incomplete");
    }

    private static string SafeFailureCode(Exception exception) => exception switch
    {
        ModelProviderException provider => "provider_kind:" + new string(provider.Kind.Where(char.IsAsciiLetterOrDigit).Take(48).ToArray())
            + ";status_code:" + (provider.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"),
        ChatGptAuthException auth => "chatgpt_auth_kind:" + new string(auth.Kind.Where(char.IsAsciiLetterOrDigit).Take(48).ToArray()),
        HttpRequestException => "transport_failure",
        OperationCanceledException => "bounded_timeout_or_cancellation",
        _ => "exception_type:" + exception.GetType().Name,
    };

    private static IModelProvider ConnectConfiguredProvider(string alias, string modelId,
        ModelDefinition model, ProviderDescriptor provider, IPlatformPaths paths)
    {
        if (model.Id != modelId || !provider.SupportsNativeToolCalls)
            throw new InvalidOperationException("configured_model_or_native_tools_mismatch");
        if (alias == "luna")
        {
            if (provider.Family != ProviderFamily.OpenAIResponses || provider.Profile != "codex"
                || provider.BillingMode != BillingMode.IncludedQuota)
                throw new InvalidOperationException("configured_luna_subscription_profile_mismatch");
            var auth = OmniHost.CreateChatGptAuth(paths);
            EnsureFreshSubscription(auth);
            return OmniHost.ConnectProvider(provider, provider.BaseUrl, provider.Auth.SecretRef ?? "", "",
                subscription: new NoRefreshSubscriptionSource(auth));
        }

        if (provider.Family != ProviderFamily.OpenAiChatCompatible || provider.BillingMode != BillingMode.Local
            || provider.LocalHost?.Mode != LocalHostMode.Attach
            || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host != "192.168.1.104" || endpoint.Port != 8080
            || endpoint.AbsolutePath.TrimEnd('/') != "/v1" || string.IsNullOrWhiteSpace(provider.TrustedCertificatePath)
            || !File.Exists(provider.TrustedCertificatePath))
            throw new InvalidOperationException("configured_qwen_local_attach_or_tls_mismatch");
        var apiKey = provider.Auth.Kind == AuthKind.ApiKey
            ? OmniHost.ResolveApiKey(OmniHost.CreateUserCredentialStore(paths), provider.Auth.SecretRef ?? "",
                environmentKey: null, CancellationToken.None)
            : null;
        if (provider.Auth.Kind == AuthKind.ApiKey && string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("configured_qwen_secret_missing");
        if (provider.Auth.Kind is not (AuthKind.None or AuthKind.ApiKey))
            throw new InvalidOperationException("configured_qwen_auth_kind_not_supported");
        return OmniHost.ConnectProvider(provider, provider.BaseUrl, provider.Auth.SecretRef ?? "", apiKey ?? "");
    }

    private static void EnsureFreshSubscription(ChatGptSubscriptionAuthProvider auth)
    {
        var status = auth.Status(CancellationToken.None);
        if (!status.LoggedIn || status.Expired || status.ExpiresAt is not { } expiry
            || expiry - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("configured_luna_subscription_not_fresh_enough_without_refresh");
    }

    private sealed class NoRefreshSubscriptionSource(ChatGptSubscriptionAuthProvider auth) : ISubscriptionCredentialSource
    {
        public ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureFreshSubscription(auth);
            return auth.GetAsync(cancellationToken);
        }

        public ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<SubscriptionCredential>(new InvalidOperationException("live_acceptance_refresh_disabled"));
    }

    private sealed class GitFixture
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "OmniCoreLiveWorktreeAcceptance", Guid.NewGuid().ToString("N"));
        public string Repository => Path.Combine(Root, "repository with spaces");
        public string Data => Path.Combine(Root, "data");
        public string Marker { get; } = "LIVE-WORKTREE-" + Guid.NewGuid().ToString("N");
        public string HeadBefore { get; private set; } = "";

        private GitFixture() { Directory.CreateDirectory(Repository); Directory.CreateDirectory(Data); }

        public static GitFixture Create()
        {
            var fixture = new GitFixture();
            fixture.Git("init", "--quiet", "--initial-branch=main");
            fixture.Git("config", "core.autocrlf", "false");
            fixture.Git("config", "core.hooksPath", Path.Combine(fixture.Root, "disabled-hooks"));
            fixture.Git("config", "commit.gpgSign", "false");
            fixture.Write("integration-target.txt", "base " + fixture.Marker + "\n");
            fixture.CommitAll("base fixture");
            fixture.HeadBefore = fixture.Git("rev-parse", "HEAD").Trim();
            return fixture;
        }

        public WorktreeCreateRequest Request() => new(WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(Repository)),
            LaneId.New(), Repository, Data);

        private void Write(string path, string text)
        {
            var full = Path.GetFullPath(path, Repository);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }

        private void CommitAll(string message)
        {
            Git("add", "-A");
            Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", message);
        }

        public string Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = Repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("fixture_git_start_failed");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("fixture_git_timed_out");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new InvalidOperationException("fixture_git_failed:" + process.ExitCode + ":" + stderr);
            return stdout;
        }
    }
}
