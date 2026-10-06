using OmniCore.Domain;
using OmniCore.Engine;

namespace OmniCore.Tests;

/// <summary>
/// Tests del router puro de modelos (spec §21, ADR-0007, ADR-0011 §2).
/// Verifica selección por alias y perfil efectivo, sin ramificar por nombres de modelo/proveedor (INV-007).
/// </summary>
public sealed class ModelRouterTests
{
    [Fact]
    public void Physical_route_preference_distinguishes_two_routes_of_the_same_logical_model()
    {
        var first = CreateCandidate("same-model");
        var second = first with { Route = new ModelRoute("fixture", "http://127.0.0.1:9090/v1",
            ProviderFamily.OpenAiChatCompatible, null, "same-model") };
        var policy = new RoutingPolicy(new Dictionary<RoutingTaskKind, IReadOnlyList<RouteId>>
        {
            [RoutingTaskKind.Implementation] = [new RouteId(second.RouteId.Value)],
        }, false);
        var decision = ModelRouter.Select(CreateRequest(), [first, second], policy);
        Assert.Same(second, decision.Chosen);
        Assert.Equal("same-model", decision.Chosen.ModelId);
        Assert.Equal(second.RouteId, decision.Chosen.RouteId);
        var rejected = Assert.Single(decision.Rejected);
        Assert.Equal(first.RouteId, rejected.RouteId);
        Assert.Equal("same-model", rejected.ModelId);
        Assert.Equal(RouteRejection.NotInPreferences, rejected.Reason);
    }

    // ----- Helpers para crear perfiles y candidatos -----

    private static EffectiveModelProfile CreateProfile(
        long recommendedUsableContext = 32_000,
        ToolCallFormat[]? toolFormats = null,
        string[]? inputModalities = null,
        string modelId = "test-model")
    {
        return new EffectiveModelProfile(
            modelId,
            contextWindow: recommendedUsableContext * 2,
            recommendedUsableContext: recommendedUsableContext,
            maxOutputTokens: 4096,
            inputModalities: inputModalities ?? Array.Empty<string>(),
            toolCallFormats: toolFormats ?? new[] { ToolCallFormat.Native },
            supportsParallelTools: true,
            traits: new Dictionary<string, double>
            {
                ["ToolCallReliability"] = 0.8,
                ["InstructionFollowing"] = 0.8,
                ["PlanTrackingReliability"] = 0.8,
                ["ToolErrorRecovery"] = 0.8,
                ["MultiStepExecutionReliability"] = 0.8,
            });
    }

    private static RouteCandidate CreateCandidate(
        string alias,
        EffectiveModelProfile? profile = null,
        bool isLocal = true,
        bool available = true,
        bool hasWritePolicy = true,
        decimal? pricePerMillionTokensUsd = null)
    {
        return new RouteCandidate(
            ModelRoute.DefaultForModel(alias, "fixture", "http://127.0.0.1:8080/v1", ProviderFamily.OpenAiChatCompatible),
            alias,
            profile ?? CreateProfile(),
            isLocal,
            available,
            hasWritePolicy,
            pricePerMillionTokensUsd);
    }

    private static RoutingRequest CreateRequest(
        RoutingTaskKind kind = RoutingTaskKind.Implementation,
        bool requiresWrite = false,
        long estimatedContextTokens = 1000,
        string[]? requiredCapabilities = null,
        bool localOnly = false)
    {
        return new RoutingRequest(kind, requiresWrite, estimatedContextTokens, requiredCapabilities ?? Array.Empty<string>(), localOnly);
    }

    private static RoutingPolicy CreatePolicy(
        Dictionary<RoutingTaskKind, string[]>? preferences = null,
        bool preferLocal = false)
    {
        var dict = new Dictionary<RoutingTaskKind, IReadOnlyList<RouteId>>();
        if (preferences is not null)
        {
            foreach (var kvp in preferences)
            {
                dict[kvp.Key] = kvp.Value.Select(RouteId.ForDefaultModel).ToArray();
            }
        }
        return new RoutingPolicy(dict, preferLocal);
    }

    // ----- 1. Preference order -----

