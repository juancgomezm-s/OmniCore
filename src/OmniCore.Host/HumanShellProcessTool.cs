namespace OmniCore.Host;

using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Sandbox;
using OmniCore.Tools;
using OmniCore.Protocol;

/// <summary>
/// The signed native !command route uses the same shell.exec intent, ToolRuntime, permission
/// policy and sandbox launcher as an agent shell call, but keeps a separate process owner so an
/// agent cannot read, send input to, or stop a human terminal from the shared root Lane.
/// </summary>
internal sealed class HumanShellProcessTool : ITool, INativeUserActionOnlyTool,
    IWeakSandboxConsentPreflightTool
{
    internal const string ProcessOwnerSource = "human.shell";
    private const int MaximumOutputBytes = 32 * 1_048_576;
    private readonly PersistentProcessSessionManager _manager;
    private readonly ISandboxProcessLauncher _launcher;
    private readonly SandboxStrength _requestedStrength;
    private readonly IPathBoundaryValidator _paths;
    private readonly Action<string>? _writeOutput;
    private readonly ToolDescriptor _descriptor = new(new ToolId("shell.exec"),
        "Runs a human-submitted raw shell command in the current workspace. This high-risk surface requires the Session policy.",
        new InputSchema("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"},\"cwd\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\"}},\"required\":[\"command\",\"cwd\",\"timeoutSeconds\"],\"additionalProperties\":false}"),
        new[] { "process", "execution" }, readOnly: false, destructive: false,
        risk: ToolRisk.High, source: ComponentSource.Core(), protection: ToolProtection.Protected,
        effectClass: EffectClass.NonIdempotent);

    internal HumanShellProcessTool(PersistentProcessSessionManager manager, ISandboxProcessLauncher launcher,
        SandboxStrength requestedStrength, IPathBoundaryValidator paths, Action<string>? writeOutput)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _requestedStrength = requestedStrength == SandboxStrength.Weak ? SandboxStrength.Weak : SandboxStrength.Strong;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _writeOutput = writeOutput;
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!TryParse(call.NormalizedArgumentsJson, context.WorkspaceRoot, out var command, out var cwd,
                out var timeout, out var error))
            return new PreparationRejected(error, null, ToolErrorCode.InvalidArguments);
        var (executable, arguments) = ShellInvocation(command);
        // Raw shell syntax can read and write any path below the workspace, independent of
        // the command's initial cwd. The tool claim and OS sandbox therefore use the same root.
        var workspace = Path.GetFullPath(context.WorkspaceRoot);
        var claims = new ResourceClaims(new[] { workspace }, new[] { workspace }, Array.Empty<NetworkGrant>(),
            new ProcessClaim(executable, arguments, "External", NetworkRequired: false, WorkingDirectory: cwd),
            Array.Empty<string>());
        return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.NonIdempotent, claims, ToolRisk.Critical, null));
    }

    public WeakSandboxConsentPreflight? CheckWeakSandboxConsentPreflight(ToolIntent intent,
        ToolPreparationContext preparation, CancellationToken cancellationToken)
    {
        if (_requestedStrength != SandboxStrength.Strong
            || intent.Claims.Process is not { } process
            || _launcher is not IStrongSandboxPreflight preflight) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var launch = CreateLaunchSpec(SandboxStrength.Strong, process.Executable, process.Args,
            process.WorkingDirectory ?? preparation.WorkspaceRoot, preparation.WorkspaceRoot, TimeSpan.FromSeconds(1));
        return preflight.CheckStrongAvailability(launch) == StrongSandboxPreflightResult.UnavailableBeforeLaunch
            ? new WeakSandboxConsentPreflight(process.Executable, process.WorkingDirectory ?? preparation.WorkspaceRoot,
                "sandbox.strong_unavailable") : null;
    }

    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent authorized,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.IsNativeUserAction)
            return ToolResult.Error(ToolErrorCode.CapabilityRefused, "shell.human_action_required");
        if (!TryParse(authorized.Intent.NormalizedArgumentsJson, context.WorkspaceRoot, out var command,
                out var cwd, out var timeout, out var error))
            return ToolResult.Error(ToolErrorCode.InvalidArguments, error);
        if (!Directory.Exists(cwd) || !_paths.IsWithin(cwd, context.WorkspaceRoot))
            return ToolResult.Error(ToolErrorCode.CapabilityRefused, "shell.cwd_outside_workspace");

        if (authorized.Intent.Claims.Process is not { } processClaim
            || string.IsNullOrWhiteSpace(processClaim.WorkingDirectory)
            || !string.Equals(Path.GetFullPath(processClaim.WorkingDirectory ?? ""), cwd,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return ToolResult.Error(ToolErrorCode.CapabilityRefused, "shell.process_claim_mismatch");
        var (expectedShell, expectedArguments) = ShellInvocation(command);
        if (!string.Equals(Path.GetFullPath(processClaim.Executable), Path.GetFullPath(expectedShell),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !processClaim.Args.SequenceEqual(expectedArguments, StringComparer.Ordinal))
            return ToolResult.Error(ToolErrorCode.CapabilityRefused, "shell.process_claim_mismatch");
        var effectiveStrength = context.WeakSandboxConsentForThisCall && !context.AutomaticSandboxConsentForThisCall
            ? SandboxStrength.Weak : _requestedStrength;
        var launch = CreateLaunchSpec(effectiveStrength, processClaim.Executable, processClaim.Args, cwd,
            context.WorkspaceRoot, TimeSpan.FromSeconds(timeout));
        if (effectiveStrength == SandboxStrength.Strong && context.AutomaticSandboxConsentForThisCall
            && _launcher is IStrongSandboxPreflight preflight
            && preflight.CheckStrongAvailability(launch) == StrongSandboxPreflightResult.UnavailableBeforeLaunch)
            launch = launch with { RequestedStrength = SandboxStrength.Weak };
        var started = await _manager.StartAsync(context.MutationRunId, context.MutationLaneId,
            launch, _launcher, context.EmitEvent, cancellationToken, workspaceRoot: context.WorkspaceRoot,
            sourceCall: authorized.Intent.ToolCallId, sourceTool: new ToolId(ProcessOwnerSource),
            turn: context.MutationTurnId, sourceProcessClaim: authorized.Intent.Claims.Process)
            .ConfigureAwait(false);
        if (started.Handle is null)
            return ToolResult.Error(ToolErrorCode.ProcessFailure, started.Error ?? "shell.start_failed");
        if (started.State != "running")
            return new ToolResult("Shell execution did not start.",
                Preview(started.State, null, 0, 0, gap: false), null, 0, false,
                started.State == "unknown" ? EffectOutcome.Unknown : EffectOutcome.None, true,
                started.State == "unknown" ? ToolErrorCode.UnknownEffect : ToolErrorCode.ProcessFailure);

        try
        {
        var cancelled = false;
        var outputByteCount = 0;
        var output = new StringBuilder();
        var cursor = 0L;
        var state = started.State;
        var gap = false;
        var readToken = cancellationToken;
        long highWater = 0;
        int? exitCode = null;
        while (state is ("starting" or "running" or "stopping") || cursor < highWater)
        {
            (IReadOnlyList<ProcessOutputChunk> Output, long Last, long HighWater, bool Gap,
                string State, int? ExitCode) read;
            try
            {
                read = await _manager.ReadAsync(started.Handle, context.MutationRunId,
                    context.MutationLaneId, cursor, maximumChunks: 32, wait: TimeSpan.FromMilliseconds(250),
                    readToken, humanShell: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                state = await _manager.StopAsync(started.Handle, context.MutationRunId,
                    context.MutationLaneId, context.EmitEvent, CancellationToken.None).ConfigureAwait(false);
                readToken = CancellationToken.None;
                read = await _manager.ReadAsync(started.Handle, context.MutationRunId,
                    context.MutationLaneId, cursor, maximumChunks: 32, wait: TimeSpan.FromSeconds(1),
                    CancellationToken.None, humanShell: true).ConfigureAwait(false);
            }
            gap |= read.Gap;
            state = read.State;
            exitCode = read.ExitCode;
            highWater = read.HighWater;
            foreach (var chunk in read.Output)
            {
                var bytes = Encoding.UTF8.GetByteCount(chunk.Text);
                if (bytes > MaximumOutputBytes - outputByteCount)
                { gap = true; break; }
                outputByteCount += bytes;
                output.Append(chunk.Text);
                cursor = chunk.Sequence;
                _writeOutput?.Invoke(chunk.Text);
            }
            // The next read observes cancellation inside the StopAsync catch above. Do not
            // throw here, outside that ownership-cleanup path.
            if (state is not ("starting" or "running" or "stopping") && cursor >= highWater) break;
            if (gap && cursor < highWater) break;
        }

        if (state is ("starting" or "running" or "stopping"))
        {
            // Output caps/gaps and other early exits must not leave the owned shell running.
            // Stopping does not make a partial capture a completed effect.
            gap = true;
            state = await _manager.StopAsync(started.Handle, context.MutationRunId,
                context.MutationLaneId, context.EmitEvent, CancellationToken.None).ConfigureAwait(false);
            var finalRead = await _manager.ReadAsync(started.Handle, context.MutationRunId,
                context.MutationLaneId, cursor, maximumChunks: 32, wait: TimeSpan.Zero,
                CancellationToken.None, humanShell: true).ConfigureAwait(false);
            gap |= finalRead.Gap || finalRead.HighWater > cursor;
            highWater = finalRead.HighWater;
            exitCode = finalRead.ExitCode;
            state = finalRead.State;
        }

        var text = output.ToString();
        ArtifactRef? outputArtifact = null;
        if (text.Length > 0 && context.Artifacts is { } artifacts)
            outputArtifact = artifacts.PutText(text, "text/plain; charset=utf-8", ArtifactKind.ProcessOutput,
                Sensitivity.Sensitive);
        var preview = DisplayPreview(state, exitCode, cursor, highWater, gap, text);
        _writeOutput?.Invoke($"\n[exit code {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; state {state}{(gap ? "; output gap" : "")}]\n");
        var summary = "{" + JsonObj.Field("state", state)
            + ",\"exitCode\":" + (exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")
            + ",\"cancelled\":" + (cancelled ? "true" : "false")
            + ",\"lastSequence\":" + cursor.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"highWater\":" + highWater.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (outputArtifact is null ? "" : ",\"outputArtifact\":"
                + JsonSerializer.Serialize(outputArtifact, AttachmentReferenceJsonContext.Default.ArtifactRef))
            + "}";
        if (state is "unknown" or "failed" or "timed_out" or "output_limit"
            or "starting" or "running" or "stopping" || cancelled || gap || cursor < highWater)
            return new ToolResult(summary, preview, outputArtifact, text.Length, outputArtifact is not null,
                EffectOutcome.Unknown, true, cancelled ? ToolErrorCode.Cancellation : ToolErrorCode.UnknownEffect);
        // Nonzero is a real, completed shell result. Preserve it as a completed ToolRuntime effect;
        // the exit code remains explicit in the durable result and is not converted to model text.
        return new ToolResult(summary, preview, outputArtifact, text.Length, outputArtifact is not null,
            EffectOutcome.Applied, false, null);
        }
        catch (Exception executionFailure) when (executionFailure is not OutOfMemoryException
            and not StackOverflowException)
        {
            try
            {
                await _manager.StopAsync(started.Handle, context.MutationRunId,
                    context.MutationLaneId, context.EmitEvent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException
                and not StackOverflowException)
            {
                throw new AggregateException("Human shell execution and owned-process cleanup both failed.",
                    executionFailure, cleanupFailure);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(executionFailure).Throw();
            throw;
        }
    }

    private SandboxLaunchSpec CreateLaunchSpec(SandboxStrength strength, string executable,
        IReadOnlyList<string> arguments, string cwd, string workspaceRoot, TimeSpan timeout) =>
        new(strength, executable, arguments, cwd, new Dictionary<string, string>(),
            [new SandboxAllowedPath(Path.GetFullPath(workspaceRoot), SandboxPathAccess.Read | SandboxPathAccess.Write)],
            new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()), timeout)
        { Duplex = true, WindowsShellCommandLine = OperatingSystem.IsWindows() };

    private static bool TryParse(string json, string workspaceRoot, out string command, out string cwd,
        out int timeout, out string error)
    {
        command = cwd = "";
        timeout = 0;
        error = "shell.invalid_request";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || !root.TryGetProperty("command", out var commandNode) || commandNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("cwd", out var cwdNode) || cwdNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("timeoutSeconds", out var timeoutNode)
                || timeoutNode.ValueKind != JsonValueKind.Number || !timeoutNode.TryGetInt32(out timeout)) return false;
            command = commandNode.GetString() ?? "";
            var requestedCwd = cwdNode.GetString() ?? "";
            if (command.Length is < 1 or > 16_384 || command.Any(char.IsControl)
                || requestedCwd.Length is < 1 or > 4_096 || requestedCwd.Any(char.IsControl)
                || timeout is < 1 or > 3_600) return false;
            cwd = Path.GetFullPath(Path.IsPathRooted(requestedCwd)
                ? requestedCwd : Path.Combine(workspaceRoot, requestedCwd));
            if (!IsLexicallyWithin(cwd, workspaceRoot))
            { error = "shell.cwd_outside_workspace"; return false; }
            error = "";
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    private static (string Executable, IReadOnlyList<string> Arguments) ShellInvocation(string command) =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                new[] { "/d", "/s", "/c", command })
            : ("/bin/sh", new[] { "-c", command });

    private static bool IsLexicallyWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.Equals(fullRoot, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
            || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static string Preview(string state, int? exitCode, long last, long highWater, bool gap) =>
        "{" + JsonObj.Field("state", state) + "," + JsonObj.FieldRaw("exitCode",
            exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")
        + ",\"lastSequence\":" + last.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"highWater\":" + highWater.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"gap\":" + (gap ? "true" : "false") + "}";

    // The existing displayOutput wire field is human-facing, bounded and redacted by
    // ProtocolMapper. Keep the complete output in CAS, never in the model transcript.
    private static string DisplayPreview(string state, int? exitCode, long last, long highWater,
        bool gap, string text)
    {
        var length = Math.Min(text.Length, 8192);
        string encoded;
        while (true)
        {
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            encoded = JsonObj.Field("output", text[..length]);
            if (encoded.Length <= 12000) break;
            length /= 2;
        }
        var metadata = Preview(state, exitCode, last, highWater, gap);
        return metadata[..^1] + "," + encoded + ",\"outputTruncated\":"
            + (length < text.Length ? "true" : "false") + "}";
    }
}
