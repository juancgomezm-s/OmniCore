namespace OmniCore.Host;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Domain;

/// <summary>Bounded routing signals from human input, never application text or WorkingState.</summary>
internal static class DesktopSkillDemand
{
    internal const string TaskType = "desktop-interaction";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static bool Match(string input, string pattern) => Regex.IsMatch(input, pattern, Options, TimeSpan.FromMilliseconds(100));
    internal static bool Matches(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 32_768) return false;
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var text = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        text = Regex.Replace(text, @"```[\s\S]*?```|`[^`]*`|\b[\w-]+\.(?:cs|rs|py|js|tsx?|json|md|ya?ml)\b", " ", Options, TimeSpan.FromMilliseconds(100));
        if (Match(text, @"^\s*(?:explica(?:me)?\s+como|documenta|explain\s+how|what\s+is|que\s+es)\b")) return false;
        text = Regex.Replace(text, @"\b(?:no\s+quiero\s+que|do\s+not|don't|no|sin|without)\s+(?:\w+\s+){0,2}(?:prueb\w*|prob\w*|captur\w*|control\w*|interact\w*|automat\w*|escrib\w*|test\w*|click\w*)\b", " ", Options, TimeSpan.FromMilliseconds(100));
        var target = Match(text, @"\b(?:ventanas?|pantalla|escritorio|aplicacion|interfaz|app|windows?|screen|desktop|application|interface|boton|teclado|raton|button|keyboard|mouse|notepad|calculadora|calculator|navegador|browser)\b|\bbloc\s+de\s+notas\b");
        var action = Match(text, @"\b(?:prueb\w*|prob(?:ar|emos|ando)|verifica\w*|comprueb\w*|captur\w*|control\w*|pulsa\w*|presiona\w*|escrib\w*|automatiz\w*|interact\w*|clic|test\w*|verify|capture|inspect|click|type|press|operate|automate|interact|screenshot)\b");
        return target && action || Match(text, @"\b(?:haz\s+clic|toma\s+(?:una\s+)?captura\s+de\s+pantalla)\b");
    }

    internal static string ReadHumanText(string? partsJson)
    {
        if (string.IsNullOrWhiteSpace(partsJson)) return "";
        using var doc = JsonDocument.Parse(partsJson);
        if (doc.RootElement.ValueKind == JsonValueKind.String) return doc.RootElement.GetString() ?? "";
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", doc.RootElement.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String
            ? p.GetString() : p.ValueKind == JsonValueKind.Object && p.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String && type.GetString() == "text"
                && p.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : ""));
    }
}

/// <summary>Releases only the temporary automatic desktop selection after a settled principal turn.</summary>
internal sealed class AutomaticDesktopSkillScope(SessionId session, SessionSkillActivationStore store,
    Action<SkillActivationChanged> emit, Func<bool> pendingTurn, Action? unloaded = null) : IDisposable
{
    public void Dispose()
    {
        var identity = new SkillActivationIdentity(ScopeLevel.BuiltIn, "desktop-control");
        var activation = store.ReadDetailed(session).GetValueOrDefault(identity);
        if (activation is null || activation.Origin != "Automatic" || pendingTurn()) return;
        store.Release(session, identity.Scope, identity.Id);
        try { emit(new SkillActivationChanged(identity.Id, identity.Scope, activation.ManifestSha256, Activated: false, Origin: "Automatic")); }
        catch
        {
            store.Activate(session, identity.Scope, identity.Id, activation.ManifestSha256, activation.PackageContentSha256, activation.Origin);
            throw;
        }
        unloaded?.Invoke();
    }

    internal static bool HasOpenTurn(IReadOnlyList<DomainEventPayload> events, LaneId lane)
    {
        var last = events.OfType<TurnStarted>().LastOrDefault(e => e.LaneId == lane);
        return last is not null && !events.Any(e => e is TurnCompleted completed && completed.TurnId == last.TurnId
            || e is TurnAbandoned abandoned && abandoned.TurnId == last.TurnId);
    }
}
