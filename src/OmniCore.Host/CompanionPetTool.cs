using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Host;

/// <summary>Built-in extension contribution, admitted through the ordinary tool pipeline.</summary>
public sealed class CompanionPetTool(CompanionPetService service, Action? synchronize = null,
    string clientId = "terminal") : ITool
{
    public ToolDescriptor Descriptor { get; } = new(new ToolId("companion.pet"),
        "Discover and care for the virtual pet / mascota: discover, status, pet, rename, show, hide. Cosmetic only; never do work to feed it.",
        new InputSchema("""
            {"type":"object","properties":{"action":{"type":"string","enum":["discover","status","pet","rename","show","hide"]},"name":{"type":"string"}},"required":["action"],"additionalProperties":false}
            """), ["companion", "pet", "mascota", "animal", "huevo"], true, false, ToolRisk.Low,
        ComponentSource.Core(), ToolProtection.Protected);
    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            var action = CompanionPetProtocol.DecodeAction(call.NormalizedArgumentsJson);
            if (action?.Action is not ("discover" or "status" or "pet" or "rename" or "show" or "hide"))
                return new PreparationRejected("Unknown companion action.", null, ToolErrorCode.InvalidArguments);
            if (action.Action == "rename") CompanionPetService.ValidateName(action.Name);
            return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));
        }
        catch (ArgumentException error) { return new PreparationRejected(error.Message, null, ToolErrorCode.InvalidArguments); }
    }
    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); synchronize?.Invoke();
        var action = CompanionPetProtocol.DecodeAction(intent.Intent.NormalizedArgumentsJson)! with { ClientId = clientId };
        try
        {
            var snapshot = service.Act(action, intent.Intent.ToolCallId.ToString());
            return Task.FromResult(ToolResult.Ok("Companion updated.", CompanionPetProtocol.Encode(snapshot), 0, false, EffectOutcome.None));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments, error.Message)); }
    }
}
