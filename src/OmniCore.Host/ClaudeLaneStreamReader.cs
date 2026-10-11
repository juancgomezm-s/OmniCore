namespace OmniCore.Host;

using System.Buffers;
using System.Text;
using System.Text.Json;

internal sealed record ClaudeLaneSessionStarted(string SessionId, string Model, IReadOnlyList<string> Tools,
    IReadOnlyDictionary<string, bool> Capabilities);

internal sealed record ClaudeLaneStreamLimits(int MaximumLineBytes = 1_048_576,
    int MaximumTotalBytes = 16_777_216, int MaximumEvents = 20_000,
    int MaximumTools = 1_024, int MaximumToolNameCharacters = 256,
    int MaximumIdentifierCharacters = 512, long? MaximumTokens = null,
    int? MaximumToolCalls = null, decimal? MaximumCostUsd = null)
{
    public void Validate()
    {
        if (MaximumLineBytes is < 256 or > 1_048_576
            || MaximumTotalBytes < MaximumLineBytes || MaximumTotalBytes > 16_777_216
            || MaximumEvents is < 1 or > 20_000 || MaximumTools is < 1 or > 1_024
            || MaximumToolNameCharacters is < 1 or > 256
            || MaximumIdentifierCharacters is < 16 or > 512
            || MaximumTokens is <= 0 || MaximumToolCalls is <= 0 || MaximumCostUsd is <= 0)
            throw new ArgumentOutOfRangeException(nameof(ClaudeLaneStreamLimits));
    }
}

internal sealed record ClaudeLaneUsage(long? InputTokens, long? OutputTokens,
    long? CacheReadInputTokens, long? CacheCreationInputTokens, decimal? TotalCostUsd);

internal sealed record ClaudeLaneStreamOutcome(string SessionId, string Model, IReadOnlyList<string> Tools,
    IReadOnlyDictionary<string, bool> Capabilities, bool IsError, string? ResultText,
    string? StructuredOutputJson, ClaudeLaneUsage Usage,
    IReadOnlyList<string> PermissionDenials, int EventCount, long StreamBytes,
    long TotalTokens, int ToolUseCount);

internal enum ClaudeLaneStreamErrorCode
{
    InvalidUtf8,
    InvalidJson,
    DuplicateJsonMember,
    LimitExceeded,
    InitRequired,
    InvalidInit,
    InvalidInitTool,
    InvalidInitCapabilitiesScalar,
    InvalidInitCapabilityValue,
    InvalidResult,
    SessionMismatch,
    DuplicateTerminal,
    MissingTerminal,
    BudgetExceeded,
}

internal sealed class ClaudeLaneStreamException(ClaudeLaneStreamErrorCode code)
    : Exception("Claude stream-json protocol validation failed.")
{
    public ClaudeLaneStreamErrorCode Code { get; } = code;
}

