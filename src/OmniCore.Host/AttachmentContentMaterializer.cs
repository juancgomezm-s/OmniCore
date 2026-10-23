namespace OmniCore.Host;

using System.IO.Compression;
using System.Text;
using System.Xml;
using OmniCore.Abstractions;
using OmniCore.Domain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

/// <summary>Bounded materialization of immutable user attachment snapshots (no paths or OCR).</summary>
internal static class AttachmentContentMaterializer
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumTextCharacters = 200_000;
    public const int MaximumPdfPages = 100;
    private const long MaximumDocxExpandedBytes = 32 * 1024 * 1024;

    public static ContentBlock Read(ArtifactRef reference, IArtifactStore artifacts)
    {
        if (reference.ExtractedTextSnapshot is { } snapshot)
        {
            var expectedExtractor = ExtractorIdentity(reference.MediaType);
            if (expectedExtractor is null || reference.ExtractorId != expectedExtractor
                || snapshot.MediaType != "text/plain" || snapshot.Kind != ArtifactKind.Other
                || snapshot.Sensitivity != Sensitivity.Sensitive || artifacts.GetText(snapshot.Hash) is not { } extracted
                || extracted.Length > MaximumTextCharacters)
                throw new InvalidDataException("Extracted attachment snapshot is missing or incompatible.");
            return new DocumentBlock(reference.MediaType, SafeName(reference), extracted);
        }
        if (reference.Size is <= 0 or > MaximumBytes || artifacts is not IBinaryArtifactStore binary)
            throw new InvalidDataException("Attachment is missing, corrupt, or outside the size limit.");
        var bytes = binary.GetBytes(reference.Hash, reference.Size)
            ?? throw new InvalidDataException("Attachment bytes failed integrity verification.");
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Attachment exceeds the size limit.");

        return Materialize(reference, bytes);
    }

    public static (ArtifactRef Snapshot, string ExtractorId)? CreateExtractedTextSnapshot(
        ArtifactRef original, byte[] bytes, IArtifactStore artifacts)
    {
        var extractor = ExtractorIdentity(original.MediaType);
        if (extractor is null || Materialize(original, bytes) is not DocumentBlock document) return null;
        var snapshot = artifacts.PutText(document.ExtractedText, "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
        return (snapshot, extractor);
    }

    internal static string? ExtractorIdentity(string mediaType) => mediaType switch
    {
        "text/plain" or "text/markdown" or "text/csv" or "application/json" => "omnicore.utf8-strict/1",
        "application/pdf" => "omnicore.pdfpig-content-order/0.1.16-v1",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "omnicore.docx-ooxml-text/1",
        _ => null,
    };

    public static void ValidateBeforePublication(byte[] bytes, string fileName)
    {
        if (bytes.Length is 0 or > MaximumBytes)
            throw new InvalidDataException("Attachment content is invalid or unsupported.");
        var mediaType = DetectMediaType(bytes);
        ValidateAttachmentBytes(mediaType, bytes);
        if (mediaType.StartsWith("image/", StringComparison.Ordinal)) return; // no OCR or hidden text extraction
        var reference = new ArtifactRef(ArtifactId.New(),
            ContentHash.Sha256(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()),
            bytes.LongLength, mediaType, ArtifactKind.Other, Sensitivity.Sensitive, Redacted: false)
        { FileName = fileName };
        if (Materialize(reference, bytes) is DocumentBlock document
            && !string.Equals(new RedactionPolicy().Redact(document.ExtractedText), document.ExtractedText, StringComparison.Ordinal))
            throw new InvalidDataException("Attachment contains protected secret material and was not stored.");
    }

    private static ContentBlock Materialize(ArtifactRef reference, byte[] bytes) => reference.MediaType switch
        {
            "image/png" or "image/jpeg" or "image/webp" => ValidateImage(reference, bytes),
            "text/plain" or "text/markdown" or "text/csv" or "application/json" => ReadText(reference, bytes),
            "application/pdf" => ReadPdf(reference, bytes),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ReadDocx(reference, bytes),
            _ => throw new InvalidDataException("Attachment media type is not supported.")
        };

    public static string DetectMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8)) return "application/pdf";
        if (bytes.Length >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K' && bytes[2] is 3 or 5 or 7
            && bytes[3] is 4 or 6 or 8)
        {
            try
            {
                using var memory = new MemoryStream(bytes.ToArray(), writable: false);
                using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
                return zip.GetEntry("word/document.xml") is not null
                    ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                    : throw new InvalidDataException("Only DOCX documents are accepted from ZIP content.");
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            { throw new InvalidDataException("ZIP document is malformed.", ex); }
        }
        if (bytes.Length > 0 && !bytes.Contains((byte)0))
        {
            try { _ = new UTF8Encoding(false, true).GetString(bytes); return "text/plain"; }
            catch (DecoderFallbackException) { }
        }
        throw new InvalidDataException("Attachment type could not be verified from its content.");
    }

    public static bool IsSupportedIntake(string mediaType) => mediaType is "image/png" or "image/jpeg" or "image/webp"
        or "application/pdf" or "text/plain"
        or "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static ContentBlock ValidateImage(ArtifactRef reference, byte[] bytes)
    {
        if (reference.MediaType == "image/webp")
        {
            _ = WebpImageValidator.Decode(bytes);
            return new ImageBlock(reference.MediaType, bytes);
        }
        ValidateImageHeader(reference.MediaType, bytes);
        return new ImageBlock(reference.MediaType, bytes);
    }

    public static void ValidateAttachmentBytes(string mediaType, byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes || DetectMediaType(bytes) != mediaType || !IsSupportedIntake(mediaType))
            throw new InvalidDataException("Attachment content is invalid or unsupported.");
        if (mediaType == "image/webp") _ = WebpImageValidator.Decode(bytes);
        else if (mediaType.StartsWith("image/", StringComparison.Ordinal)) ValidateImageHeader(mediaType, bytes);
    }

    private static void ValidateImageHeader(string mediaType, ReadOnlySpan<byte> bytes)
    {
        uint width, height;
        if (mediaType == "image/png")
        {
            if (bytes.Length < 24 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("PNG signature is invalid.");
            width = ReadBigEndian(bytes.Slice(16, 4)); height = ReadBigEndian(bytes.Slice(20, 4));
        }
        else if (mediaType == "image/jpeg")
        {
            (width, height) = JpegDimensions(bytes);
        }
        else throw new InvalidDataException("Unsupported image media type.");
        if (width == 0 || height == 0 || (ulong)width * height > 40_000_000UL)
            throw new InvalidDataException("Image dimensions are invalid or exceed the pixel limit.");
    }

    private static (uint Width, uint Height) JpegDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) throw new InvalidDataException("JPEG signature is invalid.");
        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset++] != 0xff) continue;
            var marker = data[offset++];
            if (marker is 0xd8 or 0xd9 or 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > data.Length) break;
            var length = data[offset] << 8 | data[offset + 1];
            if (length < 2 || offset + length > data.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (length < 7) break;
                return ((uint)(data[offset + 5] << 8 | data[offset + 6]), (uint)(data[offset + 3] << 8 | data[offset + 4]));
            }
            offset += length;
        }
        throw new InvalidDataException("JPEG dimensions were not found.");
    }

    private static ContentBlock ReadText(ArtifactRef reference, byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Length > MaximumTextCharacters) throw new InvalidDataException("Text attachment exceeds the character limit.");
        return new DocumentBlock(reference.MediaType, SafeName(reference), text);
    }

    private static ContentBlock ReadPdf(ArtifactRef reference, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pdf = PdfDocument.Open(stream);
        if (pdf.NumberOfPages > MaximumPdfPages) throw new InvalidDataException("PDF exceeds the page limit.");
        var text = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(ContentOrderTextExtractor.GetText(page));
            if (text.Length > MaximumTextCharacters) throw new InvalidDataException("PDF extracted text exceeds the character limit.");
        }
        return new DocumentBlock(reference.MediaType, SafeName(reference), text.ToString().Length == 0
            ? "No extractable text was found. This PDF may be scanned; OCR is not performed automatically."
            : text.ToString());
    }

    private static ContentBlock ReadDocx(ArtifactRef reference, byte[] bytes)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        if (zip.Entries.Count > 2048) throw new InvalidDataException("DOCX archive has too many entries.");
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (entry.Length > 8 * 1024 * 1024 || expanded > MaximumDocxExpandedBytes
                || (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > 100))
                throw new InvalidDataException("DOCX archive exceeds expansion limits.");
        }
        var document = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("DOCX has no document.xml.");
        var result = new StringBuilder();
        using var input = document.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024, IgnoreComments = true });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "t") continue;
            var part = reader.ReadElementContentAsString();
            if (result.Length + part.Length > MaximumTextCharacters) throw new InvalidDataException("DOCX extracted text exceeds the character limit.");
            result.Append(part).Append(' ');
        }
        return new DocumentBlock(reference.MediaType, SafeName(reference), result.ToString());
    }

    private static string SafeName(ArtifactRef reference) => string.IsNullOrWhiteSpace(reference.FileName) ? "attachment" : Path.GetFileName(reference.FileName);
    private static uint ReadBigEndian(ReadOnlySpan<byte> bytes) => (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
}
