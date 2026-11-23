using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCore.Host;

internal static class LocalAnswerCompletion
{
    public const string Instruction = "\n\nFinish now with a concise final answer to the user's last request. Use only the evidence already available. Do not call tools. Put the final answer in the normal assistant response, not in reasoning.";

    public static bool NeedsFinalAnswer(ModelResponse response) =>
        response.StopReason is StopReason.EndTurn or StopReason.MaxOutputTokens
        && !response.Content.Any(block => block is ToolCallBlock or ImageBlock
            || block is TextBlock text && !string.IsNullOrWhiteSpace(text.Text));

    public static string MissingAnswerDiagnostic(bool hasReasoning, bool unexpectedTools, StopReason stop) =>
        unexpectedTools ? "cli.runtime.turn.tools_in_final_answer"
        : stop == StopReason.MaxOutputTokens ? "cli.runtime.turn.output_exhausted_without_answer"
        : hasReasoning ? "cli.runtime.turn.reasoning_only"
        : "cli.runtime.turn.empty_response";
}
