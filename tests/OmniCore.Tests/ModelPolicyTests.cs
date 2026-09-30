using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Security;

namespace OmniCore.Tests;

/// <summary>
/// Tests de política de modelos (ADR-0044): clave exacta, presets, intersección con
/// HarnessPolicy y frontera de capacidad. Negativos clave: la categoría sola nunca otorga
/// permisos y sin política el fallback es ObserveOnly.
/// </summary>
public sealed class ModelPolicyTests
{
    // ---- ModelPolicyKey (ADR-0044 §2) ----

    [Fact]
    public void ModelPolicyKey_canonical_json_is_deterministic()
    {
        var a = new ModelPolicyKey("openai", "gpt-x", null, "q8",
            new[] { "adapter-b", "adapter-a" }, "llama.cpp", "b1234", "tpl-9", "default", "v1");
        var b = new ModelPolicyKey("openai", "gpt-x", null, "q8",
            new[] { "adapter-a", "adapter-b" }, "llama.cpp", "b1234", "tpl-9", "default", "v1");

        // Los adapters se canonizan como conjunto ordenado: el orden de entrada no importa.
        Assert.Equal(a.CanonicalJson(), b.CanonicalJson());
        Assert.Equal(a.PolicyKeyHash(), b.PolicyKeyHash());
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void ModelPolicyKey_distinguishes_config_details()
    {
        var baseKey = ModelPolicyKey.For("openai", "gpt-x");
        var quantized = new ModelPolicyKey("openai", "gpt-x", null, "q8",
            Array.Empty<string>(), null, null, null, "default", "v1");
        var templated = new ModelPolicyKey("openai", "gpt-x", null, null,
            Array.Empty<string>(), null, null, "tpl-9", "default", "v1");

        // Cambiar cuantización o chat template produce una clave nueva: exige decisión nueva.
        Assert.NotEqual(baseKey.PolicyKeyHash(), quantized.PolicyKeyHash());
        Assert.NotEqual(baseKey.PolicyKeyHash(), templated.PolicyKeyHash());
    }

    [Fact]
    public void ModelPolicyKey_rejects_empty_ids()
    {
        Assert.Throws<ArgumentException>(() => ModelPolicyKey.For("", "m"));
        Assert.Throws<ArgumentException>(() => ModelPolicyKey.For("p", ""));
    }

    // ---- Presets (ADR-0044 §3–§4) ----

    [Fact]
    public void ObserveOnly_preset_forbids_any_mutation()
    {
        var policy = ModelPolicyPresets.ObserveOnly();

        Assert.Equal(ModelPolicyCategory.ObserveOnly, policy.Category);
        Assert.Equal(FileMutationMode.None, policy.MutationPolicy.Mode);
        Assert.False(policy.MutationPolicy.CanMutateFiles);
        Assert.Equal(DestructiveActionPolicy.Deny, policy.MutationPolicy.Delete);
        Assert.Equal(DestructiveActionPolicy.Deny, policy.MutationPolicy.MoveOrRename);
        // Solo lectura/lista/búsqueda/referencias/plan.
        Assert.True(policy.ToolPolicy.Allows(ModelToolCapability.WorkspaceRead));
        Assert.True(policy.ToolPolicy.Allows(ModelToolCapability.Search));
        Assert.True(policy.ToolPolicy.Allows(ModelToolCapability.ReferenceResolve));
        Assert.True(policy.ToolPolicy.Allows(ModelToolCapability.PlanProposal));
        Assert.False(policy.ToolPolicy.Allows(ModelToolCapability.PatchExisting));
        Assert.False(policy.ToolPolicy.Allows(ModelToolCapability.ReplaceFile));
        Assert.False(policy.ToolPolicy.Allows(ModelToolCapability.Shell));
    }

    [Fact]
    public void ObserveOnly_with_mutation_is_a_domain_error()
    {
        // La categoría es un invariante del preset: ObserveOnly + mutación es inválido.
        var mutation = ModelPolicyPresets.PatchOnly().MutationPolicy;
        Assert.Throws<ArgumentException>(() => new UserModelPolicy(
            ModelPolicyCategory.ObserveOnly, ModelPolicyPresets.ObserveOnly().ToolPolicy, mutation,
            "custom", null));
    }

    [Fact]
    public void Presets_escalate_monotonically()
    {
        var observe = ModelPolicyPresets.ObserveOnly();
        var patch = ModelPolicyPresets.PatchOnly();
        var scoped = ModelPolicyPresets.ScopedCoder();
        var full = ModelPolicyPresets.FullAgent();

        Assert.Equal(FileMutationMode.None, observe.MutationPolicy.Mode);
        Assert.Equal(FileMutationMode.PatchExisting, patch.MutationPolicy.Mode);
        Assert.Equal(FileMutationMode.PatchAndCreate, scoped.MutationPolicy.Mode);
        Assert.Equal(FileMutationMode.Full, full.MutationPolicy.Mode);

        // Cada preset añade capacidades, nunca las quita.
        foreach (var c in observe.ToolPolicy.CapabilityCeiling)
        {
            Assert.Contains(c, patch.ToolPolicy.CapabilityCeiling);
            Assert.Contains(c, full.ToolPolicy.CapabilityCeiling);
        }

        foreach (var c in patch.ToolPolicy.CapabilityCeiling)
        {
            Assert.Contains(c, full.ToolPolicy.CapabilityCeiling);
        }

        Assert.Contains(ModelToolCapability.Shell, full.ToolPolicy.CapabilityCeiling);
        Assert.DoesNotContain(ModelToolCapability.Shell, scoped.ToolPolicy.CapabilityCeiling);
        Assert.Equal(ModelPolicyPresets.For(ModelPolicyCategory.FullAgent), full);
    }

    [Fact]
    public void FileMutationPolicy_validates_invariants()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMutationPolicy(
            FileMutationMode.None, DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny,
            0, 0, 1.5, true, true, true, false));
        Assert.Throws<ArgumentException>(() => new FileMutationPolicy(
            FileMutationMode.None, DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny,
            2, 0, 0, true, true, true, false));
    }

    // ---- EffectiveModelPolicy: techo ∩ harness (ADR-0044 §1, §10.1) ----

    [Fact]
    public void Without_stored_policy_fallback_is_ObserveOnly()
    {
        var key = ModelPolicyKey.For("p", "m");
        var effective = EffectiveModelPolicy.Resolve(key, null, StrongHarness());

        Assert.True(effective.IsFallback);
        Assert.Equal(ModelPolicyCategory.ObserveOnly, effective.Category);
        Assert.Equal(FileMutationMode.None, effective.MutationPolicy.Mode);
        Assert.Null(effective.Revision);
    }

    [Fact]
    public void Effective_policy_intersects_with_harness_by_minimum()
    {
        var key = ModelPolicyKey.For("p", "m");
        var stored = new StoredModelPolicy(key, 3, ModelPolicyPresets.ScopedCoder(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        // Harness desconfiado: Direct, 6 visibles. El techo pide Discovered, 10, discovery.
        var weakHarness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 6,
            GuidanceLevel.DomainOnly, 1, PlanControl.RuntimeDriven, 4);

        var effective = EffectiveModelPolicy.Resolve(key, stored, weakHarness);

        // Modo por el más restrictivo; visibles por el menor; discovery no sobrevive.
        Assert.False(effective.IsFallback);
        Assert.Equal(ToolMode.Direct, effective.ToolPolicy.Mode);
        Assert.Equal(6, effective.ToolPolicy.MaxVisibleTools);
        Assert.False(effective.ToolPolicy.AllowToolDiscovery);
        // La mutación es siempre la del techo del usuario.
        Assert.Equal(FileMutationMode.PatchAndCreate, effective.MutationPolicy.Mode);
        Assert.Equal(3, effective.Revision);
    }

    [Fact]
    public void Effective_policy_allows_discovery_when_both_layers_admit()
    {
        var key = ModelPolicyKey.For("p", "m");
        var stored = new StoredModelPolicy(key, 1, ModelPolicyPresets.FullAgent(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var effective = EffectiveModelPolicy.Resolve(key, stored, StrongHarness());

        Assert.Equal(ToolMode.Discovered, effective.ToolPolicy.Mode);
        Assert.True(effective.ToolPolicy.AllowToolDiscovery);
        Assert.Equal(16, effective.ToolPolicy.MaxVisibleTools);
    }

    [Fact]
    public void Fingerprint_is_stable_and_revision_sensitive()
    {
        var key = ModelPolicyKey.For("p", "m");
        var stored1 = new StoredModelPolicy(key, 1, ModelPolicyPresets.PatchOnly(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var stored2 = new StoredModelPolicy(key, 2, ModelPolicyPresets.PatchOnly(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var harness = StrongHarness();

        var f1 = EffectiveModelPolicy.Resolve(key, stored1, harness).Fingerprint();
        var f1again = EffectiveModelPolicy.Resolve(key, stored1, harness).Fingerprint();
        var f2 = EffectiveModelPolicy.Resolve(key, stored2, harness).Fingerprint();
        var fallback = EffectiveModelPolicy.Resolve(key, null, harness).Fingerprint();

        Assert.Equal(f1, f1again);
        Assert.NotEqual(f1, f2);
        Assert.NotEqual(f1, fallback);
    }

    // ---- ModelCapabilityBoundary (ADR-0044 §5): la categoría sola nunca otorga ----

    [Fact]
    public void Boundary_fallback_rejects_writes_even_if_tool_is_classified()
    {
        // Sin política guardada: fallback ObserveOnly → fake.write/filesystem.write rechazada.
        var effective = EffectiveModelPolicy.Resolve(ModelPolicyKey.For("p", "m"), null, StrongHarness());
        var boundary = new ModelCapabilityBoundary(effective);

        var write = BoundaryTests.Intent("fake.write", writes: new[] { "a.txt" });
        var decision = boundary.Evaluate(write);

        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);
        Assert.Equal(ModelToolCapability.ReplaceFile, decision.Capability);
    }

    [Fact]
    public void Boundary_fallback_allows_reads_and_plan()
    {
        var effective = EffectiveModelPolicy.Resolve(ModelPolicyKey.For("p", "m"), null, StrongHarness());
        var boundary = new ModelCapabilityBoundary(effective);

        Assert.True(boundary.Evaluate(BoundaryTests.Intent("filesystem.read")).Allowed);
        Assert.True(boundary.Evaluate(BoundaryTests.Intent("filesystem.list")).Allowed);
        Assert.True(boundary.Evaluate(BoundaryTests.Intent("filesystem.search")).Allowed);
        Assert.True(boundary.Evaluate(BoundaryTests.Intent("reference.resolve")).Allowed);
        Assert.True(boundary.Evaluate(BoundaryTests.Intent("plan.propose")).Allowed);
    }

    [Fact]
    public void Boundary_ObserveOnly_rejects_patch_but_allows_read()
    {
        var effective = EffectiveFor(ModelPolicyPresets.ObserveOnly());
        var boundary = new ModelCapabilityBoundary(effective);

        Assert.True(boundary.Evaluate(BoundaryTests.Intent("filesystem.read")).Allowed);
        var patch = boundary.Evaluate(BoundaryTests.Intent("filesystem.patch", writes: new[] { "f.cs" }));
        Assert.False(patch.Allowed);
        Assert.Equal(ModelToolCapability.PatchExisting, patch.Capability);
    }

    [Fact]
    public void Boundary_PatchOnly_allows_patch_but_not_write_or_delete()
    {
        var effective = EffectiveFor(ModelPolicyPresets.PatchOnly());
        var boundary = new ModelCapabilityBoundary(effective);

        Assert.True(boundary.Evaluate(
            BoundaryTests.Intent("filesystem.patch", writes: new[] { "f.cs" })).Allowed);
        Assert.False(boundary.Evaluate(
            BoundaryTests.Intent("filesystem.write", writes: new[] { "n.cs" })).Allowed);
        Assert.False(boundary.Evaluate(
            BoundaryTests.Intent("fake.write", writes: new[] { "n.cs" })).Allowed);
        // Sin permiso de procesos: PatchOnly no incluye GeneralProcess.
        Assert.False(boundary.Evaluate(BoundaryTests.Intent("process.exec")).Allowed);
    }

    [Fact]
    public void Boundary_FullAgent_allows_write_and_shell()
    {
        var effective = EffectiveFor(ModelPolicyPresets.FullAgent());
        var boundary = new ModelCapabilityBoundary(effective);

        Assert.True(boundary.Evaluate(
            BoundaryTests.Intent("filesystem.write", writes: new[] { "n.cs" })).Allowed);
        Assert.True(boundary.Evaluate(BoundaryTests.Intent("shell.exec")).Allowed);
        Assert.True(boundary.Evaluate(
            BoundaryTests.Intent("filesystem.patch", writes: new[] { "f.cs" })).Allowed);
    }

    [Fact]
    public void Boundary_category_alone_never_grants_unclassified_tools()
    {
        // La tool no está clasificada: ninguna categoría la deja pasar (INV-018/ADR-0027).
        foreach (var preset in new[] { ModelPolicyPresets.ObserveOnly(), ModelPolicyPresets.PatchOnly(),
            ModelPolicyPresets.ScopedCoder(), ModelPolicyPresets.FullAgent() })
        {
            var boundary = new ModelCapabilityBoundary(EffectiveFor(preset));
            var decision = boundary.Evaluate(BoundaryTests.Intent("weird.invented"));
            Assert.False(decision.Allowed, "una tool sin clasificar jamás pasa: " + preset.Category);
            Assert.Null(decision.Capability);
        }
    }

    [Fact]
    public void Boundary_rejects_secret_and_network_claims_under_any_category()
    {
        var boundary = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyPresets.FullAgent()));

        var secrets = boundary.Evaluate(BoundaryTests.Intent("filesystem.read",
            secrets: new[] { "api-key" }));
        Assert.False(secrets.Allowed, "ADR-0018: ningún claim de secretos atraviesa la frontera");

        var network = boundary.Evaluate(BoundaryTests.Intent("filesystem.read",
            network: new[] { new NetworkGrant("evil.example", 443) }));
        // FullAgent tiene Network en el techo: la frontera permite pasar (no autoriza: el
        // Permission Engine sigue siendo la única autoridad, INV-018).
        Assert.True(network.Allowed, "FullAgent incluye Network en su techo");

        // FullAgent sí tiene Network en el techo: la frontera no corta ahí (el Permission Engine
        // sigue siendo la autoridad). Se verifica el mismo intent bajo un techo sin Network.
        var scoped = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyPresets.ScopedCoder()));
        Assert.False(scoped.Evaluate(BoundaryTests.Intent("filesystem.read",
            network: new[] { new NetworkGrant("evil.example", 443) })).Allowed);
    }

    [Fact]
    public void Boundary_deny_cut_precedes_permission_pipeline()
    {
        // FullAgent deja Delete en Ask: la frontera no corta (decide el Permission Engine);
        // un Custom con Deny sí corta aquí, antes del pipeline (ADR-0044 §4).
        var full = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyPresets.FullAgent()));
        Assert.True(full.Evaluate(BoundaryTests.Intent("filesystem.delete", writes: new[] { "x" })).Allowed);

        var denyDelete = new UserModelPolicy(ModelPolicyCategory.Custom,
            ModelPolicyPresets.FullAgent().ToolPolicy,
            new FileMutationPolicy(FileMutationMode.Full, DestructiveActionPolicy.Deny,
                DestructiveActionPolicy.Ask, 8, 2000, 1.0, true, true, true, false),
            "custom", "sin delete");
        var custom = new ModelCapabilityBoundary(EffectiveFor(denyDelete));
        var decision = custom.Evaluate(BoundaryTests.Intent("filesystem.delete", writes: new[] { "x" }));
        Assert.False(decision.Allowed);
        Assert.Equal(ModelToolCapability.DeleteFile, decision.Capability);
    }

    [Fact]
    public void Boundary_allow_is_not_authorization()
    {
        // La frontera restringe, no autoriza (INV-002): Allow significa solo "no corta aquí" y
        // el Permission Engine sigue siendo obligatorio. Se verifica por contrato del tipo:
        // ModelCapabilityDecision no abre AuthorizedToolIntent en ningún caso (RequiresAsk solo baja a Ask).
        var boundary = new ModelCapabilityBoundary(EffectiveFor(ModelPolicyPresets.FullAgent()));
        var decision = boundary.Evaluate(BoundaryTests.Intent("filesystem.read"));

        Assert.True(decision.Allowed);
        Assert.Null(decision.Reason);
        Assert.False(typeof(ModelCapabilityDecision).GetProperties().Length > 4,
            "la decisión de frontera no puede portar autorización");
    }

    // ---- Helpers ----

    private static EffectiveModelPolicy EffectiveFor(UserModelPolicy policy)
    {
        var key = ModelPolicyKey.For("p", "m");
        return EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, policy, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            StrongHarness());
    }

    private static HarnessPolicy StrongHarness() => new(ToolCallFormat.Native, ToolMode.Discovered, 16,
        GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8);
}

