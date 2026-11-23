namespace OmniCore.Client;

public readonly record struct ConversationDisplaySettings(bool ShowReasoning, bool ShowTools);

/// <summary>Local display preferences for the conversation.</summary>
public sealed class ReasoningDisplaySettingsStore(string path)
{
    public bool Load() => LoadSettings().ShowReasoning;

    public ConversationDisplaySettings LoadSettings()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16) return new(true, true);
            return File.ReadAllText(path).Trim() switch
            {
                "false" => new(false, true),
                "true,false" => new(true, false),
                "false,false" => new(false, false),
                _ => new(true, true),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(true, true); }
    }

    public bool Save(bool visible) => Save(LoadSettings() with { ShowReasoning = visible });

    public bool Save(ConversationDisplaySettings settings)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var reasoning = settings.ShowReasoning ? "true" : "false";
            File.WriteAllText(temporary, settings.ShowTools ? reasoning : reasoning + ",false");
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
