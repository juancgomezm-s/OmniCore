using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

public sealed record WidgetPreferences(string Visible = "auto", bool Expanded = false, int? Priority = null);
public sealed record SidebarPreferences(bool? Visible = null, string Mode = "auto",
    int StackedMinWidth = 120, int TabbedMinWidth = 90,
    IReadOnlyDictionary<string, WidgetPreferences>? Widgets = null);
public sealed record SidebarPreferencesSnapshot(string Revision, SidebarPreferences Preferences,
    IReadOnlyList<string> Diagnostics, IReadOnlyDictionary<string, string>? Sources = null);

public static class SidebarPreferencesJson
{
    public static string Encode(SidebarPreferencesSnapshot value) => JsonSerializer.Serialize(value, SidebarPreferencesJsonContext.Default.SidebarPreferencesSnapshot);
    public static SidebarPreferencesSnapshot? Decode(string json) => JsonSerializer.Deserialize(json, SidebarPreferencesJsonContext.Default.SidebarPreferencesSnapshot);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SidebarPreferencesSnapshot))]
internal partial class SidebarPreferencesJsonContext : JsonSerializerContext;
