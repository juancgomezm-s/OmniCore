namespace OmniCore.Tests;

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Memory;

public sealed class ScopedMemoryCrashRecoveryTests
{
    [Theory]
    [InlineData("global", "prepared")]
    [InlineData("global", "database")]
    [InlineData("global", "receipt")]
    [InlineData("global", "completed")]
    [InlineData("workspace", "prepared")]
    [InlineData("workspace", "database")]
    [InlineData("project", "prepared")]
    [InlineData("project", "database")]
    public async System.Threading.Tasks.Task Killed_promotion_reconciles_once_from_its_durable_boundary(string scopeName, string cut)
    {
        using var fixture = new CrashFixture(scopeName);
        using var child = fixture.Start("promote", cut);
        await child.Ready(cut);
        await child.Kill();
        var before = fixture.Memories.Read(fixture.Scope, CancellationToken.None);
        Assert.Equal(cut == "prepared" ? 0 : 1, before.Count);
        using var journal = fixture.OpenJournal();
        var policy = fixture.Policy(journal);
        var results = policy.Reconcile(TestContext.Current.CancellationToken);
        Assert.Equal(cut == "completed" ? 0 : 1, results.Count);
        Assert.Empty(policy.Reconcile(TestContext.Current.CancellationToken));
        Assert.Single(fixture.Memories.Read(fixture.Scope, CancellationToken.None));
        var events = fixture.Read(journal);
        Assert.Single(events.OfType<ScopedMemoryMutationPrepared>());
        Assert.Single(events.OfType<MemoryPromoted>());
        Assert.Single(events.OfType<ScopedMemoryMutationCompleted>());
        Assert.Single(events.OfType<InteractionResolved>(), item => item.Cause == InteractionCause.User && item.OptionId == "promote");
        fixture.AssertIntegrityAndLedger(1);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("database")]
    [InlineData("receipt")]
    [InlineData("completed")]
    public async System.Threading.Tasks.Task Killed_forget_finishes_without_reintroducing_the_previous_promotion(string cut)
    {
        using var fixture = new CrashFixture("global");
        using var child = fixture.Start("forget", cut);
        await child.Ready(cut);
        await child.Kill();
        Assert.Equal(cut == "prepared" ? 1 : 0, fixture.Memories.Read(fixture.Scope, CancellationToken.None).Count);
        using var journal = fixture.OpenJournal();
        var policy = fixture.Policy(journal);
        policy.Reconcile(TestContext.Current.CancellationToken);
        Assert.Empty(policy.Reconcile(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Memories.Read(fixture.Scope, CancellationToken.None));
        var events = fixture.Read(journal);
        Assert.Equal(2, events.OfType<ScopedMemoryMutationPrepared>().Count());
        Assert.Equal(2, events.OfType<ScopedMemoryMutationCompleted>().Count());
        Assert.Single(events.OfType<MemoryForgotten>());
        fixture.AssertIntegrityAndLedger(2);
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("approved")]
    public async System.Threading.Tasks.Task Killing_before_a_prepared_intent_cannot_create_memory_during_recovery(string cut)
    {
        using var fixture = new CrashFixture("global");
        using var child = fixture.Start("promote", cut);
        await child.Ready(cut);
        await child.Kill();
        using var journal = fixture.OpenJournal();
        var policy = fixture.Policy(journal);
        Assert.Empty(policy.Reconcile(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Memories.Read(fixture.Scope, CancellationToken.None));
        var events = fixture.Read(journal);
        Assert.DoesNotContain(events, item => item is ScopedMemoryMutationPrepared or MemoryPromoted);
        if (cut == "approved")
        {
            var firstAsk = events.OfType<InteractionRequested>().Single();
            var reproposed = policy.Propose(fixture.Scope, "Use the copper checklist before a release.", MemoryKind.Convention, TestContext.Current.CancellationToken);
            Assert.Equal("ask", reproposed.Decision);
            var nextAsk = fixture.Read(journal).OfType<InteractionRequested>().Last();
            Assert.NotEqual(firstAsk.InteractionId, nextAsk.InteractionId);
            Assert.Equal("reject", nextAsk.DefaultOptionId);
            Assert.Empty(fixture.Memories.Read(fixture.Scope, CancellationToken.None));
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Project_recovery_waits_for_current_workspace_trust_after_the_writer_was_killed()
    {
        using var fixture = new CrashFixture("project");
        using var child = fixture.Start("promote", "prepared");
        await child.Ready("prepared");
        await child.Kill();
        fixture.Trust(false);
        using var journal = fixture.OpenJournal();
        var policy = fixture.Policy(journal);
        Assert.Empty(policy.Reconcile(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Memories.Read(fixture.Scope, CancellationToken.None));
        Assert.DoesNotContain(fixture.Read(journal), item => item is ScopedMemoryMutationCompleted);
        fixture.Trust(true);
        Assert.Single(policy.Reconcile(TestContext.Current.CancellationToken));
        Assert.Single(fixture.Memories.Read(fixture.Scope, CancellationToken.None));
        fixture.AssertIntegrityAndLedger(1);
    }

    [Fact]
    public async System.Threading.Tasks.Task Another_Session_can_forget_after_a_crash_without_recovery_resurrecting_the_record()
    {
        using var fixture = new CrashFixture("global");
        using var child = fixture.Start("promote", "database");
        await child.Ready("database");
        await child.Kill();
        var record = Assert.Single(fixture.Memories.Read(fixture.Scope, CancellationToken.None));
        using (var otherJournal = SqliteEventStore.OpenUnpooled(Path.Combine(fixture.Root, "other-journal.db")))
        {
            var other = fixture.Policy(otherJournal, SessionId.New());
            Assert.True(other.Forget(fixture.Scope, record.Id, TestContext.Current.CancellationToken));
        }
        using var originalJournal = fixture.OpenJournal();
        var original = fixture.Policy(originalJournal);
        Assert.Single(original.Reconcile(TestContext.Current.CancellationToken));
        Assert.Empty(original.Reconcile(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Memories.Read(fixture.Scope, CancellationToken.None));
        Assert.Single(fixture.Read(originalJournal).OfType<MemoryPromoted>());
        fixture.AssertIntegrityAndLedger(2);
    }

    [Theory]
    [InlineData("promote")]
    [InlineData("forget")]
    public async System.Threading.Tasks.Task Killing_a_writer_blocked_before_SQLite_commit_rolls_back_records_and_operation_receipt_together(string operation)
    {
        using var fixture = new CrashFixture("global");
        if (operation == "promote") fixture.CreateEmptyMemoryDatabase();
        using var child = fixture.Start(operation, "transaction");
        await child.Ready("transaction");
        using (var blocker = fixture.MemoryConnection())
        {
            blocker.Open();
            using var transaction = blocker.BeginTransaction(deferred: true);
            using var snapshot = blocker.CreateCommand(); snapshot.Transaction = transaction;
            snapshot.CommandText = "SELECT COUNT(*) FROM scoped_memory_records";
            Assert.Equal(operation == "promote" ? 0L : 1L, snapshot.ExecuteScalar());
            await child.Continue();
            await fixture.WaitForRollbackJournal(child.Process);
            // The read transaction permits a RESERVED writer but prevents its final EXCLUSIVE
            // commit. A competing writer must see BUSY while the real ApplyPrepared is active.
            using (var competitor = fixture.MemoryConnection(timeout: 1))
            {
                competitor.Open();
                var busy = Assert.Throws<SqliteException>(() => competitor.BeginTransaction(deferred: false));
                Assert.Equal(5, busy.SqliteErrorCode);
            }
            Assert.False(child.Process.HasExited);
            await child.Kill();
        }
        Assert.Equal(operation == "promote" ? 0 : 1, fixture.Memories.Read(fixture.Scope, CancellationToken.None).Count);
        fixture.AssertIntegrityAndLedger(operation == "promote" ? 0 : 1);
        using var journal = fixture.OpenJournal();
        var policy = fixture.Policy(journal);
        Assert.Single(policy.Reconcile(TestContext.Current.CancellationToken));
        Assert.Empty(policy.Reconcile(TestContext.Current.CancellationToken));
        Assert.Equal(operation == "promote" ? 1 : 0, fixture.Memories.Read(fixture.Scope, CancellationToken.None).Count);
        fixture.AssertIntegrityAndLedger(operation == "promote" ? 1 : 2);
    }

    private sealed class CrashFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-memory-crash-" + Guid.NewGuid().ToString("N"));
        private string Workspace => Path.Combine(Root, "workspace");
        private string Data => Path.Combine(Root, "data");
        private readonly string _scopeName;
        private readonly SessionId _session = SessionId.New();
        private readonly IEventCodecRegistry _codecs = EventCodecs.Create();
        internal MemoryScope Scope { get; }
        internal ScopedMemoryStore Memories => new(Data);
        private string MemoryPath => Scope.Level switch
        {
            ScopeLevel.User => Path.Combine(Data, "user.db"),
            ScopeLevel.Project => Path.Combine(Data, "projects", Scope.Id, "memory.db"),
            _ => Path.Combine(Data, "workspaces", Scope.Id, "memory.db"),
        };

        internal CrashFixture(string scopeName)
        {
            _scopeName = scopeName;
            Directory.CreateDirectory(Workspace);
            Scope = scopeName switch
            {
                "global" => MemoryScope.ForUser(),
                "project" => MemoryScope.ForProject(ProjectIdentity.FromWorkspaceRoot(Workspace)),
                _ => MemoryScope.ForWorkspace(WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
                    ProjectIdentity.ResolvePhysicalWorkspaceRoot(Workspace)))),
            };
            Trust(true);
        }

        internal void Trust(bool trusted) => new WorkspaceTrustStore(OmniHost.CreatePlatformPaths(Data)).SetTrusted(Workspace, trusted);
        internal SqliteEventStore OpenJournal() => SqliteEventStore.OpenUnpooled(Path.Combine(Root, "journal.db"));
        internal IReadOnlyList<DomainEventPayload> Read(SqliteEventStore journal) => journal.ReadFrom(_session, 1).Select(_codecs.Decode).ToArray();
        internal ScopedMemoryPolicy Policy(SqliteEventStore journal, SessionId? session = null)
        {
            var selected = session ?? _session;
            return new(selected, Memories, () => journal.ReadFrom(selected, 1).Select(_codecs.Decode).ToArray(),
                payload => new EventStream(journal, _codecs, selected).Append(payload, DurabilityClass.Barrier),
                scope => scope == Scope && (scope.Level != ScopeLevel.Project
                    || new WorkspaceTrustStore(OmniHost.CreatePlatformPaths(Data)).IsTrusted(Workspace)));
        }

        internal CrashChild Start(string operation, string cut) => new(Root, _session.ToString(), _scopeName, operation, cut);
        internal SqliteConnection MemoryConnection(int timeout = 5) => new(new SqliteConnectionStringBuilder
            { DataSource = MemoryPath, Pooling = false, DefaultTimeout = timeout }.ToString());

        internal void CreateEmptyMemoryDatabase()
        {
            var seed = new MemoryRecord("seed", Scope, MemoryKind.Fact, "Fixture seed.", .5, 1,
                MemorySource.UserStated, DateTimeOffset.UtcNow, null, MemoryExpirationKind.Never, [], [], null);
            Memories.Add(seed, CancellationToken.None);
            Assert.True(Memories.Remove(Scope, seed.Id, CancellationToken.None));
        }

        internal void AssertIntegrityAndLedger(int operations)
        {
            using var connection = MemoryConnection(); connection.Open();
            using var check = connection.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", check.ExecuteScalar());
            check.CommandText = "SELECT COUNT(*) FROM scoped_memory_operations";
            Assert.Equal((long)operations, check.ExecuteScalar());
        }

        internal async System.Threading.Tasks.Task WaitForRollbackJournal(Process child)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(4))
            {
                if (File.Exists(MemoryPath + "-journal") && new FileInfo(MemoryPath + "-journal").Length > 512) return;
                Assert.False(child.HasExited, "The fixture exited before its SQLite write transaction could be interrupted.");
                await System.Threading.Tasks.Task.Delay(10);
            }
            Assert.Fail("The writer did not create its rollback journal while commit was blocked.");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CrashChild : IDisposable
    {
        internal Process Process { get; }
        private readonly System.Threading.Tasks.Task<string> _stderr;
        internal CrashChild(params string[] arguments)
        {
            var assembly = Path.Combine(AppContext.BaseDirectory, "OmniCore.MemoryCrashTestProcess.dll");
            Assert.True(File.Exists(assembly), "The crash fixture must be built and copied by the test project.");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add(assembly);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            Process = System.Diagnostics.Process.Start(start)!;
            _stderr = Process.StandardError.ReadToEndAsync();
        }

        internal async System.Threading.Tasks.Task Ready(string cut)
        {
            var marker = await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (marker is null)
                Assert.Fail("The fixture exited before its durability boundary: " + await _stderr);
            Assert.Equal("ready:" + cut, marker);
        }

        internal async System.Threading.Tasks.Task Continue()
        {
            await Process.StandardInput.WriteLineAsync("continue");
            await Process.StandardInput.FlushAsync();
        }

        internal async System.Threading.Tasks.Task Kill()
        {
            if (Process.HasExited) Assert.Fail("The fixture exited before the parent could terminate it: " + await _stderr);
            Process.Kill(entireProcessTree: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Process.WaitForExitAsync(deadline.Token);
            Assert.NotEqual(0, Process.ExitCode);
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(10000);
            }
            Process.Dispose();
        }
    }
}
