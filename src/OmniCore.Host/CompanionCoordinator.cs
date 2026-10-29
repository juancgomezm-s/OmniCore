using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Protocol;

namespace OmniCore.Host;

/// <summary>Same-profile durable mailbox. A lifetime instance id is never reused by a restarted process.
/// Claims commit before dispatch: a crash cannot replay an instruction automatically.</summary>
public sealed partial class CompanionCoordinator
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    public CompanionCoordinator(string dataDirectory, Func<DateTimeOffset>? clock = null)
    {
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "companion");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_directory); CheckPath(_directory);
        var path = Path.Combine(_directory, "coordinator.db"); CheckPath(path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 1 }.ToString());
        db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS instances(id TEXT PRIMARY KEY, at INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS notices(id TEXT PRIMARY KEY, at INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS deliveries(id TEXT PRIMARY KEY, target TEXT NOT NULL, at INTEGER NOT NULL, status TEXT NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS deliveries_target ON deliveries(target);
            CREATE TABLE IF NOT EXISTS chats(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS preferences(id TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS dialogue(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS dialogue_imports(id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS notice_routes(id TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS projects(id INTEGER PRIMARY KEY AUTOINCREMENT,workspace TEXT UNIQUE NOT NULL);
            CREATE TABLE IF NOT EXISTS companion_facts(id TEXT PRIMARY KEY,category TEXT NOT NULL,project TEXT,text TEXT NOT NULL,source TEXT NOT NULL,status TEXT NOT NULL,at INTEGER NOT NULL);
            """; cmd.ExecuteNonQuery(); return db;
    }
    private static void CheckPath(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("El almacenamiento de la mascota no admite enlaces simbólicos.");
    }
    private static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql, params object?[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, typeof(T), CompanionCoordinatorJson.Default);
    private static T Decode<T>(string json) => (T)JsonSerializer.Deserialize(json, typeof(T), CompanionCoordinatorJson.Default)!;
    private long Now => _clock().ToUnixTimeMilliseconds();
    private static T[] Rows<T>(SqliteConnection db, SqliteTransaction? tx, string sql, params object?[] args)
    {
        using var cmd = Cmd(db, tx, sql, args); using var reader = cmd.ExecuteReader();
        var result = new List<T>(); while (reader.Read()) result.Add(Decode<T>(reader.GetString(0))); return result.ToArray();
    }
    public void Heartbeat(CompanionInstance instance)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Cmd(db, tx, "INSERT INTO instances VALUES($p0,$p1,$p2) ON CONFLICT(id) DO UPDATE SET at=excluded.at,json=excluded.json",
            instance.Id, Now, Encode(instance with { Seen = _clock() })); cmd.ExecuteNonQuery();
        foreach (var table in new[] { "instances", "notices", "deliveries" })
        { using var prune = Cmd(db, tx, "DELETE FROM " + table + " WHERE at<$p0", Now - 7 * 86400000L); prune.ExecuteNonQuery(); }
        foreach (var notice in Rows<CompanionNotice>(db, tx, "SELECT json FROM notices WHERE id LIKE 'decision/%' ORDER BY at DESC LIMIT 100"))
            if (notice.Instance == instance.Id && !notice.Resolved && (notice.Session != instance.Session
                || !instance.Decisions.Any(d => notice.Id == "decision/" + instance.Session + "/" + d.Id)))
            {
                using var resolved = Cmd(db, tx, "UPDATE notices SET json=$p1 WHERE id=$p0", notice.Id,
                    Encode(notice with { Read = true, Resolved = true, Title = "Decisión resuelta" })); resolved.ExecuteNonQuery();
            }
        tx.Commit();
    }
    public void Disconnect(string id)
    { using var db = Open(); using var cmd = Cmd(db, null, "DELETE FROM instances WHERE id=$p0", id); cmd.ExecuteNonQuery(); }
    public CompanionHubSnapshot Read()
    {
        using var db = Open();
        foreach (var item in Rows<CompanionDelivery>(db, null, "SELECT json FROM deliveries WHERE status IN ('queued','claimed') AND target NOT IN (SELECT id FROM instances WHERE at>$p0) LIMIT 128", Now - 15000))
            WriteDelivery(db, null, item with { Status = item.Status == "claimed" ? "unknown" : "rejected",
                Detail = item.Status == "claimed" ? "La instancia se desconectó tras recibirla. Comprueba el resultado antes de reenviar." : "La instancia de destino se desconectó; no se reenviará a otra sesión." }, item.Status);
        CompanionMemory memory;
        try { memory = ReadMemory(); }
        catch (IOException error) { memory = new(Path.Combine(_directory, "memory.md"), "Memoria no disponible: " + error.Message, "unavailable"); }
        return new(Rows<CompanionInstance>(db, null, "SELECT json FROM instances WHERE at>$p0 ORDER BY at DESC LIMIT 64", Now - 15000),
            Rows<CompanionNotice>(db, null, "SELECT json FROM notices ORDER BY at DESC LIMIT 100"),
            Rows<CompanionDelivery>(db, null, "SELECT json FROM deliveries ORDER BY at DESC LIMIT 100"), memory, ReadReaction());
    }
    public void Notify(CompanionNotice notice)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var route = Rows<CompanionInstance>(db, tx, "SELECT json FROM instances WHERE id=$p0", notice.Instance).FirstOrDefault(i => i.Session == notice.Session);
        if (route is not null) { using var binding = Cmd(db, tx, "INSERT OR IGNORE INTO notice_routes VALUES($p0,$p1)", notice.Id, Encode(route)); binding.ExecuteNonQuery(); }
        using var cmd = Cmd(db, tx, "INSERT OR IGNORE INTO notices VALUES($p0,$p1,$p2)", notice.Id, Now, Encode(notice)); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public void MarkRead(string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var notice = Rows<CompanionNotice>(db, tx, "SELECT json FROM notices WHERE id=$p0", id).FirstOrDefault();
        if (notice is not null) { using var cmd = Cmd(db, tx, "UPDATE notices SET json=$p1 WHERE id=$p0", id, Encode(notice with { Read = true })); cmd.ExecuteNonQuery(); }
        tx.Commit();
    }
    public void Send(CompanionDelivery delivery)
    {
        if (!Guid.TryParse(delivery.Id, out _) || delivery.Text.Length > 8000 || string.IsNullOrWhiteSpace(delivery.Text) && delivery.Kind == "instruction"
            || delivery.Kind is not ("instruction" or "answer")) throw new ArgumentException("Instrucción inválida.");
        using var db = Open(); using var tx = db.BeginTransaction();
        var target = Rows<CompanionInstance>(db, tx, "SELECT json FROM instances WHERE id=$p0 AND at>$p1", delivery.Target, Now - 15000).FirstOrDefault();
        if (target is null || target.Session != delivery.Session || target.Run != delivery.Run)
            throw new InvalidOperationException("La instancia se desconectó o cambió de sesión. Selecciona el destino de nuevo.");
        if (!target.AcceptsInstructions)
            throw new InvalidOperationException("Ese principal está en modo headless: admite mensajes laterales, pero no nuevas órdenes humanas desde otra instancia.");
        if (delivery.Kind == "answer" && !target.Decisions.Any(d => d.Id == delivery.Interaction && d.OptionIds.Contains(delivery.Option)))
            throw new InvalidOperationException("La decisión ya no está pendiente.");
        using var count = Cmd(db, tx, "SELECT count(*) FROM deliveries WHERE target=$p0 AND status='queued' AND json_extract(json,'$.kind')!='message'", delivery.Target);
        if (Convert.ToInt32(count.ExecuteScalar()) >= 32) throw new InvalidOperationException("La bandeja de esta instancia está llena.");
        using var cmd = Cmd(db, tx, "INSERT OR IGNORE INTO deliveries VALUES($p0,$p1,$p2,'queued',$p3)", delivery.Id, delivery.Target, Now,
            Encode(delivery with { Status = "queued", Detail = null })); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public CompanionDelivery? Claim(string instance, string session, string? run, bool busy)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var deliveries = Rows<CompanionDelivery>(db, tx, "SELECT json FROM deliveries WHERE target=$p0 AND status='queued' ORDER BY at LIMIT 32", instance);
        foreach (var item in deliveries)
        {
            if (item.Kind == "message") continue; // Lateral data is read by agents.read, never executed as human input.
            var stale = item.Session != session || item.Run != run;
            if (!stale && busy && item.Kind == "instruction") continue;
            var next = item with { Status = stale ? "rejected" : "claimed", Detail = stale ? "La sesión cambió antes de recibir la instrucción." : "Recibida; si el proceso termina no se reintentará automáticamente." };
            WriteDelivery(db, tx, next); tx.Commit(); return stale ? null : next;
        }
        tx.Commit(); return null;
    }
    private static void WriteDelivery(SqliteConnection db, SqliteTransaction? tx, CompanionDelivery delivery, string? expectedStatus = null)
    { using var cmd = Cmd(db, tx, "UPDATE deliveries SET status=$p1,json=$p2 WHERE id=$p0 AND ($p3 IS NULL OR status=$p3)", delivery.Id, delivery.Status, Encode(delivery), expectedStatus); cmd.ExecuteNonQuery(); }
    public void Complete(CompanionDelivery delivery, bool accepted, string detail)
    {
        using var db = Open(); WriteDelivery(db, null, delivery with { Status = accepted ? "accepted" : "rejected", Detail = detail });
        using (var followup = Cmd(db, null, "UPDATE companion_facts SET status=$p1,at=$p2 WHERE source=$p0", "delivery/" + delivery.Id, accepted ? "received" : "rejected", Now)) followup.ExecuteNonQuery();
        Notify(new("delivery/" + delivery.Id, delivery.Target, delivery.Session,
            (accepted ? "Orden recibida · " : "Orden rechazada · ") + detail, _clock()));
    }
    public bool ReadQuiet()
    { using var db = Open(); using var cmd = Cmd(db, null, "SELECT value FROM preferences WHERE id='quiet'"); return cmd.ExecuteScalar() as string == "true"; }
    public void SetQuiet(bool quiet)
    { using var db = Open(); using var cmd = Cmd(db, null, "INSERT INTO preferences VALUES('quiet',$p0) ON CONFLICT(id) DO UPDATE SET value=excluded.value", quiet ? "true" : "false"); cmd.ExecuteNonQuery(); }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public CompanionMemory ReadMemory()
    {
        Directory.CreateDirectory(_directory); CheckPath(_directory);
        var file = Path.Combine(_directory, "memory.md"); CheckPath(file);
        if (File.Exists(file) && new FileInfo(file).Length > 16384) throw new IOException("La memoria excede 16 KiB.");
        var text = File.Exists(file) ? File.ReadAllText(file) : "# Recuerdos de la mascota\n\nNombre preferido: \n\n## Recordar\n\n## Preguntar después\n";
        return new(file, text, Hash(text));
    }
    public CompanionMemory SaveMemory(string text, string expectedVersion)
    {
        if (Encoding.UTF8.GetByteCount(text) > 16384 || text.Contains('\0')) throw new ArgumentException("Memoria inválida; máximo 16 KiB.");
        using var db = Open(); using var tx = db.BeginTransaction();
        var previous = ReadMemory();
        if (previous.Version != expectedVersion) throw new InvalidOperationException("La memoria cambió en otra instancia. Recárgala antes de guardar.");
        var temporary = previous.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, previous.Path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        tx.Commit(); return new(previous.Path, text, Hash(text));
    }
    public CompanionChatMessage[] Chat(string id)
    {
        using var db = Open(); using var cmd = Cmd(db, null, "SELECT json FROM chats WHERE id=$p0", id);
        return cmd.ExecuteScalar() is string json ? Decode<CompanionChatMessage[]>(json) : [];
    }
    public void SaveChat(string id, IEnumerable<CompanionChatMessage> messages)
    {
        var tail = messages.TakeLast(24).Select(m => m with { Text = m.Text[..Math.Min(m.Text.Length, 4000)] }).ToArray();
        using var db = Open(); using var cmd = Cmd(db, null, "INSERT INTO chats VALUES($p0,$p1) ON CONFLICT(id) DO UPDATE SET json=excluded.json", id, Encode(tail)); cmd.ExecuteNonQuery();
        using var prune = Cmd(db, null, "DELETE FROM chats WHERE rowid NOT IN (SELECT rowid FROM chats ORDER BY rowid DESC LIMIT 128)"); prune.ExecuteNonQuery();
    }
}
