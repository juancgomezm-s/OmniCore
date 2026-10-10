namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Tools;
using System.Text.Json;

/// <summary>A Host-registered, compiled workflow definition (ADR-0024/0027).</summary>
public sealed record CompiledWorkflowDescriptor(WorkflowRef Reference, ComponentSource Source)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Reference);
        ArgumentNullException.ThrowIfNull(Source);
        Reference.Validate();
        if (Source.Kind != SourceKind.BuiltIn || Source.Trust != TrustLevel.Core
            || !Reference.Id.StartsWith(Source.Owner + ":", StringComparison.Ordinal)
            || !string.Equals(Reference.Version, Source.Version, StringComparison.Ordinal))
            throw new InvalidOperationException("Dynamic tools require the exact source of a registered compiled workflow.");
    }
}

/// <summary>Creates workflow-owned Dynamic tools that still run through the ordinary tool pipeline.</summary>
public static class WorkflowToolFactory
{
    public static ITool Register(FakeCatalog catalog, CompiledWorkflowDescriptor workflow, string name,
        string description, ModelToolCapability capability, ITool implementation)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(implementation);
        workflow.Validate();
        ValidateToken(name, nameof(name));
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Dynamic tool description is required.");
        if (!Enum.IsDefined(capability)) throw new ArgumentOutOfRangeException(nameof(capability));
        if (capability == ModelToolCapability.Shell
            || implementation.Descriptor.Id.ToString() == "shell.exec")
            throw new ArgumentException("Workflow-owned Dynamic tools cannot wrap or classify shell execution.", nameof(implementation));

