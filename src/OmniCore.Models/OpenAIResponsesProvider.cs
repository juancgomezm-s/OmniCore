namespace OmniCore.Models;

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Perfil del adaptador Responses (ADR-0011 §2, §3.4).</summary>
public enum ResponsesProfile
{
    /// <summary>Responses API con API key (<c>/responses</c>).</summary>
    Api,

    /// <summary>Backend de la suscripción ChatGPT (<c>/codex/responses</c>) con OAuth.</summary>
    Codex,
}

/// <summary>Credencial de una request del perfil <c>codex</c>: token OAuth vigente + cuenta de ChatGPT.</summary>
public sealed record SubscriptionCredential(string AccessToken, string AccountId);

/// <summary>Fuente de credenciales de suscripción (la implementa ChatGptSubscriptionAuthProvider).</summary>
public interface ISubscriptionCredentialSource
{
    /// <summary>Devuelve un token vigente (refrescándolo antes de expirar si hace falta).</summary>
    ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken);

    /// <summary>Tras un 401: fuerza un refresh. Si falla o sigue en 401, el error es AuthenticationFailed.</summary>
    ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>Opciones del adaptador Responses.</summary>
public sealed class OpenAIResponsesOptions
{
    public ResponsesProfile Profile { get; init; } = ResponsesProfile.Api;

    /// <summary>Versión que se envía en el User-Agent honesto (ADR-0011 §3.4: nunca se imita otro producto).</summary>
    public string ClientVersion { get; init; } = "0.1";

    /// <summary><c>max_output_tokens</c>; null deja que el servidor decida.</summary>
    public int? MaxOutputTokens { get; init; }

    public OpenAiProviderOptions Resilience { get; init; } = new();
}

/// <summary>
/// Adaptador nativo de la Responses API de OpenAI (ADR-0005 §1). Siempre <c>store: false</c>: la
/// continuación no depende de estado en el servidor. El razonamiento cifrado
/// (<c>reasoning.encrypted_content</c>) se conserva intacto en <see cref="ProviderState"/> y solo se
/// reenvía al MISMO modelo (ProviderOpaque, replay SameModel).
/// </summary>
public sealed class OpenAIResponsesProvider : IModelProvider, IReportsRateLimits
{
    private const string OpaqueKindPrefix = "openai.responses.ProviderOpaque/";
    private readonly ProviderDescriptor _descriptor;
    private readonly ISecretProvider _secrets;
    private readonly ISubscriptionCredentialSource? _subscription;
    private readonly Func<HttpClient> _httpFactory;
    private readonly OpenAIResponsesOptions _options;
    private readonly ProviderResilience _resilience;

    public OpenAIResponsesProvider(ProviderDescriptor descriptor, ISecretProvider secrets, Func<HttpClient> httpFactory,
        OpenAIResponsesOptions? options = null, ISubscriptionCredentialSource? subscription = null)
    {
        if (descriptor.Family != ProviderFamily.OpenAIResponses)
            throw new ArgumentException("El descriptor no es de la familia OpenAIResponses.", nameof(descriptor));
        _descriptor = descriptor;
        _secrets = secrets;
        _httpFactory = httpFactory;
        _options = options ?? new OpenAIResponsesOptions();
        if (_options.MaxOutputTokens is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "max_output_tokens debe ser mayor que cero.");
        if (_options.Profile == ResponsesProfile.Codex && subscription is null)
            throw new ArgumentException("El perfil codex necesita una fuente de credenciales de suscripción.", nameof(subscription));
        _subscription = subscription;
        _resilience = new ProviderResilience(_options.Resilience, descriptor.Id);
    }

    /// <summary>Informa tokens; costo y cuota no llegan en el stream.</summary>
    public ProviderCapabilities Capabilities { get; } = new(true, false, true);

