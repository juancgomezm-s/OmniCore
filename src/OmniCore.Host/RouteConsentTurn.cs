namespace OmniCore.Host;

using OmniCore.Domain;
using System.Text.Json.Serialization;

/// <summary>The already admitted input waiting at route authorization, before TurnStarted.</summary>
internal sealed record RouteConsentTurn(long InputSequence, LaneId Lane,
    TurnInstructionSnapshot InstructionSnapshot, ReasoningResolution? ReasoningResolution);

[JsonSerializable(typeof(RouteConsentTurn))]
internal partial class RouteConsentJsonContext : JsonSerializerContext;
