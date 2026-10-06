using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// CLI-level contracts for GC's User evidence roots. The private SQLite fixture uses only the
/// agreed minimum evidence columns; it does not claim to define the full production schema.
/// </summary>
public sealed class M5UserQualificationGcCliTests
{
    [Fact]
    public async Task User_scope_dry_run_counts_candidates_then_live_sweep_keeps_both_history_revisions()
    {
        using var fixture = new Fixture();
        var revisionOne = fixture.PutOldBlob("user-evidence-revision-one");
        var revisionTwo = fixture.PutOldBlob("user-evidence-revision-two");
        var orphanPath = fixture.PutOldOrphan("unreferenced-user-gc-orphan");
        fixture.CreateEvidenceTable();
        fixture.AddEvidence("profile-key-hash", 1, revisionOne);
        fixture.AddEvidence("profile-key-hash", 2, revisionTwo);

        var dryRunOutput = new StringWriter();
        var dryRunExitCode = await MaintenanceCommandHost.Gc(fixture.UserArgs(dryRun: true), dryRunOutput);

        Assert.Equal(0, dryRunExitCode);
        Assert.Contains("2 vivo(s) por referencia", dryRunOutput.ToString());
        Assert.Contains("1 candidato(s) a borrar (dry-run)", dryRunOutput.ToString());
        Assert.True(File.Exists(fixture.BlobPath(revisionOne.Hash.Value)));
        Assert.True(File.Exists(fixture.BlobPath(revisionTwo.Hash.Value)));
        Assert.True(File.Exists(orphanPath), "dry-run must not remove the old orphan");

        var liveOutput = new StringWriter();
        var liveExitCode = await MaintenanceCommandHost.Gc(fixture.UserArgs(dryRun: false), liveOutput);

        Assert.Equal(0, liveExitCode);
        Assert.True(File.Exists(fixture.BlobPath(revisionOne.Hash.Value)), "historical revision 1 was collected");
        Assert.True(File.Exists(fixture.BlobPath(revisionTwo.Hash.Value)), "current revision 2 was collected");
        Assert.False(File.Exists(orphanPath), "live sweep should remove the old orphan");
        Assert.Equal(2, fixture.CountEvidenceRows());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public async Task Invalid_user_root_returns_error_and_leaves_orphans_untouched(string invalidReference)
    {
        using var fixture = new Fixture();
        var invalidRoot = fixture.PutOldBlob("user-evidence-checked-by-cli");
        var orphanPath = fixture.PutOldOrphan("orphan-survives-user-mark-failure");
        fixture.CreateEvidenceTable();
        if (invalidReference == "missing")
        {
            File.Delete(fixture.BlobPath(invalidRoot.Hash.Value));
        }
        else
        {
            File.WriteAllText(fixture.BlobPath(invalidRoot.Hash.Value), new string('x', (int)invalidRoot.Size));
            fixture.SetOld(invalidRoot.Hash.Value);
        }
        fixture.AddEvidence("profile-key-hash", 1, invalidRoot);

        var output = new StringWriter();
        var exitCode = await MaintenanceCommandHost.Gc(fixture.UserArgs(dryRun: false), output);

        Assert.Equal(1, exitCode);
        Assert.Contains("error", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(orphanPath), "an invalid User root must abort before any deletion");
        if (invalidReference == "missing")
            Assert.False(File.Exists(fixture.BlobPath(invalidRoot.Hash.Value)));
        else
            Assert.Equal(new string('x', (int)invalidRoot.Size),
                File.ReadAllText(fixture.BlobPath(invalidRoot.Hash.Value)));
    }

    [Fact]
    public async Task User_scope_with_explicit_journal_is_rejected_without_sweeping()
    {
        using var fixture = new Fixture();
        var orphanPath = fixture.PutOldOrphan("scope-conflict-keeps-orphan");
        fixture.CreateEmptyJournal();
        var args = new[]
        {
            "gc", "--scope", "user", "--artifacts", fixture.DataDirectory,
            "--journal", fixture.EmptyJournalPath, "--grace-hours", "0",
        };

        var output = new StringWriter();
        var exitCode = await MaintenanceCommandHost.Gc(args, output);

        Assert.Equal(2, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(output.ToString()));
        Assert.True(File.Exists(orphanPath), "invalid scope combination must be rejected before sweeping");
    }

    [Fact]
    public async Task Invalid_scope_is_rejected_without_sweeping()
    {
        using var fixture = new Fixture();
        var orphanPath = fixture.PutOldOrphan("invalid-scope-keeps-orphan");
        var args = new[] { "gc", "--scope", "not-a-scope", "--artifacts", fixture.DataDirectory,
            "--grace-hours", "0" };

        var output = new StringWriter();
        var exitCode = await MaintenanceCommandHost.Gc(args, output);

        Assert.Equal(2, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(output.ToString()));
        Assert.True(File.Exists(orphanPath), "invalid scope must be rejected before sweeping");
    }

    [Fact]
    public async Task Default_workspace_scope_still_uses_the_explicit_journal_and_artifact_directory()
    {
        using var fixture = new Fixture();
        var workspaceRoot = fixture.PutOldBlob("workspace-journal-reference");
        var orphanPath = fixture.PutOldOrphan("workspace-scope-orphan");
        fixture.CreateWorkspaceJournalWithReference(workspaceRoot.Hash.Value);
        var args = new[]
        {
            "gc", "--journal", fixture.WorkspaceJournalPath,
            "--artifacts", fixture.DataDirectory, "--grace-hours", "0",
        };

        var output = new StringWriter();
        var exitCode = await MaintenanceCommandHost.Gc(args, output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(fixture.BlobPath(workspaceRoot.Hash.Value)),
            "legacy default scope must retain journal-referenced workspace blobs");
        Assert.False(File.Exists(orphanPath));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "omnicore-m5-user-gc-cli-" + Guid.NewGuid().ToString("N"));
            DataDirectory = Path.Combine(Root, "artifacts");
            Directory.CreateDirectory(DataDirectory);
            Artifacts = new FileArtifactStore(DataDirectory);
            EmptyJournalPath = Path.Combine(Root, "empty-journal.db");
            WorkspaceJournalPath = Path.Combine(Root, "workspace-journal.db");
        }

