namespace OmniCore.Models;

using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Opciones de resiliencia para un endpoint Chat Completions.</summary>
public sealed class OpenAiProviderOptions
{
    public int CircuitFailureThreshold { get; init; } = 5;
    public int MaxRetries { get; init; } = 2;
    public TimeSpan CircuitCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public Func<TimeSpan, CancellationToken, ValueTask> DelayAsync { get; init; } = TaskDelay;
    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;
    public Func<double> Jitter { get; init; } = static () => Random.Shared.NextDouble();
    public ProviderResilienceCatalog? CircuitCatalog { get; init; }

    private static ValueTask TaskDelay(TimeSpan delay, CancellationToken cancellationToken) =>
        new(System.Threading.Tasks.Task.Delay(delay, cancellationToken));
}

/// <summary>
/// Adaptador nativo de la familia OpenAI Chat Completions. El transporte consume SSE de forma
/// incremental y usa DTOs generados por System.Text.Json; los campos no modelados se conservan
/// en ProviderState para replay exclusivo en el mismo modelo/familia.
/// </summary>
public sealed class OpenAiChatCompatibleProvider : IModelProvider, IModelRequestAttemptBound
{
    private readonly ProviderDescriptor _descriptor;
    private readonly ISecretProvider _secrets;
    private readonly Func<HttpClient> _httpFactory;
    private readonly OpenAiProviderOptions _options;
    private readonly ProviderResilience _resilience;

    public OpenAiChatCompatibleProvider(ProviderDescriptor descriptor, ISecretProvider secrets)
        : this(descriptor, secrets, static () => new HttpClient(), null, null) { }

    public OpenAiChatCompatibleProvider(ProviderDescriptor descriptor, ISecretProvider secrets,
        Func<HttpClient> httpFactory)
        : this(descriptor, secrets, httpFactory, null, null) { }

    public OpenAiChatCompatibleProvider(ProviderDescriptor descriptor, ISecretProvider secrets,
        Func<HttpClient> httpFactory, OpenAiProviderOptions? options)
        : this(descriptor, secrets, httpFactory, options, null) { }

    public OpenAiChatCompatibleProvider(ProviderDescriptor descriptor, ISecretProvider secrets,
        Func<HttpClient> httpFactory, OpenAiProviderOptions? options, string? providerKey)
    {
        _descriptor = descriptor;
        _secrets = secrets;
        _httpFactory = httpFactory;
        _options = options ?? new OpenAiProviderOptions();
        ProviderKey = providerKey ?? descriptor.Id;
        _resilience = _options.CircuitCatalog?.Acquire(ProviderKey, _options)
            ?? new ProviderResilience(_options, ProviderKey);
    }

    public string ProviderKey { get; }
    public long? MaximumGenerationRequestAttempts => _resilience.MaximumGenerationRequestAttempts;
    public ProviderCapabilities Capabilities => ProviderCapabilities.Local();

