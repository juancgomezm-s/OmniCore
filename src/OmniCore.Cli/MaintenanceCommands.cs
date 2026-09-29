namespace OmniCore.Cli;

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
public sealed class MaintenanceCommands
{
    private const string DefaultJournal = ".omnicore-sim-journal.db";

    public static Task<int> SessionPurge(string[] args, TextWriter? output = null)
    {
        var console = output ?? Console.Out;
        var journalPath = DefaultJournal;
        string? sessionId = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--journal" && i + 1 < args.Length)
            {
                journalPath = args[++i];
            }
            else if (sessionId is null && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                sessionId = args[i];
            }
            else
            {
                console.WriteLine("omni session purge: opción o argumento desconocido '" + args[i] + "'");
                return Task.FromResult(2);
            }
        }

        if (sessionId is null)
        {
            console.WriteLine("Uso: omni session purge {id-de-sesión} [--journal {ruta}]");
            return Task.FromResult(2);
        }

        SessionId session;
        try
        {
            session = SessionId.Parse(sessionId);
        }
        catch (FormatException)
        {
            console.WriteLine("omni session purge: id de sesión no válido: '" + sessionId + "'");
            return Task.FromResult(2);
        }

        if (!File.Exists(journalPath))
        {
            console.WriteLine("omni session purge: journal no encontrado: " + journalPath);
            return Task.FromResult(1);
        }

        // La auditoría vive junto al journal (mismo layout que OmniHost.CreateInProcessServer).
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(journalPath))!;
        var store = new SqliteEventStore(journalPath);
        try
        {
            var purger = new SessionPurger(store, new FileAuditSink(dataDir));
            var result = purger.Purge(session, WorkspaceId.Of(dataDir), DateTimeOffset.Now,
                CancellationToken.None);
            if (result is null)
            {
                console.WriteLine("omni session purge: sesión no encontrada en el journal: " + session);
                return Task.FromResult(1);
            }

            console.WriteLine(result.SummaryLine());
            console.WriteLine("Los artifacts quedan huérfanos: ejecuta 'omni gc' para recogerlos.");
            return Task.FromResult(0);
        }
        finally
        {
            store.Close();
        }
    }

    public static Task<int> Gc(string[] args, TextWriter? output = null)
    {
        var console = output ?? Console.Out;
        var journalPath = DefaultJournal;
        var artifactsDir = (string?) null;
        var graceHours = (int?) null;
        var dryRun = args.Any(a => a == "--dry-run");
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--journal" && i + 1 < args.Length)
            {
                journalPath = args[++i];
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
                console.WriteLine("omni gc: opción desconocida '" + args[i] + "'");
                console.WriteLine("Uso: omni gc [--journal <ruta>] [--artifacts <dir>] [--grace-hours N] [--dry-run]");
                return Task.FromResult(2);
            }
        }

        if (!File.Exists(journalPath))
        {
            console.WriteLine("omni gc: journal no encontrado: " + journalPath);
            return Task.FromResult(1);
        }

        var artifactsRoot = artifactsDir ?? Path.GetDirectoryName(Path.GetFullPath(journalPath))!;
        var gc = new ArtifactGc(artifactsRoot);
        var result = gc.Sweep(journalPath, graceHours is null
            ? ArtifactGc.DefaultGrace
            : TimeSpan.FromHours(graceHours.Value), dryRun, DateTimeOffset.Now, CancellationToken.None);
        console.WriteLine(result.SummaryLine());
        if (dryRun && result.Deleted > 0)
        {
            console.WriteLine("Reejecuta sin --dry-run para borrar de verdad.");
        }

        return Task.FromResult(0);
    }

    public static Task<int> AuditPurge(string[] args, TextWriter? output = null)
    {
        var console = output ?? Console.Out;
        var dataDir = ".";
        var retentionDays = AuditRetention.DefaultRetentionDays;
        DateTimeOffset? before = null;
        var dryRun = args.Any(a => a == "--dry-run");
        for (var i = 1; i < args.Length; i++)
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
                    console.WriteLine("omni audit purge: --before espera una fecha ISO (p. ej. 2026-01-01): '"
                        + args[i + 1] + "'");
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
                console.WriteLine("omni audit purge: opción desconocida '" + args[i] + "'");
                console.WriteLine("Uso: omni audit purge [--data-dir <dir>] [--before <fecha>] "
                    + "[--retention-days N] [--dry-run]");
                return Task.FromResult(2);
            }
        }

        var sink = new FileAuditSink(dataDir);
        var retention = new AuditRetention(dataDir, sink);
        var now = DateTimeOffset.Now;
        var cutoff = before ?? now.AddDays(-retentionDays);
        var result = retention.PurgeBefore(cutoff, dryRun, now, CancellationToken.None);
        console.WriteLine(result.SummaryLine());
        console.WriteLine("Registro de auditoría: " + OmniCore.Infrastructure.FileAuditSink.AuditFilePath(dataDir).ToString());
        if (dryRun && result.Removed > 0)
        {
            console.WriteLine("Reejecuta sin --dry-run para aplicar la retención.");
        }

        return Task.FromResult(0);
    }
}