        public string Root { get; }
        public string DataDirectory { get; }
        public string EmptyJournalPath { get; }
        public string WorkspaceJournalPath { get; }
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public FileArtifactStore Artifacts { get; }

        public static DateTimeOffset Now { get; } =
            new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        public string[] UserArgs(bool dryRun)
        {
            var args = new List<string>
            {
                "gc", "--scope", "user", "--artifacts", DataDirectory, "--grace-hours", "0",
            };
            if (dryRun) args.Add("--dry-run");
            return args.ToArray();
        }

        public ArtifactRef PutOldBlob(string content)
        {
            var artifact = Artifacts.PutText(content, "text/plain", ArtifactKind.Other, Sensitivity.Normal);
            SetOld(artifact.Hash.Value);
            return artifact;
        }

        public string PutOldOrphan(string content) => BlobPath(PutOldBlob(content).Hash.Value);

        public string BlobPath(string hash) => Path.Combine(DataDirectory, "blobs", "sha256",
            hash[..2], hash.Substring(2, 2), hash);

        public void SetOld(string hash) =>
            File.SetLastWriteTimeUtc(BlobPath(hash), Now.UtcDateTime.AddDays(-2));

        public void CreateEvidenceTable()
        {
            using var connection = OpenUserDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE model_qualification_evidence (
                    key_hash TEXT NOT NULL,
                    profile_revision INTEGER NOT NULL,
                    artifact_algorithm TEXT NOT NULL,
                    artifact_hash TEXT NOT NULL,
                    artifact_size INTEGER NOT NULL,
                    PRIMARY KEY (key_hash, profile_revision)
                )
                """;
            command.ExecuteNonQuery();
        }

        public void AddEvidence(string keyHash, long revision, ArtifactRef artifact)
        {
            using var connection = OpenUserDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO model_qualification_evidence
                    (key_hash, profile_revision, artifact_algorithm, artifact_hash, artifact_size)
                VALUES ($keyHash, $revision, $algorithm, $hash, $size)
                """;
            command.Parameters.AddWithValue("$keyHash", keyHash);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$algorithm", artifact.Hash.Algorithm);
            command.Parameters.AddWithValue("$hash", artifact.Hash.Value);
            command.Parameters.AddWithValue("$size", artifact.Size);
            command.ExecuteNonQuery();
        }

        public int CountEvidenceRows()
        {
            using var connection = OpenUserDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM model_qualification_evidence";
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void CreateEmptyJournal() => CreateJournal(EmptyJournalPath, payload: null);

        public void CreateWorkspaceJournalWithReference(string hash) =>
            CreateJournal(WorkspaceJournalPath, payload: "workspace references sha256 " + hash);

        private static void CreateJournal(string path, string? payload)
        {
            using var connection = new SqliteConnection("DataSource=" + path);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE events (artifacts TEXT, payload TEXT);
                INSERT INTO events (artifacts, payload) VALUES ('', $payload);
                """;
            command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        private SqliteConnection OpenUserDatabase()
        {
            var connection = new SqliteConnection("DataSource=" + UserDatabasePath);
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            ClearPool(UserDatabasePath);
            ClearPool(EmptyJournalPath);
            ClearPool(WorkspaceJournalPath);
            Directory.Delete(Root, recursive: true);
        }

        private static void ClearPool(string path)
        {
            if (!File.Exists(path)) return;
            using var connection = new SqliteConnection("DataSource=" + path);
            SqliteConnection.ClearPool(connection);
        }
    }
}
