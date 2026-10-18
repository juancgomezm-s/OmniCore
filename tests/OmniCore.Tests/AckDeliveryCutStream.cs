namespace OmniCore.Tests;

using System.Text.Json;

/// <summary>
/// Test-only transport that delivers one complete JSON-lines ACK to its reader, then
/// fails the corresponding writer call before StdioProtocolHost can schedule work
/// after the ACK. This exercises a real durable ready/no-start recovery window.
/// </summary>
internal sealed class AckDeliveryCutStream(Stream inner, string commandId) : Stream
{
    private readonly MemoryStream _frame = new();
    private bool _targetFrame;
    private bool _failed;

    public string? LastFrameMessageId { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => inner.Flush();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        inner.Write(buffer, offset, count);
        _frame.Write(buffer, offset, count);
    }

    public override void WriteByte(byte value)
    {
        inner.WriteByte(value);
        if (value != (byte)'\n')
        {
            _frame.WriteByte(value);
            return;
        }

        if (!_targetFrame)
        {
            using var json = JsonDocument.Parse(_frame.GetBuffer().AsMemory(0, checked((int)_frame.Length)));
            LastFrameMessageId = json.RootElement.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("ack", out var ack)
                && ack.ValueKind == JsonValueKind.Object
                && ack.TryGetProperty("commandId", out var id)
                && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            _targetFrame = string.Equals(LastFrameMessageId, commandId, StringComparison.Ordinal);
        }
        _frame.SetLength(0);

        if (!_targetFrame || _failed) return;
        _failed = true;
        throw new IOException("Test transport failed after delivering the selected ACK frame.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame.Dispose();
        }
        base.Dispose(disposing);
    }
}
