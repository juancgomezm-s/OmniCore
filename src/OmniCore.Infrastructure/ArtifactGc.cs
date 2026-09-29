namespace OmniCore.Infrastructure;

using System.Text.RegularExpressions;

/// <summary>
/// Garbage collection de blobs del artifact store (ADR-0001 §8, mark-and-sweep):
/// <para>
/// <b>Mark:</b> el conjunto vivo es todo hash referenciado por cualquier evento del journal:
/// la columna artifacts del envelope (formato Parts <c>id|alg|hash;...</c>) más cualquier hash
/// hexadecimal que aparezca en el payload JSON (superaproximación deliberada: si el payload no
/// decodifica por corrupción, sus refs siguen vivas — nunca se borra algo recuperable).
/// </para>
/// <para>
/// <b>Sweep:</b> se borran los blobs fuera del conjunto vivo con una antigüedad mayor al periodo
/// de gracia (24 h por defecto, ADR-0001 §8). La gracia hace de lease de escrituras en curso:
/// un blob recién escrito (posiblemente por un evento aún sin commit) nunca se recoge. Un blob
/// referenciado NUNCA se borra. <c>omni gc --dry-run</c> solo informa.
/// </para>
/// </summary>
public sealed class ArtifactGc
{
    /// <summary>Gracia por defecto: 24 h (ADR-0001 §8).</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(24);

    private static readonly Regex HexHash = new("[0-9a-fA-F]{40,64}", RegexOptions.Compiled);

    private readonly string _blobsRoot;

    public ArtifactGc(string artifactsDataDirectory)
    {
        _blobsRoot = Path.Combine(artifactsDataDirectory, "blobs");
    }

    /// <summary>Resultado del sweep (o de su simulación).</summary>
    public sealed class SweepResult
    {
        public long Scanned { get; init; }

        public long LiveReferenced { get; init; }

        public long KeptByGrace { get; init; }

        public long Deleted { get; init; }

        public long ReclaimedBytes { get; init; }

        public bool DryRun { get; init; }

        public TimeSpan Grace { get; init; }

        public string SummaryLine() =>
            "gc: " + Scanned + " blob(s), " + LiveReferenced + " vivo(s) por referencia, "
                + KeptByGrace + " dentro de la gracia (" + (int) Grace.TotalHours + " h), "
                + Deleted + (DryRun ? " candidato(s) a borrar (dry-run)" : " borrado(s)")
                + (ReclaimedBytes > 0 ? ", " + ReclaimedBytes + " bytes" : "");
    }

    /// <summary>
    /// Marca el conjunto vivo leyendo el journal (read-only) y barre el store. El reloj se
    /// recibe explícito (<paramref name="now"/>) para tests deterministas.
    /// </summary>
    public SweepResult Sweep(string journalPath, TimeSpan grace, bool dryRun, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var live = MarkLive(journalPath, cancellationToken);
        return SweepBlobs(live, grace, dryRun, now, cancellationToken);
    }

    /// <summary>Conjunto vivo: hashes del envelope + hashes que aparecen en el payload JSON.</summary>
    private static HashSet<string> MarkLive(string journalPath, CancellationToken cancellationToken)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(journalPath))
        {
            return live;
        }

        using var conn = Microsoft.Data.Sqlite.SqliteFactory.Instance!
            .CreateDataSource("DataSource=" + journalPath + ";Mode=ReadOnly")!.OpenConnection()!;
        var cmd = conn.CreateCommand()!;
        cmd.CommandText = "SELECT artifacts, payload FROM events";
        var reader = cmd.ExecuteReader()!;
        foreach (System.Data.Common.DbDataRecord row in reader)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = AsText(row.GetValue(0));
            if (envelope is not null && envelope.Length > 0)
            {
                foreach (var part in envelope.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var fields = part.Split('|');
                    if (fields.Length == 3)
                    {
                        live.Add(fields[2]);
                    }
                }
            }

            var payload = AsText(row.GetValue(1));
            if (payload is not null)
            {
                // Superaproximación: cualquier hash hexadecimal del payload cuenta como vivo.
                foreach (Match match in HexHash.Matches(payload))
                {
                    live.Add(match.Value);
                }
            }
        }

        return live;
    }

    private SweepResult SweepBlobs(HashSet<string> live, TimeSpan grace, bool dryRun, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long scanned = 0, keptByGrace = 0, deleted = 0, reclaimed = 0L;
        if (Directory.Exists(_blobsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_blobsRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned += 1;
                var hash = Path.GetFileName(file);
                if (live.Contains(hash))
                {
                    continue;
                }

                var lastWrite = File.GetLastWriteTimeUtc(file);
                if (lastWrite >= now.UtcDateTime - grace)
                {
                    keptByGrace += 1;
                    continue;
                }

                if (dryRun)
                {
                    deleted += 1;
                    reclaimed += new FileInfo(file).Length;
                    continue;
                }

                reclaimed += new FileInfo(file).Length;
                File.Delete(file);
                deleted += 1;
            }
        }

        return new SweepResult {
            Scanned = scanned,
            LiveReferenced = scanned - keptByGrace - deleted,
            KeptByGrace = keptByGrace,
            Deleted = deleted,
            ReclaimedBytes = reclaimed,
            DryRun = dryRun,
            Grace = grace,
        };
    }

    private static string? AsText(object? value) => value is null || value == DBNull.Value
        ? null
        : value.ToString();
}
