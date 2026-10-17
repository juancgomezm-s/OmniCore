using System.Text;
using System.Text.Json;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class LiveFileHandoffTests
{
    [Fact]
    [Trait("Category", "Live")]
    public void Live_Luna_creates_file_and_DeepSeek_v4_flash_patches_it_in_the_same_conversation() =>
        TuiWiringTests.RunLiveFileHandoff();
}

public sealed partial class TuiWiringTests
{
    internal static void RunLiveFileHandoff()
    {
        if (Environment.GetEnvironmentVariable("OMNI_LIVE_FILE_HANDOFF") != "1")
        {
            Assert.Skip("Explicit opt-in required: real Luna subscription and DeepSeek v4 Flash OpenRouter inference.");
            return;
        }
        var evidence = Environment.GetEnvironmentVariable("OMNI_LIVE_EVIDENCE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(evidence));
        evidence = Path.GetFullPath(evidence!);
        Directory.CreateDirectory(evidence);
        var previousDirectory = Environment.CurrentDirectory;
        var variables = new[] { DefaultPlatformPaths.DataDirVariable, DefaultPlatformPaths.ConfigDirVariable, "OMNI_MODEL", "OMNI_QWEN_KEY", "NO_COLOR" };
        var previous = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        using var fx = TuiFixture.Create();
        OmniCliRuntime? runtime = null;
        var phase = "preflight";
        try
        {
            // Clone only configuration and encrypted connection material into disposable private paths.
            // User policies, visibility, selection, existing conversations and credentials are never changed.
            var userData = Environment.GetEnvironmentVariable("OMNI_LIVE_USER_DATA_DIR");
            var userConfig = Environment.GetEnvironmentVariable("OMNI_LIVE_USER_CONFIG_DIR");
            Assert.True(Directory.Exists(userData) && Directory.Exists(userConfig), "Explicit real user paths required.");
            var sourceAuth = OmniHost.CreateChatGptAuth(new DefaultPlatformPaths(userData!, userConfig!));
            _ = sourceAuth.GetAsync(default).AsTask().GetAwaiter().GetResult();
            Assert.True(sourceAuth.Status(default).ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(15), "Fresh subscription required before isolated credential copy.");
            var data = Path.Combine(fx.Root, "live-data");
            var config = Path.Combine(data, "config");
            Directory.CreateDirectory(config);
            foreach (var file in new[] { "providers.yaml", "models.yaml" })
                File.Copy(Path.Combine(userConfig!, file), Path.Combine(config, file));
            foreach (var file in new[] { "credentials.ini", "credentials.key", "provider-connections.json" })
                if (File.Exists(Path.Combine(userData!, file))) File.Copy(Path.Combine(userData!, file), Path.Combine(data, file));
            foreach (var file in Directory.EnumerateFiles(userData!, "openrouter-model-catalog-*.json"))
                File.Copy(file, Path.Combine(data, Path.GetFileName(file)));
            // Only these three filesystem tools are authorized for this isolated acceptance workspace.
            File.WriteAllText(Path.Combine(config, "permissions.yaml"), "profile: balanced\nrules:\n  filesystem.read: allow\n  filesystem.write: allow\n  filesystem.patch: allow\n");
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable("OMNI_MODEL", null);
            Environment.SetEnvironmentVariable("OMNI_QWEN_KEY", null);
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            fx.Server.ClosePersistentStore();
            fx.Workspace = Path.Combine(evidence, "workspace");
            Directory.CreateDirectory(fx.Workspace);
            Environment.CurrentDirectory = fx.Workspace;
            var paths = OmniHost.CreatePlatformPaths();
            var connections = new TuiProviderConnectionHost(paths: paths);
            var router = connections.List(default).Single(row => row.Backend == "openrouter" && row.CanDiscoverModels);
            const string targetId = "deepseek/deepseek-v4-flash";
            if (!connections.CachedOpenRouterPickerModels(default).Any(model => model.Id == targetId))
                connections.RefreshOpenRouterCatalogAsync(router.ProviderId, null, 0, 100, default).GetAwaiter().GetResult();
            Assert.Contains(connections.CachedOpenRouterPickerModels(default), model => model.Id == targetId);
            connections.RegisterOpenRouterModels(router.ProviderId, [targetId], default);
            var registry = OmniHost.LoadUserConfiguration(paths).Registry;
            var luna = registry.Models().Single(model => model.Id == "gpt-6-luna");
            var target = registry.Models().Single(model => model.Id == targetId);
            var lunaProvider = registry.Provider(luna.ProviderId)!;
            Assert.Equal("codex", lunaProvider.Profile);
            Assert.Equal(BillingMode.IncludedQuota, lunaProvider.BillingMode);
            Assert.Equal(BillingMode.MeteredCurrency, registry.Provider(target.ProviderId)!.BillingMode);
            var policies = ModelPolicyHost.Create();
            policies.Set(new(luna.ProviderId, luna.Id), 0, "ScopedCoder", "Isolated real file creation acceptance.", default);
            policies.Set(new(target.ProviderId, target.Id), 0, "PatchOnly", "Isolated localized edit acceptance; no whole-file replacement.", default);
            policies.Select(ModelPolicyHost.WorkspaceSelectionId(fx.Workspace), new(luna.ProviderId, luna.Id), luna.Id, false, default);
            runtime = OmniCliRuntime.Create(fx.Workspace);
            runtime.Localize = (key, args) => Localization.Spanish().Resolve(key, args);
            fx.Server = Assert.IsType<OmniServer>(runtime.Connect(default));
            fx.Artifacts = OmniHost.CreateArtifactStore(OmniHost.WorkspaceDataDirectory(paths, fx.Workspace));
            fx.Server.ConfigureNewSessionRoutingPolicy(new SessionRoutingPolicy(1,
                [AuthorizedModelRoute.From(ModelRoutingHost.RouteFor(luna, lunaProvider), lunaProvider.BillingMode)],
                [lunaProvider.BillingMode], false, null, luna.ProviderId));
            Assert.Equal("ok", fx.Server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"Prueba real de continuidad y edición localizada entre modelos.\"}"), default).Status);
            Assert.Equal(RunMode.Act, fx.Server.CurrentRunMode());
            var session = fx.Server.LastSessionId();
            var run = fx.Server.LastRunId();
            fx.StartTui(policies: policies, account: new TuiAccountHost(), turnHost: new TuiTurnHost(runtime),
                providerConnections: connections, initialColumns: 120, initialRows: 40);
            var marker = "ENLACE-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var targetFile = Path.Combine(fx.Workspace, "continuidad.txt");
            Assert.False(File.Exists(targetFile));
            var lines = new[] { "Prueba de continuidad", "autor: Luna", "estado: pendiente" }
                .Concat(Enumerable.Range(1, 17).Select(i => $"línea conservada {i:00}")).ToArray();
            var createPrompt = "Crea realmente el archivo continuidad.txt en este espacio de trabajo usando las herramientas de archivos. "
                + "Debe contener exactamente estas 20 líneas, una por renglón, con salto de línea final:\n" + string.Join("\n", lines)
                + "\nNo crees otros archivos, no ejecutes procesos ni propongas un plan. Recuerda la clave de conversación " + marker + " para el siguiente mensaje, pero no la guardes en el archivo. "
                + "Después de escribirlo, léelo y confirma brevemente la creación y la clave.";
            Stage("luna-creating");
            fx.Invoke(() => { fx.App.Composer!.Text = createPrompt; fx.App.Composer.MoveEnd(); fx.App.Composer.SetFocus(); });
            fx.InjectKey(new Key(KeyCode.Enter));
            WaitForReply(1);
            Assert.True(File.Exists(targetFile), "Luna must create the real file, not just claim it.");
            var before = File.ReadAllText(targetFile);
            Assert.Equal(lines, File.ReadAllLines(targetFile));
            File.WriteAllText(Path.Combine(evidence, "antes-luna.txt"), before, new UTF8Encoding(false));
            Assert.All(fx.Decoded<ModelStepStarted>(), step => Assert.Equal(luna.Id, step.ModelId));
            Assert.Contains(fx.Decoded<ToolCallRequested>(), call => call.ToolName == "filesystem.write");
            var toolCount = fx.Decoded<ToolCallRequested>().Count();
            Stage("luna-created");
            Type(fx, "/modelo");
            KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "omni-model-palette", "selector en la misma conversación");
            fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text = targetId);
            fx.Wait(() => PickerItems(fx).Any(row => row.Contains(targetId)), "DeepSeek exacto disponible");
            fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<ListView>().Single().SetFocus());
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => fx.App.Overlay is null && fx.App.Header!.Text.Contains(targetId), "cambio a DeepSeek mediante Enter");
            const string editPrompt = "Continúa sobre el mismo archivo que acabas de crear. Cambia únicamente la línea de estado a 'estado: editado por DeepSeek'. "
                + "Lee primero el archivo y aplica un parche localizado. Conserva literalmente las otras 19 líneas y el salto final; "
                + "no lo recrees ni lo reescribas completo, no ejecutes procesos y no crees otros archivos. "
                + "Confirma el cambio y repite la clave de continuidad que te di en el mensaje anterior, sin que yo tenga que repetirla.";
            Stage("deepseek-selected");
            Type(fx, editPrompt);
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => fx.Decoded<InteractionRequested>().Any(item => item.Kind == InteractionKind.ModelRouteConsent)
                && fx.App.Overlay is not null, "autorización de la conexión DeepSeek");
            var consent = Assert.Single(fx.Decoded<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
            Assert.Equal(targetId, SessionRoutingAuthorization.Parse(consent)!.Route.RouteId.Value);
            var inputCount = fx.Decoded<UserInputReceived>().Count();
            Assert.Equal(3, inputCount); // seed, Luna creation request, single DeepSeek edit request
            Stage("deepseek-consent");
            fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<Button>()
                .Single(button => button.Text.Contains("Usar en esta conversación")).SetFocus());
            fx.InjectKey(new Key(KeyCode.Enter)); // A single approval must resume the already submitted input.
            WaitForReply(2);
            var after = File.ReadAllText(targetFile);
            Assert.Equal(before.Replace("estado: pendiente", "estado: editado por DeepSeek", StringComparison.Ordinal), after);
            var editCalls = fx.Decoded<ToolCallRequested>().Skip(toolCount).ToArray();
            Assert.Contains(editCalls, call => call.ToolName == "filesystem.read");
            Assert.Contains(editCalls, call => call.ToolName == "filesystem.patch");
            Assert.DoesNotContain(editCalls, call => call.ToolName == "filesystem.write");
            var patch = Assert.Single(editCalls, call => call.ToolName == "filesystem.patch");
            Assert.Contains(fx.Decoded<ToolCallSucceeded>(), item => item.ToolCallId == patch.ToolCallId);
            var reply = fx.Artifacts.GetText(fx.Decoded<AssistantMessageRecorded>().Last().ContentRef!.Hash)!;
            Assert.Contains(marker, reply);
            Assert.Equal(inputCount, fx.Decoded<UserInputReceived>().Count());
            Assert.Single(fx.Decoded<UserInputReceived>(), input => input.InputPartsJson.Contains("estado: editado por DeepSeek"));
            Assert.Equal(session, fx.Server.LastSessionId());
            Assert.Equal(run, fx.Server.LastRunId());
            Assert.Equal(2, fx.Decoded<TurnCompleted>().Count());
            Assert.Contains(fx.Decoded<ModelStepStarted>(), step => step.ModelId == targetId);
            Assert.Equal(new[] { "continuidad.txt" }, Directory.GetFiles(fx.Workspace).Select(Path.GetFileName).ToArray());
            File.WriteAllText(Path.Combine(evidence, "despues-deepseek.txt"), after, new UTF8Encoding(false));
            ExportPreferenceFrame(fx, 120, 40, "live-file-handoff", marker);
            File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, sourceModel = luna.Id, targetModel = targetId, realProviders = true,
                workspace = fx.Workspace, file = targetFile, contextMarker = marker, targetReply = reply,
                sameSession = true, sameRun = true, promptResent = false, consentEnterCount = 1,
                inputCountBeforeConsent = inputCount, inputCountAfterReply = fx.Decoded<UserInputReceived>().Count(),
                linesBefore = lines.Length, unchangedLines = 19, patchedLines = 1, wholeFileRewrite = false,
                sourceTools = fx.Decoded<ToolCallRequested>().Take(toolCount).Select(call => call.ToolName).ToArray(),
                targetTools = editCalls.Select(call => call.ToolName).ToArray(),
                modelSteps = fx.Decoded<ModelStepStarted>().Select(step => step.ModelId).ToArray(),
                nativeWindowAutomated = false, tuiDriver = "Terminal.Gui DOTNET"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Stage("complete");

            void Stage(string value)
            {
                phase = value;
                File.WriteAllText(Path.Combine(evidence, "stage.json"), JsonSerializer.Serialize(new { phase, utc = DateTimeOffset.UtcNow }));
            }
            void WaitForReply(int count)
            {
                Assert.True(WaitUntil(() => fx.LoopError is not null || fx.Decoded<TurnAbandoned>().Any()
                    || fx.Decoded<TurnCompleted>().Count() >= count && fx.App.Status!.Text.Contains("Listo"), 240000),
                    "Real provider did not complete in 240 seconds; see isolated evidence and journal.");
                Assert.Null(fx.LoopError);
                Assert.Empty(fx.Decoded<TurnAbandoned>());
            }
        }
        finally
        {
            fx.Application?.RequestStop(); fx.Loop?.Join(5000);
            if (runtime is not null)
            {
                File.WriteAllText(Path.Combine(evidence, "diagnostic.json"), JsonSerializer.Serialize(new
                {
                    phase, status = fx.App?.Status?.Text.ToString(),
                    completedTurns = fx.Decoded<TurnCompleted>().Count(),
                    abandoned = fx.Decoded<TurnAbandoned>().Select(item => item.Reason).ToArray(),
                    replies = fx.Decoded<AssistantMessageRecorded>().Select(item => fx.Artifacts.GetText(item.ContentRef!.Hash)).ToArray(),
                    models = fx.Decoded<ModelStepStarted>().Select(item => item.ModelId).ToArray(),
                    tools = fx.Decoded<ToolCallRequested>().Select(item => item.ToolName).ToArray(),
                    rejected = fx.Decoded<ToolCallRejected>().Select(item => item.ErrorCode?.ToString()).ToArray(),
                    interactionKinds = fx.Decoded<InteractionRequested>().Select(item => item.Kind.ToString()).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            runtime?.Dispose();
            Environment.CurrentDirectory = previousDirectory;
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
