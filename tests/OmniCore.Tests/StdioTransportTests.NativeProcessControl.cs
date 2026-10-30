using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Sandbox;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public Task Native_process_controls_cover_active_and_quiescent_run_then_passively_read_terminal_output() =>
        RunNativeProcessControlsAsync(SandboxStrength.Strong);

    [Fact]
    public Task Native_weak_process_controls_use_the_real_persistent_job_without_an_extra_provider_request() =>
        RunNativeProcessControlsAsync(SandboxStrength.Weak);

    private async Task RunNativeProcessControlsAsync(SandboxStrength strength)
    {
        if (!OperatingSystem.IsWindows() && (!OperatingSystem.IsLinux() || strength == SandboxStrength.Weak))
        {
            Assert.Skip("Persistent Strong integration supports Windows/Linux; the Weak job backend requires Windows.");
            return;
        }

        var executable = OperatingSystem.IsWindows() ? "./OmniCore.SandboxTestProcess.exe" : "/bin/sh";
        var processArgs = new Func<string, string, string>((executable, workspace) => JsonSerializer.Serialize(new
        {
            executable,
            argv = OperatingSystem.IsWindows() ? new[] { "persistent-process-control" }
                : new[] { "-c", "printf 'READY-ñ\\n'; while IFS= read -r line; do printf 'ACK:%s\\n' \"$line\"; done; printf 'EOF-FINAL-ñ\\n'; exit 35" },
            cwd = workspace,
            timeoutSeconds = 90,
            terminal = false,
            readPaths = OperatingSystem.IsWindows() ? new[]
            {
                "OmniCore.SandboxTestProcess.exe", "OmniCore.SandboxTestProcess.dll",
                "OmniCore.SandboxTestProcess.deps.json", "OmniCore.SandboxTestProcess.runtimeconfig.json",
                "OmniCore.Infrastructure.dll", "OmniCore.Abstractions.dll", "OmniCore.Domain.dll",
            } : Array.Empty<string>(),
            writePaths = Array.Empty<string>(),
        }));
        var releaseSecondRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"),
            providerResponseAsync: (index, _) => index switch
            {
                0 => Task.FromResult(CliEndToEndTests.ResponsesToolCallResponse("persistent-a", "process.start",
                    processArgs(executable, Environment.CurrentDirectory))),
                1 => releaseSecondRequest.Task,
                2 => Task.FromResult(CliEndToEndTests.ResponsesTextResponse("Both process sessions were handled.")),
                _ => Task.FromResult(CliEndToEndTests.ResponsesTextResponse("Unexpected extra provider request.")),
            },
            permissionsYaml: "profile: autonomous\nrules:\n  tool.search: allow\n  process.start: allow\n  process.read: allow\n  process.send_input: allow\n  process.stop: allow\n",
            processSandboxStrength: strength);
        if (OperatingSystem.IsWindows()) CopyProcessHelperInto(fixture.Workspace);
        InitializeProcessWorkspace(fixture.Workspace);

        var sessionId = fixture.CreateEmptySession();
        var selection = fixture.QueryJson("session.model.selection");
        var startTurn = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "conversation.start",
            text = "Start the first process, then wait for the second process request.",
            mode = "act",
            expectedRevision = (long?)null,
            expectedPolicyRevision = selection.GetProperty("sessionPolicyRevision").GetInt64(),
            expectedDefaultRevision = (string?)null,
        }));
        var startAck = Ack(fixture.ExchangeTrusted(startTurn, sessionId, null));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, startAck.Outcome?.Kind);
        var runId = fixture.QueryJson("humanActionScope").GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The process control turn did not create a Run.");
        await fixture.WaitForProviderBodiesAsync(2, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var firstHandle = FindLatestProcessHandle(fixture, sessionId);
        var controlCount = 0;
        async Task<(JsonElement Result, string? DisplayOutput)> Control(string toolName, object args)
        {
            var catalog = fixture.QueryJson("command.catalog");
            var commandId = Guid.NewGuid().ToString("D");
            var argumentsJson = JsonSerializer.Serialize(args);
            var payload = JsonSerializer.Serialize(new
            {
                cmd = "process.control",
                canonicalCommandId = "core:process.control",
                arguments = new[] { toolName, argumentsJson },
                expectedSessionId = sessionId,
                expectedRunId = runId,
                expectedModeRevision = NullableJsonInt64(catalog.GetProperty("modeRevision")),
                expectedPolicyRevision = catalog.GetProperty("policyRevision").GetInt64(),
                expectedSelectionRevision = catalog.GetProperty("selectionRevision").GetInt32(),
                expectedRoutingPolicyRevision = NullableJsonInt64(catalog.GetProperty("routingPolicyRevision")),
                catalogRevision = catalog.GetProperty("catalogRevision").GetString(),
                sourceFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    "omnicore-native-process-control-v1"))),
            });
            var request = WireEnvelope.Command(commandId, payload);
            var ack = Ack(fixture.ExchangeTrusted(request, sessionId, runId));
            Assert.True(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
                $"{toolName} was not admitted: {ack.Status}/{ack.Error}/{ack.Outcome?.Reason}");
            await fixture.WaitForReceiptExecutionAsync(commandId, "completed");
            var receipt = fixture.ReadReceipt(commandId);
            Assert.True(receipt is { State: CommandReceiptState.Completed, OutcomeKind: "Accepted",
                ExecutionState: "completed", ExecutionCode: "native_tool_completed" },
                $"Native control receipt was not terminal: {receipt?.State}/{receipt?.OutcomeKind}/{receipt?.ExecutionState}/{receipt?.ExecutionCode}");
            await WaitForNativeToolSucceededAsync(fixture, sessionId, commandId, toolName);
            var result = ReadNativeToolResult(fixture, sessionId, toolName);
            controlCount++;
            return result;
        }

        var firstRead = await Control("process.read", new { handle = firstHandle, afterSequence = 0, maxChunks = 16, waitMs = 5_000 });
        Assert.Contains("READY-ñ", firstRead.DisplayOutput, StringComparison.Ordinal);
        releaseSecondRequest.TrySetResult(CliEndToEndTests.ResponsesToolCallResponse("persistent-b", "process.start",
            processArgs(executable, fixture.Workspace)));
        await fixture.Provider.WaitForRequestAsync(3, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await WaitForTurnTerminalAsync(fixture, sessionId, runId);
        var secondHandle = FindLatestProcessHandle(fixture, sessionId);

        var firstSession = Assert.Single(fixture.QueryJson("process.sessions:" + runId)
            .GetProperty("processes").EnumerateArray(), item => item.GetProperty("handle").GetString() == firstHandle);
        var initialSequence = firstSession.GetProperty("lastOutputSequence").GetInt64();
        Assert.True(initialSequence > 0);
        var input = await Control("process.send_input", new { handle = firstHandle, input = "hello\n", eof = false });
        Assert.Contains("Input accepted", input.Result.GetRawText(), StringComparison.Ordinal);
        var ackRead = await Control("process.read", new { handle = firstHandle, afterSequence = initialSequence, maxChunks = 16, waitMs = 5_000 });
        Assert.Contains("ACK:hello", ackRead.DisplayOutput, StringComparison.Ordinal);
        var stopped = await Control("process.stop", new { handle = firstHandle });
        Assert.Contains("stopped", stopped.Result.GetRawText(), StringComparison.Ordinal);
        var eof = await Control("process.send_input", new { handle = secondHandle, input = "", eof = true });
        Assert.Contains("Input accepted", eof.Result.GetRawText(), StringComparison.Ordinal);

        await WaitForRunCompletedAsync(fixture, sessionId, runId);
        await WaitForRunSummaryAsync(fixture, sessionId, runId);
        var eventsBeforeQueries = fixture.ReadEvents(sessionId);
        Assert.Equal(5, controlCount);
        Assert.Equal(3, fixture.Provider.RequestCount);
        Assert.Equal(1, eventsBeforeQueries.Count(item => item.Payload is UserInputReceived inputEvent
            && inputEvent.RunId.ToString() == runId));
        var controls = eventsBeforeQueries.Where(item => item.Payload is NativeCommandInvocationAdmitted admitted
            && admitted.Kind == "nativeTool").ToArray();
        Assert.Equal(5, controls.Length);
        Assert.All(controls, item => Assert.Equal(runId, item.Envelope.RunId?.ToString()));
        foreach (var control in controls)
        {
            var invocationId = Assert.IsType<NativeCommandInvocationAdmitted>(control.Payload).InvocationId.ToString();
            var requested = Assert.Single(eventsBeforeQueries, item => item.Payload is ToolCallRequested call
                && call.ProviderCallId == "native-action-" + invocationId);
            var toolCallId = Assert.IsType<ToolCallRequested>(requested.Payload).ToolCallId;
            Assert.Contains(eventsBeforeQueries, item => item.Envelope.Sequence > requested.Envelope.Sequence
                && item.Envelope.ToolCallId == toolCallId && item.Payload is ToolCallSucceeded);
        }

        var terminalSnapshot = fixture.QueryJson("process.sessions:" + runId);
        var secondTerminal = Assert.Single(terminalSnapshot.GetProperty("processes").EnumerateArray(),
            item => item.GetProperty("handle").GetString() == secondHandle);
        Assert.Equal("exited", secondTerminal.GetProperty("state").GetString());
        Assert.False(secondTerminal.GetProperty("canInput").GetBoolean());
        Assert.False(secondTerminal.GetProperty("canStop").GetBoolean());
        var terminalOutput = fixture.QueryJson($"process.output:{runId}:{secondHandle}:0");
        Assert.Equal("exited", terminalOutput.GetProperty("state").GetString());
        Assert.Contains("EOF-FINAL-ñ", terminalOutput.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(35, terminalOutput.GetProperty("exitCode").GetInt32());
        Assert.Equal(3, fixture.Provider.RequestCount);
        var afterQueries = fixture.ReadEvents(sessionId);
        Assert.True(eventsBeforeQueries.Count == afterQueries.Count,
            "Passive process queries appended events: " + string.Join(" | ", afterQueries.Skip(eventsBeforeQueries.Count)
                .Select(item => item.Payload.GetType().Name + ":" + item.Payload)));
    }

    private static void CopyProcessHelperInto(string workspace)
    {
        var names = new[]
        {
            "OmniCore.SandboxTestProcess.exe", "OmniCore.SandboxTestProcess.dll",
            "OmniCore.SandboxTestProcess.deps.json", "OmniCore.SandboxTestProcess.runtimeconfig.json",
            "OmniCore.Infrastructure.dll", "OmniCore.Abstractions.dll", "OmniCore.Domain.dll",
        };
        foreach (var name in names)
        {
            var source = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(source)) throw new FileNotFoundException("Fresh sandbox process fixture output is missing.", source);
            File.Copy(source, Path.Combine(workspace, name), overwrite: true);
        }
    }

    private static void InitializeProcessWorkspace(string workspace)
    {
        RunGit(workspace, "init", "--quiet");
        RunGit(workspace, "config", "user.name", "OmniCore process fixture");
        RunGit(workspace, "config", "user.email", "process-fixture@localhost");
        if (OperatingSystem.IsWindows())
            RunGit(workspace, "add", "OmniCore.SandboxTestProcess.exe", "OmniCore.SandboxTestProcess.dll",
                "OmniCore.SandboxTestProcess.deps.json", "OmniCore.SandboxTestProcess.runtimeconfig.json",
                "OmniCore.Infrastructure.dll", "OmniCore.Abstractions.dll", "OmniCore.Domain.dll");
        RunGit(workspace, "commit", "--quiet", "--allow-empty", "-m", "process fixture baseline");
    }

    private static void RunGit(string workspace, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Git could not start for the process fixture.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stdout + stderr);
    }

    private static string FindLatestProcessHandle(NativeCommandHandoffFixture fixture, string sessionId)
    {
        var events = fixture.ReadEvents(sessionId);
        var starts = events.Where(item => item.Payload is ToolCallRequested call && call.ToolName == "process.start").ToArray();
        Assert.True(starts.Length is 1 or 2, "Expected one or two process.start calls; observed: "
            + string.Join(" | ", events.Where(item => item.Payload is ToolCallRequested)
                .Select(item => Assert.IsType<ToolCallRequested>(item.Payload).ToolName + "/"
                    + Assert.IsType<ToolCallRequested>(item.Payload).ProviderCallId)));
        var requested = starts[^1];
        var callId = Assert.IsType<ToolCallRequested>(requested.Payload).ToolCallId;
        var terminal = events.Where(item => item.Payload is ToolCallSucceeded call && call.ToolCallId == callId).ToArray();
        if (terminal.Length == 0)
            throw new InvalidOperationException("process.start did not succeed; call=" + callId + "; terminal events="
                + string.Join(" | ", events.Select(item => item.Payload.GetType().Name + ":" + item.Payload)
                    .TakeLast(24)));
        using var result = JsonDocument.Parse(Assert.IsType<ToolCallSucceeded>(terminal[^1].Payload).ResultJson);
        var resultRoot = result.RootElement;
        using var summary = resultRoot.TryGetProperty("summary", out var summaryValue)
            && summaryValue.ValueKind == JsonValueKind.String
            ? JsonDocument.Parse(summaryValue.GetString()!) : null;
        if (summary is not null) resultRoot = summary.RootElement;
        if (!resultRoot.TryGetProperty("handle", out var handleValue))
            throw new InvalidDataException("process.start result omitted its opaque handle: " + result.RootElement.GetRawText());
        return handleValue.GetString()
            ?? throw new InvalidDataException("process.start returned no opaque handle.");
    }

    private static (JsonElement Result, string? DisplayOutput) ReadNativeToolResult(NativeCommandHandoffFixture fixture, string sessionId,
        string toolName)
    {
        var events = fixture.ReadEvents(sessionId);
        var request = events.Last(item => item.Payload is ToolCallRequested call && call.ToolName == toolName);
        var callId = Assert.IsType<ToolCallRequested>(request.Payload).ToolCallId;
        var succeeded = Assert.IsType<ToolCallSucceeded>(events.Last(item =>
            item.Payload is ToolCallSucceeded call && call.ToolCallId == callId).Payload);
        return (JsonDocument.Parse(succeeded.ResultJson).RootElement.Clone(), succeeded.DisplayOutput);
    }

    private static async Task WaitForNativeToolSucceededAsync(NativeCommandHandoffFixture fixture,
        string sessionId, string invocationId, string toolName)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var events = fixture.ReadEvents(sessionId);
            var request = events.LastOrDefault(item => item.Payload is ToolCallRequested call
                && call.ProviderCallId == "native-action-" + invocationId && call.ToolName == toolName);
            if (request.Payload is ToolCallRequested requested
                && events.Any(item => item.Envelope.Sequence > request.Envelope.Sequence
                    && item.Envelope.ToolCallId == requested.ToolCallId
                    && item.Payload is ToolCallSucceeded)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Native process control did not produce its correlated ToolCallSucceeded result.");
    }

    private static async Task WaitForTurnTerminalAsync(NativeCommandHandoffFixture fixture, string sessionId, string runId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (fixture.ReadEvents(sessionId).Any(item => item.Envelope.RunId?.ToString() == runId
                && item.Payload is TurnCompleted)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Model Turn did not complete after process.start B.");
    }

    private static async Task WaitForRunCompletedAsync(NativeCommandHandoffFixture fixture, string sessionId, string runId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (fixture.ReadEvents(sessionId).Any(item => item.Envelope.RunId?.ToString() == runId
                && item.Payload is RunCompleted)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        var states = fixture.QueryJson("process.sessions:" + runId).GetRawText();
        throw new TimeoutException("Run remained open after all process controls: " + states);
    }

    private static async Task WaitForRunSummaryAsync(NativeCommandHandoffFixture fixture, string sessionId, string runId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (fixture.ReadEvents(sessionId).Any(item => item.Payload is RunSummaryRecorded summary
                && summary.RunId.ToString() == runId)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("The final Run summary was not durably recorded.");
    }
}
