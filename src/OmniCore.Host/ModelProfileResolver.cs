namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Models;

/// <summary>
/// Resuelve los hechos declarados y los traits provisionales de M2. Los overrides explícitos
/// prevalecen por trait; las mediciones empíricas llegarán con la suite de M5.
/// </summary>
public sealed class ModelProfileResolver
{
    public EffectiveModelProfile Resolve(ModelDefinition model, ProviderDescriptor? provider,
        IReadOnlyDictionary<string, double>? overrides = null)
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
            model.MaxOutputTokens, new[] { "text" }, formats, false, traits);
    }
}
