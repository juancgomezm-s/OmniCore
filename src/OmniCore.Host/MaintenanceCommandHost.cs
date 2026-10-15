namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

/// <summary>
/// Comandos de mantenimiento del journal (M4, ADR-0001 §8 + ADR-0043 §1):
/// <para>
/// <c>omni session purge {id}</c> — audita antes de borrar y elimina los eventos de la sesión.<br/>
/// <c>omni gc [--dry-run] [--grace-hours N]</c> — mark-and-sweep de blobs con periodo de gracia.<br/>
/// <c>omni audit purge [--before fecha] [--retention-days N] [--dry-run]</c> — retención de
/// auditoría que deja constancia de sí misma.
/// </para>
/// <para>
/// Códigos de salida: 0 = ok, 1 = sin efecto (sesión inexistente, nada que borrar) o error,
/// 2 = uso incorrecto. La salida es inyectable para tests deterministas.
/// </para>
/// </summary>
public sealed class MaintenanceCommandHost
{
    public static Task<int> SessionPurge(string[] args, TextWriter? output = null,
        Func<CommandOutputText, string>? resolve = null)
    {
        var console = output ?? Console.Out;
        var paths = OmniHost.CreatePlatformPaths();
        var journalPath = Path.Combine(OmniHost.WorkspaceDataDirectory(paths, "."), "journal.db");
        var dataDir = paths.DataDirectory;
        string? sessionId = null;
        var firstOption = args.Length >= 2 && args[0] == "session" && args[1] == "purge" ? 2 : 1;
        for (var i = firstOption; i < args.Length; i++)
        {
            if (args[i] == "--journal" && i + 1 < args.Length)
            {
                journalPath = args[++i];
            }
            else if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                dataDir = args[++i];
            }
            else if (sessionId is null && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                sessionId = args[i];
            }
            else
            {
                LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.unknown_option",
                    new Dictionary<string, string> { ["command"] = "omni session purge", ["value"] = args[i] },
                    "omni session purge: opción o argumento desconocido '" + args[i] + "'");
                return Task.FromResult(2);
            }
        }

        if (sessionId is null)
        {
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.session_purge.usage", null,
                "Uso: omni session purge {id-de-sesión} [--journal {ruta}] [--data-dir {directorio-de-usuario}]");
            return Task.FromResult(2);
        }

        SessionId session;
        try
        {
            session = SessionId.Parse(sessionId);
        }
        catch (FormatException)
        {
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.invalid_session",
                new Dictionary<string, string> { ["value"] = sessionId! },
                "omni session purge: id de sesión no válido: '" + sessionId + "'");
            return Task.FromResult(2);
        }

        if (!File.Exists(journalPath))
        {
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.journal_missing",
                new Dictionary<string, string> { ["command"] = "omni session purge", ["path"] = journalPath },
                "omni session purge: journal no encontrado: " + journalPath);
            return Task.FromResult(1);
        }

        // La auditoría vive en scope User, fuera del journal del workspace (ADR-0043 §1).
        var store = new SqliteEventStore(journalPath);
        try
        {
            store.AcquireSessionWriterLease(session);
            var purger = new SessionPurger(store, new FileAuditSink(dataDir));
            var workspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
                ProjectIdentity.ResolvePhysicalWorkspaceRoot(".")));
            var result = purger.Purge(session, workspace, DateTimeOffset.UtcNow,
                CancellationToken.None);
            if (result is null)
            {
                LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.session_missing",
                    new Dictionary<string, string> { ["session"] = session.ToString() },
                    "omni session purge: sesión no encontrada en el journal: " + session);
                return Task.FromResult(1);
            }

            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.session_purged",
                new Dictionary<string, string>
                {
                    ["session"] = result.Session.ToString(),
                    ["events"] = result.EventsDeleted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }, result.SummaryLine());
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.artifacts_orphaned", null,
                "Los artifacts quedan huérfanos: ejecuta 'omni gc' para recogerlos.");
            return Task.FromResult(0);
        }
        catch (SessionWriterLeaseUnavailableException)
        {
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.session_busy",
                new Dictionary<string, string> { ["session"] = session.ToString() },
                "omni session purge: la sesión está en uso por otro cliente: " + session);
            return Task.FromResult(1);
        }
        finally
        {
            store.Close();
        }
    }

    public static Task<int> Gc(string[] args, TextWriter? output = null,
        Func<CommandOutputText, string>? resolve = null)
    {
        var console = output ?? Console.Out;
        var paths = OmniHost.CreatePlatformPaths();
        var journalPath = Path.Combine(OmniHost.WorkspaceDataDirectory(paths, "."), "journal.db");
        var artifactsDir = (string?) null;
        var graceHours = (int?) null;
        var scope = "workspace";
        var explicitJournal = false;
        var dryRun = args.Any(a => a == "--dry-run");
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--journal" && i + 1 < args.Length)
            {
                explicitJournal = true;
                journalPath = args[++i];
            }
            else if (args[i] == "--scope" && i + 1 < args.Length)
            {
                scope = args[++i].ToLowerInvariant();
            }
            else if (args[i] == "--artifacts" && i + 1 < args.Length)
            {
                artifactsDir = args[++i];
            }
            else if (args[i] == "--grace-hours" && i + 1 < args.Length && int.TryParse(args[i + 1], out var h))
            {
                graceHours = h < 0 ? 0 : h;
                i++;
            }
            else if (args[i] == "--dry-run")
            {
                // Ya capturado arriba.
            }
            else
            {
                LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.unknown_option",
                    new Dictionary<string, string> { ["command"] = "omni gc", ["value"] = args[i] },
                    "omni gc: opción desconocida '" + args[i] + "'");
                LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.gc.usage", null,
                    "Uso: omni gc [--scope workspace|user] [--journal <ruta> (workspace)] [--artifacts <dir>] [--grace-hours N] [--dry-run]");
                return Task.FromResult(2);
            }
        }

        if (scope is not ("workspace" or "user") || (scope == "user" && explicitJournal))
        {
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.gc.invalid_scope", null,
                "omni gc: scope debe ser workspace o user; User no acepta --journal de workspace.");
            return Task.FromResult(2);
        }
        if (scope == "workspace" && !File.Exists(journalPath))
        {
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.journal_missing",
                new Dictionary<string, string> { ["command"] = "omni gc", ["path"] = journalPath },
                "omni gc: journal no encontrado: " + journalPath);
            return Task.FromResult(1);
        }

        var artifactsRoot = artifactsDir ?? (scope == "user"
            ? paths.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        var gc = new ArtifactGc(artifactsRoot);
        ArtifactGc.SweepResult result;
        try
        {
            result = gc.Sweep(scope == "user" ? null : journalPath, graceHours is null
                ? ArtifactGc.DefaultGrace
                : TimeSpan.FromHours(graceHours.Value), dryRun, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.gc.error",
                new Dictionary<string, string> { ["error"] = exception.Message }, "omni gc: error: " + exception.Message);
            return Task.FromResult(1);
        }
        if (scope == "user") LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.scope_user",
            null, "Scope: User");
        var deleteLabel = result.DryRun ? "cli.maintenance.gc.delete.dry" : "cli.maintenance.gc.delete.live";
        var deleteText = resolve?.Invoke(new CommandOutputText(deleteLabel, new Dictionary<string, string>()))
            ?? (result.DryRun ? result.Deleted + " candidato(s) a borrar (dry-run)" : result.Deleted + " borrado(s)");
        var bytesText = result.ReclaimedBytes > 0
            ? resolve?.Invoke(new CommandOutputText("cli.maintenance.gc.bytes", new Dictionary<string, string>
                { ["bytes"] = result.ReclaimedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
                ?? ", " + result.ReclaimedBytes + " bytes"
            : string.Empty;
        LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.gc.summary", new Dictionary<string, string>
        {
            ["scanned"] = result.Scanned.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["live"] = result.LiveReferenced.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["grace"] = result.KeptByGrace.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hours"] = ((int)result.Grace.TotalHours).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["deleted"] = result.Deleted.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["deleteLabel"] = deleteText,
            ["bytes"] = bytesText,
        }, result.SummaryLine());
        if (dryRun && result.Deleted > 0)
        {
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.gc.dry_hint", null,
                "Reejecuta sin --dry-run para borrar de verdad.");
        }

        return Task.FromResult(0);
    }

    public static Task<int> AuditPurge(string[] args, TextWriter? output = null,
        Func<CommandOutputText, string>? resolve = null)
    {
        var console = output ?? Console.Out;
        var dataDir = OmniHost.CreatePlatformPaths().DataDirectory;
        var retentionDays = AuditRetention.DefaultRetentionDays;
        DateTimeOffset? before = null;
        var dryRun = args.Any(a => a == "--dry-run");
        var firstOption = args.Length >= 2 && args[0] == "audit" && args[1] == "purge" ? 2 : 1;
        for (var i = firstOption; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                dataDir = args[++i];
            }
            else if (args[i] == "--retention-days" && i + 1 < args.Length && int.TryParse(args[i + 1], out var d))
            {
                retentionDays = d < 0 ? 0 : d;
                i++;
            }
            else if (args[i] == "--before" && i + 1 < args.Length)
            {
                if (!DateTimeOffset.TryParse(args[i + 1],
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedCutoff))
                {
                    LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.audit_purge.date_invalid",
                        new Dictionary<string, string> { ["value"] = args[i + 1] },
                        "omni audit purge: --before espera una fecha ISO (p. ej. 2026-01-01): '" + args[i + 1] + "'");
                    return Task.FromResult(2);
                }

                before = parsedCutoff;
                i++;
            }
            else if (args[i] == "--dry-run")
            {
                // Ya capturado arriba.
            }
            else
            {
                LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.unknown_option",
                    new Dictionary<string, string> { ["command"] = "omni audit purge", ["value"] = args[i] },
                    "omni audit purge: opción desconocida '" + args[i] + "'");
                LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.audit_purge.usage", null,
                    "Uso: omni audit purge [--data-dir <dir>] [--before <fecha>] [--retention-days N] [--dry-run]");
                return Task.FromResult(2);
            }
        }

        var sink = new FileAuditSink(dataDir);
        var retention = new AuditRetention(dataDir, sink);
        var now = DateTimeOffset.UtcNow;
        var cutoff = before ?? now.AddDays(-retentionDays);
        var result = retention.PurgeBefore(cutoff, dryRun, now, CancellationToken.None);
        var unparseableText = result.UnparseableKept > 0
            ? resolve?.Invoke(new CommandOutputText("cli.maintenance.audit_purge.unparseable", new Dictionary<string, string>
                { ["count"] = result.UnparseableKept.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
                ?? " (" + result.UnparseableKept + " sin fecha legible, conservados)"
            : string.Empty;
        var dryText = dryRun
            ? resolve?.Invoke(new CommandOutputText("cli.maintenance.audit_purge.dry", new Dictionary<string, string>()))
                ?? " — dry-run (sin cambios)"
            : string.Empty;
        LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.audit_purge.summary", new Dictionary<string, string>
        {
            ["examined"] = result.Examined.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["removed"] = result.Removed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["kept"] = result.Kept.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["unparseable"] = unparseableText,
            ["dryRun"] = dryText,
        }, result.SummaryLine());
        var auditPath = OmniCore.Infrastructure.FileAuditSink.AuditFilePath(dataDir).ToString();
        LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.audit_path",
            new Dictionary<string, string> { ["path"] = auditPath }, "Registro de auditoría: " + auditPath);
        if (dryRun && result.Removed > 0)
        {
            LocalizedCommandOutput.WriteLine(console, resolve, "cli.maintenance.audit_purge.dry_hint", null,
                "Reejecuta sin --dry-run para aplicar la retención.");
        }

        return Task.FromResult(0);
    }
}
