namespace OmniCore.Host;

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Sandbox;

/// <summary>A bounded LSP client over an already owned Host process and the Strong sandbox.</summary>
internal sealed class LanguageServerTool : ITool
{
    private const int MaxDocumentBytes = 128 * 1024;
    private const int MaxOutputBytes = 192 * 1024;
    private readonly IReadOnlyList<LanguageServerDefinition> _servers;
    private readonly PersistentProcessSessionManager _processes;
    private readonly ISandboxProcessLauncher _launcher;
    private readonly IPathBoundaryValidator _paths;
    private readonly Func<string, bool> _isWorkspaceTrusted;
    private readonly Func<bool> _isConfigurationCurrent;
    private readonly IReadOnlyDictionary<string, (string? Path, string? Error)> _executables;
    private readonly bool _diagnostics;
    private readonly ToolDescriptor _descriptor;
    private readonly Action? _afterServerStartForTests;
    private readonly Action? _beforeSourceRecheckForTests;

    internal LanguageServerTool(IReadOnlyList<LanguageServerDefinition> servers, bool diagnostics,
        PersistentProcessSessionManager processes, ISandboxProcessLauncher launcher,
        IPathBoundaryValidator paths, IExecutableResolver executables, Func<string, bool> isWorkspaceTrusted,
        Func<bool> isConfigurationCurrent, Action? afterServerStartForTests = null,
        Action? beforeSourceRecheckForTests = null)
    {
        _servers = servers;
        _diagnostics = diagnostics;
        _processes = processes;
        _launcher = launcher;
        _paths = paths;
        _isWorkspaceTrusted = isWorkspaceTrusted;
        _isConfigurationCurrent = isConfigurationCurrent;
        _afterServerStartForTests = afterServerStartForTests;
        _beforeSourceRecheckForTests = beforeSourceRecheckForTests;
        var resolved = new Dictionary<string, (string? Path, string? Error)>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            try { resolved.Add(server.Name, (executables.Resolve(server.Command, AppContext.BaseDirectory).ResolvedPath, null)); }
            catch (ExecutableNotFoundException) { resolved.Add(server.Name, (null, "code.server.executable_not_found")); }
            catch (ExecutableRequiresShellException) { resolved.Add(server.Name, (null, "code.server.shell_script_not_allowed")); }
            catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException)
            { resolved.Add(server.Name, (null, "code.server.executable_unavailable")); }
        }
        _executables = resolved;
        var id = diagnostics ? "code.diagnostics" : "code.navigate";
        var schema = diagnostics
            ? "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}"
            : "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"operation\":{\"type\":\"string\",\"enum\":[\"definition\",\"references\",\"documentSymbol\"]},\"line\":{\"type\":\"integer\",\"minimum\":0},\"character\":{\"type\":\"integer\",\"minimum\":0}},\"required\":[\"path\",\"operation\"],\"additionalProperties\":false}";
        _descriptor = new ToolDescriptor(new ToolId(id), diagnostics
            ? "Read diagnostics for a workspace document from the user-configured language server."
            : "Navigate a workspace document through the user-configured language server (definition, references, or document symbols).",
            new InputSchema(schema), ["code", "lsp", "read"], true, false, ToolRisk.Low,
            ComponentSource.Core(), ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            using var document = JsonDocument.Parse(call.NormalizedArgumentsJson);
            var root = document.RootElement;
            var path = root.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.IndexOf('\0') >= 0)
                return Reject("code.path.invalid", ToolErrorCode.InvalidArguments);
            if (!_diagnostics)
            {
                var operation = root.GetProperty("operation").GetString();
                if (operation is not ("definition" or "references" or "documentSymbol"))
                    return Reject("code.operation.invalid", ToolErrorCode.InvalidArguments);
                if (operation != "documentSymbol" && (!root.TryGetProperty("line", out var line)
                    || !line.TryGetInt32(out var lineValue) || lineValue is < 0 or > 1_000_000
                    || !root.TryGetProperty("character", out var character)
                    || !character.TryGetInt32(out var characterValue) || characterValue is < 0 or > 1_000_000))
                    return Reject("code.position.invalid", ToolErrorCode.InvalidArguments);
            }
            var full = Path.GetFullPath(path, context.WorkspaceRoot);
            if (!_paths.IsWithin(full, context.WorkspaceRoot) || new RedactionPolicy().IsSecretPath(Path.GetRelativePath(context.WorkspaceRoot, full)))
                return Reject("code.path.outside_workspace", ToolErrorCode.CapabilityRefused);
            var extension = Path.GetExtension(full).ToLowerInvariant();
            var server = _servers.SingleOrDefault(candidate => candidate.Extensions.Contains(extension));
            if (server is null) return Reject("code.language.unsupported", ToolErrorCode.InvalidArguments);
            var resolved = _executables[server.Name];
            if (resolved.Path is null) return Reject(resolved.Error ?? "code.server.executable_unavailable", ToolErrorCode.CapabilityRefused);
            var executable = resolved.Path;
            if (_paths.IsWithin(executable, context.WorkspaceRoot))
                return Reject("code.server.executable_in_workspace", ToolErrorCode.CapabilityRefused);
            // The configured User command is the explicit authority to provide this read-only
            // language service. Model input selects only a file/position, never a process or argv.
            var claims = new ResourceClaims(["**"], [], [], null, []);
            return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.None, claims, ToolRisk.Low, null));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or IOException or UnauthorizedAccessException or ExecutableNotFoundException
            or ExecutableRequiresShellException)
        { return Reject("code.request.invalid", ToolErrorCode.InvalidArguments); }
    }

    public async global::System.Threading.Tasks.Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context, CancellationToken token)
    {
        var run = context.MutationRunId;
        var lane = context.MutationLaneId;
        if (run.Length == 0 || lane.Length == 0) return ToolResult.Error(ToolErrorCode.CapabilityRefused, "code.owner.required");
        string? handle = null;
        ExtensionWriteWorkspace? staging = null;
        var managerOwnsStaging = false;
        try
        {
            using var args = JsonDocument.Parse(intent.Intent.NormalizedArgumentsJson);
            var request = args.RootElement;
            var requestedPath = Path.GetFullPath(request.GetProperty("path").GetString()!, context.WorkspaceRoot);
            var extension = Path.GetExtension(requestedPath).ToLowerInvariant();
            var server = _servers.SingleOrDefault(candidate => candidate.Extensions.Contains(extension));
            if (server is null) return ToolResult.Error(ToolErrorCode.InvalidArguments, "code.language.unsupported");
            var resolvedExecutable = _executables[server.Name];
            var executable = resolvedExecutable.Path;
            if (executable is null)
                return ToolResult.Error(ToolErrorCode.CapabilityRefused, resolvedExecutable.Error ?? "code.server.executable_unavailable");
            if (_paths.IsWithin(executable, context.WorkspaceRoot))
                return ToolResult.Error(ToolErrorCode.CapabilityRefused, "code.server.executable_in_workspace");
            var path = requestedPath;
            if (!_paths.IsWithin(path, context.WorkspaceRoot) || !_isWorkspaceTrusted(context.WorkspaceRoot)
                || !_isConfigurationCurrent()
                || !File.Exists(path) || HasReparsePoint(path, context.WorkspaceRoot))
                return ToolResult.Error(ToolErrorCode.CapabilityRefused, "code.workspace_or_document_unavailable");
            var bytes = await ReadWorkspaceDocumentAsync(path, context.WorkspaceRoot, token).ConfigureAwait(false);
            if (bytes.Length > MaxDocumentBytes) return ToolResult.Error(ToolErrorCode.LimitExceeded, "code.document_too_large");
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var preHash = Convert.ToHexString(SHA256.HashData(bytes));
            var relative = Path.GetRelativePath(context.WorkspaceRoot, path);
            var sourceClaims = new ResourceClaims(["**"], [], [], null, []);
            staging = ExtensionWriteWorkspace.Create(context.WorkspaceRoot,
                Path.Combine(Path.GetTempPath(), "OmniCore", "lsp-workspaces"), sourceClaims);
            var stagedRoot = staging.Root;
            var stagedDocument = Path.GetFullPath(relative, stagedRoot);
            if (!File.Exists(stagedDocument) || HasReparsePoint(stagedDocument, stagedRoot))
                return ToolResult.Error(ToolErrorCode.CapabilityRefused, "code.document_not_in_safe_snapshot");
            var stagedBytes = await File.ReadAllBytesAsync(stagedDocument, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(stagedBytes)))
                return ToolResult.Error(ToolErrorCode.ToolFailure, "code.workspace_snapshot_changed");
            var uri = new Uri(stagedDocument).AbsoluteUri;
            var documentVersion = BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(preHash)) & int.MaxValue;
            if (documentVersion == 0) documentVersion = 1;
            var executableHash = await HashExecutableAsync(executable, token).ConfigureAwait(false);
            var startupFiles = await ConfiguredFileArgumentsAsync(server.Args, context.WorkspaceRoot, token).ConfigureAwait(false);
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, executable, server.Args,
                stagedRoot, new Dictionary<string, string>(StringComparer.Ordinal),
                [new SandboxAllowedPath(stagedRoot, SandboxPathAccess.Read),
                 new SandboxAllowedPath(executable, SandboxPathAccess.Read),
                 .. startupFiles.Paths],
                new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(35))
            { Duplex = true };
            var started = await _processes.StartAsync(run, lane, launch, _launcher, context.EmitEvent, token,
                staging, stagedRoot, intent.Intent.ToolCallId, intent.Intent.ToolId, context.MutationTurnId,
                sourceProcessClaim: null).ConfigureAwait(false);
            handle = started.Handle;
            managerOwnsStaging = handle is not null;
            if (handle is null || started.State != "running")
                return ToolResult.Error(ToolErrorCode.CapabilityRefused, started.Error ?? "code.server.launch_failed");
            _afterServerStartForTests?.Invoke();
            var executableHashAfterLaunch = await HashExecutableAsync(executable, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(executableHash),
                    Convert.FromHexString(executableHashAfterLaunch)))
                return ToolResult.Error(ToolErrorCode.ToolFailure, "code.server_command_changed_during_launch");
            var startupFilesAfterLaunch = await ConfiguredFileArgumentsAsync(server.Args, context.WorkspaceRoot, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(startupFiles.Fingerprint),
                    Convert.FromHexString(startupFilesAfterLaunch.Fingerprint)))
                return ToolResult.Error(ToolErrorCode.ToolFailure, "code.server_startup_file_changed_during_launch");
            using var protocol = new OwnedLspProtocol(new ManagedLspTransport(_processes, handle, run, lane), token);
            var rootUri = new Uri(Path.GetFullPath(stagedRoot) + Path.DirectorySeparatorChar).AbsoluteUri;
            var initialized = await protocol.RequestAsync("initialize", new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["clientInfo"] = new JsonObject { ["name"] = "OmniCore", ["version"] = "1" },
                ["rootUri"] = rootUri,
                ["capabilities"] = new JsonObject { ["textDocument"] = JsonNode.Parse("{\"definition\":{},\"references\":{},\"documentSymbol\":{},\"diagnostic\":{}}") },
                ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = "workspace" })
            }, token).ConfigureAwait(false);
            if (initialized.ValueKind != JsonValueKind.Object) return ToolResult.Error(ToolErrorCode.ToolFailure, "code.server.invalid_initialize");
            await protocol.NotifyAsync("initialized", new JsonObject(), token).ConfigureAwait(false);
            await protocol.NotifyAsync("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = uri,
                    ["languageId"] = server.LanguageId,
                    ["version"] = documentVersion,
                    ["text"] = text
                }
            }, token).ConfigureAwait(false);
            JsonElement result;
            if (_diagnostics)
            {
                var diagnostic = await protocol.RequestOrNotificationAsync("textDocument/diagnostic",
                    new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, uri, token).ConfigureAwait(false);
                result = diagnostic;
            }
            else
            {
                var op = request.GetProperty("operation").GetString()!;
                var line = request.TryGetProperty("line", out var lineElement) ? lineElement.GetInt32() : 0;
                var character = request.TryGetProperty("character", out var characterElement) ? characterElement.GetInt32() : 0;
                var method = op switch
                {
                    "definition" => "textDocument/definition",
                    "references" => "textDocument/references",
                    _ => "textDocument/documentSymbol"
                };
                var parameters = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } };
                if (op != "documentSymbol")
                {
                    parameters["position"] = new JsonObject { ["line"] = line, ["character"] = character };
                    if (op == "references") parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
                }
                result = await protocol.RequestAsync(method, parameters, token).ConfigureAwait(false);
            }
            _beforeSourceRecheckForTests?.Invoke();
            var afterBytes = await ReadWorkspaceDocumentAsync(path, context.WorkspaceRoot, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(preHash), SHA256.HashData(afterBytes)))
                return ToolResult.Error(ToolErrorCode.ToolFailure, "code.document_changed_during_request");
            if (!_isWorkspaceTrusted(context.WorkspaceRoot))
                return ToolResult.Error(ToolErrorCode.CapabilityRefused, "code.workspace_trust_revoked");
            if (!_isConfigurationCurrent())
                return ToolResult.Error(ToolErrorCode.CapabilityRefused, "code.server_configuration_changed");
            var jsonNode = JsonNode.Parse(result.GetRawText());
            RemapUris(jsonNode, stagedRoot, context.WorkspaceRoot);
            var json = new JsonObject { ["documentVersion"] = preHash.ToLowerInvariant(), ["result"] = jsonNode }.ToJsonString();
            if (Encoding.UTF8.GetByteCount(json) > MaxOutputBytes) return ToolResult.Error(ToolErrorCode.LimitExceeded, "code.result_too_large");
            return new ToolResult(json, null, null, 0, false, EffectOutcome.None);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return ToolResult.Error(ToolErrorCode.ToolFailure, "code.request.cancelled"); }
        catch (TimeoutException) { return ToolResult.Error(ToolErrorCode.ToolFailure, "code.server.timeout"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or DecoderFallbackException
            or UnauthorizedAccessException or ArgumentException or KeyNotFoundException
            or StrongSandboxUnavailableBeforeLaunchException)
        { return ToolResult.Error(ToolErrorCode.ToolFailure, "code.server." + ex.GetType().Name); }
        finally
        {
            if (handle is not null)
            {
                try { await _processes.StopAsync(handle, run, lane, context.EmitEvent, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or KeyNotFoundException) { }
            }
            if (!managerOwnsStaging) PersistentProcessSessionManager.DisposeStaging(staging);
        }
    }

    private static async global::System.Threading.Tasks.Task<string> HashExecutableAsync(string executable, CancellationToken token)
    {
        var info = new FileInfo(executable);
        if (!info.Exists || info.Length is <= 0 or > 100 * 1024 * 1024
            || (info.Attributes & FileAttributes.ReparsePoint) != 0 || HasAnyReparseAncestor(executable))
            throw new InvalidDataException("Configured language-server executable is not a bounded regular file.");
        await using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static async global::System.Threading.Tasks.Task<byte[]> ReadWorkspaceDocumentAsync(string path, string workspaceRoot, CancellationToken token)
    {
        if (HasReparsePoint(path, workspaceRoot)) throw new InvalidDataException("Workspace document contains a link.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxDocumentBytes) throw new InvalidDataException("Workspace document exceeds the safe read limit.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (HasReparsePoint(path, workspaceRoot)) throw new InvalidDataException("Workspace document changed into a link while opening.");
        using var buffer = new MemoryStream((int)info.Length);
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxDocumentBytes) throw new InvalidDataException("Workspace document exceeds the safe read limit.");
            buffer.Write(chunk, 0, read);
        }
        if (HasReparsePoint(path, workspaceRoot)) throw new InvalidDataException("Workspace document changed during read.");
        return buffer.ToArray();
    }

    private async global::System.Threading.Tasks.Task<(IReadOnlyList<SandboxAllowedPath> Paths, string Fingerprint)> ConfiguredFileArgumentsAsync(
        IReadOnlyList<string> arguments, string workspaceRoot, CancellationToken token)
    {
        var allowed = new List<SandboxAllowedPath>();
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var argument in arguments)
        {
            var candidateInWorkspace = Path.GetFullPath(argument, workspaceRoot);
            if (_paths.IsWithin(candidateInWorkspace, workspaceRoot) && File.Exists(candidateInWorkspace))
                throw new InvalidDataException("Language-server arguments cannot reference workspace-controlled files.");
            if (!Path.IsPathFullyQualified(argument) || !File.Exists(argument)) continue;
            var full = Path.GetFullPath(argument);
            if (_paths.IsWithin(full, workspaceRoot))
                throw new InvalidDataException("Language-server arguments cannot reference workspace-controlled files.");
            var info = new FileInfo(full);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 100 * 1024 * 1024
                || new RedactionPolicy().IsSecretPath(full) || HasAnyReparseAncestor(full))
                throw new InvalidDataException("Language-server argument file is not a bounded regular file.");
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            fingerprint.AppendData(Encoding.UTF8.GetBytes(full));
            fingerprint.AppendData(hash);
            allowed.Add(new SandboxAllowedPath(full, SandboxPathAccess.Read));
        }
        return (allowed, Convert.ToHexString(fingerprint.GetHashAndReset()));
    }

    internal static void RemapUris(JsonNode? node, string stagedRoot, string workspaceRoot)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                if ((pair.Key is "uri" or "targetUri") && pair.Value is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile)
                        throw new InvalidDataException("Language server returned a non-file location.");
                    var full = Path.GetFullPath(uri.LocalPath);
                    if (!_Within(full, stagedRoot)) throw new InvalidDataException("Language server returned a location outside its workspace snapshot.");
                    var relative = Path.GetRelativePath(stagedRoot, full);
                    obj[pair.Key] = new Uri(Path.GetFullPath(relative, workspaceRoot)).AbsoluteUri;
                }
                else RemapUris(pair.Value, stagedRoot, workspaceRoot);
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array) RemapUris(item, stagedRoot, workspaceRoot);

        static bool _Within(string candidate, string root)
        {
            var full = Path.GetFullPath(candidate);
            var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return full.Equals(basePath, comparison) || full.StartsWith(basePath + Path.DirectorySeparatorChar, comparison);
        }
    }

    private static bool HasReparsePoint(string path, string root)
    {
        var current = path;
        while (_pathsWithin(current, root))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || Path.Equals(parent, current)) break;
            current = parent;
        }
        return false;
        static bool _pathsWithin(string candidate, string boundary)
        {
            var full = Path.GetFullPath(candidate);
            var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return full.Equals(basePath, comparison) || full.StartsWith(basePath + Path.DirectorySeparatorChar, comparison);
        }
    }

    private static bool HasAnyReparseAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Equals(current, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            current = parent;
        }
    }

    private PreparationRejected Reject(string reason, ToolErrorCode code) => new(reason, null, code);
}

