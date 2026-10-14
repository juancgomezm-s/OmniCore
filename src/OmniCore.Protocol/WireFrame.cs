namespace OmniCore.Protocol;

/// <summary>Strict, bounded UTF-8 JSON-lines framing shared by the net8 client and net10 Host.</summary>
public static class WireFrame
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    public static string? Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var frame = new MemoryStream();
        while (true)
        {
            var next = input.ReadByte();
            if (next < 0)
            {
                if (frame.Length == 0) return null;
                throw new EndOfStreamException("Truncated stdio protocol frame.");
            }
            if (next == '\n') break;
            if (frame.Length >= StdioProtocol.MaximumFrameBytes)
                throw new InvalidDataException("Stdio protocol frame exceeds the configured limit.");
            frame.WriteByte((byte)next);
        }
        if (frame.Length > 0 && frame.GetBuffer()[frame.Length - 1] == '\r')
            frame.SetLength(frame.Length - 1);
        if (frame.Length == 0) throw new InvalidDataException("Empty stdio protocol frame.");
        try { return StrictUtf8.GetString(frame.GetBuffer(), 0, checked((int)frame.Length)); }
        catch (System.Text.DecoderFallbackException ex)
        { throw new InvalidDataException("Stdio protocol frame is not valid UTF-8.", ex); }
    }

    public static void Write(Stream output, string json)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(json);
        var byteCount = StrictUtf8.GetByteCount(json);
        if (byteCount > StdioProtocol.MaximumFrameBytes)
            throw new InvalidDataException("Stdio protocol frame exceeds the configured limit.");
        var bytes = StrictUtf8.GetBytes(json);
        output.Write(bytes);
        output.WriteByte((byte)'\n');
        output.Flush();
    }
}