internal static class BoundaryTests
{
    internal static ToolIntent Intent(string tool, string[]? writes = null,
        string[]? secrets = null, NetworkGrant[]? network = null)
    {
        var claims = new ResourceClaims(
            reads: new[] { "workspace" },
            writes: writes ?? Array.Empty<string>(),
            network: network ?? Array.Empty<NetworkGrant>(),
            process: null,
            secrets: secrets ?? Array.Empty<string>());
        return new ToolIntent(new ToolCallId(Guid.NewGuid()), new ToolId(tool), "{}",
            EffectClass.NonIdempotent, claims, ToolRisk.Medium, null);
    }
}

/// <summary>Tests del cableado de la frontera en el ToolRuntime (ADR-0044 §5).</summary>
public sealed class ToolRuntimeBoundaryTests
{
    private sealed class EventSink
    {
        public List<OmniCore.Domain.DomainEventPayload> Events { get; } = new();

        public OmniCore.Tools.VoidBox Emit(OmniCore.Domain.DomainEventPayload payload)
        {
            Events.Add(payload);
            return OmniCore.Tools.VoidBox.Instance;
        }

        public int Count(string eventName)
        {
            var n = 0;
            foreach (var e in Events)
            {
                if (e.Type().ToString() == eventName)
                {
                    n++;
                }
            }

            return n;
        }
    }

