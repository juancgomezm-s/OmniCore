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
        return ReadAll(session).Where(pair => pair.Key.Scope == ScopeLevel.Project)
            .ToDictionary(pair => pair.Key.Id, pair => pair.Value, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<SkillActivationIdentity, string> ReadAll(SessionId session)
        => ReadDetailed(session).ToDictionary(pair => pair.Key, pair => pair.Value.ManifestSha256);

    public IReadOnlyDictionary<SkillActivationIdentity, SkillActivationRecord> ReadDetailed(SessionId session)
    {
        var path = PathFor(session);
        lock (Gate(path))
        {
            if (!File.Exists(path)) return new Dictionary<SkillActivationIdentity, SkillActivationRecord>();
            var result = new Dictionary<SkillActivationIdentity, SkillActivationRecord>();
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var fields = line.Split('|');
                ScopeLevel scope;
                string encodedId;
                string manifestHash;
                string contentHash;
                string origin;
                if (fields.Length == 2 && TryDecode(fields[0], out var legacyId))
                {
                    scope = ScopeLevel.Project;
                    encodedId = fields[0];
                    manifestHash = fields[1];
                    contentHash = fields[1];
                    origin = "User";
                }
                else if (fields.Length == 3 && Enum.TryParse(fields[0], ignoreCase: false, out scope)
                    && IsSupportedScope(scope))
                {
                    encodedId = fields[1];
                    manifestHash = fields[2];
                    contentHash = fields[2];
                    origin = "User";
                }
                else if (fields.Length == 4 && Enum.TryParse(fields[0], ignoreCase: false, out scope)
                    && IsSupportedScope(scope))
                {
                    encodedId = fields[1];
                    manifestHash = fields[2];
                    contentHash = fields[3];
                    origin = "User";
                }
                else if (fields.Length == 5 && Enum.TryParse(fields[0], ignoreCase: false, out scope)
                    && IsSupportedScope(scope) && fields[4] is "User" or "Model" or "Automatic")
                {
                    encodedId = fields[1];
                    manifestHash = fields[2];
                    contentHash = fields[3];
                    origin = fields[4];
                }
                else throw new InvalidDataException("The session skill activation store is malformed.");
                if (!TryDecode(encodedId, out var id) || !IsSha256(manifestHash) || !IsSha256(contentHash)
                    || !result.TryAdd(new(scope, id), new(manifestHash, contentHash, origin)))
                    throw new InvalidDataException("The session skill activation store is malformed.");
            }
            return result;
        }
    }

    public string Revision(SessionId session)
    {
        var activations = ReadDetailed(session).OrderBy(pair => pair.Key).ToArray();
        var suppressions = ReadAutomaticSuppressions(session).OrderBy(item => item.Scope)
            .ThenBy(item => item.SkillId, StringComparer.Ordinal).ThenBy(item => item.ManifestSha256, StringComparer.Ordinal)
            .ThenBy(item => item.PackageContentSha256, StringComparer.Ordinal).ToArray();
        var canonical = new StringBuilder("session-skill-state-v1\n")
            .Append(session).Append('\n');
        foreach (var pair in activations)
            canonical.Append("active|").Append(pair.Key.Scope).Append('|')
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key.Id))).Append('|')
                .Append(pair.Value.ManifestSha256).Append('|').Append(pair.Value.PackageContentSha256).Append('|')
                .Append(pair.Value.Origin).Append('\n');
        foreach (var suppression in suppressions)
            canonical.Append("suppressed|").Append(suppression.Scope).Append('|')
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(suppression.SkillId))).Append('|')
                .Append(suppression.ManifestSha256).Append('|').Append(suppression.PackageContentSha256).Append('\n');
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public IReadOnlyList<SkillAutomaticSuppression> ReadAutomaticSuppressions(SessionId session)
    {
        var path = SuppressionPathFor(session);
        lock (Gate(path))
        {
            if (!File.Exists(path)) return [];
            var result = new List<SkillAutomaticSuppression>();
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var fields = line.Split('|');
                if (fields.Length != 4 || !Enum.TryParse(fields[0], ignoreCase: false, out ScopeLevel scope)
                    || !IsSupportedScope(scope) || !TryDecode(fields[1], out var id)
                    || !IsSha256(fields[2]) || !IsSha256(fields[3]))
                    throw new InvalidDataException("The Session skill suppression store is malformed.");
                result.Add(new(scope, id, fields[2], fields[3]));
            }
            if (result.Distinct().Count() != result.Count)
                throw new InvalidDataException("The Session skill suppression store contains duplicates.");
            return result;
        }
    }

    public void Activate(SessionId session, string skillId, string manifestSha256)
        => Activate(session, ScopeLevel.Project, skillId, manifestSha256);

    public void Activate(SessionId session, ScopeLevel scope, string skillId, string manifestSha256)
        => Activate(session, scope, skillId, manifestSha256, manifestSha256);

    public void Activate(SessionId session, ScopeLevel scope, string skillId, string manifestSha256,
        string packageContentSha256)
        => Activate(session, scope, skillId, manifestSha256, packageContentSha256, "User");

    public void ActivateAutomatic(SessionId session, ScopeLevel scope, string skillId, string manifestSha256,
        string packageContentSha256) => Activate(session, scope, skillId, manifestSha256,
            packageContentSha256, "Automatic");

    public void Activate(SessionId session, ScopeLevel scope, string skillId, string manifestSha256,
        string packageContentSha256, string origin)
    {
        if (!IsSupportedScope(scope) || !IsSkillId(skillId) || !IsSha256(manifestSha256)
            || !IsSha256(packageContentSha256) || origin is not ("User" or "Model" or "Automatic"))
            throw new ArgumentException("The session skill activation identity is invalid.");
        Mutate(session, items => items[new(scope, skillId)] = new(manifestSha256, packageContentSha256, origin));
        ClearAutomaticSuppression(session, scope, skillId);
    }

    public bool Release(SessionId session, string skillId)
        => Release(session, ScopeLevel.Project, skillId);

    public bool Release(SessionId session, ScopeLevel scope, string skillId)
    {
        if (!IsSupportedScope(scope) || !IsSkillId(skillId)) throw new ArgumentException("The skill id is invalid.", nameof(skillId));
        var removed = false;
        Mutate(session, items => removed = items.Remove(new(scope, skillId)));
        return removed;
    }

    public bool IsAutomaticSuppressed(SessionId session, ScopeLevel scope, string skillId,
        string manifestSha256, string packageContentSha256)
    {
        var path = SuppressionPathFor(session);
        lock (Gate(path))
        {
            if (!File.Exists(path)) return false;
            return File.ReadAllLines(path, Encoding.UTF8).Any(line => line == SuppressionLine(scope,
                skillId, manifestSha256, packageContentSha256));
        }
    }

    public bool HasAutomaticSuppression(SessionId session, ScopeLevel scope, string skillId,
        string manifestSha256)
    {
        var path = SuppressionPathFor(session);
        if (!File.Exists(path)) return false;
        var prefix = scope + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(skillId))
            + "|" + manifestSha256 + "|";
        lock (Gate(path)) return File.ReadAllLines(path, Encoding.UTF8)
            .Any(line => line.StartsWith(prefix, StringComparison.Ordinal));
    }

    public void SuppressAutomatic(SessionId session, ScopeLevel scope, string skillId,
        string manifestSha256, string packageContentSha256)
    {
        if (!IsSupportedScope(scope) || !IsSkillId(skillId) || !IsSha256(manifestSha256)
            || !IsSha256(packageContentSha256)) throw new ArgumentException("The automatic skill identity is invalid.");
        var path = SuppressionPathFor(session);
        lock (Gate(path))
        {
            Directory.CreateDirectory(_root);
            var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            lines.Add(SuppressionLine(scope, skillId, manifestSha256, packageContentSha256));
            WriteAtomic(path, lines.Order(StringComparer.Ordinal));
        }
    }

    public void ClearAutomaticSuppression(SessionId session, ScopeLevel scope, string skillId)
    {
        var path = SuppressionPathFor(session);
        lock (Gate(path))
        {
            if (!File.Exists(path)) return;
            var prefix = scope + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(skillId)) + "|";
            var lines = File.ReadAllLines(path, Encoding.UTF8).Where(line => !line.StartsWith(prefix, StringComparison.Ordinal));
            WriteAtomic(path, lines);
        }
    }

    private void Mutate(SessionId session, Action<Dictionary<SkillActivationIdentity, SkillActivationRecord>> mutation)
    {
        var path = PathFor(session);
        lock (Gate(path))
        {
            var items = File.Exists(path) ? ReadDetailed(session).ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<SkillActivationIdentity, SkillActivationRecord>();
            mutation(items);
            Directory.CreateDirectory(_root);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                WriteAtomic(temporary, items.OrderBy(pair => pair.Key)
                    .Select(pair => pair.Key.Scope + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key.Id))
                        + "|" + pair.Value.ManifestSha256 + "|" + pair.Value.PackageContentSha256 + "|" + pair.Value.Origin));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private string PathFor(SessionId session) => Path.Combine(_root, session + ".txt");
    private string SuppressionPathFor(SessionId session) => Path.Combine(_root, session + ".suppressed.txt");
    private static string SuppressionLine(ScopeLevel scope, string id, string manifestHash, string packageHash) =>
        scope + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(id)) + "|" + manifestHash + "|" + packageHash;
    private static void WriteAtomic(string path, IEnumerable<string> lines)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllLines(temporary, lines, new UTF8Encoding(false)); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static object Gate(string path) => Gates.GetOrAdd(path, static _ => new object());
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsSupportedScope(ScopeLevel scope) => scope is ScopeLevel.BuiltIn or ScopeLevel.User
        or ScopeLevel.Project or ScopeLevel.Workspace or ScopeLevel.Session;
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

public sealed record SkillActivationRecord(string ManifestSha256, string PackageContentSha256, string Origin = "User");

public sealed record SkillAutomaticSuppression(ScopeLevel Scope, string SkillId,
    string ManifestSha256, string PackageContentSha256);

public sealed record SkillActivationIdentity(ScopeLevel Scope, string Id) : IComparable<SkillActivationIdentity>
{
    public int CompareTo(SkillActivationIdentity? other)
    {
        if (other is null) return 1;
        var scope = Scope.CompareTo(other.Scope);
        return scope != 0 ? scope : string.Compare(Id, other.Id, StringComparison.Ordinal);
    }
}
