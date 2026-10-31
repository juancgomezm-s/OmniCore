using OmniCore.Domain;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class AutomaticDesktopSkillTests
{
    [Theory]
    [InlineData("Prueba una ventana con teclado y ratón.")]
    [InlineData("Quiero que pruebes la ventana de mi aplicación.")]
    [InlineData("Captura la pantalla de Calculadora.")]
    [InlineData("Haz clic aquí.")]
    [InlineData("Vamos a probar esta ventana.")]
    [InlineData("Test the application window and verify its text field.")]
    [InlineData("Type hello into Notepad.")]
    [InlineData("No escribas en la ventana; captura su pantalla.")]
    public void Human_desktop_requests_select_the_skill(string input)
    {
        Assert.Equal(DesktopSkillDemand.TaskType, SkillActivationContext.FromInput(input,"",Path.GetTempPath()).TaskType);
    }

    [Theory]
    [InlineData("Explica cómo probar una ventana.")]
    [InlineData("No controles la ventana.")]
    [InlineData("Refactoriza window.rs y ejecuta sus tests.")]
    [InlineData("Prueba esta función matemática.")]
    [InlineData("Escribe documentación sobre accesibilidad.")]
    [InlineData("Cuánto es 82 por 7?")]
    [InlineData("`Prueba la ventana` es un ejemplo de texto.")]
    public void Unrelated_educational_negated_and_code_requests_do_not_select_desktop(string input)
    {
        Assert.False(DesktopSkillDemand.Matches(input));
    }

    [Fact]
    public void Working_state_cannot_request_desktop_activation()
    {
        Assert.NotEqual(DesktopSkillDemand.TaskType,SkillActivationContext.FromInput("Resume el proyecto.","Prueba la ventana con el ratón.",Path.GetTempPath()).TaskType);
        Assert.Equal("Prueba una ventana",DesktopSkillDemand.ReadHumanText("[{\"type\":\"text\",\"text\":\"Prueba una ventana\"},{\"type\":\"image\",\"text\":\"ignored\"}]").Trim());
        Assert.Equal("Prueba una ventana",DesktopSkillDemand.ReadHumanText("[\"Prueba una ventana\"]"));
        Assert.Equal("Haz clic en el botón",DesktopSkillDemand.ReadHumanText("\"Haz clic en el bot\\u00f3n\""));
    }

    [Theory]
    [InlineData(false,"Automatic",false)]
    [InlineData(true,"Automatic",true)]
    [InlineData(false,"User",true)]
    [InlineData(false,"Model",true)]
    public void Completed_tasks_unload_automatic_selection_but_preserve_pending_or_pinned_selection(bool pending,string origin,bool retained)
    {
        using var f=new Fixture();f.Activate(origin);
        var events=new List<SkillActivationChanged>();var removed=false;
        using(var scope=new AutomaticDesktopSkillScope(f.Session,f.Store,events.Add,()=>pending,()=>removed=true)) { }
        Assert.Equal(retained,f.Store.ReadDetailed(f.Session).Count==1);
        Assert.Equal(!retained,removed);
        Assert.Equal(retained?0:1,events.Count);
        if(!retained)Assert.False(events[0].Activated);
    }

    [Fact]
    public void Failed_lifecycle_write_restores_activation()
    {
        using var f=new Fixture();f.Activate("Automatic");
        var scope=new AutomaticDesktopSkillScope(f.Session,f.Store,_=>throw new IOException("fixture"),()=>false);
        Assert.Throws<IOException>(scope.Dispose);Assert.Single(f.Store.ReadDetailed(f.Session));
    }

    [Fact]
    public void Resumable_turn_is_retained_until_completed_or_abandoned()
    {
        var turn=TurnId.New();var lane=LaneId.New();
        Assert.True(AutomaticDesktopSkillScope.HasOpenTurn([new TurnStarted(turn,lane)],lane));
        Assert.False(AutomaticDesktopSkillScope.HasOpenTurn([new TurnStarted(turn,lane),new TurnCompleted(turn)],lane));
        Assert.False(AutomaticDesktopSkillScope.HasOpenTurn([new TurnStarted(turn,lane),new TurnAbandoned(turn,"cancelled")],lane));
        Assert.False(AutomaticDesktopSkillScope.HasOpenTurn([new TurnStarted(turn,LaneId.New())],lane));
    }

    private sealed class Fixture:IDisposable
    {
        private readonly string _root=Path.Combine(Path.GetTempPath(),"omni-desktop-auto-"+Guid.NewGuid().ToString("N"));
        public SessionId Session{get;}=SessionId.New();
        public SessionSkillActivationStore Store{get;}
        public Fixture(){Directory.CreateDirectory(_root);Store=new(_root);}
        public void Activate(string origin)=>Store.Activate(Session,ScopeLevel.BuiltIn,"desktop-control",new string('a',64),new string('b',64),origin);
        public void Dispose()=>Directory.Delete(_root,true);
    }
}
