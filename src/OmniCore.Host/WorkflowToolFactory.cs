namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Tools;

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

        var dynamicTool = new WorkflowOwnedDynamicTool(workflow, name, description, capability, implementation);
        catalog.Add(dynamicTool);
        return dynamicTool;
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
        internal ModelToolCapability Capability { get; }

        internal WorkflowOwnedDynamicTool(CompiledWorkflowDescriptor workflow, string name, string description,
            ModelToolCapability capability, ITool implementation)
        {
            _implementation = implementation;
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
            var result = _implementation.Prepare(call, context);
            if (result is not Prepared prepared) return result;
            if (prepared.Intent.ToolCallId != call.ToolCallId || !prepared.Intent.ToolId.Equals(Descriptor.Id)
                || prepared.Intent.Effect != Descriptor.EffectClass || prepared.Intent.Risk != Descriptor.Risk)
                throw new InvalidOperationException("Dynamic tool implementation changed the pipeline identity or declared risk.");
            return result;
        }

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!intent.Intent.ToolId.Equals(Descriptor.Id))
                throw new InvalidOperationException("Dynamic tool authorization belongs to another registration.");
            return _implementation.ExecuteAsync(intent, context, cancellationToken);
        }

        public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent, ToolExecutionContext context)
        {
            if (!intent.Intent.ToolId.Equals(Descriptor.Id))
                throw new InvalidOperationException("Dynamic tool reconciliation belongs to another registration.");
            return (_implementation as IReconcilableTool)?.DescribeReconciliation(intent, context);
        }
    }
}
