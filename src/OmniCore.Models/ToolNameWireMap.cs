namespace OmniCore.Models;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Provider-safe, request-scoped aliases for canonical tool names.</summary>
internal sealed class ToolNameWireMap
{
    private readonly IReadOnlyDictionary<string, string> _toWire;
    private readonly IReadOnlyDictionary<string, string> _toCanonical;

    private ToolNameWireMap(Dictionary<string, string> toWire)
    {
        _toWire = toWire;
        _toCanonical = toWire.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    }

    public static ToolNameWireMap Create(IEnumerable<string> canonicalNames, int maximumLength)
    {
        var names = canonicalNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(names.Where(name => IsValid(name, maximumLength)), StringComparer.Ordinal);
        foreach (var name in names.Where(name => IsValid(name, maximumLength))) map.Add(name, name);
        foreach (var name in names.Where(name => !IsValid(name, maximumLength)))
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
            var counter = 0;
            string alias;
            do
            {
                var suffix = counter == 0 ? string.Empty : "_" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var prefix = "omni_";
                var hashLength = Math.Min(digest.Length, maximumLength - prefix.Length - suffix.Length);
                if (hashLength < 1) throw new ArgumentOutOfRangeException(nameof(maximumLength));
                alias = prefix + digest[..hashLength] + suffix;
                counter++;
            } while (used.Contains(alias));
            used.Add(alias);
            map.Add(name, alias);
        }
        return new ToolNameWireMap(map);
    }

    public static ToolNameWireMap ForRequest(ModelRequest request, int maximumLength) =>
        Create(request.Tools.Select(tool => tool.Name)
            .Concat(request.Messages.SelectMany(message => message.Content.OfType<ToolCallBlock>()
                .Select(call => call.ToolName)))
            .Concat(request.ToolChoice.Mode == "exact" && request.ToolChoice.ToolName is { } exact
                ? new[] { exact } : Array.Empty<string>()), maximumLength);

    public string ToWire(string canonicalName) => _toWire.TryGetValue(canonicalName, out var alias)
        ? alias : canonicalName;

    public string ToCanonical(string wireName) => _toCanonical.TryGetValue(wireName, out var name)
        ? name : wireName;

    public ModelStreamEvent Restore(ModelStreamEvent item) => item switch
    {
        BlockCompleted completed when completed.Block is ToolCallBlock call =>
            completed with { Block = call with { ToolName = ToCanonical(call.ToolName) } },
        ResponseCompleted completed => completed with
        {
            Response = completed.Response with
            {
                Content = completed.Response.Content.Select(block => block is ToolCallBlock call
                    ? (ContentBlock)(call with { ToolName = ToCanonical(call.ToolName) }) : block).ToArray(),
            },
        },
        _ => item,
    };

    private static bool IsValid(string name, int maximumLength) =>
        name.Length is > 0 && name.Length <= maximumLength
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