    [Fact]
    public void Select_preference_order_chooses_first_matching_alias()
    {
        var candidateA = CreateCandidate("model-a", CreateProfile(recommendedUsableContext: 64_000));
        var candidateB = CreateCandidate("model-b", CreateProfile(recommendedUsableContext: 32_000));
        var candidateC = CreateCandidate("model-c", CreateProfile(recommendedUsableContext: 16_000));

        var request = CreateRequest(RoutingTaskKind.Implementation);
        var policy = CreatePolicy(new Dictionary<RoutingTaskKind, string[]>
        {
            [RoutingTaskKind.Implementation] = new[] { "model-c", "model-a", "model-b" }
        });

        var decision = ModelRouter.Select(request, new[] { candidateA, candidateB, candidateC }, policy);

        Assert.Equal("model-c", decision.Chosen.ModelId);
        Assert.Equal(2, decision.Rejected.Count);
    }

    [Fact]
    public void Select_preference_order_skips_unavailable_in_list()
    {
        var candidateA = CreateCandidate("model-a", available: false);
        var candidateB = CreateCandidate("model-b");
        var candidateC = CreateCandidate("model-c");

        var request = CreateRequest(RoutingTaskKind.Implementation);
        var policy = CreatePolicy(new Dictionary<RoutingTaskKind, string[]>
        {
            [RoutingTaskKind.Implementation] = new[] { "model-a", "model-b", "model-c" }
        });

        var decision = ModelRouter.Select(request, new[] { candidateA, candidateB, candidateC }, policy);

        Assert.Equal("model-b", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, r => r.ModelId == "model-a" && r.Reason == RouteRejection.Unavailable);
    }

    // ----- 2. Unavailable skipped -----

    [Fact]
    public void Select_rejects_unavailable_candidate()
    {
        var candidate = CreateCandidate("unavailable-model", available: false);
        var available = CreateCandidate("available-model");

        var request = CreateRequest();
        var policy = CreatePolicy();

        var decision = ModelRouter.Select(request, new[] { candidate, available }, policy);

        Assert.Equal("available-model", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, r => r.ModelId == "unavailable-model" && r.Reason == RouteRejection.Unavailable);
    }

    // ----- 3. Context too small -----

    [Fact]
    public void Select_rejects_context_too_small()
    {
        var smallContext = CreateCandidate("small", CreateProfile(recommendedUsableContext: 1000));
        var largeContext = CreateCandidate("large", CreateProfile(recommendedUsableContext: 100_000));

        var request = CreateRequest(estimatedContextTokens: 50_000);
        var policy = CreatePolicy();

        var decision = ModelRouter.Select(request, new[] { smallContext, largeContext }, policy);

        Assert.Equal("large", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, r => r.ModelId == "small" && r.Reason == RouteRejection.ContextTooSmall);
    }

    // ----- 4. Missing native tools -----

    [Fact]
    public void Select_rejects_missing_native_tools_capability()
    {
        var noNativeTools = CreateCandidate("no-native", CreateProfile(toolFormats: new[] { ToolCallFormat.PromptedJson }));
        var hasNativeTools = CreateCandidate("has-native", CreateProfile(toolFormats: new[] { ToolCallFormat.Native }));

        var request = CreateRequest(requiredCapabilities: new[] { "native-tools" });
        var policy = CreatePolicy();

        var decision = ModelRouter.Select(request, new[] { noNativeTools, hasNativeTools }, policy);

        Assert.Equal("has-native", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, r => r.ModelId == "no-native" && r.Reason == RouteRejection.MissingCapability);
    }

    [Fact]
    public void Select_rejects_missing_vision_capability()
    {
        var noVision = CreateCandidate("no-vision", CreateProfile(inputModalities: Array.Empty<string>()));
        var hasVision = CreateCandidate("has-vision", CreateProfile(inputModalities: new[] { "image", "text" }));

        var request = CreateRequest(requiredCapabilities: new[] { "vision" });
        var policy = CreatePolicy();

        var decision = ModelRouter.Select(request, new[] { noVision, hasVision }, policy);

        Assert.Equal("has-vision", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, r => r.ModelId == "no-vision" && r.Reason == RouteRejection.MissingCapability);
    }

    // ----- 5. Local-only -----

