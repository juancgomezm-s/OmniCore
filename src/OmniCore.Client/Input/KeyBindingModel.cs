using System.Collections.Immutable;
using OmniCore.Protocol;

namespace OmniCore.Client;

[Flags]
public enum KeyModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Meta = 8 }

/// <summary>Portable key identity; terminal key codes are translated by the renderer.</summary>
public readonly record struct KeyChord(string Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return false;
        var parts = text.Split('+');
        var modifiers = KeyModifiers.None;
        foreach (var part in parts.Take(parts.Length - 1))
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyModifiers.Control,
                "alt" => KeyModifiers.Alt, "shift" => KeyModifiers.Shift,
                "meta" or "cmd" => KeyModifiers.Meta, _ => KeyModifiers.None,
            };
            if (modifier == KeyModifiers.None || modifiers.HasFlag(modifier)) return false;
            modifiers |= modifier;
        }
        var key = parts[^1];
        if (key.Length == 1 && !char.IsWhiteSpace(key[0]) && !char.IsControl(key[0]))
            key = key == "-" ? "Minus" : key.ToUpperInvariant();
        else
        {
            key = key.ToLowerInvariant() switch
            {
                "esc" or "escape" => "Esc", "return" or "enter" => "Enter", "space" => "Space",
                "tab" => "Tab", "backspace" => "Backspace", "delete" => "Delete", "insert" => "Insert",
                "up" => "Up", "down" => "Down", "left" => "Left", "right" => "Right",
                "home" => "Home", "end" => "End", "pageup" => "PageUp", "pagedown" => "PageDown",
                "plus" => "Plus", "minus" => "Minus", _ => key.ToUpperInvariant(),
            };
            if (key is not ("Esc" or "Enter" or "Space" or "Tab" or "Backspace" or "Delete" or "Insert"
                or "Up" or "Down" or "Left" or "Right" or "Home" or "End" or "PageUp" or "PageDown" or "Plus" or "Minus")
                && !(key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var number) && number is >= 1 and <= 24
                    && key == "F" + number.ToString(System.Globalization.CultureInfo.InvariantCulture))) return false;
        }
        chord = new(key, modifiers); return true;
    }

    public static bool TryParseSequence(string? text, out ImmutableArray<KeyChord> sequence)
    {
        sequence = [];
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256) return false;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) return false;
        var keys = ImmutableArray.CreateBuilder<KeyChord>();
        foreach (var part in parts) { if (!TryParse(part, out var key)) return false; keys.Add(key); }
        sequence = keys.ToImmutable(); return true;
    }

    public override string ToString() => (Modifiers.HasFlag(KeyModifiers.Control) ? "Ctrl+" : "")
        + (Modifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : "") + (Modifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : "")
        + (Modifiers.HasFlag(KeyModifiers.Meta) ? "Meta+" : "") + Key;
}

public enum KeyBindingSource { Default, Extension, User }
public enum KeyBindingTargetKind { Action, Command }

public sealed record KeyBindingTarget
{
    public KeyBindingTargetKind Kind { get; }
    public string Id { get; }
    public ImmutableArray<string> Arguments { get; }
    public string CatalogKey => Kind + ":" + Id;
    internal string? EffectiveActionId => Kind == KeyBindingTargetKind.Action ? Id
        : ClientCommandRegistry.Default.TryResolve(new CommandInvocation(Id, Arguments, "KeyBinding"), out var action) ? action?.ActionId : null;
    public KeyBindingTarget(KeyBindingTargetKind kind, string id, IEnumerable<string>? arguments = null)
    {
        Kind = kind; Id = id; Arguments = (arguments ?? []).ToImmutableArray();
    }
    internal bool SameAs(KeyBindingTarget other) => (EffectiveActionId is { } action && other.EffectiveActionId is { } otherAction
        ? action == otherAction : Kind == other.Kind && Id == other.Id)
        && Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal);
}

public sealed record KeyBindingDefinition(string Keys, KeyBindingTarget Target, string When = "true", int Priority = 0,
    KeyBindingSource Source = KeyBindingSource.Default, string SourceId = "Core", bool RemoveDefault = false);