    /// <summary>Agrega el stream para los consumidores síncronos heredados.</summary>
    public ModelResponse Complete(ModelRequest request, CancellationToken cancellationToken)
    {
        var enumerator = StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            ModelResponse? response = null;
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                if (enumerator.Current is ResponseCompleted completed) response = completed.Response;
            return response ?? throw new ModelProviderException("ProviderUnavailable", "El stream terminó sin una respuesta completa.");
        }
        finally { enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var halfOpenProbe = _resilience.EnterCircuit();
        HttpResponseMessage response;
        HttpClient http;
        try
        {
            (response, http) = await SendWithRetryAsync(BuildBody(request), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _resilience.AbandonProbe(halfOpenProbe);
            throw;
        }
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

    private System.Threading.Tasks.Task<(HttpResponseMessage Response, HttpClient Client)> SendWithRetryAsync(
        string requestJson, CancellationToken cancellationToken) =>
        _resilience.SendWithRetryAsync(_httpFactory, () => CreateHttpRequest(requestJson, cancellationToken),
            ErrorFromBody, IsRetryableStatus, cancellationToken);

    public static ModelResponse ParseChatCompletion(string json)
    {
        ChatCompletionResponse response;
        try
        {
            response = JsonSerializer.Deserialize(json, OpenAiJsonContext.Default.ChatCompletionResponse)
                ?? throw new ModelProviderException("ProviderError", "Respuesta JSON vacía o inválida.");
        }
        catch (JsonException ex) { throw new ModelProviderException("ProviderError", "Respuesta JSON inválida: " + ex.Message, null, ex); }
        return ToModelResponse(response, "", "");
    }

    private async IAsyncEnumerable<ModelStreamEvent> ReadSseAsync(HttpResponseMessage response,
        ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var tools = new SortedDictionary<int, ToolAccumulator>();
        var opaqueFields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var usage = new TokenUsage(0, 0, 0, 0, 0);
        var usageReported = false;
        var stop = StopReason.EndTurn;
        var sawChunk = false;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
        string? line;
        var data = new StringBuilder();
        while ((line = await ReadSseLineAsync(reader, cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                if (data.Length == 0) continue;
                var payload = data.ToString().TrimEnd('\n'); data.Clear();
                if (payload == "[DONE]") break;
                var chunk = DeserializeChunk(payload);
                if (chunk is null) continue;
                sawChunk = true;
                if (chunk.Usage is not null)
                {
                    usageReported = true;
                    usage = new TokenUsage(chunk.Usage.PromptTokens, chunk.Usage.CompletionTokens, 0, 0, 0);
                    yield return new UsageUpdated(usage);
                }
                foreach (var choice in chunk.Choices ?? [])
                {
                    if (choice.Delta is not null)
                    {
                        if (!string.IsNullOrEmpty(choice.Delta.Content))
                        {
                            text.Append(choice.Delta.Content);
                            yield return new TextDelta(0, choice.Delta.Content);
                        }
                        if (!string.IsNullOrEmpty(choice.Delta.ReasoningContent))
                        {
                            reasoning.Append(choice.Delta.ReasoningContent);
                            yield return new ReasoningDelta(1, choice.Delta.ReasoningContent);
                        }
                        foreach (var delta in choice.Delta.ToolCalls ?? [])
                        {
                            if (!tools.TryGetValue(delta.Index, out var tool))
                            {
                                tool = new ToolAccumulator(delta.Index);
                                tools.Add(delta.Index, tool);
                            }
                            tool.Id ??= delta.Id;
                            tool.Name.Append(delta.Function?.Name);
                            var args = delta.Function?.Arguments;
                            tool.Arguments.Append(args);
                            if (!string.IsNullOrEmpty(args)) yield return new ToolArgumentsDelta(100 + delta.Index, args);
                        }
                        foreach (var (key, value) in choice.Delta.Extra ?? []) opaqueFields[key] = value.Clone();
                    }
                    if (choice.FinishReason is not null) stop = MapFinishReason(choice.FinishReason);
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
        }
        if (!sawChunk) throw new ModelProviderException("ProviderUnavailable", "El servidor cerró el stream antes de enviar eventos.");
        var content = new List<ContentBlock>();
        if (reasoning.Length > 0)
        {
            var block = new ReasoningBlock(reasoning.ToString(), ReasoningVisibility.Full, null);
            content.Add(block);
            opaqueFields["reasoning_content"] = JsonSerializer.SerializeToElement(reasoning.ToString(), OpenAiJsonContext.Default.String);
            yield return new BlockCompleted(1, block);
        }
        if (text.Length > 0)
        {
            var block = new TextBlock(text.ToString());
            content.Add(block);
            yield return new BlockCompleted(0, block);
        }
        foreach (var tool in tools.Values)
        {
            var block = new ToolCallBlock(ToolCallId.New(), tool.Id, tool.Name.ToString(), tool.Arguments.Length == 0 ? "{}" : tool.Arguments.ToString());
            content.Add(block);
            yield return new BlockCompleted(100 + tool.Index, block);
        }
        if (tools.Count > 0 && stop == StopReason.EndTurn) stop = StopReason.ToolUse;
        var state = opaqueFields.Count == 0 ? null : new ProviderState("openai.chat.ProviderOpaque/" + request.Model.Model,
            JsonSerializer.Serialize(opaqueFields, OpenAiJsonContext.Default.DictionaryStringJsonElement));
        var finalResponse = new ModelResponse(content, stop, usage, state,
            new ProviderMetadata(response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() ?? "" : "",
                request.Model.Model.ToString(), null), usageReported ? TokenUsageFields.Input | TokenUsageFields.Output : TokenUsageFields.None);
        yield return new ResponseCompleted(finalResponse);
    }

    private static async System.Threading.Tasks.Task<string?> ReadSseLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try { return await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        { throw new ModelProviderException("ProviderUnavailable", ex.Message, null, ex); }
    }

    private static ChatChunk? DeserializeChunk(string payload)
    {
        try { return JsonSerializer.Deserialize(payload, OpenAiJsonContext.Default.ChatChunk); }
        catch (JsonException ex) { throw new ModelProviderException("ProviderError", "SSE JSON inválido: " + ex.Message, null, ex); }
    }

    private HttpRequestMessage CreateHttpRequest(string requestJson, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _descriptor.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        if (_descriptor.Auth.Kind == AuthKind.ApiKey && _descriptor.Auth.SecretRef is not null)
        {
            var secret = _secrets.GetSecret(_descriptor.Auth.SecretRef, cancellationToken);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret.Value());
        }
        return request;
    }

    private string BuildBody(ModelRequest request)
    {
        var reasoning = request.Reasoning ?? request.Model.Reasoning;
        request.Model.Route?.ReasoningCapability.ValidateRequest(reasoning);
        // Response reasoning_content does not establish a request-side dialect for
        // arbitrary compatible endpoints. Reject rather than invent an extension.
        if (reasoning is not null)
            throw new NotSupportedException("Selected reasoning has no representation in this adapter.");
        var messages = new List<ChatRequestMessage>();
        if (!string.IsNullOrEmpty(request.Instructions))
            messages.Add(new ChatRequestMessage { Role = "system", Content = request.Instructions });
        var serverIds = CollectServerCallIds(request.Messages);
        foreach (var message in request.Messages)
            messages.AddRange(MapMessage(message, serverIds));

        // ProviderState is the existing durable opaque-continuation carrier: this adapter stores
        // ProviderOpaque message fields here because artifact payload creation belongs to the runtime.
        // Replay is restricted to the exact original model, and credentials are never included.
        if (request.Continuation is { Kind: var kind } continuation &&
            kind == "openai.chat.ProviderOpaque/" + request.Model.Model.ToString())
        {
            try
            {
                var extra = JsonSerializer.Deserialize(continuation.PayloadJson, OpenAiJsonContext.Default.DictionaryStringJsonElement);
                if (extra is not null)
                {
                    var lastAssistant = messages.FindLast(m => m.Role == "assistant");
                    if (lastAssistant is null)
                    {
                        lastAssistant = new ChatRequestMessage { Role = "assistant" };
                        messages.Add(lastAssistant);
                    }
                    foreach (var field in extra) lastAssistant.Extra[field.Key] = field.Value;
                }
            }
            catch (JsonException) { /* invalid opaque state is ignored rather than corrupting the request */ }
        }

        var tools = request.Tools.Select(tool => new ChatRequestTool
        {
            Function = new ChatRequestFunction
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = ParseJsonElementOrEmpty(tool.InputSchemaJson),
            },
        }).ToList();
        var dto = new ChatRequestDto
        {
            Model = request.Model.Model.ToString(),
            Messages = messages,
            Tools = tools.Count == 0 ? null : tools,
            Stream = true,
            MaxTokens = request.Model.MaxOutputTokens,
            ToolChoice = request.ToolChoice.Mode switch
            {
                "none" => JsonSerializer.SerializeToElement("none", OpenAiJsonContext.Default.String),
                "exact" => JsonSerializer.SerializeToElement(
                    new ChatToolChoice { Type = "function", Function = new ChatToolChoiceFunction { Name = request.ToolChoice.ToolName ?? "" } },
                    OpenAiJsonContext.Default.ChatToolChoice),
                _ => JsonSerializer.SerializeToElement("auto", OpenAiJsonContext.Default.String),
            },
        };
        return JsonSerializer.Serialize(dto, OpenAiJsonContext.Default.ChatRequestDto);
    }

    private static IEnumerable<ChatRequestMessage> MapMessage(ModelMessage message, Dictionary<string, string> ids)
    {
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    yield return new ChatRequestMessage { Role = Role(message.Role), Content = text.Text };
                    break;
                case ToolCallBlock call:
                    yield return new ChatRequestMessage
                    {
                        Role = "assistant", Content = null,
                        ToolCalls = [new ChatRequestToolCall
                        {
                            Id = ids.GetValueOrDefault(call.Id.ToString(), call.ProviderCallId ?? call.Id.ToString()),
                            Type = "function",
                            Function = new ChatRequestFunctionCall { Name = call.ToolName, Arguments = call.ArgumentsJson },
                        }],
                    };
                    break;
                case ToolResultBlock result:
                    yield return new ChatRequestMessage
                    {
                        Role = "tool", ToolCallId = ids.GetValueOrDefault(result.Id.ToString(), result.Id.ToString()),
                        Content = result.Content.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
                    };
                    break;
                case ProviderOpaqueBlock opaque when opaque.Opaque.Family == ProviderFamily.OpenAiChatCompatible &&
                    opaque.Opaque.Replay != ReplayPolicy.Never:
                    // Payload loading belongs to ArtifactStore; without it the opaque block is not replayable here.
                    break;
            }
        }
    }

    private static Dictionary<string, string> CollectServerCallIds(IReadOnlyList<ModelMessage> messages)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in messages.SelectMany(m => m.Content).OfType<ToolCallBlock>())
            ids[call.Id.ToString()] = string.IsNullOrEmpty(call.ProviderCallId) ? call.Id.ToString() : call.ProviderCallId!;
        return ids;
    }

    private static ModelResponse ToModelResponse(ChatCompletionResponse response, string model, string requestId)
    {
        var content = new List<ContentBlock>();
        var choice = response.Choices?.FirstOrDefault();
        var message = choice?.Message;
        if (message is not null)
        {
            if (!string.IsNullOrEmpty(message.ReasoningContent)) content.Add(new ReasoningBlock(message.ReasoningContent, ReasoningVisibility.Full, null));
            if (!string.IsNullOrEmpty(message.Content)) content.Add(new TextBlock(message.Content));
            foreach (var tool in message.ToolCalls ?? [])
                content.Add(new ToolCallBlock(ToolCallId.New(), tool.Id, tool.Function?.Name ?? "", tool.Function?.Arguments ?? "{}"));
        }
        var usage = response.Usage is null ? new TokenUsage(0, 0, 0, 0, 0) :
            new TokenUsage(response.Usage.PromptTokens, response.Usage.CompletionTokens, 0, 0, 0);
        var opaque = message?.Extra is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(message.Extra, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(message?.ReasoningContent))
            opaque["reasoning_content"] = JsonSerializer.SerializeToElement(message.ReasoningContent, OpenAiJsonContext.Default.String);
        var state = opaque.Count == 0 ? null :
            new ProviderState("openai.chat.ProviderOpaque/" + (string.IsNullOrEmpty(model) ? response.Model ?? "" : model),
                JsonSerializer.Serialize(opaque, OpenAiJsonContext.Default.DictionaryStringJsonElement));
        return new ModelResponse(content, MapFinishReason(choice?.FinishReason), usage, state,
            new ProviderMetadata(requestId, response.Model ?? model, null), response.Usage is null ? TokenUsageFields.None : TokenUsageFields.Input | TokenUsageFields.Output);
    }

    private static StopReason MapFinishReason(string? finish) => finish switch
    {
        "tool_calls" or "function_call" => StopReason.ToolUse,
        "length" => StopReason.MaxOutputTokens,
        "content_filter" => StopReason.ContentFilter,
        "stop" or null => StopReason.EndTurn,
        _ => StopReason.EndTurn,
    };

    private static string Role(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => "user",
    };

    private static JsonElement ParseJsonElementOrEmpty(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("{}").RootElement.Clone();
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static bool IsRetryableStatus(int status) => status is 408 or 429 || status >= 500;

    internal static ModelProviderException ErrorFromBody(int status, string body) =>
        ProviderResilience.ErrorFromBody(status, body);

    private sealed class ToolAccumulator(int index)
    {
        public int Index { get; } = index;
        public string? Id { get; set; }
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }
}

