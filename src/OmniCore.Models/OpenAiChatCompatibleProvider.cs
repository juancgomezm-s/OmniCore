namespace OmniCore.Models;

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Provider de la familia OpenAI Chat Completions (ADR-0005/0011): ik_llama/llama.cpp,
/// OpenRouter y otros endpoints /v1/chat/completions realmente compatibles. Cliente HTTP
/// delgado; en M2 hace peticiones síncronas y normaliza la respuesta. El streaming SSE es
/// una mejora posterior sobre el mismo mapeo.
/// </summary>
public sealed class OpenAiChatCompatibleProvider : IModelProvider
{
    private readonly ProviderDescriptor _descriptor;

    private readonly ISecretProvider _secrets;

    private readonly Func<HttpClient> _httpFactory;

    public OpenAiChatCompatibleProvider(ProviderDescriptor descriptor, ISecretProvider secrets)
    {
        _descriptor = descriptor;
        _secrets = secrets;
        _httpFactory = () => new HttpClient();
    }

    /// <summary>
    /// Provider con un factory de HttpClient configurable (tests o hosts locales con TLS
    /// self-signed, ADR-0038). M2 usa esto para conectar ik_llama en 127.0.0.1/IP local.
    /// </summary>
    public OpenAiChatCompatibleProvider(ProviderDescriptor descriptor, ISecretProvider secrets,
        Func<HttpClient> httpFactory)
    {
        _descriptor = descriptor;
        _secrets = secrets;
        _httpFactory = httpFactory;
    }

    public ProviderCapabilities Capabilities => ProviderCapabilities.Local();

    /// <summary>CompleteAsync normalizado (no-streaming en M2).</summary>
    public ModelResponse Complete(ModelRequest request, CancellationToken cancellationToken)
    {
        var http = _httpFactory();
        var url = _descriptor.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = BuildBody(request);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // Content-Type explícito: StringContent sin segundo arg produce text/plain (P1-9).
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        if (_descriptor.Auth.Kind == AuthKind.ApiKey && _descriptor.Auth.SecretRef is not null)
        {
            var secret = _secrets.GetSecret(_descriptor.Auth.SecretRef!, cancellationToken);
            httpReq.Headers.Add("Authorization", "Bearer " + secret.Value());
        }

        var resp = http.Send(httpReq, cancellationToken);
        var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
        {
            // El body del servidor se conserva (no se descarta): el diagnóstico tipa la causa.
            throw ErrorFromBody((int) resp.StatusCode, text);
        }

        return ParseChatCompletion(text);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = Complete(request, cancellationToken);
        yield return new ResponseStarted(0);
        foreach (var block in response.Content)
        {
            yield return new BlockCompleted(0, block);
        }

        yield return new ResponseCompleted(response);
    }

    public static ModelResponse ParseChatCompletion(string json)
    {
        var map = MiniJson.ParseObject(json);
        var choices = MiniJson.ReadArray(map.TryGetValue("choices", out var c) ? c : null);
        var content = new List<ContentBlock>();
        var stop = StopReason.EndTurn;
        if (choices.Count > 0)
        {
            var first = choices[0];
            var fmsg = MiniJson.Field(first, "message");
            var finish = MiniJson.Field(first, "finish_reason");
            if (finish == "tool_calls")
            {
                stop = StopReason.ToolUse;
            }
            else if (finish == "length")
            {
                stop = StopReason.MaxOutputTokens;
            }
            else if (finish == "content_filter")
            {
                stop = StopReason.ContentFilter;
            }

            // Modelos tipo Qwen con thinking exponen `reasoning_content` (no texto final).
            var reasoning = MiniJson.Field(fmsg, "reasoning_content");
            if (reasoning is not null && reasoning.Length > 0)
            {
                content.Add(new ReasoningBlock(reasoning, ReasoningVisibility.Full, null));
            }

            var txt = MiniJson.Field(fmsg, "content");
            if (txt is not null && txt.Length > 0)
            {
                content.Add(new TextBlock(txt));
            }

            var tcs = MiniJson.ReadArray(MiniJson.Field(fmsg, "tool_calls"));
            if (tcs.Count > 0)
            {
                foreach (var tc in tcs)
                {
                    var fn = MiniJson.Field(tc, "function");
                    content.Add(new ToolCallBlock(
                        ToolCallId.New(),
                        MiniJson.Field(tc, "id"),
                        MiniJson.Field(fn, "name") ?? "",
                        MiniJson.Field(fn, "arguments") ?? "{}"));
                }
            }
        }

        var usage = new TokenUsage(0, 0, 0, 0, 0);
        var u = map.TryGetValue("usage", out var ujson) ? ujson : null;
        if (u is not null)
        {
            usage = new TokenUsage(
                AsLong(MiniJson.Field(u, "prompt_tokens")),
                AsLong(MiniJson.Field(u, "completion_tokens")),
                0, 0, 0);
        }

        return new ModelResponse(content, stop, usage, null, new ProviderMetadata("", "", null));
    }

