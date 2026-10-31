using System.Text;
using OmniCore.Domain;
using OmniCore.Host;
using Task=System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class DesktopIndicatorCancellationTests
{
    [Theory]
    [InlineData("OMNICORE_DESKTOP_CANCELLED\n",1)]
    [InlineData("OMNICORE_DESKTOP_CANCELLED\r\nOMNICORE_DESKTOP_CANCELLED\n",1)]
    [InlineData("window title: OMNICORE_DESKTOP_CANCELLED\n",0)]
    [InlineData("{\"text\":\"OMNICORE_DESKTOP_CANCELLED\"}\n",0)]
    public async Task Only_the_exact_native_control_marker_cancels_once(string signal,int expected)
    {
        var root=Path.Combine(Path.GetTempPath(),"omni-indicator-marker-"+Guid.NewGuid().ToString("N"));
        var gate=new DesktopSkillGate(SessionId.New(),new SessionSkillActivationStore(root),root);
        var count=0;using var tool=new DesktopControlTool(gate,()=>count++);
        using var reader=new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(signal)));
        await tool.MonitorCancellationAsync(reader);
        Assert.Equal(expected,count);Assert.False(tool.HasStarted);
    }

    [Fact]
    public async Task Disposed_tool_does_not_cancel_a_later_turn()
    {
        var root=Path.Combine(Path.GetTempPath(),"omni-indicator-marker-"+Guid.NewGuid().ToString("N"));
        var count=0;var tool=new DesktopControlTool(new DesktopSkillGate(SessionId.New(),new SessionSkillActivationStore(root),root),()=>count++);
        tool.Dispose();
        using var reader=new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("OMNICORE_DESKTOP_CANCELLED\n")));
        await tool.MonitorCancellationAsync(reader);Assert.Equal(0,count);
    }
}
