namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Sandbox;

/// <summary>process.exec: argv literal, nunca interpretado por un shell (ADR-0015).</summary>
public sealed class ProcessExecTool : ITool
{
    private readonly ToolDescriptor _descriptor;
    private readonly ISandboxProcessLauncher _processLauncher;
    private readonly IPathBoundaryValidator _boundary;
    private readonly SandboxStrength _requestedStrength;

    public ProcessExecTool(ISandboxProcessLauncher processLauncher, IPathBoundaryValidator boundary,
        SandboxStrength requestedStrength = SandboxStrength.Strong)
    {
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _boundary = boundary;
        _requestedStrength = requestedStrength == SandboxStrength.Weak
            ? SandboxStrength.Weak : SandboxStrength.Strong;
        _descriptor = ProcessToolJson.Descriptor("process.exec",
            "Runs a process with literal argv without using a shell. The cwd must be within the workspace.",
            "{\"type\":\"object\",\"properties\":{\"executable\":{\"type\":\"string\"},\"argv\":{\"type\":\"array\"},\"cwd\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\"},\"networkRequired\":{\"type\":\"boolean\"}},\"required\":[\"executable\",\"argv\",\"cwd\",\"timeoutSeconds\"],\"additionalProperties\":false}");
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!ProcessToolJson.TryParse(call.NormalizedArgumentsJson, out var args, out var error))
            return new PreparationRejected(error, null, ToolErrorCode.InvalidArguments);
        var cwd = ProcessToolJson.ResolveCwd(context.WorkspaceRoot, args.Cwd);
        if (cwd is null || !ProcessToolJson.IsLexicallyWithin(cwd, context.WorkspaceRoot))
            return new PreparationRejected("cwd debe permanecer dentro del workspace", null,
                ToolErrorCode.InvalidArguments);
        var claims = new ResourceClaims(Array.Empty<string>(), new[] { cwd },
            Array.Empty<NetworkGrant>(),
            new ProcessClaim(args.Executable, args.Argv, "External", args.NetworkRequired), Array.Empty<string>());
        return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.NonIdempotent, claims, ToolRisk.High, null));
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var process = intent.Intent.Claims.Process!;
        var (cwd, timeout) = ProcessToolJson.GetRuntimeValues(intent.Intent.NormalizedArgumentsJson,
            context.WorkspaceRoot);
        if (cwd is null || !_boundary.IsWithin(cwd, context.WorkspaceRoot) || !Directory.Exists(cwd))
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments,
                "cwd fuera del workspace o no existe"));
        return ProcessToolJson.RunAsync(_processLauncher, process.Executable, process.Args, cwd,
            context.WorkspaceRoot, timeout, process.NetworkRequired,
            _requestedStrength, context, cancellationToken);
    }
}

/// <summary>shell.exec: superficie de mayor riesgo. El comando raw nunca se analiza para autorizarlo.</summary>
public sealed class ShellExecTool : ITool
{
    private readonly ToolDescriptor _descriptor;
    private readonly ISandboxProcessLauncher _processLauncher;
    private readonly IPathBoundaryValidator _boundary;
    private readonly SandboxStrength _requestedStrength;

    public ShellExecTool(ISandboxProcessLauncher processLauncher, IPathBoundaryValidator boundary,
        SandboxStrength requestedStrength = SandboxStrength.Strong)
    {
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _boundary = boundary;
        _requestedStrength = requestedStrength == SandboxStrength.Weak
            ? SandboxStrength.Weak : SandboxStrength.Strong;
        _descriptor = ProcessToolJson.Descriptor("shell.exec",
            "Runs a raw command through the platform shell. This is a high-risk surface and requires authorization.",
            "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"},\"cwd\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\"}},\"required\":[\"command\",\"cwd\",\"timeoutSeconds\"],\"additionalProperties\":false}");
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!ProcessToolJson.TryParseShell(call.NormalizedArgumentsJson, out var command, out var requestedCwd,
            out var timeout, out var error)) return new PreparationRejected(error, null, ToolErrorCode.InvalidArguments);
        var cwd = ProcessToolJson.ResolveCwd(context.WorkspaceRoot, requestedCwd);
        if (cwd is null || !ProcessToolJson.IsLexicallyWithin(cwd, context.WorkspaceRoot))
            return new PreparationRejected("cwd debe permanecer dentro del workspace", null,
                ToolErrorCode.InvalidArguments);
        var (shell, shellArgs) = ProcessToolJson.ShellInvocation(command);
        // shell.exec queda autorizado como tool distinta y command viaja raw en argv[1], sin parseo.
        var claims = new ResourceClaims(Array.Empty<string>(), new[] { cwd }, Array.Empty<NetworkGrant>(),
            new ProcessClaim(shell, shellArgs, "External"), Array.Empty<string>());
        return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.NonIdempotent, claims, ToolRisk.Critical, null));
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!ProcessToolJson.TryParseShell(intent.Intent.NormalizedArgumentsJson, out var command,
            out var requestedCwd, out var timeout, out var error))
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments, error));
        var cwd = ProcessToolJson.ResolveCwd(context.WorkspaceRoot, requestedCwd);
        if (cwd is null || !_boundary.IsWithin(cwd, context.WorkspaceRoot) || !Directory.Exists(cwd))
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments,
                "cwd fuera del workspace o no existe"));
        var (_, args) = ProcessToolJson.ShellInvocation(command);
        return ProcessToolJson.RunAsync(_processLauncher, intent.Intent.Claims.Process!.Executable,
            args, cwd, context.WorkspaceRoot, TimeSpan.FromSeconds(timeout), networkRequired: false,
            _requestedStrength, context, cancellationToken);
    }
}

