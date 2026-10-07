namespace OmniCore.Context;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Invoca una capacidad lateral del mismo provider nativo y deja eventos/artifacts auditables.</summary>
public sealed class MetaModelService
{
    private readonly IModelProvider _provider;
    private readonly IArtifactStore _artifacts;
    private readonly IContextEventSink _events;
    private readonly ModelSelection _selection;
    private readonly Func<string, string> _redact;
    private readonly Func<TokenUsage, decimal?>? _costEstimator;
    private readonly Func<string, bool>? _reserve;
    private readonly Action<string>? _dispatch;
    private readonly Action<string, decimal?, long>? _afterReceipt;
    private readonly Action<string>? _releaseBeforeDispatch;

    public MetaModelService(IModelProvider provider, IArtifactStore artifacts, IContextEventSink events,
        ModelSelection selection, Func<string, string>? redact = null, Func<TokenUsage, decimal?>? costEstimator = null,
        Func<string, bool>? reserve = null, Action<string>? dispatch = null,
        Action<string, decimal?, long>? afterReceipt = null, Action<string>? releaseBeforeDispatch = null)
    {
        _provider = provider;
        _artifacts = artifacts;
        _events = events;
        _selection = selection;
        _redact = redact ?? (text => text);
        _costEstimator = costEstimator;
        _reserve = reserve;
        _dispatch = dispatch;
        _afterReceipt = afterReceipt;
        _releaseBeforeDispatch = releaseBeforeDispatch;
    }

