namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>A restrictive intent check run before permission evaluation and again before execution.</summary>
public interface IToolIntentBoundary
{
    ModelCapabilityDecision Evaluate(ToolIntent intent);
}

/// <summary>A boundary may apply an additional, narrowly scoped rule to an intent produced by
/// a Core tool that ToolRuntime has already verified is callable only from a native user action.</summary>
public interface INativeUserActionIntentBoundary : IToolIntentBoundary
{
    ModelCapabilityDecision EvaluateNativeUserAction(ToolIntent intent);
}
