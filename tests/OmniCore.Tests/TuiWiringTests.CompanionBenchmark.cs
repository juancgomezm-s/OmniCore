using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OmniCore.Client;
using Terminal.Gui.Drivers;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private sealed class PetCountingWriter : TextWriter
    {
        private long _bytes;
        internal long Bytes => Interlocked.Read(ref _bytes);
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Interlocked.Add(ref _bytes, Encoding.UTF8.GetByteCount(new[] { value }));
        public override void Write(string? value) { if (value is not null) Interlocked.Add(ref _bytes, Encoding.UTF8.GetByteCount(value)); }
        public override void Write(ReadOnlySpan<char> value) => Interlocked.Add(ref _bytes, Encoding.UTF8.GetByteCount(value));
        public override void Write(char[] value, int offset, int count) => Write(value.AsSpan(offset, count));
    }
    [Fact(Explicit = true)]
    public void Companion_rendering_measures_real_driver_cpu_memory_and_output()
    {
        var destination = Environment.GetEnvironmentVariable("OMNICORE_PET_BENCHMARK_FILE");
        Assert.False(string.IsNullOrWhiteSpace(destination), "Set OMNICORE_PET_BENCHMARK_FILE for this explicit benchmark.");
        var prior = Console.Out;
        using var output = new PetCountingWriter();
        var samples = new List<object>();
        try
        {
            Console.SetOut(output);
            RunTuiTest(fx =>
            {
                SeedPlaygroundPet(fx, "ox", 0);
                var client = new CompanionExpressionClient(fx.Server);
                fx.StartTui(initialColumns: 120, initialRows: 40, client: client);
                if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Terminal.Gui.Input.Key(KeyCode.F2));
                fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "benchmark companion connected");
                Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPlayFrame == 0, "home benchmark excludes roaming");
                EmulatePetSixelDevice(fx);
                for (var trial = 0; trial < 3; trial++)
                foreach (var (mode, expression) in new[] { ("hidden", "idle"), ("text", "idle"), ("unicode", "idle"),
                    ("graphics", "idle"), ("unicode", "working"), ("graphics", "working") })
                {
                    client.Expression = expression;
                    var ready = Stopwatch.StartNew();
                    fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "mode " + mode));
                    fx.Wait(() => fx.App.CompanionVisual.Effective == mode && (mode != "graphics"
                        || fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Any(image => image.EncodedSixel is { Length: > 0 })), "benchmark mode ready");
                    var readyMs = ready.Elapsed.TotalMilliseconds;
                    Thread.Sleep(1000); // Finish retired views and async preparation before steady-state measurement.
                    var heap = GC.GetTotalMemory(forceFullCollection: true);
                    using var process = Process.GetCurrentProcess();
                    process.Refresh(); var privateBytes = process.PrivateMemorySize64;
                    var bytesBefore = output.Bytes;
                    var cpuBefore = process.TotalProcessorTime;
                    var elapsed = Stopwatch.StartNew();
                    Thread.Sleep(8000); // One complete eight-second work-animation period; UI loop runs normally.
                    var wallMs = elapsed.Elapsed.TotalMilliseconds;
                    Assert.Null(fx.LoopError); Assert.True(fx.Loop.IsAlive);
                    var bytes = output.Bytes - bytesBefore;
                    var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                    samples.Add(new { Trial = trial, Mode = mode, Expression = expression, ReadyMs = readyMs,
                        WallMs = wallMs, CpuMs = cpuMs, OneCorePercent = 100 * cpuMs / wallMs, ManagedHeapBytes = heap,
                        PrivateBytes = privateBytes, OutputUtf8Bytes = bytes, BytesPerSecond = bytes * 1000 / wallMs });
                    File.WriteAllText(destination!, JsonSerializer.Serialize(new { Framework = "Terminal.Gui 2.6.0-develop.61",
                        Driver = "DOTNET", Device = "controlled Sixel/UTF-8 capability results; counting output sink", Columns = 120, Rows = 40,
                        SixelPaletteLimit = fx.Application.Driver!.Force16Colors ? 16 : 64,
                        Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString(), Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
                }
                Assert.Equal(18, samples.Count);
            }, noColor: false);
        }
        finally { Console.SetOut(prior); }
    }
}