    [Fact]
    public void Select_rejects_remote_when_local_only()
    {
        var local = CreateCandidate("local", isLocal: true);
        var remote = CreateCandidate("remote", isLocal: false);

        var request = CreateRequest(localOnly: true);
        var policy = CreatePolicy();

        var decision = ModelRouter.Select(request, new[] { local, remote }, policy);

        Assert.Equal("local", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, r => r.ModelId == "remote" && r.Reason == RouteRejection.NotLocal);
    }

    [Fact]
    public void Select_allows_remote_when_not_local_only()
    {
        var local = CreateCandidate("local", isLocal: true);
        var remote = CreateCandidate("remote", isLocal: false);

        var request = CreateRequest(localOnly: false);
        var policy = CreatePolicy();

        var decision = ModelRouter.Select(request, new[] { local, remote }, policy);

        Assert.Equal("local", decision.Chosen.ModelId); // first in list wins when no preference
        Assert.DoesNotContain(decision.Rejected, r => r.Reason == RouteRejection.NotLocal);
    }

    // ----- 6. Write task without policy rejected (ADR-0044) -----

    [Fact]
    public void Select_rejects_write_task_without_write_policy()
    {
        var noWritePolicy = CreateCandidate("no-write-policy", hasWritePolicy: false);
        var hasWritePolicy = CreateCandidate("has-write-policy", hasWritePolicy: true);

        var request = CreateRequest(requiresWrite: true);
        var policy = CreatePolicy();

        var decision = ModelRouter.Select(request, new[] { noWritePolicy, hasWritePolicy }, policy);

        Assert.Equal("has-write-policy", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, r => r.ModelId == "no-write-policy" && r.Reason == RouteRejection.NoWritePolicy);
    }

    [Fact]
    public void Select_accepts_same_candidate_for_non_write_task_without_write_policy()
    {
        // El mismo candidato SIN política de escritura SÍ es aceptado para tarea NO de escritura
        var noWritePolicy = CreateCandidate("no-write-policy", hasWritePolicy: false);

        var request = CreateRequest(requiresWrite: false);
        var policy = CreatePolicy();

        var decision = ModelRouter.Select(request, new[] { noWritePolicy }, policy);

        Assert.Equal("no-write-policy", decision.Chosen.ModelId);
        Assert.Empty(decision.Rejected);
    }

    // ----- 7. PreferLocal reordering -----

    [Fact]
    public void Select_prefer_local_reorders_survivors_local_first_stable()
    {
        // Candidatos en orden: remote1, local1, remote2, local2
        var remote1 = CreateCandidate("remote1", isLocal: false);
        var local1 = CreateCandidate("local1", isLocal: true);
        var remote2 = CreateCandidate("remote2", isLocal: false);
        var local2 = CreateCandidate("local2", isLocal: true);

        var request = CreateRequest();
        var policy = CreatePolicy(preferLocal: true);

        var decision = ModelRouter.Select(request, new[] { remote1, local1, remote2, local2 }, policy);

        Assert.Equal("local1", decision.Chosen.ModelId); // local1 estaba antes que local2 en la lista original
        // Verificar orden en rechazados: local2, remote1, remote2 (locales primero, luego remotos, ambos estables)
        var rejectedAliases = decision.Rejected.Select(r => r.ModelId).ToArray();
        Assert.Equal(new[] { "local2", "remote1", "remote2" }, rejectedAliases);
    }

    [Fact]
    public void Select_without_prefer_local_keeps_preference_order()
    {
        var remote = CreateCandidate("remote", isLocal: false);
        var local = CreateCandidate("local", isLocal: true);

        var request = CreateRequest();
        var policy = CreatePolicy(
            preferences: new Dictionary<RoutingTaskKind, string[]> { [RoutingTaskKind.Implementation] = new[] { "remote", "local" } },
            preferLocal: false);

        var decision = ModelRouter.Select(request, new[] { local, remote }, policy);

        // Preferencia dice "remote" primero, así que gana aunque local esté primero en la lista de candidatos
        Assert.Equal("remote", decision.Chosen.ModelId);
    }

    // ----- 8. Kind without preferences uses given order -----