/// <summary>Error tipado del provider.</summary>
public sealed class ModelProviderException : InvalidOperationException
{
    private readonly string _detail;
    public string Kind { get; }
    public int? StatusCode { get; }

    public ModelProviderException(string kind, string message, int? statusCode = null, Exception? innerException = null)
        : base("provider " + kind + ": " + message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        _detail = message;
    }

    public override string Message => "provider " + Kind + ": " + _detail;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatRequestDto))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatChunk))]
[JsonSerializable(typeof(TokenizeRequestDto))]
[JsonSerializable(typeof(TokenizeResponseDto))]
[JsonSerializable(typeof(ChatToolChoice))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(string))]
internal partial class OpenAiJsonContext : JsonSerializerContext;

internal sealed class ChatRequestDto
{
    public string Model { get; set; } = "";
    public List<ChatRequestMessage> Messages { get; set; } = [];
    public List<ChatRequestTool>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public JsonElement? ToolChoice { get; set; }
    public bool Stream { get; set; }
    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxTokens { get; set; }
}
internal sealed class ChatRequestMessage
{
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<ChatRequestToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; } = new(StringComparer.Ordinal);
}
internal sealed class ChatRequestTool { public string Type { get; set; } = "function"; public ChatRequestFunction Function { get; set; } = new(); }
internal sealed class ChatRequestFunction { public string Name { get; set; } = ""; public string Description { get; set; } = ""; public JsonElement Parameters { get; set; } }
internal sealed class ChatRequestToolCall { public string Id { get; set; } = ""; public string Type { get; set; } = "function"; public ChatRequestFunctionCall Function { get; set; } = new(); }
internal sealed class ChatRequestFunctionCall { public string Name { get; set; } = ""; public string Arguments { get; set; } = "{}"; }
internal sealed class ChatToolChoice { public string Type { get; set; } = "function"; public ChatToolChoiceFunction Function { get; set; } = new(); }
internal sealed class ChatToolChoiceFunction { public string Name { get; set; } = ""; }
internal sealed class ChatCompletionResponse
{
    public string? Model { get; set; }
    public List<ChatChoice>? Choices { get; set; }
    public ChatUsage? Usage { get; set; }
}
internal sealed class ChatChoice { public ChatMessageDto? Message { get; set; } [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; } }
internal sealed class ChatMessageDto
{
    public string? Content { get; set; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")] public List<ChatToolCallDto>? ToolCalls { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
internal sealed class ChatToolCallDto { public string? Id { get; set; } public ChatFunctionCallDto? Function { get; set; } }
internal sealed class ChatFunctionCallDto { public string? Name { get; set; } public string? Arguments { get; set; } }
internal sealed class ChatUsage { [JsonPropertyName("prompt_tokens")] public long PromptTokens { get; set; } [JsonPropertyName("completion_tokens")] public long CompletionTokens { get; set; } }
internal sealed class ChatChunk
{
    public List<ChatChunkChoice>? Choices { get; set; }
    public ChatUsage? Usage { get; set; }
}
internal sealed class ChatChunkChoice { public ChatDelta? Delta { get; set; } [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; } }
internal sealed class ChatDelta
{
    public string? Content { get; set; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")] public List<ChatToolDelta>? ToolCalls { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
internal sealed class ChatToolDelta { public int Index { get; set; } public string? Id { get; set; } public ChatFunctionDelta? Function { get; set; } }
internal sealed class ChatFunctionDelta { public string? Name { get; set; } public string? Arguments { get; set; } }
