namespace OmniCore.Host;

/// <summary>Presentation-only command copy; keeps Domain types behind the Host boundary.</summary>
public sealed record CommandOutputText(string Key, IReadOnlyDictionary<string, string> Args);

/// <summary>Lets a presentation layer resolve Host command copy without a Host→Client dependency.</summary>
internal static class LocalizedCommandOutput
{
    public static void WriteLine(TextWriter output, Func<CommandOutputText, string>? resolve,
        string key, IReadOnlyDictionary<string, string>? args = null, string? legacyFallback = null)
    {
        var values = args is null ? new Dictionary<string, string>(StringComparer.Ordinal)
            : args.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var text = resolve?.Invoke(new CommandOutputText(key, values)) ?? legacyFallback ?? key;
        output.WriteLine(text);
    }
}
