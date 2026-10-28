namespace OmniCore.Client;

/// <summary>Local display preference only; never changes a provider request or stores reasoning text.</summary>
public sealed class ReasoningDisplaySettingsStore(string path)
{
    public bool Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16) return true;
            return File.ReadAllText(path).Trim() != "false";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    public bool Save(bool visible)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary, visible ? "true" : "false");
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
