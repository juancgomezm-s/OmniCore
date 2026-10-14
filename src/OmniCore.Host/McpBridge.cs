namespace OmniCore.Host;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Sandbox;

/// <summary>Wire implementation pinned to the stateless MCP revision 2026-07-28.</summary>
internal static class McpWire
{
    public const string Version = "2026-07-28";
    public const string MetadataVersion = "io.modelcontextprotocol/protocolVersion";
    public const string MetadataCapabilities = "io.modelcontextprotocol/clientCapabilities";
    public const string MetadataClientInfo = "io.modelcontextprotocol/clientInfo";
    public const int MaximumPayloadBytes = 1_048_576;

    public static string Request(long id, string method, string? parametersJson = null)
    {
        using var document = JsonDocument.Parse(parametersJson ?? "{}");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("jsonrpc", "2.0"); writer.WriteNumber("id", id);
            writer.WriteString("method", method); writer.WritePropertyName("params"); writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()) property.WriteTo(writer);
            writer.WritePropertyName("_meta"); writer.WriteStartObject();
            writer.WriteString(MetadataVersion, Version);
            writer.WritePropertyName(MetadataClientInfo); writer.WriteStartObject();
            writer.WriteString("name", "OmniCore"); writer.WriteString("version", "1.0"); writer.WriteEndObject();
            writer.WritePropertyName(MetadataCapabilities); writer.WriteStartObject(); writer.WriteEndObject();
            writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static JsonElement Result(string json, long expectedId)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw new InvalidDataException("MCP response exceeds its byte limit.");
        using var response = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version)
            || version.GetString() != "2.0" || !root.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number || id.GetInt64() != expectedId)
            throw new InvalidDataException("MCP response did not match the request.");
        if (root.TryGetProperty("error", out _)) throw new InvalidDataException("MCP server returned an error.");
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("MCP response has no object result.");
        return result.Clone();
    }
}

internal sealed record McpServerDefinition(string Id, string Transport, string? Command, string[] Args,
    string? WorkingDirectory, string? Endpoint, string[] AllowedHosts, bool UserTrusted,
    string Scope, string Identity)
{
    public McpWebConnector? Web { get; init; }
    public string? AuthRef { get; init; }
    public string TrustGeneration { get; init; } = "";
    public bool HonorReadOnlyHint { get; init; }
    public string EffectiveIdentity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Identity + "\ntrust-generation:" + TrustGeneration))).ToLowerInvariant();

    public static McpServerDefinition Parse(JsonElement json, string scope, bool workspaceTrusted)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MCP server entry must be an object.");
        var id = Required("id");
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$"))
            throw new InvalidDataException("MCP server id is invalid.");
        var transport = Required("transport").ToLowerInvariant();
        if (transport is not ("stdio" or "http")) throw new InvalidDataException("MCP transport must be stdio or http.");
        var command = Optional("command");
        var endpoint = Optional("endpoint");
        var args = Strings("args");
        var hosts = Strings("allowedHosts");
        var working = Optional("workingDirectory");
        // A value in project/user JSON is not a trust decision. Only the explicit,
        // hash-bound trust receipt written by the user may trust a remote endpoint.
        var userTrusted = false;
        var honorReadOnlyHint = scope == "user" && OptionalBool("honorReadOnlyHint");
        var authRef = Optional("authRef");
        if (authRef is not null && (scope != "user"
            || !System.Text.RegularExpressions.Regex.IsMatch(authRef, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")))
            throw new InvalidDataException("MCP credentials may be referenced only from User configuration.");
        if (transport == "stdio" && (string.IsNullOrWhiteSpace(command) || endpoint is not null || !Path.IsPathRooted(command)))
            throw new InvalidDataException("stdio requires command and forbids endpoint.");
        if (transport == "http")
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0
                || uri.Scheme is not ("https" or "http") || uri.Fragment.Length != 0 || uri.Query.Length != 0)
                throw new InvalidDataException("MCP endpoint must be an absolute HTTPS URL without embedded credentials, query, or fragment.");
            if (uri.Scheme == "http")
            {
                if (!IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip))
                    throw new InvalidDataException("Remote MCP endpoints require HTTPS; cleartext HTTP is limited to loopback fixtures.");
            }
            if (!hosts.Contains(uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The MCP endpoint host must appear in allowedHosts.");
        }
        if (scope == "project" && !workspaceTrusted) scope = "project-untrusted-workspace";
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.GetRawText()))).ToLowerInvariant();
        return new McpServerDefinition(id, transport, command, args, working, endpoint, hosts, userTrusted,
            scope, identity) { Web = ParseWeb(json), AuthRef = authRef, HonorReadOnlyHint = honorReadOnlyHint };

        string Required(string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException("MCP entry is missing " + name + ".");
        string? Optional(string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool OptionalBool(string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        string[] Strings(string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new InvalidDataException(name + " must contain strings.")).Take(64).ToArray()
            : [];

        static McpWebConnector? ParseWeb(JsonElement entry)
        {
            if (!entry.TryGetProperty("web", out var web)) return null;
            if (web.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MCP web connector must be an object.");
            var searchTool = GetString("searchTool"); var fetchTool = GetString("fetchTool");
            if (searchTool is null && fetchTool is null) throw new InvalidDataException("MCP web connector needs searchTool or fetchTool.");
            var hosts = GetStrings("allowedTargets");
            var maxResults = GetInt("maxResults", 5, 1, 8);
            var maxBytes = GetInt("maxBytes", 65536, 1, 262144);
            foreach (var target in hosts)
                if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri) || targetUri.UserInfo.Length != 0
                    || targetUri.Query.Length != 0 || targetUri.Fragment.Length != 0 || targetUri.AbsolutePath != "/"
                    || targetUri.Scheme is not ("https" or "http")
                    || targetUri.Scheme == "http" && (!IPAddress.TryParse(targetUri.Host, out var ip) || !IPAddress.IsLoopback(ip)))
                    throw new InvalidDataException("MCP web targets must be exact HTTPS origins (HTTP loopback only).");
            return new McpWebConnector(searchTool, fetchTool,
                GetString("queryArgument") ?? "query", GetString("limitArgument") ?? "limit",
                GetString("uriArgument") ?? "uri", GetString("bytesArgument") ?? "maxBytes",
                hosts, maxResults, maxBytes, GetInt("maxRedirects", 0, 0, 5));

            string? GetString(string key) => web.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            string[] GetStrings(string key) => web.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new InvalidDataException(key + " must contain strings.")).Take(64).ToArray()
                : [];
            int GetInt(string key, int fallback, int minimum, int maximum)
            {
                if (!web.TryGetProperty(key, out var value)) return fallback;
                if (!value.TryGetInt32(out var parsed) || parsed < minimum || parsed > maximum)
                    throw new InvalidDataException("MCP web " + key + " is outside its supported bounds.");
                return parsed;
            }
        }
    }

    public TrustLevel Trust => Scope == "project" && (Transport != "http" || UserTrusted) ? TrustLevel.Project
        : Scope.StartsWith("project-", StringComparison.Ordinal) ? TrustLevel.Untrusted
        : Transport == "stdio" || UserTrusted ? TrustLevel.Trusted : TrustLevel.Untrusted;
}