public sealed record KeyBindingDiagnostic(string Code, string Subject, string? Detail = null, string SourceId = "Core");

public sealed class KeyBindingContext
{
    private readonly ImmutableDictionary<string, bool> _values;
    public KeyBindingContext(IEnumerable<KeyValuePair<string, bool>> values) =>
        _values = values.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    public bool this[string key] => _values.TryGetValue(key, out var value) && value;
    internal bool SameAs(KeyBindingContext other) => _values.All(pair => pair.Value == other[pair.Key])
        && other._values.All(pair => pair.Value == this[pair.Key]);
}

public sealed class KeyBinding
{
    public KeyBindingDefinition Definition { get; }
    public ImmutableArray<KeyChord> Sequence { get; }
    internal KeyBindingCondition Condition { get; }
    internal KeyBinding(KeyBindingDefinition definition, ImmutableArray<KeyChord> sequence, KeyBindingCondition condition)
    { Definition = definition; Sequence = sequence; Condition = condition; }
}

public enum KeyBindingResolutionStatus { None, Pending, Matched, Conflict }
public sealed record KeyBindingResolution(KeyBindingResolutionStatus Status, KeyBinding? Binding,
    ImmutableArray<KeyBinding> Candidates);

/// <summary>Target ownership is supplied by the client's action/command registry, never by YAML.</summary>
public sealed class KeyBindingTargetCatalog
{
    private readonly ImmutableDictionary<string, string> _owners;
    public KeyBindingTargetCatalog(IEnumerable<KeyValuePair<string, string>> owners) => _owners = owners.ToImmutableDictionary(StringComparer.Ordinal);
    public KeyBindingTargetCatalog WithTargets(IEnumerable<KeyValuePair<string, string>> owners) => new(_owners.Concat(owners));
    internal string? Owner(KeyBindingTarget target) => _owners.GetValueOrDefault(target.CatalogKey);
    public static KeyBindingTargetCatalog Core { get; } = new(
        ClientCommandRegistry.Default.Actions.Select(action => "Action:" + action.Id)
            .Concat(ClientCommandRegistry.Default.Commands.Select(command => "Command:" + command.Name))
            .Concat(new[] { "sidebar.toggle", "model.select", "preferences.open", "palette.open", "overlay.close", "keybindings.open", "session.search", "agent.inspect" }.Select(id => "Action:" + id))
            .Distinct(StringComparer.Ordinal).Select(key => new KeyValuePair<string, string>(key, "Core")));
}

public static class KeyBindingDefaults
{
    public static ImmutableArray<string> ContextKeys { get; } = ["input", "runActive", "interruptPending", "awaitingPermission",
        "paletteOpen", "overlayOpen", "view.plan", "view.tasks", "view.context"];
    public static ImmutableArray<KeyBindingDefinition> Safety { get; } = [
        new("Ctrl+C", new(KeyBindingTargetKind.Action, ClientActionIds.RunInterrupt), "runActive && !interruptPending"),
        new("Ctrl+C", new(KeyBindingTargetKind.Action, ClientActionIds.RunCancel), "runActive && interruptPending"),
        new("Ctrl+Shift+C", new(KeyBindingTargetKind.Action, ClientActionIds.RunCancel), "runActive")];
    public static ImmutableArray<KeyBindingDefinition> All { get; } = Safety.AddRange(new KeyBindingDefinition[] {
        new("F2", new(KeyBindingTargetKind.Action, "sidebar.toggle")),
        new("F3", new(KeyBindingTargetKind.Action, "model.select"), "!overlayOpen"),
        new("F4", new(KeyBindingTargetKind.Action, "preferences.open")),
        new("Ctrl+P", new(KeyBindingTargetKind.Action, "palette.open"), "!overlayOpen && !awaitingPermission"),
        new("Ctrl+F", new(KeyBindingTargetKind.Action, "session.search"), "!overlayOpen && !awaitingPermission") });
}