/// <summary>Small JSON-RPC/LSP stdio transport. All messages are UTF-8 byte-count framed and bounded.</summary>
internal interface ILspProcessTransport
{
    global::System.Threading.Tasks.Task SendAsync(string text, CancellationToken token);
    global::System.Threading.Tasks.Task<LspTransportRead> ReadAsync(TimeSpan wait, CancellationToken token);
}

internal sealed record LspTransportRead(string Output, string State, bool Gap = false);

internal sealed class ManagedLspTransport(PersistentProcessSessionManager processes, string handle,
    string run, string lane) : ILspProcessTransport
{
    private long _cursor;
    public async global::System.Threading.Tasks.Task SendAsync(string text, CancellationToken token) =>
        await processes.SendAsync(handle, run, lane, text, false, null, null, token).ConfigureAwait(false);

    public async global::System.Threading.Tasks.Task<LspTransportRead> ReadAsync(TimeSpan wait, CancellationToken token)
    {
        var read = await processes.ReadAsync(handle, run, lane, _cursor, 8, wait, token).ConfigureAwait(false);
        _cursor = read.Last;
        return new LspTransportRead(string.Concat(read.Output.Where(chunk => chunk.Stream == "stdout").Select(chunk => chunk.Text)),
            read.State, read.Gap);
    }
}