    private string BuildBody(ModelRequest request)
    {
        var msgs = new List<string>();
        if (request.Instructions is not null && request.Instructions!.Length > 0)
        {
            msgs.Add("{\"role\":\"system\",\"content\":" + Eq(request.Instructions!) + "}");
        }

        // Correlación id canónico → ProviderCallId del servidor para el roundtrip de resultados.
        var serverIds = CollectServerCallIds(request.Messages);
        foreach (var msg in request.Messages)
        {
            msgs.AddRange(MessageToJson(msg, serverIds));
        }

        var tools = new List<string>();
        foreach (var tool in request.Tools)
        {
            tools.Add("{\"type\":\"function\",\"function\":{\"name\":" + Eq(tool.Name)
                + ",\"description\":" + Eq(tool.Description)
                + ",\"parameters\":" + (tool.InputSchemaJson.Length == 0 ? "{}" : tool.InputSchemaJson) + "}}");
        }

        var parts = new List<string>();
        parts.Add("\"model\":" + Eq(request.Model.Model.ToString()));
        parts.Add("\"messages\":[" + string.Join(",", msgs.ToArray()) + "]");
        if (tools.Count > 0)
        {
            parts.Add("\"tools\":[" + string.Join(",", tools.ToArray()) + "]");
        }

        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    private static IReadOnlyList<string> MessageToJson(ModelMessage msg,
        Dictionary<string, string> serverCallIds)
    {
        var result = new List<string>();
        foreach (var block in msg.Content)
        {
            if (block is TextBlock text)
            {
                result.Add("{\"role\":" + Eq(Role(msg.Role)) + ",\"content\":" + Eq(text.Text) + "}");
            }
            else if (block is ToolCallBlock call)
            {
                // El id del call del provider (si se preservó) es el que el servidor conoce;
                // `arguments` debe ser STRING JSON (no un objeto) según el protocolo.
                var serverId = ServerCallId(call, serverCallIds);
                result.Add("{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":"
                    + Eq(serverId) + ",\"type\":\"function\",\"function\":{\"name\":"
                    + Eq(call.ToolName) + ",\"arguments\":" + Eq(call.ArgumentsJson) + "}}]}");
            }
            else if (block is ToolResultBlock toolResult)
            {
                result.Add("{\"role\":\"tool\",\"tool_call_id\":"
                    + Eq(LookupServerId(toolResult.Id, serverCallIds)) + ",\"content\":"
                    + Eq(TextOf(toolResult.Content)) + "}");
            }
        }

        return result;
    }

    /// <summary>
    /// Rastrea los tool_call ids del servidor en todo el historial: el ToolResultBlock solo
    /// conoce el id canónico OmniCore, así que la correlación con el id del provider se hace
    /// contra los bloques ToolCallBlock previos (roundtrip correcto del tool_call_id).
    /// </summary>
    private static Dictionary<string, string> CollectServerCallIds(IReadOnlyList<ModelMessage> messages)
    {
        var map = new Dictionary<string, string>();
        foreach (var msg in messages)
        {
            foreach (var block in msg.Content)
            {
                if (block is ToolCallBlock call)
                {
                    map[call.Id.ToString()] = call.ProviderCallId is not null && call.ProviderCallId!.Length > 0
                        ? call.ProviderCallId!
                        : call.Id.ToString();
                }
            }
        }

        return map;
    }

    private static string ServerCallId(ToolCallBlock call, Dictionary<string, string> serverCallIds)
    {
        return serverCallIds.TryGetValue(call.Id.ToString(), out var v) ? v! : call.Id.ToString();
    }

    private static string LookupServerId(ToolCallId canonicalId, Dictionary<string, string> serverCallIds)
    {
        var key = canonicalId.ToString();
        return serverCallIds.TryGetValue(key, out var v) ? v! : key;
    }

    private static string Role(MessageRole role)
    {
        if (role == MessageRole.System) return "system";
        if (role == MessageRole.Assistant) return "assistant";
        if (role == MessageRole.Tool) return "tool";
        return "user";
    }

    private static string Eq(string value)
    {
        var esc = value.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t")
            .Replace("\b", "\\b")
            .Replace("\f", "\\f");
        return "\"" + esc + "\"";
    }

    private static string TextOf(IReadOnlyList<ContentBlock> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is TextBlock t) return t.Text;
        }

        return "";
    }

    private static long AsLong(string? v)
    {
        if (v is null) return 0;
        return long.TryParse(v.Trim(), out var n) ? n : 0;
    }

    /// <summary>
    /// Clasifica el error conservando el body del servidor: si el body es JSON con
    /// error.message se usa ese texto; si no, el primer segmento. Nunca descarta el detalle
    /// (el diagnóstico del provider depende de él, ADR-0011 §6).
    /// </summary>
    internal static ModelProviderException ErrorFromBody(int status, string body)
    {
        var kind = "ProviderUnavailable";
        var message = body is null || body!.Length == 0 ? "(sin body)" : body!;
        try
        {
            var map = MiniJson.ParseObject(message);
            var nested = MiniJson.Field(message, "error");
            if (nested is not null)
            {
                var detail = MiniJson.Field(nested!, "message");
                if (detail is not null && detail!.Length > 0)
                {
                    kind = "ProviderError";
                    message = "#" + status + ": " + detail!;
                }
            }
        }
        catch (Exception)
        {
            // no es JSON; usamos el body plano
        }

        return new ModelProviderException(kind, message);
    }

    private static string TextHead(string s) => s.Length <= 80 ? s : s.Substring(0, 80);
}