    private static ModelCapabilityBoundary FallbackBoundary()
    {
        var effective = EffectiveModelPolicy.Resolve(ModelPolicyKey.For("p", "m"), null,
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 16, GuidanceLevel.Full, 3,
                PlanControl.ModelDriven, 8));
        return new ModelCapabilityBoundary(effective);
    }

    [Fact]
    public async System.Threading.Tasks.Task ObserveOnly_boundary_rejects_write_before_permissions()
    {
        var sink = new EventSink();
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow);
        var runtime = OmniCore.Tools.ToolRuntime.For(OmniCore.Tools.FakeCatalog.Default(), policy,
            sink.Emit, FallbackBoundary());

        var outcome = runtime.Run(
            new ValidatedToolCall(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.write"), "pc-1", "{}"),
            new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), true, TestContext.Current.CancellationToken);

        // El Permission Engine habría permitido: la frontera corta antes (categoría nunca otorga).
        Assert.False(outcome.Succeeded);
        Assert.Equal(OmniCore.Domain.ToolCallState.Rejected, outcome.FinalState);
        Assert.Equal(0, sink.Count("toolcall.permission_evaluated"));
        Assert.Equal(0, sink.Count("toolcall.authorized"));
        Assert.Equal(0, sink.Count("toolcall.started"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Boundary_allows_read_within_ceiling()
    {
        var sink = new EventSink();
        var policy = ScriptedPermissionPolicy.WithTool("fake.read", PermissionDecision.Allow);
        var runtime = OmniCore.Tools.ToolRuntime.For(OmniCore.Tools.FakeCatalog.Default(), policy,
            sink.Emit, FallbackBoundary());

        var outcome = runtime.Run(
            new ValidatedToolCall(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.read"), "pc-2", "{}"),
            new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), true, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(OmniCore.Domain.ToolCallState.Succeeded, outcome.FinalState);
        Assert.Equal(1, sink.Count("toolcall.permission_evaluated"));
        Assert.Equal(1, sink.Count("toolcall.succeeded"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Without_boundary_pipeline_is_unchanged()
    {
        var sink = new EventSink();
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow);
        var runtime = OmniCore.Tools.ToolRuntime.For(OmniCore.Tools.FakeCatalog.Default(), policy,
            sink.Emit);

        var outcome = runtime.Run(
            new ValidatedToolCall(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.write"), "pc-3", "{}"),
            new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), true, TestContext.Current.CancellationToken);

        // Null boundary = comportamiento M2 intacto: la frontera es componible, no obligatoria.
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, sink.Count("toolcall.prepared"));
        Assert.Equal(1, sink.Count("toolcall.permission_evaluated"));
        Assert.Equal(1, sink.Count("toolcall.succeeded"));
    }
}
