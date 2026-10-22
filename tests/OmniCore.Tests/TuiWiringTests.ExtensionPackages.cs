using OmniCore.Host;
using OmniCore.Abstractions;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Domain;
using System.Diagnostics;
using System.Text.Json;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Extension_view_refreshes_external_changes_and_preserves_selection_without_reopening() => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        var manager = new ExtensionPackageManager(paths);
        manager.Install(PilotPath("filetree"), CancellationToken.None); manager.Change("omnicore.filetree", "enable");
        fx.StartTui(initialColumns: 120, initialRows: 35);
        fx.Invoke(() => fx.App.ShowExtensionView(new("omnicore.filetree", "files", Path: "src")));
        var panel = fx.App.Overlay!;
        var list = panel.SubViews.OfType<ListView>().Single(view => view.Id == "extension-view-items");
        fx.Invoke(() => list.SelectedItem = 1);
        var selected = fx.Read(() => list.Source!.ToList()[list.SelectedItem!.Value]!.ToString());
        File.WriteAllText(Path.Combine(fx.Workspace, "src", "Aaron.cs"), "class Aaron {}");
        fx.Wait(() => fx.Read(() => list.Source!.ToList().Cast<string>().Any(text => text.Contains("Aaron.cs", StringComparison.Ordinal))), "external file appears through background refresh");
        Assert.Same(panel, fx.App.Overlay);
        Assert.Equal(selected, fx.Read(() => list.Source!.ToList()[list.SelectedItem!.Value]!.ToString()));
        fx.InjectKey(new(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "view closes and refresh is cancelled");
    });

    [Fact]
    public void Time_machine_clicks_create_and_open_a_real_alternative_without_changing_original_files() => RunTuiTest(fx =>
    {
        GitForExtensionUi(fx.Workspace, "init"); GitForExtensionUi(fx.Workspace, "config", "user.name", "Test");
        GitForExtensionUi(fx.Workspace, "config", "user.email", "test@localhost");
        GitForExtensionUi(fx.Workspace, "add", "src"); GitForExtensionUi(fx.Workspace, "commit", "-m", "base");
        var session = fx.Server.LastSessionId()!;
        fx.Server.RecordWorkspaceCheckpoint(session, fx.Server.AcquireStore().CurrentSequence(session), "before-instruction");
        Assert.Equal("ok", fx.Server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"checkpoint para alternativa\"}"), CancellationToken.None).Status);
        var original = Path.Combine(fx.Workspace, "src", "Alpha.cs"); File.WriteAllText(original, "original dirty");
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        var manager = new ExtensionPackageManager(paths); manager.Install(PilotPath("time-machine"), CancellationToken.None);
        manager.Change("omnicore.time-machine", "enable");
        fx.StartTui(initialColumns: 120, initialRows: 35);
        fx.Invoke(() => fx.App.ShowExtensionView(new("omnicore.time-machine", "history")));
        var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single(view => view.Id == "extension-view-items");
        fx.Invoke(() => list.SelectedItem = list.Source!.ToList().Cast<string>().ToList().FindIndex(text => text.Contains("checkpoint para alternativa", StringComparison.Ordinal)));
        ClickId(fx, "extension-view-alternative");
        fx.Wait(() => fx.App.Overlay?.Id == "extension-alternative", "instruction form");
        ClickId(fx, "extension-alternative-create");
        fx.Wait(() => fx.App.Overlay?.Id == "extension-alternative-ready", "Core published alternative");
        ClickId(fx, "extension-alternative-open");
        fx.Wait(() => fx.App.RequestedAlternative is not null, "navigation to confirmed alternative requested");
        var target = fx.App.RequestedAlternative!;
        Assert.Equal("class Alpha { }", File.ReadAllText(Path.Combine(target.Workspace, "src", "Alpha.cs")));
        Assert.Equal("original dirty", File.ReadAllText(original)); Assert.Equal(session, fx.Server.LastSessionId());
        var targetServer = OmniHost.OpenPersistentServer(Path.Combine(OmniHost.WorkspaceDataDirectory(paths, target.Workspace), "journal.db"), Path.Combine(fx.Root, "target-audit"));
        targetServer.ConfigureWorkspaceRoot(target.Workspace);
        var imported = targetServer.AcquireStore().ReadFrom(SessionId.Parse(target.SessionId), 1).Select(targetServer.AcquireCodecs().Decode)
            .OfType<ConversationHistoryImported>().Single();
        Assert.Equal(session.ToString(), imported.SourceSessionId);
        Assert.Contains(imported.Messages, message => message.Text.Contains("semilla de prueba", StringComparison.Ordinal));
        targetServer.ClosePersistentStore();
    });

    private static string PilotPath(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OmniCore.slnx"))) root = root.Parent;
        Assert.NotNull(root); return Path.Combine(root.FullName, "extensions", "pilots", name);
    }
    private static void GitForExtensionUi(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000), output); Assert.Equal(0, process.ExitCode); Assert.DoesNotContain("fatal:", error);
    }

    [Fact]
    public void Extension_packages_preferences_enables_declarative_pilot_and_opens_real_view() => RunTuiTest(fx =>
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OmniCore.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        var manager = new ExtensionPackageManager(paths);
        manager.Install(Path.Combine(root.FullName, "extensions", "pilots", "filetree"), CancellationToken.None);
        manager.Install(Path.Combine(root.FullName, "extensions", "pilots", "agent-flow"), CancellationToken.None);
        manager.Install(Path.Combine(root.FullName, "extensions", "pilots", "time-machine"), CancellationToken.None);
        manager.Change("omnicore.agent-flow", "enable"); manager.Change("omnicore.time-machine", "enable");
        fx.StartTui(initialColumns: 120, initialRows: 35);
        fx.Invoke(() => fx.App.ShowExtensionPackages());
        fx.Wait(() => fx.App.Overlay?.Id == "extension-packages", "package screen opened");
        Assert.Equal(3, fx.App.Overlay!.SubViews.OfType<ListView>().Single().Source!.Count);
#pragma warning disable CS0618
        var protection = fx.App.Overlay.SubViews.OfType<TextView>().Single(view => view.Id == "extension-package-details");
        Assert.Contains("Protección: Declarativa", fx.Read(() => protection.Text.ToString()));
#pragma warning restore CS0618
        var packages = fx.App.Overlay.SubViews.OfType<ListView>().Single();
        var filetreeIndex = packages.Source!.ToList().Cast<string>().ToList().FindIndex(label => label.StartsWith("omnicore.filetree", StringComparison.Ordinal));
        fx.Invoke(() => packages.SelectedItem = filetreeIndex);
        ClickId(fx, "extension-package-enable");
        fx.Wait(() => fx.App.Overlay?.Id == "extension-package-confirm", "explicit enable confirmation");
        var yes = fx.App.Overlay!.SubViews.OfType<Button>().First();
        fx.Invoke(() => yes.InvokeCommand(Terminal.Gui.Input.Command.Accept));
        fx.Wait(() => manager.Inspect("omnicore.filetree").Enabled, "Core stored explicit user consent");
        ClickId(fx, "extension-package-views");
        fx.Wait(() => fx.App.Overlay?.Id == "extension-views", "headless catalog rendered in TUI");
        fx.Invoke(() => fx.App.ShowExtensionView(new("omnicore.filetree", "files")));
        fx.Wait(() => fx.App.Overlay?.Id == "extension-view", "real filetree view opened");
        Assert.NotNull(fx.App.Overlay!.SubViews.OfType<ListView>().Single(view => view.Id == "extension-view-items").Source);
        fx.Invoke(() => fx.App.ShowExtensionView(new("omnicore.agent-flow", "agents")));
        Assert.Equal("extension-view", fx.App.Overlay!.Id);
        fx.Invoke(() => fx.App.ShowExtensionView(new("omnicore.time-machine", "history")));
        Assert.Equal("extension-view", fx.App.Overlay!.Id);
        Assert.Contains("sin snapshot recuperable", OverlayText(fx.App.Overlay));
    });
}
