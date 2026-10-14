namespace OmniCore.Host;

using System.Text;
using System.Text.Json;
using OmniCore.Engine;
using OmniCore.Extensions;

/// <summary>Runs declarative, read-only package validators at the normal Run completion gate.</summary>
public sealed class SkillCompletionValidator
{
    private const int MaximumValidators = 32;
    private const int MaximumFileBytes = 1_048_576;
    private readonly IReadOnlyList<Rule> _rules;
    private readonly IReadOnlyList<ExtensionCompletionValidator> _extensionValidators;

    public SkillCompletionValidator(IEnumerable<LoadedSkill> skills,
        IEnumerable<ExtensionCompletionValidator>? extensionValidators = null)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var rules = new List<Rule>();
        foreach (var skill in skills)
        foreach (var resource in skill.Resources ?? [])
        {
            if (resource.Kind != SkillResourceKind.Validator) continue;
            if (rules.Count == MaximumValidators)
                throw new InvalidDataException("The active skills declare too many validators.");
            rules.Add(Parse(skill, resource));
        }
        _rules = rules.AsReadOnly();
        _extensionValidators = extensionValidators?.ToArray() ?? [];
    }

    public bool HasValidators => _rules.Count > 0 || _extensionValidators.Count > 0;

    internal IEnumerable<(string HookId, string ResultKey)> HookValidationIdentities() =>
        _rules.Select(rule => ($"skill.{rule.SkillId}.{rule.Id}", $"skill:{rule.SkillId}:{rule.Id}"))
            .Concat(_extensionValidators.SelectMany(validator => validator.HookValidationIdentities()))
            .ToArray();

    public IReadOnlyList<ExternalCompletionGateResult> Run(string workspaceRoot,
        Func<string, SkillValidationReadResult?> readThroughPolicy, CancellationToken cancellationToken = default,
        string? boundedRunContextJson = null, IReadOnlySet<string>? requestedHookIds = null,
        IReadOnlySet<string>? excludedHookIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(readThroughPolicy);
        var root = Path.GetFullPath(workspaceRoot);
        var results = new List<ExternalCompletionGateResult>(_rules.Count);
        var effectiveRequested = requestedHookIds;
        if (effectiveRequested is null && excludedHookIds is { Count: > 0 })
            effectiveRequested = HookValidationIdentities().Select(identity => identity.HookId)
                .Where(id => !excludedHookIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in effectiveRequested is null ? _rules : _rules.Where(rule =>
            effectiveRequested.Contains($"skill.{rule.SkillId}.{rule.Id}")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePath(root, rule.Path);
            var passed = false;
            if (path is not null)
            {
                try
                {
                    var relative = Path.GetRelativePath(root, path);
                    var read = readThroughPolicy(relative);
                    if (read is { Allowed: true })
                    {
                        if (rule.Kind == "file-exists") passed = true;
                        else passed = read.Content is { Length: <= MaximumFileBytes } content
                            && content.Contains(rule.Text!, StringComparison.Ordinal);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or DecoderFallbackException or ArgumentException or InvalidOperationException) { passed = false; }
            }
            results.Add(new("skill:" + rule.SkillId + ":" + rule.Id, passed,
                passed ? "Skill validator passed." : "Skill validator failed.", ValidatorId:
                    $"skill.{rule.SkillId}.{rule.Id}"));
        }
        foreach (var validator in _extensionValidators)
            results.AddRange(validator.Run(cancellationToken, boundedRunContextJson, effectiveRequested));
        return results;
    }

    private static Rule Parse(LoadedSkill skill, LoadedSkillResource resource)
    {
        try
        {
            using var document = JsonDocument.Parse(resource.Content, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("kind", out var kindNode) || kindNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("A skill validator must declare id, kind, and path.");
            var id = idNode.GetString()!;
            var kind = kindNode.GetString()!;
            var path = pathNode.GetString()!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{0,63}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                || kind is not ("file-exists" or "file-contains") || !SafeRelativePath(path))
                throw new InvalidDataException("A skill validator contains an unsupported identity or rule.");
            string? text = null;
            if (kind == "file-contains")
            {
                if (!root.TryGetProperty("text", out var textNode) || textNode.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(text = textNode.GetString()) || text.Length > 512)
                    throw new InvalidDataException("A file-contains validator requires bounded text.");
            }
            var allowed = kind == "file-contains"
                ? new[] { "id", "kind", "path", "text" } : new[] { "id", "kind", "path" };
            if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
                throw new InvalidDataException("A skill validator contains an unknown field.");
            return new(skill.Skill.Manifest.Id!, id, kind, path, text);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A skill validator is malformed.", exception);
        }
    }

    private static bool SafeRelativePath(string value)
    {
        var normalized = value.Replace('\\', '/');
        return value.Length is > 0 and <= 240 && !value.Any(char.IsControl) && !Path.IsPathRooted(value)
            && !normalized.Split('/').Any(part => part is "" or "." or "..");
    }

    private static string? ResolvePath(string root, string relative)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)));
            return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? full : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    private sealed record Rule(string SkillId, string Id, string Kind, string Path, string? Text);
}

public sealed record SkillValidationReadResult(bool Allowed, string? Content);
