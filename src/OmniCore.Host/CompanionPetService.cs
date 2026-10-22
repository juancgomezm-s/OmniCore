using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed record CompanionPetOptions(int HatchAfter = 20, int RewardCooldownSeconds = 30,
    int DailyRewardsPerKind = 40, int PetCooldownSeconds = 5,
    Dictionary<string, string>? InteractionPatterns = null);

internal sealed class CompanionPetData
{
    public long Revision { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; }
    public DateTimeOffset LastActivity { get; set; }
    public DateTimeOffset LastPet { get; set; }
    public string Name { get; set; } = "Omni";
    public string? Species { get; set; }
    public string Variant { get; set; } = "warm.spots-0";
    public int Xp { get; set; }
    public int Mood { get; set; } = 70;
    public int Interactions { get; set; }
    public int HatchAfter { get; set; }
    public string RulesVersion { get; set; } = CompanionInteractionRules.Version;
    public Dictionary<string, int> Signals { get; set; } = new();
    public Dictionary<string, CompanionClientPreferences> Clients { get; set; } = new();
}

/// <summary>One local profile, shared across workspaces/processes. SQLite IMMEDIATE transactions
/// commit aggregate state, deduplication receipts and projection cursors together.</summary>
public sealed class CompanionPetService
{
    private readonly string _file;
    private readonly CompanionPetOptions _options;
    private readonly CompanionInteractionRules _rules;
    private readonly string _rulesVersion;
    private readonly Func<double> _random;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _reactionGate = new();
    private CompanionReaction? _reaction;
    public CompanionPetService(string profileDataDirectory, CompanionPetOptions? options = null,
        Func<double>? random = null, Func<DateTimeOffset>? clock = null)
    {
        _file = Path.Combine(Path.GetFullPath(profileDataDirectory), "companion", "pet.db");
        _options = options ?? new();
        if (_options.HatchAfter is < 1 or > 10000 || _options.RewardCooldownSeconds is < 0 or > 86400
            || _options.DailyRewardsPerKind is < 1 or > 1000 || _options.PetCooldownSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(options));
        _rules = new(_options.InteractionPatterns);
        _rulesVersion = CompanionInteractionRules.Version + ":" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", (_options.InteractionPatterns
                ?? CompanionInteractionRules.DefaultPatterns).OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)))))[..12];
        _random = random ?? (() => RandomNumberGenerator.GetInt32(1 << 24) / (double)(1 << 24));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }
    internal static CompanionPetOptions ReadOptions(string profileDataDirectory)
    {
        var path = Path.Combine(profileDataDirectory, "companion", "options.json");
        try
        {
            var options = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path),
                CompanionStoreJson.Default.CompanionPetOptions) ?? new() : new();
            _ = new CompanionPetService(profileDataDirectory, options); // validate without creating storage
            return options;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        { return new(); }
    }
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _file, DefaultTimeout = 10 }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS pet (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS receipts (id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS cursors (id TEXT PRIMARY KEY, seq INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS rewards (kind TEXT PRIMARY KEY, day TEXT NOT NULL, count INTEGER NOT NULL, at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        return db;
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql, params object[] values)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i]);
        return cmd;
    }
    private static CompanionPetData? Read(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = Command(db, tx, "SELECT json FROM pet WHERE id=1");
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize(json, CompanionStoreJson.Default.CompanionPetData) : null;
    }
    private static void Write(SqliteConnection db, SqliteTransaction tx, CompanionPetData state)
    {
        state.Revision++;
        using var cmd = Command(db, tx, "INSERT INTO pet VALUES(1,$p0) ON CONFLICT(id) DO UPDATE SET json=excluded.json",
            JsonSerializer.Serialize(state, CompanionStoreJson.Default.CompanionPetData));
        cmd.ExecuteNonQuery();
    }
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Receipt(SqliteConnection db, SqliteTransaction tx, string identity)
    {
        using var cmd = Command(db, tx, "INSERT OR IGNORE INTO receipts VALUES($p0)", Digest(identity));
        return cmd.ExecuteNonQuery() == 1;
    }
    private static void ValidateClient(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Invalid companion client id.");
    }
    public static string ValidateName(string? name)
    {
        var value = name?.Trim().Normalize(NormalizationForm.FormC) ?? "";
        if (value.Length is < 1 or > 24 || value.Any(c => !char.IsLetterOrDigit(c) && c is not (' ' or '-' or '_')))
            throw new ArgumentException("Name must contain 1–24 letters, digits, spaces, - or _.");
        return value;
    }
    // XP/mood/level rules adapted from cc-arcade (MIT, Seza Akgün, 2026).
    // Full attribution and license: docs/third-party/cc-arcade-LICENSE.txt.
    private static int MoodAt(CompanionPetData state, DateTimeOffset now) => state.Mood <= 30 ? state.Mood
        : Math.Max(30, state.Mood - (int)Math.Clamp((now - state.LastActivity).TotalMinutes / 10, 0, 100));
    private CompanionPetSnapshot Snapshot(CompanionPetData? state, string clientId)
    {
        if (state is null) return new(false, 0, null, null, "egg.v1", "warm.spots-0", 0, 0, 70, "egg", 0,
            _options.HatchAfter, _rulesVersion, new());
        var level = state.Species is null ? 0 : Math.Max(1, (int)Math.Floor(Math.Sqrt(state.Xp / 10d)));
        var stage = state.Species is null ? "egg" : level >= 10 ? "legendary" : level >= 6 ? "adult" : level >= 3 ? "young" : "baby";
        CompanionReaction? reaction;
        lock (_reactionGate) reaction = _reaction?.Until > _clock() ? _reaction : null;
        return new(true, state.Revision, state.Name, state.Species, (state.Species ?? "egg") + ".v1", state.Variant,
            state.Xp, level, MoodAt(state, _clock()), stage, state.Interactions, state.HatchAfter, state.RulesVersion,
            state.Clients.GetValueOrDefault(clientId) ?? new(), reaction);
    }
    public CompanionPetSnapshot Status(string clientId = "terminal")
    {
        ValidateClient(clientId);
        if (!File.Exists(_file)) return Snapshot(null, clientId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        return Snapshot(Read(db, tx), clientId);
    }
    public CompanionPetSnapshot Act(CompanionPetAction action, string identity)
    {
        ValidateClient(action.ClientId);
        if (action.Action is not ("discover" or "status" or "pet" or "rename" or "show" or "hide" or "preferences"))
            throw new ArgumentException("Unknown companion action.");
        var name = action.Action == "rename" ? ValidateName(action.Name) : null;
        if (action.Action == "status") return Status(action.ClientId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var state = Read(db, tx); var now = _clock(); var changed = false;
        if (state is null)
        {
            if (action.Action != "discover") throw new InvalidOperationException("Discover the companion first with /pet.");
            state = new() { DiscoveredAt = now, LastActivity = now, HatchAfter = _options.HatchAfter, RulesVersion = _rulesVersion };
            changed = true;
        }
        var petted = false;
        if (Receipt(db, tx, "action/" + identity))
        {
            var preferences = state.Clients.GetValueOrDefault(action.ClientId) ?? new();
            switch (action.Action)
            {
                case "rename": changed |= state.Name != name; state.Name = name!; break;
                case "pet" when now - state.LastPet >= TimeSpan.FromSeconds(_options.PetCooldownSeconds):
                    state.LastPet = now; changed = petted = true; break;
                case "show": case "hide": case "preferences":
                    var next = preferences with { Visible = action.Action == "show" || action.Action != "hide" && preferences.Visible,
                        ReducedMotion = action.ReducedMotion ?? preferences.ReducedMotion };
                    changed |= next != preferences; state.Clients[action.ClientId] = next; break;
            }
        }
        if (changed) Write(db, tx, state);
        tx.Commit();
        if (petted) React("pet", now, 3, 2);
        return Snapshot(state, action.ClientId);
    }
    private void React(string animation, DateTimeOffset at, int priority, int seconds)
    {
        // Replaying old history never replays a reaction. No frame or animation state is durable.
        var now = _clock();
        if (at > now || now - at > TimeSpan.FromSeconds(seconds)) return;
        lock (_reactionGate)
            if (_reaction is null || _reaction.Until <= now || priority >= _reaction.Priority)
                _reaction = new(animation, at.AddSeconds(seconds), priority);
    }
    private bool Reward(SqliteConnection db, SqliteTransaction tx, CompanionPetData state, string kind,
        string identity, DateTimeOffset at)
    {
        if (!Receipt(db, tx, "reward/" + identity)) return false;
        if (state.Species is null) return false;
        var day = at.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using var read = Command(db, tx, "SELECT day,count,at FROM rewards WHERE kind=$p0", kind);
        using var row = read.ExecuteReader();
        var count = 0;
        if (row.Read())
        {
            count = row.GetString(0) == day ? row.GetInt32(1) : 0;
            if (at - DateTimeOffset.Parse(row.GetString(2), System.Globalization.CultureInfo.InvariantCulture) < TimeSpan.FromSeconds(_options.RewardCooldownSeconds)) return false;
        }
        row.Close();
        if (count >= _options.DailyRewardsPerKind) return false;
        var (xp, mood) = kind switch { "test-pass" => (10, 8), "test-fail" => (2, -12), "commit" => (15, 10), "edit" => (1, 1), _ => (0, 0) };
        if (xp == 0) return false;
        state.Xp = (int)Math.Min(int.MaxValue, (long)state.Xp + xp);
        state.Mood = Math.Clamp(MoodAt(state, at) + mood, 0, 100); state.LastActivity = at;
        using var save = Command(db, tx, "INSERT INTO rewards VALUES($p0,$p1,$p2,$p3) ON CONFLICT(kind) DO UPDATE SET day=excluded.day,count=excluded.count,at=excluded.at",
            kind, day, count + 1, at.ToString("O")); save.ExecuteNonQuery();
        return true;
    }
    /// <summary>Pull projection of Core's existing journal, including recovery. Never emits new journal events.</summary>
    public void Synchronize(IEventStore journal, IEventCodecRegistry codecs, SessionId session)
    {
        if (!File.Exists(_file)) return;
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var state = Read(db, tx); if (state is null) return;
        using var cursor = Command(db, tx, "SELECT seq FROM cursors WHERE id=$p0", session.ToString());
        var after = cursor.ExecuteScalar() is long value ? value : 0;
        var tail = journal.ReadFrom(session, after + 1); if (tail.Count == 0) return;
        var changed = false;
        // Exact pre/start metadata may precede the cursor; it is read only when an outcome needs it.
        IReadOnlyList<DomainEvent>? history = null;
        var reactions = new List<(string Id, DateTimeOffset At, int Priority, int Seconds)>();
        foreach (var envelope in tail)
        {
            if (envelope.Timestamp <= state.DiscoveredAt || !Receipt(db, tx, "event/" + envelope.EventId)) continue;
            var payload = codecs.Decode(envelope);
            var message = payload switch
            {
                UserInputReceived input => (Text: input.InputPartsJson, Origin: input.Origin),
                FollowUpQueued input => (Text: input.InputPartsJson, Origin: input.Origin),
                TurnSteeringReceived input => (Text: input.InputPartsJson, Origin: input.Origin),
                _ => (Text: (string?)null, Origin: (string?)null),
            };
            // A promoted queued input retains its original text and origin. Count receipt at
            // enqueue, never the synthetic UserInputReceived immediately after its promotion.
            var promoted = payload is UserInputReceived && journal.ReadFrom(session, Math.Max(1, envelope.Sequence - 1))
                .FirstOrDefault()?.Type.ToString() == "followup.promoted";
            var messageIdentity = envelope.Causation is CommandCausation cause ? "command/" + cause.CommandId
                : payload is FollowUpQueued queued ? "followup/" + queued.FollowUpId
                : payload is TurnSteeringReceived steering ? "steering/" + steering.SteeringId : "event/" + envelope.EventId;
            if (state.Species is null && state.RulesVersion == _rulesVersion && message.Text is not null && !promoted
                && (message.Origin is null || message.Origin is "User" or "user" or "Human" or "human")
                && Receipt(db, tx, "message/" + messageIdentity))
            {
                var classification = _rules.Classify(InputText(message.Text));
                if (classification.Valid)
                {
                    state.Interactions++;
                    if (classification.Signal is { } signal) state.Signals[signal] = state.Signals.GetValueOrDefault(signal) + 1;
                    if (state.Interactions >= state.HatchAfter)
                    {
                        state.Species = CompanionInteractionRules.Draw(CompanionInteractionRules.Weights(state.Signals, state.Interactions), _random());
                        state.Variant = (_random() < .5 ? "warm" : "cool") + ".spots-" + (int)(_random() * 3);
                    }
                    changed = true;
                }
            }
            var callId = payload switch { ToolCallSucceeded s => s.ToolCallId, ToolCallFailed f => f.ToolCallId, _ => (ToolCallId?)null };
            if (callId is { } call)
            {
                history ??= journal.ReadFrom(session, 1);
                var start = history.Where(e => e.Sequence < envelope.Sequence).Select(codecs.Decode).OfType<ToolCallStarted>().LastOrDefault(s => s.ToolCallId == call);
                var evidence = payload switch { ToolCallSucceeded s => s.ActivityEvidence, ToolCallFailed f => f.ActivityEvidence, _ => null };
                string? reward = null; var rewardId = call.ToString();
                if (start?.Attribution is { SourceKind: "BuiltIn" } attribution)
                {
                    if (payload is ToolCallSucceeded { AfterStateRef: not null } && start.BeforeStateRef is not null
                        && attribution.ToolId is "filesystem.patch" or "filesystem.write"
                        && OmniCore.Tools.FilesystemReconciliationMetadata.Parse(start.ReconciliationJson) is { ExpectedPostHash: not null } metadata
                        && metadata.ExpectedPreHash != metadata.ExpectedPostHash)
                        reward = "edit";
                    else if (attribution.ToolId is "process.exec" or "shell.exec" && evidence is { Verified: true })
                    {
                        if (evidence.Kind == "test" && evidence.Passed >= 0 && evidence.Failed >= 0 && (long)evidence.Passed + evidence.Failed > 0
                            && evidence.Completed)
                            reward = payload is ToolCallSucceeded && evidence.ExitCode == 0 && evidence.Failed == 0 ? "test-pass"
                                : payload is ToolCallFailed failure && failure.ErrorCode == ToolErrorCode.ProcessFailure
                                    && failure.EffectOutcome == EffectOutcome.Partial && evidence.ExitCode == 1 && evidence.Failed > 0
                                    ? "test-fail" : null;
                        if (payload is ToolCallSucceeded && evidence.Kind == "commit" && evidence.Completed && evidence.ExitCode == 0
                            && evidence.BeforeRevision != evidence.AfterRevision && evidence.AfterRevision is { Length: >= 40 })
                        { reward = "commit"; rewardId = "commit/" + evidence.AfterRevision; }
                    }
                }
                if (reward is not null && Reward(db, tx, state, reward, rewardId, envelope.Timestamp)) changed = true;
                reactions.Add((payload is ToolCallFailed ? "failure" : "success", envelope.Timestamp, 2, 2));
            }
        }
        if (changed) Write(db, tx, state);
        using var update = Command(db, tx, "INSERT INTO cursors VALUES($p0,$p1) ON CONFLICT(id) DO UPDATE SET seq=excluded.seq", session.ToString(), tail[^1].Sequence);
        update.ExecuteNonQuery(); tx.Commit();
        foreach (var reaction in reactions) React(reaction.Id, reaction.At, reaction.Priority, reaction.Seconds);
    }
    internal static string InputText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.String) return doc.RootElement.GetString() ?? "";
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return string.Join("\n", doc.RootElement.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString()
                    : p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var text)
                        && (!p.TryGetProperty("type", out var type) || type.GetString() == "text") ? text.GetString() : null));
        }
        catch (JsonException) { }
        return "";
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CompanionPetData))]
[JsonSerializable(typeof(CompanionPetOptions))]
internal partial class CompanionStoreJson : JsonSerializerContext;
