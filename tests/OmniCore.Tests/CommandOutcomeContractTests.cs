using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class CommandOutcomeContractTests
{
    [Fact]
    public void Legacy_ack_constructor_and_factories_keep_status_error_and_unknown_outcome()
    {
        var legacy = new CommandAck("command-1", "accepted-by-legacy", "legacy detail");
        Assert.Equal("command-1", legacy.CommandId);
        Assert.Equal("accepted-by-legacy", legacy.Status);
        Assert.Equal("legacy detail", legacy.Error);
        Assert.Null(legacy.Outcome);
        Assert.Null(legacy.FirstSeq);
        Assert.Null(legacy.LastSeq);

        var ok = CommandAck.Ok("command-2");
        Assert.Equal("ok", ok.Status);
        Assert.Null(ok.Error);
        Assert.Null(ok.Outcome);

        var fail = CommandAck.Fail("command-3", "failure");
        Assert.Equal("error", fail.Status);
        Assert.Equal("failure", fail.Error);
        Assert.Null(fail.Outcome);
    }

    [Fact]
    public void Explicit_runtime_outcome_variants_and_deferred_reason_are_preserved()
    {
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, RuntimeCommandOutcome.Accepted().Kind);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, RuntimeCommandOutcome.Rejected().Kind);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, RuntimeCommandOutcome.NoOp().Kind);

        var deferred = RuntimeCommandOutcome.Deferred("awaiting approval");
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, deferred.Kind);
        Assert.Equal("awaiting approval", deferred.Reason);

        var ack = new CommandAck("command-4", "legacy-ok", null, RuntimeCommandOutcome.Rejected(), 7, 9);
        Assert.Equal("legacy-ok", ack.Status); // Status is compatibility data, not an inferred outcome.
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        Assert.Null(ack.Outcome?.Reason);
        Assert.Equal(7, ack.FirstSeq);
        Assert.Equal(9, ack.LastSeq);
    }

    [Fact]
    public void Deferred_requires_a_reason()
    {
        Assert.Throws<ArgumentException>(() => RuntimeCommandOutcome.Deferred(""));
        Assert.Throws<ArgumentException>(() => RuntimeCommandOutcome.Deferred(" \t "));
    }

    [Fact]
    public void Sequence_ranges_are_optional_paired_positive_and_ordered()
    {
        var empty = new CommandAck("empty", "ok", null, RuntimeCommandOutcome.NoOp());
        Assert.Null(empty.FirstSeq);
        Assert.Null(empty.LastSeq);

        var singleSequence = new CommandAck("single", "ok", null, RuntimeCommandOutcome.Accepted(), 4, 4);
        Assert.Equal(4, singleSequence.FirstSeq);
        Assert.Equal(4, singleSequence.LastSeq);

        Assert.Throws<ArgumentException>(() =>
            new CommandAck("unpaired-first", "ok", null, RuntimeCommandOutcome.Accepted(), 1));
        Assert.Throws<ArgumentException>(() =>
            new CommandAck("unpaired-last", "ok", null, RuntimeCommandOutcome.Accepted(), lastSeq: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CommandAck("zero-first", "ok", null, RuntimeCommandOutcome.Accepted(), 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CommandAck("zero-last", "ok", null, RuntimeCommandOutcome.Accepted(), 1, 0));
        Assert.Throws<ArgumentException>(() =>
            new CommandAck("descending", "ok", null, RuntimeCommandOutcome.Accepted(), 5, 4));
    }

    [Fact]
    public void Protocol_ack_contract_does_not_reference_domain_types()
    {
        var assemblyReferences = typeof(CommandAck).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name);
        Assert.DoesNotContain("OmniCore.Domain", assemblyReferences);
        Assert.Equal(typeof(CommandAck).Assembly, typeof(RuntimeCommandOutcome).Assembly);
    }
}
