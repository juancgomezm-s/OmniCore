namespace OmniCore.Host;

using System.Buffers.Binary;
using SkiaSharp;

/// <summary>Validates the complete bounded still-WebP container and decodes its pixels before publication.</summary>
internal static class WebpImageValidator
{
    internal static (int Width, int Height) Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is < 20 or > GeneratedImageValidator.MaximumImageBytes
            || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)
            || (ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) + 8 != (ulong)bytes.Length)
            throw new InvalidDataException("WebP container length or signature is invalid.");

        (int Width, int Height)? canvas = null;
        (int Width, int Height)? image = null;
        var offset = 12;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8)
                throw new InvalidDataException("WebP chunk header is incomplete.");
            var tag = bytes.AsSpan(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var paddedLength = (ulong)length + (length & 1u);
            if (paddedLength > (ulong)(bytes.Length - offset - 8))
                throw new InvalidDataException("WebP chunk payload is incomplete.");
            var payload = bytes.AsSpan(offset + 8, checked((int)length));
            if ((length & 1u) != 0 && bytes[offset + 8 + (int)length] != 0)
                throw new InvalidDataException("WebP chunk padding is invalid.");

            if (tag.SequenceEqual("ANIM"u8) || tag.SequenceEqual("ANMF"u8))
                throw new InvalidDataException("Animated WebP images are not supported.");
            if (tag.SequenceEqual("VP8X"u8))
            {
                if (offset != 12 || canvas is not null || payload.Length != 10
                    || (payload[0] & 0xc1) != 0 || payload[1] != 0 || payload[2] != 0 || payload[3] != 0)
                    throw new InvalidDataException("WebP extended header is invalid.");
                if ((payload[0] & 0x02) != 0)
                    throw new InvalidDataException("Animated WebP images are not supported.");
                canvas = (1 + ReadUInt24(payload[4..]), 1 + ReadUInt24(payload[7..]));
                GeneratedImageValidator.ValidateDimensions(canvas.Value.Width, canvas.Value.Height);
            }
            else if (tag.SequenceEqual("VP8 "u8) || tag.SequenceEqual("VP8L"u8))
            {
                if (image is not null)
                    throw new InvalidDataException("WebP contains more than one still image payload.");
                if (tag.SequenceEqual("VP8L"u8))
                {
                    if (payload.Length < 5 || payload[0] != 0x2f)
                        throw new InvalidDataException("WebP lossless header is incomplete or invalid.");
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
                    if ((bits >> 29) != 0)
                        throw new InvalidDataException("WebP lossless version is unsupported.");
                    image = (1 + (int)(bits & 0x3fff), 1 + (int)((bits >> 14) & 0x3fff));
                }
                else
                {
                    if (payload.Length < 10 || (payload[0] & 1) != 0
                        || !payload.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                        throw new InvalidDataException("WebP lossy header is incomplete or invalid.");
                    image = (BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3fff,
                        BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3fff);
                }
                GeneratedImageValidator.ValidateDimensions(image.Value.Width, image.Value.Height);
            }
            offset = checked(offset + 8 + (int)paddedLength);
        }
        if (image is null || (canvas is not null && canvas != image))
            throw new InvalidDataException("WebP image data is missing or disagrees with its canvas.");

        using var stream = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException("WebP image could not be decoded.");
        // The still-image codec can report zero animation frames. The bounded RIFF
        // walk above independently rejects animation and requires one image payload.
        if (codec.EncodedFormat != SKEncodedImageFormat.Webp || codec.FrameCount is < 0 or > 1
            || codec.Info.Width != image.Value.Width || codec.Info.Height != image.Value.Height)
            throw new InvalidDataException("WebP decoder metadata disagrees with its bounded still image.");
        var info = new SKImageInfo(image.Value.Width, image.Value.Height,
            SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[checked(info.Width * info.Height * 4)];
        // The Pixels convenience property also accepts IncompleteInput. Publication requires
        // an explicit successful full decode; partial pixels are never treated as an image.
        if (codec.GetPixels(info, pixels) != SKCodecResult.Success)
            throw new InvalidDataException("WebP image data is malformed or incomplete.");
        return image.Value;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
}
