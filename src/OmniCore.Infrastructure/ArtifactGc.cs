namespace OmniCore.Infrastructure;

using System.Text.RegularExpressions;
using OmniCore.Domain;

/// <summary>
/// Garbage collection de blobs del artifact store (ADR-0001 §8, mark-and-sweep):
/// <para>
/// <b>Mark:</b> el conjunto vivo es todo hash referenciado por cualquier evento del journal:
/// la columna artifacts del envelope (formato Parts <c>id|alg|hash;...</c>) más cualquier hash
/// hexadecimal que aparezca en el payload JSON (superaproximación deliberada: si el payload no
/// decodifica por corrupción, sus refs siguen vivas — nunca se borra algo recuperable).
/// También marca todas las revisiones de model_qualification_evidence en el user.db del
/// mismo directorio del CAS. Una referencia User inválida aborta todo el sweep.
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

    private readonly string _dataDirectory;
    private readonly string _blobsRoot;
    private readonly FileArtifactStore _artifactStore;

    public ArtifactGc(string artifactsDataDirectory)
    {
        _dataDirectory = Path.GetFullPath(artifactsDataDirectory);
        _blobsRoot = Path.Combine(_dataDirectory, "blobs");
        _artifactStore = new FileArtifactStore(_dataDirectory);
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
    /// Marca el conjunto vivo leyendo raíces User y un journal opcional (read-only), y barre el store. El reloj se
    /// recibe explícito (<paramref name="now"/>) para tests deterministas.
    /// </summary>
    public SweepResult Sweep(string? journalPath, TimeSpan grace, bool dryRun, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Exclusive OS lease prevents a concurrent CAS write from being swept between its
        // publication and journal commit. PutText refreshes the blob's mtime under the same
        // lease, so the grace period covers the post-publication commit window.
        using var lease = ArtifactStoreLease.Acquire(_dataDirectory, cancellationToken);
        var live = MarkLive(journalPath, cancellationToken);
        return SweepBlobs(live, grace, dryRun, now, cancellationToken);
    }

    /// <summary>Conjunto vivo: hashes del envelope + hashes que aparecen en el payload JSON.</summary>
    private HashSet<string> MarkLive(string? journalPath, CancellationToken cancellationToken)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedBlobs = new Queue<string>();
        MarkUserQualificationEvidence(live, referencedBlobs, cancellationToken);
        if (File.Exists(journalPath))
        {
            var journalConnectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = journalPath!,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(journalConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT artifacts, payload FROM events";
            using var reader = cmd.ExecuteReader();
            foreach (System.Data.Common.DbDataRecord row in reader)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var envelope = AsText(row.GetValue(0));
                if (envelope is not null && envelope.Length > 0)
                {
                    foreach (var part in SplitUnescaped(envelope, ';'))
                    {
                        if (part.Length == 0) continue;
                        var fields = SplitUnescaped(part, '|');
                        // Formatos respaldados por el writer: anterior (3 campos) y actual (8 campos).
                        if ((fields.Count == 3 || fields.Count == 8) && fields[1] == "sha256" && IsSha256Hex(fields[2]))
                        {
                            live.Add(fields[2]);
                            referencedBlobs.Enqueue(fields[2]);
                        }
                    }
                }

                var payload = AsText(row.GetValue(1));
                if (payload is not null)
                {
                    // Superaproximación: cualquier hash hexadecimal del payload cuenta como vivo.
                    foreach (Match match in HexHash.Matches(payload))
                        live.Add(match.Value);
                    try
                    {
                        using var json = System.Text.Json.JsonDocument.Parse(payload);
                        CollectContentHashes(json.RootElement, referencedBlobs);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Unparseable payload: retain every hex hash found above; never derive
                        // additional references from ambiguous text.
                    }
                }
            }
        }

        // Some canonical references are nested inside referenced artifacts (notably an
        // externalized output hash inside TurnStarted.ContextSnapshotRef). Follow the CAS
        // reference graph recursively, starting only from typed journal references.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (referencedBlobs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = referencedBlobs.Dequeue();
            if (!visited.Add(hash)) continue;
            live.Add(hash);
            var probe = _artifactStore.Probe(ContentHash.Sha256(hash));
            if (probe.Status != BlobStatus.Ok || probe.Content is null)
                throw new InvalidDataException("GC no puede completar el mark: artifact referenciado "
                    + hash + " no se puede leer (" + probe.Status + "). No se barre ningún blob.");
            foreach (Match match in HexHash.Matches(probe.Content))
                live.Add(match.Value); // conservative mark; only typed refs below extend traversal
            try
            {
                using var nestedJson = System.Text.Json.JsonDocument.Parse(probe.Content);
                CollectContentHashes(nestedJson.RootElement, referencedBlobs);
            }
            catch (System.Text.Json.JsonException) { }
        }

        return live;
    }

    private void MarkUserQualificationEvidence(HashSet<string> live, Queue<string> references,
        CancellationToken cancellationToken)
    {
        MarkUserQualificationRoots(live, references, cancellationToken,
            "model_qualification_evidence", SqliteModelQualificationStore.EvidenceSchemaMarker);
        MarkUserQualificationRoots(live, references, cancellationToken,
            "qualification_probe_receipts", SqliteModelQualificationStore.ProbeReceiptSchemaMarker);
    }

    private void MarkUserQualificationRoots(HashSet<string> live, Queue<string> references,
        CancellationToken cancellationToken, string tableName, string schemaMarker)
    {
        var database = Path.Combine(_dataDirectory, "user.db");
        if (!File.Exists(database)) return;
        if ((File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("GC no puede leer raíces User a través de un enlace.");
        try
        {
            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            connection.Open();
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "SELECT type FROM sqlite_master WHERE name = $table COLLATE NOCASE";
                schema.Parameters.AddWithValue("$table", tableName);
                var type = schema.ExecuteScalar();
                if (type is null)
                {
                    using var migrations = connection.CreateCommand();
                    migrations.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='model_profile_migrations'";
                    if (Convert.ToInt64(migrations.ExecuteScalar()) != 0)
                    {
                        migrations.CommandText = "SELECT COUNT(*) FROM model_profile_migrations WHERE name=$marker";
                        migrations.Parameters.AddWithValue("$marker", schemaMarker);
                        if (Convert.ToInt64(migrations.ExecuteScalar()) != 0)
                            throw new InvalidDataException("GC: falta la tabla de evidencia User instalada.");
                    }
                    return; // Truly legacy databases have no declared evidence capability.
                }
                if (!Equals(type, "table"))
                    throw new InvalidDataException("GC: el registro de evidencia User no es una tabla.");
            }
            using var command = connection.CreateCommand();
            // Historical revisions are roots too, not just the currently selected profile.
            // Table names are private constants above, never renderer/user input.
            command.CommandText = "SELECT artifact_algorithm, artifact_hash, artifact_size FROM " + tableName;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.GetValue(0) is not string algorithm || algorithm != "sha256"
                    || reader.GetValue(1) is not string hash || !IsSha256Hex(hash)
                    || reader.GetValue(2) is not long size || size < 0
                    || !_artifactStore.Verify(ContentHash.Sha256(hash), size))
                    throw new InvalidDataException("GC no puede completar el mark: referencia de cualificación User inválida. No se barre ningún blob.");
                live.Add(hash);
                references.Enqueue(hash);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            throw new InvalidDataException("GC no puede completar el mark de evidencia User. No se barre ningún blob.", exception);
        }
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

    private static void CollectContentHashes(System.Text.Json.JsonElement element, Queue<string> hashes,
        bool fingerprintComponent = false)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            string? algorithm = null;
            string? value = null;
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("algorithm", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    algorithm = property.Value.GetString();
                else if (property.Name.Equals("value", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    value = property.Value.GetString();
            }
            if (algorithm == "sha256" && value is not null && IsSha256Hex(value)) hashes.Enqueue(value);
            var isFingerprint = IsExecutionFingerprint(element);
            foreach (var property in element.EnumerateObject())
            {
                // A component Hash is a configuration digest, not a CAS reference. Its
                // optional Content carries the real reference and must still be traversed.
                if (fingerprintComponent && property.Name.Equals("hash", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (isFingerprint && property.Name.Equals("components", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var component in property.Value.EnumerateArray())
                        CollectContentHashes(component, hashes, fingerprintComponent: true);
                }
                else CollectContentHashes(property.Value, hashes);
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CollectContentHashes(item, hashes);
    }

    private static bool IsExecutionFingerprint(System.Text.Json.JsonElement element)
    {
        // Arbitrary tool/model JSON may also contain modelKey and components/hash.
        // Only exempt digests when the complete known fingerprint contract is present;
        // an ambiguous or malformed object keeps normal, fail-closed CAS traversal.
        foreach (var name in new[] { "modelKey", "harnessPolicyHash", "toolkitHash", "tokenizerHash",
                     "contextPolicyHash", "overridesHash", "build", "modelPolicyHash" })
            if (!TryUniqueProperty(element, name, out var value)
                || value.ValueKind != System.Text.Json.JsonValueKind.String) return false;
        if (!TryUniqueProperty(element, "components", out var components)
            || components.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != System.Text.Json.JsonValueKind.Object
                || !TryUniqueProperty(component, "name", out var name)
                || name.ValueKind != System.Text.Json.JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()) || !names.Add(name.GetString()!)
                || !TryUniqueProperty(component, "version", out var version)
                || version.ValueKind != System.Text.Json.JsonValueKind.String
                || !TryUniqueProperty(component, "hash", out var hash)
                || hash.ValueKind != System.Text.Json.JsonValueKind.Object
                || !TryUniqueProperty(hash, "algorithm", out var algorithm)
                || algorithm.ValueKind != System.Text.Json.JsonValueKind.String
                || algorithm.GetString() != "sha256"
                || !TryUniqueProperty(hash, "value", out var value)
                || value.ValueKind != System.Text.Json.JsonValueKind.String
                || value.GetString() is not { } digest || !IsSha256Hex(digest)) return false;
        }
        return true;
    }

    private static bool TryUniqueProperty(System.Text.Json.JsonElement element, string name,
        out System.Text.Json.JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) return false;
            value = property.Value;
            found = true;
        }
        return found;
    }

    private static bool IsSha256Hex(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? AsText(object? value) => value is null || value == DBNull.Value
        ? null
        : value.ToString();

    private static List<string> SplitUnescaped(string text, char delimiter)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(c);
                current.Append(text[++i]);
            }
            else if (c == delimiter)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }
}
