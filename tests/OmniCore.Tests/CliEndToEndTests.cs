using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>Smoke tests through the real CLI dispatcher, persistent Host and provider HTTP adapter.</summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CliEndToEndTests
{
    [Fact]
    public async Task Routed_override_invokes_logical_model_and_persists_the_selected_physical_route()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "providers.yaml"), """
                providers:
                  scripted: { family: OpenAiChatCompatible, baseUrl: http://127.0.0.1:1/v1, auth: none, billingMode: Local }
                """);
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  scripted-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [worker] }
                routing:
                  exploration: [worker]
                """);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", provider.BaseUrl);
            string? requestBody = null;
            provider.RespondWith((_, request) => { requestBody = request; return TextResponse("ruta física seleccionada"); });
            var host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            var output = new List<string>();
            Assert.True(0 == await host.ExecuteAsync("hola por ruta override", output.Add, TestContext.Current.CancellationToken),
                string.Join("\n", output));
            Assert.Equal(1, provider.RequestCount);
            Assert.Contains("\"model\":\"scripted-model\"", requestBody);
            var expected = new ModelRoute("scripted", provider.BaseUrl, ProviderFamily.OpenAiChatCompatible, null, "scripted-model");
            var started = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<ModelStepStarted>());
            Assert.Equal("scripted-model", started.ModelId);
            Assert.Equal(expected.Id, started.RouteId);
            Assert.NotEqual(started.ModelId, started.RouteId!.Value);
            var turn = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<TurnStarted>());
            Assert.NotNull(turn.Fingerprint);
            Assert.Equal(RuntimeBuildIdentity.ForAssembly(typeof(OmniCliRuntime).Assembly), turn.Fingerprint.Build);
            Assert.Equal(new[] { "context.policy", "model.descriptor", "model.harness", "model.profile",
                "plan.revision", "prompt.template", "provider.adapter", "runtime.build", "tools.plan" },
                turn.Fingerprint.Components.Select(component => component.Name));
            Assert.NotEqual("core-tools-1", turn.Fingerprint.ToolkitHash);
            var adapter = Assert.Single(turn.Fingerprint.Components, component => component.Name == "provider.adapter");
            Assert.Equal("2", adapter.Version);
            using var adapterMetadata = new MemoryStream();
            using (var writer = new Utf8JsonWriter(adapterMetadata))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("route");
                using (var routeJson = JsonDocument.Parse(expected.CanonicalJson()))
                    routeJson.RootElement.WriteTo(writer);
                writer.WriteString("providerType", typeof(OpenAiChatCompatibleProvider).FullName);
                writer.WriteString("providerBuild", RuntimeBuildIdentity.ForAssembly(typeof(OpenAiChatCompatibleProvider).Assembly));
                writer.WriteEndObject();
            }
            Assert.Equal(ContentHash.Sha256(Convert.ToHexStringLower(SHA256.HashData(
                adapterMetadata.ToArray()))), adapter.Hash);
            Assert.All(turn.Fingerprint.Components, component => Assert.Null(component.Content));
        });
    }

    [Fact]
    public async Task Authorized_auto_escalation_reuses_run_and_marks_completed_only_after_target_response()
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  tiny-model: { provider: scripted, context: 4, recommendedUsableContext: 4, maxOutput: 1, aliases: [tiny] }
                  target-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [target] }
                routing:
                  exploration: [tiny]
                  escalation: { mode: auto, chain: [tiny, target] }
                """);
            var host = new TuiTurnHost(OmniCliRuntime.Create(workspace));
            var diagnostics = new List<string>();
            Assert.True(0 == await host.ExecuteAsync("intención auto original", diagnostics.Add, TestContext.Current.CancellationToken),
                string.Join("\n", diagnostics));
            Assert.Equal(1, provider.RequestCount);
            var events = ReadCurrentSessionEvents(workspace);
            var run = Assert.Single(events.OfType<RunCreated>());
            var requested = Assert.Single(events.OfType<ModelEscalationRequested>());
            var approved = Assert.Single(events.OfType<ModelEscalationApproved>());
            var completed = Assert.Single(events.OfType<ModelEscalationCompleted>());
            Assert.Equal(run.RunId, requested.RunId);
            Assert.Equal(run.RunId, approved.RunId);
            Assert.Equal(run.RunId, completed.RunId);
            Assert.Equal(requested.TurnId, approved.TurnId);
            Assert.Equal(requested.TurnId, completed.TurnId);
            Assert.Equal("target-model", Assert.Single(events.OfType<ModelStepStarted>()).ModelId);
            Assert.DoesNotContain(events.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
            Assert.True(events.ToList().FindIndex(item => item is ModelStepCompleted)
                < events.ToList().FindIndex(item => item is ModelEscalationCompleted));
        });
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("endpoint")]
    [InlineData("billing")]
    public async Task Escalation_resume_denies_rejected_or_changed_route_before_http(string change)
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  tiny-model: { provider: scripted, context: 4, recommendedUsableContext: 4, maxOutput: 1, aliases: [tiny] }
                  target-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [target] }
                routing:
                  exploration: [tiny]
                  escalation: { mode: ask, chain: [tiny, target] }
                """);
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            Assert.Equal(3, await host.ExecuteAsync("original", _ => { }, TestContext.Current.CancellationToken));
            var consent = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<InteractionRequested>(),
                item => item.Kind == InteractionKind.ModelRouteConsent);
            var option = change == "deny" ? "deny" : "allow_route";
            Assert.Equal("ok", client.Send(OmniCore.Protocol.WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"interaction.respond\",\"interactionId\":\"" + consent.InteractionId
                + "\",\"optionId\":\"" + option + "\"}"), CancellationToken.None).Status);
            if (change == "endpoint") Environment.SetEnvironmentVariable("OMNI_BASE_URL", "http://127.0.0.1:1/v1");
            if (change == "billing")
                File.WriteAllText(Path.Combine(config, "providers.yaml"),
                    "providers:\n  scripted: { family: OpenAiChatCompatible, baseUrl: '" + provider.BaseUrl + "', auth: none, billingMode: Unknown }\n");
            Assert.Equal(1, await host.ResumeEscalationAsync(consent.InteractionId.ToString(), _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(0, provider.RequestCount);
            var events = ReadCurrentSessionEvents(workspace);
            Assert.DoesNotContain(events, item => item is ModelStepStarted or ModelEscalationApproved or ModelEscalationCompleted);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task User_consented_ask_escalation_resumes_exact_target_and_original_input_once(bool reopenRuntime)
    {
        await InIsolatedCli(async (workspace, config, data, provider) =>
        {
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  tiny-model: { provider: scripted, context: 4, recommendedUsableContext: 4, maxOutput: 1, aliases: [tiny] }
                  target-model: { provider: scripted, context: 8192, maxOutput: 2048, aliases: [target] }
                routing:
                  exploration: [tiny]
                  escalation: { mode: ask, chain: [tiny, target] }
                """);
            string? received = null;
            provider.RespondWith((_, request) => { received = request; return TextResponse("respuesta del destino autorizado"); });
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            Assert.Equal(3, await host.ExecuteAsync("intención original durable", _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(0, provider.RequestCount);
            var before = ReadCurrentSessionEvents(workspace);
            var requested = Assert.Single(before.OfType<ModelEscalationRequested>());
            Assert.NotNull(requested.TurnId);
            Assert.NotNull(requested.LaneId);
            var consent = Assert.Single(before.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
            var inputCount = before.OfType<UserInputReceived>().Count();
            Assert.Equal(1, inputCount);
            Assert.Equal("ok", client.Send(OmniCore.Protocol.WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"follow-up reservado para después\"}"), CancellationToken.None).Status);
            Assert.Equal("ok", client.Send(OmniCore.Protocol.WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"interaction.respond\",\"interactionId\":\"" + consent.InteractionId
                + "\",\"optionId\":\"allow_route\"}"), CancellationToken.None).Status);
            if (reopenRuntime)
            {
                runtime = OmniCliRuntime.Create(workspace);
                client = runtime.Connect(CancellationToken.None);
                host = new TuiTurnHost(runtime);
            }
            var diagnostics = new List<string>();
            var code = await host.ResumeEscalationAsync(consent.InteractionId.ToString(), diagnostics.Add, TestContext.Current.CancellationToken);
            Assert.True(code == 0, string.Join("\n", diagnostics));
            Assert.Equal(1, provider.RequestCount);
            Assert.NotNull(received);
            Assert.Contains("target-model", received);
            Assert.Contains("intención original durable", Regex.Unescape(received));
            Assert.DoesNotContain("follow-up reservado", Regex.Unescape(received));
            var after = ReadCurrentSessionEvents(workspace);
            Assert.Single(after.OfType<ModelEscalationApproved>());
            Assert.Single(after.OfType<ModelEscalationCompleted>());
            Assert.Single(after.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
            Assert.All(after.OfType<ModelStepStarted>(), step => Assert.Equal("target-model", step.ModelId));
            Assert.Equal(inputCount, after.OfType<UserInputReceived>().Count());
            var queued = Assert.Single(after.OfType<FollowUpQueued>());
            Assert.DoesNotContain(after.OfType<FollowUpPromoted>(), item => item.FollowUpId == queued.FollowUpId);
            Assert.Equal(1, await host.ResumeEscalationAsync(consent.InteractionId.ToString(), _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal(1, provider.RequestCount);
        });
    }

    [Fact]
    public async Task Tui_chat_rejects_file_writes_at_the_tool_boundary()
    {
        await InIsolatedCli(async (workspace, data, config, provider) =>
        {
            provider.RespondWith((index, _) => index == 0
                ? ToolCallResponse("unrequested-write", "filesystem.write", "{\"path\":\"unrequested.txt\",\"content\":\"must not exist\"}")
                : TextResponse("Ejemplo entregado aquí en la conversación"));
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            var diagnostics = new List<string>();
            var exit = await host.ExecuteAsync("Dame un ejemplo aquí, no archivos", diagnostics.Add, TestContext.Current.CancellationToken);
            Assert.Equal(0, exit);
            Assert.False(File.Exists(Path.Combine(workspace, "unrequested.txt")));
            var events = client.SubscribeSince(1).ToArray();
            Assert.Contains(events, evt => evt.PayloadJson.Contains("toolcall.rejected", StringComparison.Ordinal));
            Assert.Contains(events, evt => evt.PayloadJson.Contains("assistant_message.recorded", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Tui_follow_up_sends_previous_run_user_and_assistant_text_to_the_provider()
    {
        await InIsolatedCli(async (workspace, data, config, provider) =>
        {
            provider.RespondWith((_, _) => TextResponse("respuesta anterior única"));
            var runtime = OmniCliRuntime.Create(workspace);
            var host = new TuiTurnHost(runtime);
            Assert.Equal(0, await host.ExecuteActAsync("pregunta anterior única", _ => { }, TestContext.Current.CancellationToken));
            string? received = null;
            provider.RespondWith((_, request) => { received = request; return TextResponse("continuación con contexto"); });
            var messages = new List<string>();
            Assert.True(0 == await host.ExecuteAsync("continúa el ejemplo anterior", messages.Add, TestContext.Current.CancellationToken), string.Join("\n", messages));
            Assert.NotNull(received);
            var decoded = System.Text.RegularExpressions.Regex.Unescape(received!);
            Assert.Contains("pregunta anterior", decoded);
            Assert.Contains("respuesta anterior", decoded);
            Assert.Contains("continúa el ejemplo anterior", decoded);
        });
    }

    [Fact]
    public async Task Tui_turn_host_executes_real_runtime_http_and_journals_assistant_response()
    {
        await InIsolatedCli(async (workspace, data, config, provider) =>
        {
            var runtime = OmniCliRuntime.Create(workspace);
            var client = runtime.Connect(CancellationToken.None);
            var host = new TuiTurnHost(runtime);
            var messages = new List<string>();
            var code = await host.ExecuteAsync("hola", messages.Add, CancellationToken.None);
            Assert.True(code == 0, string.Join("\n", messages));
            Assert.True(provider.RequestCount > 0, "TUI Host debe consultar realmente el proveedor HTTP");
            var identity = client.Query("sessionIdentity", CancellationToken.None);
            Assert.NotNull(identity);
            Assert.Contains("respuesta-scripted", string.Join("\n", messages));
            Assert.Contains(client.SubscribeSince(1), envelope => envelope.PayloadJson.Contains("assistant_message.recorded"));
            var user = Assert.Single(ReadCurrentSessionEvents(workspace).OfType<UserInputReceived>());
            Assert.Contains("hola", user.InputPartsJson);
        });
    }
    [Fact]
    public async Task M1_to_M4_cli_surface_runs_in_isolated_directories()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), "omnicore-cli-e2e-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);

        var previousDirectory = Environment.CurrentDirectory;
        var priorData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var priorConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var priorLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        var priorModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        OmniCliRuntime? previousRuntime = null;
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", "es");
            Environment.SetEnvironmentVariable("OMNI_MODEL", null);

            await using var provider = new ScriptedHttpProvider();
            File.WriteAllText(Path.Combine(config, "providers.yaml"), string.Join(Environment.NewLine,
                "providers:", "  scripted:", "    family: OpenAiChatCompatible", "    baseUrl: " + provider.BaseUrl,
                "    auth: none", "    billingMode: Local", ""));
            File.WriteAllText(Path.Combine(config, "models.yaml"), string.Join(Environment.NewLine,
                "models:", "  scripted-model:", "    provider: scripted", "    context: 8192",
                "    maxOutput: 2048", ""));
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
            previousRuntime = CliApp.UseRuntimeForTests(OmniCliRuntime.Create(workspace));

            // M1: default and JSON renderers, all checked-in scenario files, and crash/recovery.
            var sim = await Run("sim");
            Assert.Equal(0, sim.Code);
            Assert.Contains("Run iniciado:", sim.Output);
            AssertNoLeaks(sim.Output);

            var simJson = await Run("sim", "--json");
            Assert.Equal(0, simJson.Code);
            Assert.Contains("\"type\"", simJson.Output);
            Assert.Contains("\"outcome\"", simJson.Output);
            AssertNoLeaks(simJson.Output, machineReadable: true);

            var scenarios = Path.Combine(repositoryRoot, "docs", "sim");
            foreach (var file in Directory.GetFiles(scenarios, "*.yaml").OrderBy(path => path, StringComparer.Ordinal))
            {
                var result = await Run("sim", file, "--json");
                // crash-resume.yaml models a successful interrupted state; the explicit --crash
                // flag below is the command-level interrupted exit path.
                const int expectedExit = 0;
                Assert.True(result.Code == expectedExit,
                    $"{Path.GetFileName(file)} expected exit={expectedExit}, actual={result.Code}: {result.Output}");
                Assert.Contains("\"outcome\"", result.Output);
                AssertNoLeaks(result.Output, machineReadable: true);
            }

            var crash = await Run("sim", "--crash", "--json");
            Assert.Equal(1, crash.Code);
            Assert.Contains("Interrupted", crash.Output);
            AssertNoLeaks(crash.Output, machineReadable: true);
            var resumed = await Run("sim", "--resume", "--json");
            Assert.Equal(0, resumed.Code);
            Assert.Contains("sim.resumed", resumed.Output);
            Assert.Contains("\"outcome\"", resumed.Output);
            AssertNoLeaks(resumed.Output, machineReadable: true);

            // M1 scenarios can deliberately leave a live Question interaction. Ordinary input must
            // not resolve it (FollowUp contract). Start the independent HTTP smoke in its own
            // workspace instead of implicitly relying on ask to bypass that pending interaction.
            workspace = Path.Combine(root, "workspace-http");
            Directory.CreateDirectory(workspace);
            Environment.CurrentDirectory = workspace;
            CliApp.UseRuntimeForTests(OmniCliRuntime.Create(workspace));

            // M2: actual HTTP Chat Completions provider, plus diagnostics and typed client commands.
            var doctor = await Run("doctor");
            Assert.Equal(0, doctor.Code);
            Assert.Contains("omni doctor — diagnóstico de M2", doctor.Output);
            Assert.Contains("Modelos disponibles:", doctor.Output);
            Assert.Contains("Estado: modelo configurado", doctor.Output);
            Assert.DoesNotContain("doctor.", doctor.Output);
            AssertNoLeaks(doctor.Output);
            var ask = await Run("ask", "explica el estado");
            Assert.True(ask.Code == 0, $"ask expected exit=0, actual={ask.Code}: {ask.Output}");
            Assert.Contains("respuesta-scripted", ask.Output);
            AssertNoLeaks(ask.Output);

            // Verify the new workspace's journal after ask has actually created it.
            var doctorJournal = await Run("doctor", "--verify-journal");
            Assert.Equal(0, doctorJournal.Code);
            Assert.Contains("verify-journal:", doctorJournal.Output);
            AssertNoLeaks(doctorJournal.Output);

            var explain = await Run("explain", "estado del plan");
            Assert.Equal(0, explain.Code);
            Assert.Contains("Pregunta: estado del plan", explain.Output);
            AssertNoLeaks(explain.Output);

            var context = await Run("/context");
            Assert.Equal(0, context.Code);
            Assert.Contains("Snapshot de contexto", context.Output);
            AssertNoLeaks(context.Output);

            var tools = await Run("/tools");
            Assert.Equal(0, tools.Code);
            Assert.Contains("Tools efectivas", tools.Output);
            AssertNoLeaks(tools.Output);

            var typedExplain = await Run("/explain");
            Assert.Equal(0, typedExplain.Code);
            Assert.Contains("respuesta-scripted", typedExplain.Output);
            AssertNoLeaks(typedExplain.Output);

            // M3: exercise the real Act path and verify the scripted provider response is rendered.
            var act = await Run("act", "implementa el objetivo");
            Assert.Equal(0, act.Code);
            Assert.Contains("respuesta-scripted", act.Output);
            AssertNoLeaks(act.Output);

            var policyList = await Run("model", "list");
            Assert.Equal(0, policyList.Code);
            Assert.Contains("scripted-model", policyList.Output);
            AssertNoLeaks(policyList.Output);

            var policyShowMissing = await Run("model", "policy", "show", "scripted-model");
            Assert.Equal(1, policyShowMissing.Code);
            AssertNoLeaks(policyShowMissing.Output);

            var policySet = await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "e2e");
            Assert.Equal(0, policySet.Code);
            Assert.Contains("PatchOnly", policySet.Output);
            AssertNoLeaks(policySet.Output);

            var policyShow = await Run("model", "policy", "show", "scripted-model");
            Assert.Equal(0, policyShow.Code);
            Assert.Contains("PatchOnly", policyShow.Output);
            AssertNoLeaks(policyShow.Output);

            var select = await Run("model", "select", "scripted-model");
            Assert.Equal(0, select.Code);
            Assert.Contains("scripted-model", select.Output);
            AssertNoLeaks(select.Output);

            var policyHistory = await Run("model", "policy", "history", "scripted-model");
            Assert.Equal(0, policyHistory.Code);
            Assert.Contains("rev=", policyHistory.Output);
            AssertNoLeaks(policyHistory.Output);

            var policyDelete = await Run("model", "policy", "delete", "scripted-model");
            Assert.Equal(0, policyDelete.Code);
            Assert.Contains("eliminada", policyDelete.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(policyDelete.Output);

            // M4: trust, permission grants, interaction resolution, integrity and maintenance.
            var trust = await Run("trust");
            Assert.Equal(0, trust.Code);
            Assert.Contains("confiable", trust.Output);
            AssertNoLeaks(trust.Output);
            var untrust = await Run("trust", "revoke");
            Assert.Equal(0, untrust.Code);
            Assert.Contains("no confiable", untrust.Output);
            AssertNoLeaks(untrust.Output);

            var paths = OmniHost.CreatePlatformPaths();
            var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
                ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspace)));
            var workspaceData = OmniHost.WorkspaceDataDirectory(paths, workspace);
            var grantStore = new FilePermissionGrantStore(workspaceData, new FileAuditSink(data));
            var grant = new PermissionGrantRecord(GrantId.New(), "filesystem.read", new string('a', 64),
                GrantLifetime.Workspace, workspaceId, null, DateTimeOffset.UtcNow);
            grantStore.Add(grant, CancellationToken.None);

            var permissions = await Run("permissions", "list");
            Assert.Equal(0, permissions.Code);
            Assert.Contains(grant.Id.ToString(), permissions.Output);
            AssertNoLeaks(permissions.Output);
            var revoke = await Run("permissions", "revoke", grant.Id.ToString());
            Assert.Equal(0, revoke.Code);
            Assert.Contains("revocado", revoke.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(revoke.Output);
            var revokeMissing = await Run("permissions", "revoke", Guid.NewGuid().ToString());
            Assert.Equal(1, revokeMissing.Code);
            AssertNoLeaks(revokeMissing.Output);

            var resolve = await Run("resolve");
            Assert.Equal(1, resolve.Code);
            Assert.Contains("No hay", resolve.Output);
            AssertNoLeaks(resolve.Output);
            var resolveUnknown = await Run("resolve", "missing-interaction", "allow_once");
            Assert.Equal(1, resolveUnknown.Code);
            AssertNoLeaks(resolveUnknown.Output);

            // An explicit bad path is a stable user-facing failure mode, independent of internal storage layout.
            var verifyOk = await Run("verify-journal");
            Assert.True(verifyOk.Code == 0, $"verify-journal exited {verifyOk.Code}: {verifyOk.Output}");
            Assert.Contains("— OK", verifyOk.Output);
            AssertNoLeaks(verifyOk.Output);

            var verify = await Run("verify-journal", Path.Combine(root, "missing-journal.db"));
            Assert.Equal(1, verify.Code);
            Assert.Contains("JournalUnreadable", verify.Output);
            AssertNoLeaks(verify.Output);

            var sessionPurge = await Run("session", "purge", "not-a-guid");
            Assert.Equal(2, sessionPurge.Code);
            Assert.Contains("no válido", sessionPurge.Output);
            AssertNoLeaks(sessionPurge.Output);

            var gc = await Run("gc", "--dry-run");
            Assert.Equal(0, gc.Code);
            Assert.Contains("GC", gc.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(gc.Output);

            var auditPurge = await Run("audit", "purge", "--dry-run", "--before", "2026-01-01");
            Assert.Equal(0, auditPurge.Code);
            Assert.Contains("auditoría", auditPurge.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(auditPurge.Output);

            // Headless only: must return without initializing an interactive terminal driver.
            var tui = await Run("tui", "--sim");
            Assert.True(tui.Code == 0, $"tui --sim exited {tui.Code}: {tui.Output}");
            Assert.Contains("TUI (sim)", tui.Output);
            Assert.Contains("OmniCore", tui.Output);
            AssertNoLeaks(tui.Output);

            var journal = Path.Combine(workspaceData, "journal.db");
            string sessionId;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = journal }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT session_id FROM events ORDER BY seq LIMIT 1";
                sessionId = Convert.ToString(command.ExecuteScalar()) ?? "";
            }
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            var purge = await Run("session", "purge", sessionId);
            Assert.Equal(0, purge.Code);
            Assert.Contains("sesión", purge.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(purge.Output);

            Environment.SetEnvironmentVariable("OMNI_LOCALE", "en");
            var englishDoctor = await Run("doctor");
            Assert.Equal(0, englishDoctor.Code);
            Assert.Contains("omni doctor — M2 diagnostics", englishDoctor.Output);
            Assert.Contains("Available models:", englishDoctor.Output);
            Assert.Contains("Status: model configured", englishDoctor.Output);
            Assert.DoesNotContain("doctor.", englishDoctor.Output);
            AssertNoLeaks(englishDoctor.Output);

            Environment.SetEnvironmentVariable("OMNI_LOCALE", "es");
            var explicitEnglishDoctor = await Run("doctor", "--locale", "en");
            Assert.Equal(0, explicitEnglishDoctor.Code);
            Assert.Contains("Available models:", explicitEnglishDoctor.Output);
            Assert.DoesNotContain("doctor.", explicitEnglishDoctor.Output);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", "en");

            var help = await Run("--help");
            Assert.Equal(0, help.Code);
            Assert.Contains("Usage:", help.Output);
            AssertNoLeaks(help.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", priorData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", priorConfig);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", priorLocale);
            Environment.SetEnvironmentVariable("OMNI_MODEL", priorModel);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
            if (previousRuntime is not null) CliApp.UseRuntimeForTests(previousRuntime);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task Act_executes_read_and_approved_patch_before_completing_gates()
    {
        await InIsolatedCli(async (workspace, _, data, provider) =>
        {
            const string original = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n";
            File.WriteAllText(Path.Combine(workspace, "doc.txt"), original);
            var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant();
            provider.RespondWith((requestNumber, _) => requestNumber switch
            {
                0 => ToolCallResponse("call-read", "filesystem.read", """{"path":"doc.txt"}"""),
                1 => ToolCallResponse("call-patch", "filesystem.patch",
                    PatchArguments(version)),
                _ => TextResponse("cambio aplicado"),
            });

            var policy = await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "cli-e2e");
            Assert.Equal(0, policy.Code);
            var policyService = OmniHost.CreateModelPolicyService(data);
            var policyKey = ModelPolicyKey.For("scripted", "scripted-model");
            var stored = Assert.IsType<StoredModelPolicy>(policyService.Get(policyKey, CancellationToken.None));
            var oldMutation = stored.Policy.MutationPolicy;
            var noPostEditGate = new FileMutationPolicy(oldMutation.Mode, oldMutation.Delete, oldMutation.MoveOrRename,
                oldMutation.MaxFilesPerTurn, oldMutation.MaxChangedLinesPerTurn, oldMutation.MaxRewriteRatio,
                oldMutation.RequirePriorRead, oldMutation.RequireExpectedVersionToken,
                requirePostEditValidation: false, allowParallelMutations: oldMutation.AllowParallelMutations);
            policyService.Set(policyKey, stored.Revision,
                new UserModelPolicy(stored.Policy.Category, stored.Policy.ToolPolicy, noPostEditGate,
                    stored.Policy.Source, "cli-e2e-approved-patch"), CancellationToken.None);
            var act = await Run("act", "actualiza doc.txt");
            Assert.Equal(0, act.Code);
            Assert.Equal("one\ntwo-X\nthree\nfour\nfive\nsix\nseven\neight\n",
                File.ReadAllText(Path.Combine(workspace, "doc.txt")));
            Assert.Contains("[herramienta] filesystem.read", act.Output);
            Assert.Contains("[herramienta] filesystem.patch", act.Output);
            AssertNoLeaks(act.Output);

            var payloads = ReadCurrentSessionEvents(workspace);
            var patchCall = Assert.Single(payloads.OfType<ToolCallRequested>(),
                call => call.ToolName == "filesystem.patch");
            var started = Assert.Single(payloads.OfType<ToolCallStarted>(),
                evt => evt.ToolCallId == patchCall.ToolCallId);
            var succeeded = Assert.Single(payloads.OfType<ToolCallSucceeded>(),
                evt => evt.ToolCallId == patchCall.ToolCallId);
            Assert.Equal(EffectClass.NonIdempotent, started.EffectClass);
            var ordered = payloads.ToList();
            Assert.True(ordered.IndexOf(started) < ordered.IndexOf(succeeded),
                "Barrier ToolCallStarted must precede successful effect outcome");
            var validation = Assert.Single(payloads.OfType<RunValidationStarted>());
            var completed = Assert.Single(payloads.OfType<RunCompleted>());
            Assert.True(payloads.ToList().IndexOf(validation) < payloads.ToList().IndexOf(completed));
            Assert.Equal(3, provider.RequestCount);
        });
    }

    [Fact]
    public async Task Act_permission_ask_without_tty_stays_awaiting_input_without_inventing_answer()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            const string original = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n";
            File.WriteAllText(Path.Combine(workspace, "doc.txt"), original);
            Assert.Equal(0, (await Run("trust")).Code);
            var settings = Path.Combine(workspace, ".omnicore");
            Directory.CreateDirectory(settings);
            File.WriteAllText(Path.Combine(settings, "settings.yaml"), string.Join(Environment.NewLine,
                "permissionRestrictions:", "  filesystem.patch: ask", ""));
            provider.RespondWith((requestNumber, _) => requestNumber == 0
                ? ToolCallResponse("call-read", "filesystem.read", """{"path":"doc.txt"}""")
                : ToolCallResponse("call-patch", "filesystem.patch",
                    PatchArguments(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant())));

            Assert.Equal(0, (await Run("model", "policy", "set", "scripted-model", "--category", "PatchOnly", "--note", "cli-e2e-ask")).Code);
            var result = await Run("act", "actualiza doc.txt");
            Assert.Equal(3, result.Code);
            Assert.Contains("InputRequired", result.Output);
            Assert.Contains("No se asumió una respuesta", result.Output);
            Assert.Equal(original, File.ReadAllText(Path.Combine(workspace, "doc.txt")));
            var payloads = ReadCurrentSessionEvents(workspace);
            Assert.Contains(payloads, evt => evt is InteractionRequested request && request.Kind == InteractionKind.Permission);
            Assert.Contains(payloads, evt => evt is RunAwaitingInput);
            Assert.DoesNotContain(payloads, evt => evt is InteractionResolved);
            Assert.DoesNotContain(payloads, evt => evt is ToolCallStarted started && started.EffectClass != EffectClass.None);
            AssertNoLeaks(result.Output);
        });
    }

    [Fact]
    public async Task Resolve_lists_and_answers_live_effect_recovery_interaction_then_allows_new_run()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
            Directory.CreateDirectory(workspaceData);
            var journalPath = Path.Combine(workspaceData, "journal.db");
            var statePath = Path.Combine(workspaceData, "lastsession.txt");
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var store = new SqliteEventStore(journalPath);
            var stream = new EventStream(store, codecs, session);
            stream.Append(new SessionCreated(session, WorkspaceId.Of(workspace).ToString(), workspace,
                ProfileId.New(), DateTimeOffset.UtcNow));
            var run = TestRun.Open(stream, session, "efecto interrumpido", RunMode.Act);
            var call = ToolCallId.New();
            stream.Append(new ToolCallRequested(call, "pc-recovery", "filesystem.patch", "{}"));
            stream.Append(new ToolCallPrepared(call, "{}"));
            stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(call));
            stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent, null), DurabilityClass.Barrier);
            stream.Append(new ToolCallEffectUnknown(call, EffectClass.NonIdempotent));
            stream.Append(new RunFailed(run.RunId, "interrupted after barrier"));
            File.WriteAllText(statePath, session + Environment.NewLine + run.RunId);
            store.Close();

            var list = await Run("resolve");
            Assert.Equal(0, list.Code);
            Assert.Contains("resolution_applied", list.Output);
            var match = Regex.Match(list.Output, @"#(?<id>[0-9a-fA-F-]{36})");
            Assert.True(match.Success, "resolve should print the live interaction id: " + list.Output);

            var response = await Run("resolve", match.Groups["id"].Value, "resolution_applied");
            Assert.Equal(0, response.Code);
            Assert.Contains("resuelta", response.Output, StringComparison.OrdinalIgnoreCase);
            AssertNoLeaks(list.Output);
            AssertNoLeaks(response.Output);

            provider.RespondWith((_, _) => TextResponse("nuevo run completado"));
            var act = await Run("act", "inicia trabajo nuevo");
            Assert.Equal(0, act.Code);
            Assert.Contains("nuevo run completado", act.Output);
            var payloads = ReadAllSessionEvents(workspace);
            Assert.True(payloads.OfType<RunCreated>().Count() >= 2);
            Assert.Contains(payloads, evt => evt is RunCompleted);
            Assert.Contains(payloads, evt => evt is InteractionResolved resolved
                && resolved.InteractionId.ToString() == match.Groups["id"].Value);
        });
    }

    private static IReadOnlyList<DomainEventPayload> ReadCurrentSessionEvents(string workspace)
    {
        var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
        var sessionId = SessionId.Parse(File.ReadAllLines(Path.Combine(workspaceData, "lastsession.txt"))[0]);
        var store = new SqliteEventStore(Path.Combine(workspaceData, "journal.db"));
        try { return store.ReadFrom(sessionId, 1).Select(EventCodecs.Create().Decode).ToArray(); }
        finally { store.Close(); }
    }

    private static IReadOnlyList<DomainEventPayload> ReadAllSessionEvents(string workspace)
    {
        var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
        var journal = Path.Combine(workspaceData, "journal.db");
        var sessions = new List<SessionId>();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = journal }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT session_id FROM events";
            using var reader = command.ExecuteReader();
            while (reader.Read()) sessions.Add(SessionId.Parse(reader.GetString(0)));
        }
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            return sessions.SelectMany(session => store.ReadFrom(session, 1))
                .Select(codecs.Decode).ToArray();
        }
        finally { store.Close(); }
    }

    private static async Task InIsolatedCli(Func<string, string, string, ScriptedHttpProvider, Task> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-cli-e2e-gap-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        var previousDirectory = Environment.CurrentDirectory;
        var priorData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var priorConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var priorLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        var priorModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        OmniCliRuntime? previousRuntime = null;
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", null);
            Environment.SetEnvironmentVariable("OMNI_MODEL", null);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
            await using var provider = new ScriptedHttpProvider();
            File.WriteAllText(Path.Combine(config, "providers.yaml"), string.Join(Environment.NewLine,
                "providers:", "  scripted:", "    family: OpenAiChatCompatible", "    baseUrl: " + provider.BaseUrl,
                "    auth: none", "    billingMode: Local", ""));
            File.WriteAllText(Path.Combine(config, "models.yaml"), string.Join(Environment.NewLine,
                "models:", "  scripted-model:", "    provider: scripted", "    context: 8192",
                "    maxOutput: 2048", ""));
            var runtime = OmniCliRuntime.Create(workspace);
            runtime.Localize = (key, values) => Localization.Spanish().Resolve(key, values);
            previousRuntime = CliApp.UseRuntimeForTests(runtime);
            await run(workspace, config, data, provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, priorData);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, priorConfig);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", priorLocale);
            Environment.SetEnvironmentVariable("OMNI_MODEL", priorModel);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
            if (previousRuntime is not null) CliApp.UseRuntimeForTests(previousRuntime);
            Environment.CurrentDirectory = previousDirectory;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static string ToolCallResponse(string id, string name, string arguments)
    {
        var eventBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { tool_calls = new[] { new
                { index = 0, id, type = "function", function = new { name, arguments } } } }, finish_reason = (string?)null } },
        });
        return string.Join("\n", "data: " + eventBody, "",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}", "",
            "data: [DONE]", "");
    }

    private static string PatchArguments(string version) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            path = "doc.txt", expectedVersion = version, oldText = "two\n", newText = "two-X\n",
        });

    private static string TextResponse(string text)
    {
        var eventBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = text }, finish_reason = (string?)null } },
        });
        return string.Join("\n", "data: " + eventBody, "",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}", "",
            "data: [DONE]", "");
    }

    private static async Task<(int Code, string Output)> Run(params string[] args)
    {
        var prior = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var code = await CliApp.RunAsync(args);
            return (code, output.ToString());
        }
        finally { Console.SetOut(prior); }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs", "sim")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró docs/sim.");
    }

    private static void AssertNoLeaks(string output, bool machineReadable = false)
    {
        Assert.False(Regex.IsMatch(output, @"(?m)^\s*at [A-Za-z0-9_.+`]+\("), "Se filtró una traza: " + output);
        Assert.DoesNotContain("frames:", output, StringComparison.OrdinalIgnoreCase);
        if (machineReadable) return; // JSON conserva identificadores de eventos, que no son texto localizado.
        var localizationKey = Regex.Match(output, @"\b[a-z][a-z0-9]*(?:\.[a-z][a-z0-9]*)*\.[a-z][a-z0-9]*_[a-z0-9_]+\b",
            RegexOptions.IgnoreCase);
        Assert.False(localizationKey.Success, "Se filtró una clave de localización: " + localizationKey.Value);
    }

    /// <summary>A tiny real HTTP server returning deterministic OpenAI-compatible SSE responses.</summary>
    private sealed class ScriptedHttpProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private Func<int, string, string>? _response;
        private int _requestCount;

        public ScriptedHttpProvider()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public void RespondWith(Func<int, string, string> response) => _response = response;

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = HandleAsync(client, _stop.Token);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                var body = new char[contentLength];
                var read = 0;
                while (read < body.Length)
                {
                    var count = await reader.ReadAsync(body.AsMemory(read), cancellationToken);
                    if (count == 0) break;
                    read += count;
                }

                var requestNumber = Interlocked.Increment(ref _requestCount) - 1;
                var request = new string(body, 0, read);
                var eventText = _response?.Invoke(requestNumber, request) ?? TextResponse("respuesta-scripted");
                var bytes = Encoding.UTF8.GetBytes(eventText);
                var response = Encoding.ASCII.GetBytes(string.Join("\r\n", "HTTP/1.1 200 OK",
                    "Content-Type: text/event-stream", "Cache-Control: no-cache", "Connection: close",
                    "Content-Length: " + bytes.Length) + "\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _serve; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
