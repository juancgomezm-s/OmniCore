namespace OmniCore.Host;

using System.Net;
using System.Text;
using System.Text.Json;

internal sealed class McpRpcException(int code, string message, JsonElement? data) : IOException(message)
{
    public int Code { get; } = code;
    public JsonElement? DataObject { get; } = data;
    public bool IsModern => Code is -32022 or -32020 or -32021;
    internal static McpRpcException From(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number))
            throw new InvalidDataException("Invalid MCP JSON-RPC error.");
        return new(number, "MCP JSON-RPC error " + number + ".",
            error.TryGetProperty("data", out var data) ? data.Clone() : null);
    }
}

internal sealed class McpUnsupportedResultException(string kind) : IOException("MCP result requires unsupported continuation: " + kind + ".");

internal static class McpEndpoint
{
    internal static Uri Validate(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.Query.Length != 0 || uri.Scheme is not ("https" or "http")
            || uri.Scheme == "http" && (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip)))
            throw new InvalidDataException("MCP requires HTTPS, or HTTP to an explicit loopback IP, without credentials, query or fragment.");
        return uri;
    }
}

/// <summary>Negotiates one server era. It never retries a tools/call after a send.</summary>
internal sealed class McpProtocolConnection(Func<string, string, string, CancellationToken, Task<string>> raw)
{
    internal static readonly string[] SupportedVersions = [McpWire.Version, "2025-11-25", "2025-06-18", "2025-03-26"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _version;
    private string? _discovery;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, McpHeaderBinding[]> _headers = new();

    internal void ConfigureTool(string name, JsonElement schema) => _headers[name] = McpHeaderBinding.Parse(schema);
    internal IEnumerable<KeyValuePair<string,string>> Headers(string parameters)
    {
        using var document = JsonDocument.Parse(parameters);
        var root = document.RootElement;
        if (!root.TryGetProperty("name", out var name) || !_headers.TryGetValue(name.GetString() ?? "", out var bindings)
            || !root.TryGetProperty("arguments", out var args)) return [];
        return bindings.Select(binding => binding.Value(args)).Where(item => item.HasValue).Select(item => item!.Value).ToArray();
    }

    internal async Task<string> SendAsync(string method, string args, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_version is null) await Negotiate(token).ConfigureAwait(false);
            if (method == "server/discover") return _discovery!;
            return await raw(method, args, _version!, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task Negotiate(CancellationToken token)
    {
        try
        {
            var response = await raw("server/discover", "{}", McpWire.Version, token).ConfigureAwait(false);
            var result = McpWire.Result(response, 1);
            if (result.TryGetProperty("supportedVersions", out var versions)
                && (versions.ValueKind != JsonValueKind.Array || !versions.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == McpWire.Version)))
                throw new InvalidDataException("Modern MCP server has no mutually supported modern revision.");
            _discovery = response; _version = McpWire.Version; return;
        }
        catch (McpRpcException error) when (error.IsModern)
        {
            // A recognized modern error is never permission to enter a legacy handshake.
            throw;
        }
        catch (Exception error) when (error is McpRpcException or TimeoutException
            || error is HttpRequestException http && http.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            token.ThrowIfCancellationRequested();
        }
        var initialized = await raw("initialize", "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"OmniCore\",\"version\":\"1.0\"}}",
            "2025-11-25", token).ConfigureAwait(false);
        var answer = McpWire.Result(initialized, 1);
        if (!answer.TryGetProperty("protocolVersion", out var selected) || selected.ValueKind != JsonValueKind.String
            || !SupportedVersions.Skip(1).Contains(selected.GetString(), StringComparer.Ordinal))
            throw new InvalidDataException("MCP initialize selected an unsupported legacy revision.");
        var version = selected.GetString()!;
        await raw("notifications/initialized", "{}", version, token).ConfigureAwait(false);
        using var discovery = JsonDocument.Parse("{\"supportedVersions\":[" + McpWire.Quote(version)
            + "],\"capabilities\":" + (answer.TryGetProperty("capabilities", out var capabilities) ? capabilities.GetRawText() : "{}") + "}");
        _discovery = McpWire.Response(discovery.RootElement); _version = version;
    }
}

internal sealed record McpHeaderBinding(string Name, string[] Path, string Type)
{
    internal static McpHeaderBinding[] Parse(JsonElement schema)
    {
        var result = new List<McpHeaderBinding>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(schema, [], true, 0);
        return result.ToArray();
        void Walk(JsonElement node, string[] path, bool reachable, int depth)
        {
            if (depth > 24) throw new InvalidDataException("MCP header schema exceeds its depth limit.");
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("x-mcp-header", out var header))
                {
                    var name = header.ValueKind == JsonValueKind.String ? header.GetString() : null;
                    var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (!reachable || path.Length == 0 || name is null || name.Length is 0 or > 128
                        || !name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))
                        || !names.Add(name) || type is not ("string" or "integer" or "boolean"))
                        throw new InvalidDataException("Invalid x-mcp-header annotation.");
                    result.Add(new("Mcp-Param-" + name, path, type));
                }
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
                        foreach (var child in property.Value.EnumerateObject()) Walk(child.Value, [.. path, child.Name], reachable, depth + 1);
                    else if (property.Name is not ("x-mcp-header" or "default" or "const" or "enum" or "examples")) Walk(property.Value, path, false, depth + 1);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Walk(item, path, false, depth + 1);
        }
    }
    internal KeyValuePair<string,string>? Value(JsonElement arguments)
    {
        var value = arguments;
        foreach (var key in Path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        string text;
        if (Type == "string" && value.ValueKind == JsonValueKind.String) text = value.GetString()!;
        else if (Type == "boolean" && value.ValueKind is JsonValueKind.True or JsonValueKind.False) text = value.GetRawText();
        else if (Type == "integer" && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            && number is >= -9007199254740991 and <= 9007199254740991) text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else throw new InvalidDataException("MCP header argument does not match its primitive schema.");
        if (Encoding.UTF8.GetByteCount(text) > 4096) throw new InvalidDataException("MCP parameter header exceeds its limit.");
        return new(Name, text);
    }
}