/// <summary>Error tipado del provider (spec §71; ADR-0011 §5).</summary>
public sealed class ModelProviderException : InvalidOperationException
{
    public string Kind { get; }

    private readonly string _detail;

    public ModelProviderException(string kind, string message)
    {
        Kind = kind;
        _detail = message is null ? "" : message!;
    }

    /// <summary>El mensaje del proveedor se conserva (base(message) no es alcanzable; override).</summary>
    public override string Message => "provider " + Kind + ": " + _detail;
}

/// <summary>Mini parser JSON plano para respuestas de chat.completions (no streaming).</summary>
internal sealed class MiniJson
{
    public static Dictionary<string, string> ParseObject(string json)
    {
        var map = new Dictionary<string, string>();
        var s = json.Trim();
        var body = s.Length >= 2 && s[0] == '{' ? s.Substring(1, s.Length - 2) : s;
        var i = 0;
        while (i < body.Length)
        {
            var colon = body.IndexOf(':', i);
            if (colon < 0) break;
            var key = Unquote(body.Substring(i, colon - i).Trim());
            var after = colon + 1;
            while (after < body.Length && (body[after] == ' ')) after += 1;
            if (after >= body.Length) break;
            if (body[after] == '"' || body[after] == '{' || body[after] == '[')
            {
                var end = FindValueEnd(body, after);
                map[key] = body.Substring(after, end - after + 1);
                var comma = body.IndexOf(',', end);
                i = comma < 0 ? body.Length : comma + 1;
            }
            else
            {
                var comma = body.IndexOf(',', after);
                var end = comma < 0 ? body.Length : comma;
                map[key] = body.Substring(after, end - after);
                i = comma < 0 ? body.Length : comma + 1;
            }
        }

        return map;
    }

    private static int FindValueEnd(string s, int start)
    {
        var quote = s[start] == '"';
        var depth = 0;
        var from = quote ? start + 1 : start;
        for (var i = from; i < s.Length; i++)
        {
            if (quote)
            {
                var c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    i += 1;
                    continue;
                }

                if (c == '"') return i;
            }
            else
            {
                if (s[i] == '{' || s[i] == '[') depth += 1;
                else if (s[i] == '}' || s[i] == ']')
                {
                    depth -= 1;
                    if (depth <= 0) return i;
                }
                else if (s[i] == ',' && depth == 0)
                {
                    return i;
                }
            }
        }

        return s.Length;
    }

    public static IReadOnlyList<string> ReadArray(string? arr)
    {
        var result = new List<string>();
        if (arr is null || arr == "null") return result;
        var s = arr.Trim();
        var body = s.Length >= 2 && s[0] == '[' ? s.Substring(1, s.Length - 2) : s;
        var i = 0;
        while (i < body.Length)
        {
            var skip = body[i] == ' ' || body[i] == ',';
            if (skip)
            {
                i += 1;
                continue;
            }

            var end = FindValueEnd(body, i);
            if (end >= body.Length)
            {
                result.Add(body.Substring(i));
                break;
            }

            result.Add(body.Substring(i, end - i + (body[end] == ',' ? 1 : 1)).TrimStart(',', ' '));
            i = end + 1;
        }

        return result;
    }

    public static string? Field(string? obj, string name)
    {
        if (obj is null || obj!.Length == 0) return null;
        var idx = obj!.IndexOf("\"" + name + "\"", StringComparison.Ordinal);
        if (idx < 0) return null;
        var colon = obj.IndexOf(':', idx);
        if (colon < 0) return null;
        var after = colon + 1;
        while (after < obj.Length && obj[after] == ' ') after += 1;
        if (after >= obj.Length) return null;
        if (obj[after] == '"')
        {
            var end = FindValueEnd(obj, after);
            return end < 0 ? null : Unquote(obj.Substring(after, end - after + 1));
        }

        if (obj[after] == '{' || obj[after] == '[')
        {
            var end = FindValueEnd(obj, after);
            return obj.Substring(after, end - after + 1);
        }

        var comma = obj.IndexOf(',', after);
        var bracket = obj.IndexOf('}', after);
        var br = obj.IndexOf(']', after);
        var endIdx = comma < 0 ? (bracket < 0 ? (br < 0 ? obj.Length : br) : bracket) : Math.Min(comma,
            bracket < 0 ? (br < 0 ? obj.Length : br) : bracket);
        return obj.Substring(after, endIdx - after).Trim();
    }

    private static string Unquote(string t)
    {
        var v = t.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
        {
            var inner = v.Substring(1, v.Length - 2);
            return inner.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
        }

        return v;
    }
}