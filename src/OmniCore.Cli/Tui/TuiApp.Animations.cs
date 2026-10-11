using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void RenderAnimationPreview() => _animationPreview?.Render(_previewEffect, _activityPhase,
        _animationPreferences.Enabled && !_animationPreferences.ReducedMotion,
        Environment.GetEnvironmentVariable("NO_COLOR") is not null, ascii: _animationPreferences.Ascii);

    private bool SaveAnimationPreferences(AnimationPreferences preferences)
    {
        if (!_animationStore.Save(preferences)) return false;
        _animationPreferences = preferences;
        _activityTicks = 0;
        RenderActivity();
        RenderAnimationPreview();
        return true;
    }

    private void HandleAnimationCommand(string argument)
    {
        if (argument is "" or "showcase") { ShowAnimationSettings(); return; }
        AnimationPreferences? next = argument switch
        {
            "on" => _animationPreferences with { Enabled = true },
            "off" => _animationPreferences with { Enabled = false },
            "reduced" => _animationPreferences with { ReducedMotion = true },
            "full" => _animationPreferences with { ReducedMotion = false },
            "fast" => _animationPreferences with { IntervalMilliseconds = 100 },
            "normal" => _animationPreferences with { IntervalMilliseconds = 200 },
            "slow" => _animationPreferences with { IntervalMilliseconds = 300 },
            "ascii" => _animationPreferences with { Ascii = true },
            "unicode" => _animationPreferences with { Ascii = false },
            _ => null,
        };
        if (next is null)
        {
            var parts = argument.Split(':');
            var effect = parts[^1];
            if (AnimationCatalog.Names.Contains(effect))
                next = parts.Length == 1 ? _animationPreferences with
                { Waiting = effect, Reasoning = effect, Answer = effect, Tools = effect }
                : parts.Length == 2 ? parts[0] switch
                {
                    "waiting" => _animationPreferences with { Waiting = effect },
                    "thinking" => _animationPreferences with { Reasoning = effect },
                    "working" => _animationPreferences with { Answer = effect },
                    "tool" => _animationPreferences with { Tools = effect },
                    _ => null,
                } : null;
        }
        if (next is null)
        {
            ShowMessage(Ui("Usa /animation showcase · on/off · reduced/full · fast/normal/slow\nEfectos: ",
                "Use /animation showcase · on/off · reduced/full · fast/normal/slow\nEffects: ")
                + string.Join(", ", AnimationCatalog.Names));
            return;
        }
        if (!SaveAnimationPreferences(next))
            ShowMessage(Ui("No se pudo guardar la preferencia de animación.", "Could not save animation preferences."));
    }

    private void ShowAnimationSettings()
    {
        if (_window is null || _overlay is not null) return;
        _previewEffect = _animationPreferences.Waiting;
        if (_window.Frame.Height < 18)
        {
            ShowMessage(Ui("Amplía la terminal para la vista previa.\n/animation on/off · reduced/full · fast/normal/slow",
                "Enlarge the terminal for the preview.\n/animation on/off · reduced/full · fast/normal/slow"));
            return;
        }
        var frame = OverlayFrame(Ui("Ω Indicadores y animaciones", "Ω Activity and animations"), 14);
        var notice = new Label { Id = "animation-notice", X = 2, Y = 1, Width = Dim.Fill(2), Height = 1 };
        if (_animationStore.Notice is not null)
            notice.Text = Ui("Configuración inválida · usando valores seguros", "Invalid settings · using safe defaults");
        _animationPreview = new ActivityView { Id = "animation-preview", X = 2, Y = 2, Width = 12, Height = 1 };
        var description = new Label { X = 16, Y = 2, Width = Dim.Fill(2), Height = 1 };
        var effects = new List<(Button Button, ChatActivityPhase Phase, string Label)>();
        void AddEffect(string id, int y, ChatActivityPhase phase, string label)
        {
            var button = new Button { Id = id, X = 2, Y = y, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
            button.Accepted += (_, _) =>
            {
                var current = _animationPreferences.Effect(phase);
                var next = AnimationCatalog.Names[(Array.IndexOf(AnimationCatalog.Names.ToArray(), current) + 1) % AnimationCatalog.Names.Count];
                var preferences = phase switch
                {
                    ChatActivityPhase.WaitingForResponse => _animationPreferences with { Waiting = next },
                    ChatActivityPhase.Reasoning => _animationPreferences with { Reasoning = next },
                    ChatActivityPhase.AnswerText => _animationPreferences with { Answer = next },
                    _ => _animationPreferences with { Tools = next },
                };
                Save(preferences);
                _previewEffect = _animationPreferences.Effect(phase);
                Update();
            };
            effects.Add((button, phase, label));
            frame.Add(button);
        }
        var enabled = new Button { Id = "animation-enabled", X = 2, Y = 9, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        var reduced = new Button { Id = "animation-reduced", X = 2, Y = 10, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        var speed = new Button { Id = "animation-speed", X = 2, Y = 11, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        var ascii = new Button { Id = "animation-ascii", X = 2, Y = 12, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        AddEffect("animation-waiting", 4, ChatActivityPhase.WaitingForResponse, Ui("Espera", "Waiting"));
        AddEffect("animation-thinking", 5, ChatActivityPhase.Reasoning, Ui("Razonamiento reportado", "Reported reasoning"));
        AddEffect("animation-working", 6, ChatActivityPhase.AnswerText, Ui("Respuesta", "Answer"));
        AddEffect("animation-tool", 7, ChatActivityPhase.Tools, Ui("Herramientas", "Tools"));
        enabled.Accepted += (_, _) => { Save(_animationPreferences with { Enabled = !_animationPreferences.Enabled }); Update(); };
        reduced.Accepted += (_, _) => { Save(_animationPreferences with { ReducedMotion = !_animationPreferences.ReducedMotion }); Update(); };
        speed.Accepted += (_, _) => { Save(_animationPreferences with { IntervalMilliseconds = _animationPreferences.IntervalMilliseconds == 300 ? 100 : _animationPreferences.IntervalMilliseconds + 100 }); Update(); };
        ascii.Accepted += (_, _) => { Save(_animationPreferences with { Ascii = !_animationPreferences.Ascii }); Update(); };
        void Save(AnimationPreferences preferences)
        {
            notice.Text = SaveAnimationPreferences(preferences) ? "" : Ui("No se pudo guardar · sin cambios", "Could not save · unchanged");
        }
        void Update()
        {
            foreach (var item in effects) item.Button.Text = item.Label + " · " + _animationPreferences.Effect(item.Phase);
            enabled.Text = Ui("Animaciones", "Animations") + " · " + (_animationPreferences.Enabled ? "ON" : "OFF");
            reduced.Text = Ui("Movimiento reducido", "Reduced motion") + " · " + (_animationPreferences.ReducedMotion ? "ON" : "OFF");
            speed.Text = Ui("Intervalo", "Interval") + " · " + _animationPreferences.IntervalMilliseconds + " ms";
            ascii.Text = Ui("Caracteres", "Characters") + " · " + (_animationPreferences.Ascii ? "ASCII" : "Unicode Ω");
            description.Text = _previewEffect + Ui(" · vista previa", " · preview");
            RenderAnimationPreview();
        }
        var help = new Label { Id = "omni-help", X = 2, Y = 13, Width = Dim.Fill(2), Height = 1,
            Text = Ui("Enter cambia · OFF conserva el estado sin movimiento", "Enter changes · OFF keeps status without motion") };
        var close = new Button { X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2), Text = Ui("Cerrar", "Close") };
        close.Accepted += (_, _) => CloseOverlay();
        ApplyTheme(_window, dimmed: true);
        if (_conversation is not null) { _conversation.Dimmed = true; _conversation.SetNeedsDraw(); }
        frame.Add(notice, _animationPreview, description, enabled, reduced, speed, ascii, help, close);
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        Update();
        effects[0].Button.SetFocus();
    }
}