/// <summary>Strict bounded parser for Claude CLI stream-json. The init callback is awaited before
/// any later event can be consumed so the caller can durably bind the resumed session first.</summary>
internal static class ClaudeLaneStreamReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async ValueTask<ClaudeLaneStreamOutcome> ReadAsync(Stream stdout,
        Func<ClaudeLaneSessionStarted, CancellationToken, ValueTask> onSessionStarted,
        ClaudeLaneStreamLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(onSessionStarted);
        if (!stdout.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stdout));
        var bounds = limits ?? new ClaudeLaneStreamLimits();
        bounds.Validate();
        var state = new ParseState();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var line = new ArrayBufferWriter<byte>(Math.Min(bounds.MaximumLineBytes, 8192));
        long bytes = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                bytes = checked(bytes + read);
                if (bytes > bounds.MaximumTotalBytes) throw Error(ClaudeLaneStreamErrorCode.LimitExceeded);
                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        await ProcessLineAsync(line.WrittenMemory, state, bounds, bytes, onSessionStarted,
                            cancellationToken).ConfigureAwait(false);
                        line.Clear();
                        continue;
                    }
                    if (line.WrittenCount >= bounds.MaximumLineBytes) throw Error(ClaudeLaneStreamErrorCode.LimitExceeded);
                    line.GetSpan(1)[0] = value;
                    line.Advance(1);
                }
            }
            if (line.WrittenCount > 0)
                await ProcessLineAsync(line.WrittenMemory, state, bounds, bytes, onSessionStarted,
                    cancellationToken).ConfigureAwait(false);
            if (state.Init is null) throw Error(ClaudeLaneStreamErrorCode.InitRequired);
            if (state.Terminal is null) throw Error(ClaudeLaneStreamErrorCode.MissingTerminal);
            return state.Terminal with { EventCount = state.EventCount, StreamBytes = bytes };
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async ValueTask ProcessLineAsync(ReadOnlyMemory<byte> bytes, ParseState state,
        ClaudeLaneStreamLimits limits, long streamBytes,
        Func<ClaudeLaneSessionStarted, CancellationToken, ValueTask> onSessionStarted,
        CancellationToken cancellationToken)
    {
        var source = bytes.ToArray();
        var length = source.Length;
        if (length > 0 && source[length - 1] == (byte)'\r') length--;
        if (length == 0) throw Error(ClaudeLaneStreamErrorCode.InvalidJson);
        string json;
        try { json = StrictUtf8.GetString(source, 0, length); }
        catch (DecoderFallbackException) { throw Error(ClaudeLaneStreamErrorCode.InvalidUtf8); }
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException) { throw Error(ClaudeLaneStreamErrorCode.InvalidJson); }
        using (document)
        {
            EnsureUniqueMembers(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Error(ClaudeLaneStreamErrorCode.InvalidJson);
            state.EventCount++;
            if (state.EventCount > limits.MaximumEvents) throw Error(ClaudeLaneStreamErrorCode.LimitExceeded);
            if (state.Terminal is not null) throw Error(ClaudeLaneStreamErrorCode.DuplicateTerminal);
            var type = String(document.RootElement, "type", required: true, maxChars: 64);
            if (type == "system" && String(document.RootElement, "subtype", false, 64) == "init")
            {
                if (state.Init is not null || state.EventCount != 1) throw Error(ClaudeLaneStreamErrorCode.InvalidInit);
                var sessionId = String(document.RootElement, "session_id", required: true, limits.MaximumIdentifierCharacters);
                var model = String(document.RootElement, "model", required: true, limits.MaximumIdentifierCharacters);
                var toolsElement = Required(document.RootElement, "tools", JsonValueKind.Array);
                if (toolsElement.GetArrayLength() > limits.MaximumTools) throw Error(ClaudeLaneStreamErrorCode.LimitExceeded);
                var tools = new List<string>(toolsElement.GetArrayLength());
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tool in toolsElement.EnumerateArray())
                {
                    if (tool.ValueKind != JsonValueKind.String) throw Error(ClaudeLaneStreamErrorCode.InvalidInitTool);
                    var name = tool.GetString();
                    if (!ValidText(name, limits.MaximumToolNameCharacters) || !seen.Add(name!))
                        throw Error(ClaudeLaneStreamErrorCode.InvalidInitTool);
                    tools.Add(name!);
                }
                state.Init = new(sessionId!, model!, tools, ParseCapabilities(document.RootElement, limits));
                await onSessionStarted(state.Init, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (state.Init is null) throw Error(ClaudeLaneStreamErrorCode.InitRequired);
            if (document.RootElement.TryGetProperty("session_id", out var eventSession))
            {
                if (eventSession.ValueKind != JsonValueKind.String || eventSession.GetString() != state.Init.SessionId)
                    throw Error(ClaudeLaneStreamErrorCode.SessionMismatch);
            }
            if (type != "result")
            {
                if (type == "assistant") ObserveAssistantUsage(document.RootElement, state, limits);
                return;
            }
            var resultSession = String(document.RootElement, "session_id", required: true, limits.MaximumIdentifierCharacters);
            if (resultSession != state.Init.SessionId) throw Error(ClaudeLaneStreamErrorCode.SessionMismatch);
            var isErrorElement = Required(document.RootElement, "is_error", JsonValueKind.True, JsonValueKind.False);
            string? resultText = null;
            if (document.RootElement.TryGetProperty("result", out var resultElement))
            {
                if (resultElement.ValueKind != JsonValueKind.String) throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
                resultText = resultElement.GetString();
            }
            string? structured = null;
            if (document.RootElement.TryGetProperty("structured_output", out var structuredElement))
                structured = structuredElement.GetRawText();
            var usage = ParseUsage(document.RootElement, out var cost);
            if (state.InputTokens is not null || state.OutputTokens is not null)
                usage = usage with { InputTokens = state.InputTokens, OutputTokens = state.OutputTokens };
            if (limits.MaximumTokens is not null && (usage.InputTokens is null || usage.OutputTokens is null)
                || limits.MaximumCostUsd is not null && cost is null)
                throw Error(ClaudeLaneStreamErrorCode.BudgetExceeded);
            if (usage.InputTokens is { } finalInput && usage.OutputTokens is { } finalOutput
                && (finalInput > long.MaxValue - finalOutput
                    || limits.MaximumTokens is { } maxFinalTokens && finalInput + finalOutput > maxFinalTokens))
                throw Error(ClaudeLaneStreamErrorCode.BudgetExceeded);
            if ((state.InputTokens ?? 0) > long.MaxValue - (state.OutputTokens ?? 0))
                throw Error(ClaudeLaneStreamErrorCode.BudgetExceeded);
            if (limits.MaximumCostUsd is { } maximumCost && cost is { } actualCost && actualCost > maximumCost)
                throw Error(ClaudeLaneStreamErrorCode.BudgetExceeded);
            var denials = ParseDenials(document.RootElement, limits);
            state.Terminal = new(state.Init.SessionId, state.Init.Model, state.Init.Tools, state.Init.Capabilities,
                isErrorElement.GetBoolean(), resultText, structured,
                usage with { TotalCostUsd = cost }, denials, 0, streamBytes,
                (state.InputTokens ?? 0) + (state.OutputTokens ?? 0), state.ToolUseCount);
        }
    }

    private static IReadOnlyDictionary<string, bool> ParseCapabilities(JsonElement root, ClaudeLaneStreamLimits limits)
    {
        if (!root.TryGetProperty("capabilities", out var capabilities))
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        if (capabilities.ValueKind == JsonValueKind.Array)
        {
            if (capabilities.GetArrayLength() > limits.MaximumTools)
                throw Error(ClaudeLaneStreamErrorCode.LimitExceeded);
            var listed = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var item in capabilities.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !ValidText(item.GetString(), limits.MaximumToolNameCharacters)
                    || listed.ContainsKey(item.GetString()!))
                    throw Error(ClaudeLaneStreamErrorCode.InvalidInitCapabilityValue);
                listed.Add(item.GetString()!, true);
            }
            return listed;
        }
        if (capabilities.ValueKind != JsonValueKind.Object)
            throw Error(ClaudeLaneStreamErrorCode.InvalidInitCapabilitiesScalar);
        if (capabilities.EnumerateObject().Count() > limits.MaximumTools)
            throw Error(ClaudeLaneStreamErrorCode.LimitExceeded);
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in capabilities.EnumerateObject())
        {
            if (item.Name.Length is 0 || item.Name.Length > limits.MaximumToolNameCharacters
                || item.Name.Any(char.IsControl) || item.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Error(ClaudeLaneStreamErrorCode.InvalidInitCapabilityValue);
            result.Add(item.Name, item.Value.GetBoolean());
        }
        return result;
    }

    private static void ObserveAssistantUsage(JsonElement root, ParseState state, ClaudeLaneStreamLimits limits)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        if (message.TryGetProperty("usage", out var usage))
        {
            if (usage.ValueKind != JsonValueKind.Object) throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
            var input = OptionalNonnegativeInt64(usage, "input_tokens") ?? 0;
            var output = OptionalNonnegativeInt64(usage, "output_tokens") ?? 0;
            try
            {
                state.InputTokens = checked((state.InputTokens ?? 0) + input);
                state.OutputTokens = checked((state.OutputTokens ?? 0) + output);
            }
            catch (OverflowException) { throw Error(ClaudeLaneStreamErrorCode.BudgetExceeded); }
            if (limits.MaximumTokens is { } maxTokens
                && state.InputTokens.Value > maxTokens - state.OutputTokens.Value)
                throw Error(ClaudeLaneStreamErrorCode.BudgetExceeded);
        }
        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind != JsonValueKind.Array) throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var kind)
                    && kind.ValueKind == JsonValueKind.String && kind.GetString() == "tool_use")
                {
                    if (state.ToolUseCount == int.MaxValue) throw Error(ClaudeLaneStreamErrorCode.BudgetExceeded);
                    state.ToolUseCount++;
                    if (limits.MaximumToolCalls is { } maximum && state.ToolUseCount > maximum)
                        throw Error(ClaudeLaneStreamErrorCode.BudgetExceeded);
                }
            }
        }
    }

    private static ClaudeLaneUsage ParseUsage(JsonElement root, out decimal? cost)
    {
        long? input = null, output = null, cacheRead = null, cacheWrite = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.ValueKind != JsonValueKind.Object) throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
            input = OptionalNonnegativeInt64(usage, "input_tokens");
            output = OptionalNonnegativeInt64(usage, "output_tokens");
            cacheRead = OptionalNonnegativeInt64(usage, "cache_read_input_tokens");
            cacheWrite = OptionalNonnegativeInt64(usage, "cache_creation_input_tokens");
        }
        cost = null;
        if (root.TryGetProperty("total_cost_usd", out var costElement))
        {
            if (costElement.ValueKind != JsonValueKind.Number || !costElement.TryGetDecimal(out var parsed) || parsed < 0)
                throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
            cost = parsed;
        }
        return new(input, output, cacheRead, cacheWrite, cost);
    }

    private static IReadOnlyList<string> ParseDenials(JsonElement root, ClaudeLaneStreamLimits limits)
    {
        if (!root.TryGetProperty("permission_denials", out var denied)) return [];
        if (denied.ValueKind != JsonValueKind.Array || denied.GetArrayLength() > limits.MaximumTools)
            throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
        var result = new List<string>(denied.GetArrayLength());
        foreach (var item in denied.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
            var tool = String(item, "tool_name", true, limits.MaximumToolNameCharacters);
            var reason = String(item, "reason", false, limits.MaximumIdentifierCharacters) ?? "";
            result.Add(reason.Length == 0 ? tool! : tool + ": " + reason);
        }
        return result;
    }

    private static long? OptionalNonnegativeInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed) || parsed < 0)
            throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
        return parsed;
    }

    private static JsonElement Required(JsonElement parent, string name, params JsonValueKind[] kinds)
    {
        if (!parent.TryGetProperty(name, out var value) || !kinds.Contains(value.ValueKind))
            throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
        return value;
    }

    private static string? String(JsonElement parent, string name, bool required, int maxChars)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            if (required) throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
            return null;
        }
        if (value.ValueKind != JsonValueKind.String) throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
        var text = value.GetString();
        if (!ValidText(text, maxChars)) throw Error(ClaudeLaneStreamErrorCode.InvalidResult);
        return text;
    }

    private static bool ValidText(string? value, int limit) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= limit && !value.Any(char.IsControl);

    private static void EnsureUniqueMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Error(ClaudeLaneStreamErrorCode.DuplicateJsonMember);
                EnsureUniqueMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) EnsureUniqueMembers(child);
    }

    private static ClaudeLaneStreamException Error(ClaudeLaneStreamErrorCode code) => new(code);
    private sealed class ParseState
    {
        public ClaudeLaneSessionStarted? Init;
        public ClaudeLaneStreamOutcome? Terminal;
        public int EventCount;
        public long? InputTokens;
        public long? OutputTokens;
        public int ToolUseCount;
    }
}
