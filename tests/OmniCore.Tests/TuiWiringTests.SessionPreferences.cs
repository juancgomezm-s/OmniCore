namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Extensions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Views;
using Key = Terminal.Gui.Input.Key;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 35)]
    public void Session_memory_screen_uses_typed_details_scrolls_and_cancel_returns_without_mutation(int width, int height) => RunTuiTest(fx =>
    {
        using var sessionQuery = JsonDocument.Parse(fx.Server.Query("memory.session", CancellationToken.None)!.Json);
        var sessionId = sessionQuery.RootElement.GetProperty("session").GetString()!;
        for (var index = 0; index < 14; index++)
        {
            var payload = "{\"cmd\":\"memory.session.add\",\"content\":"
                + "\"fixture note " + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\",\"expectedSessionId\":\"" + sessionId + "\"}";
            Assert.Equal("ok", fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None).Status);
        }
        fx.StartTui(initialColumns: width, initialRows: height);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences opened");
        ChoosePreferenceCategory(fx, "context_sessions");
        ClickId(fx, "preferences-memory-open");
        fx.Wait(() => fx.App.Overlay?.Id == "session-preferences", "typed memory preferences screen opened");

        var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single(view => view.Id == "session-preferences-list");
        Assert.Equal(14, list.Source!.Count);
        Assert.Contains("Indicada por el usuario", OverlayText(fx.App.Overlay));
        var initialLabels = list.Source.ToList().Cast<string>().ToArray();
        var knownNoteIndex = Array.FindIndex(initialLabels, label => label.Contains("fixture note 0", StringComparison.Ordinal));
        Assert.True(knownNoteIndex >= 0, "The known fixture memory must be visible regardless of storage ordering.");
        fx.Invoke(() => list.SelectedItem = knownNoteIndex);
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("fixture note 0"), "known memory detail is selected");
        ExportPreferenceFrame(fx, width, height, "memory", "fixture note 0");
        KeyWithEffect(fx, knownNoteIndex == 0 ? KeyCode.CursorDown : KeyCode.CursorUp,
            () => list.SelectedItem != knownNoteIndex, "selection advances through records");

        // Add and forget exercise both trusted UI write paths end to end.
        ClickId(fx, "session-preferences-add");
        var addInput = fx.App.Overlay!.SubViews.OfType<TextField>().Single(view => view.Id == "session-preferences-memory-input");
        fx.Invoke(() => addInput.Text = "Added through UI");
        ClickId(fx, "session-preferences-memory-save");
        ClickId(fx, "session-preferences-confirm-yes");
        fx.Wait(() => JsonDocument.Parse(fx.Server.Query("memory.session", CancellationToken.None)!.Json)
            .RootElement.GetProperty("records").EnumerateArray().Count() == 15, "trusted UI add stores the confirmed memory");
        var refreshedList = fx.App.Overlay!.SubViews.OfType<ListView>().Single(view => view.Id == "session-preferences-list");
        var addedIndex = Array.FindIndex(refreshedList.Source!.ToList().Cast<string>().ToArray(),
            label => label.Contains("Added through UI", StringComparison.Ordinal));
        Assert.True(addedIndex >= 0, "The newly added memory must be selectable.");
        fx.Invoke(() => refreshedList.SelectedItem = addedIndex);
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("Added through UI"), "new memory details are selected");
        ClickId(fx, "session-preferences-action");
        ClickId(fx, "session-preferences-confirm-yes");
        fx.Wait(() => JsonDocument.Parse(fx.Server.Query("memory.session", CancellationToken.None)!.Json)
            .RootElement.GetProperty("records").EnumerateArray().Count() == 14, "trusted UI forget removes the selected memory");
        using (var afterForget = JsonDocument.Parse(fx.Server.Query("memory.session", CancellationToken.None)!.Json))
            Assert.DoesNotContain(afterForget.RootElement.GetProperty("records").EnumerateArray(),
                record => record.GetProperty("content").GetString() == "Added through UI");

        // The cancel path in the add form must still be side-effect free.
        ClickId(fx, "session-preferences-add");
        Assert.Equal("session-preferences-add-memory", fx.App.Overlay!.Id);
        var input = fx.App.Overlay.SubViews.OfType<TextField>().Single(view => view.Id == "session-preferences-memory-input");
        fx.Invoke(() => input.Text = "cancelled note");
        ClickId(fx, "session-preferences-memory-cancel");
        Assert.Equal("session-preferences", fx.App.Overlay!.Id);
        using var final = JsonDocument.Parse(fx.Server.Query("memory.session", CancellationToken.None)!.Json);
        Assert.Equal(14, final.RootElement.GetProperty("records").GetArrayLength());

        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is not null && Descendants(fx.App.Overlay)
            .Any(view => view.Id == "preferences-memory-open"), "Escape returns to the current category page");
    });

    [Fact]
    public void Skill_activation_and_release_are_confirmed_session_scoped_ui_actions() => RunTuiTest(fx =>
    {
        var skillDirectory = Path.Combine(fx.Workspace, ".omnicore", "skills", "fixture.skill");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "skill.yaml"),
            "id: fixture.skill\nversion: 1.0.0\ndisplayName: Fixture skill\ntools: [fixture.inspect]\n");
        File.WriteAllText(Path.Combine(skillDirectory, "instructions.md"), "bounded fixture instructions");
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        new WorkspaceTrustStore(paths).SetTrusted(fx.Workspace, true);
        fx.StartTui(initialColumns: 120, initialRows: 35);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences opened");
        ChoosePreferenceCategory(fx, "extensions_skills");
        ClickId(fx, "preferences-skills-open");
        fx.Wait(() => fx.App.Overlay?.Id == "session-preferences", "skills preferences screen opened");
        Assert.Contains("fixture.inspect", OverlayText(fx.App.Overlay!));
        Assert.Equal("Discovered", JsonDocument.Parse(fx.Server.Query("skills", CancellationToken.None)!.Json)
            .RootElement.GetProperty("skills")[0].GetProperty("state").GetString());

        ClickId(fx, "session-preferences-action");
        Assert.Equal("session-preferences-confirm", fx.App.Overlay!.Id);
        ClickId(fx, "session-preferences-confirm-yes");
        fx.Wait(() => JsonDocument.Parse(fx.Server.Query("skills", CancellationToken.None)!.Json)
            .RootElement.GetProperty("skills")[0].GetProperty("state").GetString() == "Activated",
            "confirmed activation stores the session-scoped selection");

        ClickId(fx, "session-preferences-action");
        Assert.Equal("session-preferences-confirm", fx.App.Overlay!.Id);
        ClickId(fx, "session-preferences-confirm-yes");
        fx.Wait(() => JsonDocument.Parse(fx.Server.Query("skills", CancellationToken.None)!.Json)
            .RootElement.GetProperty("skills")[0].GetProperty("state").GetString() == "Discovered",
            "confirmed release removes the session-scoped activation");
    });

    [Fact]
    public void Extension_grant_and_revoke_are_distinct_confirmed_actions_for_the_selected_hash() => RunTuiTest(fx =>
    {
        var extensionDirectory = Path.Combine(fx.Workspace, ".omnicore", "extensions", "fixture.extension");
        Directory.CreateDirectory(extensionDirectory);
        File.WriteAllText(Path.Combine(extensionDirectory, ExtensionManifestLoader.ManifestFileName), ExtensionManifest);
        File.WriteAllText(Path.Combine(extensionDirectory, "schema.json"), "{\"type\":\"object\"}");
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        new WorkspaceTrustStore(paths).SetTrusted(fx.Workspace, true);
        fx.StartTui(initialColumns: 120, initialRows: 35);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences opened");
        ChoosePreferenceCategory(fx, "extensions_skills");
        ClickId(fx, "preferences-extensions-open");
        fx.Wait(() => fx.App.Overlay?.Id == "session-preferences", "extensions preferences screen opened");
        ExportPreferenceFrame(fx, 120, 35, "extensions", "Nombre original sin traducir");
        Assert.Contains("Fixture extension", OverlayText(fx.App.Overlay!));
        Assert.Contains("Nombre original sin traducir", OverlayText(fx.App.Overlay!));
        Assert.Contains("tool.register", OverlayText(fx.App.Overlay!));
        Assert.True(fx.App.Overlay!.SubViews.OfType<Button>().Single(button => button.Id == "session-preferences-secondary").Enabled);

        ClickId(fx, "session-preferences-secondary");
        Assert.Equal("session-preferences-confirm", fx.App.Overlay!.Id);
        ClickId(fx, "session-preferences-confirm-yes");
        fx.Wait(() => JsonDocument.Parse(fx.Server.Query("extensions", CancellationToken.None)!.Json)
            .RootElement.GetProperty("extensions")[0].GetProperty("status").GetString() == "Ready", "grant consented to current hash");
        Assert.True(fx.App.Overlay!.SubViews.OfType<Button>().Single(button => button.Id == "session-preferences-secondary").Enabled);

        // The secondary button remains Grant after consent; it must never route through Revoke.
        ClickId(fx, "session-preferences-secondary");
        ClickId(fx, "session-preferences-confirm-yes");
        fx.Wait(() => JsonDocument.Parse(fx.Server.Query("extensions", CancellationToken.None)!.Json)
            .RootElement.GetProperty("extensions")[0].GetProperty("status").GetString() == "Ready", "grant does not revoke existing consent");

        ClickId(fx, "session-preferences-action");
        ClickId(fx, "session-preferences-confirm-yes");
        fx.Wait(() => JsonDocument.Parse(fx.Server.Query("extensions", CancellationToken.None)!.Json)
            .RootElement.GetProperty("extensions")[0].GetProperty("status").GetString() == "ConsentRequired", "explicit revoke removes consent");
    });

    private const string ExtensionManifest = """
        manifestVersion: 1
        id: fixture.extension
        version: 1.0.0
        displayName: Fixture extension
        publisher: tests
        omnicoreApi: '>=1.0 <2.0'
        provides:
          tools:
            - name: fixture
              schema: schema.json
        requests:
          capabilities: [tool.register]
          permissions:
            process:
              - executable: fixture.exe
        runtime:
          kind: process
          command: fixture.exe
          args: []
        source:
          kind: local
          location: .
          sha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
        """;
}
