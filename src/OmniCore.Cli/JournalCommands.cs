namespace OmniCore.Cli;

using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

/// <summary>
/// Comandos del journal (M4): <c>omni verify-journal [ruta] [--session id] [--artifacts dir]
/// [--json] [--max-issues N]</c> verifica el journal SQLite y sus referencias a artifacts de
/// forma read-only (ADR-0001 §7). También alimenta <c>omni doctor --verify-journal</c>.
/// <para>
/// El CLI solo compone: el verificador es <see cref="JournalVerifier"/> (Infrastructure) y el
/// artifact store el mismo <see cref="FileArtifactStore"/> del runtime. La salida es inyectable
/// para tests deterministas; los códigos de salida: 0 = OK, 1 = problemas (corrupción o journal
/// ilegible), 2 = uso incorrecto.
/// </para>
/// </summary>
public sealed class JournalCommands
{
    private const string DefaultJournal = ".omnicore-sim-journal.db";

    public static Task<int> VerifyJournal(string[] args, TextWriter? output = null)
    {
        var console = output ?? Console.Out;
        var journalPath = DefaultJournal;
        var artifactsDir = (string?) null;
        string? session = null;
        var json = false;
        var maxIssues = 500;

        // El primer argumento posicional (que no empiece por --) es la ruta del journal.
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                json = true;
            }
            else if (arg == "--session" && i + 1 < args.Length)
            {
                session = args[++i];
            }
            else if (arg == "--artifacts" && i + 1 < args.Length)
            {
                artifactsDir = args[++i];
            }
            else if (arg == "--max-issues" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n))
            {
                maxIssues = n < 1 ? 1 : n;
                i++;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                console.WriteLine("omni verify-journal: opción desconocida '" + arg + "'");
                PrintUsage(console);
                return Task.FromResult(2);
            }
            else
            {
                journalPath = arg;
            }
        }

        if (session is not null && !IsGuid(session))
        {
            console.WriteLine("omni verify-journal: --session espera un GUID de sesión válido: '" + session + "'");
            return Task.FromResult(2);
        }

        // Raíz de blobs por defecto: el directorio del journal (journal.db y blobs/ comparten
        // directorio de datos). El flow ask del CLI usa .omnicore-artifacts y se fija con
        // --artifacts (doctor --verify-journal la pasa explícita).
        var artifactsRoot = artifactsDir ?? Path.GetDirectoryName(Path.GetFullPath(journalPath))!;

        var codecs = EventCodecs.Create();
        var artifacts = new FileArtifactStore(artifactsRoot);
        var verifier = new JournalVerifier(codecs, artifacts, maxIssues);
        var report = session is null
            ? verifier.VerifyJournal(journalPath)
            : verifier.VerifySession(journalPath, session);

        if (json)
        {
            console.WriteLine(ReportJson(report));
            return Task.FromResult(report.Ok ? 0 : 1);
        }

        console.WriteLine(report.SummaryLine());
        foreach (var issue in report.Issues)
        {
            console.WriteLine(IssueLine(issue));
        }

        if (report.IssuesTruncated)
        {
            console.WriteLine("… y " + (report.TotalIssueCount - report.Issues.Count) + " problema(s) más "
                + "(usa --max-issues para listarlos).");
        }

        return Task.FromResult(report.Ok ? 0 : 1);
    }

    private static void PrintUsage(TextWriter console)
    {
        console.WriteLine("Uso: omni verify-journal [ruta] [--session <id>] [--artifacts <dir>] [--json] "
            + "[--max-issues N]");
        console.WriteLine("  ruta        journal SQLite (por defecto " + DefaultJournal + ")");
        console.WriteLine("  --artifacts raíz de blobs (por defecto, el directorio del journal)");
    }

    /// <summary>Línea legible de un problema: código, ubicación y detalle (en español). El
    /// detalle pasa por el redactor de PII por defensa en profundidad: el journal ya se
    /// escribe redactado (ADR-0018 §4), pero un error de parse puede incluir fragmentos.
    /// </summary>
    private static string IssueLine(JournalIssue issue)
    {
        var detail = new OmniCore.Domain.PiiRedactor().Redact(issue.Detail);
        var where = "seq=" + (issue.Sequence?.ToString() ?? "—");
        if (issue.SessionId is not null)
        {
            where = "sesión=" + issue.SessionId + " " + where;
        }

        if (issue.EventType is not null)
        {
            where += " evento=" + issue.EventType;
        }

        if (issue.Field is not null)
        {
            where += " campo=" + issue.Field;
        }

        return "[" + issue.Code + "] " + where + " — " + detail;
    }

    /// <summary>Reporte como un objeto JSON de una línea (para scripting y CI).</summary>
    private static string ReportJson(JournalVerificationReport report)
    {
        var json = "{" + JsonObj.FieldBool("ok", report.Ok)
            + "," + JsonObj.FieldRaw("sessions", report.SessionCount.ToString())
            + "," + JsonObj.FieldRaw("events", report.EventCount.ToString())
            + "," + JsonObj.FieldRaw("artifactRefs", report.ArtifactRefCount.ToString())
            + "," + JsonObj.FieldRaw("issues", report.TotalIssueCount.ToString())
            + "," + JsonObj.Field("journal", report.JournalPath)
            + ",\"codes\":[" + string.Join(",", report.Issues.Select(i => "\"" + i.Code + "\"")) + "]"
            + "}";
        return json;
    }

    private static bool IsGuid(string text)
    {
        try
        {
            OmniCore.Domain.EventId.ParseGuidText(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
