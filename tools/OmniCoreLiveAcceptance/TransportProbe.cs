using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCoreLiveAcceptance;

internal sealed partial class AcceptanceRunner
{
    // A diagnostic, NOT tool acceptance. No generated code or tools are executed.
    // The 64-token cap is request-local; configured model/server values stay untouched.
    public async Task<TransportProbeEvidence> RunTransportProbeAsync(PreflightReport preflight, string directory)
    {
        var provider = ConnectModelProvider(preflight.ModelAlias, preflight.ModelId);
        var started = DateTimeOffset.UtcNow;
        DateTimeOffset? first = null;
        var headersReceived = false;
        var completed = false;
        var counts = new Dictionary<string, int>();
        long textCharacters = 0, reasoningCharacters = 0, toolArgumentCharacters = 0;
        string? failure = null, stopReason = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var request = new ModelRequest(new ModelSelection(new ModelIdValue(preflight.ModelId),
                preflight.ContextWindow!.Value, ToolMode.Direct, null,
                maxOutputTokens: preflight.ModelAlias == "qwen" ? 64 : null),
            [new ModelMessage(MessageRole.User, [new TextBlock("Reply with OK only.")])],
            "This is a bounded connectivity diagnostic. Do not use tools.", [], ToolChoice.None(), null, null, null, null);
        try
        {
            await foreach (var item in provider.StreamAsync(request, timeout.Token))
            {
                first ??= DateTimeOffset.UtcNow;
                var kind = item.GetType().Name;
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                switch (item)
                {
                    case ResponseStarted: headersReceived = true; break;
                    case TextDelta delta: textCharacters += delta.Text.Length; break;
                    case ReasoningDelta delta: reasoningCharacters += delta.Text.Length; break;
                    case ToolArgumentsDelta delta: toolArgumentCharacters += delta.PartialJson.Length; break;
                    case ResponseCompleted done: completed = true; stopReason = done.Response.StopReason.ToString(); break;
                }
            }
        }
        catch (OperationCanceledException) { failure = "bounded_45_second_timeout"; }
        catch (Exception exception) { failure = SafeProviderFailureCode(exception); }
        var evidence = new TransportProbeEvidence(preflight.ModelId, started, DateTimeOffset.UtcNow,
            first, headersReceived, completed, counts, textCharacters, reasoningCharacters,
            toolArgumentCharacters, stopReason, failure, request.Model.MaxOutputTokens,
            (provider as IModelRequestAttemptBound)?.MaximumGenerationRequestAttempts);
        File.WriteAllText(Path.Combine(directory, "transport-probe.json"), JsonSerializer.Serialize(evidence, JsonOptions.Indented));
        return evidence;
    }
}

internal sealed record TransportProbeEvidence(string ModelId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    DateTimeOffset? FirstEventAt, bool ResponseHeadersReceived, bool Completed,
    IReadOnlyDictionary<string, int> EventCounts, long TextCharacters, long ReasoningCharacters,
    long ToolArgumentCharacters, string? StopReason, string? Failure, long? RequestOutputCap,
    long? MaximumHttpAttempts);
