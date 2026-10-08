namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Persists sensitive provider continuation state outside journal payloads.</summary>
internal static class ProviderStateCheckpoint
{
    private const string SafeFailure = "Provider state checkpoint is invalid.";
    private const string MediaType = "application/vnd.omnicore.provider-state+json";

    public static string? Persist(IArtifactStore artifacts, ProviderState? state, string modelId,
        RouteId routeId, TurnId turnId, int stepIndex, string? routeIdentityHash)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(routeId);
        ArgumentNullException.ThrowIfNull(turnId);
        if (state is null) return null;

        try
        {
            var stateJson = JsonSerializer.Serialize(state, ProviderStateCheckpointJsonContext.Default.ProviderState);
            var protector = artifacts as IProtectedArtifactStore;
            var purpose = Purpose(modelId, routeId, turnId, stepIndex, routeIdentityHash);
            var stateRef = protector is null
                ? artifacts.PutText(stateJson, MediaType, ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive)
                : protector.PutProtectedText(stateJson, purpose, MediaType, ArtifactKind.ProviderOpaqueState);
            if (stateRef.Id is null || stateRef.Size < 0 || stateRef.MediaType != MediaType
                || stateRef.Kind != ArtifactKind.ProviderOpaqueState
                || stateRef.Sensitivity != Sensitivity.Sensitive
                || stateRef.Redacted || !ValidSha256(stateRef.Hash)
                || !artifacts.Verify(stateRef.Hash, stateRef.Size)
                || (protector is null && stateRef.Size != Encoding.UTF8.GetByteCount(stateJson))
                || !string.Equals(protector is null ? artifacts.GetText(stateRef.Hash)
                    : protector.GetProtectedText(stateRef, purpose), stateJson, StringComparison.Ordinal))
                throw Invalid();

            var descriptor = new ProviderStateCheckpointDescriptor
            {
                Version = protector is null ? 2 : 3,
                ModelId = modelId,
                RouteId = routeId.Value,
                RouteIdentityHash = routeIdentityHash,
                TurnId = turnId.ToString(),
                StepIndex = stepIndex,
                StateRef = stateRef,
            };
            return JsonSerializer.Serialize(descriptor, ProviderStateCheckpointJsonContext.Default.ProviderStateCheckpointDescriptor);
        }
        catch (InvalidDataException)
        {
            throw Invalid();
        }
        catch (Exception)
        {
            throw Invalid();
        }
    }

    public static ProviderState? Restore(IArtifactStore artifacts, string descriptorJson, string modelId,
        RouteId routeId, TurnId turnId, int stepIndex, string? routeIdentityHash)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(routeId);
        ArgumentNullException.ThrowIfNull(turnId);

        ProviderStateCheckpointDescriptor descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize(descriptorJson,
                ProviderStateCheckpointJsonContext.Default.ProviderStateCheckpointDescriptor) ?? throw Invalid();
        }
        catch (Exception)
        {
            throw Invalid();
        }

        if (descriptor.Version is not (1 or 2 or 3)) throw Invalid();
        if (string.IsNullOrWhiteSpace(descriptor.ModelId) || string.IsNullOrWhiteSpace(descriptor.RouteId)
            || string.IsNullOrWhiteSpace(descriptor.TurnId) || !Guid.TryParse(descriptor.TurnId, out _)
            || descriptor.StateRef is null)
            throw Invalid();
        if (!string.Equals(descriptor.ModelId, modelId, StringComparison.Ordinal)
            || !string.Equals(descriptor.RouteId, routeId.Value, StringComparison.Ordinal)
            || !string.Equals(descriptor.TurnId, turnId.ToString(), StringComparison.Ordinal)
            || descriptor.StepIndex != stepIndex)
            return null;

        // Version 1 and unqualified version 2 checkpoints have no proof that the current
        // endpoint/protocol/profile/provider model is the same physical route. Never replay them.
        if (descriptor.Version == 1 || routeIdentityHash is null || descriptor.RouteIdentityHash is null
            || !ValidRouteIdentityHash(routeIdentityHash)
            || !ValidRouteIdentityHash(descriptor.RouteIdentityHash)
            || !string.Equals(descriptor.RouteIdentityHash, routeIdentityHash, StringComparison.Ordinal))
            return null;

        var stateRef = descriptor.StateRef;
        if (stateRef is null || stateRef.Id is null || stateRef.Hash is null || stateRef.Size < 0
            || stateRef.MediaType != MediaType || stateRef.Kind != ArtifactKind.ProviderOpaqueState
            || stateRef.Sensitivity != Sensitivity.Sensitive
            || stateRef.Redacted || !ValidSha256(stateRef.Hash))
            throw Invalid();

        try
        {
            if (!artifacts.Verify(stateRef.Hash, stateRef.Size)) throw Invalid();
            var content = descriptor.Version == 3
                ? (artifacts as IProtectedArtifactStore ?? throw Invalid()).GetProtectedText(stateRef,
                    Purpose(modelId, routeId, turnId, stepIndex, routeIdentityHash))
                : artifacts.GetText(stateRef.Hash);
            if (content is null || (descriptor.Version != 3 && Encoding.UTF8.GetByteCount(content) != stateRef.Size)) throw Invalid();
            var state = JsonSerializer.Deserialize(content, ProviderStateCheckpointJsonContext.Default.ProviderState);
            if (state is null || state.Kind is null || state.PayloadJson is null) throw Invalid();
            return state;
        }
        catch (Exception)
        {
            throw Invalid();
        }
    }

    private static bool ValidSha256(ContentHash hash) =>
        hash is not null
        && string.Equals(hash.Algorithm, "sha256", StringComparison.Ordinal)
        && hash.Value is { Length: 64 } value
        && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidRouteIdentityHash(string? value) => value is { Length: 64 }
        && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static InvalidDataException Invalid() => new(SafeFailure);

    private static string Purpose(string modelId, RouteId routeId, TurnId turnId, int stepIndex, string? identityHash)
        => JsonSerializer.Serialize(new[] { "provider-state", modelId, routeId.Value, turnId.ToString(),
            stepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), identityHash ?? string.Empty },
            ProviderStateCheckpointJsonContext.Default.StringArray);
}

internal sealed record ProviderStateCheckpointDescriptor
{
    public required int Version { get; init; }
    public required string ModelId { get; init; }
    public required string RouteId { get; init; }
    public string? RouteIdentityHash { get; init; }
    public required string TurnId { get; init; }
    public required int StepIndex { get; init; }
    public required ArtifactRef StateRef { get; init; }
}

[JsonSerializable(typeof(ProviderState))]
[JsonSerializable(typeof(ProviderStateCheckpointDescriptor))]
[JsonSerializable(typeof(string[]))]
internal partial class ProviderStateCheckpointJsonContext : JsonSerializerContext;
