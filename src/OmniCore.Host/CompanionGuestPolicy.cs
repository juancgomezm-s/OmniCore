using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Domain;

namespace OmniCore.Host;

/// <summary>A stable cosmetic lottery for a profile/session, independent of the species RNG.</summary>
internal static class CompanionGuestPolicy
{
    internal const int Claude = 1, Gpt = 2;
    // One sample per profile/session. Disjoint intervals allow a regular session
    // to welcome a guest after actual model use, without replacing an existing guest.
    internal static string? Select(SessionId? session, DateTimeOffset discoveredAt, int modelsUsed = 0)
    {
        if (session is null) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("companion-guest-v1/"
            + discoveredAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "/" + session));
        var sample = BinaryPrimitives.ReadUInt32LittleEndian(bytes) / 4294967296d;
        if (sample < .025) return "claude-crab";
        if (sample < .05) return "codex-robot";
        if (sample < .15 && (modelsUsed & Claude) != 0) return "claude-crab";
        if (sample is >= .15 and < .25 && (modelsUsed & Gpt) != 0) return "codex-robot";
        return null;
    }
    internal static int ModelFamily(string modelId)
    {
        var name = modelId.AsSpan(modelId.LastIndexOf('/') + 1);
        return name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            ? Claude : name.Equals("gpt", StringComparison.OrdinalIgnoreCase) || name.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ? Gpt : 0;
    }
}
