namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;

internal enum ClaudeLaneProcessFailure
{
    StreamingRuntimeUnavailable,
    TimedOut,
    NonZeroExit,
    MissingProtocolResult,
    InvalidProtocol,
    AgentReportedError,
    InvalidAgentResult,
}

/// <summary>Closed, non-sensitive categories derived from local stderr. Raw provider/runtime
/// diagnostics are deliberately never copied into Host messages or durable events.</summary>
internal enum ClaudeLaneProcessDiagnostic
{
    AuthenticationRequired,
    PermissionDenied,
    InvalidArguments,
    ConfigurationRejected,
    ProviderUnavailable,
    RateOrBudgetLimited,
    Other,
}

internal sealed record ClaudeLaneProcessOutcome(ClaudeLaneStreamOutcome? Stream,
    AgentResult? AgentResult, ClaudeLaneProcessFailure? Failure, int ExitCode,
    ClaudeLaneStreamErrorCode? ProtocolError = null,
    ClaudeLaneProcessDiagnostic? Diagnostic = null);

/// <summary>Runs a fully specified argv process through the Host process boundary, feeds stdout
/// directly into the bounded parser, and returns success only after both protocol EOF and exit 0.</summary>
internal sealed class ClaudeLaneProcessExecutor(IProcessRuntime runtime)
{
    internal async Task<ClaudeLaneProcessOutcome> ExecuteAsync(string executable, IReadOnlyList<string> args,
        string workingDirectory, IReadOnlyDictionary<string, string> environment, TimeSpan timeout,
        Func<ClaudeLaneSessionStarted, CancellationToken, ValueTask> onSessionStarted,
        CancellationToken cancellationToken, ClaudeLaneStreamLimits? streamLimits = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime is not IStreamingProcessRuntime streaming)
            return new(null, null, ClaudeLaneProcessFailure.StreamingRuntimeUnavailable, -1);
        var launch = ClaudeAccountService.WrapWindowsShim(new ProcessLaunch(executable, args, workingDirectory,
            environment, captureOutput: true));
        var handle = runtime.Launch(launch, cancellationToken);
        ClaudeLaneStreamOutcome? parsed = null;
        ClaudeLaneStreamException? deferredEofError = null;
        ProcessResult process;
        try
        {
            process = await streaming.WaitStreamingAsync(handle, timeout, async (stdout, token) =>
            {
                try
                {
                    parsed = await ClaudeLaneStreamReader.ReadAsync(stdout, onSessionStarted,
                        streamLimits, token).ConfigureAwait(false);
                }
                catch (ClaudeLaneStreamException exception) when (exception.Code == ClaudeLaneStreamErrorCode.InitRequired)
                {
                    // InitRequired is detected only after stdout reaches EOF. Keep the process
                    // result so diagnostics can be reduced to a closed enum; never retain stderr.
                    deferredEofError = exception;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ClaudeLaneStreamException exception)
        {
            // Surface only a closed protocol enum, never stream text or provider stderr.
            return new(null, null, ClaudeLaneProcessFailure.InvalidProtocol, -1, exception.Code);
        }
        if (process.TimedOut) return new(null, null, ClaudeLaneProcessFailure.TimedOut, process.ExitCode);
        if (process.ExitCode != 0) return new(null, null, ClaudeLaneProcessFailure.NonZeroExit,
            process.ExitCode, deferredEofError?.Code, ClassifyDiagnostic(process.Stderr));
        if (deferredEofError is not null)
            return new(null, null, ClaudeLaneProcessFailure.InvalidProtocol, process.ExitCode,
                deferredEofError.Code, ClassifyDiagnostic(process.Stderr));
        if (parsed is null) return new(null, null, ClaudeLaneProcessFailure.MissingProtocolResult, process.ExitCode);
        if (parsed.IsError) return new(parsed, null, ClaudeLaneProcessFailure.AgentReportedError, process.ExitCode);
        var validated = ClaudeLaneAgentResultValidator.Validate(parsed.StructuredOutputJson);
        return validated is null
            ? new(parsed, null, ClaudeLaneProcessFailure.InvalidAgentResult, process.ExitCode)
            : new(parsed, validated.Result, null, process.ExitCode);
    }

    private static ClaudeLaneProcessDiagnostic? ClassifyDiagnostic(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        if (ContainsAny(stderr, "not logged in", "authentication", "login required", "api key"))
            return ClaudeLaneProcessDiagnostic.AuthenticationRequired;
        if (ContainsAny(stderr, "permission denied", "not permitted", "access denied"))
            return ClaudeLaneProcessDiagnostic.PermissionDenied;
        if (ContainsAny(stderr, "unknown option", "invalid option", "unknown argument", "usage:"))
            return ClaudeLaneProcessDiagnostic.InvalidArguments;
        if (ContainsAny(stderr, "setting source", "settings file", "mcp config", "configuration"))
            return ClaudeLaneProcessDiagnostic.ConfigurationRejected;
        if (ContainsAny(stderr, "rate limit", "budget exceeded", "quota"))
            return ClaudeLaneProcessDiagnostic.RateOrBudgetLimited;
        if (ContainsAny(stderr, "connect", "network", "unavailable", "overloaded"))
            return ClaudeLaneProcessDiagnostic.ProviderUnavailable;
        return ClaudeLaneProcessDiagnostic.Other;
    }

    private static bool ContainsAny(string source, params string[] candidates) => candidates.Any(candidate =>
        source.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
