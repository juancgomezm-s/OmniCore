namespace OmniCore.Security;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Grant store del usuario, dentro del directorio de datos del WorkspaceId (ADR-0037 §5).
/// El formato es texto delimitado con campos Base64; no contiene rutas ni valores de secretos.
/// Cada cambio se reemplaza atómicamente y se audita.
/// </summary>
public sealed class FilePermissionGrantStore : IPermissionGrantStore
{
    private readonly string _file;
    private readonly string _auditFile;
    private readonly IAuditSink _audit;
    private readonly object _gate = new();

    public FilePermissionGrantStore(string workspaceDataDirectory, IAuditSink audit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDataDirectory);
        _file = Path.Combine(workspaceDataDirectory, "permission-grants.tsv");
        _auditFile = Path.Combine(workspaceDataDirectory, "permission-grants.audit.tsv");
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public IReadOnlyList<PermissionGrantRecord> List(WorkspaceId workspace, RunId? run = null)
    {
        lock (_gate)
            return Read().Where(g => g.Workspace.Equals(workspace)
                && (g.Lifetime == GrantLifetime.Workspace
                    || g.Lifetime == GrantLifetime.Run && run is not null && g.Run?.Equals(run) == true))
                .ToArray();
    }

    public PermissionGrantRecord? Find(WorkspaceId workspace, RunId? run, string toolId, string claimsKey)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return Read().FirstOrDefault(g => g.Workspace.Equals(workspace)
                && g.ToolId.Equals(toolId, StringComparison.Ordinal)
                && g.ClaimsKey.Equals(claimsKey, StringComparison.Ordinal)
                && (g.Lifetime == GrantLifetime.Workspace
                    || g.Lifetime == GrantLifetime.Run && run is not null && g.Run?.Equals(run) == true)
                && g.CreatedAt <= now);
        }
    }

    public void Add(PermissionGrantRecord grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        cancellationToken.ThrowIfCancellationRequested();
        if (grant.ClaimsKey.Length != 64 || !grant.ClaimsKey.All(Uri.IsHexDigit))
            throw new ArgumentException("El fingerprint de claims debe ser SHA-256 hexadecimal.", nameof(grant));
        if (grant.Lifetime is not (GrantLifetime.Run or GrantLifetime.Workspace))
            throw new ArgumentException("Solo se almacenan grants persistentes.", nameof(grant));
        lock (_gate)
        {
            var grants = Read().Where(g => g.Id != grant.Id).Append(grant).ToArray();
            Write(grants);
        }
        AppendAudit("created", grant);
        _audit.Record(new AuditRecord("permission.grant.created", grant.Workspace, null, grant.Run,
            DateTimeOffset.UtcNow, grant.Id.ToString(), new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tool"] = grant.ToolId,
                ["lifetime"] = grant.Lifetime.ToString(),
                ["claimsKey"] = grant.ClaimsKey,
            }), cancellationToken);
    }

    public bool Revoke(WorkspaceId workspace, GrantId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PermissionGrantRecord? revoked;
        lock (_gate)
        {
            var grants = Read();
            revoked = grants.FirstOrDefault(g => g.Workspace.Equals(workspace) && g.Id.Equals(id));
            if (revoked is null) return false;
            Write(grants.Where(g => !g.Id.Equals(id)).ToArray());
        }
        AppendAudit("revoked", revoked);
        _audit.Record(new AuditRecord("permission.grant.revoked", workspace, null, revoked.Run,
            DateTimeOffset.UtcNow, id.ToString(), new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tool"] = revoked.ToolId,
                ["lifetime"] = revoked.Lifetime.ToString(),
                ["claimsKey"] = revoked.ClaimsKey,
            }), cancellationToken);
        return true;
    }

    /// <summary>Fingerprint determinista y sin contenido sensible de la reclamación exacta.</summary>
    public static string ClaimsKey(ToolIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var claims = intent.Claims;
        var text = new StringBuilder();
        Add("tool", intent.ToolId.ToString());
        foreach (var value in claims.Reads.OrderBy(v => v, StringComparer.Ordinal)) Add("read", value);
        foreach (var value in claims.Writes.OrderBy(v => v, StringComparer.Ordinal)) Add("write", value);
        foreach (var value in claims.Network.OrderBy(v => v.Host, StringComparer.Ordinal).ThenBy(v => v.Port))
            Add("network", value.Host + ":" + (value.Port?.ToString(CultureInfo.InvariantCulture) ?? "*"));
        if (claims.Process is not null)
        {
            Add("process.executable", claims.Process.Executable);
            Add("process.effect", claims.Process.EffectClass);
            foreach (var arg in claims.Process.Args) Add("process.arg", arg);
        }
        foreach (var value in claims.Secrets.OrderBy(v => v, StringComparer.Ordinal)) Add("secret", value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));

        void Add(string name, string value) => text.Append(name).Append(':').Append(value.Length)
            .Append(':').Append(value).Append('\n');
    }

    private void AppendAudit(string action, PermissionGrantRecord grant)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_auditFile)!);
        var line = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\t" + action
            + "\t" + grant.Id + "\t" + grant.Workspace + "\t" + Encode(grant.ToolId)
            + "\t" + grant.Lifetime + "\t" + (grant.Run?.ToString() ?? "")
            + "\t" + grant.ClaimsKey + Environment.NewLine;
        File.AppendAllText(_auditFile, line, new UTF8Encoding(false));
    }

    private PermissionGrantRecord[] Read()
    {
        if (!File.Exists(_file)) return Array.Empty<PermissionGrantRecord>();
        var result = new List<PermissionGrantRecord>();
        foreach (var line in File.ReadAllLines(_file))
        {
            if (line.Length == 0) continue;
            var fields = line.Split('\t');
            if (fields.Length != 7 || !Guid.TryParse(fields[0], out var id)
                || !Enum.TryParse<GrantLifetime>(fields[3], out var lifetime)
                || lifetime is not (GrantLifetime.Run or GrantLifetime.Workspace)
                || !long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                throw new InvalidDataException("El almacén de grants está dañado; se falla cerrado.");
            try
            {
                var tool = Decode(fields[1]);
                var claims = fields[2];
                var workspace = WorkspaceId.Parse(Decode(fields[4]));
                var runText = Decode(fields[5]);
                var run = runText.Length == 0 ? null : RunId.Parse(runText);
                result.Add(new PermissionGrantRecord(new GrantId(id), tool, claims, lifetime, workspace, run,
                    new DateTimeOffset(ticks, TimeSpan.Zero)));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new InvalidDataException("El almacén de grants está dañado; se falla cerrado.", ex);
            }
        }
        return result.ToArray();
    }

    private void Write(IReadOnlyList<PermissionGrantRecord> grants)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp";
        var lines = grants.Select(g => string.Join('\t', g.Id, Encode(g.ToolId), g.ClaimsKey,
            g.Lifetime.ToString(), Encode(g.Workspace.ToString()), Encode(g.Run?.ToString() ?? ""),
            g.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)));
        File.WriteAllLines(temp, lines, new UTF8Encoding(false));
        File.Move(temp, _file, true);
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
