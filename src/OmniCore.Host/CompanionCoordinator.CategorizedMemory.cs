using System.Text.RegularExpressions;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class CompanionCoordinator
{
    public static readonly string[] MemoryCategories = ["personal", "work", "agents", "followup"];
    public CompanionMemoryEntry RememberCategorized(string text, string category, string? project = null, string source = "user", string status = "active", string? id = null)
    {
        text = text.Trim().Normalize();
        if (!MemoryCategories.Contains(category) || text.Length is < 1 or > 2000 || text.Any(char.IsControl)
            || project?.Length > 200 || status is not ("active" or "resolved" or "queued" or "received" or "rejected")) throw new ArgumentException("Recuerdo o categoría inválidos.");
        var key = id ?? Hash(category + "\n" + project + "\n" + text.ToUpperInvariant());
        using var db = Open(); using var cmd = Cmd(db, null, "INSERT INTO companion_facts VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6) ON CONFLICT(id) DO UPDATE SET status=excluded.status,at=excluded.at",
            key, category, project, text, source, status, Now); cmd.ExecuteNonQuery();
        return new(key, text, category, project, source, status);
    }
    public CompanionMemoryEntry[] SearchMemory(string query = "", string? category = null, string? project = null, int count = 5)
    {
        if (category is not null && !MemoryCategories.Contains(category)) throw new ArgumentException("Categoría inválida.");
        using var db = Open();
        var words = Regex.Matches(query, @"[\p{L}\p{N}]{2,}").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        var search = string.Join(" OR ", words.Select(w => "\"" + w + "\""));
        var ranked = new List<CompanionMemoryEntry>();
        // Bundled SQLite supports FTS5. If a replacement build does not, LIKE remains functional.
        try
        {
            using var schema = db.CreateCommand(); schema.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS companion_facts_fts USING fts5(id UNINDEXED,text);
                INSERT INTO companion_facts_fts(id,text) SELECT id,text FROM companion_facts WHERE id NOT IN(SELECT id FROM companion_facts_fts);
                DELETE FROM companion_facts_fts WHERE id NOT IN(SELECT id FROM companion_facts);
                """; schema.ExecuteNonQuery();
            if (search.Length > 0)
            {
                using var cmd = Cmd(db, null, "SELECT f.id,f.text,f.category,f.project,f.source,f.status FROM companion_facts_fts JOIN companion_facts f ON companion_facts_fts.id=f.id WHERE companion_facts_fts MATCH $p0 AND ($p1 IS NULL OR f.category=$p1) AND ($p2 IS NULL OR f.project=$p2 OR f.category='personal') AND f.status!='resolved' ORDER BY bm25(companion_facts_fts) LIMIT $p3", search, category, project, count);
                using var reader = cmd.ExecuteReader(); while (reader.Read()) ranked.Add(Fact(reader));
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        if (ranked.Count < count)
        {
            using var cmd = Cmd(db, null, "SELECT id,text,category,project,source,status FROM companion_facts WHERE ($p0 IS NULL OR category=$p0) AND ($p1 IS NULL OR project=$p1 OR category='personal') AND status!='resolved' ORDER BY CASE WHEN category='followup' THEN 0 ELSE 1 END,at DESC LIMIT 200", category, project);
            using var reader = cmd.ExecuteReader();
            while (reader.Read() && ranked.Count < count)
            {
                var item = Fact(reader);
                if (!ranked.Any(f => f.Id == item.Id) && (words.Length == 0 || item.Category == "followup" || words.Any(w => item.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))) ranked.Add(item);
            }
        }
        return ranked.ToArray();
    }
    private static CompanionMemoryEntry Fact(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5));
    public bool ResolveMemory(string id)
    { using var db = Open(); using var cmd = Cmd(db, null, "UPDATE companion_facts SET status='resolved',at=$p1 WHERE id=$p0 AND category='followup'", id, Now); return cmd.ExecuteNonQuery() > 0; }
    private bool ForgetCategorized(string id)
    { using var db = Open(); using var cmd = Cmd(db, null, "DELETE FROM companion_facts WHERE id=$p0", id); return cmd.ExecuteNonQuery() > 0; }
    private CompanionMemoryEntry? CategorizedFact(string id)
    {
        using var db = Open(); using var cmd = Cmd(db, null, "SELECT id,text,category,project,source,status FROM companion_facts WHERE id=$p0", id);
        using var reader = cmd.ExecuteReader(); return reader.Read() ? Fact(reader) : null;
    }
}
