using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Sandbox;
using OmniCore.Tools;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class McpBridgeTests
{
    [Fact]
    public async Task Trusted_configured_mcp_server_reaches_model_tool_runtime_and_returns_attributed_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mcp-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverTask = ServeFixture(listener, 3, cancellation.Token);
        try
        {
            var paths = new DefaultPlatformPaths(Path.Combine(root, "data"));
            var config = Path.Combine(paths.ConfigDirectory, "mcp", "servers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "[{\"id\":\"local-fixture\",\"transport\":\"http\",\"endpoint\":\"http://127.0.0.1:"
                + port + "/mcp\",\"allowedHosts\":[\"127.0.0.1:" + port + "\"]}]");
            var store = new McpServerStore(paths, root, workspaceTrusted: false);
            Assert.Equal(TrustLevel.Untrusted, Assert.Single(store.List()).Trust);
            store.Trust("local-fixture", trusted: true);
            Assert.Equal(TrustLevel.Trusted, Assert.Single(store.List()).Trust);

            var discovered = store.DiscoverTools(new McpStrongStdioTransport(new UnusedLauncher()),
                new McpStreamableHttpTransport(), cancellation.Token);
            var tool = Assert.Single(discovered);
            Assert.Equal("mcp.local-fixture.fixture.lookup", tool.Descriptor.Id.ToString());
            Assert.False(tool.Descriptor.ReadOnly);
            Assert.Equal(EffectClass.NonIdempotent, tool.Descriptor.EffectClass);
            Assert.Equal(ToolRisk.High, tool.Descriptor.Risk);
            var requests = new List<ModelRequest>();
            var step = 0;
            var session = SessionId.New();
            var eventStore = new InMemoryEventStore();
            var run = TestRun.Open(eventStore, session);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var catalog = new FakeCatalog().Add(tool);
            catalog.Add(new ToolSearchTool(catalog));
            var executor = new ScriptedToolExecutor(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                { [tool.Descriptor.Id.ToString()] = PermissionDecision.Allow, ["tool.search"] = PermissionDecision.Allow }),
                root, boundary: null, audit: null, interactionResponder: _ => "allow_once", isInteractive: true);
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                if (step++ == 0)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "discover", "tool.search",
                        "{\"query\":\"fixture lookup\",\"maxResults\":2}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                if (step == 2)
                {
                    Assert.Contains(request.Tools, candidate => candidate.Name == tool.Descriptor.Id.ToString());
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "lookup-call",
                        tool.Descriptor.Id.ToString(), "{\"query\":\"MCP-SENSITIVE-ARG-9327\"}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                }
                var output = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()
                    .SelectMany(result => result.Content.OfType<TextBlock>()).Select(text => text.Text)
                    .FirstOrDefault(text => text.Contains("\"kind\":\"Mcp\"", StringComparison.Ordinal));
                Assert.NotNull(output);
                Assert.Contains("offline fixture result", output);
                return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "tools", "context", "observer", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Discovered, null), eventStore, codecs,
                artifacts, new InMemoryAuditSink(), new RedactionPolicy());

            var result = turn.Ask("use MCP", "fixture system", session, run.RunId, run.RootLane, "",
                cancellation.Token);
            Assert.True(result.StopReason == StopReason.EndTurn,
                result.StopReason + " / " + result.FailureDiagnostic + " / " + string.Join("; ", result.ToolCalls.Select(call => call.Summary)));
            Assert.Equal(3, requests.Count);
            SecretRedactorRegistry.Register("MCP-SENSITIVE-ARG-9327");
            var ask = Assert.Single(eventStore.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<InteractionRequested>(), item => item.Kind == InteractionKind.Permission);
            using (var subject = JsonDocument.Parse(ask.SubjectJson))
            {
                Assert.Equal("high", subject.RootElement.GetProperty("risk").GetString());
                Assert.Equal("mcp.first_effect", subject.RootElement.GetProperty("reason").GetString());
                Assert.Contains(subject.RootElement.GetProperty("details").EnumerateArray(), detail =>
                    detail.GetProperty("value").GetString()!.Contains("127.0.0.1", StringComparison.Ordinal));
                Assert.DoesNotContain("MCP-SENSITIVE-ARG-9327", ask.SubjectJson);
                Assert.DoesNotContain("toolPreferenceSubstitution", ask.SubjectJson);
            }
            foreach (var locale in new[] { "es", "en", "fr", "it" })
            {
                var text = new Localization(locale);
                var expected = text.ResolveDetailed("mcp.first_effect");
                Assert.False(expected.UsedFallback);
                var payload = JsonSerializer.Serialize(new
                {
                    type = "interaction.requested",
                    interactionId = ask.InteractionId.ToString(),
                    kind = "Permission",
                    options = "deny,allow_once",
                    subject = ask.SubjectJson,
                });
                var overlay = new ClientProjection(text).Apply(ClientState.Empty(),
                    WireEnvelope.Event(Ids.NewV7(), payload)).Overlays.Single();
                Assert.Contains(expected.Text, overlay.Subject);
                Assert.DoesNotContain("MCP-SENSITIVE-ARG-9327", overlay.Subject);
            }
            await serverTask;
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Project_consent_is_hash_bound_and_workspace_trust_gates_discovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mcp-consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DefaultPlatformPaths(Path.Combine(root, "data"));
            var file = Path.Combine(root, ".omnicore", "mcp.servers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var fixture = Path.GetFullPath(Path.Combine(root, "fixture.exe"));
            File.WriteAllText(file, "[{\"id\":\"repo-tool\",\"transport\":\"stdio\",\"command\":"
                + System.Text.Json.JsonSerializer.Serialize(fixture) + ",\"args\":[]}]");
            var untrustedWorkspace = new McpServerStore(paths, root, workspaceTrusted: false);
            Assert.Equal("project-untrusted-workspace", Assert.Single(untrustedWorkspace.List()).Scope);
            Assert.Throws<InvalidOperationException>(() => untrustedWorkspace.Trust("repo-tool", true));

            var trustedWorkspace = new McpServerStore(paths, root, workspaceTrusted: true);
            Assert.Equal("project-unconsented", Assert.Single(trustedWorkspace.List()).Scope);
            trustedWorkspace.Trust("repo-tool", true);
            Assert.Equal(TrustLevel.Project, Assert.Single(trustedWorkspace.List()).Trust);
            File.WriteAllText(file, "[{\"id\":\"repo-tool\",\"transport\":\"stdio\",\"command\":"
                + System.Text.Json.JsonSerializer.Serialize(Path.GetFullPath(Path.Combine(root, "changed.exe"))) + ",\"args\":[]}]");
            Assert.Equal("project-unconsented", Assert.Single(trustedWorkspace.List()).Scope);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Http_binding_pins_modern_headers_and_rejects_redirects_and_cancellation()
    {
        using var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(McpWire.Version, request.Headers.GetValues("MCP-Protocol-Version").Single());
            Assert.Equal("server/discover", request.Headers.GetValues("Mcp-Method").Single());
            Assert.Contains("application/json", request.Headers.Accept.Select(value => value.MediaType));
            Assert.Contains("text/event-stream", request.Headers.Accept.Select(value => value.MediaType));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                { Headers = { Location = new Uri("https://outside.invalid/mcp") } });
        });
        var server = new McpServerDefinition("remote", "http", null, [], null,
            "https://mcp.example.invalid/rpc", ["mcp.example.invalid"], true, "user", "identity");
        var transport = new McpStreamableHttpTransport(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.SendAsync(server, "server/discover", "{}", CancellationToken.None));

        using var stalled = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var cancelTransport = new McpStreamableHttpTransport(stalled);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelTransport.SendAsync(server,
            "server/discover", "{}", cancel.Token));
    }

    [Fact]
    public async Task Http_sse_returns_correlated_multiline_response_without_waiting_for_eof()
    {
        var sse = "event: progress\ndata: {\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{}}\n\n"
            + ": keepalive\nid: message-1\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\n"
            + "data: \"result\":{\"ok\":true}}\n\n";
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new OpenSseContent(sse),
        }));
        var server = new McpServerDefinition("sse", "http", null, [], null,
            "https://mcp.example.invalid/rpc", ["mcp.example.invalid"], true, "user", "identity");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await new McpStreamableHttpTransport(handler).SendAsync(server, "server/discover", "{}", deadline.Token);
        Assert.Contains("\"id\":1", result);
        using var parsed = JsonDocument.Parse(result);
        Assert.True(parsed.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Remote_trust_requires_explicit_user_marker_and_retrust_changes_approval_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mcp-trust-generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DefaultPlatformPaths(Path.Combine(root, "data"));
            var userFile = Path.Combine(paths.ConfigDirectory, "mcp", "servers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
            File.WriteAllText(userFile, "[{\"id\":\"remote\",\"transport\":\"http\",\"trusted\":true,"
                + "\"endpoint\":\"https://mcp.example.invalid/rpc\",\"allowedHosts\":[\"mcp.example.invalid\"]}]");
            var store = new McpServerStore(paths, root, workspaceTrusted: false);
            var untrusted = Assert.Single(store.List());
            Assert.Equal(TrustLevel.Untrusted, untrusted.Trust);
            store.Trust("remote", true);
            var firstTrust = Assert.Single(store.List());
            Assert.Equal(TrustLevel.Trusted, firstTrust.Trust);
            store.Trust("remote", false);
            Assert.Equal(TrustLevel.Untrusted, Assert.Single(store.List()).Trust);
            store.Trust("remote", true);
            var retrusted = Assert.Single(store.List());
            Assert.NotEqual(firstTrust.EffectiveIdentity, retrusted.EffectiveIdentity);
            static McpTool IdentityTool(McpServerDefinition definition, string schema) => new(definition, "lookup", "fixture",
                schema, false, new FixedTransport("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"), () => true);
            var baseApproval = IdentityTool(retrusted, "{\"type\":\"object\"}").FirstEffectApproval;
            Assert.NotEqual(baseApproval, IdentityTool(retrusted with { Identity = "changed-config" }, "{\"type\":\"object\"}").FirstEffectApproval);
            Assert.NotEqual(baseApproval, IdentityTool(retrusted with { AuthRef = "mcp:remote:auth:rotated" }, "{\"type\":\"object\"}").FirstEffectApproval);
            Assert.NotEqual(baseApproval, IdentityTool(retrusted, "{\"type\":\"string\"}").FirstEffectApproval);

            var projectFile = Path.Combine(root, ".omnicore", "mcp.servers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
            File.WriteAllText(projectFile, "[{\"id\":\"project-remote\",\"transport\":\"http\","
                + "\"endpoint\":\"https://mcp.example.invalid/rpc\",\"allowedHosts\":[\"mcp.example.invalid\"]}]");
            var projectStore = new McpServerStore(paths, root, workspaceTrusted: true);
            projectStore.Trust("project-remote", true);
            File.Delete(Path.Combine(paths.DataDirectory, "mcp", "trusted-remote.tsv"));
            Assert.Equal(TrustLevel.Untrusted, Assert.Single(projectStore.List(), item => item.Id == "project-remote").Trust);
            projectStore.Trust("project-remote", true);
            Assert.Equal(TrustLevel.Project, Assert.Single(projectStore.List(), item => item.Id == "project-remote").Trust);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Mcp_user_configuration_updates_are_validated_transactionally_and_preserve_other_credentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mcp-config-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DefaultPlatformPaths(Path.Combine(root, "data"));
            var store = new McpServerStore(paths, root, workspaceTrusted: false);
            store.AddHttp("first", "https://first.example/rpc");
            store.AddHttp("second", "https://second.example/rpc");
            store.SetCredentialReference("first", "mcp:first:auth:initial");
            store.SetCredentialReference("second", "mcp:second:auth:initial");
            var config = Path.Combine(paths.ConfigDirectory, "mcp", "servers.json");
            var beforeInvalid = File.ReadAllBytes(config);
            Assert.Throws<ArgumentException>(() => store.AddStdio("../invalid", Path.GetFullPath("fixture.exe"), []));
            Assert.Throws<ArgumentException>(() => store.AddHttp("bad id", "https://bad.example/rpc"));
            Assert.Equal(beforeInvalid, File.ReadAllBytes(config));
            store.SetCredentialReference("first", "mcp:first:auth:rotated");
            using var json = JsonDocument.Parse(File.ReadAllBytes(config));
            var entries = json.RootElement.EnumerateArray().ToArray();
            Assert.Equal("mcp:first:auth:rotated", entries.Single(item => item.GetProperty("id").GetString() == "first")
                .GetProperty("authRef").GetString());
            Assert.Equal("mcp:second:auth:initial", entries.Single(item => item.GetProperty("id").GetString() == "second")
                .GetProperty("authRef").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Mcp_json_rpc_isError_produces_failed_tool_outcome_with_unknown_effect()
    {
        var server = new McpServerDefinition("fixture", "http", null, [], null,
            "https://mcp.example.invalid/rpc", ["mcp.example.invalid"], true, "user", "identity");
        var bridge = new McpTool(server, "mutate", "fixture", "{\"type\":\"object\"}", false,
            new FixedTransport("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"isError\":true,\"content\":[]}}"), () => true);
        var catalog = new FakeCatalog().Add(bridge);
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                { [bridge.Descriptor.Id.ToString()] = PermissionDecision.Allow }), Path.GetTempPath());
        var outcome = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), bridge.Descriptor.Id,
            "error", "{}"), false, CancellationToken.None);
        Assert.False(outcome.Succeeded);
        Assert.NotEmpty(outcome.Events.OfType<ToolCallEffectUnknown>());
    }

    [Fact]
    public async Task Local_stdio_uses_a_real_controlled_fixture_under_Strong_and_propagates_error_and_cancel()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The real Strong stdio fixture requires Windows AppContainer.");
            return;
        }
        var capabilities = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!capabilities.StrongAvailable)
        {
            Assert.Skip("AppContainer unavailable: " + capabilities.StrongUnavailableReason);
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "omni-mcp-stdio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe");
            using var transport = new McpStrongStdioTransport(new WindowsAppContainerProcessLauncher());
            var server = new McpServerDefinition("stdio-fixture", "stdio", helper, ["mcp"], root, null,
                [], false, "user", "fixture-hash");
            var response = await transport.SendAsync(server, "server/discover", "{}", CancellationToken.None);
            var result = McpWire.Result(response, 1);
            Assert.Contains("2026-07-28", result.GetProperty("supportedVersions").GetRawText());

            var failing = server with { Args = ["mcp-error"] };
            await Assert.ThrowsAsync<McpRpcException>(() => transport.SendAsync(failing,
                "tools/list", "{}", CancellationToken.None));

            var stalled = server with { Args = ["mcp-stall"] };
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(stalled,
                "server/discover", "{}", cancel.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Configured_web_aliases_reuse_mcp_and_enforce_uri_result_and_byte_ceiling()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mcp-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var listener = new HttpListener();
        var port = FreePort(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var fixture = ServeWebFixture(listener, 6, cancellation.Token);
        try
        {
            var paths = new DefaultPlatformPaths(Path.Combine(root, "data"));
            var config = Path.Combine(paths.ConfigDirectory, "mcp", "servers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "[{\"id\":\"web-fixture\",\"transport\":\"http\","
                + "\"endpoint\":\"http://127.0.0.1:" + port + "/mcp\",\"allowedHosts\":[\"127.0.0.1:" + port + "\"],"
                + "\"web\":{\"searchTool\":\"lookup\",\"fetchTool\":\"fetch\",\"queryArgument\":\"term\","
                + "\"limitArgument\":\"limit\",\"uriArgument\":\"url\",\"bytesArgument\":\"bytes\","
                + "\"allowedTargets\":[\"https://allowed.example\"],\"maxResults\":2,\"maxBytes\":128," +
                "\"maxRedirects\":2}}]");
            var store = new McpServerStore(paths, root, workspaceTrusted: false);
            Assert.Equal(TrustLevel.Untrusted, Assert.Single(store.List()).Trust);
            store.Trust("web-fixture", trusted: true);
            var discovered = store.DiscoverTools(new McpStrongStdioTransport(new UnusedLauncher()),
                new McpStreamableHttpTransport(), cancellation.Token);
            Assert.Equal(2, discovered.Count);
            var search = Assert.IsType<McpWebTool>(discovered.Single(tool => tool.Descriptor.Id.ToString().EndsWith("web.search", StringComparison.Ordinal)));
            var fetch = Assert.IsType<McpWebTool>(discovered.Single(tool => tool.Descriptor.Id.ToString().EndsWith("web.fetch", StringComparison.Ordinal)));
            var preparedFetch = Assert.IsType<Prepared>(fetch.Prepare(new ValidatedToolCall(ToolCallId.New(),
                fetch.Descriptor.Id, "risk-contract", "{\"uri\":\"https://allowed.example/page\"}"),
                new ToolPreparationContext(root, DateTimeOffset.UtcNow)));
            Assert.Equal(fetch.Descriptor.Risk, preparedFetch.Intent.Risk);
            Assert.Equal(fetch.Descriptor.EffectClass, preparedFetch.Intent.Effect);
            var rejectedUri = Assert.IsType<PreparationRejected>(fetch.Prepare(new ValidatedToolCall(ToolCallId.New(),
                fetch.Descriptor.Id, "bad-uri", "{\"uri\":\"https://outside.example/\"}"),
                new ToolPreparationContext(root, DateTimeOffset.UtcNow)));
            Assert.Equal(ToolErrorCode.InvalidArguments, rejectedUri.ErrorCode);

            var hostTools = HostTools.Explorer();
            foreach (var tool in discovered) hostTools.RegisterMcpTool(tool);
            var catalog = hostTools.Catalog();
            Assert.Same(search, catalog.Find(search.Descriptor.Id));
            var policy = new ScriptedPermissionPolicy(
                new Dictionary<string, PermissionDecision>
                {
                    [search.Descriptor.Id.ToString()] = PermissionDecision.Allow,
                    [fetch.Descriptor.Id.ToString()] = PermissionDecision.Allow,
                    ["tool.search"] = PermissionDecision.Allow,
                });
            var executor = new ScriptedToolExecutor(catalog, policy, root, boundary: null, audit: null,
                interactionResponder: _ => "allow_once", isInteractive: true);
            var session = SessionId.New(); var eventStore = new InMemoryEventStore(); var run = TestRun.Open(eventStore, session);
            var artifacts = new FileArtifactStore(root); var step = 0;
            var turn = new ExplorerTurn((request, _) =>
            {
                if (step++ == 0)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "find-web", "tool.search",
                        "{\"query\":\"web search configured MCP source\",\"maxResults\":4}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                if (step == 2)
                {
                    Assert.Contains(request.Tools, tool => tool.Name == search.Descriptor.Id.ToString());
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "web-search",
                        search.Descriptor.Id.ToString(), "{\"query\":\"offline\",\"maxResults\":2}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                }
                var output = Assert.Single(request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()
                    .SelectMany(result => result.Content.OfType<TextBlock>()),
                    item => item.Text.Contains("\"results\"", StringComparison.Ordinal));
                using var parsed = JsonDocument.Parse(output.Text);
                Assert.Contains("allowed.example", parsed.RootElement.GetProperty("results")[0].GetProperty("uri").GetString());
                Assert.Contains("\t", parsed.RootElement.GetProperty("results")[0].GetProperty("title").GetString());
                return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "tools", "context", "observer", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Discovered, null), eventStore,
                EventCodecs.Create(), artifacts, new InMemoryAuditSink(), new RedactionPolicy());
            var modelResult = turn.Ask("search MCP web", "system", session, run.RunId, run.RootLane, "",
                cancellation.Token);
            Assert.Equal(StopReason.EndTurn, modelResult.StopReason);
            step = 0;
            var reopenedResult = turn.Ask("search MCP web again", "system", session, run.RunId, run.RootLane, "",
                cancellation.Token);
            Assert.Equal(StopReason.EndTurn, reopenedResult.StopReason);
            var approvals = eventStore.ReadFrom(session, 1).Select(EventCodecs.Create().Decode).ToArray();
            Assert.Single(approvals.OfType<ToolPreferenceSubstitutionApproved>());
            Assert.Single(approvals.OfType<PermissionRequested>());
            var fetchResult = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
                fetch.Descriptor.Id, "web-fetch", "{\"uri\":\"https://allowed.example/page\",\"maxBytes\":100}"),
                false, cancellation.Token);
            Assert.True(fetchResult.Succeeded, fetchResult.Summary);
            Assert.Contains("redirectChain", fetchResult.Preview);
            Assert.DoesNotContain("unauthorized content", fetchResult.Preview);
            var blockedRedirect = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
                fetch.Descriptor.Id, "web-crossed-redirect",
                "{\"uri\":\"https://allowed.example/cross\",\"maxBytes\":100}"), false, cancellation.Token);
            Assert.False(blockedRedirect.Succeeded);
            Assert.NotEmpty(blockedRedirect.Events.OfType<ToolCallEffectUnknown>());
            await fixture;
        }
        finally { cancellation.Cancel(); listener.Stop(); Directory.Delete(root, true); }
    }

    private static async Task ServeFixture(HttpListener listener, int requests, CancellationToken token)
    {
        for (var index = 0; index < requests; index++)
        {
            var context = await listener.GetContextAsync().WaitAsync(token);
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync(token);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(McpWire.Version, json.RootElement.GetProperty("params").GetProperty("_meta")
                .GetProperty(McpWire.MetadataVersion).GetString());
            Assert.Equal(McpWire.Version, context.Request.Headers["MCP-Protocol-Version"]);
            var method = json.RootElement.GetProperty("method").GetString();
            Assert.Equal(method, context.Request.Headers["Mcp-Method"]);
            var result = method switch
            {
                "server/discover" => "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{}}",
                "tools/list" => "{\"tools\":[{\"name\":\"fixture.lookup\",\"description\":\"offline lookup\",\"annotations\":{\"readOnlyHint\":true},\"inputSchema\":{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}}]}",
                "tools/call" => "{\"content\":[{\"type\":\"text\",\"text\":\"offline fixture result\"}]}",
                _ => throw new InvalidOperationException("Unexpected fixture method.")
            };
            var response = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + result + "}");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = response.Length;
            await context.Response.OutputStream.WriteAsync(response, token);
            context.Response.Close();
        }
    }

    private static async Task ServeWebFixture(HttpListener listener, int requests, CancellationToken token)
    {
        for (var index = 0; index < requests; index++)
        {
            var context = await listener.GetContextAsync().WaitAsync(token);
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            using var json = JsonDocument.Parse(await reader.ReadToEndAsync(token));
            Assert.Equal(McpWire.Version, json.RootElement.GetProperty("params").GetProperty("_meta")
                .GetProperty(McpWire.MetadataVersion).GetString());
            var method = json.RootElement.GetProperty("method").GetString();
            var name = json.RootElement.GetProperty("params").TryGetProperty("name", out var toolName)
                ? toolName.GetString() : null;
            var hasCallArgs = json.RootElement.GetProperty("params").TryGetProperty("arguments", out var callArgs);
            var callUri = hasCallArgs && callArgs.TryGetProperty("url", out var url) ? url.GetString() : null;
            if (method == "tools/call" && name == "fetch")
            {
                Assert.Equal(2, callArgs.GetProperty("maxRedirects").GetInt32());
                Assert.Contains("https://allowed.example", callArgs.GetProperty("allowedTargets").EnumerateArray()
                    .Select(item => item.GetString()));
            }
            var result = method switch
            {
                "server/discover" => "{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{}}",
                "tools/list" => "{\"tools\":[{\"name\":\"lookup\",\"inputSchema\":{\"type\":\"object\"}},"
                    + "{\"name\":\"fetch\",\"inputSchema\":{\"type\":\"object\"}}]}",
                "tools/call" when name == "lookup" =>
                    "{\"structuredContent\":{\"results\":[{\"uri\":\"https://allowed.example/one\",\"title\":\"control\\t\\u0001\",\"snippet\":\"s1\"},"
                    + "{\"uri\":\"https://allowed.example/two\",\"title\":\"two\",\"snippet\":\"s2\"},"
                    + "{\"uri\":\"https://outside.example/three\",\"title\":\"three\",\"snippet\":\"s3\"}]}}",
                "tools/call" when name == "fetch" && callUri == "https://allowed.example/cross" =>
                    "{\"structuredContent\":{\"finalUri\":\"https://allowed.example/final\",\"redirectChain\":[\"https://allowed.example/cross\",\"https://outside.example/hop\",\"https://allowed.example/final\"],\"content\":\"unauthorized content\"}}",
                "tools/call" when name == "fetch" =>
                    "{\"structuredContent\":{\"finalUri\":\"https://allowed.example/final\",\"redirectChain\":[\"https://allowed.example/page\",\"https://allowed.example/final\"],\"content\":\"bounded offline body\"}}",
                _ => throw new InvalidOperationException("Unexpected web fixture method or tool.")
            };
            var bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + result + "}");
            context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, token); context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private sealed class UnusedLauncher : ISandboxProcessLauncher
    {
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The HTTP fixture must not launch a local process.");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class FixedTransport(string response) : IMcpRequestTransport
    {
        public Task<string> SendAsync(McpServerDefinition server, string method, string parametersJson,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class OpenSseContent : HttpContent
    {
        private readonly byte[] _response;
        public OpenSseContent(string response)
        {
            _response = Encoding.UTF8.GetBytes(response);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new EventThenStallStream(_response));
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(_response).AsTask();
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context,
            CancellationToken cancellationToken) => stream.WriteAsync(_response, cancellationToken).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class EventThenStallStream(byte[] bytes) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - _offset);
                bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
                return ValueTask.FromResult(count);
            }
            return new ValueTask<int>(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ContinueWith<int>(_ => 0, cancellationToken, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default));
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
