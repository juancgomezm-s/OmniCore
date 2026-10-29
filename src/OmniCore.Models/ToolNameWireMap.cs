namespace OmniCore.Models;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Codex-style, provider-safe, request-scoped aliases for canonical tool names.</summary>
internal sealed class ToolNameWireMap
{
    // Use one vocabulary across providers. These are names for OmniCore function tools,
    // not declarations of OpenAI built-in tools: their advertised JSON schemas still apply.
    // Keep shell commands distinct from literal argv and persistent process operations.
    private static readonly IReadOnlyDictionary<string, string> PreferredAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["filesystem.read"] = "read_file",
            ["filesystem.list"] = "list_dir",
            ["filesystem.search"] = "grep_files",
            ["filesystem.patch"] = "apply_patch",
            ["filesystem.write"] = "write_file",
            ["media.read"] = "view_image",
            ["shell.exec"] = "exec_command",
            ["process.exec"] = "exec_process",
            ["process.start"] = "start_process",
            ["process.read"] = "read_process_output",
            ["process.send_input"] = "write_stdin",
            ["process.stop"] = "stop_process",
            ["plan.propose"] = "update_plan",
            ["user.ask"] = "request_user_input",
            ["tool.search"] = "tool_search",
            ["search.web"] = "web_search",
            ["web.read"] = "web_fetch",
            ["artifact.read"] = "read_artifact",
            ["reference.resolve"] = "resolve_reference",
            ["skill.load"] = "load_skill",
            ["memory.propose"] = "propose_memory",
            ["mode.propose"] = "propose_mode",
            ["companion.pet"] = "companion_pet",
            ["core.agents.mailbox.receive"] = "receive_agent_messages",
            ["worktree.integrate"] = "integrate_worktree",
            ["worktree.cleanup"] = "cleanup_worktree",
            ["worktree.discard"] = "discard_worktree",
            ["worktree.retention"] = "set_worktree_retention",
            ["extension.batch.apply"] = "apply_extension_batch",
        };

    private static readonly Regex CanonicalReference = new(
        @"(?<![a-zA-Z0-9_.-])(?:" + string.Join("|", PreferredAliases.Keys.Select(Regex.Escape))
            + @")(?![a-zA-Z0-9_-]|\.[a-zA-Z0-9_-])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private readonly IReadOnlyDictionary<string, string> _toWire;
    private readonly IReadOnlyDictionary<string, string> _toCanonical;

    private ToolNameWireMap(Dictionary<string, string> toWire)
    {
        _toWire = toWire;
        _toCanonical = toWire.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    }

    public static ToolNameWireMap Create(IEnumerable<string> canonicalNames, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var names = canonicalNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(names.Where(name => IsValid(name, maximumLength)), StringComparer.Ordinal);
        foreach (var name in names.Where(name => IsValid(name, maximumLength))) map.Add(name, name);
        foreach (var name in names.Where(name => !IsValid(name, maximumLength)))
        {
            var readable = PreferredAliases.TryGetValue(name, out var preferred) ? preferred
                : new string(name.Select(character => IsValidCharacter(character) ? character : '_').ToArray());
            if (readable.Length == 0 || readable.All(character => character == '_')) readable = "tool";
            if (readable.Length <= maximumLength && used.Add(readable))
            {
                map.Add(name, readable);
                continue;
            }

            // Preserve the operation name; use a short digest only for truncation or collisions.
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
            var counter = 0;
            string alias;
            do
            {
                var suffix = counter == 0 ? string.Empty : "_" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var hashLength = Math.Min(12, maximumLength - 1 - suffix.Length);
                if (hashLength < 1) throw new ArgumentOutOfRangeException(nameof(maximumLength));
                var prefixLength = Math.Min(readable.Length, maximumLength - hashLength - 1 - suffix.Length);
                alias = readable[..prefixLength] + "_" + digest[..hashLength] + suffix;
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

    // Rewrite trusted instructions/descriptions only, never user text, arguments, or results.
    // Use this request's allocation so even a colliding alias in a description stays callable.
    public string ToWireText(string text) => CanonicalReference.Replace(text, match => ToWire(match.Value));

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
        && name.All(IsValidCharacter);

    private static bool IsValidCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '-';
}
