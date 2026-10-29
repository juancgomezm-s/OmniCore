namespace OmniCore.Host;

using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>One outbound exposure per canonical web call, durable across agents and compaction.
/// A new read call explicitly recovers cached evidence through the ordinary paid tool path.</summary>
public sealed class WebResearchContextGuard(IWebResearchStore? store = null, TimeProvider? clock = null)
{
    private const int MaximumDeliveryRecords = 2048;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal Projection Project(string runId, IEnumerable<DomainEvent> journal, IEventCodecRegistry codecs)
    {
        var canonical = journal.Select(codecs.Decode).OfType<ToolCallRequested>()
            .Where(call => call.ToolName is "search.web" or "web.read")
            .Select(call => call.ToolCallId.ToString()).ToHashSet(StringComparer.Ordinal);
        var state = store?.Read(runId);
        return new(canonical, state, canonical.Count != 0 || state is not null);
    }

    internal sealed class Projection(HashSet<string> calls, WebResearchState? state, bool protectsContext)
    {
        internal bool ProtectsContext => protectsContext;
        internal bool HasState => state is not null;
        internal bool IsWebMessage(ModelMessage message) => message.Content.Any(block => block switch
        {
            ToolCallBlock call => calls.Contains(call.Id.ToString()),
            ToolResultBlock result => calls.Contains(result.Id.ToString()),
            _ => false,
        });
        internal ModelMessage Apply(ModelMessage message) => !protectsContext ? message
            : new(message.Role, message.Content.Select(block => block switch
            {
                ToolCallBlock call when calls.Contains(call.Id.ToString()) => call with { ArgumentsJson = "{}" },
                ToolResultBlock result when calls.Contains(result.Id.ToString()) && (state is null
                    || state.Status != WebResearchStatus.Active || state.DeliveredToolCalls.ContainsKey(result.Id.ToString())
                    || !state.ToolReceipts.ContainsKey(result.Id.ToString()) || !IsNormalReply(result)) => Empty(result),
                _ => block,
            }).ToArray());
        internal bool IsWebResult(ToolResultBlock result) => calls.Contains(result.Id.ToString());
    }

    internal Invocation Admit(string runId, string invocationId, ModelRequest request, Projection projection)
    {
        if (!projection.ProtectsContext) return new(request, null);
        var projectedMessages = request.Messages.Select(projection.Apply).ToArray();
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var candidates = projectedMessages.SelectMany(message => message.Content).OfType<ToolResultBlock>()
            .Where(projection.IsWebResult).Where(IsNormalReply)
            .GroupBy(result => result.Id.ToString(), StringComparer.Ordinal).Select(group => group.First()).ToArray();
        long admittedBytes = 0;
        if (store is not null && projection.HasState)
            store.Update(runId, state =>
            {
                if (state.ContextDeliveries.Count >= MaximumDeliveryRecords || _clock.GetUtcNow() >= state.Deadline)
                { CloseBudget(state); return; }
                if (state.Status != WebResearchStatus.Active) return;
                if (state.ContextDeliveries.Any(delivery => delivery.Id == invocationId)) return;
                long additional = 0;
                foreach (var result in candidates)
                {
                    var id = result.Id.ToString();
                    if (state.DeliveredToolCalls.ContainsKey(id) || !state.ToolReceipts.ContainsKey(id)) continue;
                    var original = state.Attempts.LastOrDefault(attempt => attempt.ToolCallId == id);
                    if (original is null) continue;
                    var bytes = Bytes(result);
                    var extra = Math.Max(0, bytes - original.ReservedTokens);
                    if (state.EvidenceTokensUsed + state.CachedContextTokensUsed + additional + extra > state.Limits.EvidenceTokens)
                    { CloseBudget(state); continue; }
                    // The initial body was already paid before the web operation. Claim it before model dispatch.
                    allowed.Add(id);
                    admittedBytes += bytes;
                    additional += extra;
                    state.DeliveredToolCalls.Add(id, invocationId);
                }
                if (allowed.Count == 0) return;
                state.CachedContextTokensUsed += additional;
                state.ContextDeliveries.Add(new(invocationId, allowed.ToArray(), admittedBytes, additional, _clock.GetUtcNow()));
            });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var messages = projectedMessages.Select(message => new ModelMessage(message.Role,
            message.Content.Select(block => block switch
            {
                ToolResultBlock result when projection.IsWebResult(result) => allowed.Contains(result.Id.ToString())
                    && seen.Add(result.Id.ToString()) ? result : Empty(result),
                ProviderOpaqueBlock => null,
                ReasoningBlock reasoning => reasoning with { OpaquePayload = null },
                _ => block,
            }).Where(block => block is not null).Cast<ContentBlock>().ToArray())).ToArray();
        var safe = new ModelRequest(request.Model, messages, request.Instructions, request.Tools, request.ToolChoice,
            request.Output, request.Reasoning, request.Cache, continuation: null);
        return new(safe, admittedBytes == 0 ? null : ordinal =>
        {
            var admitted = false;
            store!.Update(runId, state =>
            {
                if (state.ContextDeliveries.Count >= MaximumDeliveryRecords || _clock.GetUtcNow() >= state.Deadline
                    || state.Status != WebResearchStatus.Active
                    || ordinal > 1 && state.EvidenceTokensUsed + state.CachedContextTokensUsed + admittedBytes > state.Limits.EvidenceTokens)
                { CloseBudget(state); return; }
                if (ordinal <= 1) { admitted = true; return; } // Prepaid initial body; still revalidate every physical send.
                state.CachedContextTokensUsed += admittedBytes;
                state.ContextDeliveries.Add(new(invocationId + "/send/" + ordinal, allowed.ToArray(), admittedBytes,
                    admittedBytes, _clock.GetUtcNow(), PhysicalRetry: true));
                admitted = true;
            });
            if (!admitted) throw new InvalidOperationException("Web context retry withheld before generation send.");
        });
    }

    private static bool IsNormalReply(ToolResultBlock result)
    {
        if (result.Content.Count != 1 || result.Content[0] is not TextBlock { Text.Length: > 0 } text) return false;
        try
        {
            using var json = JsonDocument.Parse(text.Text);
            return json.RootElement.TryGetProperty("contextSuppressed", out var suppressed) && suppressed.ValueKind == JsonValueKind.False;
        }
        catch (JsonException) { return false; }
    }
    private static long Bytes(ToolResultBlock result) => result.Content.OfType<TextBlock>().Sum(text => (long)Encoding.UTF8.GetByteCount(text.Text));
    private static void CloseBudget(WebResearchState state)
    {
        if (state.Status != WebResearchStatus.Active) return;
        state.Status = WebResearchStatus.BudgetExhausted;
        state.TerminalReason = "web_context_budget_exhausted";
    }
    private static ToolResultBlock Empty(ToolResultBlock result) => result with { Content = [new TextBlock("")] };
    internal sealed record Invocation(ModelRequest Request, Action<long>? BeforePhysicalSend);
}
