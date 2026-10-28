using System.Diagnostics;
using System.Text.Json;
using OmniCore.Cli;
using OmniCore.Client;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact(Explicit = true)]
    public void Companion_panel_measures_static_and_cached_animated_idle()
    {
        var destination = Environment.GetEnvironmentVariable("OMNICORE_PANEL_BENCHMARK_FILE");
        Assert.False(string.IsNullOrWhiteSpace(destination));
        var prior = Console.Out;
        using var output = new PetCountingWriter();
        var samples = new List<object>();
        try
        {
            Console.SetOut(output);
            RunTuiTest(fx =>
            {
                SeedRecoveryHistory(fx); SeedPlaygroundPet(fx, "fox", 1);
                fx.StartTui(initialColumns: 120, initialRows: 40);
                if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new(Terminal.Gui.Drivers.KeyCode.F2));
                Type(fx, "/pet home");
                KeyWithEffect(fx, Terminal.Gui.Drivers.KeyCode.Enter, () => fx.App.Composer!.Text.Length == 0, "home");
                EmulatePetSixelDevice(fx);
                using var process = Process.GetCurrentProcess();
                foreach (var scenario in new[] { "hidden", "static", "animated" })
                {
                    fx.Invoke(() =>
                    {
                        CompanionPetPresentation.Execute(fx.Server, "mode " + (scenario == "hidden" ? "hidden" : "graphics"));
                        CompanionPetPresentation.Execute(fx.Server, scenario == "animated" ? "motion" : "reduced");
                    });
                    fx.Wait(() => fx.App.CompanionVisual.Effective == (scenario == "hidden" ? "hidden" : "graphics")
                        && fx.App.CompanionPet?.Preferences.ReducedMotion == (scenario != "animated"), "benchmark scenario");
                    // A complete idle cycle warms all six framework-owned frame encodings.
                    Thread.Sleep(scenario == "animated" ? 22000 : 1500);
                    var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
                    var allocatedBefore = GC.GetTotalAllocatedBytes();
                    process.Refresh();
                    var privateBefore = process.PrivateMemorySize64;
                    var bytesBefore = output.Bytes;
                    var cpuBefore = process.TotalProcessorTime;
                    var elapsed = Stopwatch.StartNew();
                    Thread.Sleep(scenario == "animated" ? 24000 : 12000);
                    var wallMs = elapsed.Elapsed.TotalMilliseconds;
                    var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                    var bytes = output.Bytes - bytesBefore;
                    var allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
                    var cached = fx.Read(() => Descendants(fx.App.MainWindow!).OfType<PixelWidgetView>()
                        .Sum(view => view.CachedRasterFrameCount));
                    if (scenario == "animated") Assert.Equal(PixelWidgetView.MaxCachedRasterFrames, cached);
                    samples.Add(new { Scenario = scenario, WallMs = wallMs, CpuMs = cpuMs,
                        OneCorePercent = 100 * cpuMs / wallMs, OutputUtf8Bytes = bytes, BytesPerSecond = bytes * 1000 / wallMs,
                        AllocatedBytes = allocated, ManagedHeapBefore = heapBefore, PrivateBytesBefore = privateBefore,
                        CachedRasterFrames = cached });
                    File.WriteAllText(destination!, JsonSerializer.Serialize(new { Framework = "Terminal.Gui 2.6.0-develop.61",
                        Driver = "DOTNET", Device = "controlled Sixel capability; counting output sink; no browser",
                        Columns = 120, Rows = 40, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
                    Assert.Null(fx.LoopError);
                }
            }, noColor: false);
        }
        finally { Console.SetOut(prior); }
    }
}
