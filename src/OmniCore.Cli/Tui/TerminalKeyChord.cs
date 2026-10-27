using OmniCore.Client;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace OmniCore.Cli;

/// <summary>The only Terminal.Gui translation boundary for portable key bindings.</summary>
internal static class TerminalKeyChord
{
    internal static bool TryConvert(KeyCode code, out KeyChord chord)
    {
        var modifiers = KeyModifiers.None;
        if ((code & KeyCode.CtrlMask) != 0) modifiers |= KeyModifiers.Control;
        if ((code & KeyCode.AltMask) != 0) modifiers |= KeyModifiers.Alt;
        if ((code & KeyCode.ShiftMask) != 0) modifiers |= KeyModifiers.Shift;
        var bare = code & ~(KeyCode.CtrlMask | KeyCode.AltMask | KeyCode.ShiftMask);
        var name = bare switch
        {
            KeyCode.CursorUp => "Up", KeyCode.CursorDown => "Down", KeyCode.CursorLeft => "Left", KeyCode.CursorRight => "Right",
            KeyCode.Delete => "Delete", KeyCode.Backspace => "Backspace", KeyCode.Insert => "Insert",
            _ => bare.ToString()
        };
        if ((uint)bare is >= 32 and <= 0xffff)
            name = bare == KeyCode.Space ? "Space" : ((char)bare).ToString();
        if (name == "+") name = "Plus";
        // Use the parser's canonical identity for aliases, letters and function keys.
        if (!KeyChord.TryParse(name, out chord)) return false;
        chord = chord with { Modifiers = modifiers };
        return true;
    }

    internal static bool Supports(KeyBindingDefinition binding)
    {
        if (!KeyChord.TryParseSequence(binding.Keys, out var sequence)) return true; // Keep the resolver's precise error.
        // Esc belongs to the interaction's default/deny and the overlay stack. Meta is not delivered by this driver.
        return sequence.All(key => key.Key != "Esc" && !key.Modifiers.HasFlag(KeyModifiers.Meta));
    }
}
