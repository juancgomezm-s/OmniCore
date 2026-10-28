namespace OmniCore.Cli;

/// <summary>Opt-in presentation only. Never changes terminal, account or session settings.</summary>
internal sealed class TuiDensityStore(string path)
{
    internal bool Read()
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= 32 && File.ReadAllText(path).Trim() == "compact"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    internal void Save(bool compact)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, compact ? "compact" : "normal"); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
