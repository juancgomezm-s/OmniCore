using System.Text.Json.Serialization;
namespace OmniCore.Models;
public sealed record ClaudeOAuthRoles(
    [property: JsonPropertyName("organization_role")] string? OrganizationRole,
    [property: JsonPropertyName("workspace_role")] string? WorkspaceRole,
    [property: JsonPropertyName("organization_name")] string? OrganizationName);
