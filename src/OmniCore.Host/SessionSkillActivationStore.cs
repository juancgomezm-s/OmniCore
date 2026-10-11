namespace OmniCore.Host;

using System.Collections.Concurrent;
using System.Text;
using OmniCore.Domain;

/// <summary>Durable Session-scoped skill selection stored beside Workspace runtime data, never in project files.</summary>
public sealed class SessionSkillActivationStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;

    public SessionSkillActivationStore(string workspaceDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDataDirectory);
        _root = Path.Combine(Path.GetFullPath(workspaceDataDirectory), "session-skills");
    }

    public IReadOnlyDictionary<string, string> Read(SessionId session)
    {
        var path = PathFor(session);
        lock (Gate(path))
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var fields = line.Split('|');
                if (fields.Length != 2 || !TryDecode(fields[0], out var id)
                    || !IsSha256(fields[1]) || !result.TryAdd(id, fields[1]))
                    throw new InvalidDataException("The session skill activation store is malformed.");
            }
            return result;
        }
    }

    public void Activate(SessionId session, string skillId, string manifestSha256)
    {
        if (!IsSkillId(skillId) || !IsSha256(manifestSha256))
            throw new ArgumentException("The session skill activation identity is invalid.");
        Mutate(session, items => items[skillId] = manifestSha256);
    }

    public bool Release(SessionId session, string skillId)
    {
        if (!IsSkillId(skillId)) throw new ArgumentException("The skill id is invalid.", nameof(skillId));
        var removed = false;
        Mutate(session, items => removed = items.Remove(skillId));
        return removed;
    }

    private void Mutate(SessionId session, Action<Dictionary<string, string>> mutation)
    {
        var path = PathFor(session);
        lock (Gate(path))
        {
            var items = File.Exists(path) ? Read(session).ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
            mutation(items);
            Directory.CreateDirectory(_root);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllLines(temporary, items.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)) + "|" + pair.Value),
                    new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private string PathFor(SessionId session) => Path.Combine(_root, session + ".txt");
    private static object Gate(string path) => Gates.GetOrAdd(path, static _ => new object());
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsSkillId(string value) => !string.IsNullOrWhiteSpace(value)
        && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9]+(?:[.-][a-z0-9]+)*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool TryDecode(string value, out string id)
    {
        try
        {
            id = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return IsSkillId(id);
        }
        catch (FormatException)
        {
            id = "";
            return false;
        }
    }
}