internal static class ProcessToolJson
{
    internal sealed record ProcessArgs(string Executable, IReadOnlyList<string> Argv, string Cwd,
        int TimeoutSeconds, bool NetworkRequired);

    internal static ToolDescriptor Descriptor(string id, string description, string schema) => new(
        new ToolId(id), description, new InputSchema(schema), new[] { "process", "execution" },
        readOnly: false, destructive: false, risk: ToolRisk.High, source: ComponentSource.Core(),
        protection: ToolProtection.Protected, effectClass: EffectClass.NonIdempotent);

    internal static bool TryParse(string json, out ProcessArgs args, out string error)
    {
        args = new ProcessArgs("", Array.Empty<string>(), "", 0, false);
        error = "Argumentos process.exec inválidos";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var executable = root.GetProperty("executable").GetString();
            var cwd = root.GetProperty("cwd").GetString();
            var timeout = root.GetProperty("timeoutSeconds").GetInt32();
            if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(cwd)
                || timeout is < 1 or > 86400 || !root.TryGetProperty("argv", out var argvElement)
                || argvElement.ValueKind != JsonValueKind.Array)
                return false;
            var argv = new List<string>();
            foreach (var item in argvElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return false;
                argv.Add(item.GetString() ?? string.Empty);
            }
            var network = root.TryGetProperty("networkRequired", out var net) && net.ValueKind == JsonValueKind.True;
            args = new ProcessArgs(executable, argv, cwd, timeout, network);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException)
        { return false; }
    }

    internal static bool TryParseShell(string json, out string command, out string cwd, out int timeout,
        out string error)
    {
        command = cwd = "";
        timeout = 0;
        error = "Argumentos shell.exec inválidos";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            command = root.GetProperty("command").GetString() ?? "";
            cwd = root.GetProperty("cwd").GetString() ?? "";
            timeout = root.GetProperty("timeoutSeconds").GetInt32();
            if (command.Length == 0 || cwd.Length == 0 || timeout is < 1 or > 86400) return false;
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException) { return false; }
    }

    internal static string? ResolveCwd(string workspaceRoot, string cwd)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(cwd) ? cwd : Path.Combine(workspaceRoot, cwd)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    internal static (string Executable, IReadOnlyList<string> Args) ShellInvocation(string command) =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), new[] { "/c", command })
            : ("/bin/sh", new[] { "-c", command });

    internal static bool IsLexicallyWithin(string path, string root)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
            return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    internal static (string? Cwd, TimeSpan Timeout) GetRuntimeValues(string json, string workspaceRoot)
    {
        if (!TryParse(json, out var parsed, out _)) return (null, TimeSpan.Zero);
        return (ResolveCwd(workspaceRoot, parsed.Cwd), TimeSpan.FromSeconds(parsed.TimeoutSeconds));
    }

    internal static bool EnsureSandboxConsent(ToolExecutionContext context, string executable)
    {
        if (context.WeakSandboxConsent?.GrantedForRun == true) return true;

        var id = InteractionId.New();
        var request = new InteractionRequested(id, InteractionKind.WeakSandboxConsent,
            "{\"operation\":\"Ejecutar proceso sin confinamiento fuerte\",\"toolOrExecutable\":\""
                + executable.Replace("\\", "\\\\").Replace("\"", "\\\"")
                + "\",\"risk\":\"high\",\"reason\":\"sandbox.strong_unavailable\"}",
            "[{\"id\":\"consent_once\",\"intent\":\"allow\",\"lifetime\":\"once\"},{\"id\":\"consent_run\",\"intent\":\"allow\",\"lifetime\":\"run\"},{\"id\":\"deny\",\"intent\":\"deny\"}]",
            "deny", null, null, null, null, 0, 1);
        context.EmitEvent?.Invoke(request);
        var option = context.IsInteractive ? context.ResolveInteraction?.Invoke(request) : null;
        var approved = option is "consent_once" or "consent_run";
        context.EmitEvent?.Invoke(new InteractionResolved(id, approved ? option! : "deny",
            context.IsInteractive ? InteractionCause.User : InteractionCause.NoClient));
        if (!approved) return false;

        if (option == "consent_run") context.WeakSandboxConsent?.GrantForRun();
        context.Audit?.Record(new AuditRecord("WeakSandboxConsentGranted", null, null, null,
            DateTimeOffset.UtcNow, null, new Dictionary<string, string>
            {
                ["requestedLevel"] = "Strong", ["effectiveLevel"] = "Weak",
                ["lifetime"] = option == "consent_run" ? "Run" : "Once",
            }), CancellationToken.None);
        return true;
    }

    internal static async Task<ToolResult> RunAsync(ISandboxProcessLauncher launcher, string executable,
        IReadOnlyList<string> args, string cwd, string workspaceRoot, TimeSpan timeout, bool networkRequired,
        SandboxStrength requestedStrength, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var network = new SandboxNetworkPolicy(networkRequired ? SandboxNetworkMode.AllowAll
            : SandboxNetworkMode.Deny, Array.Empty<string>());
        var launch = new SandboxLaunchSpec(requestedStrength, executable, args, cwd,
            new Dictionary<string, string>(), new[]
            {
                new SandboxAllowedPath(Path.GetFullPath(workspaceRoot), SandboxPathAccess.Read | SandboxPathAccess.Write),
            }, network, timeout);
        ISandboxProcessControl? process = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                process = await launcher.StartAsync(launch, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException) when (requestedStrength == SandboxStrength.Strong)
            {
                if (!EnsureSandboxConsent(context, executable))
                    throw new WeakSandboxConsentRequiredException();
                launch = launch with { RequestedStrength = SandboxStrength.Weak };
                process = await launcher.StartAsync(launch, cancellationToken).ConfigureAwait(false);
            }

            var output = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = string.Concat(output.Chunks.Where(chunk => chunk.Stream == SandboxOutputStream.StandardOutput)
                .Select(chunk => chunk.Text));
            var stderr = string.Concat(output.Chunks.Where(chunk => chunk.Stream == SandboxOutputStream.StandardError)
                .Select(chunk => chunk.Text));
            var combined = stdout.Length == 0 ? stderr : stderr.Length == 0 ? stdout : stdout + "\n" + stderr;
            var truncated = combined.Length > 16_384;
            if (truncated) combined = combined[..16_384] + "\n[output capped]";
            combined = new RedactionPolicy().Redact(combined);
            var redactor = SecretRedactorRegistry.Current;
            if (redactor is not null) combined = redactor.Redact(combined);
            var originalSize = stdout.Length + stderr.Length;
            if (output.TimedOut)
                return new ToolResult("Proceso agotó timeout o fue cancelado. " + combined, combined, null,
                    originalSize, false, EffectOutcome.Unknown, true, ToolErrorCode.ProcessFailure);
            return new ToolResult("Proceso terminó con código " + output.ExitCode
                + (truncated ? " (salida truncada)" : ""), combined, null, originalSize, false,
                output.ExitCode == 0 ? EffectOutcome.Applied : EffectOutcome.Partial, output.ExitCode != 0,
                output.ExitCode == 0 ? null : ToolErrorCode.ProcessFailure);
        }
        catch (WeakSandboxConsentRequiredException) { throw; }
        catch (ExecutableNotFoundException ex) { return ToolResult.Error(ToolErrorCode.ProcessFailure, ex.Message); }
        catch (ExecutableRequiresShellException ex) { return ToolResult.Error(ToolErrorCode.InvalidArguments, ex.Message); }
        catch (OperationCanceledException)
        {
            if (process is not null) await TerminateSafelyAsync(process).ConfigureAwait(false);
            return process is null ? ToolResult.Error(ToolErrorCode.Cancellation, "Proceso cancelado")
                : new ToolResult("Proceso cancelado; el efecto puede ser parcial", null, null, 0, false,
                    EffectOutcome.Unknown, true, ToolErrorCode.Cancellation);
        }
        catch (Exception ex)
        {
            if (process is not null) await TerminateSafelyAsync(process).ConfigureAwait(false);
            return process is null
                ? ToolResult.Error(ToolErrorCode.ProcessFailure, "No se pudo iniciar el proceso: " + ex.GetType().Name)
                : new ToolResult("Falló la espera del proceso; el efecto puede ser parcial: " + ex.GetType().Name,
                    null, null, 0, false, EffectOutcome.Unknown, true, ToolErrorCode.ProcessFailure);
        }
        finally
        {
            if (process is not null) await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask TerminateSafelyAsync(ISandboxProcessControl process)
    {
        try { await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
    }
}
