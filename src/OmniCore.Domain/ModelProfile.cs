namespace OmniCore.Domain;

/// <summary>
/// Perfil de modelo efectivo: hechos + traits resueltos por ModelQualificationKey (ADR-0007 §1).
/// Por modelo, no por Turn.
/// </summary>
public sealed class EffectiveModelProfile
{
    public string ModelId { get; }

    /// <summary>Ruta de la configuración efectiva; las APIs anteriores conservan su ruta por defecto.</summary>
    public RouteId RouteId { get; }

    public long ContextWindow { get; }

    public long RecommendedUsableContext { get; }

    public long MaxOutputTokens { get; }

    public IReadOnlyList<string> InputModalities { get; }

    public IReadOnlyList<ToolCallFormat> ToolCallFormats { get; }

    public bool SupportsParallelTools { get; }

    /// <summary>Rasgos empíricos o heurísticos, por nombre de trait (0..1).</summary>
    public IReadOnlyDictionary<string, double> Traits { get; }

    public EffectiveModelProfile(string modelId, long contextWindow, long recommendedUsableContext,
        long maxOutputTokens, IReadOnlyList<string> inputModalities, IReadOnlyList<ToolCallFormat> toolCallFormats,
        bool supportsParallelTools, IReadOnlyDictionary<string, double> traits, RouteId? routeId = null)
    {
        ModelId = modelId;
        RouteId = routeId ?? OmniCore.Domain.RouteId.ForDefaultModel(modelId);
        ContextWindow = contextWindow;
        RecommendedUsableContext = recommendedUsableContext;
        MaxOutputTokens = maxOutputTokens;
        InputModalities = inputModalities;
        ToolCallFormats = toolCallFormats;
        SupportsParallelTools = supportsParallelTools;
        Traits = traits;
    }

    public double Trait(string name, double fallback) => Traits.TryGetValue(name, out var v) ? v! : fallback;
}

/// <summary>
/// Política del harness derivada del EffectiveModelProfile por una función pura (ADR-0007 §3).
/// Todo valor configurable se prueba con tests.
/// </summary>
public sealed record ContextManagementPolicy(int ExternalizeAboveCharacters, int CompressBodyCharacters,
    int RecentTailItems, int CompactAfterItems, int MaxCheckpointCharacters)
{
    public static ContextManagementPolicy Default { get; } = new(4096, 1200, 12, 24, 6000);
}

public sealed class HarnessPolicy
{
    public ToolCallFormat ToolCallFormat { get; }

    public ToolMode ToolMode { get; }

    public int MaxVisibleTools { get; }

    public GuidanceLevel GuidanceLevel { get; }

    public int RepairAttempts { get; }

    public PlanControl PlanControl { get; }

    public int StallThresholdTurns { get; }

    public ContextManagementPolicy ContextManagement { get; }

    public HarnessPolicy(ToolCallFormat toolCallFormat, ToolMode toolMode, int maxVisibleTools,
        GuidanceLevel guidanceLevel, int repairAttempts, PlanControl planControl, int stallThresholdTurns,
        ContextManagementPolicy? contextManagement = null)
    {
        ToolCallFormat = toolCallFormat;
        ToolMode = toolMode;
        MaxVisibleTools = maxVisibleTools;
        GuidanceLevel = guidanceLevel;
        RepairAttempts = repairAttempts;
        PlanControl = planControl;
        StallThresholdTurns = stallThresholdTurns;
        ContextManagement = contextManagement ?? ContextManagementPolicy.Default;
    }
}

/// <summary>Resolvedor puro: EffectiveModelProfile → HarnessPolicy (ADR-0007 §3).</summary>
public sealed class HarnessPolicyResolver
{
    public HarnessPolicy Resolve(EffectiveModelProfile profile)
    {
        var toolReliability = profile.Trait("ToolCallReliability", 0.5);
        var instructionFollowing = profile.Trait("InstructionFollowing", 0.5);
        var planTracking = profile.Trait("PlanTrackingReliability", 0.5);
        var repair = profile.Trait("ToolErrorRecovery", 0.5);
        var multiStep = profile.Trait("MultiStepExecutionReliability", 0.5);

        var fmt = ToolCallFormat.Native;
        if (!profile.ToolCallFormats.Contains(ToolCallFormat.Native) || toolReliability < 0.4)
        {
            fmt = profile.ToolCallFormats.Contains(ToolCallFormat.Grammar)
                ? ToolCallFormat.Grammar
                : ToolCallFormat.PromptedJson;
        }

        // Un modelo con tool calling débil recibe pocas tools explícitas; discovery exige
        // fiabilidad suficiente (ADR-0044 §4).
        var mode = toolReliability >= 0.6 ? ToolMode.Discovered : ToolMode.Direct;
        var visible = profile.RecommendedUsableContext < 16_000 ? 6 : 12;
        var guidance = instructionFollowing >= 0.7 ? GuidanceLevel.Full
            : instructionFollowing >= 0.4 ? GuidanceLevel.DomainOnly : GuidanceLevel.Off;
        var repairs = repair >= 0.6 ? 3 : 1;
        var plan = planTracking >= 0.7 ? PlanControl.ModelDriven
            : planTracking >= 0.4 ? PlanControl.Assisted : PlanControl.RuntimeDriven;
        var stall = multiStep >= 0.6 ? 8 : 4;
        var contextPolicy = profile.RecommendedUsableContext < 16_000
            ? new ContextManagementPolicy(2048, 600, 8, 16, 3000)
            : new ContextManagementPolicy(4096, 1200, 12, 24, 6000);

        return new HarnessPolicy(fmt, mode, visible, guidance, repairs, plan, stall, contextPolicy);
    }
}

/// <summary>Selección de modelo y parámetros para una request concreta (spec §19).</summary>
public sealed class ModelSelection
{
    public ModelIdValue Model { get; }

    public long ContextBudget { get; }

    public ToolMode ToolMode { get; }

    public ReasoningRequest? Reasoning { get; }

    /// <summary>Ruta concreta; los journals antiguos conservan la ruta 1:1 del modelo (ADR-0046).</summary>
    public RouteId RouteId { get; }

    public ModelSelection(ModelIdValue model, long contextBudget, ToolMode toolMode, ReasoningRequest? reasoning,
        RouteId? routeId = null)
    {
        Model = model;
        ContextBudget = contextBudget;
        ToolMode = toolMode;
        Reasoning = reasoning;
        RouteId = routeId ?? OmniCore.Domain.RouteId.ForDefaultModel(model.ToString());
    }
}

/// <summary>Id de modelo como valor (el tipo fuerte se agregará con el registro en M2).</summary>
public sealed class ModelIdValue
{
    private readonly string _id;

    public ModelIdValue(string id) => _id = id;

    public override string ToString() => _id;

    public override bool Equals(object? other) => other is ModelIdValue m && m._id.Equals(_id, StringComparison.Ordinal);

    public override int GetHashCode() => _id.GetHashCode();
}

/// <summary>Elección de tool que pide el modelo (spec §37).</summary>
public enum ToolMode
{
    Direct,
    Discovered,
    Code,
}
