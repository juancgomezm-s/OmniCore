namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using Task = System.Threading.Tasks.Task;

/// <summary>
/// Lee páginas de un CAS artifact solo cuando el hash está referenciado por el journal de la
/// sesión activa. No permite inspeccionar blobs arbitrarios (ADR-0001 §8).
/// </summary>
public sealed class ArtifactReadTool : ITool
{
    public const int DefaultPageCharacters = 4096;
    public const int MaximumPageCharacters = 16384;

    private readonly IArtifactStore _artifacts;
    private readonly Func<string, bool> _isReferencedByCurrentSession;
    private readonly ToolDescriptor _descriptor = new(
        new ToolId("artifact.read"),
        "Reads one page from a CAS artifact referenced by the current session; use the hash from its externalization stub.",
        new InputSchema("{\"type\":\"object\",\"required\":[\"hash\"],\"properties\":{"
            + "\"hash\":{\"type\":\"string\",\"description\":\"SHA-256 hex from the externalization stub\"},"
            + "\"offset\":{\"type\":\"integer\",\"minimum\":0},"
            + "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16384}}}"),
        new[] { "read", "artifact" }, true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.Protected,
        EffectClass.None);

    public ArtifactReadTool(IArtifactStore artifacts, Func<string, bool> isReferencedByCurrentSession)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _isReferencedByCurrentSession = isReferencedByCurrentSession
            ?? throw new ArgumentNullException(nameof(isReferencedByCurrentSession));
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!TryParse(call.NormalizedArgumentsJson, out var hash, out var offset, out var limit))
            return new PreparationRejected("Provide a valid SHA-256 hash and page offset/limit.", null,
                ToolErrorCode.InvalidArguments);

        var claims = ResourceClaims.Empty();
        return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.None, claims, ToolRisk.Low, null));
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParse(intent.Intent.NormalizedArgumentsJson, out var hash, out var offset, out var limit))
            return Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments,
                "Invalid artifact read arguments."));

        // Check the journal before accessing the CAS: an unknown hash must not distinguish
        // between a missing blob and arbitrary content in the store.
        if (!_isReferencedByCurrentSession(hash))
            return Task.FromResult(ToolResult.Error(ToolErrorCode.PermissionDenied,
                "The artifact is not referenced by the current session."));

        string? content;
        try { content = _artifacts.GetText(ContentHash.Sha256(hash)); }
        catch (InvalidDataException)
        {
            return Task.FromResult(ToolResult.Error(ToolErrorCode.ArtifactCorrupted, "The artifact is corrupted."));
        }
        if (content is null)
            return Task.FromResult(ToolResult.Error(ToolErrorCode.ArtifactMissing, "The artifact is unavailable."));

        var start = Math.Min(offset, content.Length);
        var count = Math.Min(limit, content.Length - start);
        var page = content.Substring(start, count);
        var next = start + count;
        var hasMore = next < content.Length;
        var summary = "artifact page offset=" + start + " chars=" + count + " total=" + content.Length
            + (hasMore ? " nextOffset=" + next : " end=true");
        return Task.FromResult(new ToolResult(summary, page, null, content.Length, false, EffectOutcome.None));
    }

    private static bool TryParse(string json, out string hash, out int offset, out int limit)
    {
        hash = "";
        offset = 0;
        limit = DefaultPageCharacters;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("hash", out var hashValue)
                || hashValue.ValueKind != JsonValueKind.String)
                return false;
            hash = hashValue.GetString() ?? "";
            if (root.TryGetProperty("offset", out var offsetValue)
                && (!offsetValue.TryGetInt32(out offset) || offset < 0)) return false;
            if (root.TryGetProperty("limit", out var limitValue)
                && (!limitValue.TryGetInt32(out limit) || limit < 1 || limit > MaximumPageCharacters)) return false;
            return hash.Length == 64 && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
