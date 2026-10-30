namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Memory;

public sealed class ScopedMemoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-m10-memory-" + Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken None = CancellationToken.None;
    private static MemoryScope Project => MemoryScope.ForProject(ProjectId.Derive("https://example.test/repository"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(ScopeLevel.Project)]
    [InlineData(ScopeLevel.Workspace)]
    [InlineData(ScopeLevel.User)]
    public void Durable_partitions_reopen_and_preserve_metadata(ScopeLevel level)
    {
        var scope = Scope(level);
        var record = Record(scope, "Build with dotnet build.") with
        {
            Expiration = MemoryExpirationKind.At,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(3),
        };
        var store = new ScopedMemoryStore(_root);
        store.Add(record, None);
        var persisted = Assert.Single(new ScopedMemoryStore(_root).Read(scope, None));
        Assert.Equal(record.Id, persisted.Id);
        Assert.Equal(record.Content, persisted.Content);
        Assert.Equal(record.EvidenceRefs, persisted.EvidenceRefs);
        Assert.Equal(record.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(record.Scope, persisted.Scope);
        Assert.Equal(record.Confidence, persisted.Confidence);
    }

    [Fact]
    public void Equal_ids_and_content_do_not_cross_scope_or_workspace_partitions()
    {
        var store = new ScopedMemoryStore(_root);
        var scopes = new[] { Project, MemoryScope.ForProject(ProjectId.Derive("other")),
            MemoryScope.ForWorkspace(WorkspaceId.Of("clone-one")),
            MemoryScope.ForWorkspace(WorkspaceId.Of("clone-two")), MemoryScope.ForUser(),
            new MemoryScope(ScopeLevel.Workspace, Project.Id) };
        foreach (var scope in scopes) store.Add(Record(scope, "The same shared content.") with { Id = "same-id" }, None);
        foreach (var scope in scopes)
            Assert.Equal("The same shared content.", Assert.Single(store.Read(scope, None)).Content);
        Assert.True(store.Remove(scopes[0], "same-id", None));
        foreach (var scope in scopes.Skip(1)) Assert.Single(store.Read(scope, None));
    }

    [Fact]
    public void Retries_deduplicate_normalized_content_and_reject_id_conflicts()
    {
        var store = new ScopedMemoryStore(_root);
        var original = Record(Project, "Use dotnet build.");
        store.Add(original, None);
        Assert.Equal(original.Id, store.Add(original, None).Id);
        Assert.Equal(original.Id, store.Add(Record(Project, "  use  DOTNET build. "), None).Id);
        Assert.Throws<InvalidDataException>(() => store.Add(original with { Content = "Different fact." }, None));
        Assert.Single(store.Read(Project, None));
    }

    [Fact]
    public void Replacement_chains_reject_foreign_missing_forked_and_older_predecessors()
    {
        var store = new ScopedMemoryStore(_root);
        var original = Record(Project, "Original.");
        store.Add(original, None);
        var next = Record(Project, "Next.") with { Supersedes = original.Id, CreatedAt = original.CreatedAt.AddSeconds(1) };
        Assert.Throws<InvalidDataException>(() => store.Add(next with { Supersedes = "missing" }, None));
        Assert.Throws<InvalidDataException>(() => store.Add(next with { CreatedAt = original.CreatedAt.AddSeconds(-1) }, None));
        Assert.Throws<InvalidDataException>(() => store.Add(next with { Scope = MemoryScope.ForUser() }, None));
        store.Add(next, None);
        Assert.Throws<InvalidDataException>(() => store.Add(next with { Id = "fork", Content = "Fork." }, None));
        var latest = Record(Project, "Latest.") with { Supersedes = next.Id, CreatedAt = next.CreatedAt.AddSeconds(1) };
        store.Add(latest, None);
        Assert.True(store.Remove(Project, next.Id, None));
        Assert.Equal(original.Id, store.Read(Project, None).Single(item => item.Id == latest.Id).Supersedes);
        Assert.True(store.Remove(Project, latest.Id, None));
        Assert.Null(Assert.Single(store.Read(Project, None)).Supersedes);
    }

    [Fact]
    public void Failed_receipt_rolls_back_records_and_replacement_links()
    {
        var store = new ScopedMemoryStore(_root);
        var original = Record(Project, "Original.");
        var next = Record(Project, "Next.") with { Supersedes = original.Id, CreatedAt = original.CreatedAt.AddSeconds(1) };
        store.Add(original, None); store.Add(next, None);
        Assert.Throws<IOException>(() => store.RemoveWithReceipt(Project, original.Id,
            () => throw new IOException("fixture receipt failure"), () => false, None));
        var reopened = new ScopedMemoryStore(_root).Read(Project, None);
        Assert.Equal(2, reopened.Count);
        Assert.Equal(original.Id, reopened.Single(item => item.Id == next.Id).Supersedes);
    }

    [Fact]
    public void Durable_receipt_survives_callback_failure_and_cancellation()
    {
        var store = new ScopedMemoryStore(_root);
        var original = Record(Project, "Original.");
        store.Add(original, None);
        using var cancellation = new CancellationTokenSource();
        Assert.True(store.RemoveWithReceipt(Project, original.Id, () =>
        {
            cancellation.Cancel();
            throw new IOException("failure after durable receipt");
        }, () => true, cancellation.Token));
        Assert.Empty(new ScopedMemoryStore(_root).Read(Project, None));
        var published = false;
        Assert.False(store.RemoveWithReceipt(Project, "missing", () => published = true, () => false, None));
        Assert.False(published);
    }

    [Fact]
    public void Successful_receipt_publication_commits_even_when_callback_cancels_the_token()
    {
        var store = new ScopedMemoryStore(_root);
        var record = Record(Project, "Forget after confirmation."); store.Add(record, None);
        using var cancellation = new CancellationTokenSource();
        var published = false;
        Assert.True(store.RemoveWithReceipt(Project, record.Id, () =>
        {
            published = true; cancellation.Cancel();
        }, () => published, cancellation.Token));
        Assert.True(published);
        Assert.Empty(new ScopedMemoryStore(_root).Read(Project, None));
    }

    [Fact]
    public void Mark_used_is_monotonic_and_persists_across_instances()
    {
        var store = new ScopedMemoryStore(_root);
        var record = Record(Project, "Fact."); store.Add(record, None);
        var used = record.CreatedAt.AddMinutes(1);
        Assert.Equal(1, store.MarkUsed(Project, [record.Id, record.Id, "missing"], used, None));
        Assert.Equal(0, store.MarkUsed(Project, [record.Id], used.AddSeconds(-1), None));
        Assert.Equal(used, Assert.Single(new ScopedMemoryStore(_root).Read(Project, None)).LastUsedAt);
    }

    [Fact]
    public async System.Threading.Tasks.Task Independent_instances_serialize_concurrent_writes_without_lost_updates()
    {
        new ScopedMemoryStore(_root).Add(Record(Project, "Initial."), None);
        await System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 12).Select(index => System.Threading.Tasks.Task.Run(() =>
            new ScopedMemoryStore(_root).Add(Record(Project, "Concurrent " + index), None))));
        Assert.Equal(13, new ScopedMemoryStore(_root).Read(Project, None).Count);
    }

    [Fact]
    public void User_database_keeps_existing_tables()
    {
        Directory.CreateDirectory(_root);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "user.db")};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE existing_preferences (value TEXT); INSERT INTO existing_preferences VALUES ('preserved')";
            command.ExecuteNonQuery();
        }
        new ScopedMemoryStore(_root).Add(Record(MemoryScope.ForUser(), "User preference."), None);
        using var reopened = new SqliteConnection($"Data Source={Path.Combine(_root, "user.db")};Pooling=False");
        reopened.Open(); using var query = reopened.CreateCommand();
        query.CommandText = "SELECT value FROM existing_preferences";
        Assert.Equal("preserved", query.ExecuteScalar());
    }

    [Fact]
    public void Cancellation_before_storage_does_not_create_files()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var store = new ScopedMemoryStore(_root);
        Assert.ThrowsAny<OperationCanceledException>(() => store.Add(Record(Project, "Fact."), cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => store.Read(Project, cancelled.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Partition_limit_rejects_the_next_record_without_losing_existing_data()
    {
        var store = new ScopedMemoryStore(_root);
        for (var index = 0; index < 500; index++) store.Add(Record(Project, "Fact " + index), None);
        Assert.Throws<InvalidDataException>(() => store.Add(Record(Project, "One too many."), None));
        Assert.Equal(500, new ScopedMemoryStore(_root).Read(Project, None).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Corrupt_index_or_oversized_payload_is_rejected_on_reopen(bool oversized)
    {
        var store = new ScopedMemoryStore(_root); store.Add(Record(Project, "Fact."), None);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "projects", Project.Id, "memory.db"), Pooling = false,
        }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = oversized ? "UPDATE scoped_memory_records SET payload = zeroblob(4194305)"
                : "UPDATE scoped_memory_records SET record_id = 'mismatched-index'";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => new ScopedMemoryStore(_root).Read(Project, None));
    }

    [Fact]
    public void Reparse_point_does_not_redirect_memory_into_another_directory()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "projects");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or PlatformNotSupportedException or NotSupportedException)
            {
                if (!TryCreateJunction(link, outside))
                {
                    Assert.Skip("The environment cannot create directory symbolic links or junctions."); return;
                }
            }
            Assert.Throws<InvalidDataException>(() => new ScopedMemoryStore(_root).Add(Record(Project, "Fact."), None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("-journal")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void Database_and_sidecar_links_cannot_redirect_writes(string suffix)
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(_root, "preserved.txt"); File.WriteAllText(outside, "untouched");
        var link = Path.Combine(_root, "user.db" + suffix);
        try
        {
            try { File.CreateSymbolicLink(link, outside); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or PlatformNotSupportedException or NotSupportedException)
            {
                Assert.Skip("The environment cannot create file symbolic links."); return;
            }
            Assert.Throws<InvalidDataException>(() => new ScopedMemoryStore(_root).Add(Record(MemoryScope.ForUser(), "Fact."), None));
            Assert.Equal("untouched", File.ReadAllText(outside));
        }
        finally { if (File.Exists(link)) File.Delete(link); }
    }

    [Fact]
    public void Payload_budget_stops_large_records_before_the_record_count_limit()
    {
        var store = new ScopedMemoryStore(_root);
        var rejected = false;
        var accepted = 0;
        for (var index = 0; index < 500; index++)
        {
            var record = Record(Project, index + " " + new string('x', 4000)) with
            {
                EvidenceRefs = Enumerable.Range(0, 32).Select(number => number + new string('e', 500)).ToArray(),
            };
            try { store.Add(record, None); accepted++; }
            catch (InvalidDataException) { rejected = true; break; }
        }
        Assert.True(rejected);
        Assert.InRange(accepted, 1, 499);
        Assert.Equal(accepted, new ScopedMemoryStore(_root).Read(Project, None).Count);
    }

    [Theory]
    [InlineData(ScopeLevel.Project, "../outside")]
    [InlineData(ScopeLevel.Workspace, "ABCDEF0123456789")]
    [InlineData(ScopeLevel.User, "another-user")]
    [InlineData(ScopeLevel.Task, "user")]
    [InlineData(ScopeLevel.Session, "invalid-session")]
    public void Invalid_scope_identity_cannot_become_a_storage_path(ScopeLevel level, string id)
    {
        var scope = new MemoryScope(level, id);
        Assert.Throws<ArgumentException>(scope.Validate);
        Assert.Throws<ArgumentException>(() => new ScopedMemoryStore(_root).Read(scope, None));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Session_store_and_scoped_store_reject_each_others_scopes()
    {
        var session = MemoryScope.ForSession(SessionId.New());
        Assert.Throws<ArgumentException>(() => new ScopedMemoryStore(_root).Read(session, None));
        var local = new SessionMemoryStore(_root);
        foreach (var scope in new[] { Project, Scope(ScopeLevel.Workspace), MemoryScope.ForUser() })
        {
            Assert.Throws<ArgumentException>(() => local.Read(scope, None));
            Assert.Throws<ArgumentException>(() => local.Add(Record(scope, "Fact."), None));
            Assert.Throws<ArgumentException>(() => local.Remove(scope, "id", None));
            Assert.Throws<ArgumentException>(() => local.MarkUsed(scope, ["id"], DateTimeOffset.UtcNow, None));
            Assert.Throws<ArgumentException>(() => local.MarkUsed(scope, [], DateTimeOffset.UtcNow, None));
            Assert.Throws<ArgumentException>(() => local.RemoveWithReceipt(scope, "id", () => { }, () => false, None));
        }
    }

    [Theory]
    [InlineData(ScopeLevel.User, false)]
    [InlineData(ScopeLevel.User, true)]
    [InlineData(ScopeLevel.Project, false)]
    [InlineData(ScopeLevel.Workspace, false)]
    [InlineData(ScopeLevel.Session, false)]
    public void Session_promotion_cannot_replay_a_candidate_in_another_scope(ScopeLevel level, bool alreadyPromoted)
    {
        var session = SessionId.New();
        var foreign = level == ScopeLevel.Session ? MemoryScope.ForSession(SessionId.New()) : Scope(level);
        List<DomainEventPayload> events = [new MemoryCandidateProposed("foreign", foreign,
            MemoryKind.Preference, "Preference.", [], 1, MemoryCandidateOrigin.Model, "promote", null, DateTimeOffset.UtcNow)];
        if (alreadyPromoted) events.Add(new MemoryPromoted("foreign", "memory", null, DateTimeOffset.UtcNow));
        var policy = new SessionMemoryPolicy(session, new ScopedMemoryStore(_root), () => events, events.Add);
        Assert.Throws<InvalidOperationException>(() => policy.Promote("foreign"));
        Assert.Throws<InvalidOperationException>(() => policy.Reject("foreign"));
        Assert.DoesNotContain(events, item => item is MemoryRejected);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.1)]
    public void Nonfinite_or_out_of_range_scores_are_rejected(double score)
    {
        Assert.Throws<ArgumentException>(() => (Record(Project, "Fact.") with { Confidence = score }).Validate());
        Assert.Throws<ArgumentException>(() => (Record(Project, "Fact.") with { Importance = score }).Validate());
    }

    private static MemoryScope Scope(ScopeLevel level) => level switch
    {
        ScopeLevel.Project => Project,
        ScopeLevel.Workspace => MemoryScope.ForWorkspace(WorkspaceId.Of("fixture-root")),
        ScopeLevel.User => MemoryScope.ForUser(),
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static MemoryRecord Record(MemoryScope scope, string content) => new(Guid.NewGuid().ToString("N"),
        scope, MemoryKind.Fact, content, .7, .9, MemorySource.UserStated, DateTimeOffset.UtcNow,
        null, MemoryExpirationKind.Never, ["fixture"], ["test:explicit-user"], null);

    private static bool TryCreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The isolated junction fixture did not finish.");
        }
        return process.ExitCode == 0;
    }
}
