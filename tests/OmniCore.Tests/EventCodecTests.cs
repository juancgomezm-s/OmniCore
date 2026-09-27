using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class EventCodecTests
{
    [Fact]
    public async Task RunCreated_round_trips_through_json()
    {
        var codecs = EventCodecs.Create();
        var created = new OmniCore.Domain.RunCreated(OmniCore.Domain.RunId.New(), OmniCore.Domain.SessionId.New(),
            "obj", OmniCore.Domain.RunMode.Act, OmniCore.Domain.ExecutionStrategy.Direct,
            OmniCore.Domain.FailurePolicy.BlockDependents, new OmniCore.Domain.TaskBudget(null, null, 5, 2),
            OmniCore.Domain.TaskId.New(), DateTimeOffset.Now);

        var codec = codecs.CodecFor(created.Type());
        var json = codec.Encode(created);
        Console.WriteLine("JSON=" + json);
        var back = codec.Decode(created.Type(), json);

        Assert.True(back is OmniCore.Domain.RunCreated, "El payload decodificado debe ser RunCreated");
        var restored = (OmniCore.Domain.RunCreated) back;
        Console.WriteLine("DECODED objective=" + restored.Objective + " mode=" + restored.Mode
            + " strategy=" + restored.Strategy + " budget=" + restored.Budget);
        Assert.Equal("obj", restored.Objective);
        Assert.Equal(OmniCore.Domain.RunMode.Act, restored.Mode);
    }

    [Fact]
    public async Task RunCompleted_round_trips()
    {
        var codecs = EventCodecs.Create();
        var completed = new OmniCore.Domain.RunCompleted(OmniCore.Domain.RunId.New(),
            OmniCore.Domain.RunOutcome.Completed);
        var codec = codecs.CodecFor(completed.Type());
        var back = codec.Decode(completed.Type(), codec.Encode(completed));

        Assert.True(back is OmniCore.Domain.RunCompleted, "El payload decodificado debe ser RunCompleted");
        Assert.Equal(OmniCore.Domain.RunOutcome.Completed,
            ((OmniCore.Domain.RunCompleted) back).Outcome);
    }

    [Fact]
    public async Task Unknown_event_type_fails_typed()
    {
        var codecs = EventCodecs.Create();
        Assert.Throws<UnknownEventTypeException>(() =>
            codecs.CodecFor(OmniCore.Domain.EventType.Of("nope.nope")));
    }
}