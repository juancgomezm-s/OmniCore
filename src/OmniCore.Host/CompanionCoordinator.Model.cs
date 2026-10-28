using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class CompanionCoordinator
{
    public CompanionChatSettings ChatSettings()
    {
        using var db = Open(); using var cmd = Cmd(db, null, "SELECT value FROM preferences WHERE id='chat-model'");
        return cmd.ExecuteScalar() is string json ? Decode<CompanionChatSettings>(json) : new();
    }
    public void SaveChatSettings(CompanionChatSettings settings, IReadOnlyList<CompanionModelOption> models)
    {
        _ = SelectChatModel(settings, models);
        using var db = Open(); using var cmd = Cmd(db, null,
            "INSERT INTO preferences VALUES('chat-model',$p0) ON CONFLICT(id) DO UPDATE SET value=excluded.value", Encode(settings)); cmd.ExecuteNonQuery();
    }
    public static CompanionModelOption SelectChatModel(CompanionChatSettings settings, IReadOnlyList<CompanionModelOption> models)
    {
        var model = models.SingleOrDefault(m => m.Id == settings.ModelId)
            ?? throw new InvalidOperationException("Elige un modelo propio para la mascota en Configuración > Modelos y ejecución > Mascota: modelo y consumo.");
        if (settings.LocalOnly && !model.Local) throw new InvalidOperationException("Solo modelo local está activado. Elige un modelo local; no se usará una ruta remota como respaldo.");
        return model;
    }
    private static void EnsureUsage(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        using var cmd = Cmd(db, null, """
            CREATE TABLE IF NOT EXISTS companion_usage_receipts(id TEXT PRIMARY KEY, model TEXT NOT NULL, provider TEXT NOT NULL, status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS companion_usage_totals(model TEXT NOT NULL, provider TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(model,provider));
            """); cmd.ExecuteNonQuery();
    }
    public CompanionChatUsage[] ChatUsage()
    { using var db = Open(); EnsureUsage(db); return Rows<CompanionChatUsage>(db, null, "SELECT json FROM companion_usage_totals ORDER BY provider,model"); }
    public void BeginChatUsage(string id, string model, string provider)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("Invalid usage identity.");
        using var db = Open(); EnsureUsage(db); using var tx = db.BeginTransaction();
        using var insert = Cmd(db, tx, "INSERT OR IGNORE INTO companion_usage_receipts VALUES($p0,$p1,$p2,'pending')", id, model, provider);
        if (insert.ExecuteNonQuery() == 0) { tx.Commit(); return; }
        var current = Rows<CompanionChatUsage>(db, tx, "SELECT json FROM companion_usage_totals WHERE model=$p0 AND provider=$p1", model, provider).SingleOrDefault() ?? new(model, provider);
        WriteUsage(db, tx, current with { Requests = checked(current.Requests + 1) }); tx.Commit();
    }
    public void FinishChatUsage(CompanionUsageSample sample, bool failed = false)
    {
        if (new[] { sample.Input, sample.Output, sample.CacheRead, sample.CacheWrite, sample.Reasoning }.Any(n => n < 0)
            || sample.EstimatedUsd < 0 || sample.ReportedUsd < 0) throw new ArgumentException("Invalid usage measurement.");
        using var db = Open(); EnsureUsage(db); using var tx = db.BeginTransaction();
        using var update = Cmd(db, tx, "UPDATE companion_usage_receipts SET status=$p3 WHERE id=$p0 AND model=$p1 AND provider=$p2 AND status='pending'",
            sample.Id, sample.Model, sample.Provider, failed ? "failed" : "completed");
        if (update.ExecuteNonQuery() == 0) { tx.Commit(); return; }
        var current = Rows<CompanionChatUsage>(db, tx, "SELECT json FROM companion_usage_totals WHERE model=$p0 AND provider=$p1", sample.Model, sample.Provider).Single();
        WriteUsage(db, tx, current with
        {
            Responses = checked(current.Responses + (failed ? 0 : 1)), Failed = checked(current.Failed + (failed ? 1 : 0)),
            Input = checked(current.Input + (sample.Input ?? 0)), Output = checked(current.Output + (sample.Output ?? 0)),
            CacheRead = checked(current.CacheRead + (sample.CacheRead ?? 0)), CacheWrite = checked(current.CacheWrite + (sample.CacheWrite ?? 0)),
            Reasoning = checked(current.Reasoning + (sample.Reasoning ?? 0)),
            MeasuredTokenRequests = checked(current.MeasuredTokenRequests + (sample.Input is not null && sample.Output is not null ? 1 : 0)),
            EstimatedUsd = checked(current.EstimatedUsd + (sample.EstimatedUsd ?? 0)), PricedResponses = checked(current.PricedResponses + (sample.EstimatedUsd is not null ? 1 : 0)),
            ReportedUsd = checked(current.ReportedUsd + (sample.ReportedUsd ?? 0)), ReportedCostResponses = checked(current.ReportedCostResponses + (sample.ReportedUsd is not null ? 1 : 0))
        }); tx.Commit();
    }
    private static void WriteUsage(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx, CompanionChatUsage usage)
    {
        using var cmd = Cmd(db, tx, "INSERT INTO companion_usage_totals VALUES($p0,$p1,$p2) ON CONFLICT(model,provider) DO UPDATE SET json=excluded.json", usage.Model, usage.Provider, Encode(usage)); cmd.ExecuteNonQuery();
    }
}
