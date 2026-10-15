using System.Buffers.Binary;
using OmniCore.Cli;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class WindowsClipboardImageTests
{
    [Fact]
    public void Converts_bounded_rgb_dib_to_png_without_external_processes()
    {
        var dib = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20), 4);
        dib[40] = 0; dib[41] = 0; dib[42] = 255; // BGR red, one padded row

        var png = WindowsClipboardImage.ConvertDibToPng(dib);

        Assert.NotNull(png);
        Assert.Equal("image/png", AttachmentContentMaterializer.DetectMediaType(png));
        AttachmentContentMaterializer.ValidateAttachmentBytes("image/png", png);
    }
}
