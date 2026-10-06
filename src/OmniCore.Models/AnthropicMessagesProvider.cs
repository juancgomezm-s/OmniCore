namespace OmniCore.Models;

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Opciones del adaptador Anthropic Messages.</summary>
public sealed class AnthropicProviderOptions
{
    /// <summary>Versión de la API enviada en <c>anthropic-version</c>.</summary>
    public string ApiVersion { get; init; } = "2023-06-01";

    /// <summary><c>max_tokens</c> cuando la request no pide razonamiento con presupuesto.</summary>
    public int DefaultMaxOutputTokens { get; init; } = 8192;

    /// <summary>Margen de salida visible que se suma al presupuesto de razonamiento.</summary>
    public int OutputTokensAboveReasoningBudget { get; init; } = 4096;

    public OpenAiProviderOptions Resilience { get; init; } = new();
}

/// <summary>
/// Adaptador nativo de la Messages API de Anthropic (ADR-0005 §1): nunca pasa por un adapter
/// OpenAI-compatible. Los bloques de razonamiento con firma (<c>thinking</c>/<c>redacted_thinking</c>)
/// se conservan intactos en <see cref="ProviderState"/> y solo se reenvían al MISMO modelo, como
/// exige la API para continuar un turno con tools (ProviderOpaque, replay SameModel).
/// </summary>
public sealed class AnthropicMessagesProvider : IModelProvider, IReportsRateLimits, IModelRequestAttemptBound
{
    private const string OpaqueKindPrefix = "anthropic.messages.ProviderOpaque/";
    private readonly ProviderDescriptor _descriptor;
    private readonly ISecretProvider _secrets;
    private readonly Func<HttpClient> _httpFactory;
    private readonly AnthropicProviderOptions _options;
    private readonly ProviderResilience _resilience;

    public AnthropicMessagesProvider(ProviderDescriptor descriptor, ISecretProvider secrets)
        : this(descriptor, secrets, static () => new HttpClient(), null) { }

