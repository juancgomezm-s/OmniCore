using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class EscalationEventCodecTests
{
    [Fact]
    public void ModelEscalationRequested_round_trips()
    {
        var codecs = EventCodecs.Create();
        var evt = new ModelEscalationRequested(RunId.New(), "qwen-4b", "qwen-max", EscalationCause.ContextLimit);
        var codec = codecs.CodecFor(evt.Type());
        var json = codec.Encode(evt);
        var back = codec.Decode(evt.Type(), json);

        Assert.True(back is ModelEscalationRequested, "El payload decodificado debe ser ModelEscalationRequested");
        var restored = (ModelEscalationRequested)back;
        Assert.Equal(evt.RunId, restored.RunId);
        Assert.Equal("qwen-4b", restored.FromModel);
        Assert.Equal("qwen-max", restored.ToModel);
        Assert.Equal(EscalationCause.ContextLimit, restored.Cause);
    }

    [Fact]
    public void ModelEscalationApproved_round_trips()
    {
        var codecs = EventCodecs.Create();
        var evt = new ModelEscalationApproved(RunId.New(), "qwen-max", "user@example.com");
        var codec = codecs.CodecFor(evt.Type());
        var json = codec.Encode(evt);
        var back = codec.Decode(evt.Type(), json);

        Assert.True(back is ModelEscalationApproved, "El payload decodificado debe ser ModelEscalationApproved");
        var restored = (ModelEscalationApproved)back;
        Assert.Equal(evt.RunId, restored.RunId);
        Assert.Equal("qwen-max", restored.ToModel);
        Assert.Equal("user@example.com", restored.ApprovedBy);
    }

    [Fact]
    public void ModelEscalationCompleted_round_trips()
    {
        var codecs = EventCodecs.Create();
        var evt = new ModelEscalationCompleted(RunId.New(), "qwen-max");
        var codec = codecs.CodecFor(evt.Type());
        var json = codec.Encode(evt);
        var back = codec.Decode(evt.Type(), json);

        Assert.True(back is ModelEscalationCompleted, "El payload decodificado debe ser ModelEscalationCompleted");
        var restored = (ModelEscalationCompleted)back;
        Assert.Equal(evt.RunId, restored.RunId);
        Assert.Equal("qwen-max", restored.ToModel);
    }

    [Fact]
    public void EscalationCause_all_values_round_trip()
    {
        var codecs = EventCodecs.Create();
        var run = RunId.New();
        foreach (var cause in new[] { EscalationCause.CapabilityMissing, EscalationCause.ContextLimit,
            EscalationCause.RepeatedFailure, EscalationCause.Uncertainty, EscalationCause.ToolReliability,
            EscalationCause.ManualRequest })
        {
            var evt = new ModelEscalationRequested(run, "from", "to", cause);
            var codec = codecs.CodecFor(evt.Type());
            var back = (ModelEscalationRequested)codec.Decode(evt.Type(), codec.Encode(evt));
            Assert.Equal(cause, back.Cause);
        }
    }
}
