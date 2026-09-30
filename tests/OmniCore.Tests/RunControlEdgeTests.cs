using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>Casos borde de RunControlService que encontró la revisión de GLM: un Run inexistente no está activo.</summary>
public sealed class RunControlEdgeTests
{
    [Fact]
    public void Interrupt_and_cancel_of_a_run_that_does_not_exist_fail_as_not_active_and_write_nothing()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        TestRun.Open(store, session);
        var control = new RunControlService(store, EventCodecs.Create());
        var before = store.ReadFrom(session, 1).Count;
        var ghost = RunId.New();

        Assert.Throws<RunNotActiveException>(() => control.Interrupt(session, ghost));
        Assert.Throws<RunNotActiveException>(() => control.CancelRun(session, ghost));
        Assert.Equal(before, store.ReadFrom(session, 1).Count);
    }
}
