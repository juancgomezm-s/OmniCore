namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>A restrictive intent check run before permission evaluation and again before execution.</summary>
public interface IToolIntentBoundary
{
    ModelCapabilityDecision Evaluate(ToolIntent intent);
}
