using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCore.Tests;

public sealed class MutationLedgerLifetimeTests
{
    private static FileMutationPolicy BuildPolicy() => new(
        FileMutationMode.PatchExisting, DestructiveActionPolicy.Deny,
        DestructiveActionPolicy.Deny, 3, 200, 0.5, true, true, true, false);

    [Fact]
    public void Pending_validations_survive_beginning_another_turn()
    {
        var ledger = new FileReadRegistry().Ledger;
        ledger.Bind(BuildPolicy());
        var firstId = ToolCallId.New();
        var secondId = ToolCallId.New();
        ledger.BeginTurn();
        ledger.RecordMutation("first.cs", 1, 2, firstId);
        var original = Assert.Single(ledger.PendingValidations());
        Assert.Equal(1, original.Turn);
        Assert.Equal(firstId, original.ToolCallId);
        ledger.BeginTurn();
        Assert.Equal(2, ledger.TurnNumber);
        Assert.Equal(0, ledger.ChangedLinesThisTurn);
        Assert.Equal(3, ledger.ChangedLinesThisRun);
        Assert.Equal(1, ledger.MutationsThisRun);
        Assert.Equal(original, Assert.Single(ledger.PendingValidations()));
        ledger.RecordMutation("second.cs", 0, 1, secondId);
        Assert.Collection(ledger.PendingValidations(),
            first => Assert.Equal(original, first),
            second =>
            {
                Assert.Equal("second.cs", second.Path);
                Assert.Equal(2, second.Turn);
                Assert.Equal(secondId, second.ToolCallId);
            });
    }

    [Fact]
    public void Fresh_registry_does_not_inherit_pending_validations_from_existing_registry()
    {
        var registry = new FileReadRegistry();
        var policy = BuildPolicy();
        registry.Ledger.Bind(policy);
        registry.Ledger.BeginTurn();
        var callId = ToolCallId.New();
        registry.Ledger.RecordMutation("fixture.cs", 1, 2, callId);

        var original = Assert.Single(registry.Ledger.PendingValidations());
        Assert.Equal("fixture.cs", original.Path);
        Assert.Equal(callId, original.ToolCallId);
        Assert.Equal(1, original.Turn);

        var fresh = new FileReadRegistry();
        fresh.Ledger.Bind(policy);
        Assert.Empty(fresh.Ledger.PendingValidations());
        Assert.Equal(original, Assert.Single(registry.Ledger.PendingValidations()));
    }

    [Fact]
    public void Taking_pending_validations_returns_original_entry_and_clears_only_pending_list()
    {
        var registry = new FileReadRegistry();
        registry.Ledger.Bind(BuildPolicy());
        registry.Ledger.BeginTurn();
        var callId = ToolCallId.New();
        registry.Ledger.RecordMutation("fixture.cs", 1, 2, callId);
        var original = Assert.Single(registry.Ledger.PendingValidations());

        var taken = registry.Ledger.TakePendingValidations();
        Assert.Equal(original, Assert.Single(taken));
        Assert.Empty(registry.Ledger.PendingValidations());
        Assert.Empty(registry.Ledger.TakePendingValidations());
        Assert.Equal(original, Assert.Single(taken));
        Assert.Equal(1, registry.Ledger.MutationsThisRun);
        Assert.Equal(3, registry.Ledger.ChangedLinesThisRun);
    }
}