    [Fact]
    public void Select_kind_without_preferences_uses_candidate_order()
    {
        var first = CreateCandidate("first");
        var second = CreateCandidate("second");
        var third = CreateCandidate("third");

        // Policy sin preferencias para Implementation
        var policy = CreatePolicy(
            preferences: new Dictionary<RoutingTaskKind, string[]> { [RoutingTaskKind.Meta] = new[] { "third" } },
            preferLocal: false);

        var request = CreateRequest(RoutingTaskKind.Implementation);
        var decision = ModelRouter.Select(request, new[] { first, second, third }, policy);

        Assert.Equal("first", decision.Chosen.ModelId);
        Assert.Equal(2, decision.Rejected.Count);
        Assert.Contains(decision.Rejected, r => r.ModelId == "second" && r.Reason == RouteRejection.NotInPreferences);
        Assert.Contains(decision.Rejected, r => r.ModelId == "third" && r.Reason == RouteRejection.NotInPreferences);
    }

    [Fact]
    public void Select_empty_preference_list_treats_all_as_eligible()
    {
        var first = CreateCandidate("first");
        var second = CreateCandidate("second");

        var policy = CreatePolicy(
            preferences: new Dictionary<RoutingTaskKind, string[]> { [RoutingTaskKind.Implementation] = Array.Empty<string>() },
            preferLocal: false);

        var request = CreateRequest(RoutingTaskKind.Implementation);
        var decision = ModelRouter.Select(request, new[] { first, second }, policy);

        Assert.Equal("first", decision.Chosen.ModelId);
    }

    // ----- 9. No route → typed error listing reasons -----

    [Fact]
    public void Select_no_survivor_throws_NoRouteAvailableException_with_all_rejections()
    {
        var candidate1 = CreateCandidate("unavailable", available: false);
        var candidate2 = CreateCandidate("small-context", CreateProfile(recommendedUsableContext: 100));
        var candidate3 = CreateCandidate("no-native-tools", CreateProfile(toolFormats: new[] { ToolCallFormat.PromptedJson }));

        var request = CreateRequest(
            estimatedContextTokens: 1000,
            requiredCapabilities: new[] { "native-tools" });
        var policy = CreatePolicy();

        var ex = Assert.Throws<NoRouteAvailableException>(() =>
            ModelRouter.Select(request, new[] { candidate1, candidate2, candidate3 }, policy));

        Assert.Equal(3, ex.Rejected.Count);
        Assert.Contains(ex.Rejected, r => r.ModelId == "unavailable" && r.Reason == RouteRejection.Unavailable);
        Assert.Contains(ex.Rejected, r => r.ModelId == "small-context" && r.Reason == RouteRejection.ContextTooSmall);
        Assert.Contains(ex.Rejected, r => r.ModelId == "no-native-tools" && r.Reason == RouteRejection.MissingCapability);

        // Mensaje en español
        Assert.Contains("No hay modelo disponible", ex.Message);
    }

    // ----- 10. Name-independence: swapped aliases produce choice by profile/facts, not names -----

    [Fact]
    public void Select_name_independence_swapped_aliases_choice_follows_profile_not_name()
    {
        // Dos perfiles: A es mejor (contexto grande, native tools), B es peor (contexto pequeño, sin native tools)
        var profileA = CreateProfile(recommendedUsableContext: 100_000, toolFormats: new[] { ToolCallFormat.Native });
        var profileB = CreateProfile(recommendedUsableContext: 1_000, toolFormats: new[] { ToolCallFormat.PromptedJson });

        // Caso 1: alias "alpha" -> perfil A, alias "beta" -> perfil B
        var alphaA = CreateCandidate("alpha", profileA);
        var betaB = CreateCandidate("beta", profileB);

        var request = CreateRequest(estimatedContextTokens: 50_000, requiredCapabilities: new[] { "native-tools" });
        var policy = CreatePolicy();

        var decision1 = ModelRouter.Select(request, new[] { alphaA, betaB }, policy);
        Assert.Equal("alpha", decision1.Chosen.ModelId); // Elige por perfil, no por nombre

        // Caso 2: MISMOS perfiles, PERO aliases intercambiados
        // alias "alpha" -> perfil B (el malo), alias "beta" -> perfil A (el bueno)
        var alphaB = CreateCandidate("alpha", profileB);
        var betaA = CreateCandidate("beta", profileA);

        var decision2 = ModelRouter.Select(request, new[] { alphaB, betaA }, policy);
        Assert.Equal("beta", decision2.Chosen.ModelId); // Ahora gana "beta" porque tiene el buen perfil

        // La decisión sigue al PERFIL, no al NOMBRE del alias
        Assert.NotEqual(decision1.Chosen.ModelId, decision2.Chosen.ModelId);
    }