internal sealed record McpWebConnector(string? SearchTool, string? FetchTool, string QueryArgument,
    string LimitArgument, string UriArgument, string BytesArgument, string[] AllowedTargets,
    int MaxResults, int MaxBytes, int MaxRedirects);

internal interface IMcpRequestTransport
{
    Task<string> SendAsync(McpServerDefinition server, string method, string parametersJson, CancellationToken cancellationToken);
}

/// <summary>Runs local MCP requests in Strong without ambient network or filesystem access.</summary>
internal sealed class McpStrongStdioTransport(ISandboxProcessLauncher launcher) : IMcpRequestTransport
{
    public async Task<string> SendAsync(McpServerDefinition server, string method, string parametersJson, CancellationToken token)
    {
        if (server.Transport != "stdio" || server.Command is null) throw new InvalidOperationException("MCP transport mismatch.");
        var executable = Path.GetFullPath(server.Command);
        var cwd = Path.GetFullPath(server.WorkingDirectory ?? Path.GetDirectoryName(executable)!);
        var request = McpWire.Request(1, method, parametersJson);
        var spec = new SandboxLaunchSpec(SandboxStrength.Strong, executable, server.Args, cwd,
            new Dictionary<string, string>(StringComparer.Ordinal), [], new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []),
            TimeSpan.FromSeconds(15), request + "\n");
        await using var process = await launcher.StartAsync(spec, token).ConfigureAwait(false);
        SandboxProcessOutput output;
        try { output = await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        if (output.TimedOut) throw new TimeoutException("MCP server timed out.");
        var line = string.Join("\n", output.Chunks.Where(chunk => chunk.Stream == SandboxOutputStream.StandardOutput).Select(chunk => chunk.Text))
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (output.ExitCode != 0 || line is null || Encoding.UTF8.GetByteCount(line) > McpWire.MaximumPayloadBytes)
            throw new InvalidDataException("MCP server did not return a bounded response.");
        _ = McpWire.Result(line, 1);
        return line;
    }
}

