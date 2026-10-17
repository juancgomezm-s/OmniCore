using System.Text.Json;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class LiveModelSwitchTests
{
    [Fact]
    [Trait("Category", "Live")]
    public void Live_Luna_to_Sol_continues_once_after_Enter_and_remembers_prior_context() =>
        TuiWiringTests.RunLiveLunaToSol();
}

public sealed partial class TuiWiringTests
{
    internal static void RunLiveLunaToSol()
    {
        if (Environment.GetEnvironmentVariable("OMNI_LIVE_LUNA_SOL") != "1")
        {
            Assert.Skip("Opt-in acceptance test: requires the user's configured ChatGPT subscription and network.");
            return;
        }
        var evidence = Environment.GetEnvironmentVariable("OMNI_LIVE_EVIDENCE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(evidence));
        Directory.CreateDirectory(evidence!);
        var previousDirectory = Environment.CurrentDirectory;
        var previousModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        var previousData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var previousConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        using var fx = TuiFixture.Create();
        OmniCliRuntime? runtime = null;
        try
        {
            Environment.CurrentDirectory = fx.Workspace;
            Environment.SetEnvironmentVariable("OMNI_MODEL", null);
            var userData = Environment.GetEnvironmentVariable("OMNI_LIVE_USER_DATA_DIR");
            var userConfig = Environment.GetEnvironmentVariable("OMNI_LIVE_USER_CONFIG_DIR");
            Assert.True(Directory.Exists(userData), "Explicit live user data directory required.");
            Assert.True(Directory.Exists(userConfig), "Explicit live user configuration directory required.");
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, userData);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, userConfig);
            var paths = OmniHost.CreatePlatformPaths();
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var luna = loaded.Registry.Models().Single(model => model.Id == "gpt-6-luna");
            var sol = loaded.Registry.Models().Single(model => model.Id == "gpt-6.1-sol");
            var provider = loaded.Registry.Provider(luna.ProviderId)!;
            Assert.Equal(sol.ProviderId, luna.ProviderId);
            Assert.Equal("codex", provider.Profile);
            Assert.Equal("chatgpt.com", new Uri(provider.BaseUrl).Host);
            Assert.Equal(BillingMode.IncludedQuota, provider.BillingMode);
            fx.Server.ClosePersistentStore();
            runtime = OmniCliRuntime.Create(fx.Workspace);
            runtime.Localize = (key, values) => Localization.Spanish().Resolve(key, values);
            fx.Server = Assert.IsType<OmniServer>(runtime.Connect(CancellationToken.None));
            fx.Artifacts = OmniHost.CreateArtifactStore(OmniHost.WorkspaceDataDirectory(paths, fx.Workspace));
            // Reproduce a session whose saved permissions include Luna but not the newly chosen Sol.
            // No user configuration, existing conversation, model policy or credential is replaced.
            var lunaBinding = AuthorizedModelRoute.From(ModelRoutingHost.RouteFor(luna, provider), provider.BillingMode);
            fx.Server.ConfigureNewSessionRoutingPolicy(new SessionRoutingPolicy(1, [lunaBinding],
                [provider.BillingMode], false, null, provider.Id));
            Assert.Equal("ok", fx.Server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"Prueba aislada de continuidad entre modelos.\"}"), CancellationToken.None).Status);
            var session = fx.Server.LastSessionId();
            var run = fx.Server.LastRunId();
            var descriptors = new[] { luna, sol }.Select(model => new ModelRegistryModelDescriptor(model.Id,
                model.ProviderId, model.ContextWindow, model.RecommendedUsableContext, model.MaxOutputTokens)).ToArray();
            var policies = ModelPolicyHost.Create(paths.DataDirectory, descriptors);
            policies.Select(ModelPolicyHost.WorkspaceSelectionId(fx.Workspace), new(luna.ProviderId, luna.Id),
                luna.Id, true, CancellationToken.None);
            fx.StartTui(policies: policies, account: new TuiAccountHost(), turnHost: new TuiTurnHost(runtime),
                initialColumns: 120, initialRows: 40);
            var marker = "CONTINUIDAD-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            Type(fx, "Recuerda esta clave para mi siguiente mensaje: " + marker + ". Responde solamente con la clave.");
            fx.InjectKey(new Key(KeyCode.Enter));
            WaitForLiveReply(1);
            var first = fx.Decoded<AssistantMessageRecorded>().Single();
            Assert.Contains(marker, fx.Artifacts.GetText(first.ContentRef!.Hash));
            Assert.Equal(luna.Id, Assert.Single(fx.Decoded<ModelStepStarted>()).ModelId);
            KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay?.Id == "omni-model-palette", "selector de modelos");
            fx.Invoke(() =>
            {
                var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
                var rows = list.Source!.ToList().Cast<object>().Select(item => item.ToString() ?? "").ToArray();
                list.SelectedItem = Array.FindIndex(rows, item => item.Contains(sol.Id, StringComparison.Ordinal));
                Assert.True(list.SelectedItem >= 0);
                list.SetFocus();
            });
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => fx.App.Overlay is null && fx.App.Header!.Text.ToString().Contains(sol.Id), "Sol seleccionado mediante Enter");
            const string pending = "Repite la clave de continuidad que te di antes. Responde solamente con la clave.";
            Type(fx, pending);
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => fx.Decoded<InteractionRequested>().Any(item => item.Kind == InteractionKind.ModelRouteConsent)
                && fx.App.Overlay is not null, "consentimiento para usar Sol");
            var consent = Assert.Single(fx.Decoded<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
            Assert.Equal(sol.Id, SessionRoutingAuthorization.Parse(consent)!.Route.RouteId.Value);
            var inputsAtConsent = fx.Decoded<UserInputReceived>().Count();
            Assert.Single(fx.Decoded<ModelStepStarted>());
            ExportPreferenceFrame(fx, 120, 40, "live-sol-consent", "Usar en esta conversación");
            fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<Button>()
                .Single(button => button.Text.ToString().Contains("Usar en esta conversación")).SetFocus());
            // One Enter. No additional send, resume callback or retry injected by the test.
            fx.InjectKey(new Key(KeyCode.Enter));
            WaitForLiveReply(2);
            var messages = fx.Decoded<AssistantMessageRecorded>();
            var solText = fx.Artifacts.GetText(messages.Last().ContentRef!.Hash)!;
            Assert.Contains(marker, solText);
            Assert.Equal(inputsAtConsent, fx.Decoded<UserInputReceived>().Count());
            Assert.Equal(3, inputsAtConsent); // seed, Luna's context message, Sol's single request
            Assert.Single(fx.Decoded<UserInputReceived>(), input => input.InputPartsJson.Contains(pending));
            Assert.Equal(new[] { luna.Id, sol.Id }, fx.Decoded<ModelStepStarted>().Select(step => step.ModelId));
            Assert.Equal(2, fx.Decoded<TurnCompleted>().Count());
            Assert.Equal(session, fx.Server.LastSessionId());
            Assert.Equal(run, fx.Server.LastRunId());
            Assert.Equal("allow_route", Assert.Single(fx.Decoded<InteractionResolved>()).OptionId);
            Assert.All(fx.Decoded<ModelStepCompleted>(), step => Assert.Equal(StopReason.EndTurn, step.StopReason));
            CaptureConversationFrame(fx, 120, 40, marker);
            ExportPreferenceFrame(fx, 120, 40, "live-sol-response", marker);
            File.WriteAllText(Path.Combine(evidence!, "result.json"), JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, realProvider = provider.Id, profile = provider.Profile,
                sourceModel = luna.Id, targetModel = sol.Id, contextMarker = marker,
                targetReply = solText, inputCountBeforeConsent = inputsAtConsent,
                inputCountAfterReply = fx.Decoded<UserInputReceived>().Count(), modelCalls = 2,
                sameSession = true, sameRun = true, consentEnterCount = 1, promptResent = false,
                nativeWindowAutomated = false, tuiDriver = "Terminal.Gui DOTNET",
                journal = Path.Combine(OmniHost.WorkspaceDataDirectory(paths, fx.Workspace), "journal.db")
            }));

            void WaitForLiveReply(int count)
            {
                Assert.True(WaitUntil(() => fx.LoopError is not null
                    || fx.Decoded<AssistantMessageRecorded>().Count() >= count
                        && fx.App.Status!.Text.ToString().Contains("Listo"), 180000),
                    "No llegó la respuesta del proveedor real dentro de 180 s; revisar journal/aviso de la TUI.");
                Assert.Null(fx.LoopError);
            }
        }
        finally
        {
            fx.Application?.RequestStop();
            fx.Loop?.Join(5000);
            runtime?.Dispose();
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable("OMNI_MODEL", previousModel);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, previousData);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, previousConfig);
        }
    }
}
