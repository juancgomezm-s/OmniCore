using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private static string HostShortcut(TuiFixture fx, string id = "core:explain", string arguments = "[src/Alpha.cs]") => KeyboardFile(fx,
        "version: 1\nbindings:\n  - keys: F8\n    command: " + id + "\n    arguments: " + arguments + "\n    when: input\n");

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Host_keybinding_executes_the_real_command_and_preserves_draft_and_attachment(string locale) => RunTuiTest(fx =>
    {
        var config = HostShortcut(fx);
        var saved = File.ReadAllBytes(config);
        var attachment = Path.Combine(fx.Root, "host-shortcut.txt"); File.WriteAllText(attachment, "fixture attachment");
        var client = new HostShortcutClient(fx.Server);
        var turn = new HostShortcutTurn(fx);
        fx.StartTui(client: client, turnHost: turn, localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        Type(fx, "/attach \"" + attachment + "\""); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is not null, "adjunto preparado"); fx.InjectKey(new Key(KeyCode.Esc));
        Type(fx, "borrador que conserva el atajo");
        Assert.DoesNotContain(fx.App.KeyboardConfiguration.Diagnostics, issue => issue.Code == "unknown_target");
        fx.InjectKey(new Key(KeyCode.F8));
        fx.Wait(() => turn.Calls == 1, "el atajo alcanza el consumidor de outcomes Host");
        var invocation = JsonObj.Parse(Assert.Single(client.Commands).PayloadJson);
        Assert.Equal("command.invoke", invocation["cmd"]); Assert.Equal("core:explain", invocation["name"]);
        Assert.Equal("[\"src/Alpha.cs\"]", invocation["arguments"]); Assert.Equal("KeyBinding:User", invocation["origin"]);
        fx.Read(() =>
        {
            Assert.Equal("borrador que conserva el atajo", fx.App.Composer!.Text);
            var chips = Descendants(fx.App.MainWindow!).Single(view => view.Id == "omni-attachments");
            Assert.True(chips.Visible); Assert.Contains("host-shortcut.txt", chips.Text.ToString());
        });
        Assert.Contains("Alpha", turn.Input!); Assert.Equal(saved, File.ReadAllBytes(config));
    });

    [Fact]
    public void Host_keybinding_refuses_replaced_or_disabled_sources_until_explicit_reload() => RunTuiTest(fx =>
    {
        HostShortcut(fx);
        var client = new HostShortcutClient(fx.Server) { Catalog = HostShortcutCatalog("one") };
        fx.StartTui(client: client); Type(fx, "conservar");
        client.Catalog = HostShortcutCatalog("two");
        fx.InjectKey(new Key(KeyCode.F8)); Assert.Empty(client.Commands);
        Assert.Equal("conservar", fx.Read(() => fx.App.Composer!.Text));
        fx.Read(() => Assert.Contains("/keybindings", OverlayText(fx.App.Overlay!)));
        fx.InjectKey(new Key(KeyCode.Esc)); Type(fx, "/keybindings reload"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "keybindings-screen", "recarga acepta nuevo catálogo"); fx.InjectKey(new Key(KeyCode.Esc));
        client.Catalog = HostShortcutCatalog("two", enabled: false);
        fx.InjectKey(new Key(KeyCode.F8)); Assert.Empty(client.Commands);
        fx.InjectKey(new Key(KeyCode.Esc));
        client.Catalog = HostShortcutCatalog("two");
        fx.InjectKey(new Key(KeyCode.F8));
        fx.Wait(() => client.Commands.Any(command => JsonObj.Parse(command.PayloadJson).GetValueOrDefault("cmd") == "command.invoke"),
            "la recarga explícita permite el nuevo fingerprint cuando está disponible");
        Assert.Single(client.Commands, command => JsonObj.Parse(command.PayloadJson).GetValueOrDefault("cmd") == "command.invoke");
    });

    [Fact]
    public void Host_keybinding_uses_the_explicit_qualified_choice_when_the_short_name_is_ambiguous() => RunTuiTest(fx =>
    {
        HostShortcut(fx);
        var client = new HostShortcutClient(fx.Server)
        {
            Catalog = """
                {"catalog":{"commands":[
                  {"id":"fixture:explain","name":"explain","source":"Project:fixture@1","enabled":false,"canonicalEnabled":true,"sourceFingerprint":"other","arguments":["path"]},
                  {"id":"core:explain","name":"explain","source":"BuiltIn:core@1","enabled":false,"canonicalEnabled":true,"sourceFingerprint":"core","arguments":["path"]}
                ]}}
                """
        };
        fx.StartTui(client: client);
        fx.InjectKey(new Key(KeyCode.F8));
        fx.Wait(() => client.Commands.Any(), "atajo calificado enviado");
        Assert.Equal("core:explain", JsonObj.Parse(client.Commands.First().PayloadJson)["name"]);
        Assert.Single(client.Commands, command => JsonObj.Parse(command.PayloadJson).GetValueOrDefault("cmd") == "command.invoke");
    });

    [Fact]
    public void Host_keybinding_never_dismisses_approval_or_runs_in_another_selected_session() => RunTuiTest(fx =>
    {
        HostShortcut(fx, id: "fixture:inspect", arguments: "[]");
        var client = new HostShortcutClient(fx.Server) { Catalog = HostShortcutCatalog("one") };
        // Match the configured qualified identity.
        client.Catalog = client.Catalog.Replace("core:explain", "fixture:inspect", StringComparison.Ordinal);
        fx.StartTui(client: client);
        fx.Invoke(() =>
        {
            fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None);
            fx.App.Composer!.SetFocus();
            fx.App.OnApplicationKeyDown(null, new Key(KeyCode.F8));
        });
        Assert.Empty(client.Commands);
        fx.InjectKey(new Key(KeyCode.Esc));
        // No Run in the new conversation; return to a fresh fixture Run for the permission path.
        fx.Invoke(() => fx.Server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"fixture\"}"), CancellationToken.None));
        var interaction = InteractionId.New();
        fx.Invoke(() => { fx.AppendInteraction(interaction, "Permission", "{\"operation\":\"test\"}", "[{\"id\":\"deny\"}]", "deny"); fx.App.PollOnce(); });
        var overlay = fx.Read(() => fx.App.Overlay);
        fx.InjectKey(new Key(KeyCode.F8)); Assert.Same(overlay, fx.Read(() => fx.App.Overlay)); Assert.Empty(client.Commands);
        Assert.Empty(fx.Decoded<InteractionResolved>());
    });

    [Fact]
    public void Host_keybinding_unqualified_or_missing_command_is_diagnosed_without_dispatch() => RunTuiTest(fx =>
    {
        HostShortcut(fx, "not-published");
        var client = new HostShortcutClient(fx.Server);
        fx.StartTui(client: client);
        Assert.Contains(fx.App.KeyboardConfiguration.Diagnostics, issue => issue.Code == "unknown_target");
        fx.InjectKey(new Key(KeyCode.F8)); Assert.Empty(client.Commands);
    });

    private static string HostShortcutCatalog(string fingerprint, bool enabled = true) =>
        "{\"catalog\":{\"commands\":[{\"id\":\"core:explain\",\"name\":\"explain\",\"source\":\"BuiltIn:core@1\",\"enabled\":"
        + (enabled ? "true" : "false") + ",\"canonicalEnabled\":" + (enabled ? "true" : "false")
        + ",\"arguments\":[\"path\"],\"sourceFingerprint\":\"" + fingerprint + "\"}]}}";

    [Fact]
    public void Host_keybinding_catalog_read_failure_preserves_defaults_and_reports_the_source() => RunTuiTest(fx =>
    {
        HostShortcut(fx);
        var client = new HostShortcutClient(fx.Server) { CatalogUnreadable = true };
        fx.StartTui(client: client);
        Assert.Contains(fx.App.KeyboardConfiguration.Diagnostics, issue => issue.Code == "host_catalog_unavailable");
        Assert.Contains(fx.App.KeyboardConfiguration.Service.Bindings, binding => binding.Definition.Target.Id == "palette.open" && binding.Definition.Keys == "Ctrl+P");
        fx.InjectKey(new Key(KeyCode.F8)); Assert.Empty(client.Commands);
    });

    private sealed class HostShortcutClient(IOmniClient inner) : IOmniClient
    {
        internal string? Catalog;
        internal bool CatalogUnreadable;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<WireEnvelope> Commands = new();
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        { Commands.Enqueue(command); return inner.Send(command, cancellationToken); }
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => inner.SubscribeSince(fromSequence);
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
        {
            if (name == "commands" && CatalogUnreadable) throw new IOException("fixture catalog unavailable");
            return name == "commands" && Catalog is not null ? new(name, Catalog) : inner.Query(name, cancellationToken);
        }
    }

    private sealed class HostShortcutTurn(TuiFixture fixture) : ITuiTurnHost
    {
        internal int Calls;
        internal string? Input;
        public TuiAttachmentReference ImportAttachmentFromPath(string path) => new(
            fixture.Artifacts.PutText(File.ReadAllText(path), "text/plain", ArtifactKind.Other, Sensitivity.Normal)
                with { FileName = Path.GetFileName(path) });
        public Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken)
        { Input = input; Interlocked.Increment(ref Calls); return System.Threading.Tasks.Task.FromResult(0); }
    }
}
