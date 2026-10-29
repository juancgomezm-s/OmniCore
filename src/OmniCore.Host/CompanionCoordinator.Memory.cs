using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class CompanionCoordinator
{
    private static IEnumerable<(string Line, CompanionMemoryEntry Entry)> MemoryEntries(CompanionMemory memory) =>
        memory.Text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line != "Nombre preferido:")
            .Select(line => (line, new CompanionMemoryEntry(Hash(line), line.TrimStart('-', ' ').Trim())));
    public CompanionMemoryEntry[] MemoryFacts() => MemoryEntries(ReadMemory()).Select(row => row.Entry).ToArray();
    public CompanionMemoryEntry Remember(string text)
    {
        text = text.Trim().Normalize();
        if (text.Length is < 1 or > 500 || text.Any(char.IsControl) || text.StartsWith('#'))
            throw new ArgumentException("Guarda un recuerdo de una línea, de hasta 500 caracteres.");
        var memory = ReadMemory();
        var existing = MemoryEntries(memory).FirstOrDefault(row => StringComparer.OrdinalIgnoreCase.Equals(row.Entry.Text, text));
        if (existing.Entry is not null) return existing.Entry;
        var line = "- " + text;
        SaveMemory(memory.Text.TrimEnd() + "\n" + line + "\n", memory.Version);
        return new(Hash(line), text);
    }
    public bool Forget(string id)
    {
        if (id.Length != 64 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Usa el identificador devuelto por memory.read.");
        var categorized = CategorizedFact(id); var deleted = ForgetCategorized(id);
        var memory = ReadMemory(); var match = MemoryEntries(memory).FirstOrDefault(row => row.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            || categorized?.Category == "personal" && row.Entry.Text.Equals(categorized.Text, StringComparison.OrdinalIgnoreCase));
        if (match.Entry is null) return deleted;
        var lines = memory.Text.Replace("\r\n", "\n").Split('\n').Where(line => line.Trim() != match.Line);
        SaveMemory(string.Join('\n', lines), memory.Version); return true;
    }
    public CompanionReaction React(string animation)
    {
        if (!CompanionPetProtocol.Animations.Contains(animation, StringComparer.Ordinal)) throw new ArgumentException("Reacción no disponible.");
        var reaction = new CompanionReaction(animation, _clock().AddSeconds(4), 2);
        using var db = Open(); using var cmd = Cmd(db, null, "INSERT INTO preferences VALUES('reaction',$p0) ON CONFLICT(id) DO UPDATE SET value=excluded.value", Encode(reaction));
        cmd.ExecuteNonQuery(); return reaction;
    }
    private CompanionReaction? ReadReaction()
    {
        using var db = Open(); using var cmd = Cmd(db, null, "SELECT value FROM preferences WHERE id='reaction'");
        var reaction = cmd.ExecuteScalar() is string json ? Decode<CompanionReaction>(json) : null;
        return reaction?.Until > _clock() ? reaction : null;
    }
}
