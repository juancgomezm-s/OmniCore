namespace OmniCore.Tests;

using System.Buffers.Binary;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;

public sealed class WebpImageValidationTests
{
    // Real 2x3 libwebp-encoded still images: VP8L, VP8, and VP8X + ALPH + VP8.
    public const string Lossless = "UklGRh4AAABXRUJQVlA4TBEAAAAvAYAAAAdQslIUtP+BiOh/AAA=";
    public const string Lossy = "UklGRjoAAABXRUJQVlA4IC4AAADwAQCdASoCAAMAAUAmJaACdLoB+AAF9AAA/vFNr/zNExzH7V//mwNUWb5aAAAA";
    public const string Alpha = "UklGRlwAAABXRUJQVlA4WAoAAAAQAAAAAQAAAgAAQUxQSAcAAAAAZGRkZGRkAFZQOCAuAAAA8AEAnQEqAgADAAFAJiWgAnS6AfgABfQAAP7xTa/8zRMcx+1f/5sDVFm+WgAAAA==";

    [Theory]
    [InlineData(Lossless)]
    [InlineData(Lossy)]
    [InlineData(Alpha)]
    public void Complete_still_webp_is_decoded_for_intake_materialization_and_generated_output(string encoded)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-webp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            var artifacts = new FileArtifactStore(root);
            var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(), artifacts);
            var imported = server.ImportUserAttachment(bytes, "still.webp");
            Assert.Equal("image/webp", imported.MediaType);
            Assert.Equal("still.webp", imported.FileName);
            Assert.Equal(bytes, Assert.IsType<ImageBlock>(AttachmentContentMaterializer.Read(imported, artifacts)).Bytes.ToArray());
            var generated = GeneratedImageValidator.Decode("image/webp", bytes);
            Assert.Equal(2, generated.Width);
            Assert.Equal(3, generated.Height);
            Assert.Equal(bytes, generated.Bytes);
            Assert.Throws<InvalidDataException>(() => GeneratedImageValidator.Decode("image/png", bytes));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("header-only-lossless")]
    [InlineData("header-only-lossy")]
    [InlineData("riff-length")]
    [InlineData("trailing-bytes")]
    [InlineData("chunk-length")]
    [InlineData("padding")]
    [InlineData("pixel-limit")]
    [InlineData("animation")]
    [InlineData("duplicate-image")]
    public void Malformed_incomplete_oversized_or_animated_webp_never_enters_attachment_CAS(string mutation)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-webp-deny-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var valid = Convert.FromBase64String(Lossless);
            var bytes = mutation switch
            {
                "header-only-lossless" => Container(Chunk("VP8L", valid.AsSpan(20, 5).ToArray())),
                "header-only-lossy" => Container(Chunk("VP8 ", Convert.FromBase64String(Lossy).AsSpan(20, 10).ToArray())),
                "trailing-bytes" => [.. valid, 0],
                "animation" => Container(Chunk("VP8X", [2, 0, 0, 0, 1, 0, 0, 2, 0, 0]), valid[12..]),
                "duplicate-image" => Container(valid[12..], valid[12..]),
                _ => valid.ToArray(),
            };
            switch (mutation)
            {
                case "riff-length": bytes[4]++; break;
                case "chunk-length": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), uint.MaxValue); break;
                case "padding": bytes[^1] = 1; break;
                case "pixel-limit": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21, 4), 0x0fffffff); break;
            }
            var artifacts = new FileArtifactStore(root);
            var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(), artifacts);
            Assert.Throws<InvalidDataException>(() => server.ImportUserAttachment(bytes, "invalid.webp"));
            Assert.Throws<InvalidDataException>(() => GeneratedImageValidator.Decode("image/webp", bytes));
            Assert.Empty(Directory.Exists(Path.Combine(root, "blobs"))
                ? Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories) : []);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Bounded_unknown_chunk_after_still_payload_is_accepted_without_extracting_metadata()
    {
        var valid = Convert.FromBase64String(Lossless);
        var extended = Container(valid[12..], Chunk("unkn", [42]));
        var decoded = GeneratedImageValidator.Decode("image/webp", extended);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(3, decoded.Height);
    }

    private static byte[] Container(params byte[][] chunks)
    {
        var bytes = new byte[checked(12 + chunks.Sum(chunk => chunk.Length))];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        var offset = 12;
        foreach (var chunk in chunks) { chunk.CopyTo(bytes, offset); offset += chunk.Length; }
        return bytes;
    }

    private static byte[] Chunk(string tag, byte[] payload)
    {
        var bytes = new byte[8 + payload.Length + (payload.Length & 1)];
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);
        return bytes;
    }
}
