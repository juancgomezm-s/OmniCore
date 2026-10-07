using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>Real private SQLite/CAS fixtures. No provider queries, invoices or real spend.
/// This foundation does not prove Host daily admission is wired.</summary>
public sealed class QualificationProbeReceiptStoreTests
{
    [Fact]
    public void Full_receipt_survives_reopen_and_exact_repetition_is_idempotent_without_profile()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Receipt();
        using (var store = fixture.Open())
        {
            store.RecordProbeReceipt(receipt, CancellationToken.None);
            store.RecordProbeReceipt(receipt, CancellationToken.None);
            Assert.Empty(store.List(CancellationToken.None));
        }
        using var reopened = fixture.Open();
        Assert.Equal(receipt, Assert.Single(reopened.ProbeReceipts(CancellationToken.None)));
        Assert.Equal(receipt, Assert.Single(reopened.ProbeReceipts(CancellationToken.None)));
        var connection = Assert.IsAssignableFrom<System.Data.Common.DbConnection>(typeof(SqliteModelQualificationStore)
            .GetField("_conn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reopened));
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous";
        Assert.Equal(2L, Convert.ToInt64(pragma.ExecuteScalar()));
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("reservation")]
    [InlineData("usage")]
    public void Conflicting_repetition_never_overwrites_original(string change)
    {
        using var fixture = new Fixture();
        var receipt = fixture.Receipt();
        using var store = fixture.Open();
        store.RecordProbeReceipt(receipt, CancellationToken.None);
        var conflicting = fixture.Publish(change switch
        {
            "amount" => receipt with { CostUsd = 0.02m },
            "reservation" => receipt with { ReservationId = "another-reservation" },
            _ => receipt with { Usage = receipt.Usage! with { Input = 18 } },
        });
        Assert.Throws<InvalidDataException>(() => store.RecordProbeReceipt(conflicting, CancellationToken.None));
        Assert.Equal(receipt, Assert.Single(store.ProbeReceipts(CancellationToken.None)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unknown_cost_and_usage_remain_unknown_for_interrupted_probe(bool hasUsage)
    {
        using var fixture = new Fixture();
        var receipt = fixture.Receipt() with
        {
            Status = ProbeStatus.Error, Termination = ProbeExecutionTermination.Cancelled, CostUsd = null,
            Usage = hasUsage ? new TokenUsage(17, 999, 888, 777, 666) : null,
            ReportedUsageFields = hasUsage ? TokenUsageFields.Input : TokenUsageFields.None,
        };
        receipt = fixture.Publish(receipt);
        using (var store = fixture.Open()) store.RecordProbeReceipt(receipt, CancellationToken.None);
        using var reopened = fixture.Open();
        var actual = Assert.Single(reopened.ProbeReceipts(CancellationToken.None));
        Assert.Null(actual.CostUsd);
        Assert.Equal(receipt.ReportedUsageFields, actual.ReportedUsageFields);
        Assert.Equal(hasUsage ? new TokenUsage(17, 0, 0, 0, 0) : null, actual.Usage);
        Assert.Equal(hasUsage ? 17L : null, fixture.NullableScalar("SELECT input_tokens FROM qualification_probe_receipts"));
        Assert.Null(fixture.NullableScalar("SELECT output_tokens FROM qualification_probe_receipts"));
    }

    [Fact]
    public void Offset_timestamps_normalize_to_utc_without_changing_probe_day_or_identity()
    {
        using var fixture = new Fixture();
        var local = fixture.Receipt() with
        {
            StartedAtUtc = new(2026, 10, 6, 17, 59, 59, TimeSpan.FromHours(-6)),
            CompletedAtUtc = new(2026, 10, 6, 18, 0, 1, TimeSpan.FromHours(-6)),
        };
        using var store = fixture.Open();
        local = fixture.Publish(local);
        store.RecordProbeReceipt(local, CancellationToken.None);
        var actual = Assert.Single(store.ProbeReceipts(CancellationToken.None));
        Assert.Equal(local.StartedAtUtc.ToUniversalTime(), actual.StartedAtUtc);
        Assert.Equal(local.CompletedAtUtc.ToUniversalTime(), actual.CompletedAtUtc);
        Assert.Equal(TimeSpan.Zero, actual.CompletedAtUtc.Offset);
        Assert.Equal(7, actual.CompletedAtUtc.Day);
    }

    [Fact]
    public void Trigger_failure_rolls_back_receipt_but_preserves_other_receipts_and_profiles()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var first = fixture.Receipt();
        store.RecordProbeReceipt(first, CancellationToken.None);
        fixture.Exec("CREATE TRIGGER reject_receipt BEFORE INSERT ON qualification_probe_receipts BEGIN SELECT RAISE(ABORT,'fixture failure'); END");
        Assert.Throws<SqliteException>(() => store.RecordProbeReceipt(
            fixture.Publish(fixture.Receipt() with { ProbeId = "second", Ordinal = 1 }), CancellationToken.None));
        Assert.Equal(first, Assert.Single(store.ProbeReceipts(CancellationToken.None)));
        Assert.Empty(store.List(CancellationToken.None));
    }

    [Theory]
    [InlineData("cost-without-usage")]
    [InlineData("partial-cost")]
    [InlineData("invalid-usage")]
    [InlineData("negative-unreported-usage")]
    [InlineData("not-run")]
    [InlineData("unknown-fields")]
    public void Invalid_measurement_is_rejected_before_insert(string invalid)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var receipt = fixture.Receipt();
        var malformed = invalid switch
        {
            "cost-without-usage" => receipt with { Usage = null, ReportedUsageFields = TokenUsageFields.None },
            "partial-cost" => receipt with { ReportedUsageFields = TokenUsageFields.Input },
            "invalid-usage" => receipt with { Usage = receipt.Usage! with { Input = -1 } },
            "negative-unreported-usage" => receipt with { Usage = receipt.Usage! with { CacheRead = -1 } },
            "not-run" => receipt with { Status = ProbeStatus.NotRun },
            _ => receipt with { ReportedUsageFields = (TokenUsageFields)64 },
        };
        Assert.Throws<InvalidDataException>(() => store.RecordProbeReceipt(malformed, CancellationToken.None));
        Assert.Empty(store.ProbeReceipts(CancellationToken.None));
    }

    [Fact]
    public void Reader_rejects_missing_reported_count_instead_of_turning_it_into_zero()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        store.RecordProbeReceipt(fixture.Receipt(), CancellationToken.None);
        fixture.Exec("UPDATE qualification_probe_receipts SET input_tokens=NULL");
        Assert.Throws<InvalidDataException>(() => store.ProbeReceipts(CancellationToken.None));
    }

    [Theory]
    [InlineData("usage_fields=4294967299")]
    [InlineData("has_usage=2")]
    [InlineData("redacted=2")]
    [InlineData("status='0'")]
    [InlineData("status='Passed,Failed'")]
    [InlineData("billing_mode='999'")]
    public void Malformed_typed_fields_fail_closed_on_read(string update)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        store.RecordProbeReceipt(fixture.Receipt(), CancellationToken.None);
        fixture.Exec("UPDATE qualification_probe_receipts SET " + update);
        Assert.Throws<InvalidDataException>(() => store.ProbeReceipts(CancellationToken.None));
    }

