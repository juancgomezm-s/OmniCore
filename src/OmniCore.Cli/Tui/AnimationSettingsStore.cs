using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Client;

namespace OmniCore.Cli;

/// <summary>Dedicated presentation file: never reads/writes account or provider configuration.</summary>
internal sealed class AnimationSettingsStore(string path)
{
    internal string? Notice { get; private set; }
    internal AnimationPreferences Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            // Bound local config input; do not interpret paths, scripts or arbitrary effect names.
            if (new FileInfo(path).Length > 8192) throw new JsonException();
            var options = JsonSerializer.Deserialize(File.ReadAllText(path), AnimationSettingsJson.Default.AnimationPreferences);
            if (options is null || !options.IsValid) throw new JsonException();
            return options;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { Notice = "invalid"; return new(); }
    }

    internal bool Save(AnimationPreferences preferences)
    {
        if (!preferences.IsValid) return false;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, AnimationSettingsJson.Default.AnimationPreferences));
            File.Move(temporary, path, overwrite: true);
            Notice = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Notice = "save-failed"; return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AnimationPreferences))]
internal partial class AnimationSettingsJson : JsonSerializerContext;
