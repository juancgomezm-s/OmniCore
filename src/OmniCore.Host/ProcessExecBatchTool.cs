namespace OmniCore.Host;

using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Extensions;
using OmniCore.Sandbox;
using OmniCore.Tools;

/// <summary>Runs the existing process.exec/shell.exec contract in a Host-created private copy.
/// Declared write paths produce the same protected batch consumed by extension.batch.apply.</summary>
internal sealed class ProcessExecBatchTool : ITool, IStagedExtensionBatchProducer, IHostResolvedProcessExecutable
{
    private const int MaximumPreviewChars = 24_000;
    private readonly ToolDescriptor _descriptor;
    private readonly PersistentProcessSessionManager _manager;
    private readonly ISandboxProcessLauncher _launcher;
    private readonly IPathBoundaryValidator _paths;
    private readonly IExecutableResolver _executables;
    private readonly bool _shell;
    private readonly bool _allowWriters;

    internal ProcessExecBatchTool(string id, PersistentProcessSessionManager manager, ISandboxProcessLauncher launcher,
        IPathBoundaryValidator paths, IExecutableResolver executables, bool allowWriters)
    {
        if (id is not ("process.exec" or "shell.exec")) throw new ArgumentOutOfRangeException(nameof(id));
        _shell = id == "shell.exec";
        _manager = manager;
        _launcher = launcher;
        _paths = paths;
        _executables = executables;
        _allowWriters = allowWriters;
        var schema = _shell
            ? "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"},\"cwd\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\"},\"readPaths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":64},\"writePaths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":64}},\"required\":[\"command\",\"cwd\",\"timeoutSeconds\"],\"additionalProperties\":false}"
            : "{\"type\":\"object\",\"properties\":{\"executable\":{\"type\":\"string\"},\"argv\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"cwd\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\"},\"networkRequired\":{\"type\":\"boolean\"},\"readPaths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":64},\"writePaths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":64}},\"required\":[\"executable\",\"argv\",\"cwd\",\"timeoutSeconds\"],\"additionalProperties\":false}";
        _descriptor = ProcessSessionToolJson.Descriptor(id, _shell ? ToolRisk.Critical : ToolRisk.High,
            _shell
                ? "Runs a raw shell command inside a private workspace copy. Declare readPaths and writePaths; output is published as one protected batch."
                : "Runs literal argv inside a private workspace copy. Declare readPaths and writePaths; output is published as one protected batch.",
            schema);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public bool IsHostResolvedProcessExecutable(ToolIntent intent) => intent.ToolId.Equals(Descriptor.Id)
        && intent.Claims.Process is { Isolation: ProcessIsolation.PrivateProcessWorkspace }
        && Path.IsPathFullyQualified(intent.Claims.Process.Executable);

    public bool IsTrustedPrivateWorkspaceWriter(ToolIntent intent) =>
        intent.ToolId.Equals(Descriptor.Id)
        && intent.Claims.Process is { NetworkRequired: false, Isolation: ProcessIsolation.PrivateProcessWorkspace }
        && intent.Claims.Writes.Count is > 0 and <= 64
        && intent.Claims.Network.Count == 0 && intent.Claims.Secrets.Count == 0;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            using var document = JsonDocument.Parse(call.NormalizedArgumentsJson);
            var root = document.RootElement;
            string requestedExecutable;
            IReadOnlyList<string> arguments;
            string requestedCwd;
            int timeout;
            var networkRequired = false;
            if (_shell)
            {
                requestedCwd = root.GetProperty("cwd").GetString() ?? "";
                timeout = root.GetProperty("timeoutSeconds").GetInt32();
                var command = root.GetProperty("command").GetString() ?? "";
                if (command.Length == 0 || requestedCwd.Length == 0 || timeout is < 1 or > 3600)
                    return Rejected("Invalid shell request.");
                requestedExecutable = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
                    : "/bin/sh";
                arguments = OperatingSystem.IsWindows() ? ["/d", "/s", "/c", command] : ["-c", command];
            }
            else
            {
                requestedExecutable = root.GetProperty("executable").GetString() ?? "";
                requestedCwd = root.GetProperty("cwd").GetString() ?? "";
                timeout = root.GetProperty("timeoutSeconds").GetInt32();
                networkRequired = root.TryGetProperty("networkRequired", out var network) && network.ValueKind == JsonValueKind.True;
                if (!root.TryGetProperty("argv", out var argv) || argv.ValueKind != JsonValueKind.Array
                    || argv.GetArrayLength() > 256 || string.IsNullOrWhiteSpace(requestedExecutable)
                    || string.IsNullOrWhiteSpace(requestedCwd) || timeout is < 1 or > 3600)
                    return Rejected("Invalid process request.");
                var values = argv.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString() : null).ToArray();
                if (values.Any(value => value is null || value.Length > 8192)) return Rejected("Invalid argv.");
                arguments = values!;
            }
            var cwd = ResolveCwd(context.WorkspaceRoot, requestedCwd);
            if (cwd is null || !_paths.IsWithin(cwd, context.WorkspaceRoot) || !Directory.Exists(cwd))
                return new PreparationRejected("cwd must be an existing directory inside the workspace.", null,
                    ToolErrorCode.InvalidArguments);
            var executable = _executables.Resolve(requestedExecutable, context.WorkspaceRoot).ResolvedPath;
            var reads = ReadPatterns(root, "readPaths");
            var writes = ReadPatterns(root, "writePaths");
            if (!_allowWriters && writes.Length > 0)
                return new PreparationRejected("Batch publication is unavailable for this process tool context.", null,
                    ToolErrorCode.CapabilityRefused);
            if (networkRequired && writes.Length > 0)
                return new PreparationRejected("A networked process cannot publish workspace writes.", null,
                    ToolErrorCode.CapabilityRefused);
            if (reads.Length > 64 || writes.Length > 64
                || reads.Concat(writes).Any(pattern => !ExtensionWriteWorkspace.IsValidPattern(pattern)))
                return Rejected("Invalid process path scope.");
            var redaction = new RedactionPolicy();
            if (reads.Concat(writes).Any(redaction.IsSecretPath))
                return new PreparationRejected("Process path scope includes a protected path.", null,
                    ToolErrorCode.CapabilityRefused);
            if (_paths.IsWithin(executable, context.WorkspaceRoot))
            {
                var relativeExecutable = Path.GetRelativePath(context.WorkspaceRoot, executable).Replace(Path.DirectorySeparatorChar, '/');
                if (redaction.IsSecretPath(relativeExecutable))
                    return new PreparationRejected("The executable is in a protected path.", null, ToolErrorCode.CapabilityRefused);
                if (!reads.Any(pattern => ExtensionWriteWorkspace.IsPathWithinPattern(pattern, relativeExecutable)))
                    reads = [.. reads, relativeExecutable];
            }
            if (reads.Length > 64)
                return new PreparationRejected("Process read scope exceeds the safe limit.", null, ToolErrorCode.LimitExceeded);
            var claim = new ProcessClaim(executable, arguments, "External", networkRequired, cwd,
                ProcessIsolation.PrivateProcessWorkspace);
            var claims = new ResourceClaims(reads, writes, [], claim, []);
            return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.NonIdempotent, claims, Descriptor.Risk, null));
        }
        catch (ExecutableNotFoundException) { return Rejected("Process executable could not be resolved."); }
        catch (ExecutableRequiresShellException) { return Rejected("Batch scripts require shell.exec."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException
            or KeyNotFoundException or InvalidDataException)
        { return Rejected("Invalid process request."); }
    }

    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ExtensionWriteWorkspace? staging = null;
        var managerOwnsStaging = false;
        try
        {
            var claim = intent.Intent.Claims.Process
                ?? throw new InvalidOperationException("Resolved process claim is missing.");
            var writer = intent.Intent.Claims.Writes.Count > 0;
            staging = ExtensionWriteWorkspace.Create(context.WorkspaceRoot,
                Path.Combine(Path.GetTempPath(), "OmniCore", "process-workspaces"), intent.Intent.Claims);
            // Workspace executables must come from the private snapshot. The explicit shell
            // adapter resolves its fixed system shell outside the workspace; the Strong launcher
            // grants that resolved executable's directory read/execute access separately.
            var executable = _paths.IsWithin(claim.Executable, context.WorkspaceRoot)
                ? ToStagedPath(claim.Executable, context.WorkspaceRoot, staging.Root)
                : Path.GetFullPath(claim.Executable);
            var cwd = ToStagedPath(claim.WorkingDirectory!, context.WorkspaceRoot, staging.Root);
            Directory.CreateDirectory(cwd);
            if (_paths.IsWithin(claim.Executable, context.WorkspaceRoot) && !File.Exists(executable))
                throw new FileNotFoundException("Resolved process executable is absent from its authorized private copy.");
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, executable, claim.Args, cwd,
                new Dictionary<string, string>(),
                [new SandboxAllowedPath(staging.Root, SandboxPathAccess.Read
                    | (writer ? SandboxPathAccess.Write : SandboxPathAccess.None))],
                new SandboxNetworkPolicy(claim.NetworkRequired ? SandboxNetworkMode.AllowAll : SandboxNetworkMode.Deny, []),
                TimeSpan.FromSeconds(ReadTimeout(intent.Intent.NormalizedArgumentsJson)))
            { Duplex = true, WindowsShellCommandLine = _shell && OperatingSystem.IsWindows() };
            var started = await _manager.StartAsync(context.MutationRunId, context.MutationLaneId, launch, _launcher,
                context.EmitEvent, cancellationToken, staging, context.WorkspaceRoot,
                intent.Intent.ToolCallId, intent.Intent.ToolId, context.MutationTurnId,
                writer ? intent.Intent.Claims : null).ConfigureAwait(false);
            managerOwnsStaging = started.Handle is not null;
            if (started.Handle is null) return ToolResult.Error(ToolErrorCode.ToolFailure, started.Error ?? "process.start.failed");
            if (started.State != "running")
                return new ToolResult("{\"state\":\"" + started.State + "\"}", null, null, 0, true,
                    started.State == "unknown" ? EffectOutcome.Unknown : EffectOutcome.None, true,
                    started.State == "unknown" ? ToolErrorCode.UnknownEffect : ToolErrorCode.ToolFailure);

            string state;
            var cancelled = false;
            try { state = await _manager.WaitForTerminalAsync(started.Handle, context.MutationRunId,
                context.MutationLaneId, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                cancelled = true;
                state = await _manager.StopAsync(started.Handle, context.MutationRunId, context.MutationLaneId,
                    context.EmitEvent, CancellationToken.None).ConfigureAwait(false);
                if (state == "unknown") return new ToolResult("{\"state\":\"unknown\"}", null, null, 0,
                    true, EffectOutcome.Unknown, true, ToolErrorCode.UnknownEffect);
            }
            if (state is "unknown" or "failed" or "timed_out" or "output_limit")
                return new ToolResult("{\"state\":\"" + state + "\"}", null, null, 0, true,
                    EffectOutcome.Unknown, true, ToolErrorCode.UnknownEffect);

            var output = _manager.Read(started.Handle, context.MutationRunId, context.MutationLaneId, 0, 16_384,
                out var deliveredThrough, out var highWater, out var gap, out _, out var exitCode);
            var text = new StringBuilder();
            foreach (var chunk in output)
            {
                var piece = $"[{chunk.Stream}] {chunk.Text}";
                if (text.Length + piece.Length > MaximumPreviewChars)
                {
                    text.Append(piece.AsSpan(0, Math.Max(0, MaximumPreviewChars - text.Length)));
                    gap = true;
                    break;
                }
                text.Append(piece);
            }
            if (gap) text.Append("\n[process output truncated; reconnect with process.read]");

            ArtifactRef? artifact = null;
            string? proposalPreview = null;
            if (writer)
                (artifact, proposalPreview) = _manager.CreatePrivateProcessProposal(started.Handle,
                    context.MutationRunId, context.MutationLaneId, intent.Intent.ToolCallId,
                    context.MutationTurnId, context.WorkspaceRoot, context.Artifacts);
            var preview = ProcessSessionToolJson.ReadPreview(state, exitCode, 0,
                deliveredThrough, highWater, gap, output, proposalPreview);
            if (artifact is not null) preview = ProcessSessionToolJson.WithPublicationState(preview, "staged");
            var summary = BuildSummary(state, exitCode, text.ToString(), artifact is not null);
            var success = !cancelled && exitCode == 0;
            return new ToolResult(summary, preview, artifact, artifact is null ? 0 : 1,
                artifact is not null, success ? EffectOutcome.Applied : EffectOutcome.Partial,
                !success, success ? null : ToolErrorCode.ToolFailure);
        }
        catch (ToolPreflightException ex)
        { return ToolResult.Error(ex.ErrorCode, ex.Message); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
            or KeyNotFoundException or ArgumentException)
        { return ToolResult.Error(ToolErrorCode.ToolFailure, Descriptor.Id + "." + ex.GetType().Name); }
        finally { if (!managerOwnsStaging) PersistentProcessSessionManager.DisposeStaging(staging); }
    }

    private static string ToStagedPath(string path, string workspace, string stage)
    {
        var relative = Path.GetRelativePath(workspace, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Process path escaped its private workspace.");
        return Path.GetFullPath(relative, stage);
    }

    private static int ReadTimeout(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("timeoutSeconds").GetInt32();
    }

    private static string BuildSummary(string state, int? exitCode, string output, bool publication)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("state", state);
            if (exitCode is { } code) writer.WriteNumber("exitCode", code); else writer.WriteNull("exitCode");
            writer.WriteString("output", output); writer.WriteBoolean("publication", publication);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? ResolveCwd(string workspace, string cwd)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(cwd) ? cwd : Path.Combine(workspace, cwd)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static string[] ReadPatterns(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Process paths must be arrays.");
        var paths = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .ToArray();
        if (paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 512))
            throw new InvalidDataException("Process paths are invalid.");
        return paths!;
    }

    private static PreparationRejected Rejected(string message) =>
        new(message, null, ToolErrorCode.InvalidArguments);
}
