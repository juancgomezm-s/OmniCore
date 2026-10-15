namespace OmniCore.Cli;

using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

/// <summary>Reads Windows CF_DIB clipboard data and converts a bounded RGB bitmap to PNG.</summary>
internal static class WindowsClipboardImage
{
    private const uint DibFormat = 8;
    private const int MaximumClipboardBytes = 16 * 1024 * 1024;
    private const long MaximumPixels = 16_000_000;

    public static byte[]? TryReadPng()
    {
        if (!OperatingSystem.IsWindows() || !OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(DibFormat);
            if (handle == IntPtr.Zero) return null;
            var size = (long)GlobalSize(handle).ToUInt64();
            if (size is < 40 or > MaximumClipboardBytes) return null;
            var data = GlobalLock(handle);
            if (data == IntPtr.Zero) return null;
            try
            {
                var dib = new byte[(int)size];
                Marshal.Copy(data, dib, 0, dib.Length);
                return ConvertDibToPng(dib);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    internal static byte[]? ConvertDibToPng(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(dib[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        if (headerSize < 40 || headerSize > (uint)dib.Length || width <= 0 || signedHeight == 0
            || signedHeight == int.MinValue || planes != 1 || bits is not (24 or 32) || compression != 0)
            return null;
        var height = Math.Abs(signedHeight);
        if ((long)width * height > MaximumPixels) return null;
        var sourceStride = checked(((long)width * bits + 31) / 32 * 4);
        var pixelOffset = (long)headerSize;
        if (pixelOffset + sourceStride * height > dib.Length) return null;

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[checked(width * 4 + 1)];
            for (var outputY = 0; outputY < height; outputY++)
            {
                row[0] = 0;
                var sourceY = signedHeight > 0 ? height - 1 - outputY : outputY;
                var sourceRow = dib.Slice(checked((int)(pixelOffset + sourceY * sourceStride)), checked((int)sourceStride));
                for (var x = 0; x < width; x++)
                {
                    var src = x * (bits / 8);
                    var dst = 1 + x * 4;
                    row[dst] = sourceRow[src + 2];
                    row[dst + 1] = sourceRow[src + 1];
                    row[dst + 2] = sourceRow[src];
                    row[dst + 3] = 255;
                }
                zlib.Write(row);
            }
        }
        if (compressed.Length > MaximumClipboardBytes) return null;
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        WriteChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png.Length <= MaximumClipboardBytes ? png.ToArray() : null;
    }

    private static void WriteChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        destination.Write(number);
        destination.Write(type);
        destination.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in type) crc = UpdateCrc(crc, value);
        foreach (var value in data) crc = UpdateCrc(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        destination.Write(number);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr memory);
}
