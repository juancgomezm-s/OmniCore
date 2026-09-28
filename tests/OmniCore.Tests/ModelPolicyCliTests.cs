using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests del ciclo de comportamiento del CLI (ADR-0044 §6): selección nueva ⇒ onboarding,
/// consultar/cambiar/eliminar y eliminar ⇒ el onboarding reaparece. Onboarding lineal por
/// TextReader/StringWriter; el frame TUI es M4 (ADR-0045).
/// </summary>
public sealed class ModelPolicyCliTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ModelRegistry Registry() => new ModelRegistry().AddModel(
        new ModelDefinition("test-model", "openai", 8192, 8192, 4096));

    private static async System.Threading.Tasks.Task<int> Run(string[] args, string input, TextWriter output,
        string dataDir, bool interactive = true) =>
        await OmniCore.Cli.ModelPolicyCommands.Run(args, new StringReader(input), output, interactive,
            dataDir, Registry());

    [Fact]
    public async System.Threading.Tasks.Task New_selection_opens_onboarding_and_saves_choice()
    {
        var dir = TempDir();
        var output = new StringWriter();

        var code = await Run(new[] { "model", "select", "test-model" }, "1\n", output, dir);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Onboarding de política", text);
        Assert.Contains("1) ObserveOnly", text);
        Assert.Contains("TECHO", text);
        Assert.Contains("Recomendación sin evidencia", text);
        // Elegir 1 guarda ObserveOnly rev=1 y selecciona.
        Assert.Contains("Política guardada: ObserveOnly rev=1", text);
        Assert.Contains("Seleccionado: test-model", text);
    }

    [Fact]
    public async System.Threading.Tasks.Task Show_set_delete_and_reonboard_cycle()
    {
        var dir = TempDir();

        // Alta inicial vía onboarding (elige 2 = PatchOnly).
        var onboard = new StringWriter();
        Assert.Equal(0, await Run(new[] { "model", "select", "test-model" }, "2\n", onboard, dir));
        Assert.Contains("PatchOnly rev=1", onboard.ToString());

        // Consultar.
        var show = new StringWriter();
        Assert.Equal(0, await Run(new[] { "model", "policy", "show", "test-model" }, "", show, dir));
        Assert.Contains("PatchOnly", show.ToString());
        Assert.Contains("visibles=7", show.ToString());

        // Cambiar (sin --revision usa la vigente ⇒ sin conflicto).
        var update = new StringWriter();
        Assert.Equal(0, await Run(
            new[] { "model", "policy", "set", "test-model", "--category", "ScopedCoder", "--note", "prueba" },
            "", update, dir));
        Assert.Contains("ScopedCoder rev=2", update.ToString());

        // Cambiar con revisión obsoleta ⇒ conflicto, nada se pisa y exit 2.
        var stale = new StringWriter();
        Assert.Equal(2, await Run(
            new[] { "model", "policy", "set", "test-model", "--category", "FullAgent", "--revision", "1" },
            "", stale, dir));
        Assert.Contains("Conflicto de revisión", stale.ToString());
        var afterConflict = new StringWriter();
        await Run(new[] { "model", "policy", "show", "test-model" }, "", afterConflict, dir);
        Assert.Contains("ScopedCoder", afterConflict.ToString());

        // Historial: created + updated.
        var history = new StringWriter();
        Assert.Equal(0, await Run(new[] { "model", "policy", "history", "test-model" }, "", history, dir));
        Assert.Contains("rev=1 created", history.ToString());
        Assert.Contains("rev=2 updated", history.ToString());

        // Eliminar...
        var delete = new StringWriter();
        Assert.Equal(0, await Run(new[] { "model", "policy", "delete", "test-model" }, "", delete, dir));
        Assert.Contains("Política eliminada", delete.ToString());

        // ...y el onboarding reaparece en la siguiente selección.
        var reonboard = new StringWriter();
        Assert.Equal(0, await Run(new[] { "model", "select", "test-model" }, "1\n", reonboard, dir));
        Assert.Contains("Onboarding de política", reonboard.ToString());
        Assert.Contains("ObserveOnly rev=1", reonboard.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Non_interactive_select_uses_ephemeral_safe_mode()
    {
        var dir = TempDir();
        var output = new StringWriter();

        var code = await Run(new[] { "model", "select", "test-model" }, "", output, dir, interactive: false);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Sin terminal interactiva", text);
        Assert.Contains("ObserveOnly por esta selección", text);
        Assert.DoesNotContain("Onboarding de política", text);
        // No se guardó ninguna política: la próxima selección TTY abrirá onboarding.
        Assert.DoesNotContain("Política guardada", text);
    }

    [Fact]
    public async System.Threading.Tasks.Task Safe_once_choice_marks_selection_ephemeral()
    {
        var dir = TempDir();
        var output = new StringWriter();

        var code = await Run(new[] { "model", "select", "test-model" }, "s\n", output, dir);

        Assert.Equal(0, code);
        Assert.Contains("modo seguro ObserveOnly (solo esta selección)", output.ToString());
        Assert.DoesNotContain("Política guardada", output.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Cancelled_onboarding_saves_nothing()
    {
        var dir = TempDir();
        var output = new StringWriter();

        var code = await Run(new[] { "model", "select", "test-model" }, "c\n", output, dir);

        Assert.Equal(1, code);
        Assert.Contains("Cancelado: no se guardó política ni selección", output.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Invalid_choice_is_rejected_and_reasks()
    {
        var dir = TempDir();
        var output = new StringWriter();

        var code = await Run(new[] { "model", "select", "test-model" }, "9\nxx\n3\n", output, dir);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Opción no válida: '9'", text);
        Assert.Contains("Opción no válida: 'xx'", text);
        Assert.Contains("ScopedCoder rev=1", text);
    }

    [Fact]
    public async System.Threading.Tasks.Task Unknown_model_is_an_error()
    {
        var dir = TempDir();
        var output = new StringWriter();

        var code = await Run(new[] { "model", "select", "no-existe" }, "", output, dir);

        Assert.Equal(2, code);
        Assert.Contains("modelo desconocido", output.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task List_shows_policy_status_per_model()
    {
        var dir = TempDir();
        await Run(new[] { "model", "select", "test-model" }, "4\n", new StringWriter(), dir);

        var output = new StringWriter();
        Assert.Equal(0, await Run(new[] { "model", "list" }, "", output, dir));

        Assert.Contains("test-model", output.ToString());
        Assert.Contains("FullAgent rev=1", output.ToString());
    }
}
