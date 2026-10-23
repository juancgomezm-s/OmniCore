using System.Diagnostics;
using System.Text.Json;
using OmniCore.Cli;

namespace OmniCore.Tests;

public sealed class CompanionHeadlessTests
{
    [Fact]
    public async Task Real_redirected_process_preserves_structured_pet_commands_and_emits_no_control_sequences()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-pet-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            async Task<string> Run(params string[] arguments)
            {
                var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                info.ArgumentList.Add(typeof(CliApp).Assembly.Location);
                foreach (var argument in arguments) info.ArgumentList.Add(argument);
                info.Environment["OMNICORE_DATA_DIR"] = Path.Combine(root, "data");
                info.Environment["OMNICORE_CONFIG_DIR"] = Path.Combine(root, "config");
                info.Environment["TERM_PROGRAM"] = "kitty"; // An inherited name is not evidence in a pipe.
                using var process = Process.Start(info)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(0, process.ExitCode); Assert.Equal("", await stderr);
                var result = await stdout; Assert.DoesNotContain('\u001b', result); return result;
            }
            using var discovered = JsonDocument.Parse(await Run("pet", "discover", "--json"));
            Assert.True(discovered.RootElement.GetProperty("discovered").GetBoolean());
            using var graphics = JsonDocument.Parse(await Run("pet", "mode", "graphics", "--json"));
            Assert.Equal("graphics", graphics.RootElement.GetProperty("visual").GetProperty("requested").GetString());
            Assert.Equal("text", graphics.RootElement.GetProperty("visual").GetProperty("effective").GetString());
            Assert.Equal(JsonValueKind.Null, graphics.RootElement.GetProperty("visual").GetProperty("backend").ValueKind);
            var line = await Run("pet", "status", "--text");
            Assert.Contains("Omni", line); Assert.Contains(" | ", line);
            Assert.Single(line.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "omni-pet-pipe-"), Path.GetFullPath(root));
            Directory.Delete(root, recursive: true);
        }
    }
}