/// <summary>Stateless HTTP MCP 2026 binding. Redirects are rejected and bodies are byte-bounded.</summary>
internal sealed class McpStreamableHttpTransport(HttpMessageHandler? handler = null,
    ICredentialStore? credentials = null) : IMcpRequestTransport
{
    private readonly ICredentialStore _credentials = credentials ?? OmniHost.CreateUserCredentialStore(OmniHost.CreatePlatformPaths());

    public async Task<string> SendAsync(McpServerDefinition server, string method, string parametersJson, CancellationToken token)
    {
        if (server.Transport != "http" || !Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("MCP transport mismatch.");
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: handler is null)
            { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var body = McpWire.Request(1, method, parametersJson);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", McpWire.Version);
        request.Headers.Add("Mcp-Method", method);
        if (server.AuthRef is { } authRef)
        {
            var secret = _credentials.Load(authRef, token);
            if (string.IsNullOrEmpty(secret)) throw new UnauthorizedAccessException("MCP credential reference is unavailable.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        if (method == "tools/call" && JsonDocument.Parse(parametersJson).RootElement.TryGetProperty("name", out var name))
            request.Headers.Add("Mcp-Name", SafeHeader(name.GetString() ?? ""));
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("MCP HTTP request timed out."); }
        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("MCP redirects are not followed.");
            response.EnsureSuccessStatusCode();
            string text;
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                try { text = await ReadMatchingSseResponseAsync(response.Content, deadline.Token, expectedId: 1)
                        .ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("MCP HTTP response timed out."); }
            }
            else
            {
                byte[] bytes;
                try { bytes = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("MCP HTTP response timed out."); }
                text = Encoding.UTF8.GetString(bytes);
            }
            _ = McpWire.Result(text, 1);
            return text;
        }
    }

    private static string SafeHeader(string value) => value.All(character => character is >= '!' and <= '~')
        && !value.StartsWith("=?base64?", StringComparison.Ordinal) ? value
        : "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > McpWire.MaximumPayloadBytes) throw new InvalidDataException("MCP response exceeds its byte limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task<string> ReadMatchingSseResponseAsync(HttpContent content, CancellationToken token,
        long expectedId)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        var eventData = new StringBuilder();
        var consumed = 0;
        var eventDataBytes = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var line = await ReadSseLineAsync(reader, token).ConfigureAwait(false);
            if (line is null)
            {
                if (eventData.Length > 0 && TryMatchingResponse(eventData.ToString(), expectedId, out var atEof)) return atEof;
                throw new InvalidDataException("MCP event stream closed without the matching response.");
            }
            consumed = checked(consumed + Encoding.UTF8.GetByteCount(line) + 1);
            if (consumed > McpWire.MaximumPayloadBytes)
                throw new InvalidDataException("MCP event stream exceeds its byte limit.");
            if (line.Length == 0)
            {
                if (eventData.Length > 0 && TryMatchingResponse(eventData.ToString(), expectedId, out var response))
                    return response;
                eventData.Clear();
                eventDataBytes = 0;
                continue;
            }
            if (line[0] == ':') continue;
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..].TrimStart(' ');
            if (field == "data")
            {
                if (eventData.Length > 0) { eventData.Append('\n'); eventDataBytes++; }
                eventData.Append(value);
                eventDataBytes = checked(eventDataBytes + Utf8ByteCount(value.AsSpan()));
                if (eventDataBytes > McpWire.MaximumPayloadBytes)
                    throw new InvalidDataException("MCP event payload exceeds its byte limit.");
            }
            // event/id/retry are parsed as SSE fields but do not change JSON-RPC correlation.
        }

        static bool TryMatchingResponse(string data, long id, out string response)
        {
            response = "";
            try
            {
                using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
                if (!json.RootElement.TryGetProperty("id", out var responseId)
                    || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt64(out var value)
                    || value != id) return false;
                response = json.RootElement.GetRawText();
                return true;
            }
            catch (JsonException) { return false; }
        }

        static async Task<string?> ReadSseLineAsync(TextReader reader, CancellationToken token)
        {
            var line = new StringBuilder();
            var lineBytes = 0;
            var pendingHighSurrogate = false;
            // Read one character at a time so StreamReader cannot wait to fill a large caller
            // buffer after a complete SSE event has already arrived on an open stream.
            var buffer = new char[1];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0)
                {
                    if (pendingHighSurrogate) lineBytes += 3;
                    if (line.Length == 0) return null;
                    if (line.Length > McpWire.MaximumPayloadBytes || lineBytes > McpWire.MaximumPayloadBytes)
                        throw new InvalidDataException("MCP event line exceeds its byte limit.");
                    return line.ToString();
                }
                var character = buffer[0];
                if (character == '\n')
                {
                    if (pendingHighSurrogate) { lineBytes += 3; pendingHighSurrogate = false; }
                    if (line.Length > 0 && line[^1] == '\r') { line.Length--; lineBytes--; }
                    if (line.Length > McpWire.MaximumPayloadBytes || lineBytes > McpWire.MaximumPayloadBytes)
                        throw new InvalidDataException("MCP event line exceeds its byte limit.");
                    return line.ToString();
                }
                if (pendingHighSurrogate)
                {
                    lineBytes += char.IsLowSurrogate(character) ? 4 : 3;
                    pendingHighSurrogate = false;
                    if (char.IsLowSurrogate(character)) { line.Append(character); continue; }
                }
                if (char.IsHighSurrogate(character)) pendingHighSurrogate = true;
                else if (!char.IsLowSurrogate(character)) lineBytes += character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                else lineBytes += 3;
                line.Append(character);
                if (line.Length > McpWire.MaximumPayloadBytes || lineBytes > McpWire.MaximumPayloadBytes)
                    throw new InvalidDataException("MCP event line exceeds its byte limit.");
            }
        }

        static int Utf8ByteCount(ReadOnlySpan<char> value)
        {
            var bytes = 0;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsHighSurrogate(character) && index + 1 < value.Length
                    && char.IsLowSurrogate(value[index + 1])) { bytes += 4; index++; }
                else if (char.IsSurrogate(character)) bytes += 3;
                else bytes += character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
            }
            return bytes;
        }
    }
}

internal sealed class McpTool : ITool
{
    private readonly McpServerDefinition _server;
    private readonly string _remoteName;
    private readonly IMcpRequestTransport _transport;
    private readonly Func<bool> _stillTrusted;
    private readonly bool _effectful;
    public ToolDescriptor Descriptor { get; }
    internal McpServerDefinition Server => _server;
    internal IMcpRequestTransport Transport => _transport;
    internal bool StillTrusted() => _stillTrusted();
    internal ProtectedToolSubstitution? FirstEffectApproval { get; }

