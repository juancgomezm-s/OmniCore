namespace OmniCore.Domain;

using System.Collections.Frozen;

/// <summary>One Host-observed canonical event counter. No prompts, responses or paths are retained.</summary>
public sealed record RuntimeCalibrationObservation(string EventId, string Counter);

public static class RuntimeCalibrationCounters
{
    public static readonly IReadOnlySet<string> Names = new[]
        { "model_steps", "tool_calls", "valid_tool_arguments", "invalid_tool_arguments", "argument_repairs", "tool_successes", "progress_stalls", "completion_gate_rejections" }.ToFrozenSet(StringComparer.Ordinal);
    public const int MinimumSamples = 20;
}