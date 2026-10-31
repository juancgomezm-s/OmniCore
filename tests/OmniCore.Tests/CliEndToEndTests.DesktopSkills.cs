using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Task=System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class CliEndToEndTests
{
    [Fact]
    public async Task Natural_window_request_loads_and_unloads_bundled_desktop_without_skill_commands()
    {
        await InIsolatedCli(async(workspace,config,data,provider)=>
        {
            File.WriteAllText(Path.Combine(config,"models.yaml"),"models:\n  scripted-model:\n    provider: scripted\n    context: 65536\n    maxOutput: 2048\n");
            SetSessionSecurityFullAgent();SetSourceModelFullAgent(data);
            using(var modes=new RunModePreferenceStore(OmniHost.CreatePlatformPaths().UserDatabasePath))modes.Set(RunMode.Act,0);
            var disclosed=new List<bool>();
            provider.RespondWith((index,body)=>
            {
                using var request=JsonDocument.Parse(body);
                disclosed.Add(request.RootElement.GetProperty("tools").EnumerateArray().Any(
                    t=>t.GetProperty("function").GetProperty("parameters").TryGetProperty("properties",out var properties)
                        && properties.TryGetProperty("operation",out var operation)
                        && operation.TryGetProperty("enum",out var options)&&options.EnumerateArray().Any(o=>o.GetString()=="list_windows")));
                return TextResponse("fixture-complete");
            });
            using var runtime=OmniCliRuntime.Create(workspace);runtime.UseConsoleInput=false;
            var host=new TuiTurnHost(runtime);var diagnostics=new List<string>();
            Assert.Equal(0,await host.ExecuteAsync("Prueba una ventana de mi aplicación.",diagnostics.Add,TestContext.Current.CancellationToken));
            Assert.Equal(0,await host.ExecuteAsync("Cuánto es 82 por 7?",diagnostics.Add,TestContext.Current.CancellationToken));
            Assert.Equal(new[]{true,false},disclosed);
            var lifecycle=ReadCurrentSessionEvents(workspace).OfType<SkillActivationChanged>().Where(e=>e.SkillId=="desktop-control").ToArray();
            Assert.Equal(2,lifecycle.Length);Assert.True(lifecycle[0].Activated);Assert.False(lifecycle[1].Activated);
            Assert.All(lifecycle,e=>Assert.Equal("Automatic",e.Origin));
            var server=(OmniServer)runtime.Connect(TestContext.Current.CancellationToken);
            Assert.Empty(new SessionSkillActivationStore(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(),workspace)).ReadDetailed(server.LastSessionId()!));
        });
    }
}
