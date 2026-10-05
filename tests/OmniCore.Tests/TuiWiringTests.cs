
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
    // ------------------------------------------------------------------ composer: foco, tecleo, envío

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

        // F2 abre el sidebar; a 80 columnas el layout es Overlay (centrado), la conversación no se reduce.
        fx.Injector.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.Sidebar!.Visible, "F2 debe abrir el sidebar");
        fx.Wait(() => fx.App.Sidebar!.Frame.Width == 44, "a 80 cols el sidebar Overlay mide 44");
        Assert.Equal(80, conversation.Frame.Width);

        // Terminal ancha (120): el resize real del driver reencuadra la ventana; sidebar apilado a la derecha.
        fx.Application.Invoke(() => fx.Application.Screen = new Rectangle(0, 0, 120, 40));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == 120, "la ventana debe ocupar la pantalla tras el resize del driver");
        fx.Wait(() => fx.App.Sidebar!.Frame.Width == 40, "a 120 cols el sidebar apilado mide 40");
        Assert.Equal(80, fx.App.Sidebar!.Frame.X);
        Assert.Equal(80, conversation.Frame.Width); // 120 - 40 sidebar; no outer chrome

        // F2 cierra el sidebar: la conversación recupera el ancho completo.
        fx.Injector.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => !fx.App.Sidebar!.Visible, "F2 debe cerrar el sidebar");
        fx.Wait(() => conversation.Frame.Width == 120, "sin sidebar la conversación ocupa 120");

        // Vuelta a estrecho sin errores de layout.
        fx.Application.Invoke(() => fx.Application.Screen = new Rectangle(0, 0, 80, 25));
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

        fx.Application.Invoke(() => fx.App.Composer!.Text = "");
        Type(fx, "@src/");
        fx.Wait(() => fx.App.Completion!.Text?.ToString().Contains("src/Alpha.cs") == true, "el workspace real completa src/Alpha.cs");
        Assert.Contains("src/Beta.cs", fx.App.Completion!.Text?.ToString() ?? "");
        Assert.DoesNotContain(".git", fx.App.Completion!.Text?.ToString() ?? "");
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
        fx.Application.Invoke(() => buttons[0].SetFocus());
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
        fx.Application.Invoke(() => buttons[0].InvokeCommand(Command.Accept));
        fx.Wait(() => !string.IsNullOrEmpty(errorLabel.Text?.ToString()), "el envío inválido muestra el error de validación");
        Assert.NotNull(fx.App.Overlay);
        Assert.DoesNotContain(fx.Decoded<InteractionResolved>(), r => r.InteractionId.Equals(interaction));

        // Selección única con radio REAL: se marca "a" y luego, con espacio sobre "b", "a" se desmarca sola.
        fx.Application.Invoke(() => checkboxes[0].Value = CheckState.Checked);
        fx.Application.Invoke(() => checkboxes[1].SetFocus());
        fx.Injector.InjectKey(new Key(' '));
        fx.Wait(() => checkboxes[1].Value == CheckState.Checked, "espacio marca la opción enfocada");
        fx.Wait(() => checkboxes[0].Value == CheckState.UnChecked, "la selección única desmarca la opción anterior (radio)");

        // Múltiple + Otro + texto libre.
        fx.Application.Invoke(() =>
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

        fx.Application.Invoke(() => buttons[0].InvokeCommand(Command.Accept));
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
        fx.Application.Invoke(() => cancel.InvokeCommand(Command.Accept));
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

        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "F3 no abre el overlay de modelos");
        fx.Wait(() => fx.App.Overlay is not null, "F3 abre el overlay de modelos");
        var select = fx.App.Overlay!.SubViews.OfType<Button>()
            .Single(button => (button.Text?.ToString() ?? "").Contains("local/model"));
        Assert.Contains("requiere configuración", select.Text?.ToString() ?? "");

        // Sin política: la selección abre el onboarding y NADA se persiste sin elección del usuario.
        fx.Application.Invoke(() => select.InvokeCommand(Command.Accept));
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Configurar modelo") == true, "sin política el onboarding de modelos debe abrirse");
        Assert.Null(fx.App.Policies.Get(key, CancellationToken.None));

        // Elección del usuario (categoría recomendada): persiste la política y vuelve al listado.
        var recommended = fx.App.Overlay!.SubViews.OfType<Button>()
            .Single(button => (button.Text?.ToString() ?? "").Contains("[recommended]"));
        fx.Application.Invoke(() => recommended.InvokeCommand(Command.Accept));
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Modelos") == true, "tras configurar, el overlay vuelve al listado de modelos");
        var policy = fx.App.Policies.Get(key, CancellationToken.None);
        Assert.NotNull(policy);

        // Borrar la política re-dispara el onboarding la próxima vez (ADR-0044 §5).
        var delete = fx.App.Overlay!.SubViews.OfType<Button>()
            .Single(button => (button.Text?.ToString() ?? "").Contains("Eliminar política"));
        fx.Application.Invoke(() => delete.InvokeCommand(Command.Accept));
        Assert.Null(fx.App.Policies.Get(key, CancellationToken.None));
        fx.Wait(() => fx.App.Overlay!.SubViews.OfType<Button>()
            .Any(button => (button.Text?.ToString() ?? "").Contains("requiere configuración")), "el listado vuelve a mostrar el modelo sin configurar");
        var selectAgain = fx.App.Overlay!.SubViews.OfType<Button>()
            .Single(button => (button.Text?.ToString() ?? "").Contains("local/model"));
        fx.Application.Invoke(() => selectAgain.InvokeCommand(Command.Accept));
        fx.Wait(() => (fx.App.Overlay as FrameView)?.Title?.Contains("Configurar modelo") == true, "el onboarding reaparece tras borrar la política");
    });

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

        /// <summary>
    /// Ejecuta un test de wiring completo sobre una fixture fresca. Si el loop de la TUI muere
    /// por la carrera de <c>View.RenderLineCanvas</c> del build develop-61 de Terminal.Gui
    /// (defecto del framework: relee <c>_pendingOverlappedCellMaps</c> sin guard tras el
    /// null-check), reintenta el test entero hasta 3 veces; cada intento arranca de cero
    /// (journal y artifacts nuevos), asi que la semantica no cambia. En el ultimo intento la
    /// excepcion original se propaga tal cual.
    /// </summary>
    private static void RunTuiTest(Action<TuiFixture> body)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var fx = TuiFixture.Create();
            try
            {
                body(fx);
                return;
            }
            catch (Xunit.Sdk.SkipException)
            {
                throw;
            }
            catch (Exception) when (attempt < 3 && fx.LoopError is not null)
            {
            }
        }
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
            fx.Application.Invoke(() =>
            {
                if (!fx.App.Composer!.HasFocus) fx.App.Composer.SetFocus();
            });
            if (text.StartsWith(actual, StringComparison.Ordinal))
            {
                foreach (var ch in text.Substring(actual.Length)) fx.Injector.InjectKey(new Key(ch));
            }
            else
            {
                fx.Application.Invoke(() => fx.App.Composer!.Text = "");
                foreach (var ch in text) fx.Injector.InjectKey(new Key(ch));
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
            fx.Injector.InjectKey(new Key(key));
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
        public IApplication Application = null!;
        public IInputInjector Injector = null!;
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
                Assert.Fail(message + " (loop de la TUI caido: " + LoopError.Message + ")");
        }

        public void StartTui(ModelPolicyHost? policies = null)
        {
            // El build 2.6.0-develop.61 de Terminal.Gui tiene una ventana de carrera en
            // View.RenderLineCanvas (relee _pendingOverlappedCellMaps sin guard tras el null-check)
            // que a veces vuelca Begin/LayoutAndDraw en procesos sin TTY. Un vuelco mataría el
            // testhost entero, así que: se captura la excepción del hilo del loop, se reintenta
            // el ciclo Init/Run completo y, si persiste, la prueba falla; nunca cuenta como validación completada.
            for (var attempt = 1; ; attempt++)
            {
                _loopError = null;
                App = new TuiApp(Server, "es", policies);
                Application = Terminal.Gui.App.Application.Create();
                try
                {
                    Application.Init("DOTNET");
                }
                catch (Exception exception)
                {
                    CleanupApplication();
                    if (attempt >= 3)
                        Assert.Fail("El driver DOTNET de Terminal.Gui no arranca sin TTY en este entorno: "
                            + exception.Message);
                    continue;
                }
                Loop = new Thread(() =>
                {
                    try { App.RunWith(Application); }
                    catch (Exception ex) { _loopError = ex; }
                }) { IsBackground = true, Name = "tui-wiring-loop" };
                // En producción el loop de la TUI es el hilo PRIMARIO de su proceso; en la suite
                // compite con las hebras de otros tests en paralelo y muere de hambre bajo carga.
                // AboveNormal reproduce la prioridad relativa real sin tocar la semántica del wiring.
                Loop.Priority = ThreadPriority.AboveNormal;
                Loop.Start();
                var ready = WaitUntil(() => App.MainWindow?.Frame.Width > 0 == true || _loopError is not null);
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
                IEnumerable<T> result = Array.Empty<T>();
                var done = new ManualResetEventSlim(false);
                Application.Invoke(() =>
                {
                    result = ReadJournal<T>();
                    done.Set();
                });
                return done.Wait(5000) ? result : ReadJournal<T>();
            }
            return ReadJournal<T>();
        }

        private IEnumerable<T> ReadJournal<T>() where T : DomainEventPayload =>
            Server.AcquireStore().ReadFrom(Server.LastSessionId()!, 1)
                .Select(evt => Server.AcquireCodecs().Decode(evt)).OfType<T>();

        public void Dispose()
        {
            try { Application?.RequestStop(); } catch (Exception) { }
            try { Loop?.Join(5000); } catch (Exception) { }
            try { Application?.Dispose(); } catch (Exception) { }
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
