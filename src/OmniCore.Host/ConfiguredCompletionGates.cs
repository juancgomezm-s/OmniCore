namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Protocol;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>Executes trusted workspace Build/Test argv via the same process.exec permission pipeline.</summary>
internal sealed class ConfiguredCompletionGates
{
    private readonly WorkspaceGatesYaml _configuration;
    private readonly string _workspaceRoot;
    private readonly RunId _runId;
    private readonly LaneId _laneId;
    private readonly TaskId? _taskId;
    private readonly IReadOnlyDictionary<string, string>? _restrictions;
    private readonly IAuditSink _audit;
    private readonly IArtifactStore _artifacts;
    private readonly Func<InteractionRequested, string?>? _interactionResponder;
    private readonly bool _interactive;

    public ConfiguredCompletionGates(WorkspaceGatesYaml configuration, string workspaceRoot,
        RunId runId, LaneId laneId, TaskId? taskId,
        IReadOnlyDictionary<string, string>? restrictions, IAuditSink audit, IArtifactStore artifacts,
        Func<InteractionRequested, string?>? interactionResponder, bool interactive)
    {
        _configuration = configuration;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _runId = runId;
        _laneId = laneId;
        _taskId = taskId;
        _restrictions = restrictions;
        _audit = audit;
        _artifacts = artifacts;
        _interactionResponder = interactionResponder;
        _interactive = interactive;
    }

    public IReadOnlyList<ExternalCompletionGateResult> Run(EventStream stream, CancellationToken cancellationToken)
    {
        var results = new List<ExternalCompletionGateResult>();
        RunCommand("build", _configuration.Build, stream, results, cancellationToken);
        RunCommand("test", _configuration.Test, stream, results, cancellationToken);
        if (_configuration.Acceptance && results.All(result => result.Passed))
        {
            var interaction = new InteractionRequested(InteractionId.New(), InteractionKind.AcceptanceConfirmation,
                "{\"operation\":\"completion.acceptance\",\"reason\":\"Se requiere confirmación del usuario para completar el Run\"}",
                "[{\"id\":\"accept\",\"intent\":\"allow\"},{\"id\":\"revise\",\"intent\":\"deny\"}]",
                "revise", null, _laneId, _taskId, null, 0, 1);
            results.Add(new ExternalCompletionGateResult("acceptance", false,
                "esperando confirmación del usuario", interaction));
        }
        return results;
    }

    private void RunCommand(string key, IReadOnlyList<string>? argv, EventStream stream,
        List<ExternalCompletionGateResult> results, CancellationToken cancellationToken)
    {
        if (argv is null) return;
        var executable = argv[0];
        var arguments = argv.Skip(1).ToArray();
        var callId = ToolCallId.New();
        var argsJson = ProcessArguments(executable, arguments, _workspaceRoot,
            RequiresBuildNetwork(executable, arguments));
        var tool = new ProcessExecTool(SystemProcessRuntime.Instance(), new PathBoundaryValidator());
        var catalog = new FakeCatalog().Add(tool);
        var policy = OmniHost.CreateGrantAwarePolicy(RunMode.Act, _restrictions, _workspaceRoot, _runId, _audit);
        var executor = new ScriptedToolExecutor(catalog, policy, _workspaceRoot, boundary: null,
            _audit, _interactionResponder, _interactive);
        var outcome = executor.ExecuteTool(new ValidatedToolCall(callId, new ToolId("process.exec"),
            "completion-gate:" + key + ":" + callId, argsJson), false, cancellationToken, stream);
        if (outcome.Events.Count > 0)
            stream.AppendBatch(outcome.Events, DurabilityClass.Standard);

        if (outcome.Succeeded)
        {
            results.Add(new ExternalCompletionGateResult(key, true,
                RedactAndCap(outcome.Summary ?? "completado")));
            return;
        }

        var output = RedactAndCap(outcome.Preview ?? outcome.Summary ?? "El proceso falló sin salida capturada.");
        var artifact = _artifacts.PutText(output, "text/plain; charset=utf-8", ArtifactKind.ProcessOutput,
            Sensitivity.Sensitive);
        results.Add(new ExternalCompletionGateResult(key, false,
            RedactAndCap(outcome.Summary ?? "falló"), OutputArtifact: artifact));
    }

    private static string ProcessArguments(string executable, IReadOnlyList<string> arguments, string cwd,
        bool networkRequired)
    {
        var json = "{" + JsonObj.Field("executable", executable) + ",\"argv\":[";
        var encoded = new string[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
            encoded[i] = "\"" + JsonEncodedText.Encode(arguments[i]).ToString() + "\"";
        json += string.Join(",", encoded) + "]," + JsonObj.Field("cwd", cwd)
            + ",\"timeoutSeconds\":3600,\"networkRequired\":" + (networkRequired ? "true" : "false")
            + ",\"sandboxLevel\":\"Strong\"}";
        return json;
    }

    private static bool RequiresBuildNetwork(string executable, IReadOnlyList<string> arguments)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        var command = arguments.FirstOrDefault();
        var subcommand = arguments.Skip(1).FirstOrDefault();
        var dotnetBuild = name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && (command is "build" or "test" or "restore");
        var packageManagerBuild = (name is "npm" or "pnpm")
            && (command is "install" or "build" or "test"
                || command == "run" && (subcommand is "build" or "test"));
        return dotnetBuild || packageManagerBuild;
    }

    private static string RedactAndCap(string value)
    {
        var safe = OmniCliRuntime.RedactSensitive(value);
        return safe.Length <= 4000 ? safe : safe[..4000] + "\n[gate summary capped]";
    }
}
