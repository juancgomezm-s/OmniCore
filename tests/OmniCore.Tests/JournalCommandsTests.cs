using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Tests del comando <c>omni verify-journal</c> (M4, ADR-0001 §7): códigos de salida por
/// estado del journal, salida en texto y en JSON, filtro de sesión, raíz de artifacts
/// explícita y uso incorrecto. La salida se inyecta (StringWriter) para tests deterministas.
/// </summary>
// CLI dispatch temporarily replaces the process-wide Console.Out.
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class JournalCommandsTests
{
    [Fact]
    public async System.Threading.Tasks.Task Valid_journal_exits_zero_and_prints_ok()
    {
        using var fx = NewCliFixture();
        AppendValidEvents(fx);
        var output = new StringWriter();

        var code = await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath }, output);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("verify-journal: " + Path.GetFileName(fx.JournalPath), text);
        Assert.Contains("— OK", text);
    }

    [Fact]
    public async System.Threading.Tasks.Task Corrupted_journal_exits_one_and_lists_issues()
    {
        using var fx = NewCliFixture();
        AppendValidEvents(fx);
        fx.Store.Close();
        fx.RawSql("UPDATE events SET event_id = 'no-es-un-guid' WHERE seq = 1");
        var output = new StringWriter();

        var code = await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath }, output);

        Assert.Equal(1, code);
        var text = output.ToString();
        Assert.Contains("1 problema(s)", text);
        Assert.Contains("[EnvelopeFieldInvalid]", text);
        Assert.Contains("campo=event_id", text);
    }

    [Fact]
    public async System.Threading.Tasks.Task Missing_journal_exits_one_with_unreadable_report()
    {
        using var fx = NewCliFixture();
        var output = new StringWriter();

        var code = await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", Path.Combine(fx.Root, "ausente.db") }, output);

        Assert.Equal(1, code);
        Assert.Contains("[JournalUnreadable]", output.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Json_output_reports_counts_and_codes()
    {
        using var fx = NewCliFixture();
        AppendValidEvents(fx);
        fx.Store.Close();
        fx.RawSql("UPDATE events SET causation = 'weird:1' WHERE seq = 1");
        var output = new StringWriter();

        var code = await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath, "--json" }, output);

        Assert.Equal(1, code);
        var text = output.ToString().Trim();
        Assert.StartsWith("{\"ok\":false", text);
        Assert.Contains("\"sessions\":1", text);
        Assert.Contains("\"issues\":1", text);
        Assert.Contains("\"codes\":[\"EnvelopeFieldInvalid\"]", text);
    }

    [Fact]
    public async System.Threading.Tasks.Task Session_filter_reports_only_that_stream()
    {
        using var fx = NewCliFixture();
        var good = AppendValidEvents(fx);
        var other = AppendValidEvents(fx);
        fx.Store.Close();
        fx.RawSql("UPDATE events SET event_id = 'roto' WHERE session_id = :sid AND seq = 1",
            ("sid", good.ToString()));
        var output = new StringWriter();

        var exitAll = await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath }, new StringWriter());
        var codeGood = await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath, "--session", good.ToString() }, output);
        var otherWriter = new StringWriter();
        var codeOther = await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath, "--session", other.ToString() }, otherWriter);

        Assert.Equal(1, exitAll);
        Assert.Equal(1, codeGood);
        Assert.Equal(0, codeOther);
        Assert.Contains("campo=event_id", output.ToString());
        Assert.Contains("— OK", otherWriter.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Unknown_flag_and_bad_session_exit_two()
    {
        using var fx = NewCliFixture();
        AppendValidEvents(fx);

        var flag = new StringWriter();
        Assert.Equal(2, await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath, "--nada" }, flag));
        Assert.Contains("opción desconocida", flag.ToString());

        var session = new StringWriter();
        Assert.Equal(2, await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath, "--session", "no-es-un-guid" }, session));
        Assert.Contains("GUID de sesión válido", session.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Artifacts_root_flag_locates_blobs_of_the_ask_flow()
    {
        using var fx = NewCliFixture();
        // El flow ask escribe blobs bajo .omnicore-artifacts con el journal en el cwd: el
        // mismo layout que --artifacts debe poder apuntar.
        var session = SessionId.New();
        var artifacts = new FileArtifactStore(fx.AskArtifactsDir);
        var artifact = artifacts.PutText("respuesta del modelo", "text/plain", ArtifactKind.ModelResponse,
            Sensitivity.Normal);
        var evt = DomainEvent.Create(session, EventType.Of("model.completed"), 1, null, null, null, null, null,
            TurnId.New(), null, null, new[] { artifact },
            fx.Codecs.CodecFor(EventType.Of("model.completed")).Encode(
                new ModelCompleted(TurnId.New(), artifact)));
        fx.Store.Append(session, evt, DurabilityClass.Standard, CancellationToken.None);
        fx.Store.Close();
        var output = new StringWriter();

        var code = await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath, "--artifacts", fx.AskArtifactsDir }, output);

        Assert.Equal(0, code);
        // Envelope (refs declaradas) + payload (ResponseArtifact): 2 refs verificadas por hash.
        Assert.Contains("2 ref(s) de artifacts — OK", output.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task CliApp_dispatches_verify_journal()
    {
        using var fx = NewCliFixture();
        AppendValidEvents(fx);

        // El dispatch de CliApp (RunAsync) llega a JournalCommands y devuelve el mismo código.
        var prior = Console.Out;
        Console.SetOut(new StringWriter());
        try
        {
            var code = await OmniCore.Cli.CliApp.RunAsync(new[] { "verify-journal", fx.JournalPath });
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetOut(prior);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Max_issues_limit_truncates_listing_but_counts_all()
    {
        using var fx = NewCliFixture();
        AppendValidEvents(fx);
        AppendValidEvents(fx);
        AppendValidEvents(fx);
        fx.Store.Close();
        // Tres filas corruptas (una por sesión): el límite lista 2, el total sigue siendo 3.
        fx.RawSql("UPDATE events SET event_id = 'x' WHERE seq = 1");
        var output = new StringWriter();

        var code = await OmniCore.Cli.JournalCommands.VerifyJournal(
            new[] { "verify-journal", fx.JournalPath, "--max-issues", "2" }, output);

        Assert.Equal(1, code);
        var text = output.ToString();
        Assert.Contains("3 problema(s) (se listan 2 de 3)", text);
        Assert.Contains("… y 1 problema(s) más", text);
        Assert.Equal(2, text.Split("[EnvelopeFieldInvalid]", StringSplitOptions.None).Length - 1);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class CliFixture : IDisposable
    {
        public CliFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "omnicore-m4-cli-verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            JournalPath = Path.Combine(Root, "journal.db");
            AskArtifactsDir = Path.Combine(Root, ".omnicore-artifacts");
            Store = new SqliteEventStore(JournalPath);
            Codecs = EventCodecs.Create();
        }

        public string Root { get; }

        public string JournalPath { get; }

        public string AskArtifactsDir { get; }

        public SqliteEventStore Store { get; }

        public EventCodecs Codecs { get; }

        public void RawSql(string sql, params (string Name, object Value)[] args)
        {
            var conn = SqliteFactory.Instance!.CreateDataSource("DataSource=" + JournalPath)!.OpenConnection()!;
            try
            {
                var cmd = conn.CreateCommand()!;
                cmd.CommandText = sql;
                foreach (var (name, value) in args)
                {
                    var p = cmd.CreateParameter()!;
                    p.ParameterName = name;
                    p.Value = value;
                    cmd.Parameters.Add(p);
                }

                cmd.ExecuteNonQuery();
            }
            finally
            {
                conn.Close();
            }
        }

        public void Dispose()
        {
            Store.Close();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Mejor esfuerzo en Windows.
            }
        }
    }

    private static CliFixture NewCliFixture() => new();

    private static SessionId AppendValidEvents(CliFixture fx, SessionId? session = null)
    {
        session ??= SessionId.New();
        var payload = new SessionCreated(session, "ws-test", "c:\\ws", ProfileId.New(), DateTimeOffset.Now);
        var evt = DomainEvent.Create(session, EventType.Of("session.created"), 1, null, null, null, null, null,
            null, null, null, Array.Empty<ArtifactRef>(), fx.Codecs.CodecFor(payload.Type()).Encode(payload));
        fx.Store.Append(session, evt, DurabilityClass.Standard, CancellationToken.None);
        return session;
    }
}
