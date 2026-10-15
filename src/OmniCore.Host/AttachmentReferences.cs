namespace OmniCore.Host;

using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Domain;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ArtifactRef[]))]
[JsonSerializable(typeof(ArtifactRef))]
internal partial class AttachmentReferenceJsonContext : JsonSerializerContext;

internal static class AttachmentReferences
{
    public const int MaximumCount = 8;

    public static string Encode(IReadOnlyList<ArtifactRef> references)
    {
        if (references.Count > MaximumCount) throw new InvalidDataException("Too many attachments.");
        var json = JsonSerializer.Serialize(references.ToArray(), AttachmentReferenceJsonContext.Default.ArtifactRefArray);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 64 * 1024)
            throw new InvalidDataException("Attachment manifest exceeds the limit.");
        return json;
    }

    public static ArtifactRef[] Decode(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 64 * 1024)
            throw new InvalidDataException("Attachment manifest exceeds the limit.");
        var refs = JsonSerializer.Deserialize(json, AttachmentReferenceJsonContext.Default.ArtifactRefArray)
            ?? throw new InvalidDataException("Attachment manifest is invalid.");
        if (refs.Length > MaximumCount || refs.Any(reference => reference is null
            || reference.Size <= 0 || reference.Size > 16 * 1024 * 1024
            || reference.Hash.Algorithm != "sha256" || reference.Hash.Value.Length != 64
            || reference.Kind != ArtifactKind.Other || reference.Sensitivity != Sensitivity.Sensitive || reference.Redacted
            || reference.FileName is null || reference.FileName.Length is < 1 or > 180
            || Path.GetFileName(reference.FileName) != reference.FileName
            || reference.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || (reference.ExtractedTextSnapshot is null) != (reference.ExtractorId is null)
            || reference.ExtractedTextSnapshot is { } snapshot
                && (snapshot.Size is <= 0 or > AttachmentContentMaterializer.MaximumTextCharacters * 4L
                    || snapshot.Hash.Algorithm != "sha256" || snapshot.Hash.Value.Length != 64
                    || snapshot.MediaType != "text/plain" || snapshot.Kind != ArtifactKind.Other
                    || snapshot.Sensitivity != Sensitivity.Sensitive || snapshot.Redacted
                    || reference.ExtractorId != AttachmentContentMaterializer.ExtractorIdentity(reference.MediaType))))
            throw new InvalidDataException("Attachment manifest contains an invalid reference.");
        return refs;
    }
}
