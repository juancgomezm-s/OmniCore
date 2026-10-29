using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class CompanionCoordinator
{
    public bool AttentionEnabled()
    { using var db = Open(); using var cmd = Cmd(db, null, "SELECT value FROM preferences WHERE id='attention'"); return cmd.ExecuteScalar() as string != "false"; }
    public void SetAttention(bool enabled)
    { using var db = Open(); using var cmd = Cmd(db, null, "INSERT INTO preferences VALUES('attention',$p0) ON CONFLICT(id) DO UPDATE SET value=excluded.value", enabled ? "true" : "false"); cmd.ExecuteNonQuery(); }
    public CompanionMemoryEntry[] MaintainMemory(string category)
    {
        using var db = Open(); using var cmd = Cmd(db, null, "SELECT id,text,category,project,source,status FROM companion_facts WHERE category=$p0 ORDER BY at DESC", category);
        using var reader = cmd.ExecuteReader(); var entries = new List<CompanionMemoryEntry>(); while (reader.Read()) entries.Add(Fact(reader));
        return entries.Concat(category == "personal" ? MemoryFacts() : []).DistinctBy(e => e.Id).ToArray();
    }
    public void EditMemory(CompanionMemoryEntry original, string text, string? project)
    {
        text = text.Trim(); if (text.Length is < 1 or > 2000 || text.Any(char.IsControl) || project?.Length > 200) throw new ArgumentException("Recuerdo inválido.");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Cmd(db, tx, "UPDATE companion_facts SET text=$p1,project=$p2,at=$p3 WHERE id=$p0 AND text=$p4 AND project IS $p5", original.Id, text, project, Now, original.Text, original.Project);
        if (cmd.ExecuteNonQuery() == 0)
        {
            using var existing = Cmd(db, tx, "SELECT 1 FROM companion_facts WHERE id=$p0", original.Id);
            if (existing.ExecuteScalar() is not null) throw new InvalidOperationException("El recuerdo cambió en otra instancia; vuelve a abrir la lista.");
            // Legacy Markdown remains editable; preserve all unrelated lines and headings.
            var memory = ReadMemory(); var match = MemoryEntries(memory).FirstOrDefault(e => e.Entry.Id == original.Id);
            if (match.Entry is null) throw new InvalidOperationException("El recuerdo cambió; recarga la lista.");
            var updated = string.Join("\n", memory.Text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim() == match.Line ? "- " + text : line));
            // SaveMemory owns a separate transaction, so release this read-only transaction first.
            tx.Commit(); SaveMemory(updated, memory.Version); return;
        }
        using var drop = Cmd(db, tx, "DROP TABLE IF EXISTS companion_facts_fts"); drop.ExecuteNonQuery(); tx.Commit();
    }
}