    internal async Task<JsonElement> CallAsync(string remoteName, string argumentsJson, CancellationToken token)
    {
        if (!_stillTrusted()) throw new UnauthorizedAccessException("MCP source trust or identity changed.");
        using var arguments = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions { MaxDepth = 24 });
        if (arguments.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("MCP tool arguments must be an object.");
        using var callParameters = new MemoryStream();
        using (var writer = new Utf8JsonWriter(callParameters))
        {
            writer.WriteStartObject(); writer.WriteString("name", remoteName);
            writer.WritePropertyName("arguments"); arguments.RootElement.WriteTo(writer); writer.WriteEndObject();
        }
        var parameters = Encoding.UTF8.GetString(callParameters.ToArray());
        if (_server.Transport == "http")
            parameters = new RedactionPolicy().Redact(parameters);
        var response = await _transport.SendAsync(_server, "tools/call", parameters, token).ConfigureAwait(false);
        var result = McpWire.Result(response, 1);
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > 262_144)
            throw new InvalidDataException("MCP tool result exceeds its limit.");
        return result;
    }

    public McpTool(McpServerDefinition server, string name, string description, string schema,
        bool readOnlyHint, IMcpRequestTransport transport, Func<bool> stillTrusted)
    {
        _server = server; _remoteName = name; _transport = transport;
        _stillTrusted = stillTrusted ?? throw new ArgumentNullException(nameof(stillTrusted));
        var id = "mcp." + server.Id + "." + name;
        var effectiveReadOnly = server.HonorReadOnlyHint && readOnlyHint;
        _effectful = !effectiveReadOnly;
        var schemaIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.EffectiveIdentity
            + "\n" + id + "\n" + schema + "\ncredential:" + (server.AuthRef ?? "none")))).ToLowerInvariant();
        FirstEffectApproval = _effectful
            ? new ProtectedToolSubstitution("mcp.first-effect", id, schemaIdentity) : null;
        Descriptor = new ToolDescriptor(new ToolId(id), "External MCP tool: " + description,
            new InputSchema(schema), ["mcp", server.Id, server.Transport], effectiveReadOnly, false,
            effectiveReadOnly ? ToolRisk.Medium : ToolRisk.High,
            new ComponentSource(SourceKind.Mcp, server.Scope == "project" ? ScopeLevel.Project : ScopeLevel.User,
                server.Trust, server.Id, McpWire.Version, schemaIdentity), ToolProtection.None,
            effectiveReadOnly ? EffectClass.None : EffectClass.NonIdempotent);
    }

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!_stillTrusted())
            return new PreparationRejected("MCP source trust or identity changed.", null, ToolErrorCode.PermissionDenied);
        var claims = Claims(_server, context.WorkspaceRoot);
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            Descriptor.EffectClass, claims, Descriptor.Risk, null);
        return new Prepared(intent);
    }

    internal static ResourceClaims Claims(McpServerDefinition server, string workspaceRoot) => server.Transport == "stdio"
        ? new ResourceClaims([], [], [], new ProcessClaim(Path.GetFullPath(server.Command!), server.Args,
            "McpStdio", false, Path.GetFullPath(server.WorkingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(server.Command!))!)), [])
        : new ResourceClaims([], [], [new NetworkGrant(new Uri(server.Endpoint!).Host,
            new Uri(server.Endpoint!).IsDefaultPort ? null : new Uri(server.Endpoint!).Port)], null, []);

    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!_stillTrusted())
            return new ToolResult("MCP source trust or identity changed.", null, null, 0, false,
                EffectOutcome.None, isError: true, ToolErrorCode.PermissionDenied);
        try
        {
            var result = await CallAsync(_remoteName, intent.Intent.NormalizedArgumentsJson, cancellationToken).ConfigureAwait(false);
            var annotated = "{\"provenance\":{\"kind\":\"Mcp\",\"serverId\":" + Quote(_server.Id)
                + ",\"trust\":" + Quote(_server.Trust.ToString()) + ",\"tool\":" + Quote(_remoteName)
                + "},\"externalData\":" + result.GetRawText() + "}";
            var safe = new RedactionPolicy().Redact(annotated);
            if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
                return new ToolResult("MCP server reported a tool error.", safe, null, safe.Length, false,
                    _effectful ? EffectOutcome.Unknown : EffectOutcome.None, isError: true, ToolErrorCode.ToolFailure);
            return new ToolResult("MCP tool completed.", safe, null, safe.Length, false,
                _effectful ? EffectOutcome.Applied : EffectOutcome.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException
            or HttpRequestException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new ToolResult("MCP tool failed safely (" + exception.GetType().Name + ").", null, null, 0, false,
                _effectful ? EffectOutcome.Unknown : EffectOutcome.None, isError: true, ToolErrorCode.ToolFailure);
        }

        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>Optional web.search/web.fetch aliases over a declared MCP tool; no second network adapter exists.</summary>
internal sealed class McpWebTool : ITool
{
    private readonly McpTool _bridge;
    private readonly McpWebConnector _config;
    private readonly bool _search;
    public ToolDescriptor Descriptor { get; }
    internal ProtectedToolSubstitution FirstEffectApproval { get; }

    public McpWebTool(McpTool bridge, McpWebConnector config, bool search)
    {
        _bridge = bridge; _config = config; _search = search;
        var remoteName = search ? config.SearchTool : config.FetchTool;
        if (remoteName is null) throw new ArgumentException("MCP web mapping is not configured.");
        var name = search ? "web.search" : "web.fetch";
        var schema = search
            ? "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"maxResults\":{\"type\":\"integer\"}},\"required\":[\"query\"],\"additionalProperties\":false}"
            : "{\"type\":\"object\",\"properties\":{\"uri\":{\"type\":\"string\"},\"maxBytes\":{\"type\":\"integer\"}},\"required\":[\"uri\"],\"additionalProperties\":false}";
        var source = bridge.Descriptor.Source;
        Descriptor = new ToolDescriptor(new ToolId("mcp." + source.Owner + "." + name),
            search ? "Search the configured MCP web source." : "Fetch one configured MCP web URI.",
            new InputSchema(schema), ["mcp", "web", search ? "search" : "fetch", bridge.Server.Transport],
            readOnly: false, destructive: false, ToolRisk.High, source, ToolProtection.None, EffectClass.NonIdempotent);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            bridge.FirstEffectApproval?.IdentityHash ?? bridge.Descriptor.Id.ToString() + "\n" + name))).ToLowerInvariant();
        FirstEffectApproval = new ProtectedToolSubstitution("mcp.first-effect", Descriptor.Id.ToString(), identity);
    }

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!_bridge.StillTrusted())
            return new PreparationRejected("MCP source trust or identity changed.", null, ToolErrorCode.PermissionDenied);
        using var json = JsonDocument.Parse(call.NormalizedArgumentsJson);
        var root = json.RootElement;
        if (_search)
        {
            var query = root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
            var count = _config.MaxResults;
            if (root.TryGetProperty("maxResults", out var n)
                && (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out count) || count is < 1 || count > _config.MaxResults))
                return Invalid();
            if (string.IsNullOrWhiteSpace(query) || query.Length > 256) return Invalid();
        }
        else
        {
            var uriText = root.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (!AllowedUri(uriText, _config.AllowedTargets)) return Invalid();
            if (root.TryGetProperty("maxBytes", out var size)
                && (size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out var requested) || requested is < 1 || requested > _config.MaxBytes))
                return Invalid();
        }
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            Descriptor.EffectClass, McpTool.Claims(_bridge.Server, context.WorkspaceRoot), Descriptor.Risk, null);
        return new Prepared(intent);

        static PreparationRejected Invalid() => new("MCP web arguments exceed the configured target or bounds.", null,
            ToolErrorCode.InvalidArguments);
    }

    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!_bridge.StillTrusted())
            return new ToolResult("MCP source trust or identity changed.", null, null, 0, false,
                EffectOutcome.None, isError: true, ToolErrorCode.PermissionDenied);
        try
        {
            using var original = JsonDocument.Parse(intent.Intent.NormalizedArgumentsJson);
            using var mapped = MapArguments(original.RootElement);
            var result = await _bridge.CallAsync(_search ? _config.SearchTool! : _config.FetchTool!,
                mapped.RootElement.GetRawText(), cancellationToken).ConfigureAwait(false);
            if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
                return new ToolResult("MCP server reported a web tool error.", null, null, 0, false,
                    EffectOutcome.Unknown, isError: true, ToolErrorCode.ToolFailure);
            var content = _search ? BuildSearchResult(result, original.RootElement) : BuildFetchResult(result, original.RootElement);
            content = new RedactionPolicy().Redact(content);
            if (Encoding.UTF8.GetByteCount(content) > (_search ? 65_536 : _config.MaxBytes + 2048))
                throw new InvalidDataException("MCP web result exceeds its configured byte limit.");
            return new ToolResult("MCP web result received.", content, null, content.Length, false,
                EffectOutcome.Applied);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException
            or HttpRequestException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new ToolResult("MCP web request failed safely (" + exception.GetType().Name + ").", null, null, 0, false,
                EffectOutcome.Unknown, isError: true, ToolErrorCode.ToolFailure);
        }
    }

    private JsonDocument MapArguments(JsonElement original)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (_search)
            {
                writer.WriteString(_config.QueryArgument, original.GetProperty("query").GetString());
                var count = original.TryGetProperty("maxResults", out var n) ? n.GetInt32() : _config.MaxResults;
                writer.WriteNumber(_config.LimitArgument, Math.Min(count, _config.MaxResults));
            }
            else
            {
                writer.WriteString(_config.UriArgument, original.GetProperty("uri").GetString());
                var bytes = original.TryGetProperty("maxBytes", out var size) ? size.GetInt32() : _config.MaxBytes;
                writer.WriteNumber(_config.BytesArgument, Math.Min(bytes, _config.MaxBytes));
                writer.WritePropertyName("allowedTargets"); writer.WriteStartArray();
                foreach (var target in _config.AllowedTargets) writer.WriteStringValue(target);
                writer.WriteEndArray(); writer.WriteNumber("maxRedirects", _config.MaxRedirects);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    private string BuildSearchResult(JsonElement result, JsonElement request)
    {
        var data = StructuredData(result);
        if (!data.TryGetProperty("results", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MCP web search must return a bounded results array.");
        var maxResults = request.TryGetProperty("maxResults", out var count) ? count.GetInt32() : _config.MaxResults;
        var safe = new List<(string Uri, string Title, string Snippet)>();
        foreach (var entry in entries.EnumerateArray().Take(maxResults))
        {
            if (!entry.TryGetProperty("uri", out var uri) && !entry.TryGetProperty("url", out uri)) continue;
            var target = uri.ValueKind == JsonValueKind.String ? uri.GetString() : null;
            if (!AllowedUri(target, _config.AllowedTargets)) continue;
            var title = entry.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : "";
            var snippet = entry.TryGetProperty("snippet", out var excerpt) && excerpt.ValueKind == JsonValueKind.String ? excerpt.GetString() : "";
            safe.Add((target!, title ?? "", snippet ?? ""));
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WritePropertyName("provenance"); writer.WriteStartObject();
            writer.WriteString("kind", "Mcp"); writer.WriteString("serverId", _bridge.Server.Id); writer.WriteEndObject();
            writer.WriteString("query", request.GetProperty("query").GetString()); writer.WritePropertyName("results"); writer.WriteStartArray();
            foreach (var item in safe)
            {
                writer.WriteStartObject(); writer.WriteString("uri", item.Uri); writer.WriteString("title", item.Title);
                writer.WriteString("snippet", item.Snippet); writer.WriteString("source", _bridge.Server.Id); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private string BuildFetchResult(JsonElement result, JsonElement request)
    {
        var data = StructuredData(result);
        var requestedUri = request.GetProperty("uri").GetString()!;
        if (!data.TryGetProperty("redirectChain", out var chain) || chain.ValueKind != JsonValueKind.Array
            || chain.GetArrayLength() is < 1 || chain.GetArrayLength() > _config.MaxRedirects + 1
            || chain[0].ValueKind != JsonValueKind.String || !SameUri(chain[0].GetString(), requestedUri)
            || !data.TryGetProperty("finalUri", out var finalValue) || finalValue.ValueKind != JsonValueKind.String
            || chain[chain.GetArrayLength() - 1].ValueKind != JsonValueKind.String
            || !SameUri(chain[chain.GetArrayLength() - 1].GetString(), finalValue.GetString())
            || chain.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String
                || !AllowedUri(item.GetString(), _config.AllowedTargets)))
            throw new InvalidDataException("MCP fetch must report the complete, bounded, allowlisted redirectChain.");
        if (!data.TryGetProperty("content", out var contentValue) || contentValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("MCP web fetch must return text content and finalUri.");
        var content = contentValue.GetString()!;
        var bytes = request.TryGetProperty("maxBytes", out var size) ? size.GetInt32() : _config.MaxBytes;
        if (Encoding.UTF8.GetByteCount(content) > Math.Min(bytes, _config.MaxBytes))
            throw new InvalidDataException("MCP web fetch exceeded the requested byte limit.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WritePropertyName("provenance"); writer.WriteStartObject();
            writer.WriteString("kind", "Mcp"); writer.WriteString("serverId", _bridge.Server.Id); writer.WriteEndObject();
            writer.WriteString("requestedUri", requestedUri); writer.WriteString("finalUri", finalValue.GetString());
            writer.WritePropertyName("redirectChain"); chain.WriteTo(writer); writer.WriteString("content", content);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonElement StructuredData(JsonElement result)
    {
        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object)
            return structured;
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    try { using var document = JsonDocument.Parse(text.GetString()!, new JsonDocumentOptions { MaxDepth = 16 }); return document.RootElement.Clone(); }
                    catch (JsonException) { }
                }
        throw new InvalidDataException("MCP web response has no structured result.");
    }

    private static bool AllowedUri(string? value, IReadOnlyList<string> targets)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || uri.Scheme is not ("https" or "http")) return false;
        var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return targets.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameUri(string? left, string? right) => Uri.TryCreate(left, UriKind.Absolute, out var a)
        && Uri.TryCreate(right, UriKind.Absolute, out var b)
        && string.Equals(a.AbsoluteUri, b.AbsoluteUri, StringComparison.Ordinal);

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}

/// <summary>Reads configuration and consent without launching processes or opening network connections.</summary>
internal sealed class McpServerStore(IPlatformPaths paths, string workspaceRoot, bool workspaceTrusted)
{
    private readonly string _userFile = Path.Combine(paths.ConfigDirectory, "mcp", "servers.json");
    private readonly string _projectFile = Path.Combine(Path.GetFullPath(workspaceRoot), ".omnicore", "mcp.servers.json");
    private readonly string _userTrustFile = Path.Combine(paths.DataDirectory, "mcp", "trusted-remote.tsv");
    private readonly string _projectConsentFile = Path.Combine(paths.DataDirectory, "workspaces",
        WorkspaceId.Of(WorkspaceTrustStore.Canonical(workspaceRoot)).ToString(), "mcp-consent.tsv");

    public IReadOnlyList<McpServerDefinition> List()
    {
        var entries = new List<McpServerDefinition>();
        Add(_userFile, "user");
        if (File.Exists(_projectFile)) Add(_projectFile, "project");
        return entries.GroupBy(entry => entry.Id, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Single()).OrderBy(entry => entry.Scope, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();

        void Add(string file, string scope)
        {
            if (!File.Exists(file)) return;
            using var json = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 64)
                throw new InvalidDataException("MCP server configuration must be an array of at most 64 entries.");
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var server = McpServerDefinition.Parse(item, scope, workspaceTrusted);
                if (scope == "project")
                {
                    var projectMarker = TrustMarker(_projectConsentFile, server);
                    if (server.Scope == "project" && projectMarker is null)
                        server = server with { Scope = "project-unconsented" };
                    else if (projectMarker is not null)
                    {
                        var userMarker = server.Transport == "http" ? TrustMarker(_userTrustFile, server) : null;
                        server = server with
                        {
                            TrustGeneration = projectMarker + (userMarker is null ? "" : ":" + userMarker),
                            UserTrusted = server.Transport != "http" || userMarker is not null,
                        };
                    }
                }
                else if (scope == "user" && server.Transport == "http")
                {
                    var userMarker = TrustMarker(_userTrustFile, server);
                    server = server with { UserTrusted = userMarker is not null, TrustGeneration = userMarker ?? "" };
                }
                entries.Add(server);
            }
        }
    }

    public string InspectionJson()
    {
        var entries = List().Select(server =>
        {
            var displayEndpoint = server.Endpoint is { } value && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority) + "/…" : null;
            return "{" + Field("id", server.Id) + "," + Field("transport", server.Transport) + ","
                + Field("scope", server.Scope) + "," + Field("trust", server.Trust.ToString()) + ","
                + Field("identity", server.Identity) + "," + Field("endpoint", displayEndpoint ?? "") + ","
                + Field("state", server.Scope == "project-untrusted-workspace" ? "WorkspaceTrustRequired"
                    : server.Scope == "project-unconsented" ? "ConsentRequired"
                    : server.Trust == TrustLevel.Untrusted ? "Untrusted" : "Ready") + "}";
        });
        return "{\"servers\":[" + string.Join(",", entries) + "]}";

        static string Field(string key, string value) => Quote(key) + ":" + Quote(value);
        static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 2); builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: if (character < ' ') builder.Append("\\u").Append(((int)character).ToString("x4")); else builder.Append(character); break;
                }
            }
            return builder.Append('"').ToString();
        }
    }

    public void Trust(string id, bool trusted)
    {
        var server = List().SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("MCP server was not found or id is ambiguous.");
        if (server.Scope == "project-untrusted-workspace" || server.Scope == "project-unconsented" && !workspaceTrusted)
            throw new InvalidOperationException("Trust the workspace before consenting to its MCP server.");
        var isProject = server.Scope.StartsWith("project", StringComparison.Ordinal);
        if (isProject) WriteTrustMarker(_projectConsentFile, server, trusted);
        if (!isProject || server.Transport == "http") WriteTrustMarker(_userTrustFile, server, trusted);

        static void WriteTrustMarker(string file, McpServerDefinition server, bool enabled)
        {
            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
            lines.RemoveAll(line => line.Split('\t').FirstOrDefault() == server.Id);
            if (enabled) lines.Add(server.Id + "\t" + server.Identity + "\t" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllLines(temporary, lines.Order(StringComparer.Ordinal), Encoding.UTF8);
            File.Move(temporary, file, overwrite: true);
        }
    }

    public void AddStdio(string id, string executable, IReadOnlyList<string> args)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(args);
        if (!Path.IsPathRooted(executable) || args.Count > 64 || args.Any(argument => argument is null || argument.Length > 4096))
            throw new InvalidOperationException("MCP stdio requires an absolute executable and bounded literal arguments.");
        if (List().Any(server => server.Id == id)) throw new InvalidOperationException("MCP server id is already configured.");
        AppendUserEntry(writer =>
        {
            writer.WriteStartObject(); writer.WriteString("id", id); writer.WriteString("transport", "stdio");
            writer.WriteString("command", Path.GetFullPath(executable)); writer.WritePropertyName("args"); writer.WriteStartArray();
            foreach (var argument in args) writer.WriteStringValue(argument);
            writer.WriteEndArray(); writer.WriteEndObject();
        });
        _ = List();
    }

    public void AddHttp(string id, string endpoint)
    {
        ValidateId(id);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Remote MCP endpoints must use HTTPS and contain no embedded credentials, query or fragment.");
        if (List().Any(server => server.Id == id)) throw new InvalidOperationException("MCP server id is already configured.");
        var host = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
        AppendUserEntry(writer =>
        {
            writer.WriteStartObject(); writer.WriteString("id", id); writer.WriteString("transport", "http");
            writer.WriteString("endpoint", uri.AbsoluteUri); writer.WritePropertyName("allowedHosts");
            writer.WriteStartArray(); writer.WriteStringValue(host); writer.WriteEndArray(); writer.WriteEndObject();
        });
        _ = List();
    }

    public void SetCredentialReference(string id, string reference)
    {
        var matches = UserEntries().Where(entry => entry.TryGetProperty("id", out var value)
            && value.GetString() == id).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Only one configured User MCP server accepts credentials.");
        var user = matches[0];
        if (!user.TryGetProperty("transport", out var transport) || transport.GetString() != "http")
            throw new InvalidOperationException("Only remote MCP servers use bearer credentials.");
        if (!reference.StartsWith("mcp:" + id + ":auth:", StringComparison.Ordinal)
            || !System.Text.RegularExpressions.Regex.IsMatch(reference, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$"))
            throw new InvalidOperationException("MCP credential reference is invalid.");
        using var existing = JsonDocument.Parse(File.ReadAllBytes(_userFile));
        Directory.CreateDirectory(Path.GetDirectoryName(_userFile)!);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in existing.RootElement.EnumerateArray())
            {
                writer.WriteStartObject();
                var isTarget = entry.TryGetProperty("id", out var currentId) && currentId.GetString() == id;
                foreach (var property in entry.EnumerateObject())
                    if (!isTarget || property.Name != "authRef") property.WriteTo(writer);
                if (isTarget)
                    writer.WriteString("authRef", reference);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        var candidate = stream.ToArray();
        using (var validation = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = 16 }))
        {
            foreach (var entry in validation.RootElement.EnumerateArray())
                _ = McpServerDefinition.Parse(entry, "user", workspaceTrusted);
        }
        var temporary = _userFile + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, candidate);
        File.Move(temporary, _userFile, overwrite: true);
        _ = List();
    }

    public IReadOnlyList<ITool> DiscoverTools(IMcpRequestTransport stdio, IMcpRequestTransport http,
        CancellationToken cancellationToken)
    {
        var tools = new List<ITool>();
        foreach (var server in List().Where(item => item.Scope != "project-unconsented"
            && item.Trust is TrustLevel.Trusted or TrustLevel.Project))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transport = server.Transport == "stdio" ? stdio : http;
            var discovered = transport.SendAsync(server, "server/discover", "{}", cancellationToken)
                .GetAwaiter().GetResult();
            var discoveryResult = McpWire.Result(discovered, 1);
            if (!discoveryResult.TryGetProperty("supportedVersions", out var versions)
                || versions.ValueKind != JsonValueKind.Array
                || !versions.EnumerateArray().Any(version => version.ValueKind == JsonValueKind.String
                    && version.GetString() == McpWire.Version))
                throw new InvalidDataException("MCP server does not advertise the pinned protocol revision.");
            var listed = transport.SendAsync(server, "tools/list", "{}", cancellationToken).GetAwaiter().GetResult();
            var result = McpWire.Result(listed, 1);
            if (!result.TryGetProperty("tools", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray().Take(64))
            {
                if (!item.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) continue;
                var name = nameElement.GetString()!;
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$")) continue;
                var description = item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                    ? desc.GetString()![..Math.Min(1024, desc.GetString()!.Length)] : name;
                if (!item.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object) continue;
                var readOnly = server.Trust == TrustLevel.Trusted && item.TryGetProperty("annotations", out var annotations)
                    && annotations.ValueKind == JsonValueKind.Object
                    && annotations.TryGetProperty("readOnlyHint", out var hint) && hint.ValueKind == JsonValueKind.True;
                var bridge = new McpTool(server, name, description, schema.GetRawText(), readOnly, transport,
                    () => List().Any(current => current.Id == server.Id && current.EffectiveIdentity == server.EffectiveIdentity
                        && current.Trust == server.Trust));
                var mappedToWeb = server.Web is { } configuration
                    && (configuration.SearchTool == name || configuration.FetchTool == name);
                if (!mappedToWeb) tools.Add(bridge);
                if (server.Web is { } web)
                {
                    if (web.SearchTool == name) tools.Add(new McpWebTool(bridge, web, search: true));
                    if (web.FetchTool == name) tools.Add(new McpWebTool(bridge, web, search: false));
                }
            }
        }
        return tools;
    }

    private static string? TrustMarker(string file, McpServerDefinition server)
    {
        if (!File.Exists(file)) return null;
        foreach (var line in File.ReadLines(file))
        {
            var fields = line.Split('\t');
            if (fields.Length >= 2 && fields[0] == server.Id && fields[1] == server.Identity)
                return fields.Length >= 3 ? fields[2] : "legacy";
        }
        return null;
    }

    private IEnumerable<JsonElement> UserEntries()
    {
        if (!File.Exists(_userFile)) yield break;
        using var document = JsonDocument.Parse(File.ReadAllBytes(_userFile), new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("MCP User settings must be an array.");
        foreach (var entry in document.RootElement.EnumerateArray()) yield return entry.Clone();
    }

    private void AppendUserEntry(Action<Utf8JsonWriter> writeEntry)
    {
        var existing = UserEntries().ToArray();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in existing) item.WriteTo(writer);
            writeEntry(writer);
            writer.WriteEndArray();
        }
        var candidate = stream.ToArray();
        using var parsed = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = 16 });
        if (parsed.RootElement.GetArrayLength() > 64) throw new InvalidDataException("MCP server limit reached.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in parsed.RootElement.EnumerateArray())
        {
            var server = McpServerDefinition.Parse(entry, "user", workspaceTrusted);
            if (!ids.Add(server.Id)) throw new InvalidDataException("MCP server id is already configured.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_userFile)!);
        var temporary = _userFile + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, candidate);
        File.Move(temporary, _userFile, overwrite: true);
    }

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$"))
            throw new ArgumentException("MCP server id is invalid.", nameof(id));
    }
}

internal sealed class McpIntentBoundary : IToolIntentBoundary
{
    public ModelCapabilityDecision Evaluate(ToolIntent intent)
    {
        var id = intent.ToolId.ToString();
        if (!id.StartsWith("mcp.", StringComparison.Ordinal)) return ModelCapabilityDecision.Allow();
        var process = intent.Claims.Process;
        if (intent.Claims.Secrets.Count != 0 || intent.Claims.Reads.Count != 0 || intent.Claims.Writes.Count != 0)
            return ModelCapabilityDecision.Reject("MCP intent exceeds the configured resource ceiling.", null);
        if (process is not null && (process.NetworkRequired || intent.Claims.Network.Count != 0
            || process.Args.Any(argument => argument.Length > 4096)))
            return ModelCapabilityDecision.Reject("MCP stdio may not acquire network or broad filesystem authority.", null);
        if (process is null && intent.Claims.Network.Count != 1)
            return ModelCapabilityDecision.Reject("MCP HTTP intent must name exactly one configured destination.", null);
        return ModelCapabilityDecision.Allow();
    }
}
