using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Engine;

namespace OmniCore.Tests;

public sealed class PlanProposeSerializationRegressionTests
{
    [Fact]
    public void Canonical_mutation_escapes_item_id_and_roundtrips_without_changing_kind()
    {
        const string hostileId = "P1\",\"kind\":\"cancel";
        var json = Mutations.CanonicalJson("complete", hostileId, "reason \"quoted\"");

        using var document = JsonDocument.Parse(json);
        Assert.Equal("complete", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal(hostileId, document.RootElement.GetProperty("itemId").GetString());
        var planItem = PlanItemId.New();
        var resolved = Mutations.ResolveDeclared(json, new Dictionary<string, PlanItemId> { [hostileId] = planItem });
        Assert.NotNull(resolved);
        Assert.Equal(PlanMutationKind.Complete, resolved!.Kind);
        Assert.Equal(planItem, resolved.ItemId);
        Assert.Equal("reason \"quoted\"", resolved.Reason);
    }

    [Fact]
    public void Resolve_declared_rejects_duplicate_or_malformed_fields_and_accepts_normal_item()
    {
        var item = PlanItemId.New();
        var map = new Dictionary<string, PlanItemId> { ["P1"] = item };
        Assert.Equal(PlanMutationKind.Complete,
            Mutations.ResolveDeclared(Mutations.CanonicalJson("complete", "P1", null), map)!.Kind);
        Assert.Null(Mutations.ResolveDeclared("{\"kind\":\"complete\",\"kind\":\"cancel\",\"itemId\":\"P1\"}", map));
        Assert.Null(Mutations.ResolveDeclared("{\"kind\":\"mystery\",\"itemId\":\"P1\"}", map));
        Assert.Null(Mutations.ResolveDeclared("{\"kind\":\"complete\",\"itemId\":42}", map));
        Assert.Null(Mutations.ResolveDeclared("{\"kind\":\"complete\",\"itemId\":\"P1\",\"reason\":42}", map));
    }
}