internal sealed class OwnedLspProtocol(ILspProcessTransport transport, CancellationToken parentToken,
    TimeSpan? responseTimeout = null) : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly StringBuilder _pending = new();
    private readonly Queue<JsonDocument> _notifications = new();
    private readonly TimeSpan _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(10);
    private int _nextId;

    internal async global::System.Threading.Tasks.Task<JsonElement> RequestAsync(string method, JsonNode parameters, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _nextId);
        await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters.DeepClone()
        }, token).ConfigureAwait(false);
        return await WaitResponseAsync(id, token).ConfigureAwait(false);
    }

    internal async global::System.Threading.Tasks.Task<JsonElement> RequestOrNotificationAsync(string method, JsonNode parameters, string uri,
        CancellationToken token)
    {
        try { return await RequestAsync(method, parameters, token).ConfigureAwait(false); }
        catch (LspMethodUnsupportedException)
        {
            return await WaitNotificationAsync("textDocument/publishDiagnostics", uri, token).ConfigureAwait(false);
        }
    }

    internal global::System.Threading.Tasks.Task NotifyAsync(string method, JsonNode parameters, CancellationToken token) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters.DeepClone() }, token);

    private async global::System.Threading.Tasks.Task<JsonElement> WaitResponseAsync(int id, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, parentToken);
        timeout.CancelAfter(_responseTimeout);
        while (true)
        {
            var frame = await NextMessageAsync(timeout.Token).ConfigureAwait(false);
            var root = frame.RootElement;
            if (root.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var number) && number == id)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var unsupported = error.TryGetProperty("code", out var code) && code.GetInt32() == -32601;
                    frame.Dispose();
                    if (unsupported)
                        throw new LspMethodUnsupportedException();
                    throw new IOException("language_server_rpc_error");
                }
                JsonElement result;
                if (root.TryGetProperty("result", out var resultValue)) result = resultValue.Clone();
                else { using var nullResult = JsonDocument.Parse("null"); result = nullResult.RootElement.Clone(); }
                frame.Dispose();
                return result;
            }
            if (root.TryGetProperty("method", out _) && !root.TryGetProperty("id", out _))
                _notifications.Enqueue(frame);
            else
            {
                try { await RejectServerRequestAsync(root, timeout.Token).ConfigureAwait(false); }
                finally { frame.Dispose(); }
            }
        }
    }

    private async global::System.Threading.Tasks.Task<JsonElement> WaitNotificationAsync(string method, string uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, parentToken);
        timeout.CancelAfter(_responseTimeout);
        while (true)
        {
            var queued = _notifications.ToArray();
            foreach (var pending in queued)
            {
                _notifications.Dequeue();
                if (MatchesNotification(pending.RootElement, method, uri)) return pending.RootElement.GetProperty("params").Clone();
                pending.Dispose();
            }
            var frame = await NextMessageAsync(timeout.Token).ConfigureAwait(false);
            var root = frame.RootElement;
            if (MatchesNotification(root, method, uri))
            {
                var parameters = root.GetProperty("params").Clone();
                frame.Dispose();
                return parameters;
            }
            if (root.TryGetProperty("method", out _) && !root.TryGetProperty("id", out _)) _notifications.Enqueue(frame);
            else
            {
                try { await RejectServerRequestAsync(root, timeout.Token).ConfigureAwait(false); }
                finally { frame.Dispose(); }
            }
        }
    }

    private static bool MatchesNotification(JsonElement root, string method, string uri) =>
        root.TryGetProperty("method", out var name) && name.GetString() == method
        && root.TryGetProperty("params", out var parameters)
        && parameters.TryGetProperty("uri", out var uriNode) && uriNode.GetString() == uri;

    private async global::System.Threading.Tasks.Task RejectServerRequestAsync(JsonElement root, CancellationToken token)
    {
        if (!root.TryGetProperty("method", out var method)) return;
        var name = method.GetString();
        if (name is "workspace/applyEdit" or "workspace/executeCommand")
        {
            if (root.TryGetProperty("id", out var deniedId))
                await SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JsonNode.Parse(deniedId.GetRawText()),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "client method unavailable" }
                }, token).ConfigureAwait(false);
            throw new IOException("language_server_requested_disallowed_method");
        }
        if (!root.TryGetProperty("id", out var id)) return;
        if (name == "workspace/configuration")
        {
            var itemCount = root.TryGetProperty("params", out var parameters)
                && parameters.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.GetArrayLength() : 0;
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["result"] = new JsonArray(Enumerable.Repeat<JsonNode?>(null, Math.Min(itemCount, 64)).ToArray())
            }, token).ConfigureAwait(false);
            return;
        }
        if (name == "client/registerCapability")
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = null }, token).ConfigureAwait(false);
            return;
        }
        await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method unavailable" }
        }, token).ConfigureAwait(false);
    }

    private async global::System.Threading.Tasks.Task<JsonDocument> NextMessageAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_responseTimeout);
        while (true)
        {
            if (TryTakeFrame(out var message)) return message;
            var read = await transport.ReadAsync(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
            if (read.Gap) throw new IOException("language_server_output_gap");
            if (read.State is not ("running" or "starting")) throw new IOException("language_server_exited");
            _pending.Append(read.Output);
            if (Utf8.GetByteCount(_pending.ToString()) > 2_097_152) throw new IOException("language_server_output_limit");
        }
    }

    public void Dispose()
    {
        while (_notifications.TryDequeue(out var notification)) notification.Dispose();
    }

    private bool TryTakeFrame(out JsonDocument message)
    {
        message = null!;
        var text = _pending.ToString();
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0) return false;
        if (separator > 8192) throw new IOException("language_server_header_limit");
        var header = text[..separator];
        int? length = null;
        var sawLength = false;
        foreach (var line in header.Split("\r\n", StringSplitOptions.None))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && !sawLength && int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out var parsed))
            { length = parsed; sawLength = true; }
            else if (line.Length != 0 && !line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                throw new IOException("language_server_invalid_header");
        }
        if (length is null or < 0 or > 1_048_576) throw new IOException("language_server_invalid_length");
        var start = separator + 4;
        var chars = 0; var bytes = 0;
        while (start + chars < text.Length && bytes < length.Value)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(start + chars), out var rune, out var consumed);
            if (status != OperationStatus.Done) return false;
            bytes += rune.Utf8SequenceLength;
            chars += consumed;
        }
        if (bytes < length.Value) return false;
        if (bytes != length.Value) throw new IOException("language_server_invalid_utf8_length");
        message = JsonDocument.Parse(text.Substring(start, chars), new JsonDocumentOptions { MaxDepth = 64 });
        if (message.RootElement.ValueKind != JsonValueKind.Object
            || !message.RootElement.TryGetProperty("jsonrpc", out var rpc) || rpc.GetString() != "2.0")
        { message.Dispose(); message = null!; throw new IOException("language_server_invalid_json_rpc"); }
        if (message.RootElement.TryGetProperty("method", out _) && !message.RootElement.TryGetProperty("id", out _)
            && _notifications.Count >= 64)
        { message.Dispose(); message = null!; throw new IOException("language_server_notification_limit"); }
        _pending.Remove(0, start + chars);
        return true;
    }

    private async global::System.Threading.Tasks.Task SendAsync(JsonNode value, CancellationToken token)
    {
        var body = value.ToJsonString();
        var byteLength = Utf8.GetByteCount(body);
        if (byteLength > 1_048_576) throw new IOException("language_server_request_limit");
        var frame = "Content-Length: " + byteLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\r\n\r\n" + body;
        var start = 0;
        while (start < frame.Length)
        {
            var end = start;
            var byteCount = 0;
            while (end < frame.Length)
            {
                var status = Rune.DecodeFromUtf16(frame.AsSpan(end), out var rune, out var consumed);
                if (status != OperationStatus.Done) throw new IOException("language_server_invalid_utf16");
                if (byteCount + rune.Utf8SequenceLength > 12_000) break;
                byteCount += rune.Utf8SequenceLength;
                end += consumed;
            }
            if (end == start) throw new IOException("language_server_invalid_utf16");
            await transport.SendAsync(frame[start..end], token).ConfigureAwait(false);
            start = end;
        }
    }
}

internal sealed class LspMethodUnsupportedException : IOException;