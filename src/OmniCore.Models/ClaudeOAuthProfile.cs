using System.Text.Json.Serialization;

namespace OmniCore.Models;

/// <summary>
/// Perfil de cuenta leído de <c>GET /api/oauth/profile</c>. Solo se exponen los campos que el
/// runtime consume: plan, tier y identidad no secreta. El resto del wire se ignora a propósito —
/// guardar lo que no se usa amplía la superficie ante cambios del backend (plan §11.1).
/// </summary>
public sealed record ClaudeOAuthProfile
{
    private ClaudeOAuthProfile(
        string? accountUuid,
        string? emailAddress,
        string? displayName,
        string? organizationUuid,
        string? subscriptionType,
        string? rateLimitTier)
    {
        AccountUuid = accountUuid;
        EmailAddress = emailAddress;
        DisplayName = displayName;
        OrganizationUuid = organizationUuid;
        SubscriptionType = subscriptionType;
        RateLimitTier = rateLimitTier;
    }

    public string? SubscriptionType { get; init; }

    /// <summary>Nivel de rate limit informado por el servidor, nunca estimado.</summary>
    public string? RateLimitTier { get; init; }

    public string? AccountUuid { get; init; }

    public string? OrganizationUuid { get; init; }

    public string? EmailAddress { get; init; }

    public string? DisplayName { get; init; }

    internal static ClaudeOAuthProfile FromWire(ClaudeOAuthProfileDto dto) => new(
        accountUuid: dto.Account?.Uuid,
        emailAddress: dto.Account?.Email,
        displayName: string.IsNullOrWhiteSpace(dto.Account?.DisplayName) ? null : dto.Account.DisplayName,
        organizationUuid: dto.Organization?.Uuid,
        subscriptionType: MapSubscriptionType(dto.Organization?.OrganizationType),
        rateLimitTier: string.IsNullOrWhiteSpace(dto.Organization?.RateLimitTier)
            ? null
            : dto.Organization.RateLimitTier);

    /// <summary>
    /// Mapeo explícito, sin default silencioso: un organization_type desconocido da null y el
    /// runtime lo muestra como desconocido en vez de inventarse un plan (ADR-0031: la status line
    /// nunca inventa datos).
    /// </summary>
    private static string? MapSubscriptionType(string? organizationType) => organizationType switch
    {
        "claude_max" => "max",
        "claude_pro" => "pro",
        "claude_team" => "team",
        "claude_enterprise" => "enterprise",
        _ => null,
    };
}

internal sealed class ClaudeOAuthProfileDto
{
    [JsonPropertyName("account")]
    public ClaudeOAuthProfileAccountDto? Account { get; set; }

    [JsonPropertyName("organization")]
    public ClaudeOAuthProfileOrganizationDto? Organization { get; set; }
}

internal sealed class ClaudeOAuthProfileAccountDto
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

internal sealed class ClaudeOAuthProfileOrganizationDto
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("organization_type")]
    public string? OrganizationType { get; set; }

    [JsonPropertyName("rate_limit_tier")]
    public string? RateLimitTier { get; set; }
}