    /// <summary>Ventanas de rate limit de la última respuesta (cuota informada, nunca estimada).</summary>
    public IReadOnlyList<RateLimitWindow> LastRateLimits { get; private set; } = [];

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _resilience.EnterCircuit();
        var body = BuildBody(request, _options);
        var credential = _subscription is null ? null : await _subscription.GetAsync(cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response;
        HttpClient http;
        try
        {
            (response, http) = await SendAsync(body, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelProviderException ex) when (ex.StatusCode == 401 && _subscription is not null)
        {
            // Un único refresh; si el 401 persiste el error se propaga como AuthenticationFailed.
            credential = await _subscription.RefreshAsync(cancellationToken).ConfigureAwait(false);
            (response, http) = await SendAsync(body, credential, cancellationToken).ConfigureAwait(false);
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
            if (!completed && !cancellationToken.IsCancellationRequested) _resilience.MarkFailure();
        }
    }

    private System.Threading.Tasks.Task<(HttpResponseMessage Response, HttpClient Client)> SendAsync(string body,
        SubscriptionCredential? credential, CancellationToken cancellationToken) =>
        _resilience.SendWithRetryAsync(_httpFactory, () => CreateHttpRequest(body, credential, cancellationToken),
            ProviderResilience.ErrorFromBody, static status => status is 408 or 429 || status >= 500, cancellationToken);

    private HttpRequestMessage CreateHttpRequest(string body, SubscriptionCredential? credential, CancellationToken cancellationToken)
    {
        var baseUrl = _descriptor.BaseUrl.TrimEnd('/');
        var path = _options.Profile == ResponsesProfile.Codex ? "/codex/responses" : "/responses";
        var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent(_options.ClientVersion));
        if (credential is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
            request.Headers.TryAddWithoutValidation("originator", "omnicore");
        }
        else if (_descriptor.Auth.Kind == AuthKind.ApiKey && _descriptor.Auth.SecretRef is not null)
        {
            var secret = _secrets.GetSecret(_descriptor.Auth.SecretRef, cancellationToken);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret.Value());
        }
        return request;
    }

    /// <summary>Identificación honesta: <c>omnicore/&lt;versión&gt; (&lt;os&gt;; &lt;arch&gt;)</c>.</summary>
    public static string UserAgent(string version) =>
        "omnicore/" + version + " (" + System.Runtime.InteropServices.RuntimeInformation.OSDescription.Split(' ')[0].ToLowerInvariant()
        + "; " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() + ")";

    internal static string BuildBody(ModelRequest request, OpenAIResponsesOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", request.Model.Model.ToString());
            w.WriteBoolean("stream", true);
            w.WriteBoolean("store", false);
            if (!string.IsNullOrEmpty(request.Instructions)) w.WriteString("instructions", request.Instructions);
            if (options.MaxOutputTokens is { } max) w.WriteNumber("max_output_tokens", max);

            var reasoning = request.Reasoning ?? request.Model.Reasoning;
            var effort = reasoning?.Kind is "minimal" or "low" or "medium" or "high" ? reasoning.Kind : null;
            if (effort is not null)
            {
                w.WriteStartObject("reasoning");
                w.WriteString("effort", effort);
                w.WriteEndObject();
            }
            // Sin store, la continuación del razonamiento requiere pedir el contenido cifrado.
            w.WriteStartArray("include");
            w.WriteStringValue("reasoning.encrypted_content");
            w.WriteEndArray();

            WriteInput(w, request);

            if (request.Tools.Count > 0 && request.ToolChoice.Mode != "none")
            {
                w.WriteStartArray("tools");
                foreach (var tool in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteString("name", WireToolName(tool.Name));
                    w.WriteString("description", tool.Description);
                    w.WritePropertyName("parameters");
                    WriteRawJsonOrEmptyObject(w, tool.InputSchemaJson);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (request.ToolChoice.Mode == "exact" && request.ToolChoice.ToolName is not null)
                {
                    w.WriteStartObject("tool_choice");
                    w.WriteString("type", "function");
                    w.WriteString("name", WireToolName(request.ToolChoice.ToolName));
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteString("tool_choice", "auto");
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteInput(Utf8JsonWriter w, ModelRequest request)
    {
        JsonElement[]? replayReasoning = null;
        if (request.Continuation is { } continuation &&
            continuation.Kind == OpaqueKindPrefix + request.Model.Model.ToString())
        {
            try
            {
                using var doc = JsonDocument.Parse(continuation.PayloadJson);
                if (doc.RootElement.TryGetProperty("reasoning", out var items) && items.ValueKind == JsonValueKind.Array)
                    replayReasoning = items.EnumerateArray().Select(i => i.Clone()).ToArray();
            }
            catch (JsonException) { /* un estado opaco inválido se ignora */ }
        }
        var lastAssistant = -1;
        for (var i = 0; i < request.Messages.Count; i++)
            if (request.Messages[i].Role == MessageRole.Assistant) lastAssistant = i;

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in request.Messages.SelectMany(m => m.Content).OfType<ToolCallBlock>())
            ids[call.Id.ToString()] = string.IsNullOrEmpty(call.ProviderCallId) ? call.Id.ToString() : call.ProviderCallId!;

        w.WriteStartArray("input");
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            if (message.Role == MessageRole.System) continue;
            // Los items de razonamiento preceden a la salida del assistant que los produjo.
            if (i == lastAssistant && replayReasoning is not null)
                foreach (var item in replayReasoning) item.WriteTo(w);
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextBlock text when text.Text.Length > 0:
                        var assistant = message.Role == MessageRole.Assistant;
                        w.WriteStartObject();
                        w.WriteString("type", "message");
                        w.WriteString("role", assistant ? "assistant" : "user");
                        w.WriteStartArray("content");
                        w.WriteStartObject();
                        w.WriteString("type", assistant ? "output_text" : "input_text");
                        w.WriteString("text", text.Text);
                        w.WriteEndObject();
                        w.WriteEndArray();
                        w.WriteEndObject();
                        break;
                    case ToolCallBlock call:
                        w.WriteStartObject();
                        w.WriteString("type", "function_call");
                        w.WriteString("call_id", ids[call.Id.ToString()]);
                        w.WriteString("name", WireToolName(call.ToolName));
                        w.WriteString("arguments", string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                        w.WriteEndObject();
                        break;
                    case ToolResultBlock result:
                        w.WriteStartObject();
                        w.WriteString("type", "function_call_output");
                        w.WriteString("call_id", ids.GetValueOrDefault(result.Id.ToString(), result.Id.ToString()));
                        w.WriteString("output", string.Concat(result.Content.OfType<TextBlock>().Select(b => b.Text)));
                        w.WriteEndObject();
                        break;
                }
            }
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
                if (doc.RootElement.ValueKind == JsonValueKind.Object) { doc.RootElement.WriteTo(w); return; }
            }
            catch (JsonException) { }
        }
        w.WriteStartObject();
        w.WriteEndObject();
    }

    private async IAsyncEnumerable<ModelStreamEvent> ReadSseAsync(HttpResponseMessage response, ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var items = new SortedDictionary<int, ItemAccumulator>();
        ModelResponse? final = null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
        var data = new StringBuilder();
        var endOfStream = false;
        while (!endOfStream && final is null)
        {
            string? line;
            try { line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            { throw new ModelProviderException("ProviderUnavailable", ex.Message, null, ex); }
            if (line is null)
            {
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
            if (payload == "[DONE]") break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch (JsonException ex) { throw new ModelProviderException("ProviderError", "SSE JSON inválido: " + ex.Message, null, ex); }
            using (doc)
            {
                var root = doc.RootElement;
                switch (Str(root, "type"))
                {
                    case "response.output_item.added":
                    {
                        var index = root.GetProperty("output_index").GetInt32();
                        var item = root.GetProperty("item");
                        var acc = new ItemAccumulator(Str(item, "type") ?? "")
                        {
                            CallId = Str(item, "call_id"),
                            Name = Str(item, "name"),
                        };
                        items[index] = acc;
                        yield return new BlockStarted(index, acc.Kind);
                        break;
                    }
                    case "response.output_text.delta":
                    {
                        var index = root.GetProperty("output_index").GetInt32();
                        var delta = Str(root, "delta") ?? "";
                        if (items.TryGetValue(index, out var acc)) acc.Text.Append(delta);
                        yield return new TextDelta(index, delta);
                        break;
                    }
                    case "response.function_call_arguments.delta":
                    {
                        var index = root.GetProperty("output_index").GetInt32();
                        var delta = Str(root, "delta") ?? "";
                        if (items.TryGetValue(index, out var acc)) acc.Text.Append(delta);
                        if (delta.Length > 0) yield return new ToolArgumentsDelta(index, delta);
                        break;
                    }
                    case "response.reasoning_summary_text.delta":
                    {
                        var index = root.GetProperty("output_index").GetInt32();
                        var delta = Str(root, "delta") ?? "";
                        if (items.TryGetValue(index, out var acc)) acc.Text.Append(delta);
                        yield return new ReasoningDelta(index, delta);
                        break;
                    }
                    case "response.output_item.done":
                    {
                        var index = root.GetProperty("output_index").GetInt32();
                        var item = root.GetProperty("item");
                        if (!items.TryGetValue(index, out var acc)) items[index] = acc = new ItemAccumulator(Str(item, "type") ?? "");
                        acc.Done = item.Clone();
                        if (acc.ToContentBlock() is { } block) yield return new BlockCompleted(index, RestoreToolName(block, request));
                        break;
                    }
                    case "response.completed":
                    case "response.incomplete":
                    {
                        var body = root.GetProperty("response");
                        var usage = ReadUsage(body);
                        yield return new UsageUpdated(usage);
                        final = BuildResponse(body, items, usage, request, response);
                        break;
                    }
                    case "response.failed":
                    {
                        var error = root.GetProperty("response").TryGetProperty("error", out var e) ? e : default;
                        var code = error.ValueKind == JsonValueKind.Object ? Str(error, "code") ?? "error" : "error";
                        var message = error.ValueKind == JsonValueKind.Object ? Str(error, "message") ?? "" : "";
                        throw new ModelProviderException(code is "rate_limit_exceeded" ? "RateLimited" : "ProviderError", code + ": " + message);
                    }
                    case "error":
                        throw new ModelProviderException("ProviderError", (Str(root, "code") ?? "error") + ": " + (Str(root, "message") ?? ""));
                }
            }
        }
        if (final is null) throw new ModelProviderException("ProviderUnavailable", "El stream terminó sin response.completed.");
        yield return new ResponseCompleted(final);
    }

    private static TokenUsage ReadUsage(JsonElement body)
    {
        if (!body.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return new TokenUsage(0, 0, 0, 0, 0);
        var input = Long(u, "input_tokens");
        var output = Long(u, "output_tokens");
        var cached = u.TryGetProperty("input_tokens_details", out var id) && id.ValueKind == JsonValueKind.Object ? Long(id, "cached_tokens") : 0;
        var written = id.ValueKind == JsonValueKind.Object ? Long(id, "cache_write_tokens") : 0;
        var reasoning = u.TryGetProperty("output_tokens_details", out var od) && od.ValueKind == JsonValueKind.Object ? Long(od, "reasoning_tokens") : 0;
        return new TokenUsage(input, output, cached, written, reasoning);
    }

    private static ModelResponse BuildResponse(JsonElement body, SortedDictionary<int, ItemAccumulator> items, TokenUsage usage,
        ModelRequest request, HttpResponseMessage http)
    {
        var content = new List<ContentBlock>();
        var reasoningItems = new List<JsonElement>();
        foreach (var acc in items.Values)
        {
            if (acc.ToContentBlock() is { } block) content.Add(RestoreToolName(block, request));
            if (acc.Kind == "reasoning" && acc.Done is { } done && done.TryGetProperty("encrypted_content", out var enc) &&
                enc.ValueKind == JsonValueKind.String)
                reasoningItems.Add(done);
        }
        var status = Str(body, "status");
        var incompleteReason = body.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Str(details, "reason") : null;
        var stop = status == "incomplete"
            ? incompleteReason switch
            {
                "max_output_tokens" => StopReason.MaxOutputTokens,
                "content_filter" => StopReason.ContentFilter,
                _ => StopReason.Error,
            }
            : content.OfType<ToolCallBlock>().Any() ? StopReason.ToolUse : StopReason.EndTurn;
        ProviderState? state = null;
        if (reasoningItems.Count > 0)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartArray("reasoning");
                foreach (var item in reasoningItems) item.WriteTo(w);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            state = new ProviderState(OpaqueKindPrefix + request.Model.Model.ToString(), Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
        var requestId = http.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() ?? "" : "";
        return new ModelResponse(content, stop, usage, state,
            new ProviderMetadata(requestId, Str(body, "model") ?? request.Model.Model.ToString(), Str(body, "service_tier")), UsageFields(body));
    }

    private static TokenUsageFields UsageFields(JsonElement body)
    {
        if (!body.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return TokenUsageFields.None;
        var fields = TokenUsageFields.None;
        if (u.TryGetProperty("input_tokens", out var input) && input.TryGetInt64(out _)) fields |= TokenUsageFields.Input;
        if (u.TryGetProperty("output_tokens", out var output) && output.TryGetInt64(out _)) fields |= TokenUsageFields.Output;
        if (u.TryGetProperty("input_tokens_details", out var i) && i.ValueKind == JsonValueKind.Object)
        {
            if (i.TryGetProperty("cached_tokens", out var read) && read.TryGetInt64(out _)) fields |= TokenUsageFields.CacheRead;
            if (i.TryGetProperty("cache_write_tokens", out var write) && write.TryGetInt64(out _)) fields |= TokenUsageFields.CacheWrite;
        }
        if (u.TryGetProperty("output_tokens_details", out var o) && o.ValueKind == JsonValueKind.Object &&
            o.TryGetProperty("reasoning_tokens", out var reasoning) && reasoning.TryGetInt64(out _)) fields |= TokenUsageFields.Reasoning;
        return fields;
    }

    // Provider aliases never become canonical tool IDs or bypass the effective catalog.
    internal static string WireToolName(string name) => name.Length is > 0 and <= 64
        && !name.StartsWith("omni_", StringComparison.Ordinal)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? name
        : "omni_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..48];

    private static ContentBlock RestoreToolName(ContentBlock block, ModelRequest request)
    {
        if (block is not ToolCallBlock call) return block;
        var definition = request.Tools.FirstOrDefault(tool => WireToolName(tool.Name) == call.ToolName);
        return definition is null ? block : call with { ToolName = definition.Name };
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private sealed class ItemAccumulator(string kind)
    {
        public string Kind { get; } = kind;
        public string? CallId { get; set; }
        public string? Name { get; set; }
        public StringBuilder Text { get; } = new();
        public JsonElement? Done { get; set; }

        public ContentBlock? ToContentBlock()
        {
            switch (Kind)
            {
                case "message":
                    var text = Text.ToString();
                    if (text.Length == 0 && Done is { } msg && msg.TryGetProperty("content", out var parts))
                        text = string.Concat(parts.EnumerateArray().Where(p => Str(p, "type") == "output_text").Select(p => Str(p, "text")));
                    return text.Length == 0 ? null : new TextBlock(text);
                case "function_call":
                    var args = Text.ToString();
                    if (Done is { } call)
                    {
                        CallId = Str(call, "call_id") ?? CallId;
                        Name = Str(call, "name") ?? Name;
                        if (args.Length == 0) args = Str(call, "arguments") ?? "";
                    }
                    return new ToolCallBlock(ToolCallId.New(), CallId, Name ?? "", args.Length == 0 ? "{}" : args);
                case "reasoning":
                    var summary = Text.ToString();
                    return new ReasoningBlock(summary.Length == 0 ? null : summary,
                        summary.Length == 0 ? ReasoningVisibility.Omitted : ReasoningVisibility.Summarized, null);
                default:
                    return null;
            }
        }
    }
}