    public AnthropicMessagesProvider(ProviderDescriptor descriptor, ISecretProvider secrets,
        Func<HttpClient> httpFactory, AnthropicProviderOptions? options = null)
    {
        if (descriptor.Family != ProviderFamily.AnthropicMessages)
            throw new ArgumentException("El descriptor no es de la familia AnthropicMessages.", nameof(descriptor));
        _descriptor = descriptor;
        _secrets = secrets;
        _httpFactory = httpFactory;
        _options = options ?? new AnthropicProviderOptions();
        if (_options.DefaultMaxOutputTokens < 1 || _options.OutputTokensAboveReasoningBudget < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Los límites de salida deben ser mayores que cero.");
        _resilience = _options.Resilience.CircuitCatalog?.Acquire(descriptor.Id, _options.Resilience)
            ?? new ProviderResilience(_options.Resilience, descriptor.Id);
    }

    /// <summary>La API informa tokens (incluidos los de caché); no informa costo ni cuota.</summary>
    public ProviderCapabilities Capabilities { get; } = new(true, false, true);
    public long MaximumGenerationRequestAttempts => _resilience.MaximumGenerationRequestAttempts;

    /// <summary>Ventanas de rate limit de la última respuesta (cuota informada, nunca estimada).</summary>
    public IReadOnlyList<RateLimitWindow> LastRateLimits { get; private set; } = [];

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var halfOpenProbe = _resilience.EnterCircuit();
        HttpResponseMessage response;
        HttpClient http;
        try
        {
            var body = BuildBody(request, _options);
            (response, http) = await _resilience.SendWithRetryAsync(_httpFactory,
                () => CreateHttpRequest(body, cancellationToken), ProviderResilience.ErrorFromBody,
                static status => status is 408 or 429 or 529 || status >= 500, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _resilience.AbandonProbe(halfOpenProbe);
            throw;
        }
        LastRateLimits = RateLimitQuotaParser.Parse(response.Headers, DateTimeOffset.UtcNow);
        var completed = false;
        try
        {
            yield return new ResponseStarted(0);
            await foreach (var item in ReadSseAsync(response, request, cancellationToken).ConfigureAwait(false))
            {
                if (item is ResponseCompleted) { completed = true; _resilience.MarkSuccess(); }
                yield return item;
            }
            if (!completed) throw new ModelProviderException("ProviderUnavailable", "El stream terminó sin completar la respuesta.");
        }
        finally
        {
            response.Dispose();
            http.Dispose();
            if (!completed)
            {
                if (cancellationToken.IsCancellationRequested) _resilience.AbandonProbe(halfOpenProbe);
                else _resilience.MarkFailure();
            }
        }
    }

    private HttpRequestMessage CreateHttpRequest(string body, CancellationToken cancellationToken)
    {
        var baseUrl = _descriptor.BaseUrl.TrimEnd('/');
        var url = baseUrl.EndsWith("/v1", StringComparison.Ordinal) ? baseUrl + "/messages" : baseUrl + "/v1/messages";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("anthropic-version", _options.ApiVersion);
        if (_descriptor.Auth.Kind == AuthKind.ApiKey && _descriptor.Auth.SecretRef is not null)
        {
            var secret = _secrets.GetSecret(_descriptor.Auth.SecretRef, cancellationToken);
            request.Headers.TryAddWithoutValidation("x-api-key", secret.Value());
        }
        return request;
    }

    /// <summary>Construye el body de la Messages API (público para tests deterministas).</summary>
    internal static string BuildBody(ModelRequest request, AnthropicProviderOptions options)
    {
        var reasoning = request.Reasoning ?? request.Model.Reasoning;
        var budget = reasoning?.BudgetTokens is > 0 ? reasoning.BudgetTokens.Value : 0;
        if (budget is > 0 and < 1024)
            throw new ArgumentOutOfRangeException(nameof(request), "Manual thinking budget must be at least 1024 tokens.");
        var maxOutput = request.Model.MaxOutputTokens ?? (budget > 0
            ? (long)budget + options.OutputTokensAboveReasoningBudget
            : options.DefaultMaxOutputTokens);
        if (maxOutput <= budget)
            throw new ArgumentOutOfRangeException(nameof(request), "max_tokens must exceed the manual thinking budget.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", request.Model.Model.ToString());
            w.WriteNumber("max_tokens", maxOutput);
            w.WriteBoolean("stream", true);
            if (budget > 0)
            {
                w.WriteStartObject("thinking");
                w.WriteString("type", "enabled");
                w.WriteNumber("budget_tokens", budget);
                w.WriteEndObject();
            }

            var cache = request.Cache is { MaxBreakpoints: > 0 };
            if (!string.IsNullOrEmpty(request.Instructions))
            {
                w.WriteStartArray("system");
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", request.Instructions);
                if (cache) WriteCacheControl(w);
                w.WriteEndObject();
                w.WriteEndArray();
            }

            WriteMessages(w, request);

            if (request.Tools.Count > 0 && request.ToolChoice.Mode != "none")
            {
                w.WriteStartArray("tools");
                foreach (var tool in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("name", tool.Name);
                    w.WriteString("description", tool.Description);
                    w.WritePropertyName("input_schema");
                    WriteRawJsonOrEmptyObject(w, tool.InputSchemaJson);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartObject("tool_choice");
                if (request.ToolChoice.Mode == "exact" && request.ToolChoice.ToolName is not null)
                {
                    w.WriteString("type", "tool");
                    w.WriteString("name", request.ToolChoice.ToolName);
                }
                else
                {
                    w.WriteString("type", "auto");
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCacheControl(Utf8JsonWriter w)
    {
        w.WriteStartObject("cache_control");
        w.WriteString("type", "ephemeral");
        w.WriteEndObject();
    }

    /// <summary>
    /// Anthropic exige alternar user/assistant: los mensajes consecutivos del mismo rol se unen y
    /// los resultados de tools viajan como bloques <c>tool_result</c> en un mensaje user.
    /// </summary>
    private static void WriteMessages(Utf8JsonWriter w, ModelRequest request)
    {
        var turns = new List<(string Role, List<ContentBlock> Blocks)>();
        foreach (var message in request.Messages)
        {
            if (message.Role == MessageRole.System) continue; // el system va en "system"
            var role = message.Role == MessageRole.Assistant ? "assistant" : "user";
            if (turns.Count > 0 && turns[^1].Role == role) turns[^1].Blocks.AddRange(message.Content);
            else turns.Add((role, new List<ContentBlock>(message.Content)));
        }

        // Bloques de razonamiento firmados del turno anterior: solo si la continuación es de este modelo.
        JsonElement[]? replayThinking = null;
        if (request.Continuation is { } continuation &&
            continuation.Kind == OpaqueKindPrefix + request.Model.Model.ToString())
        {
            try
            {
                using var doc = JsonDocument.Parse(continuation.PayloadJson);
                if (doc.RootElement.TryGetProperty("thinking", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                    replayThinking = blocks.EnumerateArray().Select(b => b.Clone()).ToArray();
            }
            catch (JsonException) { /* un estado opaco inválido se ignora en vez de corromper la request */ }
        }
        var lastAssistant = turns.FindLastIndex(t => t.Role == "assistant");

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in request.Messages.SelectMany(m => m.Content).OfType<ToolCallBlock>())
            ids[call.Id.ToString()] = string.IsNullOrEmpty(call.ProviderCallId) ? call.Id.ToString() : call.ProviderCallId!;

        w.WriteStartArray("messages");
        for (var t = 0; t < turns.Count; t++)
        {
            var (role, blocks) = turns[t];
            w.WriteStartObject();
            w.WriteString("role", role);
            w.WriteStartArray("content");
            if (t == lastAssistant && replayThinking is not null)
                foreach (var block in replayThinking) block.WriteTo(w);
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case TextBlock text when text.Text.Length > 0:
                        w.WriteStartObject();
                        w.WriteString("type", "text");
                        w.WriteString("text", text.Text);
                        w.WriteEndObject();
                        break;
                    case ToolCallBlock call:
                        w.WriteStartObject();
                        w.WriteString("type", "tool_use");
                        w.WriteString("id", ids[call.Id.ToString()]);
                        w.WriteString("name", call.ToolName);
                        w.WritePropertyName("input");
                        WriteRawJsonOrEmptyObject(w, call.ArgumentsJson);
                        w.WriteEndObject();
                        break;
                    case ToolResultBlock result:
                        w.WriteStartObject();
                        w.WriteString("type", "tool_result");
                        w.WriteString("tool_use_id", ids.GetValueOrDefault(result.Id.ToString(), result.Id.ToString()));
                        w.WriteString("content", string.Concat(result.Content.OfType<TextBlock>().Select(b => b.Text)));
                        if (result.IsError) w.WriteBoolean("is_error", true);
                        w.WriteEndObject();
                        break;
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteRawJsonOrEmptyObject(Utf8JsonWriter w, string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    doc.RootElement.WriteTo(w);
                    return;
                }
            }
            catch (JsonException) { }
        }
        w.WriteStartObject();
        w.WriteEndObject();
    }

    private async IAsyncEnumerable<ModelStreamEvent> ReadSseAsync(HttpResponseMessage response, ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var blocks = new SortedDictionary<int, BlockAccumulator>();
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        var usageFields = TokenUsageFields.None;
        var stop = StopReason.EndTurn;
        var model = request.Model.Model.ToString();
        var sawMessageStop = false;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
        var data = new StringBuilder();
        var endOfStream = false;
        while (!endOfStream)
        {
            var line = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // Fin del stream: se despacha el evento pendiente aunque falte la línea en blanco final.
                if (data.Length == 0) break;
                endOfStream = true;
                line = "";
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
                continue;
            }
            if (line.Length != 0 || data.Length == 0) continue;

            var payload = data.ToString();
            data.Clear();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch (JsonException ex) { throw new ModelProviderException("ProviderError", "SSE JSON inválido: " + ex.Message, null, ex); }
            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out var message))
                        {
                            if (message.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } resolved) model = resolved;
                            if (message.TryGetProperty("usage", out var u))
                            {
                                input = Long(u, "input_tokens");
                                if (u.TryGetProperty("input_tokens", out _)) usageFields |= TokenUsageFields.Input;
                                if (u.TryGetProperty("output_tokens", out _)) usageFields |= TokenUsageFields.Output;
                                if (u.TryGetProperty("cache_read_input_tokens", out _)) usageFields |= TokenUsageFields.CacheRead;
                                if (u.TryGetProperty("cache_creation_input_tokens", out _)) usageFields |= TokenUsageFields.CacheWrite;
                                cacheRead = Long(u, "cache_read_input_tokens");
                                cacheWrite = Long(u, "cache_creation_input_tokens");
                                output = Long(u, "output_tokens");
                                yield return new UsageUpdated(new TokenUsage(input + cacheRead + cacheWrite, output, cacheRead, cacheWrite, 0));
                            }
                        }
                        break;
                    case "content_block_start":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        var block = root.GetProperty("content_block");
                        var kind = block.GetProperty("type").GetString() ?? "";
                        var acc = new BlockAccumulator(kind);
                        if (kind == "tool_use")
                        {
                            acc.Id = block.TryGetProperty("id", out var id) ? id.GetString() : null;
                            acc.Name = block.TryGetProperty("name", out var name) ? name.GetString() : null;
                        }
                        else if (kind == "redacted_thinking")
                        {
                            acc.Raw = block.Clone();
                        }
                        blocks[index] = acc;
                        yield return new BlockStarted(index, kind);
                        break;
                    }
                    case "content_block_delta":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        if (!blocks.TryGetValue(index, out var acc)) break;
                        var delta = root.GetProperty("delta");
                        switch (delta.GetProperty("type").GetString())
                        {
                            case "text_delta":
                                var text = delta.GetProperty("text").GetString() ?? "";
                                acc.Text.Append(text);
                                yield return new TextDelta(index, text);
                                break;
                            case "input_json_delta":
                                var partial = delta.GetProperty("partial_json").GetString() ?? "";
                                acc.Text.Append(partial);
                                if (partial.Length > 0) yield return new ToolArgumentsDelta(index, partial);
                                break;
                            case "thinking_delta":
                                var thinking = delta.GetProperty("thinking").GetString() ?? "";
                                acc.Text.Append(thinking);
                                yield return new ReasoningDelta(index, thinking);
                                break;
                            case "signature_delta":
                                acc.Signature.Append(delta.GetProperty("signature").GetString());
                                break;
                        }
                        break;
                    }
                    case "content_block_stop":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        if (blocks.TryGetValue(index, out var acc) && acc.ToContentBlock() is { } completedBlock)
                            yield return new BlockCompleted(index, completedBlock);
                        break;
                    }
                    case "message_delta":
                        if (root.TryGetProperty("delta", out var md) && md.TryGetProperty("stop_reason", out var sr) &&
                            sr.ValueKind == JsonValueKind.String)
                            stop = MapStopReason(sr.GetString());
                        if (root.TryGetProperty("usage", out var mu))
                        {
                            output = Long(mu, "output_tokens", output);
                            input = Long(mu, "input_tokens", input);
                            cacheRead = Long(mu, "cache_read_input_tokens", cacheRead);
                            cacheWrite = Long(mu, "cache_creation_input_tokens", cacheWrite);
                            yield return new UsageUpdated(new TokenUsage(input + cacheRead + cacheWrite, output, cacheRead, cacheWrite, 0));
                        }
                        break;
                    case "message_stop":
                        sawMessageStop = true;
                        break;
                    case "error":
                        var error = root.TryGetProperty("error", out var e) ? e : default;
                        var errorType = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("type", out var et) ? et.GetString() ?? "error" : "error";
                        var errorMessage = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var em) ? em.GetString() ?? "" : "";
                        throw new ModelProviderException(errorType == "overloaded_error" ? "ProviderUnavailable" : "ProviderError",
                            errorType + ": " + errorMessage);
                }
            }
            if (sawMessageStop) break;
        }
        if (!sawMessageStop) throw new ModelProviderException("ProviderUnavailable", "El stream terminó sin message_stop.");

        var content = new List<ContentBlock>();
        var opaqueThinking = new List<JsonElement>();
        foreach (var acc in blocks.Values)
        {
            if (acc.ToContentBlock() is { } block) content.Add(block);
            if (acc.ToOpaqueThinking() is { } opaque) opaqueThinking.Add(opaque);
        }
        ProviderState? state = null;
        if (opaqueThinking.Count > 0)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartArray("thinking");
                foreach (var block in opaqueThinking) block.WriteTo(w);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            state = new ProviderState(OpaqueKindPrefix + request.Model.Model.ToString(), Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
        if (stop == StopReason.EndTurn && content.OfType<ToolCallBlock>().Any()) stop = StopReason.ToolUse;
        var requestId = response.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() ?? "" : "";
        yield return new ResponseCompleted(new ModelResponse(content, stop,
            new TokenUsage(input + cacheRead + cacheWrite, output, cacheRead, cacheWrite, 0), state, new ProviderMetadata(requestId, model, null), usageFields));
    }

    private static long Long(JsonElement obj, string name, long fallback = 0) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : fallback;

    private static async System.Threading.Tasks.Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try { return await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        { throw new ModelProviderException("ProviderUnavailable", ex.Message, null, ex); }
    }

    internal static StopReason MapStopReason(string? reason) => reason switch
    {
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxOutputTokens,
        "stop_sequence" => StopReason.StopSequence,
        "refusal" => StopReason.Refusal,
        "model_context_window_exceeded" => StopReason.ContextOverflow,
        _ => StopReason.EndTurn,
    };

    private sealed class BlockAccumulator(string kind)
    {
        public string Kind { get; } = kind;
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Text { get; } = new();
        public StringBuilder Signature { get; } = new();
        public JsonElement? Raw { get; set; }

        public ContentBlock? ToContentBlock() => Kind switch
        {
            "text" => new TextBlock(Text.ToString()),
            "tool_use" => new ToolCallBlock(ToolCallId.New(), Id, Name ?? "", Text.Length == 0 ? "{}" : Text.ToString()),
            "thinking" => new ReasoningBlock(Text.ToString(), ReasoningVisibility.Full, null),
            "redacted_thinking" => new ReasoningBlock(null, ReasoningVisibility.Omitted, null),
            _ => null,
        };

        /// <summary>El bloque tal como la API exige reenviarlo (texto y firma sin modificar).</summary>
        public JsonElement? ToOpaqueThinking()
        {
            if (Kind == "redacted_thinking") return Raw;
            if (Kind != "thinking") return null;
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("type", "thinking");
                w.WriteString("thinking", Text.ToString());
                w.WriteString("signature", Signature.ToString());
                w.WriteEndObject();
            }
            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }
    }
}
