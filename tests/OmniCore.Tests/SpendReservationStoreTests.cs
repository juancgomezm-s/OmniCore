namespace OmniCore.Tests;

using OmniCore.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Task = System.Threading.Tasks.Task;

public sealed class SpendReservationStoreTests
{
    [Theory]
    [InlineData("reserved", true)]
    [InlineData("dispatched", true)]
    [InlineData("uncertain", true)]
    [InlineData("settled", false)]
    [InlineData("released", false)]
    public void Migration_of_ledger_without_pending_column_preserves_unknown_exposure(string state, bool holdsBound)
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "reservations.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE spend_reservations (id TEXT PRIMARY KEY,maximum_usd TEXT NOT NULL,state TEXT NOT NULL,
                        actual_usd TEXT,receipt TEXT,created_utc TEXT NOT NULL);
                    CREATE TABLE spend_reservation_scopes (id TEXT NOT NULL,scope TEXT NOT NULL,identity TEXT NOT NULL,
                        PRIMARY KEY(id,scope,identity));
                    INSERT INTO spend_reservations VALUES ('old','0.401',$state,NULL,NULL,'2026-10-06T23:59:00Z');
                    INSERT INTO spend_reservation_scopes VALUES ('old','daily','user');
                    """;
                command.Parameters.AddWithValue("$state", state);
                command.ExecuteNonQuery();
            }
            var migrated = new SqliteSpendReservationStore(path);
            var admission = migrated.TryReserve("after-migration", 0.401m, () => [new("daily", "user", 0.5m, 0m)]);
            Assert.Equal(holdsBound ? SqliteSpendReservationStore.Admission.Insufficient
                : SqliteSpendReservationStore.Admission.Reserved, admission);
            Assert.Equal(SqliteSpendReservationStore.Admission.Insufficient,
                new SqliteSpendReservationStore(path).TryReserve("after-reopen", 0.401m,
                    () => [new("daily", "user", 0.5m, 0m)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Separate_processes_share_one_atomic_reservation_limit()
    {
        const string childRootVariable = "OMNICORE_RESERVATION_TEST_ROOT";
        const string childIdVariable = "OMNICORE_RESERVATION_TEST_ID";
        var childRoot = Environment.GetEnvironmentVariable(childRootVariable);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        if (childRoot is not null)
        {
            var id = Environment.GetEnvironmentVariable(childIdVariable)!;
            var store = new SqliteSpendReservationStore(Path.Combine(childRoot, "reservations.db"));
            File.WriteAllText(Path.Combine(childRoot, id + ".ready"), Environment.ProcessId.ToString());
            while (!File.Exists(Path.Combine(childRoot, "go"))) await Task.Delay(10, deadline.Token);
            var result = store.TryReserve(id, 0.401m, () => [new("daily", "user", 0.5m, 0m)]);
            File.WriteAllText(Path.Combine(childRoot, id + ".result"), result.ToString());
            return;
        }
        var root = Root();
        var children = new List<Process>();
        try
        {
            _ = new SqliteSpendReservationStore(Path.Combine(root, "reservations.db"));
            for (var index = 0; index < 2; index++)
            {
                var start = new ProcessStartInfo("dotnet")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(typeof(SpendReservationStoreTests).Assembly.Location);
                start.ArgumentList.Add("-method");
                start.ArgumentList.Add("*Separate_processes_share_one_atomic_reservation_limit");
                start.ArgumentList.Add("-noColor");
                start.Environment[childRootVariable] = root;
                start.Environment[childIdVariable] = "child-" + index;
                children.Add(Process.Start(start)!);
            }
            while (!File.Exists(Path.Combine(root, "child-0.ready")) || !File.Exists(Path.Combine(root, "child-1.ready")))
            {
                Assert.All(children, child => Assert.False(child.HasExited, "Reservation fixture exited before its ready marker"));
                await Task.Delay(10, deadline.Token);
            }
            File.WriteAllText(Path.Combine(root, "go"), "release both owned fixture processes");
            foreach (var child in children)
            {
                await child.WaitForExitAsync(deadline.Token);
                var output = await child.StandardOutput.ReadToEndAsync(deadline.Token)
                    + await child.StandardError.ReadToEndAsync(deadline.Token);
                Assert.True(child.ExitCode == 0, output);
            }
            var results = Enumerable.Range(0, 2).Select(index => File.ReadAllText(Path.Combine(root, "child-" + index + ".result"))).ToArray();
            Assert.Single(results, result => result == "Reserved");
            Assert.Single(results, result => result == "Insufficient");
            Assert.NotEqual(children[0].Id, children[1].Id);
            Assert.All(children, child => Assert.NotEqual(Environment.ProcessId, child.Id));
        }
        finally
        {
            foreach (var child in children)
            {
                // These exact handles were launched by this fixture, not discovered by PID.
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(TestContext.Current.CancellationToken);
                child.Dispose();
            }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Separate_connections_atomically_reserve_one_shared_allowance()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "reservations.db");
            var stores = new[] { new SqliteSpendReservationStore(path), new SqliteSpendReservationStore(path) };
            var results = await Task.WhenAll(stores.Select((store, index) => Task.Run(() =>
                store.TryReserve("invocation-" + index, 0.401m, () => [new("daily", "user", 0.50m, 0m)]),
                TestContext.Current.CancellationToken)));
            Assert.Single(results, result => result == SqliteSpendReservationStore.Admission.Reserved);
            Assert.Single(results, result => result == SqliteSpendReservationStore.Admission.Insufficient);
            Assert.Equal(SqliteSpendReservationStore.Admission.Insufficient,
                new SqliteSpendReservationStore(path).TryReserve("after-reopen", 0.1m,
                    () => [new("daily", "user", 0.50m, 0m)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Dispatched_unknown_outcome_stays_held_and_cannot_be_released()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "reservations.db");
            var store = new SqliteSpendReservationStore(path);
            Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
                store.TryReserve("a", 0.4m, () => [new("daily", "user", 0.5m, 0m)]));
            store.MarkDispatched("a");
            store.MarkDispatched("a");
            store = new SqliteSpendReservationStore(path);
            Assert.Throws<InvalidOperationException>(() => store.ReleaseBeforeDispatch("a"));
            Assert.Equal(SqliteSpendReservationStore.Admission.Insufficient,
                store.TryReserve("b", 0.2m, () => [new("daily", "user", 0.5m, 0m)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Settlement_is_idempotent_and_does_not_double_count_canonical_spend()
    {
        var root = Root();
        try
        {
            var store = new SqliteSpendReservationStore(Path.Combine(root, "reservations.db"));
            store.TryReserve("a", 0.4m, () => [new("daily", "user", 0.5m, 0m)]);
            store.MarkDispatched("a");
            store.Settle("a", 0.3m, "canonical-receipt-a");
            store.Settle("a", 0.30m, "canonical-receipt-a");
            Assert.Throws<InvalidDataException>(() => store.Settle("a", 0.2m, "canonical-receipt-a"));
            Assert.Equal(SqliteSpendReservationStore.Admission.AlreadyExists,
                store.TryReserve("a", 0.4m, () => throw new Exception("Must not authorize replay")));
            Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
                store.TryReserve("b", 0.2m, () => [new("daily", "user", 0.5m, 0.3m)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("run")]
    [InlineData("session")]
    [InlineData("daily")]
    public void Every_scope_is_checked_and_failed_admission_leaves_no_partial_reservation(string blocked)
    {
        var root = Root();
        try
        {
            var store = new SqliteSpendReservationStore(Path.Combine(root, "reservations.db"));
            Assert.Equal(SqliteSpendReservationStore.Admission.Insufficient,
                store.TryReserve("a", 0.2m, () => new[] { "run", "session", "daily" }
                    .Select(scope => new SqliteSpendReservationStore.Limit(scope, "identity", scope == blocked ? 0.1m : 1m, 0m)).ToArray()));
            Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
                store.TryReserve("a", 0.2m, () => [new("daily", "identity", 1m, 0m)]));
            store.ReleaseBeforeDispatch("a");
            store.ReleaseBeforeDispatch("a");
            Assert.Throws<InvalidOperationException>(() => store.MarkDispatched("a"));
            Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
                store.TryReserve("b", 1m, () => [new("daily", "identity", 1m, 0m)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Evidence_failure_rolls_back_and_never_becomes_zero_spend()
    {
        var root = Root();
        try
        {
            var store = new SqliteSpendReservationStore(Path.Combine(root, "reservations.db"));
            Assert.Throws<InvalidDataException>(() => store.TryReserve("a", 0.2m,
                () => throw new InvalidDataException("Canonical evidence unavailable")));
            Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
                store.TryReserve("a", 0.2m, () => [new("daily", "user", 0.2m, 0m)]));
            Assert.Throws<InvalidDataException>(() => store.TryReserve("a", 0.1m,
                () => [new("daily", "user", 0.2m, 0m)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Decimal_boundary_is_exact_without_sqlite_floating_point_rounding()
    {
        var root = Root();
        try
        {
            var store = new SqliteSpendReservationStore(Path.Combine(root, "reservations.db"));
            const decimal amount = 9007199254740992.000000001m;
            store.TryReserve("a", amount, () => [new("daily", "user", amount, 0m)]);
            Assert.Equal(SqliteSpendReservationStore.Admission.Insufficient,
                store.TryReserve("b", 0.000000001m, () => [new("daily", "user", amount, 0m)]));
            Assert.Throws<OverflowException>(() => store.TryReserve("overflow", decimal.MaxValue,
                () => [new("daily", "user", decimal.MaxValue, decimal.MaxValue)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Unknown_persisted_state_cannot_disappear_from_pending_spend()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "reservations.db");
            var store = new SqliteSpendReservationStore(path);
            store.TryReserve("a", 0.4m, () => [new("daily", "user", 0.5m, 0m)]);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE spend_reservations SET state='unsupported-state' WHERE id='a'";
                command.ExecuteNonQuery();
            }
            Assert.Throws<InvalidDataException>(() => store.TryReserve("b", 0.2m,
                () => [new("daily", "user", 0.5m, 0m)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Uncertain_retry_keeps_unaccounted_bound_without_double_counting_known_receipt()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "reservations.db");
            var store = new SqliteSpendReservationStore(path);
            store.TryReserve("a", 1.203m, () => [new("daily", "user", 2m, 0m)]);
            store.MarkDispatched("a");
            store.RecordUncertainCompletion("a", 0.3m, "canonical-receipt-a");
            store.RecordUncertainCompletion("a", 0.30m, "canonical-receipt-a");
            store = new SqliteSpendReservationStore(path);
            Assert.Equal(SqliteSpendReservationStore.Admission.Insufficient,
                store.TryReserve("b", 0.1m, () => [new("daily", "user", 1.3m, 0.3m)]));
            Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
                store.TryReserve("c", 0.09m, () => [new("daily", "user", 1.3m, 0.3m)]));
            Assert.Throws<InvalidOperationException>(() => store.ReleaseBeforeDispatch("a"));
            Assert.Throws<InvalidOperationException>(() => store.Settle("a", 0.3m, "canonical-receipt-a"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Root()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-reservation-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
