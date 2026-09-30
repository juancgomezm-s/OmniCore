namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>process.exec: argv literal, nunca interpretado por un shell (ADR-0015).</summary>
public sealed class ProcessExecTool : ITool
{
    private readonly ToolDescriptor _descriptor;
    private readonly IProcessRuntime _processes;
    private readonly IPathBoundaryValidator _boundary;

    public ProcessExecTool(IProcessRuntime processes, IPathBoundaryValidator boundary)
    {
        _processes = processes;
        _boundary = boundary;
        _descriptor = ProcessToolJson.Descriptor("process.exec",
            "Ejecuta un proceso con argv literal; no usa shell. El cwd debe estar dentro del workspace.",
            "{\"type\":\"object\",\"properties\":{\"executable\":{\"type\":\"string\"},\"argv\":{\"type\":\"array\"},\"cwd\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\"},\"networkRequired\":{\"type\":\"boolean\"},\"sandboxLevel\":{\"type\":\"string\"}},\"required\":[\"executable\",\"argv\",\"cwd\",\"timeoutSeconds\"],\"additionalProperties\":false}");
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!ProcessToolJson.TryParse(call.NormalizedArgumentsJson, out var args, out var error))
            return new PreparationRejected(error, null);
        var cwd = ProcessToolJson.ResolveCwd(context.WorkspaceRoot, args.Cwd);
        if (cwd is null || !ProcessToolJson.IsLexicallyWithin(cwd, context.WorkspaceRoot))
            return new PreparationRejected("cwd debe permanecer dentro del workspace", null);
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
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("cwd fuera del workspace o no existe"));
        var sandbox = ProcessToolJson.GetSandboxLevel(intent.Intent.NormalizedArgumentsJson);
        if (!ProcessToolJson.EnsureSandboxConsent(context, sandbox, process.Executable))
            throw new WeakSandboxConsentRequiredException();
        return System.Threading.Tasks.Task.FromResult(ProcessToolJson.Run(_processes, process.Executable, process.Args, cwd,
            timeout, cancellationToken));
    }
}

/// <summary>shell.exec: superficie de mayor riesgo. El comando raw nunca se analiza para autorizarlo.</summary>
public sealed class ShellExecTool : ITool
{
    private readonly ToolDescriptor _descriptor;
    private readonly IProcessRuntime _processes;
    private readonly IPathBoundaryValidator _boundary;

    public ShellExecTool(IProcessRuntime processes, IPathBoundaryValidator boundary)
    {
        _processes = processes;
        _boundary = boundary;
        _descriptor = ProcessToolJson.Descriptor("shell.exec",
            "Ejecuta un comando raw mediante el shell de plataforma. Superficie de alto riesgo; requiere autorización.",
            "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"},\"cwd\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\"},\"sandboxLevel\":{\"type\":\"string\"}},\"required\":[\"command\",\"cwd\",\"timeoutSeconds\"],\"additionalProperties\":false}");
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!ProcessToolJson.TryParseShell(call.NormalizedArgumentsJson, out var command, out var requestedCwd,
            out var timeout, out var error)) return new PreparationRejected(error, null);
        var cwd = ProcessToolJson.ResolveCwd(context.WorkspaceRoot, requestedCwd);
        if (cwd is null || !ProcessToolJson.IsLexicallyWithin(cwd, context.WorkspaceRoot))
            return new PreparationRejected("cwd debe permanecer dentro del workspace", null);
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
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(error));
        var cwd = ProcessToolJson.ResolveCwd(context.WorkspaceRoot, requestedCwd);
        if (cwd is null || !_boundary.IsWithin(cwd, context.WorkspaceRoot) || !Directory.Exists(cwd))
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error("cwd fuera del workspace o no existe"));
        var (_, args) = ProcessToolJson.ShellInvocation(command);
        var sandbox = ProcessToolJson.GetSandboxLevel(intent.Intent.NormalizedArgumentsJson);
        if (!ProcessToolJson.EnsureSandboxConsent(context, sandbox, intent.Intent.Claims.Process!.Executable))
            throw new WeakSandboxConsentRequiredException();
        return System.Threading.Tasks.Task.FromResult(ProcessToolJson.Run(_processes, intent.Intent.Claims.Process!.Executable,
            args, cwd, TimeSpan.FromSeconds(timeout), cancellationToken));
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

    internal static string GetSandboxLevel(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("sandboxLevel", out var value)) return "Strong";
            var level = value.GetString();
            return level is "Strong" or "Weak" or "None" ? level : "Strong"; // valores ambiguos, fail closed
        }
        catch (JsonException) { return "Strong"; }
    }

    internal static bool EnsureSandboxConsent(ToolExecutionContext context, string requestedLevel,
        string executable)
    {
        // Strong no está disponible en este milestone. Nunca se degrada silenciosamente.
        if (requestedLevel != "Strong") return true;
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
                ["requestedLevel"] = "Strong", ["effectiveLevel"] = "Weak", ["lifetime"] = option == "consent_run" ? "Run" : "Once",
            }), CancellationToken.None);
        return true;
    }

    internal static ToolResult Run(IProcessRuntime runtime, string executable, IReadOnlyList<string> args,
        string cwd, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ProcessHandle? handle = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            handle = runtime.Launch(new ProcessLaunch(executable, args, cwd,
                new Dictionary<string, string>(), captureOutput: true), cancellationToken);
            var result = runtime.Wait(handle, timeout, cancellationToken);
            var stdout = result.Stdout ?? string.Empty;
            var stderr = result.Stderr ?? string.Empty;
            var combined = stdout.Length == 0 ? stderr : stderr.Length == 0 ? stdout : stdout + "\n" + stderr;
            var truncated = combined.Length > 16_384;
            if (truncated) combined = combined[..16_384] + "\n[output capped]";
            combined = new RedactionPolicy().Redact(combined);
            var redactor = SecretRedactorRegistry.Current;
            if (redactor is not null) combined = redactor.Redact(combined);
            var originalSize = (result.Stdout?.Length ?? 0) + (result.Stderr?.Length ?? 0);
            if (result.TimedOut)
                return new ToolResult("Proceso agotó timeout o fue cancelado. " + combined, combined, null,
                    originalSize, false, EffectOutcome.Unknown, true);
            return new ToolResult("Proceso terminó con código " + result.ExitCode
                + (truncated ? " (salida truncada)" : ""), combined, null, originalSize, false,
                result.ExitCode == 0 ? EffectOutcome.Applied : EffectOutcome.Partial,
                result.ExitCode != 0);
        }
        catch (ExecutableNotFoundException ex) { return ToolResult.Error(ex.Message); }
        catch (OperationCanceledException)
        {
            if (handle is not null) runtime.CancelTree(handle);
            return handle is null ? ToolResult.Error("Proceso cancelado")
                : new ToolResult("Proceso cancelado; el efecto puede ser parcial", null, null, 0, false,
                    EffectOutcome.Unknown, true);
        }
        catch (Exception ex)
        {
            if (handle is not null) runtime.CancelTree(handle);
            return handle is null ? ToolResult.Error("No se pudo iniciar el proceso: " + ex.GetType().Name)
                : new ToolResult("Falló la espera del proceso; el efecto puede ser parcial: " + ex.GetType().Name,
                    null, null, 0, false, EffectOutcome.Unknown, true);
        }
    }
}
