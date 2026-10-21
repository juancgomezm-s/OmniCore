namespace OmniCore.Infrastructure;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Workspace-scoped mutation identity and receipt store. Rows live outside the
/// session event tables, so session retention cannot erase path tombstones or their CAS refs.</summary>
public sealed class SqliteWorkspaceMutationHistoryStore : IWorkspaceMutationHistoryStore, IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string _dataDirectory;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly FileArtifactStore _artifacts;
    private readonly object _sync = new();

    public SqliteWorkspaceMutationHistoryStore(string dataDirectory)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(_dataDirectory);
        _artifacts = new FileArtifactStore(_dataDirectory);
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dataDirectory, "journal.db"),
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        _connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        _connection.Open();
        using var busy = _connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000";
        busy.ExecuteNonQuery();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var transaction = _connection.BeginTransaction();
        using var inspect = _connection.CreateCommand(); inspect.Transaction = transaction;
        inspect.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN "
            + "('workspace_mutation_schema','workspace_file_history','workspace_mutation_receipts')";
        var found = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = inspect.ExecuteReader()) while (reader.Read()) found.Add(reader.GetString(0));
        if (found.Contains("workspace_mutation_schema"))
        {
            if (!found.SetEquals(["workspace_mutation_schema", "workspace_file_history", "workspace_mutation_receipts"]))
                throw new InvalidDataException("Workspace mutation history schema is incomplete.");
            transaction.Commit();
            ValidateSchema();
            return;
        }
        if (found.Count != 0) throw new InvalidDataException("Workspace mutation history tables exist without a schema marker.");

        using var schema = _connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE workspace_file_history (
                workspace_id TEXT NOT NULL,
                path_key TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                state INTEGER NOT NULL,
                expected_hash TEXT,
                image_ref TEXT,
                revision INTEGER NOT NULL,
                last_receipt TEXT,
                observed_hash TEXT,
                PRIMARY KEY(workspace_id, path_key)
            );
            CREATE TABLE workspace_mutation_receipts (
                workspace_id TEXT NOT NULL,
                tool_call_id TEXT NOT NULL,
                path_key TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                run_id TEXT NOT NULL,
                turn_id TEXT NOT NULL,
                operation TEXT NOT NULL,
                expected_pre_hash TEXT,
                expected_post_hash TEXT,
                preimage_ref TEXT,
                postimage_ref TEXT,
                deleted_lines INTEGER NOT NULL,
                inserted_lines INTEGER NOT NULL,
                state INTEGER NOT NULL,
                PRIMARY KEY(workspace_id, tool_call_id, path_key)
            );
            CREATE INDEX ix_workspace_mutation_receipts_scope
                ON workspace_mutation_receipts(workspace_id, run_id, turn_id, state);
            CREATE TABLE workspace_mutation_schema (version INTEGER NOT NULL);
            INSERT INTO workspace_mutation_schema(version) VALUES(1);
            """;
        schema.ExecuteNonQuery();
        transaction.Commit();
        ValidateSchema();
    }

    public IDisposable AcquireFence(string workspaceRoot, CancellationToken cancellationToken)
    {
        var id = WorkspaceId(workspaceRoot);
        var directory = Path.Combine(_dataDirectory, "mutation-locks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new Fence(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException) { cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(25)); }
        }
    }

    public string GetWorkspaceId(string workspaceRoot) => WorkspaceId(workspaceRoot);
    public string NormalizeRelativePath(string workspaceRoot, string path) => PathIdentity(path, workspaceRoot).Relative;

    public WorkspaceFileHistoryEntry? ReadFile(string workspaceRoot, string relativePath)
    {
        var workspaceId = WorkspaceId(workspaceRoot);
        var (key, relative) = PathIdentity(relativePath, workspaceRoot);
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT relative_path, state, expected_hash, image_ref, revision, last_receipt, observed_hash "
                + "FROM workspace_file_history WHERE workspace_id=:workspace AND path_key=:key";
            command.Parameters.AddWithValue("workspace", workspaceId);
            command.Parameters.AddWithValue("key", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            if (!PathComparer.Equals(reader.GetString(0), relative))
                throw new InvalidDataException("Workspace mutation path key is inconsistent.");
            var state = (WorkspaceFileHistoryState)reader.GetInt32(1);
            if (!Enum.IsDefined(state)) throw new InvalidDataException("Workspace mutation history state is invalid.");
            var image = reader.IsDBNull(3) ? null : DeserializeRef(reader.GetString(3));
            var entry = new WorkspaceFileHistoryEntry(workspaceId, relative, state,
                reader.IsDBNull(2) ? null : reader.GetString(2), image, reader.GetInt64(4),
                reader.IsDBNull(5) ? null : ToolCallId.Parse(reader.GetString(5)))
            { ObservedHash = reader.IsDBNull(6) ? null : reader.GetString(6) };
            // A tombstone without its exact preimage is not a valid recreation basis. Surface
            // it as Unknown so all mutation paths fail closed without granting a create.
            if (state == WorkspaceFileHistoryState.Tombstone
                && (entry.ExpectedHash is null || !TryReadFaithfulPreimage(entry.ImageRef, entry.ExpectedHash, out _)))
                return entry with { State = WorkspaceFileHistoryState.Unknown, ImageRef = null };
            return entry;
        }
    }

    public WorkspaceMutationReceipt? ReadReceipt(string workspaceRoot, ToolCallId toolCallId, string relativePath)
    {
        var (key, relative) = PathIdentity(relativePath, workspaceRoot);
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT run_id, turn_id, operation, expected_pre_hash, expected_post_hash, "
                + "preimage_ref, postimage_ref, deleted_lines, inserted_lines, state, relative_path "
                + "FROM workspace_mutation_receipts WHERE workspace_id=:workspace AND tool_call_id=:call AND path_key=:key";
            command.Parameters.AddWithValue("workspace", WorkspaceId(workspaceRoot));
            command.Parameters.AddWithValue("call", toolCallId.ToString());
            command.Parameters.AddWithValue("key", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            if (!PathComparer.Equals(reader.GetString(10), relative))
                throw new InvalidDataException("Workspace mutation receipt path key is inconsistent.");
            var state = (WorkspaceMutationReceiptState)reader.GetInt32(9);
            if (!Enum.IsDefined(state)) throw new InvalidDataException("Workspace mutation receipt state is invalid.");
            var receipt = new WorkspaceMutationReceipt(WorkspaceId(workspaceRoot), relative, toolCallId,
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DeserializeRef(reader.GetString(5)),
                reader.IsDBNull(6) ? null : DeserializeRef(reader.GetString(6)),
                reader.GetInt32(7), reader.GetInt32(8), state);
            ValidateReceipt(receipt);
            if (state != WorkspaceMutationReceiptState.Unknown
                && receipt.ExpectedPreHash is not null && !TryVerifyReceiptPreimage(receipt))
                throw new InvalidDataException("Workspace mutation receipt has no faithful preimage.");
            return receipt;
        }
    }

    public void RecordObservation(string workspaceRoot, string relativePath, string observedHash,
        CancellationToken cancellationToken)
    {
        if (!IsHash(observedHash)) throw new ArgumentException("Observed file hash is invalid.", nameof(observedHash));
        cancellationToken.ThrowIfCancellationRequested();
        var workspaceId = WorkspaceId(workspaceRoot);
        var (key, relative) = PathIdentity(relativePath, workspaceRoot);
        lock (_sync)
        {
            using var tx = _connection.BeginTransaction();
            using var query = _connection.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT state, expected_hash, revision, image_ref, last_receipt FROM workspace_file_history "
                + "WHERE workspace_id=:workspace AND path_key=:key";
            query.Parameters.AddWithValue("workspace", workspaceId); query.Parameters.AddWithValue("key", key);
            using var reader = query.ExecuteReader();
            var exists = reader.Read();
            var state = exists ? (WorkspaceFileHistoryState)reader.GetInt32(0) : WorkspaceFileHistoryState.Present;
            var expected = exists && !reader.IsDBNull(1) ? reader.GetString(1) : null;
            var revision = exists ? reader.GetInt64(2) : 0;
            var imageRef = exists && !reader.IsDBNull(3) ? reader.GetString(3) : null;
            var lastReceipt = exists && !reader.IsDBNull(4) ? reader.GetString(4) : null;
            reader.Close();
            if (!exists)
            {
                UpsertState(tx, workspaceId, key, relative, WorkspaceFileHistoryState.Present,
                    observedHash, null, 1, null, observedHash);
            }
            else if (state == WorkspaceFileHistoryState.Present && expected == observedHash)
            {
                // Repeated reads do not create new versions or history growth.
            }
            else if (state is WorkspaceFileHistoryState.Tombstone or WorkspaceFileHistoryState.ExternalConflict
                or WorkspaceFileHistoryState.Unknown)
            {
                // A physical file has reappeared at a historically deleted/uncertain path.
                // Keep its logical identity and prior CAS receipt; a visible read records only
                // the observed bytes, leaving replacement to an explicit ReplaceFile decision.
                UpsertState(tx, workspaceId, key, relative, WorkspaceFileHistoryState.ExternalConflict,
                    expected, imageRef, checked(revision + 1), lastReceipt, observedHash);
            }
            else
            {
                // A successful model-visible read is the explicit boundary at which an
                // externally edited, still-present file becomes the new preimage. Historical
                // receipts remain untouched; mutation captures a protected image only after
                // authorization. Missing files are handled as conflicts elsewhere.
                UpsertState(tx, workspaceId, key, relative, WorkspaceFileHistoryState.Present,
                    observedHash, null, checked(revision + 1), lastReceipt, observedHash);
            }
            tx.Commit();
        }
    }

    public void Prepare(IReadOnlyList<WorkspaceMutationReceipt> receipts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        if (receipts.Count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(receipts));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var receipt in receipts)
            {
                ValidateReceipt(receipt);
                if (!TryVerifyReceiptPreimage(receipt))
                    throw new InvalidDataException("Workspace mutation preimage is missing or not faithful to its hash.");
                using var insert = _connection.CreateCommand(); insert.Transaction = tx;
                insert.CommandText = "INSERT INTO workspace_mutation_receipts(workspace_id,tool_call_id,path_key,relative_path,"
                    + "run_id,turn_id,operation,expected_pre_hash,expected_post_hash,preimage_ref,postimage_ref,"
                    + "deleted_lines,inserted_lines,state) VALUES(:workspace,:call,:key,:path,:run,:turn,:operation,"
                    + ":pre,:post,:preimage,:postimage,:deleted,:inserted,:state)";
                insert.Parameters.AddWithValue("workspace", receipt.WorkspaceId);
                insert.Parameters.AddWithValue("call", receipt.ToolCallId.ToString());
                insert.Parameters.AddWithValue("key", PathIdentity(receipt.RelativePath).Key);
                insert.Parameters.AddWithValue("path", receipt.RelativePath);
                insert.Parameters.AddWithValue("run", receipt.RunId); insert.Parameters.AddWithValue("turn", receipt.TurnId);
                insert.Parameters.AddWithValue("operation", receipt.Operation);
                AddNullable(insert, "pre", receipt.ExpectedPreHash); AddNullable(insert, "post", receipt.ExpectedPostHash);
                AddNullable(insert, "preimage", SerializeRef(receipt.PreimageRef));
                AddNullable(insert, "postimage", SerializeRef(receipt.PostimageRef));
                insert.Parameters.AddWithValue("deleted", receipt.DeletedLines);
                insert.Parameters.AddWithValue("inserted", receipt.InsertedLines);
                insert.Parameters.AddWithValue("state", (int)WorkspaceMutationReceiptState.Prepared);
                try { insert.ExecuteNonQuery(); }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    if (!ReceiptMatches(receipt, tx)) throw new InvalidDataException("Duplicate mutation receipt has different content.");
                }
            }
            tx.Commit();
        }
    }

    public void Complete(IReadOnlyList<WorkspaceMutationReceipt> receipts,
        IReadOnlyList<WorkspaceFileHistoryEntry> resultingStates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipts); ArgumentNullException.ThrowIfNull(resultingStates);
        if (receipts.Count is < 1 or > 64 || receipts.Count != resultingStates.Count)
            throw new ArgumentException("Mutation receipt and state batches must be bounded and aligned.");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            using var tx = _connection.BeginTransaction();
            for (var index = 0; index < receipts.Count; index++)
            {
                var receipt = receipts[index]; var state = resultingStates[index];
                ValidateReceipt(receipt);
                if (state.WorkspaceId != receipt.WorkspaceId
                    || !string.Equals(PathIdentity(state.RelativePath).Relative, state.RelativePath, StringComparison.Ordinal))
                    throw new InvalidDataException("Workspace mutation result has a different workspace or noncanonical path.");
                if (receipt.State is not (WorkspaceMutationReceiptState.Applied or WorkspaceMutationReceiptState.NotApplied
                    or WorkspaceMutationReceiptState.Conflict or WorkspaceMutationReceiptState.Unknown))
                    throw new InvalidOperationException("A prepared mutation cannot be finalized as Prepared.");
                if (receipt.State == WorkspaceMutationReceiptState.Applied)
                {
                    if (!TryVerifyReceiptPreimage(receipt))
                        throw new InvalidDataException("Applied mutation receipt has no faithful preimage.");
                    var stateMatchesReceipt = receipt.ExpectedPostHash is null
                        ? state.State == WorkspaceFileHistoryState.Tombstone
                            && state.ExpectedHash == receipt.ExpectedPreHash
                            && SerializeRef(state.ImageRef) == SerializeRef(receipt.PreimageRef)
                        : state.State == WorkspaceFileHistoryState.Present
                            && state.ExpectedHash == receipt.ExpectedPostHash
                            && SerializeRef(state.ImageRef) == SerializeRef(receipt.PostimageRef);
                    if (state.LastReceipt != receipt.ToolCallId || !stateMatchesReceipt)
                        throw new InvalidDataException("Applied mutation state does not match its durable receipt.");
                }
                var key = PathIdentity(receipt.RelativePath).Key;
                using var update = _connection.CreateCommand(); update.Transaction = tx;
                update.CommandText = "UPDATE workspace_mutation_receipts SET state=:state WHERE workspace_id=:workspace "
                    + "AND tool_call_id=:call AND path_key=:key AND state IN (:prepared,:same)";
                update.Parameters.AddWithValue("state", (int)receipt.State);
                update.Parameters.AddWithValue("workspace", receipt.WorkspaceId);
                update.Parameters.AddWithValue("call", receipt.ToolCallId.ToString()); update.Parameters.AddWithValue("key", key);
                update.Parameters.AddWithValue("prepared", (int)WorkspaceMutationReceiptState.Prepared);
                update.Parameters.AddWithValue("same", (int)receipt.State);
                var updated = update.ExecuteNonQuery();
                if (updated == 0)
                {
                    if (!ReceiptMatchesFinal(receipt, tx) || !ReceiptAlreadyFinalUnchanged(receipt, state, tx))
                        throw new InvalidDataException("Mutation receipt could not be finalized idempotently.");
                    continue;
                }
                if (receipt.State is WorkspaceMutationReceiptState.Applied or WorkspaceMutationReceiptState.Conflict
                    or WorkspaceMutationReceiptState.Unknown)
                {
                    if (state.State == WorkspaceFileHistoryState.Tombstone
                        && (state.ExpectedHash is null || !TryReadFaithfulPreimage(state.ImageRef, state.ExpectedHash, out _)))
                        throw new InvalidDataException("A tombstone cannot be committed without a faithful CAS preimage.");
                    UpsertState(tx, state.WorkspaceId, key, state.RelativePath, state.State, state.ExpectedHash,
                        SerializeRef(state.ImageRef), state.Revision, state.LastReceipt?.ToString(), null);
                }
            }
            tx.Commit();
        }
    }

    public void MarkConflict(string workspaceRoot, string relativePath, string? observedHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspaceId = WorkspaceId(workspaceRoot); var (key, relative) = PathIdentity(relativePath, workspaceRoot);
        lock (_sync)
        {
            using var tx = _connection.BeginTransaction();
            using var query = _connection.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT expected_hash, image_ref, revision, last_receipt FROM workspace_file_history "
                + "WHERE workspace_id=:workspace AND path_key=:key";
            query.Parameters.AddWithValue("workspace", workspaceId); query.Parameters.AddWithValue("key", key);
            using var reader = query.ExecuteReader();
            string? expected = null, image = null, receipt = null; long revision = 0;
            if (reader.Read()) { expected = reader.IsDBNull(0) ? null : reader.GetString(0); image = reader.IsDBNull(1) ? null : reader.GetString(1); revision = reader.GetInt64(2); receipt = reader.IsDBNull(3) ? null : reader.GetString(3); }
            reader.Close();
            UpsertState(tx, workspaceId, key, relative, WorkspaceFileHistoryState.ExternalConflict,
                expected, image, checked(revision + 1), receipt, observedHash);
            tx.Commit();
        }
    }

    public IReadOnlyList<WorkspaceMutationReceipt> ReadPending(string workspaceRoot)
    {
        var workspace = WorkspaceId(workspaceRoot);
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT tool_call_id,relative_path,run_id,turn_id,operation,expected_pre_hash,expected_post_hash,"
                + "preimage_ref,postimage_ref,deleted_lines,inserted_lines,state FROM workspace_mutation_receipts "
                + "WHERE workspace_id=:workspace AND state=:prepared ORDER BY rowid";
            command.Parameters.AddWithValue("workspace", workspace);
            command.Parameters.AddWithValue("prepared", (int)WorkspaceMutationReceiptState.Prepared);
            using var reader = command.ExecuteReader(); var values = new List<WorkspaceMutationReceipt>();
            while (reader.Read()) values.Add(new WorkspaceMutationReceipt(workspace, reader.GetString(1),
                ToolCallId.Parse(reader.GetString(0)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : DeserializeRef(reader.GetString(7)),
                reader.IsDBNull(8) ? null : DeserializeRef(reader.GetString(8)), reader.GetInt32(9), reader.GetInt32(10),
                (WorkspaceMutationReceiptState)reader.GetInt32(11)));
            return values;
        }
    }

    public void RecoverPending(string workspaceRoot, CancellationToken cancellationToken)
    {
        var pending = ReadPending(workspaceRoot);
        if (pending.Count == 0) return;
        var results = new List<WorkspaceMutationReceipt>(pending.Count);
        var states = new List<WorkspaceFileHistoryEntry>(pending.Count);
        foreach (var receipt in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var faithfulPreimage = TryVerifyReceiptPreimage(receipt);
            var observed = ObserveSafeTarget(workspaceRoot, receipt.RelativePath);
            var pre = receipt.ExpectedPreHash;
            var post = receipt.ExpectedPostHash;
            var final = !faithfulPreimage && pre is not null
                ? WorkspaceMutationReceiptState.Unknown
                : observed.Kind == SafeTargetKind.Present && pre == post && observed.Hash == pre
                ? WorkspaceMutationReceiptState.NotApplied
                : observed.Kind == SafeTargetKind.Present && observed.Hash == post
                    ? WorkspaceMutationReceiptState.Applied
                    : observed.Kind == SafeTargetKind.Absent && post is null
                        ? WorkspaceMutationReceiptState.Applied
                        : observed.Kind == SafeTargetKind.Absent && pre is null
                            ? WorkspaceMutationReceiptState.NotApplied
                            : observed.Kind == SafeTargetKind.Present && observed.Hash == pre
                                ? WorkspaceMutationReceiptState.NotApplied
                                : WorkspaceMutationReceiptState.Conflict;
            var previous = ReadFile(workspaceRoot, receipt.RelativePath);
            var state = final switch
            {
                WorkspaceMutationReceiptState.Applied when post is null => new WorkspaceFileHistoryEntry(
                    receipt.WorkspaceId, receipt.RelativePath, WorkspaceFileHistoryState.Tombstone,
                    pre, receipt.PreimageRef, checked((previous?.Revision ?? 0) + 1), receipt.ToolCallId),
                WorkspaceMutationReceiptState.Applied => new WorkspaceFileHistoryEntry(receipt.WorkspaceId,
                    receipt.RelativePath, WorkspaceFileHistoryState.Present, post,
                    receipt.PostimageRef, checked((previous?.Revision ?? 0) + 1), receipt.ToolCallId),
                WorkspaceMutationReceiptState.Conflict => new WorkspaceFileHistoryEntry(receipt.WorkspaceId,
                    receipt.RelativePath, WorkspaceFileHistoryState.ExternalConflict,
                    previous?.ExpectedHash ?? pre, previous?.ImageRef ?? receipt.PreimageRef,
                    checked((previous?.Revision ?? 0) + 1), previous?.LastReceipt),
                WorkspaceMutationReceiptState.Unknown => new WorkspaceFileHistoryEntry(receipt.WorkspaceId,
                    receipt.RelativePath, WorkspaceFileHistoryState.Unknown, previous?.ExpectedHash ?? pre,
                    previous?.ImageRef, checked((previous?.Revision ?? 0) + 1), previous?.LastReceipt),
                _ => previous ?? new WorkspaceFileHistoryEntry(receipt.WorkspaceId, receipt.RelativePath,
                    WorkspaceFileHistoryState.Unknown, pre, receipt.PreimageRef, 1, null),
            };
            results.Add(receipt with { State = final });
            states.Add(state);
        }
        Complete(results, states, cancellationToken);
    }

    private static SafeTargetObservation ObserveSafeTarget(string workspaceRoot, string relativePath)
    {
        try
        {
            _ = PathIdentity(relativePath);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            var full = Path.GetFullPath(relativePath, root);
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var prefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, comparison)) return new(SafeTargetKind.Unknown, null);

            // Recovery may classify absence only after confirming that every ancestor still
            // names the same ordinary directory chain. Links, missing roots, and permission
            // failures are ambiguous effects, never evidence of a completed delete.
            if (new DirectoryInfo(root).LinkTarget is not null) return new(SafeTargetKind.Unknown, null);
            var rootAttributes = File.GetAttributes(root);
            if ((rootAttributes & FileAttributes.Directory) == 0
                || (rootAttributes & FileAttributes.ReparsePoint) != 0)
                return new(SafeTargetKind.Unknown, null);
            var parent = Path.GetDirectoryName(full);
            var chain = new Stack<string>();
            while (parent is not null && !string.Equals(parent, root, comparison))
            {
                chain.Push(parent);
                parent = Path.GetDirectoryName(parent);
            }
            if (parent is null) return new(SafeTargetKind.Unknown, null);
            while (chain.Count > 0)
            {
                var ancestor = chain.Pop();
                if (new DirectoryInfo(ancestor).LinkTarget is not null)
                    return new(SafeTargetKind.Unknown, null);
                var attributes = File.GetAttributes(ancestor);
                if ((attributes & FileAttributes.Directory) == 0
                    || (attributes & FileAttributes.ReparsePoint) != 0)
                    return new(SafeTargetKind.Unknown, null);
            }

            if (new FileInfo(full).LinkTarget is not null || new DirectoryInfo(full).LinkTarget is not null)
                return new(SafeTargetKind.Unknown, null);
            FileAttributes leafAttributes;
            try { leafAttributes = File.GetAttributes(full); }
            catch (FileNotFoundException) { return new(SafeTargetKind.Absent, null); }
            catch (DirectoryNotFoundException) { return new(SafeTargetKind.Unknown, null); }
            if ((leafAttributes & FileAttributes.Directory) != 0
                || (leafAttributes & FileAttributes.ReparsePoint) != 0)
                return new(SafeTargetKind.Unknown, null);

            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length is < 0 or > 2 * 1024 * 1024)
                return new(SafeTargetKind.Unknown, null);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            var total = 0L;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total += read;
                if (total > 2 * 1024 * 1024) return new(SafeTargetKind.Unknown, null);
                hash.AppendData(buffer, 0, read);
            }
            if (total != stream.Length) return new(SafeTargetKind.Unknown, null);
            return new(SafeTargetKind.Present, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        { return new(SafeTargetKind.Unknown, null); }
    }

    private enum SafeTargetKind { Present, Absent, Unknown }
    private readonly record struct SafeTargetObservation(SafeTargetKind Kind, string? Hash);

    public IReadOnlyList<WorkspaceMutationReceipt> ReadBudgetCharges(string workspaceRoot, string runId, string turnId)
    {
        var workspace = WorkspaceId(workspaceRoot);
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT tool_call_id,relative_path,operation,expected_pre_hash,expected_post_hash,preimage_ref,"
                + "postimage_ref,deleted_lines,inserted_lines,state FROM workspace_mutation_receipts WHERE workspace_id=:workspace "
                + "AND run_id=:run AND turn_id=:turn AND state=:applied AND operation <> 'workspace.undo' ORDER BY rowid";
            command.Parameters.AddWithValue("workspace", workspace); command.Parameters.AddWithValue("run", runId);
            command.Parameters.AddWithValue("turn", turnId); command.Parameters.AddWithValue("applied", (int)WorkspaceMutationReceiptState.Applied);
            using var reader = command.ExecuteReader(); var values = new List<WorkspaceMutationReceipt>();
            while (reader.Read()) values.Add(new WorkspaceMutationReceipt(workspace, reader.GetString(1),
                ToolCallId.Parse(reader.GetString(0)), runId, turnId, reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DeserializeRef(reader.GetString(5)),
                reader.IsDBNull(6) ? null : DeserializeRef(reader.GetString(6)), reader.GetInt32(7), reader.GetInt32(8),
                (WorkspaceMutationReceiptState)reader.GetInt32(9)));
            return values;
        }
    }

    public IReadOnlyList<WorkspaceMutationReceipt> ReadBudgetChargesForRun(string workspaceRoot, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var workspace = WorkspaceId(workspaceRoot);
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT tool_call_id,relative_path,turn_id,operation,expected_pre_hash,expected_post_hash,"
                + "preimage_ref,postimage_ref,deleted_lines,inserted_lines,state FROM workspace_mutation_receipts "
                + "WHERE workspace_id=:workspace AND run_id=:run "
                + "AND state IN (:applied,:conflict,:unknown) AND operation <> 'workspace.undo' ORDER BY rowid";
            command.Parameters.AddWithValue("workspace", workspace); command.Parameters.AddWithValue("run", runId);
            command.Parameters.AddWithValue("applied", (int)WorkspaceMutationReceiptState.Applied);
            command.Parameters.AddWithValue("conflict", (int)WorkspaceMutationReceiptState.Conflict);
            command.Parameters.AddWithValue("unknown", (int)WorkspaceMutationReceiptState.Unknown);
            using var reader = command.ExecuteReader(); var values = new List<WorkspaceMutationReceipt>();
            while (reader.Read()) values.Add(new WorkspaceMutationReceipt(workspace, reader.GetString(1),
                ToolCallId.Parse(reader.GetString(0)), runId, reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : DeserializeRef(reader.GetString(6)),
                reader.IsDBNull(7) ? null : DeserializeRef(reader.GetString(7)), reader.GetInt32(8), reader.GetInt32(9),
                (WorkspaceMutationReceiptState)reader.GetInt32(10)));
            return values;
        }
    }

    private void UpsertState(Microsoft.Data.Sqlite.SqliteTransaction tx, string workspace, string key, string relativePath,
        WorkspaceFileHistoryState state, string? expectedHash, string? imageRef, long revision,
        string? lastReceipt, string? observedHash)
    {
        using var command = _connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO workspace_file_history(workspace_id,path_key,relative_path,state,expected_hash,image_ref,revision,last_receipt,observed_hash) "
            + "VALUES(:workspace,:key,:path,:state,:hash,:image,:revision,:receipt,:observed) ON CONFLICT(workspace_id,path_key) DO UPDATE SET "
            + "relative_path=excluded.relative_path,state=excluded.state,expected_hash=excluded.expected_hash,image_ref=excluded.image_ref,"
            + "revision=excluded.revision,last_receipt=excluded.last_receipt,observed_hash=excluded.observed_hash";
        command.Parameters.AddWithValue("workspace", workspace); command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("path", relativePath); command.Parameters.AddWithValue("state", (int)state);
        AddNullable(command, "hash", expectedHash); AddNullable(command, "image", imageRef);
        command.Parameters.AddWithValue("revision", revision); AddNullable(command, "receipt", lastReceipt);
        AddNullable(command, "observed", observedHash); command.ExecuteNonQuery();
    }

    private bool ReceiptMatches(WorkspaceMutationReceipt receipt, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        using var command = _connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT run_id,turn_id,operation,expected_pre_hash,expected_post_hash,preimage_ref,postimage_ref,deleted_lines,inserted_lines,state "
            + "FROM workspace_mutation_receipts WHERE workspace_id=:workspace AND tool_call_id=:call AND path_key=:key";
        command.Parameters.AddWithValue("workspace", receipt.WorkspaceId);
        command.Parameters.AddWithValue("call", receipt.ToolCallId.ToString());
        command.Parameters.AddWithValue("key", PathIdentity(receipt.RelativePath).Key);
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetString(0) == receipt.RunId && reader.GetString(1) == receipt.TurnId
            && reader.GetString(2) == receipt.Operation && ReadNullable(reader, 3) == receipt.ExpectedPreHash
            && ReadNullable(reader, 4) == receipt.ExpectedPostHash
            && ReadNullable(reader, 5) == SerializeRef(receipt.PreimageRef)
            && ReadNullable(reader, 6) == SerializeRef(receipt.PostimageRef)
            && reader.GetInt32(7) == receipt.DeletedLines
            && reader.GetInt32(8) == receipt.InsertedLines
            && reader.GetInt32(9) == (int)WorkspaceMutationReceiptState.Prepared;
    }

    private bool ReceiptMatchesFinal(WorkspaceMutationReceipt receipt, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        using var command = _connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT state,run_id,turn_id,operation,expected_pre_hash,expected_post_hash,preimage_ref,postimage_ref,deleted_lines,inserted_lines "
            + "FROM workspace_mutation_receipts WHERE workspace_id=:workspace AND tool_call_id=:call AND path_key=:key";
        command.Parameters.AddWithValue("workspace", receipt.WorkspaceId);
        command.Parameters.AddWithValue("call", receipt.ToolCallId.ToString()); command.Parameters.AddWithValue("key", PathIdentity(receipt.RelativePath).Key);
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt32(0) == (int)receipt.State
            && reader.GetString(1) == receipt.RunId && reader.GetString(2) == receipt.TurnId
            && reader.GetString(3) == receipt.Operation && ReadNullable(reader, 4) == receipt.ExpectedPreHash
            && ReadNullable(reader, 5) == receipt.ExpectedPostHash
            && ReadNullable(reader, 6) == SerializeRef(receipt.PreimageRef)
            && ReadNullable(reader, 7) == SerializeRef(receipt.PostimageRef)
            && reader.GetInt32(8) == receipt.DeletedLines && reader.GetInt32(9) == receipt.InsertedLines;
    }

    private bool ReceiptAlreadyFinalUnchanged(WorkspaceMutationReceipt receipt, WorkspaceFileHistoryEntry expected,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        if (receipt.State is not (WorkspaceMutationReceiptState.Applied or WorkspaceMutationReceiptState.Conflict
            or WorkspaceMutationReceiptState.Unknown)) return false;
        using var command = _connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT state,expected_hash,image_ref,revision,last_receipt FROM workspace_file_history "
            + "WHERE workspace_id=:workspace AND path_key=:key";
        command.Parameters.AddWithValue("workspace", expected.WorkspaceId);
        command.Parameters.AddWithValue("key", PathIdentity(expected.RelativePath).Key);
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt32(0) == (int)expected.State
            && ReadNullable(reader, 1) == expected.ExpectedHash
            && ReadNullable(reader, 2) == SerializeRef(expected.ImageRef)
            && reader.GetInt64(3) == expected.Revision
            && ReadNullable(reader, 4) == expected.LastReceipt?.ToString();
    }

    private void ValidateReceipt(WorkspaceMutationReceipt receipt)
    {
        if (receipt is null || receipt.WorkspaceId.Length != 64 || !IsHash(receipt.WorkspaceId)
            || string.IsNullOrWhiteSpace(receipt.RunId) || string.IsNullOrWhiteSpace(receipt.TurnId)
            || string.IsNullOrWhiteSpace(receipt.Operation) || receipt.DeletedLines < 0 || receipt.InsertedLines < 0
            || receipt.ExpectedPreHash is not null && !IsHash(receipt.ExpectedPreHash)
            || receipt.ExpectedPostHash is not null && !IsHash(receipt.ExpectedPostHash)
            || receipt.PreimageRef is not null && !IsValidImageRef(receipt.PreimageRef)
            || receipt.PostimageRef is not null && !IsValidImageRef(receipt.PostimageRef))
            throw new InvalidDataException("Workspace mutation receipt is malformed.");
        _ = PathIdentity(receipt.RelativePath);
    }

    private bool TryVerifyReceiptPreimage(WorkspaceMutationReceipt receipt) =>
        receipt.ExpectedPreHash is null && receipt.PreimageRef is null
        || TryReadFaithfulPreimage(receipt.PreimageRef, receipt.ExpectedPreHash, out _);

    /// <summary>Verifies both the CAS blob and the exact file bytes described inside the
    /// preimage document. A structurally valid ArtifactRef alone cannot authorize a tombstone.</summary>
    private bool TryReadFaithfulPreimage(ArtifactRef? reference, string? expectedHash, out string? imageHash)
    {
        imageHash = null;
        if (reference is null || !IsValidImageRef(reference)
            || reference.MediaType != "application/vnd.omnicore.filesystem-preimage+json"
            || !_artifacts.Verify(reference.Hash, reference.Size)) return false;
        try
        {
            var json = _artifacts.GetText(reference.Hash);
            if (json is null || Encoding.UTF8.GetByteCount(json) != reference.Size) return false;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return false;
            if (names.Count != 6
                || !root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var versionValue) || versionValue != 1
                || !root.TryGetProperty("Exists", out var exists) || exists.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("Encoding", out var encoding) || !encoding.TryGetInt32(out var encodingValue)
                || encodingValue is < 0 or > 3
                || !root.TryGetProperty("Text", out var text) || text.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("OriginalHash", out var originalHash) || originalHash.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("ByteLength", out var byteLength) || !byteLength.TryGetInt64(out var length))
                return false;
            var body = text.GetString()!;
            var bytes = EncodePreimage(body, encodingValue);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (length != bytes.LongLength || !IsHash(hash)
                || !string.Equals(originalHash.GetString(), hash, StringComparison.Ordinal)
                || expectedHash is not null && !string.Equals(expectedHash, hash, StringComparison.Ordinal))
                return false;
            imageHash = hash;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException
            or DecoderFallbackException or EncoderFallbackException)
        { return false; }
    }

    private static byte[] EncodePreimage(string text, int encoding) => encoding switch
    {
        0 => new UTF8Encoding(false, true).GetBytes(text),
        1 => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)],
        2 => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)],
        3 => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)],
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    internal static bool IsValidImageRef(ArtifactRef reference) => !reference.Redacted
        && reference.MediaType is "application/vnd.omnicore.filesystem-preimage+json" or "application/vnd.omnicore.filesystem-postimage+json"
        && reference.Kind == ArtifactKind.Other && reference.Sensitivity == Sensitivity.Sensitive
        && reference.Size is > 0 and <= 2 * 1024 * 1024 && IsHash(reference.Hash.Value);

    private static string WorkspaceId(string root)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) canonical = canonical.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static (string Key, string Relative) PathIdentity(string relativePath, string? workspaceRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string normalized;
        if (workspaceRoot is not null)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            var full = Path.GetFullPath(relativePath, root);
            var relative = Path.GetRelativePath(root, full);
            if (relative is "." || Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Workspace history path escapes its physical root.", nameof(relativePath));
            normalized = relative.Replace('\\', '/');
        }
        else normalized = relativePath.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized)
            || parts.Any(part => part is "" or "." or "..") || normalized.Contains(':'))
            throw new ArgumentException("Workspace history requires a normalized relative file path.", nameof(relativePath));
        var keyPath = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? normalized.ToUpperInvariant() : normalized;
        return (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyPath))), normalized);
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string? ReadNullable(System.Data.Common.DbDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static void AddNullable(System.Data.Common.DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value is null ? DBNull.Value : value;
        command.Parameters.Add(parameter);
    }
    private static string? SerializeRef(ArtifactRef? value) => value is null ? null :
        JsonSerializer.Serialize(value, WorkspaceHistoryJsonContext.Default.ArtifactRef);
    private static ArtifactRef DeserializeRef(string json) =>
        JsonSerializer.Deserialize(json, WorkspaceHistoryJsonContext.Default.ArtifactRef)
            ?? throw new InvalidDataException("Workspace mutation artifact reference is invalid.");

    private void ValidateSchema()
    {
        ValidateTableType(_connection, "workspace_mutation_schema");
        ValidateTableType(_connection, "workspace_file_history");
        ValidateTableType(_connection, "workspace_mutation_receipts");
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT version FROM workspace_mutation_schema";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 1 || reader.Read())
            throw new InvalidDataException("Workspace mutation history schema marker is invalid.");
        reader.Close();
        ValidateColumns("workspace_file_history", ["workspace_id", "path_key", "relative_path", "state",
            "expected_hash", "image_ref", "revision", "last_receipt", "observed_hash"]);
        ValidateColumns("workspace_mutation_receipts", ["workspace_id", "tool_call_id", "path_key", "relative_path",
            "run_id", "turn_id", "operation", "expected_pre_hash", "expected_post_hash", "preimage_ref",
            "postimage_ref", "deleted_lines", "inserted_lines", "state"]);
    }

    private void ValidateColumns(string table, string[] expected)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(" + table + ")";
        using var reader = command.ExecuteReader();
        var actual = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) actual.Add(reader.GetString(1));
        if (!actual.SetEquals(expected))
            throw new InvalidDataException("Workspace mutation history table declaration is invalid: " + table + ".");
    }

    internal static IReadOnlyList<ArtifactRef> ReadGarbageCollectionReferences(string dataDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(dataDirectory), "journal.db");
        if (!File.Exists(path)) return [];
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false,
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        connection.Open();
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT type,name FROM sqlite_master WHERE name IN "
            + "('workspace_mutation_schema','workspace_file_history','workspace_mutation_receipts')";
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var declarationReader = exists.ExecuteReader())
            while (declarationReader.Read()) declarations[declarationReader.GetString(1)] = declarationReader.GetString(0);
        if (!declarations.ContainsKey("workspace_mutation_schema"))
        {
            if (declarations.Count != 0)
                throw new InvalidDataException("Workspace mutation history tables exist without a schema marker.");
            return [];
        }
        if (!declarations.TryGetValue("workspace_file_history", out var historyType)
            || !declarations.TryGetValue("workspace_mutation_receipts", out var receiptsType)
            || declarations["workspace_mutation_schema"] != "table" || historyType != "table" || receiptsType != "table")
            throw new InvalidDataException("Workspace mutation history schema is corrupt.");
        foreach (var (table, columns) in new[]
        {
            ("workspace_file_history", new[] { "workspace_id", "path_key", "relative_path", "state", "expected_hash", "image_ref", "revision", "last_receipt", "observed_hash" }),
            ("workspace_mutation_receipts", new[] { "workspace_id", "tool_call_id", "path_key", "relative_path", "run_id", "turn_id", "operation", "expected_pre_hash", "expected_post_hash", "preimage_ref", "postimage_ref", "deleted_lines", "inserted_lines", "state" }),
        })
        {
            using var declaration = connection.CreateCommand(); declaration.CommandText = "PRAGMA table_info(" + table + ")";
            using var reader = declaration.ExecuteReader();
            var actual = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) actual.Add(reader.GetString(1));
            if (!actual.SetEquals(columns)) throw new InvalidDataException("Workspace mutation history schema is corrupt.");
        }
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT version FROM workspace_mutation_schema";
            using var versionReader = version.ExecuteReader();
            if (!versionReader.Read() || versionReader.GetInt32(0) != 1 || versionReader.Read())
                throw new InvalidDataException("Workspace mutation history schema version is invalid.");
        }
        using (var invalidRoots = connection.CreateCommand())
        {
            invalidRoots.CommandText = "SELECT EXISTS(SELECT 1 FROM workspace_file_history WHERE state=:tombstone "
                + "AND (expected_hash IS NULL OR image_ref IS NULL)) OR EXISTS(SELECT 1 FROM workspace_mutation_receipts "
                + "WHERE expected_pre_hash IS NOT NULL AND preimage_ref IS NULL)";
            invalidRoots.Parameters.AddWithValue("tombstone", (int)WorkspaceFileHistoryState.Tombstone);
            if (Convert.ToInt32(invalidRoots.ExecuteScalar()) != 0)
                throw new InvalidDataException("Workspace mutation history is missing a required preimage root.");
        }
        using var refs = connection.CreateCommand();
        refs.CommandText = "SELECT image_ref FROM workspace_file_history WHERE image_ref IS NOT NULL "
            + "UNION ALL SELECT preimage_ref FROM workspace_mutation_receipts WHERE preimage_ref IS NOT NULL "
            + "UNION ALL SELECT postimage_ref FROM workspace_mutation_receipts WHERE postimage_ref IS NOT NULL";
        using var refReader = refs.ExecuteReader();
        var output = new List<ArtifactRef>();
        while (refReader.Read())
        {
            ArtifactRef value;
            try { value = DeserializeRef(refReader.GetString(0)); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            { throw new InvalidDataException("Workspace mutation history contains a corrupt artifact reference.", ex); }
            if (!IsValidImageRef(value)) throw new InvalidDataException("Workspace mutation history contains an unsafe artifact reference.");
            output.Add(value);
        }
        return output;
    }

    private static void ValidateTableType(Microsoft.Data.Sqlite.SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name=:name";
        command.Parameters.AddWithValue("name", name);
        if (!string.Equals(command.ExecuteScalar() as string, "table", StringComparison.Ordinal))
            throw new InvalidDataException("Workspace mutation history declaration is not a table: " + name + ".");
    }

    public void Dispose() => _connection.Dispose();

    private sealed class Fence(FileStream stream) : IDisposable { public void Dispose() => stream.Dispose(); }
}

[JsonSerializable(typeof(ArtifactRef))]
internal partial class WorkspaceHistoryJsonContext : JsonSerializerContext;
