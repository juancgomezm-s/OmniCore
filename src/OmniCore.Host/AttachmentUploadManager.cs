namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Protocol;

/// <summary>Connection-local bounded staging. Only the trusted main-process command path can call it.</summary>
internal sealed class AttachmentUploadManager : IDisposable
{
    public const int MaximumChunkBytes = 512 * 1024;
    private readonly Dictionary<string, Upload> _completed = new(StringComparer.Ordinal);
    private Upload? _active;

    public (CommandAck Ack, ArtifactRef? Reference) Dispatch(OmniServer server, WireEnvelope command, string commandName)
    {
        Expire();
        using var payload = JsonDocument.Parse(command.PayloadJson);
        var root = payload.RootElement;
        if (!root.TryGetProperty("uploadId", out var idValue) || idValue.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(idValue.GetString(), "D", out var id))
            return Reject(command.MessageId, "attachment_upload_invalid");
        var key = id.ToString("D");
        if (commandName == "attachment.upload.begin")
        {
            var filename = root.GetProperty("fileName").GetString()!;
            var size = root.GetProperty("size").GetInt64();
            var hash = root.GetProperty("sha256").GetString()!;
            if (_completed.TryGetValue(key, out var done))
            {
                if (done.Size != size || done.ExpectedHash != hash || done.FileName != filename)
                    return Reject(command.MessageId, "attachment_upload_conflict");
                done.LastActivity = DateTimeOffset.UtcNow;
                return Accepted(command.MessageId, done.Reference);
            }
            if (_active is not null)
                return _active.Id == key && _active.Size == size && _active.ExpectedHash == hash && _active.FileName == filename
                    ? Accepted(command.MessageId) : Reject(command.MessageId, "attachment_upload_busy");
            if (size is <= 0 or > AttachmentContentMaterializer.MaximumBytes || hash.Length != 64
                || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || filename.Length is < 1 or > 180 || Path.GetFileName(filename.Replace('\\', '/')) != filename
                || filename.Any(char.IsControl))
                return Reject(command.MessageId, "attachment_upload_invalid");
            _active = new Upload(key, filename, size, hash);
            return Accepted(command.MessageId);
        }

        if (commandName == "attachment.upload.chunk")
        {
            if (_active is null || _active.Id != key) return Reject(command.MessageId, "attachment_upload_unknown");
            if (!root.TryGetProperty("index", out var indexValue) || !indexValue.TryGetInt32(out var index)
                || !root.TryGetProperty("data", out var dataValue) || dataValue.ValueKind != JsonValueKind.String)
                return Reject(command.MessageId, "attachment_upload_invalid");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(dataValue.GetString()!); }
            catch (FormatException) { return Reject(command.MessageId, "attachment_upload_invalid"); }
            if (bytes.Length is 0 or > MaximumChunkBytes) return Reject(command.MessageId, "attachment_chunk_too_large");
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (index == _active.LastIndex && index >= 0 && digest == _active.LastChunkHash)
                return Accepted(command.MessageId);
            if (index != _active.NextIndex || _active.Buffer.Length + bytes.Length > _active.Size)
                return Reject(command.MessageId, "attachment_chunk_out_of_order");
            _active.Buffer.Write(bytes);
            _active.LastIndex = index;
            _active.NextIndex++;
            _active.LastChunkHash = digest;
            _active.LastActivity = DateTimeOffset.UtcNow;
            return Accepted(command.MessageId);
        }

        if (_completed.TryGetValue(key, out var completed))
        {
            completed.LastActivity = DateTimeOffset.UtcNow;
            return Accepted(command.MessageId, completed.Reference);
        }
        if (commandName != "attachment.upload.finish" || _active is null || _active.Id != key)
            return Reject(command.MessageId, "attachment_upload_unknown");
        var upload = _active;
        var content = upload.Buffer.ToArray();
        if (content.LongLength != upload.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(content)),
                upload.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            return Reject(command.MessageId, "attachment_hash_mismatch");
        ArtifactRef reference;
        try { reference = server.ImportUserAttachment(content, upload.FileName); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
        { return Reject(command.MessageId, "attachment_content_rejected"); }
        reference = reference with { FileName = upload.FileName };
        upload.Reference = reference;
        upload.LastActivity = DateTimeOffset.UtcNow;
        while (_completed.Count >= AttachmentReferences.MaximumCount)
        {
            var oldest = _completed.MinBy(pair => pair.Value.LastActivity).Key;
            _completed[oldest].Dispose();
            _completed.Remove(oldest);
        }
        _completed.Add(key, upload);
        _active = null;
        upload.Buffer.Dispose();
        return Accepted(command.MessageId, reference);
    }

    private void Expire()
    {
        if (_active is not null && DateTimeOffset.UtcNow - _active.LastActivity > TimeSpan.FromMinutes(3))
        { _active.Buffer.Dispose(); _active = null; }
        foreach (var key in _completed.Where(pair => DateTimeOffset.UtcNow - pair.Value.LastActivity > TimeSpan.FromMinutes(3))
                     .Select(pair => pair.Key).ToArray())
        {
            _completed[key].Dispose();
            _completed.Remove(key);
        }
    }

    private static (CommandAck, ArtifactRef?) Accepted(string id, ArtifactRef? reference = null) =>
        (new CommandAck(id, "ok", null, RuntimeCommandOutcome.Accepted()), reference);

    private static (CommandAck, ArtifactRef?) Reject(string id, string code) =>
        (new CommandAck(id, "error", code, RuntimeCommandOutcome.Rejected()), null);

    public void Dispose()
    {
        _active?.Buffer.Dispose();
        _active = null;
        foreach (var item in _completed.Values) item.Buffer.Dispose();
        _completed.Clear();
    }

    private sealed class Upload(string id, string fileName, long size, string expectedHash) : IDisposable
    {
        public string Id { get; } = id;
        public string FileName { get; } = fileName;
        public long Size { get; } = size;
        public string ExpectedHash { get; } = expectedHash;
        public MemoryStream Buffer { get; } = new();
        public int NextIndex { get; set; }
        public int LastIndex { get; set; } = -1;
        public string? LastChunkHash { get; set; }
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
        public ArtifactRef? Reference { get; set; }
        public void Dispose() => Buffer.Dispose();
    }
}
