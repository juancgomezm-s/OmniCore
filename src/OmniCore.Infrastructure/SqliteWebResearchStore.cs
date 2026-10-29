namespace OmniCore.Infrastructure;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;

/// <summary>Atomic controller checkpoints shared by every lane/process in a workspace Run.</summary>
public sealed class SqliteWebResearchStore : IWebResearchStore
{
    private readonly string _connectionString;

    public SqliteWebResearchStore(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = 10,
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS web_research_runs (
                run_id TEXT PRIMARY KEY, state_json TEXT NOT NULL, revision INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public WebResearchState? Read(string runId)
    {
        using var connection = Open();
        return Read(connection, null, runId);
    }

    public WebResearchState Initialize(WebResearchState initial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initial.RunId);
        initial.Limits.Validate();
        if (initial.Questions.Count is < 1 or > 16
            || initial.Questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count() != initial.Questions.Count
            || initial.Questions.Any(question => string.IsNullOrWhiteSpace(question.Id) || string.IsNullOrWhiteSpace(question.Text))
            || initial.Deadline <= initial.StartedAt || initial.Status != WebResearchStatus.Active)
            throw new ArgumentException("Invalid initial research plan.", nameof(initial));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = Read(connection, transaction, initial.RunId);
        if (existing is not null) return existing; // Never reset counters, questions, limits or deadline.
        Write(connection, transaction, initial, insert: true);
        transaction.Commit();
        return initial;
    }

    public WebResearchState Update(string runId, Action<WebResearchState> mutation)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var state = Read(connection, transaction, runId)
            ?? throw new InvalidOperationException("Research Run has not been initialized.");
        var status = state.Status;
        var before = Counters(state);
        mutation(state);
        if (status != WebResearchStatus.Active && state.Status != status)
            throw new InvalidOperationException("Research terminal states are irreversible.");
        var after = Counters(state);
        if (before.Zip(after).Any(pair => pair.Second < pair.First))
            throw new InvalidOperationException("Research accounting cannot be reset or refunded.");
        if (state.QueriesUsed > state.Limits.Queries || state.ReadsUsed > state.Limits.Reads
            || state.AttemptsUsed > state.Limits.Attempts || state.RoundsUsed > state.Limits.Rounds
            || state.RenderingsUsed > state.Limits.Renderings || state.OcrOperationsUsed > state.Limits.OcrOperations
            || state.EvidenceTokensUsed + state.CachedContextTokensUsed > state.Limits.EvidenceTokens)
            throw new InvalidOperationException("Research reservation exceeds a shared Run ceiling.");
        Write(connection, transaction, state, insert: false);
        transaction.Commit();
        return state;
    }

    private static long[] Counters(WebResearchState state) =>
        [state.QueriesUsed, state.ReadsUsed, state.AttemptsUsed, state.RoundsUsed,
            state.RenderingsUsed, state.OcrOperationsUsed, state.EvidenceTokensUsed, state.CachedContextTokensUsed,
            state.DeliveredToolCalls.Count, state.ContextDeliveries.Count];

    private static WebResearchState? Read(SqliteConnection connection, SqliteTransaction? transaction, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state_json FROM web_research_runs WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", runId);
        if (command.ExecuteScalar() is not string json) return null;
        if (json.Length > 16 * 1024 * 1024) throw new InvalidDataException("Research checkpoint exceeds its bound.");
        var state = JsonSerializer.Deserialize(json, WebResearchStoreJson.Default.WebResearchState)
            ?? throw new InvalidDataException("Research checkpoint is unavailable.");
        if (state.RunId != runId) throw new InvalidDataException("Research checkpoint identity changed.");
        return state;
    }

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, WebResearchState state, bool insert)
    {
        var json = JsonSerializer.Serialize(state, WebResearchStoreJson.Default.WebResearchState);
        if (json.Length > 16 * 1024 * 1024) throw new InvalidDataException("Research checkpoint exceeds its bound.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insert
            ? "INSERT INTO web_research_runs(run_id,state_json,revision) VALUES($run,$json,1)"
            : "UPDATE web_research_runs SET state_json=$json,revision=revision+1 WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", state.RunId);
        command.Parameters.AddWithValue("$json", json);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}

[JsonSerializable(typeof(WebResearchState))]
internal partial class WebResearchStoreJson : JsonSerializerContext;
