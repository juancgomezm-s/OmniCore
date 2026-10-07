namespace OmniCore.Host;

using System.Text.Json;
using System.Security.Cryptography;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Canonical reusable configuration; excludes every execution/session identity.</summary>
internal static class AgentProfileFingerprint
{
    internal static ContentHash Hash(AgentProfile profile)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            Write(writer, profile);
            writer.WriteEndObject();
        }
        return ContentHash.Sha256(Convert.ToHexStringLower(SHA256.HashData(stream.ToArray())));
    }

    internal static void Write(Utf8JsonWriter writer, AgentProfile profile)
    {
        writer.WriteString("profileId", profile.Id.ToString());
        writer.WriteString("source", "resolved.configuration");
        writer.WriteString("name", profile.Name);
        writer.WriteNumber("revision", profile.Revision);
        writer.WriteStartObject("permissionCeiling");
        WriteStrings(writer, "reads", profile.PermissionCeiling.Reads);
        WriteStrings(writer, "writes", profile.PermissionCeiling.Writes);
        // Rule order and argv order can affect matching; never sort them as sets.
        writer.WriteStartArray("process");
        foreach (var rule in profile.PermissionCeiling.Process)
        {
            writer.WriteStartObject();
            writer.WriteString("executablePattern", rule.ExecutablePattern);
            WriteStrings(writer, "argvPatterns", rule.ArgvPatterns);
            writer.WriteString("decision", rule.Decision.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("network");
        foreach (var rule in profile.PermissionCeiling.Network)
        {
            writer.WriteStartObject();
            writer.WriteString("hostPattern", rule.HostPattern);
            writer.WriteString("decision", rule.Decision.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteStrings(writer, "secrets", profile.PermissionCeiling.Secrets);
        writer.WriteBoolean("allowShell", profile.PermissionCeiling.AllowShell);
        writer.WriteEndObject();
        WriteStrings(writer, "preferredTools", profile.PreferredTools.Select(tool => tool.ToString()));
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
