namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Domain;

/// <summary>Canonical, non-secret projection for the ADR-0047 mode authority fingerprint component.</summary>
internal static class ModeAuthorityFingerprint
{
    internal static void Write(Utf8JsonWriter writer, RunModeAuthority? authority)
    {
        if (authority is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("runId", authority.RunId.ToString());
        writer.WriteNumber("revision", authority.Revision);
        writer.WriteString("mode", authority.Mode.ToString());
        writer.WriteString("strategy", authority.Strategy.ToString());
        writer.WriteString("productEffort", authority.ProductEffort.ToString());
        writer.WriteBoolean("modePinned", authority.ModePinned);
        writer.WriteBoolean("autoModeSwitch", authority.AutoModeSwitch);
        writer.WriteNumber("objectiveRevision", authority.ObjectiveRevision);
        writer.WriteString("objectiveDigest", authority.ObjectiveDigest);
        writer.WriteNumber("policyRevision", authority.PolicyRevision);
        if (authority.Authorization is not { } authorization)
            writer.WriteNull("authorization");
        else
        {
            authorization.Validate();
            writer.WriteStartObject("authorization");
            writer.WriteString("authorizationId", authorization.AuthorizationId);
            writer.WriteNumber("authorityRevision", authorization.AuthorityRevision);
            writer.WriteNumber("objectiveRevision", authorization.ObjectiveRevision);
            writer.WriteString("objectiveDigest", authorization.ObjectiveDigest);
            writer.WriteNumber("policyRevision", authorization.PolicyRevision);
            writer.WriteString("grantedAtUtc", authorization.GrantedAtUtc!.Value);
            writer.WriteStartArray("allowedModes");
            foreach (var mode in authorization.AllowedModes) writer.WriteStringValue(mode.ToString());
            writer.WriteEndArray();
            writer.WriteStartObject("limits");
            writer.WriteNumber("maxAgents", authorization.Limits.MaxAgents);
            writer.WriteNumber("maxDepth", authorization.Limits.MaxDepth);
            writer.WriteNumber("maxTurns", authorization.Limits.MaxTurns);
            writer.WriteNumber("maxToolCalls", authorization.Limits.MaxToolCalls);
            writer.WriteNumber("maxElapsedSeconds", authorization.Limits.MaxElapsedSeconds);
            writer.WriteNumber("maxSpendUsd", authorization.Limits.MaxSpendUsd);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}
