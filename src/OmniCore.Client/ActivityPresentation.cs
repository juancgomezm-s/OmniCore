using OmniCore.Protocol;

namespace OmniCore.Client;

/// <summary>Content-free activity of the visible principal, with children counted separately.</summary>
public sealed record ActivityPresentation(ChatActivityPhase? Phase, int BackgroundActivities, bool PendingLocalWork)
{
    public bool Visible => Phase is not null || BackgroundActivities > 0 || PendingLocalWork;
    public bool Moving => Phase is not ChatActivityPhase.WaitingForApproval && Visible;

    public string Label(string locale = "es")
    {
        var en = locale == "en";
        var label = Phase switch
        {
            ChatActivityPhase.WaitingForResponse => en ? "Waiting for response" : "Esperando respuesta",
            ChatActivityPhase.Reasoning => en ? "Reasoning" : "Razonando",
            ChatActivityPhase.AnswerText => en ? "Responding" : "Respondiendo",
            // Tools includes requested calls, not necessarily admitted/running calls.
            ChatActivityPhase.Tools => en ? "Tools" : "Herramientas",
            ChatActivityPhase.WaitingForApproval => en ? "Input required" : "Requiere tu respuesta",
            _ => BackgroundActivities > 0 ? (en ? "Agents" : "Agentes") : (en ? "Processing…" : "Procesando…"),
        };
        return BackgroundActivities > 0 ? label + " · " + BackgroundActivities + (en ? " agents" : " agentes") : label;
    }

    public static ActivityPresentation From(string? sessionId, string? runId, string? principalLaneId,
        IEnumerable<ChatActivityEvent> observations, bool pendingLocalWork = false, int pendingChildren = 0)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(runId))
            return new(null, Math.Max(0, pendingChildren), pendingLocalWork);
        // A completed turn must win over its earlier deltas even in an unordered batch.
        var latest = observations.Where(e => e.SessionId == sessionId && e.RunId == runId && e.ObservedInProcess)
            .GroupBy(e => e.TurnId).Select(g => g.OrderByDescending(e => e.Sequence).ThenByDescending(e => e.AsOf).First())
            .Where(e => e.LaneId is not null).GroupBy(e => e.LaneId)
            .Select(g => g.OrderByDescending(e => e.Sequence).ThenByDescending(e => e.AsOf).First()).ToArray();
        var principal = principalLaneId is null ? null : latest.SingleOrDefault(e => e.LaneId == principalLaneId);
        var children = principalLaneId is null ? 0 : latest.Count(e => e.LaneId != principalLaneId && !Terminal(e.Phase));
        return new(principal is null || Terminal(principal.Phase) ? null : principal.Phase,
            Math.Max(children, Math.Max(0, pendingChildren)), principal is null && pendingLocalWork);
    }

    private static bool Terminal(ChatActivityPhase phase) =>
        phase is ChatActivityPhase.Completed or ChatActivityPhase.Cancelled or ChatActivityPhase.Failed;
}

/// <summary>User-owned visual preferences only; never model policy or canonical state.</summary>
public sealed record AnimationPreferences(bool Enabled = true, bool ReducedMotion = false,
    string Waiting = "omega-pulse", string Reasoning = "orbit-dots", string Answer = "block-wave",
    string Tools = "conveyor", int IntervalMilliseconds = 100, bool Ascii = false)
{
    public bool IsValid => AnimationCatalog.Names.Contains(Waiting) && AnimationCatalog.Names.Contains(Reasoning)
        && AnimationCatalog.Names.Contains(Answer) && AnimationCatalog.Names.Contains(Tools)
        && IntervalMilliseconds is 100 or 200 or 300;

    public string Effect(ChatActivityPhase? phase) => phase switch
    {
        ChatActivityPhase.Reasoning => Reasoning,
        ChatActivityPhase.AnswerText => Answer,
        ChatActivityPhase.Tools => Tools,
        _ => Waiting,
    };
}

public readonly record struct AnimationCell(string Glyph, int Intensity);

/// <summary>Deterministic native frames: no ANSI, I/O, random globals or terminal framework types.</summary>
public static class AnimationCatalog
{
    // The compact braille cycle used by Pi's native Loader (not pi-animations).
    private static readonly string[] BrailleFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] AsciiSpinnerFrames = ["|", "/", "-", "\\"];
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[]
        { "omega-pulse", "orbit-dots", "block-wave", "conveyor", "braille-spinner" });

    public static IReadOnlyList<AnimationCell> Render(string effect, long frame, int width,
        bool moving = true, bool ascii = false)
    {
        width = Math.Clamp(width, 0, 80);
        var cells = new AnimationCell[width];
        var tick = (int)((frame % 120 + 120) % 120);
        for (var x = 0; x < width; x++)
        {
            if (x == 0) { cells[x] = new(ascii ? "*" : "✦", moving ? 2 + tick / 4 % 4 : 5); continue; }
            if (x == 1 || !moving) { cells[x] = new(" ", 0); continue; }
            if (effect == "braille-spinner")
            {
                var frames = ascii ? AsciiSpinnerFrames : BrailleFrames;
                cells[x] = x == 2 ? new(frames[tick % frames.Length], 5) : new(" ", 0);
                continue;
            }
            var position = x - 2;
            var length = Math.Max(1, width - 2);
            var head = tick % Math.Max(1, length * 2 - 2);
            if (head >= length) head = length * 2 - 2 - head;
            var intensity = effect switch
            {
                "orbit-dots" => (position - tick % length + length) % length < 3 ? 5 : 1,
                "block-wave" => Math.Clamp((int)Math.Round(2.5 + 2.5 * Math.Sin(position * .7 - tick * .4)), 0, 5),
                "conveyor" => (position + tick) % 5 is < 2 ? 5 : 1,
                _ => Math.Clamp(5 - Math.Abs(position - head), 0, 5),
            };
            var glyph = ascii ? intensity > 3 ? "#" : intensity > 1 ? "+" : "."
                : effect == "orbit-dots" || effect == "omega-pulse" ? intensity > 3 ? "●" : intensity > 1 ? "•" : "·"
                : intensity > 3 ? "▮" : intensity > 1 ? "▪" : "·";
            cells[x] = new(glyph, intensity);
        }
        return cells;
    }
}
