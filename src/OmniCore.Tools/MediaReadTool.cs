namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using Task = System.Threading.Tasks.Task;

/// <summary>Authorized bounded image snapshot tool; path reads occur only after PermissionEngine admits its resource claim.</summary>
public sealed class MediaReadTool : ITool
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private const ulong MaximumPixels = 40_000_000;
    private readonly Func<string, bool> _isReferenced;
    private readonly Func<string, ArtifactRef?> _resolve;
    private readonly IPathBoundaryValidator _boundary;
    private readonly Func<string, byte[], (int Width, int Height)>? _decodeComplete;
    private readonly ToolDescriptor _descriptor = new(new ToolId("media.read"),
        "Reads a PNG, JPEG, or WebP image from an authorized workspace path, or reuses an image snapshot referenced by this session.",
        new InputSchema("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"hash\":{\"type\":\"string\"}},\"oneOf\":[{\"required\":[\"path\"]},{\"required\":[\"hash\"]}],\"additionalProperties\":false}"),
        ["read", "image", "media"], true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.Protected);

    public MediaReadTool(Func<string, bool> isReferenced, Func<string, ArtifactRef?> resolve,
        IPathBoundaryValidator boundary, Func<string, byte[], (int Width, int Height)>? decodeComplete = null)
    {
        _isReferenced = isReferenced ?? throw new ArgumentNullException(nameof(isReferenced));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _decodeComplete = decodeComplete;
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!TryArguments(call.NormalizedArgumentsJson, out var path, out var hash))
            return new PreparationRejected("Provide exactly one image path or SHA-256 hash.", null, ToolErrorCode.InvalidArguments);
        if (path is not null && new RedactionPolicy().IsSecretPath(path))
            return new PreparationRejected("Secret paths cannot be read.", null, ToolErrorCode.PermissionDenied);
        var claims = path is null ? ResourceClaims.Empty() : new ResourceClaims([path], [], [], null, []);
        return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.None, claims, ToolRisk.Low, null));
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryArguments(intent.Intent.NormalizedArgumentsJson, out var path, out var hash))
            return Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments, "Invalid media read arguments."));
        if (path is not null) return SnapshotPath(path, context, cancellationToken);
        if (hash is null || !_isReferenced(hash))
            return Task.FromResult(ToolResult.Error(ToolErrorCode.PermissionDenied,
                "The image is not referenced by the current session."));
        var reference = _resolve(hash);
        if (reference is null || reference.Hash.Value != hash || !SupportedImage(reference.MediaType)
            || reference.Size is <= 0 or > MaximumBytes)
            return Task.FromResult(ToolResult.Error(ToolErrorCode.ArtifactMissing, "The image is unavailable or unsupported."));
        if (context.Artifacts is not IBinaryArtifactStore binaryStore
            || binaryStore.GetBytes(reference.Hash, reference.Size) is not { } referencedBytes)
            return Task.FromResult(ToolResult.Error(ToolErrorCode.ArtifactCorrupted,
                "The referenced image failed integrity verification."));
        if (referencedBytes.LongLength != reference.Size || DetectImageType(referencedBytes) != reference.MediaType
            || !ValidateImageHeader(reference.MediaType, referencedBytes)
            || !TryReadImageDimensions(reference.MediaType, referencedBytes, out var referencedDimensions))
            return Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments,
                "The referenced image is malformed or exceeds its decode limits."));
        return Task.FromResult(new ToolResult(ImageSummary(reference.MediaType, referencedDimensions,
            reference.Hash.Value, reference.Size), null, reference,
            reference.Size, false, EffectOutcome.None));
    }

    private Task<ToolResult> SnapshotPath(string path, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path, context.WorkspaceRoot);
        if (!_boundary.IsWithin(full, context.WorkspaceRoot) || !File.Exists(full)
            || SecretPathGuard.IsSecretTarget(_boundary, full, context.WorkspaceRoot)
            || HasReparsePoint(full, context.WorkspaceRoot))
            return Task.FromResult(ToolResult.Error(ToolErrorCode.PermissionDenied,
                "The image path is outside the authorized workspace or protected."));
        var info = new FileInfo(full);
        if (info.Length is <= 0 or > MaximumBytes)
            return Task.FromResult(ToolResult.Error(ToolErrorCode.LimitExceeded, "Image exceeds the size limit."));
        byte[] bytes;
        try
        {
            using var input = new FileStream(full, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.SequentialScan,
            });
            if (input.Length is <= 0 or > MaximumBytes)
                return Task.FromResult(ToolResult.Error(ToolErrorCode.LimitExceeded, "Image exceeds the size limit."));
            if (HasReparsePoint(full, context.WorkspaceRoot))
                return Task.FromResult(ToolResult.Error(ToolErrorCode.PermissionDenied,
                    "The image path changed to a protected target while opening."));
            bytes = new byte[checked((int)input.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) break;
                offset += read;
            }
            if (offset != bytes.Length || input.ReadByte() != -1)
                return Task.FromResult(ToolResult.Error(ToolErrorCode.LimitExceeded,
                    "Image changed or exceeded the size limit while being read."));
        }
        catch (IOException)
        {
            return Task.FromResult(ToolResult.Error(ToolErrorCode.ToolFailure, "The image could not be read safely."));
        }
        if (bytes.Length is <= 0 or > MaximumBytes || DetectImageType(bytes) is not { } mediaType
            || !ValidateImageHeader(mediaType, bytes)
            || !TryReadImageDimensions(mediaType, bytes, out var dimensions))
            return Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments,
                "The file is not a valid bounded PNG, JPEG, or WebP image."));
        if (context.Artifacts is not IBinaryArtifactStore binaryStore)
            return Task.FromResult(ToolResult.Error(ToolErrorCode.ToolFailure, "Binary artifact storage is unavailable."));
        var stored = binaryStore.PutBytes(bytes, mediaType, ArtifactKind.Other, Sensitivity.Sensitive)
            with { FileName = Path.GetFileName(full) };
        return Task.FromResult(new ToolResult(ImageSummary(mediaType, dimensions, stored.Hash.Value, bytes.LongLength), null, stored,
            bytes.LongLength, false, EffectOutcome.None));
    }

    private bool TryReadImageDimensions(string mediaType, byte[] bytes, out (int Width, int Height) dimensions)
    {
        try
        {
            dimensions = _decodeComplete is not null && (mediaType is "image/png" or "image/jpeg")
                ? _decodeComplete(mediaType, bytes)
                : ReadDimensions(mediaType, bytes);
            return dimensions.Width > 0 && dimensions.Height > 0
                && (ulong)dimensions.Width * (ulong)dimensions.Height <= MaximumPixels;
        }
        catch (InvalidDataException)
        {
            dimensions = default;
            return false;
        }
    }

    private static string ImageSummary(string mediaType, (int Width, int Height) dimensions,
        string hash, long bytes) => $"Image snapshot: {mediaType}, {dimensions.Width}x{dimensions.Height}, sha256={hash}, bytes={bytes}.";

    private static bool HasReparsePoint(string fullPath, string workspaceRoot)
    {
        var current = fullPath;
        var root = Path.GetFullPath(workspaceRoot);
        while (current.Length >= root.Length)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            current = parent;
        }
        return true;
    }

    private static bool SupportedImage(string mediaType) => mediaType is "image/png" or "image/jpeg" or "image/webp";

    private static string? DetectImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    private static bool ValidateImageHeader(string mediaType, ReadOnlySpan<byte> data)
    {
        uint width, height;
        if (mediaType == "image/png")
        {
            if (data.Length < 24) return false;
            width = ReadBig(data.Slice(16, 4)); height = ReadBig(data.Slice(20, 4));
        }
        else if (mediaType == "image/webp")
        {
            if (data.Length < 30) return false;
            if (data.Slice(12, 4).SequenceEqual("VP8X"u8))
            { width = 1 + ReadLittle(data.Slice(24, 3)); height = 1 + ReadLittle(data.Slice(27, 3)); }
            else if (data.Slice(12, 4).SequenceEqual("VP8 "u8))
            { width = (uint)(data[26] | data[27] << 8) & 0x3fff; height = (uint)(data[28] | data[29] << 8) & 0x3fff; }
            else return false;
        }
        else if (mediaType == "image/jpeg")
        {
            if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) return false;
            var offset = 2;
            width = height = 0;
            while (offset + 4 <= data.Length)
            {
                if (data[offset++] != 0xff) continue;
                var marker = data[offset++];
                if (marker is 0xd8 or 0xd9 or 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
                if (offset + 2 > data.Length) break;
                var length = data[offset] << 8 | data[offset + 1];
                if (length < 2 || offset + length > data.Length) break;
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                {
                    if (length < 7) return false;
                    height = (uint)(data[offset + 3] << 8 | data[offset + 4]);
                    width = (uint)(data[offset + 5] << 8 | data[offset + 6]);
                    break;
                }
                offset += length;
            }
        }
        else return false;
        return width > 0 && height > 0 && (ulong)width * height <= MaximumPixels;
    }

    private static (int Width, int Height) ReadDimensions(string mediaType, ReadOnlySpan<byte> data)
    {
        if (mediaType == "image/png") return (checked((int)ReadBig(data.Slice(16, 4))), checked((int)ReadBig(data.Slice(20, 4))));
        if (mediaType == "image/webp" && data.Slice(12, 4).SequenceEqual("VP8X"u8))
            return (checked((int)(1 + ReadLittle(data.Slice(24, 3)))), checked((int)(1 + ReadLittle(data.Slice(27, 3)))));
        if (mediaType == "image/webp")
        {
            var width = (int)(data[26] | data[27] << 8) & 0x3fff;
            var height = (int)(data[28] | data[29] << 8) & 0x3fff;
            return (width, height);
        }
        // JPEG complete decoder is supplied by Host in production; this parser remains a
        // bounded fallback for isolated tool construction and tests.
        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset++] != 0xff) continue;
            var marker = data[offset++];
            if (marker is 0xd8 or 0xd9 or 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            var length = data[offset] << 8 | data[offset + 1];
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                return (data[offset + 5] << 8 | data[offset + 6], data[offset + 3] << 8 | data[offset + 4]);
            offset += length;
        }
        throw new InvalidDataException("JPEG dimensions were not found.");
    }

    private static uint ReadBig(ReadOnlySpan<byte> bytes) => (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    private static uint ReadLittle(ReadOnlySpan<byte> bytes) => (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16);

    private static bool TryArguments(string json, out string? path, out string? hash)
    {
        path = null; hash = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1) return false;
            if (root.TryGetProperty("path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String)
            { path = pathValue.GetString(); return !string.IsNullOrWhiteSpace(path) && path.Length <= 4096; }
            if (!root.TryGetProperty("hash", out var hashValue) || hashValue.ValueKind != JsonValueKind.String) return false;
            hash = hashValue.GetString();
            return hash is { Length: 64 } && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        }
        catch (JsonException) { return false; }
    }
}