        var dynamicTool = new WorkflowOwnedDynamicTool(workflow, name, description, capability, implementation);
        catalog.Add(dynamicTool);
        return dynamicTool;
    }

    /// <summary>Registers a verifier pinned to an explicit executable/argv and workspace directory.</summary>
    public static ITool RegisterVerification(FakeCatalog catalog, CompiledWorkflowDescriptor workflow,
        ITool processImplementation, string executable, IReadOnlyList<string> argv, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(processImplementation);
        workflow.Validate();
        if (processImplementation.Descriptor.Id.ToString() != "process.exec")
            throw new ArgumentException("Integration verification must use the ordinary process.exec implementation.", nameof(processImplementation));
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (argv is null || argv.Count == 0 || argv.Count > 64 || argv.Any(value => value is null || value.Length > 2048))
            throw new ArgumentException("Verification argv must contain 1 to 64 bounded arguments.", nameof(argv));
        var cwd = Path.GetFullPath(workspaceRoot);
        var tool = new WorkflowOwnedDynamicTool(workflow, "verify_integration",
            "Runs the workflow's exact declared integration check through process permissions and sandbox.",
            ModelToolCapability.ValidationProcess, processImplementation,
            new VerificationBinding(executable, Array.AsReadOnly(argv.ToArray()), cwd, 120));
        catalog.Add(tool);
        return tool;
    }

    /// <summary>
    /// Returns exact-id capabilities only for tools constructed by this factory. The caller must
    /// still intersect them with the effective model policy; no dyn.* prefix is ever allowlisted.
    /// </summary>
    public static IReadOnlyDictionary<string, ModelToolCapability> Capabilities(FakeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.RegisteredTools().OfType<WorkflowOwnedDynamicTool>()
            .ToDictionary(tool => tool.Descriptor.Id.ToString(), tool => tool.Capability, StringComparer.Ordinal);
    }

    private static void ValidateToken(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Dynamic tool name uses ASCII letters, digits, '-' or '_'.", parameter);
    }

    private sealed class WorkflowOwnedDynamicTool : ITool, IReconcilableTool
    {
        private readonly ITool _implementation;
        private readonly VerificationBinding? _verification;
        internal ModelToolCapability Capability { get; }

        internal WorkflowOwnedDynamicTool(CompiledWorkflowDescriptor workflow, string name, string description,
            ModelToolCapability capability, ITool implementation, VerificationBinding? verification = null)
        {
            _implementation = implementation;
            _verification = verification;
            Capability = capability;
            var inner = implementation.Descriptor;
            var scope = (ScopeLevel)Math.Max((int)workflow.Source.Scope, (int)inner.Source.Scope);
            var trust = (TrustLevel)Math.Max((int)workflow.Source.Trust, (int)inner.Source.Trust);
            Descriptor = new ToolDescriptor(new ToolId($"dyn.{workflow.Source.Owner}.{name}"), description,
                inner.InputSchema, inner.Tags, inner.ReadOnly, inner.Destructive, inner.Risk,
                new ComponentSource(SourceKind.Dynamic, scope, trust, workflow.Source.Owner, workflow.Reference.Version),
                inner.Protection, inner.EffectClass);
        }

        public ToolDescriptor Descriptor { get; }

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
        {
            if (!call.ToolId.Equals(Descriptor.Id))
                return new PreparationRejected("Dynamic tool call identity does not match its workflow registration.", null,
                    ToolErrorCode.InvalidArguments);
            if (_verification is { } binding && !MatchesVerification(call.NormalizedArgumentsJson, binding))
                return new PreparationRejected("Integration check differs from the workflow's declared executable, argv, or workspace.", null,
                    ToolErrorCode.InvalidArguments);
            var result = _implementation.Prepare(call, context);
            if (result is not Prepared prepared) return result;
            if (prepared.Intent.ToolCallId != call.ToolCallId || !prepared.Intent.ToolId.Equals(Descriptor.Id)
                || prepared.Intent.Effect != Descriptor.EffectClass || prepared.Intent.Risk != Descriptor.Risk)
                throw new InvalidOperationException("Dynamic tool implementation changed the pipeline identity or declared risk.");
            if (_verification is { } expected
                && (prepared.Intent.Claims.Process is not { } process
                    || !string.Equals(process.Executable, expected.Executable, StringComparison.Ordinal)
                    || !process.Args.SequenceEqual(expected.Argv, StringComparer.Ordinal)
                    || process.NetworkRequired
                    || process.WorkingDirectory is not { } cwd
                    || !string.Equals(cwd, expected.WorkspaceRoot, PathComparison())
                    || prepared.Intent.Claims.Writes.Count != 1
                    || !string.Equals(prepared.Intent.Claims.Writes.Single(), expected.WorkspaceRoot,
                        PathComparison())))
                return new PreparationRejected("Integration check claims do not match the pinned verifier.", null,
                    ToolErrorCode.InvalidArguments);
            return result;
        }

        private static bool MatchesVerification(string json, VerificationBinding expected)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("executable", out var executable) || executable.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("argv", out var argv) || argv.ValueKind != JsonValueKind.Array
                    || !root.TryGetProperty("cwd", out var cwd) || cwd.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("timeoutSeconds", out var timeout) || timeout.ValueKind != JsonValueKind.Number
                    || !timeout.TryGetInt32(out var seconds)
                    || root.TryGetProperty("networkRequired", out var network)
                        && network.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) return false;
                var arguments = argv.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                    ? value.GetString()! : throw new JsonException("argv entry must be text.")).ToArray();
                var requestedCwd = Path.GetFullPath(cwd.GetString()!);
                return string.Equals(executable.GetString(), expected.Executable, StringComparison.Ordinal)
                    && arguments.SequenceEqual(expected.Argv, StringComparer.Ordinal)
                    && string.Equals(requestedCwd, expected.WorkspaceRoot, PathComparison())
                    && seconds == expected.TimeoutSeconds;
            }
            catch (Exception failure) when (failure is JsonException or ArgumentException or NotSupportedException)
            { return false; }
        }

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!intent.Intent.ToolId.Equals(Descriptor.Id))
                throw new InvalidOperationException("Dynamic tool authorization belongs to another registration.");
            return _implementation.ExecuteAsync(intent, context, cancellationToken);
        }

        public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!intent.Intent.ToolId.Equals(Descriptor.Id))
                throw new InvalidOperationException("Dynamic tool reconciliation belongs to another registration.");
            return (_implementation as IReconcilableTool)?.DescribeReconciliation(intent, context, cancellationToken);
        }

        private static StringComparison PathComparison() => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private sealed record VerificationBinding(string Executable, IReadOnlyList<string> Argv,
        string WorkspaceRoot, int TimeoutSeconds);
}
