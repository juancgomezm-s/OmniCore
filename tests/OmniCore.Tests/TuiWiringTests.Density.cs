using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Density_is_saved_only_on_explicit_save_and_normal_restores_the_saved_preference() => RunTuiTest(fx =>
    {
        fx.StartTui();
        Type(fx, "/density preview");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.DensityPreviewActive, "preview without saving");
        Assert.Empty(Directory.GetFiles(fx.Root, "*.sidebar-density", SearchOption.AllDirectories));
        Type(fx, "/density save");
        KeyWithEffect(fx, KeyCode.Enter, () => !fx.App.DensityPreviewActive && fx.App.CompactDensity, "save compact preference");
        var path = Assert.Single(Directory.GetFiles(fx.Root, "*.sidebar-density", SearchOption.AllDirectories));
        Assert.True(new TuiDensityStore(path).Read());
        Type(fx, "/compacto normal");
        KeyWithEffect(fx, KeyCode.Enter, () => !fx.App.CompactDensity, "restore original preference");
        Assert.False(new TuiDensityStore(path).Read());
    });

    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 40)]
    public void Density_preview_compacts_only_sidebar_and_escape_restores_draft_pet_and_original_layout(int columns, int rows) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.Server.CompanionService.GuestSelector = (_, _, _) => null;
        CompanionPetPresentation.Execute(fx.Server, "reduced");
        var lane = fx.Decoded<LaneCreated>().Last().LaneId;
        var stream = new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        for (var i = 1; i <= 6; i++)
        {
            var text = $"### Revisión {i}\nLa vista conserva colores, herramientas y mascota.\nMenos separación deja más espacio para leer.";
            if (i == 6) text += "\nPREVIA_FIN";
            var artifact = fx.Artifacts.PutText(text, "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
            stream.Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, lane, TurnId.New(), artifact));
        }
        fx.StartTui(initialColumns: columns, initialRows: rows);
        if (!fx.Read(() => fx.App.SidebarOpen)) KeyWithEffect(fx, KeyCode.F2, () => fx.App.SidebarOpen, "sidebar open");
        if (rows >= 36)
        {
            EmulatePetSixelDevice(fx);
            fx.Wait(() => fx.Read(() => fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 1), "original graphic companion");
        }
        fx.Wait(() => fx.App.Conversation!.Text.ToString()!.Contains("PREVIA_FIN", StringComparison.Ordinal), "fixture content");
        var before = fx.Read(() => fx.App.Conversation!.Viewport.Size);
        var composer = fx.Read(() => (fx.App.Composer!.Frame, fx.App.Composer.SuperView!.Frame));
        var textBefore = fx.Read(() => fx.App.Conversation!.Text.ToString());
        var sidebarBefore = fx.Read(() => (fx.App.SidebarSession!.Frame.Y, fx.App.SidebarContent!.Frame.Y));
        var pet = fx.Read(() => (fx.App.CompanionPet!.Species, fx.App.CompanionPet.Xp));
        var sequence = fx.Server.AcquireStore().ReadFrom(fx.Server.LastSessionId()!, 1).Last().Sequence;
        ExportPreferenceFrame(fx, columns, rows, "sidebar-normal", "PREVIA_FIN");
        AddDensityRasterToCapture(fx, columns, rows, "sidebar-normal");
        Type(fx, "/compacto");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.DensityPreviewActive, "compact preview");
        fx.Wait(() => fx.App.SidebarSession!.Frame.Y < sidebarBefore.Item1 && fx.App.SidebarContent!.Frame.Y < sidebarBefore.Item2, "only sidebar spacing reduced");
        fx.Read(() =>
        {
            Assert.True(fx.App.CompactDensity);
            Assert.Equal(before, fx.App.Conversation!.Viewport.Size);
            Assert.Equal(composer, (fx.App.Composer!.Frame, fx.App.Composer.SuperView!.Frame));
            Assert.Equal(textBefore, fx.App.Conversation.Text.ToString());
            Assert.Equal(pet, (fx.App.CompanionPet!.Species, fx.App.CompanionPet.Xp));
        });
        ExportPreferenceFrame(fx, columns, rows, "sidebar-compact", "PREVIA_FIN");
        AddDensityRasterToCapture(fx, columns, rows, "sidebar-compact");
        Type(fx, "borrador intacto");
        KeyWithEffect(fx, KeyCode.Esc, () => !fx.App.DensityPreviewActive, "restore normal layout");
        fx.Wait(() => fx.App.Conversation!.Viewport.Size == before, "original viewport restored");
        fx.Read(() =>
        {
            Assert.False(fx.App.CompactDensity);
            Assert.Equal("borrador intacto", fx.App.Composer!.Text.ToString());
            Assert.Equal(pet, (fx.App.CompanionPet!.Species, fx.App.CompanionPet.Xp));
            Assert.False(fx.App.ExitRequestedByDoubleEscape);
        });
        Assert.Equal(sequence, fx.Server.AcquireStore().ReadFrom(fx.Server.LastSessionId()!, 1).Last().Sequence);
    }, noColor: false);

    // The ordinary SVG capture contains character cells. Preserve the actual driver
    // raster too; this is not a replacement sprite or a simulated ClearType sample.
    private static void AddDensityRasterToCapture(TuiFixture fx, int columns, int rows, string scene)
    {
        if (rows < 36 || Environment.GetEnvironmentVariable("OMNICORE_TUI_SNAPSHOT_DIR") is not { Length: > 0 } folder) return;
        fx.Wait(() => fx.Read(() => fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 1), "graphic restored after command completion");
        var overlay = fx.Read(() =>
        {
            var raster = fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Single();
            var pixels = raster.Pixels!;
            var cell = fx.Application.Driver.SixelSupport!.Resolution;
            var svg = new System.Text.StringBuilder();
            svg.Append(FormattableString.Invariant($"<g transform=\"translate({raster.DestinationCells.X * 12} {raster.DestinationCells.Y * 24}) scale({12d / cell.Width} {24d / cell.Height})\">"));
            for (var y = 0; y < pixels.GetLength(1); y++)
            for (var x = 0; x < pixels.GetLength(0);)
            {
                var color = pixels[x, y]; var end = x + 1;
                while (end < pixels.GetLength(0) && pixels[end, y] == color) end++;
                svg.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"{end - x}\" height=\"1\" fill=\"#{color.R:X2}{color.G:X2}{color.B:X2}\"/>");
                x = end;
            }
            return svg.Append("</g>").ToString();
        });
        var path = Path.Combine(folder, $"settings-{scene}-{columns}x{rows}.svg");
        File.WriteAllText(path, File.ReadAllText(path).Replace("</svg>", overlay + "</svg>", StringComparison.Ordinal));
    }
}
