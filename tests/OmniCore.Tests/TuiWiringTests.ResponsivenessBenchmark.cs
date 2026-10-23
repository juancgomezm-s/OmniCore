using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using OmniCore.Client;
using Terminal.Gui.Drivers;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    // Opt-in comparison against the same production loop/driver; no provider calls or user data.
    [Fact(Explicit = true)]
    public void Tui_measures_idle_output_and_completed_scroll_frames()
    {
        var destination = Environment.GetEnvironmentVariable("OMNICORE_TUI_BENCHMARK_FILE");
        Assert.False(string.IsNullOrWhiteSpace(destination));
        var prior = Console.Out;
        using var output = new PetCountingWriter();
        var samples = new List<object>();
        try
        {
            Console.SetOut(output);
            RunTuiTest(fx =>
            {
                SeedRecoveryHistory(fx);
                SeedPlaygroundPet(fx, "ox", 0);
                fx.StartTui(initialColumns: 120, initialRows: 40);
                if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new(KeyCode.F2));
                Type(fx, "/pet home");
                KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Composer!.Text.Length == 0, "home");
                EmulatePetSixelDevice(fx);
                using var process = Process.GetCurrentProcess();
                for (var trial = 0; trial < 3; trial++)
                foreach (var mode in new[] { "hidden", "unicode", "graphics" })
                {
                    fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "mode " + mode));
                    fx.Wait(() => fx.App.CompanionVisual.Effective == mode, "benchmark mode " + mode);
                    Thread.Sleep(1000);
                    var draws = 0;
                    EventHandler<EventArgs> count = (_, _) => Interlocked.Increment(ref draws);
                    fx.Application.LayoutAndDrawComplete += count;
                    try
                    {
                        var bytesBefore = output.Bytes;
                        var allocatedBefore = GC.GetTotalAllocatedBytes();
                        var cpuBefore = process.TotalProcessorTime;
                        var elapsed = Stopwatch.StartNew();
                        Thread.Sleep(3000);
                        var idleMs = elapsed.Elapsed.TotalMilliseconds;
                        var idleCpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                        var idleBytes = output.Bytes - bytesBefore;
                        var idleAllocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
                        var idleDraws = Volatile.Read(ref draws);
                        bytesBefore = output.Bytes;
                        cpuBefore = process.TotalProcessorTime;
                        var latencies = new List<double>();
                        elapsed.Restart();
                        for (var frame = 0; frame < 30; frame++)
                        {
                            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            EventHandler<EventArgs> drawn = (_, _) => completed.TrySetResult();
                            var timer = Stopwatch.StartNew();
                            fx.Invoke(() =>
                            {
                                fx.Application.LayoutAndDrawComplete += drawn;
                                fx.App.Conversation!.ScrollTo(new Point(0, frame % 2 == 0 ? 12 : 80));
                            });
                            try { completed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
                            finally { fx.Application.LayoutAndDrawComplete -= drawn; }
                            latencies.Add(timer.Elapsed.TotalMilliseconds);
                        }
                        latencies.Sort();
                        var scrollMs = elapsed.Elapsed.TotalMilliseconds;
                        var scrollCpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                        var scrollBytes = output.Bytes - bytesBefore;
                        samples.Add(new { Trial = trial, Mode = mode, IdleWallMs = idleMs, IdleCpuMs = idleCpuMs,
                            IdleDraws = idleDraws, IdleOutputBytes = idleBytes, IdleAllocatedBytes = idleAllocated,
                            ScrollFrames = latencies.Count, ScrollWallMs = scrollMs, ScrollCpuMs = scrollCpuMs,
                            ScrollOutputBytes = scrollBytes, ScrollFrameMedianMs = latencies[latencies.Count / 2],
                            ScrollFrameP95Ms = latencies[(int)(latencies.Count * .95)], ScrollFrameMaxMs = latencies[^1] });
                        File.WriteAllText(destination!, JsonSerializer.Serialize(new { Framework = "Terminal.Gui 2.6.0-develop.61",
                            Driver = "DOTNET", Device = "counting output sink, controlled Sixel capability; no browser viewer",
                            Columns = 120, Rows = 40, Runtime = Environment.Version.ToString(), Samples = samples },
                            new JsonSerializerOptions { WriteIndented = true }));
                    }
                    finally { fx.Application.LayoutAndDrawComplete -= count; }
                }
                Assert.Null(fx.LoopError);
            }, noColor: false);
        }
        finally { Console.SetOut(prior); }
    }
}
