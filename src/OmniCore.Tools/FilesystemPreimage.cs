namespace OmniCore.Tools;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Lossless representation of the text encodings already supported by filesystem tools.
/// Uses the ordinary redacted text CAS: neither raw storage nor base64 conceals source secrets.</summary>
public static class FilesystemPreimage
{
    public const string MediaType = "application/vnd.omnicore.filesystem-preimage+json";
    public const string PostMediaType = "application/vnd.omnicore.filesystem-postimage+json";

    /// <summary>Optional evidence, never turns an already applied write into a failure.
    /// Secrets or a corrupt/unavailable CAS leave the preview explicitly unavailable.</summary>
    internal static ArtifactRef? CaptureAfter(IArtifactStore? artifacts, byte[] bytes)
    {
        if (artifacts is null || bytes.Length > 2 * 1024 * 1024) return null;
        try { return CaptureCore(new(null, null, null), artifacts, bytes, PostMediaType).BeforeStateRef; }
        catch (FilesystemPreimageException) { return null; }
    }

    internal static ReconciliationSpec Capture(ReconciliationSpec reconciliation, IArtifactStore? artifacts,
        byte[]? original)
        => CaptureCore(reconciliation, artifacts, original, MediaType);

    private static ReconciliationSpec CaptureCore(ReconciliationSpec reconciliation, IArtifactStore? artifacts,
        byte[]? original, string mediaType)
    {
        if (artifacts is null) return reconciliation; // Primitive/legacy executor, not normal Act composition.
        try
        {
            FileVersion.DecodedFile? decoded = original is null ? null : FileVersion.Decode(original);
            if (decoded is { } file)
            {
                // Prove fidelity before publication, including BOM, line endings and strict decoding.
                if (!FileVersion.Encode(file.Text, file.Encoding).AsSpan().SequenceEqual(original)) throw Invalid();
                var safe = new RedactionPolicy().Redact(file.Text);
                if (SecretRedactorRegistry.Current is { } redactor) safe = redactor.Redact(safe);
                if (!string.Equals(safe, file.Text, StringComparison.Ordinal)) throw Invalid();
            }
            var document = new FilesystemPreimageDocument(1, original is not null, decoded?.Encoding,
                decoded?.Text, original is null ? null : FileVersion.VersionToken(original), original?.LongLength ?? 0);
            var json = JsonSerializer.Serialize(document, FilesystemPreimageJsonContext.Default.FilesystemPreimageDocument);
            var reference = artifacts.PutText(json, mediaType, ArtifactKind.Other, Sensitivity.Sensitive);
            var expectedHash = ContentHash.Sha256(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
            if (reference.Redacted || reference.Kind != ArtifactKind.Other || reference.Sensitivity != Sensitivity.Sensitive
                || reference.MediaType != mediaType || reference.Hash != expectedHash
                || reference.Size != Encoding.UTF8.GetByteCount(json)
                || !artifacts.Verify(reference.Hash, reference.Size)
                || !string.Equals(artifacts.GetText(reference.Hash), json, StringComparison.Ordinal)) throw Invalid();
            var restored = Read(artifacts, reference);
            if (original is null ? restored is not null : restored is null || !original.AsSpan().SequenceEqual(restored))
                throw Invalid();
            return new ReconciliationSpec(reconciliation.ExpectedPreHash, reconciliation.ExpectedPostHash,
                reconciliation.IdempotencyKey) { BeforeStateRef = reference, Reversibility = Reversibility.Reversible };
        }
        catch (Exception) { throw Invalid(); } // Never return raw contents/provider/store messages.
    }

    /// <summary>Reads/verifies the pre-image without restoring any filesystem effect (restore is M7).</summary>
    public static byte[]? Read(IArtifactStore artifacts, ArtifactRef reference)
    {
        try
        {
            if (reference.Redacted || reference.MediaType is not (MediaType or PostMediaType) || reference.Kind != ArtifactKind.Other
                || reference.Sensitivity != Sensitivity.Sensitive || !artifacts.Verify(reference.Hash, reference.Size))
                throw Invalid();
            var json = artifacts.GetText(reference.Hash) ?? throw Invalid();
            if (Encoding.UTF8.GetByteCount(json) != reference.Size || reference.Hash != ContentHash.Sha256(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))))) throw Invalid();
            var document = JsonSerializer.Deserialize(json, FilesystemPreimageJsonContext.Default.FilesystemPreimageDocument)
                ?? throw Invalid();
            if (document.Version != 1) throw Invalid();
            if (!document.Exists)
            {
                if (document.Encoding is not null || document.Text is not null || document.OriginalHash is not null
                    || document.ByteLength != 0) throw Invalid();
                return null;
            }
            if (document.Encoding is null || !Enum.IsDefined(document.Encoding.Value) || document.Text is null
                || document.OriginalHash is null || document.ByteLength < 0) throw Invalid();
            var bytes = FileVersion.Encode(document.Text, document.Encoding.Value);
            if (bytes.LongLength != document.ByteLength || FileVersion.VersionToken(bytes) != document.OriginalHash)
                throw Invalid();
            return bytes;
        }
        catch (Exception) { throw Invalid(); }
    }

    private static FilesystemPreimageException Invalid() => new();

    /// <summary>Only classify a leaf mutation with existing ordinary ancestors as reversible.
    /// Directory creation and links require additional topology evidence, not just content.</summary>
    internal static bool IsLeafOnly(string fullPath, string workspaceRoot)
    {
        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var full = Path.GetFullPath(fullPath);
            for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                }
                else if (!string.Equals(current, full, StringComparison.Ordinal)) return false;
                if (string.Equals(current, root, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
            }
            return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

internal sealed class FilesystemPreimageException : InvalidOperationException
{
    internal FilesystemPreimageException() : base("Filesystem pre-image could not be captured faithfully and safely.") { }
}

internal sealed record FilesystemPreimageDocument(int Version, bool Exists, FileVersion.FileEncoding? Encoding,
    string? Text, string? OriginalHash, long ByteLength);

[JsonSerializable(typeof(FilesystemPreimageDocument))]
internal partial class FilesystemPreimageJsonContext : JsonSerializerContext;
