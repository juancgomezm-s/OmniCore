namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmniCore.Abstractions;
using OmniCore.Infrastructure;

public sealed record McpConfigurationRow(string Id, string Transport, string Scope, string Identity,
    string Command, string[] Arguments, string WorkingDirectory, string Endpoint, bool Trusted, bool HasCredential);
public sealed record McpPreferencesSnapshot(string Revision, IReadOnlyList<McpConfigurationRow> Servers);
public sealed record McpConfigurationDraft(string Id, string Transport, string Command, string[] Arguments,
    string WorkingDirectory, string Endpoint);

/// <summary>Explicit human configuration operations. Reading/saving never probes or launches a server.</summary>
public interface IMcpPreferencesFacade
{
    McpPreferencesSnapshot Read();
    McpPreferencesSnapshot Save(string revision, string? existingId, McpConfigurationDraft draft);
    McpPreferencesSnapshot Remove(string revision, string id);
    McpPreferencesSnapshot SetTrust(string revision, string id, bool trusted);
    McpPreferencesSnapshot SetCredential(string revision, string id, string secret);
}

public sealed class McpPreferencesHost(IPlatformPaths paths, string workspaceRoot,
    ICredentialStore? credentials = null) : IMcpPreferencesFacade
{
    private readonly string _workspace = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot);
    private string UserFile => Path.Combine(paths.ConfigDirectory, "mcp", "servers.json");
    private string ProjectFile => Path.Combine(_workspace, ".omnicore", "mcp.servers.json");
    private McpServerStore Store => new(paths, _workspace, new WorkspaceTrustStore(paths).IsTrusted(_workspace));

    public static McpPreferencesHost Create(string workspaceRoot, string? dataDirectory = null,
        string? configDirectory = null) => new(new DefaultPlatformPaths(dataDirectory, configDirectory), workspaceRoot);

    public McpPreferencesSnapshot Read()
    {
        var revision = Revision();
        var rows = Store.List().Select(server => new McpConfigurationRow(server.Id, server.Transport, server.Scope,
            server.Identity, server.Command ?? "", server.Args, server.WorkingDirectory ?? "", server.Endpoint ?? "",
            server.Trust != OmniCore.Domain.TrustLevel.Untrusted, server.AuthRef is not null)).ToArray();
        if (revision != Revision()) throw new InvalidOperationException("mcpPreferences.stale");
        return new(revision, rows);
    }

    public McpPreferencesSnapshot Save(string revision, string? existingId, McpConfigurationDraft draft)
    {
        using var lease = ConfigurationWriteLease.Acquire(UserFile);
        CheckRevision(revision);
        var entries = Entries();
        var index = existingId is null ? -1 : UserIndex(entries, existingId);
        if (existingId is not null && draft.Id != existingId) throw new ArgumentException("mcpPreferences.invalid");
        if (draft.Arguments.Length > 64 || draft.Arguments.Any(arg => arg is null || arg.Length > 4096))
            throw new ArgumentException("mcpPreferences.invalid");
        if (Store.List().Any(server => server.Id == draft.Id && server.Id != existingId))
            throw new ArgumentException("mcpPreferences.duplicate");
        var entry = index < 0 ? new JsonObject() : (JsonObject)entries[index]!.DeepClone();
        entry["id"] = draft.Id; entry["transport"] = draft.Transport;
        // Preserve advanced settings and credential references when editing the same transport.
        var changedTransport = index >= 0 && entries[index]!["transport"]!.GetValue<string>() != draft.Transport;
        if (changedTransport) { entry.Remove("authRef"); entry.Remove("web"); }
        if (draft.Transport == "stdio")
        {
            if (!Path.IsPathFullyQualified(draft.Command)) throw new ArgumentException("mcpPreferences.executable");
            entry.Remove("endpoint"); entry.Remove("allowedHosts"); entry.Remove("authRef");
            entry["command"] = Path.GetFullPath(draft.Command);
            entry["args"] = new JsonArray(draft.Arguments.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray());
            if (string.IsNullOrWhiteSpace(draft.WorkingDirectory)) entry.Remove("workingDirectory");
            else
            {
                if (!Path.IsPathFullyQualified(draft.WorkingDirectory)) throw new ArgumentException("mcpPreferences.invalid");
                entry["workingDirectory"] = Path.GetFullPath(draft.WorkingDirectory);
            }
        }
        else if (draft.Transport == "http")
        {
            var endpoint = McpEndpoint.Validate(draft.Endpoint);
            entry.Remove("command"); entry.Remove("args"); entry.Remove("workingDirectory");
            entry["endpoint"] = endpoint.AbsoluteUri;
            // An edit cannot silently retain authority to another destination.
            entry["allowedHosts"] = new JsonArray(JsonValue.Create(endpoint.Authority));
        }
        else throw new ArgumentException("mcpPreferences.invalid");
        if (index < 0) entries.Add((JsonNode)entry); else entries[index] = entry;
        var bytes = Validate(entries);
        CheckRevision(revision);
        if (existingId is not null) Store.TrustUnderWriteLease(existingId, false);
        Publish(bytes);
        return Read();
    }

    public McpPreferencesSnapshot Remove(string revision, string id)
    {
        using var lease = ConfigurationWriteLease.Acquire(UserFile);
        CheckRevision(revision);
        var entries = Entries(); entries.RemoveAt(UserIndex(entries, id));
        var bytes = Validate(entries);
        Store.TrustUnderWriteLease(id, false);
        Publish(bytes);
        return Read();
    }

    public McpPreferencesSnapshot SetTrust(string revision, string id, bool trusted)
    {
        using var lease = ConfigurationWriteLease.Acquire(UserFile);
        CheckRevision(revision);
        Store.TrustUnderWriteLease(id, trusted);
        return Read();
    }

    public McpPreferencesSnapshot SetCredential(string revision, string id, string secret)
    {
        using var lease = ConfigurationWriteLease.Acquire(UserFile);
        CheckRevision(revision);
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 16384) throw new ArgumentException("mcpPreferences.invalid");
        var entries = Entries(); var index = UserIndex(entries, id);
        if (entries[index]!["transport"]!.GetValue<string>() != "http") throw new ArgumentException("mcpPreferences.invalid");
        var reference = "mcp:" + id + ":auth:" + Guid.NewGuid().ToString("N");
        entries[index]!["authRef"] = reference;
        var bytes = Validate(entries);
        var store = credentials ?? OmniHost.CreateUserCredentialStore(paths);
        store.Save(reference, secret, CancellationToken.None);
        try { CheckRevision(revision); Store.TrustUnderWriteLease(id, false); Publish(bytes); }
        catch { store.Delete(reference, CancellationToken.None); throw; }
        return Read();
    }

    private static int UserIndex(JsonArray entries, string id)
    {
        var matches = entries.Select((entry, index) => (entry, index))
            .Where(pair => pair.entry?["id"]?.GetValue<string>() == id).ToArray();
        return matches.Length == 1 ? matches[0].index : throw new InvalidOperationException("mcpPreferences.user_only");
    }

    private JsonArray Entries() => JsonNode.Parse(ReadFile(UserFile)) as JsonArray
        ?? throw new InvalidDataException("mcpPreferences.invalid");

    private static byte[] ReadFile(string file)
    {
        ConfigurationWriteLease.EnsureNoLinks(file);
        return File.Exists(file) ? File.ReadAllBytes(file) : "[]"u8.ToArray();
    }

    private string Revision()
    {
        var user = Convert.ToHexString(SHA256.HashData(ReadFile(UserFile)));
        var project = Convert.ToHexString(SHA256.HashData(ReadFile(ProjectFile)));
        // Include trust receipts: a stale open form must not undo a later trust decision.
        var servers = Store.List();
        var trust = string.Join("\n", servers.Select(server => server.Id + ":" + server.EffectiveIdentity));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + project + trust)));
    }

    private void CheckRevision(string expected)
    { if (expected != Revision()) throw new InvalidOperationException("mcpPreferences.stale"); }

    private byte[] Validate(JsonArray entries)
    {
        using var original = JsonDocument.Parse(ReadFile(UserFile), new JsonDocumentOptions { MaxDepth = 16 });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                // Identity/consent hashes use the exact object JSON. Keep untouched entries byte-for-byte,
                // including formatting, so editing one server never revokes another server's consent.
                var unchanged = original.RootElement.EnumerateArray().FirstOrDefault(item =>
                    item.TryGetProperty("id", out var id) && id.GetString() == entry?["id"]?.GetValue<string>()
                    && JsonNode.DeepEquals(JsonNode.Parse(item.GetRawText()), entry));
                if (unchanged.ValueKind != JsonValueKind.Undefined) writer.WriteRawValue(unchanged.GetRawText());
                else entry!.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
        var bytes = stream.ToArray();
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (entries.Count > 64) throw new InvalidDataException("mcpPreferences.invalid");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in json.RootElement.EnumerateArray())
            if (!ids.Add(McpServerDefinition.Parse(entry, "user", true).Id))
                throw new ArgumentException("mcpPreferences.duplicate");
        return bytes;
    }

    private void Publish(byte[] bytes)
    {
        var temporary = UserFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, UserFile, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
