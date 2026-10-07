
using System.Drawing;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Button = Terminal.Gui.Views.Button;
using Key = Terminal.Gui.Input.Key;

namespace OmniCore.Tests;

/// <summary>
/// Pruebas de CABLEADO REAL de la TUI v0 (M4, ADR-0030 §3): el <see cref="TuiApp"/> de producción
/// corriendo sobre un <see cref="OmniServer"/> real (journal SQLite + artifacts en disco) y un
/// driver real de Terminal.Gui (DOTNET, apto para ejecución sin TTY). No hay fakes de vista: las
/// teclas se inyectan por el inyector del driver, los clics se disparan con el comando Accept de
/// las vistas reales y el polling es el de producción (<c>AddTimeout</c> de 500 ms).
///
/// <para>Todos los tests de esta clase comparten el estado estático de <c>Application</c>, así que
/// viven en UNA clase para ejecutarse en serie. La usabilidad en un terminal humano real (colores,
/// contraste, solapamientos visuales) queda como validación manual pendiente: estas pruebas
/// verifican cableado, no experiencia visual.</para>
/// </summary>
public sealed class TuiWiringTests
{
    [Theory]
    [InlineData(80, 25, "conversation")]
    [InlineData(100, 30, "conversation")]
    [InlineData(140, 40, "conversation")]
    [InlineData(100, 30, "sidebar")]
    [InlineData(100, 30, "notice")]
    [InlineData(80, 25, "settings")]
    [InlineData(120, 35, "settings")]
    [InlineData(80, 25, "account")]
    [InlineData(120, 35, "account")]
    [InlineData(80, 25, "login")]
    [InlineData(120, 35, "models")]
    [InlineData(120, 35, "picker")]
    [InlineData(80, 25, "picker")]
    [InlineData(80, 25, "commands")]
    [InlineData(120, 35, "commands")]
    [InlineData(80, 25, "activity")]
    [InlineData(120, 35, "activity")]
    [InlineData(80, 25, "table")]
    [InlineData(140, 40, "table")]
    public void Visual_frames_export_the_real_driver_cells(int columns, int rows, string scene) => RunTuiTest(fx =>
    {
        var lane = fx.Decoded<LaneCreated>().Last().LaneId;
        var content = fx.Artifacts.PutText(scene == "table"
            ? "## 📦 Dependencias\nUn ejemplo dentro de la conversación, sin crear archivos.\n\n| Paquete | Versión | Descripción | Estado |\n| :--- | ---: | :--- | :---: |\n| `react` | 18.2.0 | Librería UI | ✅ Activo |\n| `typescript` | 5.3.0 | Tipado estático | ✅ Activo |\n| `eslint` | 8.54.0 | Linter | ⚠ Pendiente |\n\n### Código de ejemplo\n```c#\npublic double Celsius(double value)\n{\n    return value * 9 / 5 + 32;\n}\n```"
            : "## Conversación\nUn diseño limpio, con `código inline` y espacio para leer.\n\n"
            + "### Métodos principales\n- `AskAsync(ct)` — Explorar el proyecto\n- `ActAsync(ct)` — Ejecutar una tarea\n\n"
            + "```c#\npublic sealed class Scenarios\n{\n    // Una respuesta con estilos\n    public string Run() { return \"Listo\"; }\n    public int Count = 42;\n}\n```\n\n"
            + "La conversación conserva el foco y el historial.", "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
        new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)
            .Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, lane, TurnId.New(), content));
        var visualPolicies = scene is "models" or "picker" ? ModelPolicyHost.Create(Path.Combine(fx.Root, "visual-catalog")) : null;
        if (visualPolicies is not null)
        {
            visualPolicies.RegisterChatGptModel("subscription-a", 8192, 2048);
            visualPolicies.Set(new ModelPolicyKeyDto("chatgpt", "subscription-a"), 0,
                "ObserveOnly", null, CancellationToken.None);
        }
        var visualTurn = scene == "activity" ? new TestTurn(fx) : null;
        fx.StartTui(policies: visualPolicies, turnHost: visualTurn,
            account: new TestAccount { Pending = true, Current = new(scene is "models" or "picker", null, null, false),
                Catalog = new[] { new AvailableChatGptModel("subscription-a", "Subscription A"),
                    new AvailableChatGptModel("subscription-b", "Subscription B") } });
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(columns, rows));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == columns && fx.App.MainWindow.Frame.Height == rows,
            "el renderer debe completar el resize");
        Type(fx, "Escribe tu siguiente instrucción…");
        if (scene == "activity")
        {
            KeyWithEffect(fx, KeyCode.Enter, () => visualTurn!.Started.IsSet, "turno activo para fotograma");
            fx.Wait(() => fx.App.Activity!.Visible, "animación visible");
            fx.Invoke(() => { for (var tick = 0; tick < 7; tick++) fx.App.AnimateActivity(); });
        }
        else if (scene == "sidebar")
        {
            fx.InjectKey(new Key(KeyCode.F2));
            fx.Wait(() => fx.App.Sidebar!.Visible, "sidebar visible en fotograma");
        }
        else if (scene == "notice")
        {
            fx.Invoke(() => fx.App.Composer!.Text = "");
            Type(fx, "/context");
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => fx.App.Overlay is not null, "menú visible en fotograma");
        }
        else if (scene == "commands")
        {
            fx.Invoke(() => fx.App.Composer!.Text = "/");
            fx.Wait(() => fx.App.MainWindow!.SubViews.Any(view => view.Id == "omni-command-helper"), "helper disponible");
        }
        else if (scene is "models" or "picker")
        {
            if (scene == "models") OpenModelMaintenance(fx); else fx.InjectKey(new Key(KeyCode.F3));
            fx.Wait(() => fx.App.Overlay is not null && fx.App.Overlay.SubViews.OfType<Button>()
                .Any(b => b.Text.ToString().Contains("subscription-b"))
                || (scene == "picker" && PickerItems(fx).Any(item => item.Contains("Subscription B"))), "catálogo descubierto visible");
        }
        else if (scene is "settings" or "account" or "login")
        {
            fx.InjectKey(new Key(KeyCode.F4));
            fx.Wait(() => fx.App.Overlay is not null, "configuración visible");
            if (scene != "settings")
            {
                Click(fx, "Cuenta ChatGPT");
                fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Cuenta") == true, "cuenta visible");
                if (scene == "login")
                {
                    Click(fx, "enlace de navegador");
                    fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("TEST-CODE"), "autorización visible");
                }
            }
        }
        using var captured = new ManualResetEventSlim();
        Exception? captureError = null;
        EventHandler<EventArgs> capture = (_, _) =>
        {
            if (captured.IsSet) return;
            var cells = fx.Application.Driver!.Contents!;
            if (cells.GetLength(0) != rows || cells.GetLength(1) != columns) return;
            var text = string.Join("\n", Enumerable.Range(0, rows).Select(y =>
                string.Concat(Enumerable.Range(0, columns).Select(x => cells[y, x].Grapheme))));
            // Input injection completing is not the same as its frame being drawn.
            // Wait for the normal loop's completed frame, retaining the timeout below.
            var marker = scene switch { "table" => "Dependencias", "activity" => "Procesando", "commands" => "Tab completar", "notice" => "Aviso", "settings" => "Configuración", "account" => "Cuenta ChatGPT", "login" => "TEST-CODE", "models" => "subscription-b", "picker" => "Subscription B", _ => "Escribe tu siguiente" };
            if (!text.Contains(marker, StringComparison.Ordinal)) return;
            if (scene == "sidebar" && !text.Contains("Workspace", StringComparison.Ordinal)) return;
            try
            {
                Assert.Equal(rows, cells.GetLength(0));
                Assert.Equal(columns, cells.GetLength(1));
                Assert.DoesNotContain("╭", text);
                // Dialogs stay frameless; table content deliberately has grid borders.
                if (scene != "table") Assert.DoesNotContain("┌", text);
                if (scene is "settings" or "account" or "login")
                {
                    Assert.Contains(scene == "settings" ? "Modelos y permisos" : scene == "account" ? "código de dispositivo" : "Cancelar", text);
                    Assert.Equal(Terminal.Gui.Drawing.LineStyle.None, fx.App.Overlay!.BorderStyle);
                    Assert.False(fx.App.Composer!.HasFocus);
                }
                else if (scene == "models")
                {
                    Assert.Contains("Catálogo actualizado", text);
                    Assert.Contains("subscription-a", text);
                    Assert.Contains("Actualizar modelos", text);
                    Assert.DoesNotContain("Registrar modelo", text);
                    Assert.Equal(Terminal.Gui.Drawing.LineStyle.None, fx.App.Overlay!.BorderStyle);
                    Assert.False(fx.App.Composer!.HasFocus);
                }
                else if (scene == "picker")
                {
                    Assert.Contains("Seleccionar modelo", text);
                    Assert.DoesNotContain("Actualizar modelos", text);
                    Assert.DoesNotContain("ObserveOnly", text);
                    Assert.DoesNotContain("🗑", text);
                }
                else if (scene == "table")
                {
                    Assert.Contains("┌", text);
                    Assert.Contains("┼", text);
                    Assert.Contains("Paquete", text);
                    Assert.Contains("react", text);
                    Assert.DoesNotContain("| :---", text);
                }
                else if (scene == "activity")
                {
                    Assert.Contains("▮", text);
                    Assert.True(fx.App.Activity!.Visible);
                    var composer = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-composer");
                    Assert.Equal(composer.Frame.Bottom, fx.App.Activity.Frame.Y);
                    Assert.Equal(2, fx.App.Activity.Frame.X);
                    Assert.Contains("Procesando", text);
                    Assert.DoesNotContain("Procesando", fx.App.Status!.Text.ToString());
                    Assert.DoesNotContain("%", fx.App.Status.Text.ToString());
                }
                else if (scene == "commands")
                {
                    Assert.Contains("/models", text);
                    Assert.Contains("Seleccionar modelo", text);
                    Assert.Contains("Tab completar", text);
                    Assert.True(fx.App.Composer!.HasFocus);
                    var helper = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-command-helper");
                    var composer = fx.App.MainWindow.SubViews.Single(view => view.Id == "omni-composer");
                    Assert.True(helper.Frame.Bottom <= composer.Frame.Y);
                }
                else if (scene == "notice")
                {
                    Assert.Contains("Aviso", text);
                    Assert.Contains("OK", text);
                    Assert.Equal(Terminal.Gui.Drawing.LineStyle.None, fx.App.Overlay!.BorderStyle);
                    Assert.False(fx.App.Composer!.HasFocus);
                }
                else
                {
                    Assert.Contains("Conversación", text);
                    Assert.Contains("AskAsync(ct)", text);
                    Assert.DoesNotContain("Mensaje", text);
                    var inputRow = Enumerable.Range(0, rows).Single(y =>
                        string.Concat(Enumerable.Range(0, columns).Select(x => cells[y, x].Grapheme)).Contains("Escribe tu siguiente", StringComparison.Ordinal));
                    var helpRow = Enumerable.Range(0, rows).Single(y =>
                        string.Concat(Enumerable.Range(0, columns).Select(x => cells[y, x].Grapheme)).Contains("Enter enviar", StringComparison.Ordinal));
                    Assert.True(helpRow > inputRow, "ayudas debajo del panel de mensaje");
                    Assert.True(fx.App.Composer!.HasFocus);
                    if (scene == "sidebar") Assert.Contains("Workspace", text);
                }
                var destination = Environment.GetEnvironmentVariable("OMNICORE_TUI_SNAPSHOT_DIR");
                if (string.IsNullOrWhiteSpace(destination)) return;
                Directory.CreateDirectory(destination);
                // Export the driver's actual desired screen buffer, not an ANSI reconstruction
                // or a desktop capture. Only this synthetic fixture enters the artifact.
                var svg = new System.Text.StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{columns * 12}\" height=\"{rows * 24}\">");
                for (var y = 0; y < rows; y++)
                for (var x = 0; x < columns; x++)
                {
                    var cell = cells[y, x];
                    var attribute = cell.Attribute ?? fx.App.MainWindow!.GetScheme().Normal;
                    static string Hex(Terminal.Gui.Drawing.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                    svg.Append($"<rect x=\"{x * 12}\" y=\"{y * 24}\" width=\"12\" height=\"24\" fill=\"{Hex(attribute.Background)}\"/>");
                    if (!string.IsNullOrWhiteSpace(cell.Grapheme))
                        svg.Append($"<text x=\"{x * 12}\" y=\"{y * 24 + 19}\" font-family=\"Consolas, monospace\" font-size=\"20\" fill=\"{Hex(attribute.Foreground)}\">{System.Security.SecurityElement.Escape(cell.Grapheme)}</text>");
                }
                svg.Append("</svg>");
                File.WriteAllText(Path.Combine(destination, $"{scene}-{columns}x{rows}.svg"), svg.ToString());
                File.WriteAllText(Path.Combine(destination, $"{scene}-{columns}x{rows}.txt"), text);
            }
            catch (Exception error) { captureError = error; }
            finally { captured.Set(); }
        };
        fx.Application.LayoutAndDrawComplete += capture;
        fx.Invoke(() =>
        {
            fx.App.MainWindow!.SetNeedsLayout();
            fx.App.MainWindow.SetNeedsDraw();
        });
        try { Assert.True(captured.Wait(TimeSpan.FromSeconds(10)), "captura del renderer completada"); }
        finally { fx.Application.LayoutAndDrawComplete -= capture; }
        if (captureError is not null) throw captureError;
        visualTurn?.Release.Set();
    });

    [Fact]
    public void Rich_conversation_is_read_only_and_polling_preserves_history_position() => RunTuiTest(fx =>
    {
        var lane = fx.Server.AcquireStore().ReadFrom(fx.Server.LastSessionId()!, 1)
            .Select(e => fx.Server.AcquireCodecs().Decode(e)).OfType<LaneCreated>().Last().LaneId;
        var content = fx.Artifacts.PutText("## Key Methods\n- `AskAsync(ct)` — Run Explorer\n```c#\npublic void Run() { }\n```\n"
            + string.Join("\n", Enumerable.Range(0, 40).Select(i => "History " + i)),
            "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
        new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)
            .Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, lane, TurnId.New(), content));
        fx.StartTui();
        fx.Wait(() => fx.App.Conversation!.Text.Contains("Key Methods"), "respuesta durable renderizada");
        Assert.True(fx.App.Conversation!.ReadOnly);
        Assert.True(fx.App.Conversation.ScrollBars);
        Assert.Contains("  </> c#", fx.App.Conversation.Text);
        Assert.DoesNotContain("```", fx.App.Conversation.Text);
        var cells = fx.App.Conversation.GetAllLines().SelectMany(line => line).ToArray();
        if (Environment.GetEnvironmentVariable("NO_COLOR") is null)
        {
            var background = new Terminal.Gui.Drawing.Color("#061822");
            Assert.Equal(background, fx.App.MainWindow!.GetScheme().Normal.Background);
            Assert.Equal(new Terminal.Gui.Drawing.Color("#293648"), fx.App.Composer!.GetScheme().Focus.Background);
            Assert.Equal(new Terminal.Gui.Drawing.Color("#F4F7FB"), fx.App.Composer.GetScheme().Normal.Foreground);
            Assert.Equal(background, fx.App.Conversation.GetScheme().Normal.Background);
            Assert.All(cells, cell => Assert.NotEqual(Terminal.Gui.Drawing.Color.None, cell.Attribute?.Background));
            Assert.Contains(cells, cell => cell.Attribute?.Foreground == new Terminal.Gui.Drawing.Color("#53B8F5"));
            Assert.Contains(cells, cell => cell.Attribute?.Foreground == new Terminal.Gui.Drawing.Color("#EF9A70"));
            Assert.Contains(cells, cell => cell.Attribute?.Background == new Terminal.Gui.Drawing.Color("#0A2330"));
            Assert.Contains(cells, cell => cell.Attribute?.Foreground == new Terminal.Gui.Drawing.Color("#C8A0F5"));
            Assert.Contains(cells, cell => cell.Attribute?.Foreground == new Terminal.Gui.Drawing.Color("#F1D58A"));
            Assert.Contains(cells, cell => cell.Attribute?.Foreground == new Terminal.Gui.Drawing.Color("#67D4D0"));
        }
        Thread.Sleep(300); // Let the first styled frame render before navigating history.
        fx.Invoke(() => fx.App.Conversation.MoveEnd());
        fx.Wait(() => fx.App.Conversation.CurrentRow > 30, "historial navegable");
        var row = fx.App.Conversation.CurrentRow;
        Thread.Sleep(1100); // More than two production polls, not a handler-only test.
        Assert.Equal(row, fx.App.Conversation.CurrentRow);
        fx.Invoke(() => fx.App.Conversation.SelectAll());
        fx.Wait(() => fx.App.Conversation.SelectedText.Contains("public void Run()"), "el código coloreado sigue siendo seleccionable");
        var selection = fx.App.Conversation.SelectedText;
        Assert.DoesNotContain("┃", selection);
        Assert.False(selection.Contains('\u001b'), "la selección no debe incluir secuencias ANSI");
        // Exercise the actual Copy command without touching the user's clipboard.
        var clipboard = new FakeClipboard(false, false);
        fx.Invoke(() =>
        {
            fx.Application.Driver!.Clipboard = clipboard;
            fx.App.Conversation.InvokeCommand(Command.Copy);
        });
        fx.Wait(() => clipboard.GetClipboardData() == selection, "copiar selección entrega texto plano sin ANSI");
        Thread.Sleep(1100); // Selection must survive the same production polling path.
        Assert.Equal(selection, fx.App.Conversation.SelectedText);
        Assert.True(fx.App.Composer!.HasFocus);
    });

    [Theory]
    [InlineData(48, false)]
    [InlineData(80, false)]
    [InlineData(140, false)]
    [InlineData(48, true)]
    [InlineData(80, true)]
    [InlineData(140, true)]
    public void Conversation_drawn_cells_cover_markdown_unicode_roles_and_plain_copy(int columns, bool noColor) => RunTuiTest(fx =>
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", noColor ? "1" : null);
            var run = fx.Server.LastRunId()!;
            var lane = fx.Decoded<LaneCreated>().Last().LaneId;
            const string answer = "# ✅ QA título\nProsa **fuerte** y `inline`.\n- lista uno\n- lista dos 😊\n\n"
                + "| Archivo | Estado |\n| :--- | ---: |\n| `a.cs` | ✅ |\n| `b.html` | 中文 |\n\n"
                + "```c#\npublic int value = 42;\n```\nQA_FIN";
            var stream = new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
            Assert.Equal("ok", fx.Server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"USER_QA **literal**\"}"), CancellationToken.None).Status);
            var artifact = fx.Artifacts.PutText(answer, "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
            stream.Append(new AssistantMessageRecorded(run, lane, TurnId.New(), artifact));
            const int rows = 46;
            fx.StartTui(initialColumns: columns, initialRows: rows);
            fx.Wait(() => fx.App.MainWindow!.Frame.Width == columns && fx.App.MainWindow.Frame.Height == rows, "tamaño de QA aplicado");
            var frame = CaptureConversationFrame(fx, columns, rows, "QA_FIN");
            var text = string.Join("\n", Enumerable.Range(0, rows).Select(y => string.Concat(Enumerable.Range(0, columns).Select(x => frame[y, x].Grapheme))));
            foreach (var token in new[] { "QA título", "Prosa fuerte y inline.", "• lista uno", "• lista dos", "Archivo", "a.cs", "b.html", "public int value = 42;", "USER_QA **literal**", "┌", "┼", "└" })
                Assert.True(text.Contains(token, StringComparison.Ordinal), "faltó contenido dibujado: " + token);
            // A wide grapheme owns two physical cells; don't concatenate its continuation as prose.
            var chinese = (from y in Enumerable.Range(0, rows) from x in Enumerable.Range(0, columns - 2)
                where frame[y, x].Grapheme == "中" select (x, y)).ToArray();
            var chineseStart = Assert.Single(chinese);
            Assert.Equal("文", frame[chineseStart.y, chineseStart.x + 2].Grapheme);
            Assert.False(text.Contains("```", StringComparison.Ordinal));
            Assert.False(text.Contains("| :---", StringComparison.Ordinal));
            Assert.False(text.Contains("Mensaje", StringComparison.Ordinal));
            var allCells = frame.Cast<Terminal.Gui.Drawing.Cell>().ToArray();
            Assert.Contains(allCells, cell => cell.Grapheme.Contains("😊", StringComparison.Ordinal));
            Assert.Contains(allCells, cell => cell.Grapheme.Contains("✅", StringComparison.Ordinal));
            if (noColor)
            {
                Assert.All(allCells.Where(cell => cell.Grapheme == "┃"), cell => Assert.Equal(Terminal.Gui.Drawing.Color.None, cell.Attribute?.Foreground));
            }
            else
            {
                foreach (var color in new[] { "#53B8F5", "#EF9A70", "#C8A0F5", "#67D4D0" })
                    Assert.Contains(allCells, cell => cell.Attribute?.Foreground == new Terminal.Gui.Drawing.Color(color));
                Assert.Contains(allCells, cell => cell.Grapheme == "┃" && cell.Attribute?.Foreground == new Terminal.Gui.Drawing.Color("#B47CE7"));
                Assert.Contains(allCells, cell => cell.Grapheme == "┃" && cell.Attribute?.Foreground == new Terminal.Gui.Drawing.Color("#4DBAC9"));
                Assert.Contains(allCells, cell => cell.Attribute?.Background == new Terminal.Gui.Drawing.Color("#0A2330"));
            }
            var conversationRows = fx.App.Conversation!.GetAllLines();
            Assert.All(conversationRows, line => Assert.True(Terminal.Gui.Text.StringExtensions.GetColumns(string.Concat(line.Select(cell => cell.Grapheme)), false) <= fx.App.Conversation.Viewport.Width));
            var clipboard = new FakeClipboard(false, false);
            fx.Invoke(() =>
            {
                fx.Application.Driver!.Clipboard = clipboard;
                fx.App.Conversation.SelectAll();
                fx.App.Conversation.InvokeCommand(Command.Copy);
            });
            fx.Wait(() => clipboard.GetClipboardData()?.Contains("QA_FIN", StringComparison.Ordinal) == true, "contenido copiado completo");
            var copied = clipboard.GetClipboardData()!;
            Assert.Contains("public int value = 42;", copied);
            Assert.Contains("USER_QA **literal**", copied);
            Assert.Contains("😊", copied);
            Assert.Contains("中文", copied);
            Assert.False(copied.Contains('┃'));
            Assert.False(copied.Contains('\u001b'));
            Assert.True(fx.App.Composer!.HasFocus);
        }
        finally { Environment.SetEnvironmentVariable("NO_COLOR", previous); }
    });

    [Theory]
    [InlineData("c#", "public int value = 42;", "public", "#C8A0F5")]
    [InlineData("html", "<button class=\"qa\">Hola</button>", "button", "#67D4D0")]
    [InlineData("progress", "DEFINE VARIABLE value AS INTEGER NO-UNDO.", "DEFINE", "#C8A0F5")]
    public void Requested_languages_are_drawn_as_code_and_copied_without_markup_loss(string language, string source, string token, string color) => RunTuiTest(fx =>
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            var lane = fx.Decoded<LaneCreated>().Last().LaneId;
            var artifact = fx.Artifacts.PutText("```" + language + "\n" + source + "\n```\nQA_LANG_FIN", "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
            new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)
                .Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, lane, TurnId.New(), artifact));
            fx.StartTui();
            var frame = CaptureConversationFrame(fx, 80, 25, "QA_LANG_FIN");
            var lines = Enumerable.Range(0, 25).Select(y => string.Concat(Enumerable.Range(0, 80).Select(x => frame[y, x].Grapheme))).ToArray();
            var row = Array.FindIndex(lines, line => line.Contains(source, StringComparison.Ordinal));
            Assert.True(row >= 0, "el código completo debe dibujarse literalmente");
            var tokenStart = lines[row].IndexOf(token, StringComparison.Ordinal);
            for (var offset = 0; offset < token.Length; offset++)
                Assert.Equal(new Terminal.Gui.Drawing.Color(color), frame[row, tokenStart + offset].Attribute?.Foreground);
            var clipboard = new FakeClipboard(false, false);
            fx.Invoke(() =>
            {
                fx.Application.Driver!.Clipboard = clipboard;
                fx.App.Conversation!.SelectAll();
                fx.App.Conversation.InvokeCommand(Command.Copy);
            });
            fx.Wait(() => clipboard.GetClipboardData()?.Contains(source, StringComparison.Ordinal) == true, "copia literal del código");
            Assert.False(clipboard.GetClipboardData()!.Contains('\u001b'));
        }
        finally { Environment.SetEnvironmentVariable("NO_COLOR", previous); }
    });

    private static Terminal.Gui.Drawing.Cell[,] CaptureConversationFrame(TuiFixture fx, int columns, int rows, string marker)
    {
        using var captured = new ManualResetEventSlim();
        Terminal.Gui.Drawing.Cell[,]? result = null;
        EventHandler<EventArgs> handler = (_, _) =>
        {
            if (captured.IsSet) return;
            var cells = fx.Application.Driver!.Contents!;
            if (cells.GetLength(0) != rows || cells.GetLength(1) != columns) return;
            var drawn = string.Join("\n", Enumerable.Range(0, rows).Select(y => string.Concat(Enumerable.Range(0, columns).Select(x => cells[y, x].Grapheme))));
            if (!drawn.Contains(marker, StringComparison.Ordinal)) return;
            result = (Terminal.Gui.Drawing.Cell[,])cells.Clone();
            captured.Set();
        };
        fx.Application.LayoutAndDrawComplete += handler;
        try
        {
            fx.Invoke(() => { fx.App.MainWindow!.SetNeedsLayout(); fx.App.MainWindow.SetNeedsDraw(); });
            Assert.True(captured.Wait(TimeSpan.FromSeconds(10)), "fotograma completo de conversación dibujado");
            Assert.Null(fx.LoopError);
            return Assert.IsType<Terminal.Gui.Drawing.Cell[,]>(result);
        }
        finally { fx.Application.LayoutAndDrawComplete -= handler; }
    }

    [Fact]
    public void Composer_grows_wraps_and_sends_all_lines_with_a_block_cursor() => RunTuiTest(fx =>
    {
        var host = new TestTurn(fx);
        fx.StartTui(turnHost: host);
        Type(fx, "primera línea");
        fx.InjectKey(new Key(KeyCode.Enter | KeyCode.ShiftMask));
        fx.Wait(() => fx.App.Composer!.Text.Contains('\n'), "Shift+Enter agrega salto sin enviar");
        Assert.Equal(0, host.Calls);
        foreach (var character in "segunda línea") fx.InjectKey(new Key(character));
        fx.Wait(() => fx.App.Composer!.Text.Replace("\r\n", "\n") == "primera línea\nsegunda línea",
            "la segunda línea se edita sin borrar la primera");
        Assert.True(fx.App.Composer!.Multiline);
        Assert.True(fx.App.Composer.WordWrap);
        fx.Wait(() => fx.App.Composer!.Frame.Height == 2, "composer crece por salto de línea");
        var panel = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-composer");
        Assert.Equal(4, panel.Frame.Height);
        fx.Wait(() => fx.App.Composer!.Cursor.Style == CursorStyle.SteadyBlock, "cursor de bloque dibujado");
        Assert.True(fx.App.Composer!.Multiline);
        Assert.True(fx.App.Composer.WordWrap);
        KeyWithEffect(fx, KeyCode.Enter, () => host.Started.IsSet, "Enter envía sin insertar otro salto");
        Assert.Equal("primera línea\nsegunda línea", host.Input!.Replace("\r\n", "\n"));
        fx.Wait(() => fx.App.Composer!.Frame.Height == 1, "composer vuelve a altura mínima");
        host.Release.Set();
        fx.Wait(() => !fx.App.Activity!.Visible, "turno finaliza");
        Type(fx, new string('a', 100));
        fx.Wait(() => fx.App.Composer!.Frame.Height >= 2, "texto largo crece por ajuste visual");
    });

    [Fact]
    public void New_turn_follows_the_bottom_but_new_content_does_not_drag_a_reader() => RunTuiTest(fx =>
    {
        var host = new TestTurn(fx);
        var lane = fx.Decoded<LaneCreated>().Last().LaneId;
        var artifact = fx.Artifacts.PutText(string.Join('\n', Enumerable.Range(0, 70).Select(i => "history " + i)), "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
        var stream = new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        stream.Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, lane, TurnId.New(), artifact));
        fx.StartTui(turnHost: host);
        Type(fx, "siguiente pregunta");
        KeyWithEffect(fx, KeyCode.Enter, () => host.Started.IsSet, "se envía turno nuevo");
        fx.Wait(() => fx.App.Conversation!.CurrentRow >= 70, "enviar lleva al final del historial");
        host.Release.Set();
        fx.Wait(() => fx.App.Conversation!.Text.Contains("respuesta conectada"), "respuesta durable");
        fx.Wait(() => fx.App.Conversation!.CurrentRow == fx.App.Conversation.GetAllLines().Count - 1,
            "respuesta conserva seguimiento al final: cursor=" + fx.App.Conversation!.CurrentRow + " filas=" + fx.App.Conversation.GetAllLines().Count
            + " scroll=" + fx.App.Conversation.VerticalScrollBar.Value + " visible=" + fx.App.Conversation.VerticalScrollBar.VisibleContentSize + " total=" + fx.App.Conversation.VerticalScrollBar.ScrollableContentSize);
        fx.Invoke(() => fx.App.Conversation!.SetFocus());
        fx.InjectKey(new Key(KeyCode.Home | KeyCode.CtrlMask));
        fx.Wait(() => fx.App.Conversation!.CurrentRow == 0, "usuario relee inicio");
        var newArtifact = fx.Artifacts.PutText("mensaje mientras relees", "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
        var currentLane = fx.Decoded<LaneCreated>().Last().LaneId;
        new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)
            .Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, currentLane, TurnId.New(), newArtifact));
        fx.Wait(() => fx.App.Conversation!.Text.Contains("mensaje mientras relees"), "contenido nuevo recibido");
        Assert.Equal(0, fx.App.Conversation!.CurrentRow);
        Assert.Equal(0, fx.App.Conversation.Viewport.Y);
    });

    // ------------------------------------------------------------------ composer: foco, tecleo, envío
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conversation_accents_do_not_leak_into_copy_even_without_color(bool noColor) => RunTuiTest(fx =>
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", noColor ? "1" : null);
            var lane = fx.Decoded<LaneCreated>().Last().LaneId;
            var text = "Literal ┃ must remain\n```c#\n    int x = 42;\n```";
            var artifact = fx.Artifacts.PutText(text, "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
            new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)
                .Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, lane, TurnId.New(), artifact));
            fx.StartTui();
            fx.Invoke(() => fx.App.Conversation!.SelectAll());
            fx.Wait(() => fx.App.Conversation!.SelectedText.Contains("int x = 42;"), "copiar cuerpo completo");
            var selected = fx.App.Conversation!.SelectedText;
            Assert.Equal(1, selected.Count(character => character == '┃'));
            Assert.Contains("Literal ┃ must remain", selected);
            Assert.DoesNotContain("┃  ◈", selected);
            Assert.False(selected.Contains('\u001b'));
        }
        finally { Environment.SetEnvironmentVariable("NO_COLOR", previous); }
    });

    [Fact]
    public void Composer_receives_initial_focus_types_and_submits_to_the_journal() => RunTuiTest(fx =>
    {
        fx.StartTui();

        // Defecto corregido: sin pulsar Tab, el foco inicial es el composer y lo tecleado llega.
        fx.Wait(() => fx.App.Composer!.HasFocus, "el composer debe tener el foco inicial");

        // ADR-0031: sin cuota informada la status line muestra «—», nunca un dato inventado.
        Assert.Contains("—", fx.App.Status!.Text?.ToString() ?? "");

        Type(fx, "hola omni tui");
        fx.Wait(() => fx.App.Composer!.Text?.ToString() == "hola omni tui", "las teclas inyectadas deben llegar al composer enfocado");

        KeyWithEffect(fx, KeyCode.Enter, () => string.IsNullOrEmpty(fx.App.Composer!.Text?.ToString()),
            "el Enter no limpia el composer tras el envío");
        fx.Wait(() => fx.App.Conversation!.Text?.ToString().Contains("hola omni tui") == true, "la conversación proyecta el input enviado desde el journal");

        var received = fx.Decoded<UserInputReceived>()
            .Any(input => (input.InputPartsJson ?? "").Contains("hola omni tui"));
        Assert.True(received, "el input del TUI debe quedar durable en el journal como UserInputReceived");
    });

    // ------------------------------------------------------------------ sidebar: F2 + resizes reales

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Composer_calls_turn_host_and_projects_its_durable_answer_without_blocking_keyboard(bool noColor) => RunTuiTest(fx =>
    {
        var host = new TestTurn(fx);
        fx.StartTui(turnHost: host);
        Type(fx, "hola conectado");
        KeyWithEffect(fx, KeyCode.Enter, () => host.Started.IsSet, "Enter debe iniciar el runtime");
        fx.Wait(() => fx.App.Activity?.Visible == true, "barra visible durante inferencia");
        using var activityFrameDrawn = new ManualResetEventSlim();
        EventHandler<EventArgs> activityFrame = (_, _) =>
        {
            var cells = fx.Application.Driver!.Contents!;
            var text = string.Join("\n", Enumerable.Range(0, cells.GetLength(0)).Select(y =>
                string.Concat(Enumerable.Range(0, cells.GetLength(1)).Select(x => cells[y, x].Grapheme))));
            if (fx.App.Activity?.Visible == true && text.Contains("Procesando", StringComparison.Ordinal))
                activityFrameDrawn.Set();
        };
        fx.Application.LayoutAndDrawComplete += activityFrame;
        try
        {
            fx.Invoke(() =>
            {
                fx.App.MainWindow!.SetNeedsLayout();
                fx.App.MainWindow.SetNeedsDraw();
            });
            Assert.True(activityFrameDrawn.Wait(TimeSpan.FromSeconds(10)),
                "fotograma completo con la barra Procesando antes de verificar colores");
        }
        finally { fx.Application.LayoutAndDrawComplete -= activityFrame; }

        Assert.Equal(11, fx.App.Activity!.SubViews.Count);
        var firstFrame = fx.App.Activity.SubViews.Select(cell => (cell.Text, cell.GetScheme().Normal)).ToArray();
        if (noColor)
            Assert.All(firstFrame, cell =>
            {
                Assert.Equal(Terminal.Gui.Drawing.Color.None, cell.Normal.Foreground);
                Assert.Equal(Terminal.Gui.Drawing.Color.None, cell.Normal.Background);
            });
        else Assert.All(firstFrame, cell => Assert.Contains(cell.Normal.Foreground,
            new[] { "#12303D", "#205061", "#30788B", "#4DBAC9", "#83C6DE", "#B47CE7" }
                .Select(value => new Terminal.Gui.Drawing.Color(value))));
        fx.Invoke(() => { for (var tick = 0; tick < 5; tick++) fx.App.AnimateActivity(); });
        fx.Wait(() => !firstFrame.SequenceEqual(fx.App.Activity.SubViews.Select(cell => (cell.Text, cell.GetScheme().Normal))), "la barra debe animarse");
        Type(fx, "borrador");
        fx.Wait(() => fx.App.Composer!.Text == "borrador", "el teclado sigue disponible durante inferencia");
        Assert.Equal(1, host.Calls);
        host.Release.Set();
        fx.Wait(() => fx.App.Conversation!.Text.Contains("respuesta conectada"), "respuesta del runtime proyectada desde journal");
        fx.Wait(() => fx.App.Activity.Visible == false, "la barra desaparece al terminar");
        Assert.Contains("semilla de prueba", fx.App.Conversation!.Text);
        fx.Wait(() => fx.App.Status!.Text!.ToString()!.Contains("Listo"), "estado completado visible");
        Assert.Equal("borrador", fx.App.Composer!.Text);
    }, noColor);

    private sealed class TestTurn(TuiFixture fixture) : ITuiTurnHost
    {
        public readonly ManualResetEventSlim Started = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public int Calls;
        public string? Input;
        public Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Input = input;
            fixture.Server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"act\"," + JsonObj.Field("objective", input) + "}"), cancellationToken);
            fixture.Server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\"," + JsonObj.Field("text", input) + "}"), cancellationToken);
            Started.Set();
            Release.Wait(cancellationToken);
            var lane = fixture.Server.AcquireStore().ReadFrom(fixture.Server.LastSessionId()!, 1)
                .Select(e => fixture.Server.AcquireCodecs().Decode(e)).OfType<LaneCreated>().Last().LaneId;
            var content = fixture.Artifacts.PutText("respuesta conectada", "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
            new EventStream(fixture.Server.AcquireStore(), fixture.Server.AcquireCodecs(), fixture.Server.LastSessionId()!)
                .Append(new AssistantMessageRecorded(fixture.Server.LastRunId()!, lane, TurnId.New(), content));
            return System.Threading.Tasks.Task.FromResult(0);
        }
    }

    [Fact]
    public void Sidebar_and_conversation_respond_to_f2_and_real_screen_resizes() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var conversation = fx.App.MainWindow!.SubViews.ElementAt(1);

        // Arranque estrecho (80 cols bajo driver DOTNET): sidebar cerrado, conversación a ancho completo.
        Assert.False(fx.App.Sidebar!.Visible);
        Assert.Equal(80, conversation.Frame.Width);
        Assert.Equal(Terminal.Gui.Drawing.LineStyle.None, fx.App.MainWindow!.BorderStyle);
        Assert.Equal(24, fx.App.Status!.Frame.Y);
        Assert.True(fx.App.Composer!.Frame.Width > 60);
        Assert.Equal(Terminal.Gui.Drawing.LineStyle.None, fx.App.Sidebar.BorderStyle);

        // F2 abre el sidebar; a 80 columnas el layout es Overlay (centrado), la conversación no se reduce.
        fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.Sidebar!.Visible, "F2 debe abrir el sidebar");
        fx.Wait(() => fx.App.Sidebar!.Frame.Width == 44, "a 80 cols el sidebar Overlay mide 44");
        Assert.Equal(80, conversation.Frame.Width);
        Assert.Equal(0, fx.App.Sidebar!.Frame.Y);
        Assert.Equal(25, fx.App.Sidebar.Frame.Height);

        // Terminal ancha (120): el resize real del driver reencuadra la ventana; sidebar apilado a la derecha.
        fx.Invoke(() => fx.Application.Screen = new Rectangle(0, 0, 120, 40));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == 120, "la ventana debe ocupar la pantalla tras el resize del driver");
        fx.Wait(() => fx.App.Sidebar!.Frame.Width == 40, "a 120 cols el sidebar apilado mide 40");
        Assert.Equal(80, fx.App.Sidebar!.Frame.X);
        Assert.Equal(80, conversation.Frame.Width); // 120 - 40 sidebar; no outer chrome
        fx.Wait(() => fx.App.Composer!.SuperView!.Frame.Width == 78 && fx.App.Sidebar!.Frame.Height == 40,
            "el compositor se limita a la columna izquierda y el panel ocupa toda la altura");
        var composerFrame = fx.App.Composer!.SuperView!.Frame;
        Assert.Equal(0, fx.App.Sidebar.Frame.Y);
        Assert.Equal(40, fx.App.Sidebar.Frame.Height);
        Assert.True(composerFrame.Right < fx.App.Sidebar.Frame.X);
        Assert.Equal(80, fx.App.Status!.Frame.Width);

        // F2 cierra el sidebar: la conversación recupera el ancho completo.
        fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => !fx.App.Sidebar!.Visible, "F2 debe cerrar el sidebar");
        fx.Wait(() => conversation.Frame.Width == 120, "sin sidebar la conversación ocupa 120");
        fx.Wait(() => fx.App.Composer!.SuperView!.Frame.Width == 118, "cerrar panel recupera el ancho del mensaje");

        // Vuelta a estrecho sin errores de layout.
        fx.Invoke(() => fx.Application.Screen = new Rectangle(0, 0, 80, 25));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == 80, "condicion no alcanzada en el tope de espera");
        Assert.False(fx.App.Sidebar!.Visible);
    });

    // ------------------------------------------------------------------ autocompletado: catálogo y workspace reales

    [Fact]
    public void Autocomplete_completes_host_commands_and_workspace_paths() => RunTuiTest(fx =>
    {
        fx.StartTui();

        Type(fx, "/pl");
        fx.Wait(() => fx.App.Completion!.Text?.ToString().Contains("/plan") == true, "el catálogo real del Host completa /plan");
        Assert.Same(fx.App.MainWindow, fx.App.Completion!.SuperView);
        var composerPanel = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-composer");
        var helper = fx.App.MainWindow.SubViews.Single(view => view.Id == "omni-command-helper");
        fx.Wait(() => helper.Frame.Height > 0, "helper dibujado");
        Assert.True(helper.Frame.Bottom <= composerPanel.Frame.Y,
            "el helper debe quedar encima del mensaje, sin taparlo");
        Assert.Contains("Consultar el plan actual", helper.SubViews.OfType<ListView>().Single().Source!.ToList().Cast<object>().Single().ToString());

        fx.Invoke(() => fx.App.Composer!.Text = "");
        Type(fx, "@src/");
        fx.Wait(() => fx.App.Completion!.Text?.ToString().Contains("src/Alpha.cs") == true, "el workspace real completa src/Alpha.cs");
        Assert.Contains("src/Beta.cs", fx.App.Completion!.Text?.ToString() ?? "");
        Assert.DoesNotContain(".git", fx.App.Completion!.Text?.ToString() ?? "");
    });

    [Fact]
    public void Slash_helper_navigates_completes_and_esc_preserves_the_draft() => RunTuiTest(fx =>
    {
        fx.StartTui();
        Type(fx, "/");
        fx.Wait(() => fx.App.MainWindow!.SubViews.Any(view => view.Id == "omni-command-helper"), "slash abre helper");
        var helper = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-command-helper");
        var list = helper.SubViews.OfType<ListView>().Single();
        Assert.True(list.Source!.Count > 5);
        Assert.True(fx.App.Composer!.HasFocus);
        KeyWithEffect(fx, KeyCode.CursorDown, () => list.SelectedItem == 1, "abajo selecciona siguiente comando");
        KeyWithEffect(fx, KeyCode.CursorUp, () => list.SelectedItem == 0, "arriba recupera primero");
        KeyWithEffect(fx, KeyCode.CursorUp, () => list.SelectedItem == list.Source.Count - 1, "wrap al último");
        var expected = list.Source.ToList()[list.SelectedItem!.Value]!.ToString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        KeyWithEffect(fx, KeyCode.Tab, () => fx.App.Composer.Text.ToString() == expected + " ", "Tab completa sin ejecutar");
        Assert.Null(fx.App.Overlay);
        Assert.DoesNotContain(fx.App.MainWindow.SubViews, view => view.Id == "omni-command-helper");
        fx.Invoke(() => fx.App.Composer.Text = "/model");
        fx.Wait(() => fx.App.MainWindow.SubViews.Any(view => view.Id == "omni-command-helper"), "filtra prefijo");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Composer.Text.ToString() == "/models ", "Enter completa prefijo sin ejecutarlo");
        Assert.Null(fx.App.Overlay);
        fx.Invoke(() => fx.App.Composer.Text = "/pl");
        fx.Wait(() => fx.App.MainWindow.SubViews.Any(view => view.Id == "omni-command-helper"), "helper reabierto");
        KeyWithEffect(fx, KeyCode.Esc, () => !fx.App.MainWindow.SubViews.Any(view => view.Id == "omni-command-helper"), "Esc cierra solo sugerencias");
        Assert.Equal("/pl", fx.App.Composer.Text);
        Assert.True(fx.App.Composer.HasFocus);
    });

    // ------------------------------------------------------------------ comandos slash → overlays con datos del Host

    [Fact]
    public void Slash_commands_render_real_host_data_in_overlays() => RunTuiTest(fx =>
    {
        fx.StartTui();

        Type(fx, "/tools");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "el Enter de /tools no abre el overlay");
        var overlayText = OverlayText(fx.App.Overlay!);
        Assert.Contains("filesystem.read", overlayText);

        // Esc cierra el overlay de aviso y devuelve el foco al composer.
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Esc no cierra el overlay de aviso");
        fx.Wait(() => fx.App.Composer!.HasFocus, "el foco vuelve al composer tras cerrar el overlay");

        Type(fx, "/context");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "el Enter de /context no abre el overlay");
        Assert.Contains("snapshot", OverlayText(fx.App.Overlay!));

        // El mismo cableado Query→overlay sirve la vista de plan desde el estado real del journal.
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Esc no cierra el overlay de /context");
        Type(fx, "/plan");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "el Enter de /plan no abre el overlay");
        Assert.NotNull(fx.App.Overlay);
    });

    // ------------------------------------------------------------------ interacción de elección: clic real y Esc por defecto

    [Fact]
    public void Choice_interaction_overlay_responds_by_real_click_and_esc_default() => RunTuiTest(fx =>
    {
        var session = fx.Server.LastSessionId()!;
        var interaction = InteractionId.New();
        fx.AppendInteraction(interaction, "Permission",
            "{\"operation\":\"tool.authorize\",\"toolOrExecutable\":\"filesystem.read\",\"target\":\"src/Alpha.cs\"}",
            "[{\"id\":\"allow_once\"},{\"id\":\"deny\"}]", "deny");
        fx.StartTui();

        fx.Wait(() => fx.App.Overlay is not null, "la interacción pendiente debe abrir un overlay");
        Assert.Contains("src/Alpha.cs", OverlayText(fx.App.Overlay!));
        var buttons = fx.App.Overlay!.SubViews.OfType<Button>().ToArray();
        Assert.Equal(2, buttons.Length);
        fx.Wait(() => buttons[1].HasFocus, "la opción predeterminada (deny) recibe el foco inicial: Enter no permite por accidente");

        // Camino real de teclado: el usuario enfoca "Permitir una vez" y pulsa Enter.
        fx.Invoke(() => buttons[0].SetFocus());
        Thread.Sleep(100);
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is null, "el Enter sobre el botón no responde la interacción");
        var resolved = fx.Decoded<InteractionResolved>().Single(r => r.InteractionId.Equals(interaction));
        Assert.Equal("allow_once", resolved.OptionId);
        Assert.Equal(InteractionCause.User, resolved.Cause);

        // Una segunda interacción pendiente: Esc responde con la opción predeterminada (deny).
        var second = InteractionId.New();
        fx.AppendInteraction(second, "Permission",
            "{\"toolOrExecutable\":\"filesystem.write\",\"target\":\"src/Alpha.cs\"}",
            "[{\"id\":\"allow_once\"},{\"id\":\"deny\"}]", "deny");
        fx.Wait(() => fx.App.Overlay is not null, "la segunda interacción debe abrir un overlay");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Esc no responde la segunda interacción con la predeterminada");
        var secondResolved = fx.Decoded<InteractionResolved>().Single(r => r.InteractionId.Equals(second));
        Assert.Equal("deny", secondResolved.OptionId);
        Assert.Equal(InteractionCause.User, secondResolved.Cause);
        _ = session;
    });

    [Fact]
    public void Model_route_consent_allow_resumes_once_without_submitting_a_new_turn() => RunTuiTest(fx =>
    {
        var host = new RouteResumeTurnHost();
        var (interaction, route) = PublishRouteConsent(fx);
        host.EscalationInteractionId = interaction.ToString();
        var inputCount = fx.Decoded<UserInputReceived>().Count();
        fx.StartTui(turnHost: host);

        fx.Wait(() => fx.App.Overlay is not null, "el consentimiento de ruta debe abrir el overlay real");
        Assert.Equal(InteractionKind.ModelRouteConsent,
            Assert.Single(fx.Decoded<InteractionRequested>(), item => item.InteractionId == interaction).Kind);
        var buttons = fx.App.Overlay!.SubViews.OfType<Button>().ToArray();
        Assert.Equal(2, buttons.Length);
        fx.Invoke(() => buttons[1].SetFocus());
        KeyWithEffect(fx, KeyCode.Enter, () => host.ResumeCount == 1,
            "allow_route debe iniciar una única reanudación");
        fx.Wait(() => fx.App.Status!.Text!.ToString()!.Contains("Listo"),
            "la reanudación aceptada debe finalizar normalmente");

        Assert.Equal(interaction.ToString(), host.ResumeInteractionId);
        Assert.Equal(0, host.ExecuteCount);
        Assert.Equal(inputCount, fx.Decoded<UserInputReceived>().Count());
        var resolved = Assert.Single(fx.Decoded<InteractionResolved>(), item => item.InteractionId == interaction);
        Assert.Equal(InteractionCause.User, resolved.Cause);
        Assert.Equal("allow_route", resolved.OptionId);
        var revised = Assert.Single(fx.Decoded<SessionRoutingPolicyRevised>(), item => item.InteractionId == interaction);
        Assert.True(SessionRoutingAuthorization.Read(fx.Server.AcquireStore().ReadFrom(fx.Server.LastSessionId()!, 1),
            fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)!.Allows(route, BillingMode.MeteredCurrency));
        _ = revised;
    });

    [Fact]
    public void Model_route_consent_deny_never_resumes() => RunTuiTest(fx =>
    {
        var host = new RouteResumeTurnHost();
        var (interaction, _) = PublishRouteConsent(fx);
        host.EscalationInteractionId = interaction.ToString();
        fx.StartTui(turnHost: host);

        fx.Wait(() => fx.App.Overlay is not null, "el consentimiento de ruta debe abrir el overlay real");
        var buttons = fx.App.Overlay!.SubViews.OfType<Button>().ToArray();
        Assert.Equal(2, buttons.Length);
        fx.Invoke(() => buttons[0].SetFocus());
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is null,
            "deny debe responder y cerrar el overlay");

        var resolved = Assert.Single(fx.Decoded<InteractionResolved>(), item => item.InteractionId == interaction);
        Assert.Equal(InteractionCause.User, resolved.Cause);
        Assert.Equal("deny", resolved.OptionId);
        Assert.Equal(0, host.ResumeCount);
        Assert.Equal(0, host.ExecuteCount);
    });

    [Fact]
    public void Model_route_consent_accepted_while_originating_invocation_finishes_is_not_lost() => RunTuiTest(fx =>
    {
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RouteResumeTurnHost { InvocationGate = gate };
        try
        {
            fx.StartTui(turnHost: host);
            Type(fx, "solicitud que sigue terminando");
            KeyWithEffect(fx, KeyCode.Enter, () => host.ExecuteCount == 1, "invocación originaria aún activa");
            var (interaction, _) = PublishRouteConsent(fx);
            host.EscalationInteractionId = interaction.ToString();
            fx.Wait(() => fx.App.Overlay is not null, "el polling publica el permiso antes del return 3");
            var allow = fx.App.Overlay!.SubViews.OfType<Button>().Last();
            fx.Invoke(() => allow.InvokeCommand(Command.Accept));
            fx.Wait(() => fx.Decoded<InteractionResolved>().Any(item => item.InteractionId == interaction), "respuesta persistida");
            Assert.Equal(0, host.ResumeCount);
            gate.SetResult(3);
            fx.Wait(() => host.ResumeCount == 1, "callback diferido no se pierde cuando finaliza el origen");
            fx.Wait(() => fx.App.Status!.Text!.ToString()!.Contains("Listo"), "destino completó el callback");
            Assert.Equal(interaction.ToString(), host.ResumeInteractionId);
            Assert.Equal(1, host.ExecuteCount);
        }
        finally { gate.TrySetResult(3); }
    });

    [Fact]
    public void Rejected_route_response_shows_error_and_never_resumes() => RunTuiTest(fx =>
    {
        var host = new RouteResumeTurnHost();
        var (interaction, _) = PublishRouteConsent(fx);
        host.EscalationInteractionId = interaction.ToString();
        fx.StartTui(turnHost: host);
        fx.Wait(() => fx.App.Overlay is not null, "el consentimiento de ruta debe abrir el overlay real");
        var buttons = fx.App.Overlay!.SubViews.OfType<Button>().ToArray();
        Assert.Equal(2, buttons.Length);

        // Consume the pending request outside the visible overlay. A subsequent UI response is
        // rejected by OmniServer and must not be mistaken for successful consent.
        fx.Invoke(() =>
        {
            Assert.Equal("ok", fx.Server.RespondToInteraction(interaction, "deny").Status);
            buttons[1].InvokeCommand(Command.Accept);
        });
        fx.Wait(() => fx.App.Overlay is not null && OverlayText(fx.App.Overlay!).Contains("Aviso"),
            "la respuesta rechazada debe mostrar el aviso de error");

        Assert.Equal(0, host.ResumeCount);
        Assert.Equal(0, host.ExecuteCount);
        Assert.DoesNotContain(fx.Decoded<SessionRoutingPolicyRevised>(), item => item.InteractionId == interaction);
    });

    [Fact]
    public void Quota_consent_resumes_without_new_input_or_monetary_policy_revision() => RunTuiTest(fx =>
    {
        var host = new RouteResumeTurnHost();
        var session = fx.Server.LastSessionId()!;
        var run = fx.Server.LastRunId()!;
        var interaction = InteractionId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: run)))
            new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), session).Append(
                new InteractionRequested(interaction, InteractionKind.BudgetExceeded,
                    "{\"includedQuotaConsent\":1}",
                    "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_quota\",\"intent\":\"allow\"}]",
                    "deny", null, null, null, null, 0, 1));
        var inputs = fx.Decoded<UserInputReceived>().Count();
        fx.StartTui(turnHost: host);
        fx.Wait(() => fx.App.Overlay is not null, "quota consent overlay");
        var buttons = fx.App.Overlay!.SubViews.OfType<Button>().ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.Contains("cuota", buttons[1].Text.ToString()!, StringComparison.OrdinalIgnoreCase);
        fx.Invoke(() => buttons[1].SetFocus());
        KeyWithEffect(fx, KeyCode.Enter, () => host.ResumeCount == 1, "quota callback resumes once");
        Assert.Equal(0, host.ExecuteCount);
        Assert.Equal(interaction.ToString(), host.ResumeInteractionId);
        Assert.Equal(inputs, fx.Decoded<UserInputReceived>().Count());
        Assert.Empty(fx.Decoded<SessionRoutingPolicyRevised>());
        Assert.Equal("allow_quota", Assert.Single(fx.Decoded<InteractionResolved>()).OptionId);
    });

    private static (InteractionId Interaction, ModelRoute Route) PublishRouteConsent(TuiFixture fixture)
    {
        var session = fixture.Server.LastSessionId()!;
        var run = fixture.Server.LastRunId()!;
        Assert.Equal("ok", fixture.Server.EnsureSessionRoutingPolicy(session).Status);
        var route = ModelRoute.DefaultForModel("paid-model", "paid-provider", "https://paid.example/v1",
            ProviderFamily.OpenAiChatCompatible);
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: run)))
            new EventStream(fixture.Server.AcquireStore(), fixture.Server.AcquireCodecs(), session)
                .Append(new ModelEscalationRequested(run, "local-worker", route.ProviderModelName,
                    EscalationCause.ContextLimit));
        var result = fixture.Server.AuthorizeModelRoute(session, run, route, BillingMode.MeteredCurrency,
            requireConsent: true);
        Assert.False(result.Authorized);
        Assert.NotNull(result.Interaction);
        Assert.Equal("ok", result.Ack.Status);
        return (result.Interaction!, route);
    }

    private sealed class RouteResumeTurnHost : ITuiTurnHost
    {
        private int _resumeCount;
        private int _executeCount;
        public int ResumeCount => Volatile.Read(ref _resumeCount);
        public int ExecuteCount => Volatile.Read(ref _executeCount);
        public string? EscalationInteractionId { get; set; }
        public string? ResumeInteractionId { get; private set; }
        public TaskCompletionSource<int>? InvocationGate { get; init; }
        public bool HasEscalationForInteraction(string interactionId) =>
            StringComparer.Ordinal.Equals(EscalationInteractionId, interactionId);

        public Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            return InvocationGate?.Task ?? System.Threading.Tasks.Task.FromResult(0);
        }

        public Task<int> ResumeEscalationAsync(string interactionId, Action<string> diagnostics,
            CancellationToken cancellationToken)
        {
            ResumeInteractionId = interactionId;
            Interlocked.Increment(ref _resumeCount);
            return System.Threading.Tasks.Task.FromResult(0);
        }
        public Task<int> ResumeQuotaAsync(string interactionId, Action<string> diagnostics,
            CancellationToken cancellationToken) => ResumeEscalationAsync(interactionId, diagnostics, cancellationToken);
    }

    // ------------------------------------------------------------------ cuestionario: radio real, validación, paridad plain/TUI

    [Fact]
    public void Questionnaire_overlay_submits_typed_answers_with_plain_form_parity() => RunTuiTest(fx =>
    {
        var interaction = InteractionId.New();
        fx.PublishQuestionnaire(interaction);
        fx.StartTui();

        fx.Wait(() => fx.App.Overlay is not null, "el cuestionario pendiente debe abrir un overlay");
        var overlay = fx.App.Overlay!;
        var model = fx.App.ProjectionState.Overlays[0].Questionnaire!;
        Assert.NotNull(model);
        var checkboxes = overlay.SubViews.OfType<CheckBox>().ToArray();
        var textFields = overlay.SubViews.OfType<TextField>().ToArray();
        var buttons = overlay.SubViews.OfType<Button>().ToArray();
        var errorLabel = overlay.SubViews.OfType<Label>().Last();
        // Índices vinculados por las etiquetas publicadas del schema.
        Assert.Equal(new[] { "Compatibilidad", "Simplificar", "Unit", "Integración", "Otro" },
            checkboxes.Select(box => box.Text?.ToString()).ToArray());
        Assert.Equal(2, textFields.Length); // texto "Otro" del multi + campo de la pregunta libre
        Assert.Equal(2, buttons.Length);     // Enviar / Cancelar

        // Envío vacío: validación del lado cliente, el overlay NO se cierra ni se inventa respuesta.
        fx.Invoke(() => buttons[0].InvokeCommand(Command.Accept));
        fx.Wait(() => !string.IsNullOrEmpty(errorLabel.Text?.ToString()), "el envío inválido muestra el error de validación");
        Assert.NotNull(fx.App.Overlay);
        Assert.DoesNotContain(fx.Decoded<InteractionResolved>(), r => r.InteractionId.Equals(interaction));

        // Selección única con radio REAL: se marca "a" y luego, con espacio sobre "b", "a" se desmarca sola.
        fx.Invoke(() => checkboxes[0].Value = CheckState.Checked);
        fx.Invoke(() => checkboxes[1].SetFocus());
        fx.InjectKey(new Key(' '));
        fx.Wait(() => checkboxes[1].Value == CheckState.Checked, "espacio marca la opción enfocada");
        fx.Wait(() => checkboxes[0].Value == CheckState.UnChecked, "la selección única desmarca la opción anterior (radio)");

        // Múltiple + Otro + texto libre.
        fx.Invoke(() =>
        {
            checkboxes[2].Value = CheckState.Checked;
            checkboxes[4].Value = CheckState.Checked;
            textFields[0].Text = "Integración y probes";
            textFields[1].Text = "Conservar el comportamiento existente";
        });

        // Paridad: las mismas respuestas tipadas que produce el formulario plano con los mismos inputs.
        var plain = QuestionnairePlainFormParser.Parse(model, new Dictionary<string, QuestionnairePlainFormInput>
        {
            ["single"] = new("b"),
            ["multi"] = new("x otro", otherText: "Integración y probes"),
            ["text"] = new(text: "Conservar el comportamiento existente"),
        });
        Assert.True(plain.IsValid);

        fx.Invoke(() => buttons[0].InvokeCommand(Command.Accept));
        fx.Wait(() => fx.App.Overlay is null, "el envío válido cierra el cuestionario");

        var resolved = fx.Decoded<InteractionResolved>().Single(r => r.InteractionId.Equals(interaction));
        Assert.Equal("submitted", resolved.State);
        Assert.NotNull(resolved.AnswerRef);
        var answers = QuestionnaireCodec.DecodeAnswers(fx.Artifacts.GetText(resolved.AnswerRef!.Hash) ?? "");
        Assert.Equal(plain.Response!.Answers.Count, answers.Count);
        foreach (var (expected, actual) in plain.Response!.Answers.Zip(answers))
        {
            Assert.Equal(expected.QuestionId, actual.QuestionId);
            Assert.Equal(expected.SelectedOptionIds, actual.SelectedOptionIds);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.OtherText, actual.OtherText);
        }
        // El run en espera de input recibió la transición tipada correlacionada.
        Assert.Contains(fx.Decoded<UserInputReceived>(),
            input => (input.InputPartsJson ?? "").Contains("QuestionnaireResponse"));
    });

    [Fact]
    public void Questionnaire_overlay_cancel_is_a_structured_response_never_a_choice() => RunTuiTest(fx =>
    {
        var interaction = InteractionId.New();
        fx.PublishQuestionnaire(interaction);
        fx.StartTui();

        fx.Wait(() => fx.App.Overlay is not null, "el cuestionario pendiente debe abrir un overlay");
        var cancel = fx.App.Overlay!.SubViews.OfType<Button>().Last();
        fx.Invoke(() => cancel.InvokeCommand(Command.Accept));
        fx.Wait(() => fx.App.Overlay is null, "cancelar cierra el cuestionario");

        var resolved = fx.Decoded<InteractionResolved>().Single(r => r.InteractionId.Equals(interaction));
        Assert.Equal("cancelled", resolved.State);
        Assert.Equal("", resolved.OptionId);
    });

    // ------------------------------------------------------------------ preferencias/modelos: onboarding real por política borrada

    [Fact]
    public void Model_policies_overlay_onboarding_reappears_when_policy_is_deleted() => RunTuiTest(fx =>
    {
        var key = new ModelPolicyKeyDto("local", "model");
        fx.StartTui(ModelPolicyHost.Create(Path.Combine(fx.Root, "policies"),
            new[] { new ModelRegistryModelDescriptor("model", "local", 4096, 3072, 1024) }));

        OpenModelMaintenance(fx);
        fx.Wait(() => fx.App.Overlay is not null, "F3 abre el overlay de modelos");
        var select = fx.App.Overlay!.SubViews.OfType<Button>()
            .Single(button => (button.Text?.ToString() ?? "").Contains("local/model"));
        Assert.Contains("requiere configuración", select.Text?.ToString() ?? "");

        // Sin política: la selección abre el onboarding y NADA se persiste sin elección del usuario.
        fx.Invoke(() => select.InvokeCommand(Command.Accept));
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Configurar modelo") == true, "sin política el onboarding de modelos debe abrirse");
        Assert.Null(fx.App.Policies.Get(key, CancellationToken.None));

        // Elección del usuario (categoría recomendada): persiste la política y vuelve al listado.
        var recommended = fx.App.Overlay!.SubViews.OfType<Button>()
            .Single(button => (button.Text?.ToString() ?? "").Contains("[recommended]"));
        fx.Invoke(() => recommended.InvokeCommand(Command.Accept));
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Modelos") == true, "tras configurar, el overlay vuelve al listado de modelos");
        var policy = fx.App.Policies.Get(key, CancellationToken.None);
        Assert.NotNull(policy);

        // Borrar la política re-dispara el onboarding la próxima vez (ADR-0044 §5).
        var delete = fx.App.Overlay!.SubViews.OfType<Button>()
            .Single(button => button.Id == "delete-policy-model");
        Assert.Equal("🗑", delete.Text);
        var modelRow = fx.App.Overlay.SubViews.OfType<Button>().Single(b => b.Text.ToString().Contains("local/model"));
        fx.Wait(() => delete.Frame.Width > 0, "fila de modelos dibujada");
        Assert.Equal(modelRow.Frame.Y, delete.Frame.Y);
        Assert.True(modelRow.Frame.Right <= delete.Frame.X, "el icono no se superpone al modelo");
        fx.Invoke(() => delete.InvokeCommand(Command.Accept));
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Eliminar política") == true, "borrado pide confirmación");
        Assert.NotNull(fx.App.Policies.Get(key, CancellationToken.None));
        Click(fx, "Cancelar");
        fx.Wait(() => fx.App.Overlay?.SubViews.OfType<Button>().Any(b => b.Id == "delete-policy-model") == true, "cancelar vuelve al listado");
        Assert.NotNull(fx.App.Policies.Get(key, CancellationToken.None));
        var deleteAgain = fx.App.Overlay!.SubViews.OfType<Button>().Single(b => b.Id == "delete-policy-model");
        fx.Invoke(() => deleteAgain.InvokeCommand(Command.Accept));
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Eliminar política") == true, "confirmación visible");
        Click(fx, "Eliminar");
        fx.Wait(() => fx.App.Policies.Get(key, CancellationToken.None) is null, "confirmar elimina la política");
        Assert.Null(fx.App.Policies.Get(key, CancellationToken.None));
        fx.Wait(() => fx.App.Overlay!.SubViews.OfType<Button>()
            .Any(button => (button.Text?.ToString() ?? "").Contains("requiere configuración")), "el listado vuelve a mostrar el modelo sin configurar");
        var selectAgain = fx.App.Overlay!.SubViews.OfType<Button>()
            .Single(button => (button.Text?.ToString() ?? "").Contains("local/model"));
        fx.Invoke(() => selectAgain.InvokeCommand(Command.Accept));
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Configurar modelo") == true, "el onboarding reaparece tras borrar la política");
    });

    [Fact]
    public void Settings_account_navigation_does_not_start_login_and_logout_requires_confirmation() => RunTuiTest(fx =>
    {
        var account = new TestAccount { Current = new(true, "***1234", null, false) };
        fx.StartTui(account: account);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "F4 abre configuración");
        Click(fx, "Cuenta ChatGPT");
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("***1234"), "estado enmascarado visible");
        Assert.Equal(0, account.LoginCalls);
        Click(fx, "Cerrar sesión…");
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("¿Cerrar") == true, "confirmación visible");
        Assert.Equal(0, account.LogoutCalls);
        Click(fx, "Cancelar");
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Cuenta") == true, "cancelar conserva cuenta");
        Assert.Equal(0, account.LogoutCalls);
        Click(fx, "Cerrar sesión…");
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("¿Cerrar") == true, "confirmación visible");
        Click(fx, "Sí, cerrar sesión");
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("Sin sesión"), "logout actualiza estado");
        Assert.Equal(1, account.LogoutCalls);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Login_completes_or_shows_safe_error_without_leaving_orphan_panel(bool fail) => RunTuiTest(fx =>
    {
        var account = new TestAccount { Fail = fail };
        fx.StartTui(account: account);
        Type(fx, "/login"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Cuenta") == true, "login accesible por comando");
        Click(fx, "código de dispositivo");
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Aviso") == true, "resultado de login visible");
        Assert.Equal(1, account.LoginCalls);
        Assert.True(account.DeviceCode);
        Assert.Contains(fail ? "No se pudo iniciar" : "correctamente", OverlayText(fx.App.Overlay!));
        Assert.DoesNotContain("SECRET", OverlayText(fx.App.Overlay!));
        Assert.Single(fx.App.MainWindow!.SubViews.OfType<FrameView>(), v => v.Title?.Contains("Login") == true || v.Title?.Contains("Cuenta") == true || v.Title?.Contains("Aviso") == true);
        fx.InjectKey(new Key(KeyCode.Esc)); fx.Wait(() => fx.App.Overlay is null, "Esc vuelve al compositor");
        Assert.True(fx.App.Composer!.HasFocus);
    });

    [Fact]
    public void Esc_cancels_pending_login_and_ignores_late_progress() => RunTuiTest(fx =>
    {
        var account = new TestAccount { Pending = true };
        fx.StartTui(account: account);
        Type(fx, "/login"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is not null, "cuenta visible");
        Click(fx, "enlace de navegador");
        fx.Wait(() => account.LoginCalls == 1, "login iniciado explícitamente");
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => account.Cancelled && fx.App.Overlay is null, "Esc cancela petición propia");
        fx.Invoke(() => fx.App.PollOnce());
        Assert.Null(fx.App.Overlay);
        Assert.True(fx.App.Composer!.HasFocus);
    });

    [Fact]
    public void Driver_main_thread_identity_matches_the_thread_running_the_ui_loop() => RunTuiTest(fx =>
    {
        fx.StartTui();
        Assert.Equal(fx.Loop.ManagedThreadId, fx.Application.MainThreadId);
    });

    [Fact]
    public void Repeated_login_open_cancel_reinitializes_exact_progress_textview_on_ui_loop() => RunTuiTest(fx =>
    {
        const int cycles = 20;
        const string initialProgress = "Preparando autorización…";
        var account = new TestAccount { Pending = true };
        fx.StartTui(account: account);

        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            Type(fx, "/login");
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Cuenta") == true,
                $"cuenta visible en ciclo {cycle}");

            string? progressText = null;
            bool? readOnly = null;
            bool? wordWrap = null;
            fx.Invoke(() =>
            {
                var browser = fx.App.Overlay!.SubViews.OfType<Button>()
                    .Single(button => button.Text.ToString().Contains("enlace de navegador"));
                browser.InvokeCommand(Command.Accept);
#pragma warning disable CS0618 // Reproducer intentionally inspects the exact production login control.
                var progress = fx.App.Overlay!.SubViews.OfType<TextView>().Single();
#pragma warning restore CS0618
                progressText = progress.Text?.ToString();
                readOnly = progress.ReadOnly;
                wordWrap = progress.WordWrap;
            });
            Assert.Equal(initialProgress, progressText);
            Assert.True(readOnly);
            Assert.True(wordWrap);

            fx.Wait(() => Volatile.Read(ref account.LoginCalls) == cycle,
                $"login iniciado en ciclo {cycle}");
            fx.InjectKey(new Key(KeyCode.Esc));
            fx.Wait(() => Volatile.Read(ref account.CancelledCalls) == cycle && fx.App.Overlay is null,
                $"cancelación y cierre observables en ciclo {cycle}");

            // Drena en el loop la respuesta tardía de este intento antes de abrir el siguiente.
            fx.Invoke(() => fx.App.PollOnce());
            Assert.Null(fx.App.Overlay);
            Assert.True(fx.App.Composer!.HasFocus);
        }

        Assert.Equal(cycles, Volatile.Read(ref account.LoginCalls));
        Assert.Equal(cycles, Volatile.Read(ref account.CancelledCalls));
    });

    [Fact]
    public void Model_catalog_scrolls_and_wraps_in_both_directions_without_switching_until_enter() => RunTuiTest(fx =>
    {
        var models = Enumerable.Range(0, 30).Select(i => new ModelRegistryModelDescriptor($"model{i:00}", "local", 4096, 3072, 1024)).ToArray();
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "paged-policies"), models);
        policies.Set(new ModelPolicyKeyDto("local", "model29"), 0, "PatchOnly", null, CancellationToken.None);
        fx.StartTui(policies);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "modelos visibles");
        var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
        Assert.Equal(30, list.Source!.Count);
        Assert.True(list.HasFocus);
        Assert.Equal(Terminal.Gui.Drawing.LineStyle.None, fx.App.Overlay.BorderStyle);
        Assert.True(fx.App.Overlay.SubViews.Single(v => v.Id == "omni-menu-title").Visible);
        Assert.True(fx.App.Overlay.ViewportSettings.HasFlag(ViewportSettingsFlags.Transparent));
        KeyWithEffect(fx, KeyCode.CursorUp, () => list.SelectedItem == 29, "arriba desde primero vuelve al último");
        Assert.True(list.Viewport.Y > 0);
        Assert.Null(policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default));
        KeyWithEffect(fx, KeyCode.CursorDown, () => list.SelectedItem == 0, "abajo desde último vuelve al primero");
        Assert.Equal(0, list.Viewport.Y);
        for (var index = 1; index < 30; index++)
        {
            var expected = index;
            KeyWithEffect(fx, KeyCode.CursorDown, () => list.SelectedItem == expected, "desplazar lista");
        }
        Assert.True(list.Viewport.Y > 0);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is null, "seleccionar cierra el selector sin abrir mantenimiento");
        Assert.DoesNotContain(fx.App.MainWindow!.SubViews.OfType<FrameView>(), v => v.Title?.Contains("Modelos") == true);
        Assert.Contains("model29", fx.App.Status!.Text.ToString());
        Assert.True(fx.App.Composer!.HasFocus);
    });

    [Fact]
    public void Logged_in_user_discovers_and_selects_subscription_models_without_manual_ids() => RunTuiTest(fx =>
    {
        var account = new TestAccount { Current = new(true, "***1234", null, false),
            Catalog = new[] { new AvailableChatGptModel("subscription-test-model", "Subscription Test", 8192, 2048) } };
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "catalog"));
        fx.StartTui(policies, account);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "modelos visibles");
        fx.Wait(() => PickerItems(fx).Any(item => item.Contains("Subscription Test")), "catálogo consultado automáticamente");
        fx.Invoke(() => Assert.DoesNotContain(fx.App.Overlay!.SubViews.OfType<Button>(),
            b => b.Text.ToString().Contains("Añadir")));
        ChoosePickerItem(fx, "Subscription Test");
        fx.Wait(() => fx.App.Overlay is null, "selección rápida sin formulario de política");
        Assert.True(policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default)!.ObserveOnly);
        Assert.Contains("subscription-test-model", fx.App.Status!.Text.ToString());
        Assert.Equal("chatgpt", policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default)!.Key.ProviderId);
        Assert.Equal(0, account.LoginCalls);
    });

    [Fact]
    public void Refreshed_catalog_replaces_retired_model_and_failure_preserves_selection() => RunTuiTest(fx =>
    {
        var ct = TestContext.Current.CancellationToken;
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "live-catalog"));
        var workspace = ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory);
        policies.RegisterChatGptModel("retired-test", 8192, 2048);
        policies.Select(workspace, new ModelPolicyKeyDto("chatgpt", "retired-test"), "retired-test", true, ct);
        var account = new TestAccount { Current = new(true, "***1234", null, false),
            Catalog = new[] { new AvailableChatGptModel("available-test", "Available test") } };
        fx.StartTui(policies, account);
        OpenModelMaintenance(fx);
        fx.Wait(() => fx.App.Status!.Text.ToString().Contains("available-test"), "modelo retirado sustituido");
        Assert.Equal("available-test", policies.CurrentSelection(workspace, ct)!.ModelId);
        Assert.True(policies.CurrentSelection(workspace, ct)!.ObserveOnly);
        fx.Wait(() => fx.App.Overlay?.SubViews.OfType<Button>()
            .Any(b => b.Text.ToString().Contains("available-test")) == true,
            "el menú reconstruido debe estar disponible antes de inspeccionarlo");
        Assert.DoesNotContain(fx.App.Overlay!.SubViews.OfType<Button>(), b => b.Text.ToString().Contains("retired-test"));
        account.FailCatalog = true;
        Click(fx, "Actualizar modelos");
        fx.Wait(() => fx.App.Overlay is { } overlay && OverlayText(overlay).Contains("Consulta fallida"), "error visible sin perder selección");
        Assert.Equal("available-test", policies.CurrentSelection(workspace, ct)!.ModelId);
        Assert.DoesNotContain("SECRET", OverlayText(fx.App.Overlay!));
        Assert.True(account.CatalogCalls >= 2);
        Assert.Equal(0, account.LoginCalls);
    });

    private static void Click(TuiFixture fx, string text)
    {
        var button = fx.App.Overlay!.SubViews.OfType<Button>().Single(b => b.Text.ToString().Contains(text));
        fx.Invoke(() => button.InvokeCommand(Command.Accept));
    }

    private static string[] PickerItems(TuiFixture fx)
    {
        string[] items = [];
        // Catalog discovery replaces the overlay on the UI loop. Read the complete
        // snapshot on that same loop, not while its SubViews collection is mutating.
        fx.Invoke(() => items = fx.App.Overlay?.SubViews.OfType<ListView>().SingleOrDefault()
            ?.Source?.ToList().Cast<object>().Select(item => item.ToString() ?? "").ToArray() ?? []);
        return items;
    }

    [Fact]
    public void Model_palette_search_filters_safely_and_shortcuts_open_maintenance() => RunTuiTest(fx =>
    {
        var models = new[] { new ModelRegistryModelDescriptor("alpha", "local", 4096, 3072, 1024),
            new ModelRegistryModelDescriptor("beta", "local", 4096, 3072, 1024) };
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "search-policies"), models);
        fx.StartTui(policies);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "selector visible");
        var search = fx.App.Overlay!.SubViews.OfType<TextField>().Single();
        Assert.True(fx.App.Conversation!.Dimmed);
        KeyWithEffect(fx, KeyCode.F | KeyCode.CtrlMask, () => search.HasFocus, "Ctrl+F enfoca búsqueda");
        fx.Invoke(() => search.Text = "BETA");
        fx.Wait(() => PickerItems(fx).Length == 1, "filtro ignora mayúsculas");
        Assert.Contains("beta", PickerItems(fx).Single());
        fx.Invoke(() => search.Text = "no-such-model");
        fx.Wait(() => PickerItems(fx).Length == 0, "búsqueda vacía segura");
        fx.InjectKey(new Key(KeyCode.Enter));
        Assert.NotNull(fx.App.Overlay);
        Assert.Null(policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default));
        fx.Invoke(() => search.Text = "");
        fx.Wait(() => PickerItems(fx).Length == 2, "limpiar recupera lista completa");
        KeyWithEffect(fx, KeyCode.M | KeyCode.CtrlMask, () => fx.App.Overlay?.Id == "omni-panel", "Ctrl+M abre mantenimiento");
        Assert.Contains("Modelos", fx.App.Overlay!.Title);
        Assert.False(fx.App.Conversation!.Dimmed);
    });

    private static void ChoosePickerItem(TuiFixture fx, string text)
    {
        fx.Invoke(() =>
        {
            var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
            var items = list.Source!.ToList().Cast<object>().Select(item => item.ToString() ?? "").ToArray();
            var index = Array.FindIndex(items, item => item.Contains(text));
            Assert.True(index >= 0);
            list.SelectedItem = index;
            list.InvokeCommand(Command.Accept);
        });
    }

    private static void OpenModelMaintenance(TuiFixture fx)
    {
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "configuración visible");
        Click(fx, "Modelos y permisos");
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Modelos") == true, "mantenimiento de modelos visible");
    }

    [Fact]
    public void Maintenance_arrows_move_vertically_and_scroll_without_page_buttons() => RunTuiTest(fx =>
    {
        var models = Enumerable.Range(0, 30).Select(i => new ModelRegistryModelDescriptor($"model{i:00}", "local", 4096, 3072, 1024)).ToArray();
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "scrolling-maintenance"), models);
        fx.StartTui(policies);
        OpenModelMaintenance(fx);
        bool Focused(string id) => fx.App.Overlay?.SubViews.OfType<Button>().Any(b => b.Id == id && b.HasFocus) == true;
        fx.Wait(() => Focused("model-policy-model00"), "primera fila enfocada");
        Assert.DoesNotContain(fx.App.Overlay!.SubViews.OfType<Button>(), b => b.Text.ToString() is "Anterior" or "Siguiente");
        KeyWithEffect(fx, KeyCode.CursorDown, () => Focused("model-policy-model01"), "abajo no salta al checkbox");
        KeyWithEffect(fx, KeyCode.CursorRight, () => Focused("model-visibility-model01"), "derecha sí pasa a acciones");
        KeyWithEffect(fx, KeyCode.CursorDown, () => Focused("model-visibility-model02"), "bajar conserva columna de acciones");
        KeyWithEffect(fx, KeyCode.CursorLeft, () => Focused("model-policy-model02"), "izquierda vuelve a la fila");
        for (var index = 3; index < 30; index++)
        {
            var id = $"model-policy-model{index:00}";
            KeyWithEffect(fx, KeyCode.CursorDown, () => Focused(id), "desplazamiento continuo hasta el final");
        }
        KeyWithEffect(fx, KeyCode.CursorDown, () => Focused("model-policy-model00"), "final vuelve al inicio");
        KeyWithEffect(fx, KeyCode.CursorUp, () => Focused("model-policy-model29"), "inicio vuelve al final");
        Assert.Null(policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default));
        Assert.All(models, model => Assert.True(policies.IsVisibleInPicker(model.ProviderId, model.Id)));
    });

    [Fact]
    public void Picker_visibility_is_persistent_and_independent_of_policy_and_selection() => RunTuiTest(fx =>
    {
        var directory = Path.Combine(fx.Root, "visibility-policies");
        var models = new[] { new ModelRegistryModelDescriptor("visible-model", "local", 8192, 8192, 2048) };
        var policies = ModelPolicyHost.Create(directory, models);
        var key = new ModelPolicyKeyDto("local", "visible-model");
        policies.Set(key, 0, "PatchOnly", null, CancellationToken.None);
        var workspace = ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory);
        policies.Select(workspace, key, key.ModelId, false, CancellationToken.None);
        fx.StartTui(policies);
        OpenModelMaintenance(fx);
        var toggle = fx.App.Overlay!.SubViews.OfType<Button>().Single(b => b.Id == "model-visibility-visible-model");
        fx.Invoke(() => toggle.InvokeCommand(Command.Accept));
        fx.Wait(() => !policies.IsVisibleInPicker("local", "visible-model"), "ocultar preferencia persistida");
        Assert.Equal("PatchOnly", policies.Get(key, CancellationToken.None)!.Category);
        Assert.Equal("visible-model", policies.CurrentSelection(workspace, CancellationToken.None)!.ModelId);
        using (var reopened = ModelPolicyHost.Create(directory, models))
            Assert.False(reopened.IsVisibleInPicker("local", "visible-model"));
        fx.InjectKey(new Key(KeyCode.Esc)); fx.Wait(() => fx.App.Overlay is null, "mantenimiento cerrado");
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "selector vacío visible");
        Assert.DoesNotContain(PickerItems(fx), item => item.Contains("visible-model"));
        Assert.DoesNotContain(fx.App.Overlay.SubViews.OfType<Button>(), b => b.Id?.StartsWith("delete-policy-") == true);
        fx.InjectKey(new Key(KeyCode.Esc)); fx.Wait(() => fx.App.Overlay is null, "selector cerrado");
        OpenModelMaintenance(fx);
        var show = fx.App.Overlay!.SubViews.OfType<Button>().Single(b => b.Id == "model-visibility-visible-model");
        fx.Invoke(() => show.InvokeCommand(Command.Accept));
        fx.Wait(() => policies.IsVisibleInPicker("local", "visible-model"), "mostrar nuevamente");
        fx.InjectKey(new Key(KeyCode.Esc)); fx.Wait(() => fx.App.Overlay is null, "mantenimiento cerrado");
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "selector visible");
        Assert.Contains(PickerItems(fx), item => item.Contains("visible-model"));
    });

    private sealed class TestAccount : ITuiAccountHost
    {
        public ChatGptSessionStatus Current = new(false, null, null, false);
        public bool Fail, Pending, DeviceCode;
        public volatile bool Cancelled;
        public int LoginCalls, CancelledCalls, LogoutCalls, CatalogCalls;
        public bool FailCatalog;
        public IReadOnlyList<AvailableChatGptModel> Catalog = Array.Empty<AvailableChatGptModel>();
        public Task<IReadOnlyList<AvailableChatGptModel>> ListModelsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CatalogCalls);
            return FailCatalog
                ? System.Threading.Tasks.Task.FromException<IReadOnlyList<AvailableChatGptModel>>(new IOException("SECRET catalog error"))
                : System.Threading.Tasks.Task.FromResult(Catalog);
        }
        public ChatGptSessionStatus Status() => Current;
        public void Logout() { LogoutCalls++; Current = new(false, null, null, false); }
        public async Task<ChatGptSessionStatus> LoginAsync(bool deviceCode, Action<string> progress, CancellationToken cancellationToken)
        {
            DeviceCode = deviceCode; Interlocked.Increment(ref LoginCalls);
            progress("https://example.invalid/authorize\nTEST-CODE");
            if (Pending)
            {
                try { await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    progress("late progress");
                    Interlocked.Increment(ref CancelledCalls);
                    throw;
                }
            }
            if (Fail) throw new InvalidOperationException("SECRET raw provider failure");
            Current = new(true, "***1234", null, false); return Current;
        }
    }

    // ------------------------------------------------------------------ fixture

    /// <summary>Espera activa con tope: los cambios de estado llegan por el poll de producción (500 ms).</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (condition()) return true;
            }
            catch (Exception)
            {
                // El estado puede leerse a mitad de una mutación del bucle; reintenta.
            }
            Thread.Sleep(25);
        }
        return condition();
    }

    /// <summary>Una fixture y un intento: una caída del loop falla la prueba y conserva
    /// la excepción completa; no repetir hasta ocultar una carrera.</summary>
    private static void RunTuiTest(Action<TuiFixture> body)
    {
        using var fx = TuiFixture.Create();
        body(fx);
    }

    private static void RunTuiTest(Action<TuiFixture> body, bool noColor)
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", noColor ? "1" : null);
            RunTuiTest(body);
        }
        finally { Environment.SetEnvironmentVariable("NO_COLOR", previous); }
    }

    private static string OverlayText(View overlay) => string.Join(" ",
        overlay.SubViews.Select(view => view.Text?.ToString() ?? ""));

    /// <summary>Teclea texto en el composer verificando que llega: bajo la carga de la suite completa
    /// el driver puede perder claves inyectadas, así que se reintentan las que falten.</summary>
    private static void Type(TuiFixture fx, string text)
    {
        for (var tries = 0; ; tries++)
        {
            var actual = fx.App.Composer!.Text?.ToString() ?? "";
            if (actual == text) return;
            // El foco puede no haber vuelto al composer tras cerrar un overlay: sin foco las teclas
            // no llegan a ninguna parte. Se asegura antes de inyectar y se reintenta lo que falte.
            fx.Invoke(() =>
            {
                if (!fx.App.Composer!.HasFocus) fx.App.Composer.SetFocus();
            });
            if (text.StartsWith(actual, StringComparison.Ordinal))
            {
                foreach (var ch in text.Substring(actual.Length)) fx.InjectKey(new Key(ch));
            }
            else
            {
                fx.Invoke(() => fx.App.Composer!.Text = "");
                foreach (var ch in text) fx.InjectKey(new Key(ch));
            }
            if (WaitUntil(() => (fx.App.Composer!.Text?.ToString() ?? "") == text, 3000)) return;
            if (tries >= 3)
                Assert.Fail("las teclas no llegan al composer: '" + text + "' (quedó '" + actual
                    + "'; foco=" + fx.App.Composer!.HasFocus + ", overlay=" + (fx.App.Overlay is not null)
                    + ", loop=" + (fx.Loop?.IsAlive.ToString() ?? "null") + ")");
        }
    }

    /// <summary>Inyecta una tecla y espera su efecto observable, reintentando mientras la tecla
    /// no se procese. Solo para teclas idempotentes (Enter sobre input ya procesado es un no-op,
    /// Esc sin overlay ni interacción es un no-op, F3 reabre el mismo listado); nunca para
    /// toggles como F2.</summary>
    private static void KeyWithEffect(TuiFixture fx, KeyCode key, Func<bool> effect, string message)
    {
        for (var tries = 0; ; tries++)
        {
            fx.InjectKey(new Key(key));
            if (WaitUntil(effect, 3000)) return;
            if (tries >= 3) Assert.Fail(message);
        }
    }

    /// <summary>
    /// Servidor persistente real (journal SQLite + artifacts en disco, como un workspace de usuario)
    /// con una sesión semilla creada por el primer input, más la TUI de producción sobre un driver real.
    /// Los directorios son temporales del proceso; nunca se tocan los datos del usuario.
    /// </summary>
    internal sealed class TuiFixture : IDisposable
    {
        public string Root = "";
        public string Workspace = "";
        public string JournalDirectory = "";
        public OmniServer Server = null!;
        public IArtifactStore Artifacts = null!;
        public TuiApp App = null!;
        private ModelPolicyHost? _fixturePolicies;
        public IApplication Application = null!;
        public IInputInjector Injector = null!;

        // The injector can process keyboard events synchronously. Dispatch it on the UI
        // thread, like real keyboard input, so overlays cannot mutate during drawing.
        public void InjectKey(Key key) => Invoke(() => Injector.InjectKey(key));
        public Thread Loop = null!;

        public static TuiFixture Create()
        {
            var fx = new TuiFixture();
            fx.Root = Path.Combine(Path.GetTempPath(), "omnicore-tui-wiring-" + Guid.NewGuid().ToString("N"));
            fx.Workspace = Path.Combine(fx.Root, "ws");
            Directory.CreateDirectory(Path.Combine(fx.Workspace, "src"));
            Directory.CreateDirectory(Path.Combine(fx.Workspace, ".git"));
            File.WriteAllText(Path.Combine(fx.Workspace, "src", "Alpha.cs"), "class Alpha { }");
            File.WriteAllText(Path.Combine(fx.Workspace, "src", "Beta.cs"), "class Beta { }");
            File.WriteAllText(Path.Combine(fx.Workspace, ".git", "Alpha.secret"), "supersecreto");
            fx.JournalDirectory = Path.Combine(fx.Root, "journal");
            Directory.CreateDirectory(fx.JournalDirectory);
            fx.Server = OmniHost.OpenPersistentServer(Path.Combine(fx.JournalDirectory, "journal.db"),
                Path.Combine(fx.Root, "audit"));
            fx.Server.ConfigureWorkspaceRoot(fx.Workspace);
            fx.Artifacts = OmniHost.CreateArtifactStore(fx.JournalDirectory);
            // Sesión semilla: el primer input crea la sesión y el run en el journal real.
            var ack = fx.Server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\"," + JsonObj.FieldRaw("text", "\"semilla de prueba\"") + "}"),
                CancellationToken.None);
            Assert.Equal("ok", ack.Status);
            Assert.NotNull(fx.Server.LastSessionId());
            return fx;
        }

        private volatile Exception? _loopError;

        public Exception? LoopError => _loopError;

        /// <summary>Espera activa consciente de caidas: si el loop murio, falla ya con el motivo
        /// del framework en lugar de agotar el tope de espera.</summary>
        public void Wait(Func<bool> condition, string message)
        {
            if (!WaitUntil(() => LoopError is not null || condition()))
                Assert.Fail(message + " (no alcanzado en el tope de espera"
                    + (LoopError is null ? "" : "; loop caido: " + LoopError.Message) + ")");
            if (LoopError is not null)
                Assert.Fail(message + " (loop de la TUI caido: " + LoopError + ")");
        }

        /// <summary>Ejecuta una mutación en el hilo UI, espera su finalización y propaga su excepción.</summary>
        public void Invoke(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (Application.MainThreadId == Environment.CurrentManagedThreadId)
            {
                action();
                return;
            }

            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Application.Invoke(() =>
            {
                try
                {
                    action();
                    completed.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completed.TrySetException(exception);
                }
            });
            completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();
        }

        public void StartTui(ModelPolicyHost? policies = null, ITuiAccountHost? account = null,
            ITuiTurnHost? turnHost = null, int? initialColumns = null, int? initialRows = null)
        {
            if (initialColumns.HasValue != initialRows.HasValue)
                throw new ArgumentException("Initial screen columns and rows must be provided together.");
            // Wiring tests must never discover models with the developer's real account.
            _fixturePolicies = policies ?? ModelPolicyHost.Create(Path.Combine(Root, "fixture-policies"),
                new[] { new ModelRegistryModelDescriptor("local-worker", "local", 8192, 8192, 2048) });
            account ??= new TestAccount();
            // El build 2.6.0-develop.61 de Terminal.Gui tiene una ventana de carrera en
            // View.RenderLineCanvas (relee _pendingOverlappedCellMaps sin guard tras el null-check)
            // que a veces vuelca Begin/LayoutAndDraw en procesos sin TTY. Un vuelco mataría el
            // testhost entero, así que: se captura la excepción del hilo del loop, se reintenta
            // el ciclo Init/Run completo y, si persiste, la prueba falla; nunca cuenta como validación completada.
            for (var attempt = 1; ; attempt++)
            {
                _loopError = null;
                App = new TuiApp(Server, "es", _fixturePolicies, account, turnHost);
                Application = Terminal.Gui.App.Application.Create();
                var firstFrameDrawn = 0;
                Application.LayoutAndDrawComplete += (_, _) => Interlocked.Exchange(ref firstFrameDrawn, 1);
                using var initialized = new ManualResetEventSlim();
                Exception? initializationError = null;
                Loop = new Thread(() =>
                {
                    try
                    {
                        Application.Init("DOTNET");
                        if (initialColumns is { } columns && initialRows is { } rows)
                            Application.Driver!.SetScreenSize(columns, rows);
                    }
                    catch (Exception exception)
                    {
                        initializationError = exception;
                        _loopError = exception;
                        initialized.Set();
                        return;
                    }

                    initialized.Set();
                    try { App.RunWith(Application); }
                    catch (Exception exception) { _loopError = exception; }
                }) { IsBackground = true, Name = "tui-wiring-loop" };
                // En producción el loop de la TUI es el hilo PRIMARIO de su proceso; en la suite
                // compite con las hebras de otros tests en paralelo y muere de hambre bajo carga.
                // AboveNormal reproduce la prioridad relativa real sin tocar la semántica del wiring.
                Loop.Priority = ThreadPriority.AboveNormal;
                Loop.Start();
                initialized.Wait(TestContext.Current.CancellationToken);
                if (initializationError is not null)
                {
                    CleanupApplication();
                    if (attempt >= 3)
                        Assert.Fail("El driver DOTNET de Terminal.Gui no arranca sin TTY en este entorno: "
                            + initializationError.Message);
                    Thread.Sleep(200);
                    continue;
                }
                // A positive Frame is assigned during Begin/EndInit, before the view tree
                // is safe to mutate. Wait for the first completed frame, not a partially initialized window.
                var ready = WaitUntil(() => Volatile.Read(ref firstFrameDrawn) != 0 || _loopError is not null);
                if (ready && _loopError is null)
                {
                    Injector = Application.GetInputInjector();
                    return;
                }
                CleanupApplication();
                if (attempt >= 3)
                    Assert.Fail("La TUI no completó el ciclo Init/Run tras " + attempt
                        + " intentos (rara del driver en proceso headless): "
                        + (_loopError?.Message ?? "ventana sin layout"));
                Thread.Sleep(200);
            }
        }

        private void CleanupApplication()
        {
            try { Application?.RequestStop(); } catch (Exception) { }
            try { Loop?.Join(5000); } catch (Exception) { }
            try { Application?.Dispose(); } catch (Exception) { }
        }

        /// <summary>Publica una interacción de elección pendiente en el journal, como haría el Engine.</summary>
        public void AppendInteraction(InteractionId interaction, string kind, string subjectJson,
            string optionsJson, string defaultOptionId)
        {
            new EventStream(Server.AcquireStore(), Server.AcquireCodecs(), Server.LastSessionId()!)
                .Append(new InteractionRequested(interaction, (InteractionKind)Enum.Parse(typeof(InteractionKind),
                    kind, false), subjectJson, optionsJson, defaultOptionId, null, null, null, null, 0, 1, null, null));
        }

        /// <summary>Publica un cuestionario real (schema validado + artifact content-addressed, ADR-0045 §7).</summary>
        public void PublishQuestionnaire(InteractionId interaction)
        {
            var schema = new QuestionnaireSchema("Preferencias de trabajo", null, new QuestionField[]
            {
                new("single", "¿Enfoque?", null, QuestionKind.SingleChoice, new[]
                {
                    new QuestionOption("a", "Compatibilidad", null),
                    new QuestionOption("b", "Simplificar", null),
                }, null, true, null, null, null),
                new("multi", "¿Validaciones?", null, QuestionKind.MultipleChoice, new[]
                {
                    new QuestionOption("x", "Unit", null),
                    new QuestionOption("y", "Integración", null),
                }, new OtherInput("otro", "Otro", "especifica", true, 80), true, 1, 2, null),
                new("text", "Notas", null, QuestionKind.FreeText, Array.Empty<QuestionOption>(),
                    null, false, null, null, 200),
            });
            var service = new QuestionnaireInteractionService(Server.AcquireStore(), Server.AcquireCodecs(),
                Artifacts);
            var stream = new EventStream(Server.AcquireStore(), Server.AcquireCodecs(), Server.LastSessionId()!);
            var result = service.Publish(stream, schema, interaction, null, null);
            Assert.True(result.Published, "el schema del cuestionario debe publicarse");
            // El Turn real publica la espera humana y la transición del Run al mismo commit
            // (ADR-0034/0035): sin RunAwaitingInput el servidor no puede tipar la respuesta.
            stream.Append(new RunAwaitingInput(Server.LastRunId()!,
                ReadJournal<LaneCreated>().Last().LaneId));
        }

        /// <summary>
        /// Decodifica todos los eventos del journal de la sesión en curso. El <c>SqliteEventStore</c>
        /// expone una única conexión compartida: mientras el loop de la TUI está vivo, la lectura se
        /// serializa en el hilo del loop (donde viven el polling y los envíos de respuesta) para no
        /// usar la conexión a la vez desde dos hilos. Con el loop detenido, la lectura es directa.
        /// </summary>
        public IEnumerable<T> Decoded<T>() where T : DomainEventPayload
        {
            if (Loop is { IsAlive: true })
            {
                T[] result = Array.Empty<T>();
                Invoke(() =>
                {
                    result = ReadJournal<T>().ToArray();
                });
                return result;
            }
            return ReadJournal<T>().ToArray();
        }

        private IEnumerable<T> ReadJournal<T>() where T : DomainEventPayload =>
            Server.AcquireStore().ReadFrom(Server.LastSessionId()!, 1)
                .Select(evt => Server.AcquireCodecs().Decode(evt)).OfType<T>();

        public void Dispose()
        {
            try { Application?.RequestStop(); } catch (Exception) { }
            try { Loop?.Join(5000); } catch (Exception) { }
            try { Application?.Dispose(); } catch (Exception) { }
            _fixturePolicies?.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
