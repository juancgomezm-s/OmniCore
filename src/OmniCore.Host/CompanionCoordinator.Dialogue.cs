using System.Text;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class CompanionCoordinator
{
    public const string DialogueId = "companion.global.v2";
    public string ProjectLabel(string workspace)
    {
        var key = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) key = key.ToLowerInvariant();
        using var db = Open(); using var tx = db.BeginTransaction();
        using (var add = Cmd(db, tx, "INSERT INTO projects(workspace) SELECT $p0 WHERE NOT EXISTS(SELECT 1 FROM projects WHERE workspace=$p0)", key)) add.ExecuteNonQuery();
        using var read = Cmd(db, tx, "SELECT id FROM projects WHERE workspace=$p0", key);
        var id = Convert.ToInt64(read.ExecuteScalar()); tx.Commit(); return "Proyecto " + (100 + id);
    }
    public CompanionInstance? NoticeRoute(string id)
    { using var db = Open(); return Rows<CompanionInstance>(db, null, "SELECT json FROM notice_routes WHERE id=$p0", id).FirstOrDefault(); }
    public void AppendDialogue(CompanionChatMessage message)
    {
        using var db = Open(); using var cmd = Cmd(db, null, "INSERT OR IGNORE INTO dialogue(id,json) VALUES($p0,$p1)",
            message.Id ?? Guid.NewGuid().ToString(), Encode(message)); cmd.ExecuteNonQuery();
    }
    public void ImportLegacyChat(string key)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var marker = Cmd(db, tx, "INSERT OR IGNORE INTO dialogue_imports VALUES($p0)", key);
        if (marker.ExecuteNonQuery() == 0) return;
        using var read = Cmd(db, tx, "SELECT json FROM chats WHERE id=$p0", key);
        if (read.ExecuteScalar() is string json)
        {
            var messages = Decode<CompanionChatMessage[]>(json);
            for (var i = 0; i < messages.Length; i++)
            {
                var message = messages[i] with { Id = "legacy/" + Hash(key + "/" + i) };
                using var insert = Cmd(db, tx, "INSERT OR IGNORE INTO dialogue(id,json) VALUES($p0,$p1)", message.Id, Encode(message)); insert.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }
    public CompanionChatMessage[] DialogueTail(int count = 100)
    {
        using var db = Open(); return Rows<CompanionChatMessage>(db, null, "SELECT json FROM dialogue ORDER BY seq DESC LIMIT $p0", count).Reverse().ToArray();
    }
    public long DialogueRevision()
    { using var db = Open(); using var cmd = Cmd(db, null, "SELECT COALESCE(MAX(seq),0) FROM dialogue"); return Convert.ToInt64(cmd.ExecuteScalar()); }
    /// <summary>Read only the candidate window. The archive is never deleted by context compaction.</summary>
    public CompanionChatMessage[] DialogueContext(int capacity)
    {
        using var db = Open(); using var cmd = Cmd(db, null, "SELECT json FROM dialogue WHERE seq>COALESCE((SELECT CAST(value AS INTEGER) FROM preferences WHERE id='dialogue.through'),0) ORDER BY seq DESC");
        using var reader = cmd.ExecuteReader(); var result = new List<CompanionChatMessage>(); long characters = 0;
        while (reader.Read())
        {
            var item = Decode<CompanionChatMessage>(reader.GetString(0)); result.Add(item); characters += item.Text.Length;
            if (characters > (long)capacity * 8) break;
        }
        return result.AsEnumerable().Reverse().ToArray();
    }
    public CompanionNotice[] PendingNotices()
    {
        using var db = Open(); return Rows<CompanionNotice>(db, null, """
            SELECT json FROM (
              SELECT json,at,ROW_NUMBER() OVER(PARTITION BY json_extract(json,'$.instance'),json_extract(json,'$.session'),
                CASE WHEN instr(id,'/')=0 THEN id ELSE substr(id,1,instr(id,'/')-1) END ORDER BY at DESC) AS position
              FROM notices WHERE COALESCE(json_extract(json,'$.read'),0)=0 AND COALESCE(json_extract(json,'$.resolved'),0)=0
            ) WHERE position=1 ORDER BY at DESC
            """);
    }
    public CompanionChatMessage[] PresentNotices(bool acknowledge = true, int count = 8)
    {
        var pending = PendingNotices().Take(count).ToArray(); var labels = pending.ToDictionary(n => n.Id, n =>
        {
            var route = NoticeRoute(n.Id); return route is null ? "Sesión " + n.Session[..Math.Min(8, n.Session.Length)] : ProjectLabel(route.Workspace);
        });
        using var db = Open(); using var tx = db.BeginTransaction(); var added = new List<CompanionChatMessage>();
        foreach (var notice in pending.OrderBy(n => n.At))
        {
            var text = labels[notice.Id] + ": " + notice.Title;
            var message = new CompanionChatMessage("notice", text, "notice/" + notice.Id, notice.Id);
            using var insert = Cmd(db, tx, "INSERT OR IGNORE INTO dialogue(id,json) VALUES($p0,$p1)", message.Id, Encode(message));
            insert.ExecuteNonQuery(); added.Add(message);
            if (!acknowledge) continue;
            foreach (var old in Rows<CompanionNotice>(db, tx, "SELECT json FROM notices WHERE json_extract(json,'$.instance')=$p0 AND json_extract(json,'$.session')=$p1", notice.Instance, notice.Session)
                .Where(n => n.Id.Split('/')[0] == notice.Id.Split('/')[0] && n.At <= notice.At))
            { using var mark = Cmd(db, tx, "UPDATE notices SET json=$p1 WHERE id=$p0", old.Id, Encode(old with { Read = true })); mark.ExecuteNonQuery(); }
        }
        tx.Commit(); return added.ToArray();
    }
    public void AcknowledgeNotices(IEnumerable<string> ids)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var id in ids)
        {
            var shown = Rows<CompanionNotice>(db, tx, "SELECT json FROM notices WHERE id=$p0", id).FirstOrDefault();
            if (shown is null) continue;
            foreach (var old in Rows<CompanionNotice>(db, tx, "SELECT json FROM notices WHERE json_extract(json,'$.instance')=$p0 AND json_extract(json,'$.session')=$p1", shown.Instance, shown.Session)
                .Where(n => n.At <= shown.At && n.Id.Split('/')[0] == shown.Id.Split('/')[0]))
            { using var mark = Cmd(db, tx, "UPDATE notices SET json=$p1 WHERE id=$p0", old.Id, Encode(old with { Read = true })); mark.ExecuteNonQuery(); }
        }
        tx.Commit();
    }
    public string ConversationSummary()
    { using var db = Open(); using var read = Cmd(db, null, "SELECT value FROM preferences WHERE id='dialogue.summary'"); return read.ExecuteScalar() as string ?? ""; }
    public void SaveConversationSummary(string text)
    { using var db = Open(); using var save = Cmd(db, null, "INSERT INTO preferences VALUES('dialogue.summary',$p0) ON CONFLICT(id) DO UPDATE SET value=excluded.value", text); save.ExecuteNonQuery(); }
    public void SaveConversationCheckpoint(string text, string? throughId)
    {
        if (throughId is null) return;
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cursor = Cmd(db, tx, "SELECT seq FROM dialogue WHERE id=$p0", throughId); var sequence = cursor.ExecuteScalar();
        if (sequence is not long number) return;
        using var prior = Cmd(db, tx, "SELECT CAST(value AS INTEGER) FROM preferences WHERE id='dialogue.through'");
        if (prior.ExecuteScalar() is long previous && previous > number) return;
        using (var save = Cmd(db, tx, "INSERT INTO preferences VALUES('dialogue.summary',$p0) ON CONFLICT(id) DO UPDATE SET value=excluded.value", text)) save.ExecuteNonQuery();
        using (var save = Cmd(db, tx, "INSERT INTO preferences VALUES('dialogue.through',$p0) ON CONFLICT(id) DO UPDATE SET value=excluded.value", number.ToString(System.Globalization.CultureInfo.InvariantCulture))) save.ExecuteNonQuery();
        tx.Commit();
    }
}
