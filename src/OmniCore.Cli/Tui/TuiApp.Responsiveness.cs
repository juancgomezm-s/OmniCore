using Terminal.Gui.App;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private Action? _restoreInputLoopRate;
    private DateTimeOffset? _inputBurstUntil;

    private void PrioritizeInput()
    {
        if (_restoreInputLoopRate is null) return;
        // Faster input service during a gesture, then the original quiet loop rate.
        // This does not request a draw, speed up animations or change journal polling.
        Application.MaximumIterationsPerSecond = 50;
        _inputBurstUntil = DateTimeOffset.UtcNow.AddMilliseconds(500);
    }
}
