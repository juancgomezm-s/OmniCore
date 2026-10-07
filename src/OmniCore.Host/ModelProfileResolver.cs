namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Models;

/// <summary>
/// Resuelve <see cref="EffectiveModelProfile"/> combinando las cuatro fuentes de ADR-0007 §1:
/// hechos declarados, heurísticas, overrides del usuario y traits empíricos de la suite de
/// cualificación (capa Empirical, M5). Resolución por campo:
/// <c>traits = UserOverrides ?? Empirical (solo si el perfil está Qualified/Calibrated/Stale) ?? Heuristic</c>.
/// Nunca resuelve por nombre ni tamaño de modelo (INV-010).
/// </summary>
public sealed class ModelProfileResolver
{
    public EffectiveModelProfile Resolve(ModelDefinition model, ProviderDescriptor? provider,
        IReadOnlyDictionary<string, double>? overrides = null,
        IReadOnlyDictionary<string, double>? empiricalTraits = null, ModelRoute? route = null)
    {
        var size = model.ParameterCountBillions;
        var provisional = size is null ? 0.5 : size <= 9 ? 0.35 : size < 27 ? 0.55 : 0.7;
        var traits = new Dictionary<string, double> {
            ["ToolCallReliability"] = provisional,
            ["PlanTrackingReliability"] = provisional,
            ["InstructionFollowing"] = provisional,
            ["ToolErrorRecovery"] = provisional,
            ["MultiStepExecutionReliability"] = provisional,
        };
        // Capa Empirical (ADR-0007 §1): reemplaza a la heurística para los traits que la suite midió.
        // El llamador solo aporta traits de un perfil Qualified/Calibrated/Stale (nunca por nombre).
        if (empiricalTraits is not null)
        {
            foreach (var entry in empiricalTraits)
            {
                if (entry.Value < 0 || entry.Value > 1)
                    throw new ArgumentOutOfRangeException(nameof(empiricalTraits), "Los traits deben estar entre 0 y 1");
                traits[entry.Key] = entry.Value;
            }
        }
        if (overrides is not null)
        {
            foreach (var entry in overrides)
            {
                if (entry.Value < 0 || entry.Value > 1)
                    throw new ArgumentOutOfRangeException(nameof(overrides), "Los traits deben estar entre 0 y 1");
                traits[entry.Key] = entry.Value;
            }
        }

        var formats = new List<ToolCallFormat>();
        if (provider?.SupportsNativeToolCalls == true) formats.Add(ToolCallFormat.Native);
        if (provider?.SupportsGrammarPerRequest == true) formats.Add(ToolCallFormat.Grammar);
        if (formats.Count == 0) formats.Add(ToolCallFormat.PromptedJson);
        return new EffectiveModelProfile(model.Id, model.ContextWindow, model.RecommendedUsableContext,
            model.MaxOutputTokens, new[] { "text" }, formats, false, traits, route?.Id,
            route is null ? model.ReasoningCapability : route.ReasoningCapability);
    }
}
