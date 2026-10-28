namespace OmniCore.Host;

using System.Globalization;
using System.Text.Json;
using OmniCore.Protocol;

/// <summary>Durable, atomic state for the last update operation. Never touches user data outside its folder.</summary>
public sealed class ReleaseUpdateStateStore
{
    public const int SchemaVersion = 1;
    private readonly string _statePath;
    private readonly string _lockPath;

    public ReleaseUpdateStateStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        UpdatesDirectory = Path.Combine(dataDirectory, "updates");
        _statePath = Path.Combine(UpdatesDirectory, "state.json");
        _lockPath = Path.Combine(UpdatesDirectory, "state.lock");
    }

    public static ReleaseUpdateStateStore CreateDefault() => new(OmniHost.CreatePlatformPaths().DataDirectory);

    public string UpdatesDirectory { get; }

    public string StatePath => _statePath;

    public string StagingDirectory(string version) =>
        Path.Combine(UpdatesDirectory, "staging", SafeSegment(version));

    public string BackupDirectory(string version, string? operationId = null)
    {
        var versionDirectory = Path.Combine(UpdatesDirectory, "backup", SafeSegment(version));
        return operationId is null ? versionDirectory : Path.Combine(versionDirectory, SafeSegment(operationId));
    }

    public bool TryRead(out ReleaseUpdateState? state, out string? diagnostic)
    {
        state = null;
        diagnostic = null;
        if (!File.Exists(_statePath)) return true;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(_statePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != SchemaVersion
                || !root.TryGetProperty("operation", out var operationValue)
                || !Enum.TryParse<ReleaseUpdateOperation>(operationValue.GetString(), ignoreCase: false, out var operation)
                || !root.TryGetProperty("status", out var statusValue)
                || !Enum.TryParse<ReleaseUpdateOperationStatus>(statusValue.GetString(), ignoreCase: false, out var status)
                || !root.TryGetProperty("updatedAtUtc", out var updatedValue)
                || !DateTimeOffset.TryParse(updatedValue.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var updatedAt))
            {
                diagnostic = "update_state_invalid";
                return false;
            }
            state = new(SchemaVersion, operation, status,
                Optional(root, "fromVersion"), Optional(root, "toVersion"), Optional(root, "assetPath"),
                Optional(root, "backupPath"), OptionalInt(root, "parentPid"), OptionalInt(root, "helperPid"),
                updatedAt, Optional(root, "detail"), Optional(root, "operationId"), Optional(root, "expectedSha256"),
                OptionalLong(root, "expectedSize"), Optional(root, "npmPrefix"));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostic = "update_state_unreadable";
            return false;
        }
    }

    public void Write(ReleaseUpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(UpdatesDirectory);
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", SchemaVersion);
                writer.WriteString("operation", state.Operation.ToString());
                writer.WriteString("status", state.Status.ToString());
                WriteOptional(writer, "fromVersion", state.FromVersion);
                WriteOptional(writer, "toVersion", state.ToVersion);
                WriteOptional(writer, "assetPath", state.AssetPath);
                WriteOptional(writer, "backupPath", state.BackupPath);
                if (state.ParentPid is { } parent) writer.WriteNumber("parentPid", parent);
                if (state.HelperPid is { } helper) writer.WriteNumber("helperPid", helper);
                writer.WriteString("updatedAtUtc", state.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                WriteOptional(writer, "detail", state.Detail);
                WriteOptional(writer, "operationId", state.OperationId);
                WriteOptional(writer, "expectedSha256", state.ExpectedSha256);
                if (state.ExpectedSize is { } expectedSize) writer.WriteNumber("expectedSize", expectedSize);
                WriteOptional(writer, "npmPrefix", state.NpmPrefix);
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public IDisposable AcquireLock(TimeSpan timeout)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new LockHandle(new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None), _lockPath);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                throw new TimeoutException("update_operation_in_progress");
            }
        }
    }

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var clean = new string(value.Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-' ? character : '_').ToArray());
        return clean.Length is 0 or > 120 ? "unknown" : clean;
    }

    private static string? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? OptionalInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : null;

    private static long? OptionalLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) ? number : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private sealed class LockHandle(FileStream stream, string path) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