    [Fact]
    public void Missing_cas_evidence_rejects_insert_and_does_not_create_profile()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Receipt();
        File.Delete(fixture.Blob(receipt.Evidence));
        using var store = fixture.Open();
        Assert.Throws<InvalidDataException>(() => store.RecordProbeReceipt(receipt, CancellationToken.None));
        Assert.Empty(store.ProbeReceipts(CancellationToken.None));
        Assert.Empty(store.List(CancellationToken.None));
    }

    [Fact]
    public void Invalid_reported_usage_is_rejected_even_when_cost_is_unknown()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        Assert.Throws<InvalidDataException>(() => store.RecordProbeReceipt(fixture.Receipt() with
            { CostUsd = null, Usage = new TokenUsage(-1, 4, 0, 0, 0) }, CancellationToken.None));
        Assert.Empty(store.ProbeReceipts(CancellationToken.None));
    }

    [Fact]
    public void View_cannot_install_receipt_schema_or_migration_marker()
    {
        using var fixture = new Fixture();
        using (fixture.Open()) { }
        fixture.Exec("ALTER TABLE qualification_probe_receipts RENAME TO receipt_template; DELETE FROM model_profile_migrations WHERE name='m55-qualification-probe-receipts-v1'; CREATE VIEW qualification_probe_receipts AS SELECT * FROM receipt_template");
        var error = Record.Exception(() => { using var attempted = fixture.Open(); });
        Assert.IsType<InvalidDataException>(error);
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM model_profile_migrations WHERE name='m55-qualification-probe-receipts-v1'"));
    }

    [Fact]
    public void Missing_installed_table_fails_closed_for_store_and_gc_before_sweep()
    {
        using var fixture = new Fixture();
        using (var store = fixture.Open()) store.RecordProbeReceipt(fixture.Receipt(), CancellationToken.None);
        var orphan = fixture.Put("orphan must survive schema corruption");
        fixture.Exec("DROP TABLE qualification_probe_receipts");
        Assert.Throws<InvalidDataException>(() => fixture.Open());
        Assert.Throws<InvalidDataException>(() => fixture.Sweep());
        Assert.True(File.Exists(fixture.Blob(orphan)));
    }

    [Fact]
    public void Legacy_database_migrates_without_inventing_receipts_or_changing_profile_schema()
    {
        using var fixture = new Fixture();
        using (fixture.Open()) { }
        fixture.Exec("DROP TABLE qualification_probe_receipts; DELETE FROM model_profile_migrations WHERE name='m55-qualification-probe-receipts-v1'");
        using var migrated = fixture.Open();
        Assert.Empty(migrated.ProbeReceipts(CancellationToken.None));
        Assert.Empty(migrated.List(CancellationToken.None));
    }

    [Fact]
    public void Gc_roots_partial_receipt_without_profile_and_follows_transitive_refs()
    {
        using var fixture = new Fixture();
        var child = fixture.Put("receipt child artifact");
        var evidence = fixture.Put("{\"child\":{\"algorithm\":\"sha256\",\"value\":\"" + child.Hash.Value + "\"}}");
        var receipt = fixture.Publish(fixture.Observation() with
            { CostUsd = null, Usage = null, ReportedUsageFields = TokenUsageFields.None }, evidence);
        using (var store = fixture.Open()) store.RecordProbeReceipt(receipt, CancellationToken.None);
        var orphan = fixture.Put("old unreferenced receipt orphan");
        var sweep = fixture.Sweep();
        Assert.Equal(1, sweep.Deleted);
        Assert.True(File.Exists(fixture.Blob(evidence)));
        Assert.True(File.Exists(fixture.Blob(receipt.Evidence)));
        Assert.True(File.Exists(fixture.Blob(child)));
        Assert.False(File.Exists(fixture.Blob(orphan)));
    }

    [Fact]
    public void Corrupt_receipt_evidence_aborts_reads_and_gc_before_deleting_any_orphan()
    {
        using var fixture = new Fixture();
        var receipt = fixture.Receipt();
        using var store = fixture.Open();
        store.RecordProbeReceipt(receipt, CancellationToken.None);
        var orphan = fixture.Put("orphan preserved on invalid evidence");
        File.WriteAllText(fixture.Blob(receipt.Evidence), "corrupt evidence");
        Assert.Throws<InvalidDataException>(() => store.ProbeReceipts(CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => fixture.Sweep());
        Assert.True(File.Exists(fixture.Blob(orphan)));
    }

    [Fact]
    public void Wrong_database_namespace_rejects_receipts()
    {
        using var fixture = new Fixture();
        using var store = new SqliteModelQualificationStore(Path.Combine(fixture.Root, "workspace.db"));
        Assert.Throws<InvalidOperationException>(() => store.RecordProbeReceipt(fixture.Receipt(), CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => store.ProbeReceipts(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reservation_and_probe_ordinal_cannot_be_reused_by_another_receipt(bool sameReservation)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var first = fixture.Receipt();
        store.RecordProbeReceipt(first, CancellationToken.None);
        var other = sameReservation
            ? first with { ExecutionId = Guid.NewGuid() }
            : first with { ProbeId = "second", ReservationId = "other-reservation" };
        other = fixture.Publish(other);
        Assert.Throws<SqliteException>(() => store.RecordProbeReceipt(other, CancellationToken.None));
        Assert.Equal(first, Assert.Single(store.ProbeReceipts(CancellationToken.None)));
    }

    [Theory]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("0.0600000000000000000000000001")]
    public void Decimal_cost_is_exact_and_actual_over_reserved_bound_is_not_discarded(string amount)
    {
        using var fixture = new Fixture();
        var receipt = fixture.Publish(fixture.Receipt() with { CostUsd = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture) });
        using (var store = fixture.Open()) store.RecordProbeReceipt(receipt, CancellationToken.None);
        using var reopened = fixture.Open();
        Assert.Equal(receipt.CostUsd, Assert.Single(reopened.ProbeReceipts(CancellationToken.None)).CostUsd);
    }

    [Fact]
    public void Relational_cost_tampering_cannot_override_verified_cas_observation()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var receipt = fixture.Receipt();
        store.RecordProbeReceipt(receipt, CancellationToken.None);
        fixture.Exec("UPDATE qualification_probe_receipts SET cost_usd='0'");
        Assert.Throws<InvalidDataException>(() => store.ProbeReceipts(CancellationToken.None));
        Assert.True(new FileArtifactStore(fixture.Root).Verify(receipt.Evidence.Hash, receipt.Evidence.Size));
    }

    [Fact]
    public void Matching_blob_hash_without_matching_numeric_header_cannot_create_receipt()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        var receipt = fixture.Receipt();
        Assert.Throws<InvalidDataException>(() => store.RecordProbeReceipt(receipt with { CostUsd = 0m }, CancellationToken.None));
        Assert.Empty(store.ProbeReceipts(CancellationToken.None));
    }

    [Fact]
    public void Response_text_is_redacted_in_cas_without_changing_numeric_receipt_header()
    {
        using var fixture = new Fixture();
        const string secret = "fixture-secret-value-not-a-credential";
        var receipt = fixture.Publish(fixture.Observation(), output: "Authorization: Bearer " + secret);
        using var store = fixture.Open();
        store.RecordProbeReceipt(receipt, CancellationToken.None);
        Assert.True(receipt.Evidence.Redacted);
        Assert.DoesNotContain(secret, new FileArtifactStore(fixture.Root).GetText(receipt.Evidence.Hash)!);
        Assert.Equal(receipt, Assert.Single(store.ProbeReceipts(CancellationToken.None)));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-probe-receipts-" + Guid.NewGuid().ToString("N"));
        private string Database => Path.Combine(Root, "user.db");
        private readonly Guid _execution = Guid.NewGuid();
        public Fixture() => Directory.CreateDirectory(Root);
        public SqliteModelQualificationStore Open() => new(Database);
        public ArtifactRef Put(string text) => new FileArtifactStore(Root).PutText(text, "application/json", ArtifactKind.Other, Sensitivity.Normal);
        public string Blob(ArtifactRef artifact) => Path.Combine(Root, "blobs", "sha256", artifact.Hash.Value[..2], artifact.Hash.Value.Substring(2, 2), artifact.Hash.Value);
        public QualificationProbeReceipt Receipt() => Publish(Observation());
        public QualificationProbeReceipt Observation() => new(_execution, "reading", 0, "qualification/" + _execution + "/reading",
            new string('a', 64), "quick", "1.0", new string('b', 64),
            new DateTimeOffset(2026, 10, 6, 23, 59, 58, TimeSpan.Zero), new DateTimeOffset(2026, 10, 6, 23, 59, 59, TimeSpan.Zero),
            ProbeStatus.Passed, ProbeExecutionTermination.Completed, BillingMode.MeteredCurrency, 0.05m, 3, 1,
            0.000066m, new TokenUsage(17, 4, 0, 0, 0), TokenUsageFields.Input | TokenUsageFields.Output, null!);
        public QualificationProbeReceipt Publish(QualificationProbeReceipt receipt, ArtifactRef? child = null, string output = "fixture")
        {
            var references = child is null ? "" : ",\"child\":{\"algorithm\":\"sha256\",\"value\":\"" + child.Hash.Value + "\"}";
            return receipt with { Evidence = Put("{\"schema\":\"" + QualificationProbeReceipt.EvidenceSchema + "\",\"receipt\":"
                + receipt.CanonicalObservationJson() + references + ",\"output\":" + System.Text.Json.JsonSerializer.Serialize(output) + "}") };
        }
        public void Exec(string sql) { using var c = Connect(); using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public long Scalar(string sql) { using var c = Connect(); using var command = c.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public long? NullableScalar(string sql) { using var c = Connect(); using var command = c.CreateCommand(); command.CommandText = sql; var value = command.ExecuteScalar(); return value is DBNull or null ? null : Convert.ToInt64(value); }
        private SqliteConnection Connect() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); c.Open(); return c; }
        public ArtifactGc.SweepResult Sweep() => new ArtifactGc(Root).Sweep(null, TimeSpan.Zero, false,
            DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
