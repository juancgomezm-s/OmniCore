namespace OmniCore.Host;

using StbImageSharp;

/// <summary>Validates complete provider image outputs before they can enter the durable CAS.</summary>
internal static class GeneratedImageValidator
{
    internal const int MaximumImageBytes = 16 * 1024 * 1024;
    internal const int MaximumImagesPerResponse = 8;
    internal const int MaximumResponseBytes = 64 * 1024 * 1024;
    internal const long MaximumPixels = 40_000_000;

    internal static ValidatedGeneratedImage Decode(string mediaType, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (mediaType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new InvalidDataException("Generated image format is not supported for complete validation.");
        if (bytes.Length is 0 or > MaximumImageBytes)
            throw new InvalidDataException("Generated image is empty or exceeds the byte limit.");
        if (mediaType == "image/webp")
        {
            var webp = WebpImageValidator.Decode(bytes);
            return new ValidatedGeneratedImage(mediaType, bytes, webp.Width, webp.Height);
        }
        var signatureMatches = mediaType == "image/png"
            ? bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            : bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
        if (!signatureMatches)
            throw new InvalidDataException("Generated image MIME does not match a supported PNG/JPEG signature.");

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var info = ImageInfo.FromStream(stream)
                ?? throw new InvalidDataException("Generated image format could not be decoded.");
            ValidateDimensions(info.Width, info.Height);
            var decoded = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            if (decoded.Width != info.Width || decoded.Height != info.Height)
                throw new InvalidDataException("Generated image dimensions changed during decode.");
            var expectedDecodedBytes = checked((long)decoded.Width * decoded.Height * 4);
            if (decoded.Data.LongLength != expectedDecodedBytes)
                throw new InvalidDataException("Generated image decode is incomplete.");
            return new ValidatedGeneratedImage(mediaType, bytes, decoded.Width, decoded.Height);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException
            and not AccessViolationException and not OperationCanceledException)
        {
            throw new InvalidDataException("Generated image data is malformed or incomplete.", ex);
        }
    }

    internal static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
            throw new InvalidDataException("Generated image dimensions are outside the pixel limit.");
    }
}

internal sealed record ValidatedGeneratedImage(string MediaType, byte[] Bytes, int Width, int Height);
