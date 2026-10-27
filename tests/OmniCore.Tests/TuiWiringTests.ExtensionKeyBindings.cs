using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Enabled_extension_keybinding_is_discovered_and_dispatched_through_the_existing_command_pipeline(int version) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        var package = CreateShortcutExtension(fx.Root, version);
        var manager = new ExtensionPackageManager(paths);
        manager.Install(package, CancellationToken.None);
        manager.Change("acme.shortcut", "enable");

        // The first Host query itself refreshes declarative command registration, without starting a package process.
        var snapshot = ExtensionKeyBindingProtocol.Decode(fx.Server.Query("keybindings.extensions", CancellationToken.None)!.Json);
        var discovered = Assert.Single(snapshot.Bindings, binding => binding.Keys == "F8");
        Assert.Equal("acme.shortcut:run", discovered.Command);
        Assert.Equal("Extension:acme.shortcut@1.0.0", discovered.Source);
        Assert.False(File.Exists(Path.Combine(package, "launched.txt")));

        var mode = fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"run.mode.select\",\"mode\":\"act\",\"effort\":\"standard\"}"), CancellationToken.None);
        Assert.Equal("ok", mode.Status);
        KeyboardFile(fx, "version: 1\nbindings: []\n");
        var client = new HostShortcutClient(fx.Server);
        var turn = new ExtensionShortcutTurn();
        fx.StartTui(client: client, turnHost: turn);
        var binding = fx.App.KeyboardConfiguration.Service.Bindings.First(item =>
            item.Definition.Source == KeyBindingSource.Extension && item.Definition.Keys == "F8");
        Assert.Equal("F8", binding.Definition.Keys);
        Assert.Equal(["{\"value\":\"$(literal)\"}"], binding.Definition.Target.Arguments);
        Assert.Contains(fx.App.KeyboardConfiguration.Diagnostics, issue => issue.Code == "unsupported_terminal_binding"
            && issue.Source == discovered.Source);
        Assert.Equal(5, fx.App.KeyboardConfiguration.Diagnostics.Count(issue => issue.Code == "extension_reserved_prefix"
            && issue.Source == discovered.Source));
        Type(fx, "/keybindings");
        fx.InjectKey(new(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "keybindings-screen", "extension diagnostics screen opens");
        var report = fx.App.Overlay!.SubViews.Single(view => view.Id == "keybindings-report");
        var reportText = fx.Read(() => report.Text.ToString());
        Assert.Contains(discovered.Source, reportText);
        Assert.Contains("unsupported_terminal_binding", reportText);
        fx.InjectKey(new(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "extension diagnostics screen closes");
        var unsafeContext = fx.App.KeyboardConfiguration.Service.Resolve(binding.Sequence,
            new KeyBindingContext([new("input", true), new("overlayOpen", true)]));
        Assert.Equal(KeyBindingResolutionStatus.None, unsafeContext.Status);

        var interaction = InteractionId.New();
        fx.Invoke(() => { fx.AppendInteraction(interaction, "Permission", "{\"operation\":\"test\"}",
            "[{\"id\":\"deny\"}]", "deny"); fx.App.PollOnce(); });
        var approval = fx.Read(() => fx.App.Overlay);
        Assert.NotNull(approval);
        fx.InjectKey(new(KeyCode.Tab));
        Assert.Same(approval, fx.Read(() => fx.App.Overlay));
        fx.InjectKey(new(KeyCode.Enter));
        fx.Wait(() => fx.Decoded<InteractionResolved>().Any(evt => evt.InteractionId == interaction), "Enter responds to the approval after Tab navigation");
        Assert.Null(fx.Read(() => fx.App.Overlay));
        fx.Invoke(() => fx.App.Composer!.SetFocus());
        fx.InjectKey(new(KeyCode.F8));
        fx.Wait(() => turn.ExtensionCalls == 1, "qualified shortcut reaches the existing extension command turn path");
        Assert.Equal("ext.acme.shortcut.run", turn.ToolName);
        Assert.Contains("$(literal)", turn.Prompt);
        Assert.Contains(client.Commands, command => JsonObj.Parse(command.PayloadJson).GetValueOrDefault("cmd") == "command.invoke");
    });

    [Theory]
    [InlineData("disable")]
    [InlineData("uninstall")]
    [InlineData("update")]
    public void Disabled_or_updated_extension_binding_loaded_earlier_is_rejected_and_palette_hides_its_hint(string change) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        var package = CreateShortcutExtension(fx.Root);
        var manager = new ExtensionPackageManager(paths);
        manager.Install(package, CancellationToken.None);
        manager.Change("acme.shortcut", "enable");
        Assert.Equal("ok", fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"run.mode.select\",\"mode\":\"act\",\"effort\":\"standard\"}"), CancellationToken.None).Status);
        KeyboardFile(fx, "version: 1\nbindings: []\n");
        var client = new HostShortcutClient(fx.Server);
        fx.StartTui(client: client);
        Assert.Contains(fx.App.KeyboardConfiguration.Service.Bindings, item => item.Definition.Source == KeyBindingSource.Extension);

        if (change == "disable") manager.Change("acme.shortcut", "disable");
        else if (change == "uninstall") manager.Uninstall("acme.shortcut");
        else
        {
            var installedManifest = Path.Combine(paths.DataDirectory, "extensions", "acme.shortcut", "omnicore-extension.yaml");
            File.WriteAllText(installedManifest, File.ReadAllText(installedManifest).Replace(
                "displayName: Shortcut fixture", "displayName: Shortcut fixture updated", StringComparison.Ordinal));
        }
        fx.InjectKey(new(KeyCode.F8));
        Assert.Empty(client.Commands);
        fx.InjectKey(new(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "unavailable shortcut notice closes");
        OpenGlobalPalette(fx);
        PaletteSearch(fx, "acme.shortcut:run");
        var detail = fx.App.Overlay!.SubViews.Single(view => view.Id == "palette-details");
        Assert.DoesNotContain("F8", fx.Read(() => detail.Text.ToString()));
        Assert.DoesNotContain(fx.App.KeyboardConfiguration.Service.Bindings, item => item.Definition.Source == KeyBindingSource.Extension);
        fx.InjectKey(new(KeyCode.Esc));
    });

    private static string CreateShortcutExtension(string root, int version = 1)
    {
        var package = Path.Combine(root, "shortcut-extension");
        Directory.CreateDirectory(Path.Combine(package, "schemas"));
        File.WriteAllText(Path.Combine(package, "schemas", "run.json"), "{\"type\":\"object\"}");
        File.WriteAllText(Path.Combine(package, "shortcut-host.exe"), "fixture executable; discovery must not run this file");
        var manifest = """
            manifestVersion: 1
            id: acme.shortcut
            version: 1.0.0
            displayName: Shortcut fixture
            publisher: acme
            omnicoreApi: '>=1.0 <2.0'
            provides:
              tools:
                - name: run
                  schema: schemas/run.json
              commands:
                - name: run
                  kind: Extension
                  tool: run
              keyBindings:
                - keys: F8
                  command: acme.shortcut:run
                  arguments: ['{"value":"$(literal)"}']
                  when: input
                - keys: Enter
                  command: acme.shortcut:run
                  when: input
                - keys: Tab
                  command: acme.shortcut:run
                  when: input
                - keys: Esc
                  command: acme.shortcut:run
                  when: input
                - keys: A
                  command: acme.shortcut:run
                  when: input
                - keys: Space
                  command: acme.shortcut:run
                  when: input
                - keys: Meta+F9
                  command: acme.shortcut:run
                  when: input
            requests:
              capabilities: [tool.register, command.register, keybinding.register]
              permissions:
                process:
                  - executable: shortcut-host.exe
                paths:
                  read: []
                  write: []
            runtime:
              kind: process
              command: shortcut-host.exe
              args: [--extension]
            """;
        if (version == 2) manifest = manifest.Replace("manifestVersion: 1", "manifestVersion: 2", StringComparison.Ordinal)
            + "\nexecutionMode: trusted\n";
        File.WriteAllText(Path.Combine(package, "omnicore-extension.yaml"), manifest);
        return package;
    }

    private sealed class ExtensionShortcutTurn : ITuiTurnHost
    {
        internal int ExtensionCalls;
        internal string? ToolName;
        internal string? Prompt;
        public Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken) =>
            Task.FromResult(0);
        public Task<int> ExecuteExtensionCommandAsync(string prompt, string toolName, Action<string> diagnostics,
            CancellationToken cancellationToken)
        {
            Prompt = prompt; ToolName = toolName; Interlocked.Increment(ref ExtensionCalls);
            return Task.FromResult(0);
        }
    }
}