    public string Fingerprint(string operation)
    {
        var material = operation + "|" + _selection.Model + "|" + _selection.ContextBudget + "|" + _selection.ToolMode;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public async Task<string> SummarizeAsync(RunId runId, string operation, string content,
        int maxCharacters, CancellationToken cancellationToken)
    {
        var safeInput = _redact(content);
        var inputLimit = (int)Math.Clamp(_selection.ContextBudget * 4, 1024, int.MaxValue);
        if (safeInput.Length > inputLimit)
        {
            var half = Math.Max(1, (inputLimit - 48) / 2);
            safeInput = safeInput[..half] + "\n[…middle of older input omitted deterministically…]\n"
                + safeInput[^half..];
        }
        var modelFingerprint = Fingerprint(operation);
        var invocationId = Guid.NewGuid().ToString("N");
        var reserved = _reserve?.Invoke(invocationId) ?? false;
        if (_reserve is not null && !reserved) throw new InvalidOperationException("Meta invocation reservation denied.");
        var began = false;
        var dispatched = false;
        var safeToRelease = true;
        using var generationAttempts = GenerationRequestAttemptScope.Enter();

        var request = new ModelRequest(_selection,
            new ModelMessage[] { new(MessageRole.User, new ContentBlock[] { new TextBlock(
                "Summarize the following older conversation into durable facts, decisions, constraints, failed attempts, and unresolved questions. Do not invent information.\n\n"
                + safeInput) }) },
            "You are a deterministic context summarization service. Preserve uncertainty and redact secrets.",
            Array.Empty<ToolDefinition>(), ToolChoice.None(), null, null, null, null);
        TokenUsage? usage = null;
        var fields = TokenUsageFields.None;
        decimal? Cost() => usage is not null && fields.HasFlag(TokenUsageFields.Input | TokenUsageFields.Output)
            && !TokenUsageValidation.IsInvalid(usage, fields)
            ? _costEstimator?.Invoke(usage) : null;
        try
        {
            var input = _artifacts.PutText(safeInput, "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
            await _events.AppendAsync(new MetaModelInvocationStarted(invocationId, runId, operation,
                modelFingerprint, input), cancellationToken).ConfigureAwait(false);
            began = true;
            safeToRelease = false;
            cancellationToken.ThrowIfCancellationRequested();
            _dispatch?.Invoke(invocationId);
            dispatched = true;
            var result = "";
            await foreach (var evt in _provider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evt is ResponseCompleted completed)
                {
                    usage = completed.Response.Usage;
                    fields = completed.Response.ReportedUsageFields;
                    if (TokenUsageValidation.IsInvalid(usage, fields))
                        throw new InvalidDataException("Provider token usage is invalid.");
                    result = string.Join("\n", completed.Response.Content.OfType<TextBlock>().Select(b => b.Text));
                }
                else if (evt is ResponseFailed failed)
                    throw new InvalidOperationException("MetaModelService failed: " + failed.ErrorType);
            }

            result = _redact(result).Trim();
            if (result.Length == 0) throw new InvalidOperationException("MetaModelService returned an empty summary");
            if (result.Length > maxCharacters) result = result[..maxCharacters].TrimEnd() + "…";
            var output = _artifacts.PutText(result, "text/plain", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
            await _events.AppendAsync(new MetaModelInvocationCompleted(invocationId, runId, operation,
                modelFingerprint, output, usage, Cost(), fields), cancellationToken).ConfigureAwait(false);
            _afterReceipt?.Invoke(invocationId, Cost(), generationAttempts.ObservedSends);
            return result;
        }
        catch (Exception ex)
        {
            if (began)
            {
                if (!dispatched)
                {
                    await _events.AppendAsync(new MetaModelInvocationNotDispatched(invocationId, runId, operation,
                        modelFingerprint), CancellationToken.None).ConfigureAwait(false);
                    safeToRelease = true;
                }
                else
                {
                    await _events.AppendAsync(new MetaModelInvocationFailed(invocationId, runId, operation,
                        modelFingerprint, ex.GetType().Name, usage, Cost(), fields), CancellationToken.None).ConfigureAwait(false);
                    _afterReceipt?.Invoke(invocationId, Cost(), generationAttempts.ObservedSends);
                }
            }
            throw;
        }
        finally
        {
            if (reserved && !dispatched && safeToRelease) _releaseBeforeDispatch?.Invoke(invocationId);
        }
    }
}

/// <summary>Rutinas puras para poda/compresión y proyección del checkpoint.</summary>
public static class ContextCompaction
{
    public static bool ShouldCompact(int uncompactedOlderItems, ContextManagementPolicy policy) =>
        uncompactedOlderItems >= policy.CompactAfterItems;

    public static IReadOnlyList<ContextItem> PruneSupersededFileReads(IReadOnlyList<ContextItem> items,
        out IReadOnlyList<ContextDiagnostic> diagnostics)
    {
        var latest = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Kind != ContextItemKind.File) continue;
            var key = FileVersionKey(item.Provenance.Refs);
            if (key is not null) latest[key] = i;
        }

        var kept = new List<ContextItem>();
        var decisions = new List<ContextDiagnostic>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key = item.Kind == ContextItemKind.File ? FileVersionKey(item.Provenance.Refs) : null;
            var remove = key is not null && latest[key] != i;
            decisions.Add(new ContextDiagnostic(item.Id, item.Provenance,
                remove ? ContextDecision.Pruned : ContextDecision.Included, item.EstimatedTokens));
            if (!remove) kept.Add(item);
        }
        diagnostics = decisions;
        return kept;
    }

    public static ContextItem Compress(ContextItem item, int maxBodyCharacters)
    {
        if (maxBodyCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxBodyCharacters));
        if (item.Content.Length <= maxBodyCharacters) return item;
        const string marker = "…[older content compressed]…";
        var (structure, body) = SplitStructure(item.Content);
        var bodyLength = Math.Max(0, maxBodyCharacters - structure.Length - marker.Length);
        var excerpt = body[..Math.Min(bodyLength, body.Length)];
        return new ContextItem(item.Id, item.Kind, structure + excerpt + marker, 0, item.Priority,
            item.Retention, item.Provenance, item.PreserveWhenTrimming);
    }

    private static (string Structure, string Body) SplitStructure(string content)
    {
        var call = content.IndexOf("tool call ", StringComparison.OrdinalIgnoreCase);
        if (call >= 0)
        {
            var nameStart = call + "tool call ".Length;
            var nameEnd = nameStart;
            while (nameEnd < content.Length && !char.IsWhiteSpace(content[nameEnd])) nameEnd++;
            var end = nameEnd;
            while (end < content.Length && char.IsWhiteSpace(content[end])) end++;
            return (content[..end], content[end..]);
        }
        var result = content.IndexOf("tool result", StringComparison.OrdinalIgnoreCase);
        if (result >= 0)
        {
            var end = result + "tool result".Length;
            while (end < content.Length && char.IsWhiteSpace(content[end])) end++;
            return (content[..end], content[end..]);
        }
        var separator = content.IndexOf(':');
        if (separator >= 0 && separator < Math.Min(content.Length, 32))
        {
            var end = separator + 1;
            while (end < content.Length && char.IsWhiteSpace(content[end])) end++;
            return (content[..end], content[end..]);
        }
        return (string.Empty, content);
    }

    public static string? FileVersionKey(IReadOnlyList<string>? refs)
    {
        if (refs is null) return null;
        string? path = null, version = null;
        foreach (var value in refs)
        {
            if (value.StartsWith("path=", StringComparison.Ordinal)) path = value[5..];
            if (value.StartsWith("version=", StringComparison.Ordinal)) version = value[8..];
        }
        return path is null || version is null ? null : path + "|" + version;
    }
}
