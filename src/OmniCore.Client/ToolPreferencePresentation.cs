namespace OmniCore.Client;

/// <summary>Matches declared catalog metadata for the preference editor, never execution authority.</summary>
public static class ToolPreferencePresentation
{
    public static bool IsAlternative(ToolInspectionRow source, ToolInspectionRow target) =>
        !string.Equals(source.ToolId, target.ToolId, StringComparison.Ordinal)
        // Built-ins have distinct purposes even when their broad tags coincide
        // (e.g. filesystem.list/read). Suggest contributed alternatives, not arbitrary
        // replacements between standard tools. Existing rules remain removable.
        && !target.Source.StartsWith("BuiltIn:", StringComparison.Ordinal)
        && source.Tags is not null && target.Tags is not null
        && source.EffectClass == target.EffectClass
        && Risk(source.Risk) is int originalRisk && Risk(target.Risk) is int alternativeRisk
        && alternativeRisk <= originalRisk
        && source.Tags.Order(StringComparer.Ordinal).SequenceEqual(target.Tags.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    // Availability is deliberately re-evaluated by the runtime for each turn. A user
    // preference can be configured before an ACT run; diagnostics do not grant permission.
    private static int? Risk(string? risk) => risk switch
    { "Low" => 0, "Medium" => 1, "High" => 2, "Critical" => 3, _ => null };
}
