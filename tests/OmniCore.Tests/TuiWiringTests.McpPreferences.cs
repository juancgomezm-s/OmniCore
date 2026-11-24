#pragma warning disable CS0618 // Exercise the pinned multiline TextView with physical keys.
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Mcp_RAG_local_name_and_loopback_URL_save_and_validation_focuses_the_invalid_field(string locale) => RunTuiTest(fx =>
    {
        var host = McpPreferencesHost.Create(fx.Workspace, Path.Combine(fx.Root, "rag-name-data"));
        fx.StartTui(mcpPreferences: host, initialColumns: 80, initialRows: 25,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(() => fx.App.ShowMcpPreferences()); ClickId(fx, "mcp-add");
        fx.Invoke(() => EnterControl(fx, "mcp-name").Text = "RAG local");
        Assert.Equal("rag-local", EnterControl(fx, "mcp-id").Text.ToString());
        Assert.False(EnterControl(fx, "mcp-id").CanFocus);
        EnterOnce(fx, () => EnterControl(fx, "mcp-transport").HasFocus, "Enter salta el ID generado");
        fx.InjectKey(new Key(KeyCode.Space));
        fx.Wait(() => EnterControl(fx, "mcp-endpoint").Visible, "transporte HTTP");
        EnterOnce(fx, () => EnterControl(fx, "mcp-endpoint").HasFocus, "Enter lleva a URL");
        fx.Invoke(() => EnterControl(fx, "mcp-endpoint").Text = "http://external.invalid/mcp");
        EnterOnce(fx, () => EnterControl(fx, "mcp-save").HasFocus, "Enter enfoca Guardar");
        EnterOnce(fx, () => EnterControl(fx, "mcp-endpoint").HasFocus, "URL inválida devuelve foco al campo exacto");
        Assert.Equal(McpPreferencesResources.Get(locale, "endpoint_invalid"), EnterControl(fx, "mcp-editor-notice").Text.ToString());
        Assert.Empty(host.Read().Servers);
        fx.Invoke(() => EnterControl(fx, "mcp-endpoint").Text = "http://127.0.0.1:18081/mcp");
        ExportPreferenceFrame(fx, 80, 25, "rag-name-" + locale, "rag-local");
        EnterOnce(fx, () => EnterControl(fx, "mcp-save").HasFocus, "corregir URL permite guardar");
        EnterOnce(fx, () => fx.App.Overlay?.Id == "mcp-preferences", "nombre con espacios se guarda");
        var server = Assert.Single(host.Read().Servers);
        Assert.Equal("RAG local", server.DisplayName); Assert.Equal("rag-local", server.Id);
        Assert.Equal("http://127.0.0.1:18081/mcp", server.Endpoint);
        Assert.Contains("RAG local", ((ListView)EnterControl(fx, "mcp-servers")).Source!.ToList().Cast<object>().Single().ToString());
        ClickId(fx, "mcp-edit");
        fx.Invoke(() => EnterControl(fx, "mcp-name").Text = "RAG de la empresa");
        Assert.Equal("rag-local", EnterControl(fx, "mcp-id").Text.ToString());
        ClickId(fx, "mcp-save");
        Assert.Equal("RAG de la empresa", Assert.Single(host.Read().Servers).DisplayName);
        Assert.Equal("rag-local", Assert.Single(host.Read().Servers).Id);
    });

    [Fact]
    public void Mcp_credentials_are_masked_explicitly_saved_and_never_stored_in_the_json_configuration() => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "mcp-credential-data"));
        var host = new McpPreferencesHost(paths, fx.Workspace, new FileCredentialStore(Path.Combine(fx.Root, "fixture-credentials")));
        var saved = host.Save(host.Read().Revision, null, new("remote", "http", "", [], "", "https://fixture.invalid/mcp"));
        fx.StartTui(mcpPreferences: host);
        fx.Invoke(() => fx.App.ShowMcpPreferences());
        ClickId(fx, "mcp-credential");
        var secret = (TextField)EnterControl(fx, "mcp-secret");
        Assert.True(secret.Secret);
        Assert.True(secret.HasFocus);
        fx.Invoke(() => secret.Text = "fake-fixture-mcp-secret");
        EnterOnce(fx, () => EnterControl(fx, "mcp-credential-save").HasFocus, "Enter enfoca Guardar sin persistir secreto");
        Assert.Equal(saved.Revision, host.Read().Revision);
        EnterOnce(fx, () => fx.App.Overlay?.Id == "mcp-preferences", "Guardar credencial vuelve a servidores");
        Assert.True(Assert.Single(host.Read().Servers).HasCredential);
        Assert.False(Assert.Single(host.Read().Servers).Trusted);
        Assert.DoesNotContain("fake-fixture-mcp-secret", File.ReadAllText(Path.Combine(paths.ConfigDirectory, "mcp", "servers.json")));
        Assert.Equal(McpPreferencesResources.Get("es", "credential_set"), EnterControl(fx, "mcp-credential").Text.ToString());
    });

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Mcp_slash_opens_localized_configuration_and_Esc_returns_to_chat(string locale) => RunTuiTest(fx =>
    {
        fx.StartTui(localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        Type(fx, "/mcp");
        EnterOnce(fx, () => fx.App.Overlay?.Id == "mcp-preferences", "/mcp abre gestión y no informe estático");
        Assert.Equal(McpPreferencesResources.Get(locale, "add"), EnterControl(fx, "mcp-add").Text.ToString());
        Assert.Contains(McpPreferencesResources.Get(locale, "empty"), EnterControl(fx, "mcp-notice").Text.ToString());
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Esc regresa al chat");
        fx.Wait(() => fx.App.Composer!.HasFocus, "foco en chat");
    });

    [Theory]
    [InlineData(110, 35)]
    [InlineData(80, 25)]
    public void Mcp_preferences_add_http_edit_and_remove_with_Enter_Tab_and_safe_confirmation(int columns, int rows) => RunTuiTest(fx =>
    {
        var host = McpPreferencesHost.Create(fx.Workspace, Path.Combine(fx.Root, "mcp-tests-data"));
        fx.StartTui(mcpPreferences: host, initialColumns: columns, initialRows: rows);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferencias visibles");
        ChoosePreferenceCategory(fx, "extensions_skills");
        ClickId(fx, "preferences-mcp-open");
        fx.Wait(() => fx.App.Overlay?.Id == "mcp-preferences", "gestión MCP visible desde preferencias");
        Assert.True(EnterControl(fx, "mcp-add").HasFocus);
        fx.InjectKey(new Key(KeyCode.CursorDown));
        fx.Wait(() => EnterControl(fx, "mcp-back").HasFocus, "flecha abajo salta acciones deshabilitadas");
        fx.InjectKey(new Key(KeyCode.CursorUp));
        fx.Wait(() => EnterControl(fx, "mcp-add").HasFocus, "flecha arriba vuelve a Añadir");
        var actions = new[] { "mcp-add", "mcp-edit", "mcp-credential", "mcp-trust", "mcp-remove", "mcp-back" };
        Assert.Single(actions.Select(id => fx.Read(() => EnterControl(fx, id).Frame.X)).Distinct());
        var positions = actions.Select(id => fx.Read(() => EnterControl(fx, id).Frame.Y)).ToArray();
        Assert.True(positions.SequenceEqual(positions.Order()));
        Assert.True(fx.Read(() => EnterControl(fx, "mcp-back").Frame.Bottom <= fx.App.Overlay!.Viewport.Height));
        ExportPreferenceFrame(fx, columns, rows, "mcp-empty", "Añadir servidor");
        EnterOnce(fx, () => fx.App.Overlay?.Id == "mcp-editor", "Enter en Añadir abre editor");
        Assert.True(EnterControl(fx, "mcp-name").HasFocus);
        fx.Invoke(() => EnterControl(fx, "mcp-name").Text = "fixture-remote");
        EnterOnce(fx, () => EnterControl(fx, "mcp-transport").HasFocus, "Enter avanza a transporte sin guardar");
        fx.InjectKey(new Key(KeyCode.Space));
        fx.Wait(() => EnterControl(fx, "mcp-endpoint").Visible, "Espacio cambia a HTTP");
        EnterOnce(fx, () => EnterControl(fx, "mcp-endpoint").HasFocus, "Enter avanza al endpoint");
        fx.Invoke(() => EnterControl(fx, "mcp-endpoint").Text = "https://fixture.invalid/mcp");
        ExportPreferenceFrame(fx, columns, rows, "mcp-http-editor", "fixture.invalid");
        Assert.Empty(host.Read().Servers);
        EnterOnce(fx, () => EnterControl(fx, "mcp-save").HasFocus, "Enter salta los campos stdio ocultos y enfoca Guardar");
        EnterOnce(fx, () => fx.App.Overlay?.Id == "mcp-preferences", "otro Enter guarda una sola vez");
        Assert.False(Assert.Single(host.Read().Servers).Trusted);
        ClickId(fx, "mcp-trust");
        Assert.True(EnterControl(fx, "mcp-confirm-cancel").HasFocus);
        EnterOnce(fx, () => fx.App.Overlay?.Id == "mcp-preferences", "Enter predeterminado cancela autorización");
        Assert.False(Assert.Single(host.Read().Servers).Trusted);
        ClickId(fx, "mcp-edit");
        fx.Invoke(() => { EnterControl(fx, "mcp-endpoint").Text = "https://other.invalid/mcp"; EnterControl(fx, "mcp-endpoint").SetFocus(); });
        fx.InjectKey(new Key(KeyCode.Tab));
        fx.Wait(() => EnterControl(fx, "mcp-save").HasFocus, "Tab avanza hasta Guardar");
        EnterOnce(fx, () => fx.App.Overlay?.Id == "mcp-preferences", "Enter guarda edición");
        Assert.Equal("https://other.invalid/mcp", Assert.Single(host.Read().Servers).Endpoint);
        ClickId(fx, "mcp-remove");
        EnterOnce(fx, () => fx.App.Overlay?.Id == "mcp-preferences", "Enter seguro cancela eliminación");
        Assert.Single(host.Read().Servers);
        ClickId(fx, "mcp-remove"); ClickId(fx, "mcp-confirm-apply");
        fx.Wait(() => host.Read().Servers.Count == 0, "eliminación confirmada persistida");
    });

    [Fact]
    public void Mcp_stdio_form_preserves_literal_arguments_Tab_never_inserts_text_and_Enter_advances() => RunTuiTest(fx =>
    {
        var host = McpPreferencesHost.Create(fx.Workspace, Path.Combine(fx.Root, "mcp-stdio-data"));
        fx.StartTui(mcpPreferences: host, initialColumns: 100, initialRows: 30);
        fx.Invoke(() => fx.App.ShowMcpPreferences());
        ClickId(fx, "mcp-add");
        fx.Invoke(() =>
        {
            EnterControl(fx, "mcp-name").Text = "fixture-local";
            EnterControl(fx, "mcp-command").Text = Path.Combine(fx.Root, "unlaunched.exe");
            EnterControl(fx, "mcp-arguments").Text = "--path\na path with spaces";
            EnterControl(fx, "mcp-arguments").SetFocus();
        });
        fx.InjectKey(new Key(KeyCode.Tab));
        fx.Wait(() => EnterControl(fx, "mcp-directory").HasFocus, "Tab sale del editor de argumentos");
        Assert.Equal("--path\na path with spaces", EnterControl(fx, "mcp-arguments").Text.ToString());
        fx.InjectKey(new Key(KeyCode.Tab | KeyCode.ShiftMask));
        fx.Wait(() => EnterControl(fx, "mcp-arguments").HasFocus, "Shift Tab vuelve al editor");
        EnterOnce(fx, () => EnterControl(fx, "mcp-directory").HasFocus, "Enter sale del editor sin insertar línea");
        EnterOnce(fx, () => EnterControl(fx, "mcp-save").HasFocus, "Enter avanza a Guardar");
        Assert.Empty(host.Read().Servers);
        EnterOnce(fx, () => fx.App.Overlay?.Id == "mcp-preferences", "Enter guarda servidor local sin ejecutarlo");
        Assert.Equal(new[] { "--path", "a path with spaces" }, Assert.Single(host.Read().Servers).Arguments);
        ClickId(fx, "mcp-edit");
        fx.Invoke(() => EnterControl(fx, "mcp-arguments").SetFocus());
        var argumentEditor = (TextView)EnterControl(fx, "mcp-arguments");
        var beforeNewline = fx.Read(() => argumentEditor.Text.ToString());
        Assert.Equal("--path\na path with spaces", beforeNewline.Replace("\r", "", StringComparison.Ordinal));
        fx.InjectKey(new Key(KeyCode.Enter | KeyCode.ShiftMask));
        fx.Wait(() => argumentEditor.Text.ToString() != beforeNewline, "Shift Enter modifica argumentos");
        Assert.Equal(2, fx.Read(() => argumentEditor.Text.ToString().Count(c => c == '\n')));
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "mcp-preferences", "Esc descarta edición");
        Assert.Equal(2, Assert.Single(host.Read().Servers).Arguments.Length);
    });
}
