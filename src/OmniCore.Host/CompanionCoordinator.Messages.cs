using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class CompanionCoordinator
{
    /// <summary>Same mailbox as human instructions. Peers supply data, never user authority.</summary>
    public CompanionDelivery SendPeer(string sender, string session, string target, string text, string operationId)
    {
        if (!Guid.TryParse(operationId, out _) || string.IsNullOrWhiteSpace(text) || text.Length > 4000)
            throw new ArgumentException("Mensaje lateral inválido.");
        using var db = Open(); using var tx = db.BeginTransaction();
        var prior = Rows<CompanionDelivery>(db, tx, "SELECT json FROM deliveries WHERE id=$p0", operationId).SingleOrDefault();
        if (prior is not null)
        {
            if (prior.Kind != "message" || prior.SenderInstance != sender || prior.SenderSession != session
                || prior.Text != text || prior.Target != target && prior.Session != target)
                throw new InvalidOperationException("Identificador de envío reutilizado con otro mensaje.");
            return prior;
        }
        var source = Rows<CompanionInstance>(db, tx, "SELECT json FROM instances WHERE id=$p0 AND at>$p1", sender, Now - 15000).SingleOrDefault();
        if (source is null || source.Session != session || !source.SupportsLateral) throw new InvalidOperationException("El remitente ya no está en esta sesión.");
        var destinations = Rows<CompanionInstance>(db, tx, "SELECT json FROM instances WHERE at>$p0", Now - 15000)
            .Where(i => i.Id == target || i.Session == target).ToArray();
        if (destinations.Length != 1) throw new InvalidOperationException("Indica un ID exacto de una única sesión o instancia activa de agents.list.");
        var destination = destinations[0];
        if (!destination.SupportsLateral) throw new InvalidOperationException("La instancia de destino necesita actualizar OmniCore para recibir mensajes laterales.");
        if (destination.Id == sender || destination.Session == session) throw new ArgumentException("El mensaje lateral necesita otra sesión.");
        using var sequence = Cmd(db, tx, "INSERT INTO preferences VALUES('mailbox.sequence','1') ON CONFLICT(id) DO UPDATE SET value=CAST(value AS INTEGER)+1 RETURNING value");
        var next = Convert.ToInt64(sequence.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        var delivery = new CompanionDelivery(operationId, destination.Id, destination.Session, destination.Run, "message", text,
            SenderInstance: sender, SenderSession: session, Sequence: next);
        using var insert = Cmd(db, tx, "INSERT INTO deliveries VALUES($p0,$p1,$p2,'queued',$p3)", delivery.Id, delivery.Target, Now, Encode(delivery));
        insert.ExecuteNonQuery();
        var notice = new CompanionNotice("message/" + delivery.Id, delivery.Target, delivery.Session,
            "Mensaje lateral · " + session + " · " + text[..Math.Min(text.Length, 160)], _clock());
        using var notification = Cmd(db, tx, "INSERT OR IGNORE INTO notices VALUES($p0,$p1,$p2)", notice.Id, Now, Encode(notice));
        notification.ExecuteNonQuery(); tx.Commit();
        return delivery;
    }

    /// <summary>A cursor is repeatable after lost responses; reading never starts a model turn.</summary>
    public CompanionMailboxPage ReadPeer(string recipient, string session, long after = 0)
    {
        if (after < 0) throw new ArgumentException("Cursor inválido.");
        using var db = Open(); using var tx = db.BeginTransaction();
        var rows = new List<CompanionDelivery>();
        using (var cmd = Cmd(db, tx, "SELECT json FROM deliveries WHERE target=$p0 AND json_extract(json,'$.sequence')>$p1 AND json_extract(json,'$.session')=$p2 AND json_extract(json,'$.kind')='message' AND status IN ('queued','accepted') ORDER BY json_extract(json,'$.sequence') LIMIT 32", recipient, after, session))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var item = Decode<CompanionDelivery>(reader.GetString(0));
                if (item.Session == session && item.Kind == "message" && item.Status is "queued" or "accepted")
                    rows.Add(item with { Status = "accepted" });
            }
        foreach (var item in rows) WriteDelivery(db, tx, item);
        tx.Commit();
        return new(rows.ToArray(), rows.LastOrDefault()?.Sequence ?? after);
    }
}