    [Fact]
    public void Select_name_independence_preference_list_references_aliases_not_profiles()
    {
        // La lista de preferencias refiere ALIASES, no perfiles
        // Ambos candidatos cumplen requisitos duros; la preferencia decide
        var profileGood = CreateProfile(recommendedUsableContext: 100_000);
        var profileAlsoGood = CreateProfile(recommendedUsableContext: 80_000);

        // Preferencia dice "preferred-alias" primero
        var preferredButSlightlyWorse = CreateCandidate("preferred-alias", profileAlsoGood);
        var notPreferredButBetter = CreateCandidate("other-alias", profileGood);

        var request = CreateRequest(estimatedContextTokens: 50_000);
        var policy = CreatePolicy(
            preferences: new Dictionary<RoutingTaskKind, string[]> { [RoutingTaskKind.Implementation] = new[] { "preferred-alias", "other-alias" } });

        var decision = ModelRouter.Select(request, new[] { notPreferredButBetter, preferredButSlightlyWorse }, policy);

        // "preferred-alias" está en la lista de preferencias y cumple requisitos -> gana
        // La preferencia del usuario por alias tiene prioridad sobre calidad marginal del perfil
        Assert.Equal("preferred-alias", decision.Chosen.ModelId);
    }

    // ----- Additional: Multiple capabilities required -----

    [Fact]
    public void Select_requires_all_capabilities()
    {
        var hasBoth = CreateCandidate("both", CreateProfile(
            toolFormats: new[] { ToolCallFormat.Native },
            inputModalities: new[] { "image", "text" }));
        var hasOnlyNative = CreateCandidate("only-native", CreateProfile(
            toolFormats: new[] { ToolCallFormat.Native },
            inputModalities: Array.Empty<string>()));
        var hasOnlyVision = CreateCandidate("only-vision", CreateProfile(
            toolFormats: new[] { ToolCallFormat.PromptedJson },
            inputModalities: new[] { "image" }));

        var request = CreateRequest(requiredCapabilities: new[] { "native-tools", "vision" });
        var policy = CreatePolicy();

        var decision = ModelRouter.Select(request, new[] { hasOnlyNative, hasOnlyVision, hasBoth }, policy);

        Assert.Equal("both", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, r => r.ModelId == "only-native" && r.Reason == RouteRejection.MissingCapability);
        Assert.Contains(decision.Rejected, r => r.ModelId == "only-vision" && r.Reason == RouteRejection.MissingCapability);
    }

    // ----- Additional: PreferLocal only affects survivors, not rejections -----

    [Fact]
    public void Select_prefer_local_only_reorders_survivors_not_rejected()
    {
        var localUnavailable = CreateCandidate("local-unavail", isLocal: true, available: false);
        var remoteAvailable = CreateCandidate("remote-avail", isLocal: false, available: true);

        var request = CreateRequest();
        var policy = CreatePolicy(preferLocal: true);

        var decision = ModelRouter.Select(request, new[] { localUnavailable, remoteAvailable }, policy);

        Assert.Equal("remote-avail", decision.Chosen.ModelId);
        // El local no disponible se rechaza por Unavailable, no por NotLocal
        Assert.Contains(decision.Rejected, r => r.ModelId == "local-unavail" && r.Reason == RouteRejection.Unavailable);
        Assert.DoesNotContain(decision.Rejected, r => r.Reason == RouteRejection.NotLocal);
    }

    [Fact]
    public void Prefer_local_keeps_the_preference_order_among_local_candidates()
    {
        // Orden de entrada distinto del de preferencia: gana la preferencia, no el orden de la lista.
        var candidates = new[]
        {
            CreateCandidate("local-b", isLocal: true),
            CreateCandidate("remote", isLocal: false),
            CreateCandidate("local-a", isLocal: true),
        };
        var policy = CreatePolicy(new Dictionary<RoutingTaskKind, string[]>
        {
            [RoutingTaskKind.Implementation] = ["remote", "local-a", "local-b"],
        }, preferLocal: true);

        var decision = ModelRouter.Select(CreateRequest(), candidates, policy);

        Assert.Equal("local-a", decision.Chosen.ModelId);
    }
}
