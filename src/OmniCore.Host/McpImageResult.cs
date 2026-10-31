namespace OmniCore.Host;

using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Publishes one bounded inline MCP image through the existing sensitive CAS path.</summary>
internal static class McpImageResult
{
    internal static (string Json, ArtifactRef? Artifact) Materialize(JsonElement result, IArtifactStore? artifacts)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return (result.GetRawText(), null);
        var images = content.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() == "image").ToArray();
        if (images.Length == 0) return (result.GetRawText(), null);
        // ToolResult currently carries a single artifact. Reject multiplicity rather
        // than silently losing an image or changing the durable domain contract here.
        if (images.Length != 1) throw new InvalidDataException("Multiple MCP images are not supported by this tool-result contract.");
        if (artifacts is not IBinaryArtifactStore binary)
            throw new InvalidDataException("Binary artifact storage is unavailable for MCP images.");
        var image = images[0];
        if (!image.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String
            || mime.GetString() is not ("image/png" or "image/jpeg" or "image/webp")
            || !image.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("MCP inline image metadata is invalid.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data.GetString()!); }
        catch (FormatException exception) { throw new InvalidDataException("MCP image base64 is invalid.", exception); }
        // The entire MCP result has already passed the 256 KiB bridge limit.
        var decoded = GeneratedImageValidator.Decode(mime.GetString()!, bytes);
        var artifact = binary.PutBytes(decoded.Bytes, decoded.MediaType, ArtifactKind.Other, Sensitivity.Sensitive)
            with { FileName = "mcp-image" + (decoded.MediaType == "image/png" ? ".png"
                : decoded.MediaType == "image/jpeg" ? ".jpg" : ".webp") };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in result.EnumerateObject())
            {
                if (property.Name != "content") { property.WriteTo(writer); continue; }
                writer.WritePropertyName("content"); writer.WriteStartArray();
                foreach (var item in content.EnumerateArray())
                {
                    if (!item.Equals(image)) { item.WriteTo(writer); continue; }
                    writer.WriteStartObject(); writer.WriteString("type", "image");
                    writer.WriteString("mimeType", decoded.MediaType);
                    writer.WriteString("artifactHash", artifact.Hash.Value);
                    writer.WriteNumber("width", decoded.Width); writer.WriteNumber("height", decoded.Height);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return (Encoding.UTF8.GetString(stream.ToArray()), artifact);
    }
}
