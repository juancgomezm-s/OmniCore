using System.IO;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Tests de filesystem.list (M2 EPIC-018): listado de directorios read-only dentro del workspace.
/// - Listado no recursivo ordenado por path con kind/size
/// - Listado recursivo
/// - maxEntries cap → truncated: true
/// - path: ".." (escape) rechazado
/// - Archivo secreto (SecretPathGuard) no listado
/// - .git/ saltado
/// </summary>
public sealed class M2ReadToolsTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m2-list-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }

    private static ScriptedToolExecutor ListExecutor(string wsDir)
    {
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false, includeMutationTools: false);
        var policy = ScriptedPermissionPolicy.WithTool("filesystem.list", PermissionDecision.Allow);
        return ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, wsDir);
    }

    private static ValidatedToolCall ListCall(string path = ".", bool? recursive = null, int? maxEntries = null)
    {
        var args = new Dictionary<string, object> { ["path"] = path };
        if (recursive.HasValue) args["recursive"] = recursive.Value;
        if (maxEntries.HasValue) args["maxEntries"] = maxEntries.Value;

        var json = JsonSerializer.Serialize(args);
        return new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.list"),
            "pc-" + Guid.NewGuid().ToString("N").Substring(0, 6), json);
    }

    private static JsonDocument ParseJson(string json) => JsonDocument.Parse(json);

    [Fact]
    public void ListDirectory_non_recursive_sorted_by_path_with_kind_and_size()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        // Create test structure
        Directory.CreateDirectory(Path.Combine(ws, "subdir"));
        File.WriteAllText(Path.Combine(ws, "a.txt"), "contenido de a"); // 14 bytes
        File.WriteAllText(Path.Combine(ws, "b.txt"), "bb"); // 2 bytes
        File.WriteAllText(Path.Combine(ws, "subdir", "c.txt"), "ccc"); // 3 bytes

        var outcome = executor.ExecuteToolWithoutJournal(ListCall("."), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El listado debe tener éxito. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Succeeded, outcome.FinalState);

        var doc = ParseJson(outcome.Preview!);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        Assert.Equal(3, entries.Length);

        // Entradas ordenadas por path (a.txt, b.txt, subdir/)
        Assert.Equal("a.txt", entries[0].GetProperty("path").GetString());
        Assert.Equal("file", entries[0].GetProperty("kind").GetString());
        Assert.Equal(14, entries[0].GetProperty("size").GetInt64());

        Assert.Equal("b.txt", entries[1].GetProperty("path").GetString());
        Assert.Equal("file", entries[1].GetProperty("kind").GetString());
        Assert.Equal(2, entries[1].GetProperty("size").GetInt64());

        Assert.Equal("subdir", entries[2].GetProperty("path").GetString());
        Assert.Equal("dir", entries[2].GetProperty("kind").GetString());
        Assert.False(entries[2].TryGetProperty("size", out _), "Los directorios no tienen size");

        // truncated no debe estar presente
        Assert.False(doc.RootElement.TryGetProperty("truncated", out _));

        Cleanup(ws);
    }

    [Fact]
    public void ListDirectory_recursive_includes_nested_entries()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        // Create nested structure
        Directory.CreateDirectory(Path.Combine(ws, "dir1", "dir2"));
        File.WriteAllText(Path.Combine(ws, "root.txt"), "root");
        File.WriteAllText(Path.Combine(ws, "dir1", "file1.txt"), "file1");
        File.WriteAllText(Path.Combine(ws, "dir1", "dir2", "file2.txt"), "file2");

        var outcome = executor.ExecuteToolWithoutJournal(ListCall(".", recursive: true), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El listado recursivo debe tener éxito. summary=" + outcome.Summary);

        var doc = ParseJson(outcome.Preview!);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        Assert.Equal(5, entries.Length); // root.txt, dir1/, dir1/file1.txt, dir1/dir2/, dir1/dir2/file2.txt

        var paths = entries.Select(e => e.GetProperty("path").GetString()).ToArray();
        Assert.Contains("root.txt", paths);
        Assert.Contains("dir1", paths);
        Assert.Contains("dir1/file1.txt", paths);
        Assert.Contains("dir1/dir2", paths);
        Assert.Contains("dir1/dir2/file2.txt", paths);

        // Verificar orden lexicográfico
        var sortedPaths = paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(sortedPaths, paths);

        Cleanup(ws);
    }

    [Fact]
    public void ListDirectory_maxEntries_cap_returns_truncated_true()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        // Create 250 files (more than default 200)
        for (int i = 0; i < 250; i++)
        {
            File.WriteAllText(Path.Combine(ws, $"file{i:D3}.txt"), $"content{i}");
        }

        var outcome = executor.ExecuteToolWithoutJournal(ListCall(".", maxEntries: 50), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El listado debe tener éxito aunque truncado. summary=" + outcome.Summary);

        var doc = ParseJson(outcome.Preview!);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean(), "truncated debe ser true");

        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(50, entries.Length); // Exactamente maxEntries

        Cleanup(ws);
    }

    [Fact]
    public void ListDirectory_path_escape_rejected()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        // Crear un directorio FUERA del workspace
        var outsideDir = Path.Combine(Path.GetTempPath(), "omnicore-m2-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "outside");

        try
        {
            var relativeEscape = Path.GetRelativePath(ws, outsideDir).Replace('\\', '/');
            var outcome = executor.ExecuteToolWithoutJournal(ListCall(relativeEscape), false, CancellationToken.None);

            Assert.False(outcome.Succeeded, "Un path que escapa del workspace debe ser rechazado. summary=" + outcome.Summary);
            Assert.Equal(ToolCallState.Failed, outcome.FinalState);
            Assert.Contains("fuera del workspace", outcome.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(outsideDir);
        }
    }

    [Fact]
    public void ListDirectory_secret_file_not_listed()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        // Create normal file and secret file (.env)
        File.WriteAllText(Path.Combine(ws, "normal.txt"), "normal");
        File.WriteAllText(Path.Combine(ws, ".env"), "SECRET=value");

        var outcome = executor.ExecuteToolWithoutJournal(ListCall("."), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El listado debe tener éxito. summary=" + outcome.Summary);

        var doc = ParseJson(outcome.Preview!);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        // Solo normal.txt debe aparecer, .env debe estar oculto
        Assert.Single(entries);
        Assert.Equal("normal.txt", entries[0].GetProperty("path").GetString());
        Assert.Equal("file", entries[0].GetProperty("kind").GetString());

        Cleanup(ws);
    }

    [Fact]
    public void ListDirectory_git_directory_skipped()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        // Create .git directory and normal files
        Directory.CreateDirectory(Path.Combine(ws, ".git", "objects"));
        File.WriteAllText(Path.Combine(ws, ".git", "config"), "[core]");
        File.WriteAllText(Path.Combine(ws, "normal.txt"), "normal");

        var outcome = executor.ExecuteToolWithoutJournal(ListCall("."), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El listado debe tener éxito. summary=" + outcome.Summary);

        var doc = ParseJson(outcome.Preview!);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        // .git no debe aparecer
        var paths = entries.Select(e => e.GetProperty("path").GetString()).ToArray();
        Assert.DoesNotContain(".git", paths);
        Assert.Contains("normal.txt", paths);

        Cleanup(ws);
    }

    [Fact]
    public void ListDirectory_empty_directory_returns_empty_entries()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        // Workspace vacío (solo el directorio)
        var outcome = executor.ExecuteToolWithoutJournal(ListCall("."), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El listado de directorio vacío debe tener éxito. summary=" + outcome.Summary);

        var doc = ParseJson(outcome.Preview!);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        Assert.Empty(entries);
        Assert.False(doc.RootElement.TryGetProperty("truncated", out _));

        Cleanup(ws);
    }

    [Fact]
    public void ListDirectory_nonexistent_directory_fails()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        var outcome = executor.ExecuteToolWithoutJournal(ListCall("no-existe"), false, CancellationToken.None);

        Assert.False(outcome.Succeeded, "Directorio inexistente debe fallar. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Failed, outcome.FinalState);
        Assert.Contains("no encontrado", outcome.Summary, StringComparison.OrdinalIgnoreCase);

        Cleanup(ws);
    }

    [Fact]
    public void ListDirectory_specific_subdirectory_lists_only_that_content()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        Directory.CreateDirectory(Path.Combine(ws, "subdir"));
        File.WriteAllText(Path.Combine(ws, "root.txt"), "root");
        File.WriteAllText(Path.Combine(ws, "subdir", "inner.txt"), "inner");

        var outcome = executor.ExecuteToolWithoutJournal(ListCall("subdir"), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "Listado de subdirectorio debe tener éxito. summary=" + outcome.Summary);

        var doc = ParseJson(outcome.Preview!);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        Assert.Single(entries);
        Assert.Equal("subdir/inner.txt", entries[0].GetProperty("path").GetString());
        Assert.Equal("file", entries[0].GetProperty("kind").GetString());

        Cleanup(ws);
    }

    [Fact]
    public void ListDirectory_recursive_maxEntries_applies_to_total()
    {
        var ws = TempDir();
        var executor = ListExecutor(ws);

        // Create nested structure with many files
        Directory.CreateDirectory(Path.Combine(ws, "d1", "d2", "d3"));
        for (int i = 0; i < 30; i++)
        {
            File.WriteAllText(Path.Combine(ws, $"root{i}.txt"), $"r{i}");
        }
        for (int i = 0; i < 30; i++)
        {
            File.WriteAllText(Path.Combine(ws, "d1", $"d1_{i}.txt"), $"d1_{i}");
        }
        for (int i = 0; i < 30; i++)
        {
            File.WriteAllText(Path.Combine(ws, "d1", "d2", $"d2_{i}.txt"), $"d2_{i}");
        }

        var outcome = executor.ExecuteToolWithoutJournal(ListCall(".", recursive: true, maxEntries: 50), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "El listado recursivo truncado debe tener éxito. summary=" + outcome.Summary);

        var doc = ParseJson(outcome.Preview!);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());

        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(50, entries.Length);

        Cleanup(ws);
    }

    [Fact]
    public void ListDirectory_recursive_does_not_follow_a_link_that_leaves_the_workspace()
    {
        var ws = TempDir();
        var outside = TempDir();
        File.WriteAllText(Path.Combine(outside, "outside-secret-name.txt"), "x");
        File.WriteAllText(Path.Combine(ws, "inside.txt"), "y");
        if (!TryCreateDirectoryLink(Path.Combine(ws, "escape"), outside))
        {
            Cleanup(ws);
            Cleanup(outside);
            Assert.Skip("El entorno no permite crear enlaces de directorio.");
        }

        var outcome = ListExecutor(ws).ExecuteToolWithoutJournal(ListCall(".", recursive: true), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
        var paths = ParseJson(outcome.Preview!).RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()).ToArray();
        Assert.Contains("inside.txt", paths);
        Assert.DoesNotContain("escape", paths);
        Assert.DoesNotContain(paths, p => p!.Contains("outside-secret-name", StringComparison.Ordinal));
        Directory.Delete(Path.Combine(ws, "escape"));
        Cleanup(ws);
        Cleanup(outside);
    }

    private static bool TryCreateDirectoryLink(string linkPath, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("mklink");
                start.ArgumentList.Add("/J");
                start.ArgumentList.Add(linkPath);
                start.ArgumentList.Add(target);
                using var process = System.Diagnostics.Process.Start(start);
                process?.WaitForExit(15000);
                return process is { ExitCode: 0 } && Directory.Exists(linkPath);
            }

            Directory.CreateSymbolicLink(linkPath, target);
            return Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ScriptedToolExecutor SearchExecutor(string wsDir)
    {
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeSimulationTools: false, includeMutationTools: false);
        var policy = ScriptedPermissionPolicy.WithTool("search.text", PermissionDecision.Allow);
        return ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, wsDir);
    }

    private static ValidatedToolCall SearchCall(string pattern, bool? regex = null, string? path = null, string? glob = null, int? maxResults = null, bool? caseSensitive = null)
    {
        var args = new Dictionary<string, object> { ["pattern"] = pattern };
        if (regex.HasValue) args["regex"] = regex.Value;
        if (path != null) args["path"] = path;
        if (glob != null) args["glob"] = glob;
        if (maxResults.HasValue) args["maxResults"] = maxResults.Value;
        if (caseSensitive.HasValue) args["caseSensitive"] = caseSensitive.Value;

        var json = JsonSerializer.Serialize(args);
        return new ValidatedToolCall(ToolCallId.New(), new ToolId("search.text"),
            "pc-" + Guid.NewGuid().ToString("N").Substring(0, 6), json);
    }

    // ---------------------------------------------------------------- search.text (M2 EPIC-018)

    [Fact]
    public void Search_literal_match_returns_relative_path_line_and_text()
    {
        var ws = TempDir();
        // Subdirectorio creado ANTES de escribir el archivo: la búsqueda es recursiva.
        Directory.CreateDirectory(Path.Combine(ws, "docs"));
        File.WriteAllText(Path.Combine(ws, "docs", "notes.md"), "first line\nthe needle is here\nlast line\n");
        File.WriteAllText(Path.Combine(ws, "other.txt"), "nothing relevant\n");

        var outcome = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle"), false, CancellationToken.None);

        Assert.True(outcome.Succeeded, "La búsqueda debe tener éxito. summary=" + outcome.Summary);
        Assert.Equal(ToolCallState.Succeeded, outcome.FinalState);

        var doc = ParseJson(outcome.Preview!);
        var matches = doc.RootElement.GetProperty("matches").EnumerateArray().ToArray();

        Assert.Single(matches);
        Assert.Equal("docs/notes.md", matches[0].GetProperty("path").GetString());
        Assert.Equal(2, matches[0].GetProperty("line").GetInt32());
        Assert.Equal("the needle is here", matches[0].GetProperty("text").GetString());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean(), "Sin tope no hay truncado");

        Cleanup(ws);
    }

    [Fact]
    public void Search_single_file_path_reports_workspace_relative_path()
    {
        var ws = TempDir();
        Directory.CreateDirectory(Path.Combine(ws, "sub"));
        File.WriteAllText(Path.Combine(ws, "root.txt"), "needle root\n");
        File.WriteAllText(Path.Combine(ws, "sub", "target.txt"), "needle sub\n");
        File.WriteAllText(Path.Combine(ws, "sub", "other.txt"), "nothing\n");

        var outcome = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle", path: "sub/target.txt"), false,
            CancellationToken.None);

        Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);

        var matches = ParseJson(outcome.Preview!).RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Single(matches);
        Assert.Equal("sub/target.txt", matches[0].GetProperty("path").GetString());
        Assert.Equal("needle sub", matches[0].GetProperty("text").GetString());

        Cleanup(ws);
    }

    [Fact]
    public void Search_is_case_insensitive_by_default_and_caseSensitive_restricts()
    {
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, "mix.txt"), "Hello NEEDLE here\nneedle again\n");

        var insensitive = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle"), false,
            CancellationToken.None);

        Assert.True(insensitive.Succeeded, "summary=" + insensitive.Summary);
        var matches = ParseJson(insensitive.Preview!).RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal(2, matches.Length);

        var sensitive = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle", caseSensitive: true), false,
            CancellationToken.None);

        Assert.True(sensitive.Succeeded, "summary=" + sensitive.Summary);
        var strict = ParseJson(sensitive.Preview!).RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Single(strict);
        Assert.Equal(2, strict[0].GetProperty("line").GetInt32()); // solo "needle again"

        Cleanup(ws);
    }

    [Fact]
    public void Search_regex_mode_matches_and_invalid_pattern_fails_the_tool()
    {
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, "r.txt"), "alpha-123\nbeta-456\ngamma\n");

        // Regex válida: los escapes JSON (\\d) deben llegar a la tool como \d reales.
        var ok = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall(@"\w+-(\d+)", regex: true), false,
            CancellationToken.None);

        Assert.True(ok.Succeeded, "summary=" + ok.Summary);
        var matches = ParseJson(ok.Preview!).RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal(2, matches.Length);
        Assert.Equal(1, matches[0].GetProperty("line").GetInt32());
        Assert.Equal("alpha-123", matches[0].GetProperty("text").GetString());

        // Regex inválida: ToolResult fallido (spec §71), nunca una excepción ni un falso ok.
        var invalid = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("(unclosed", regex: true), false,
            CancellationToken.None);

        Assert.False(invalid.Succeeded, "Un patrón regex inválido debe fallar la tool. summary=" + invalid.Summary);
        Assert.Equal(ToolCallState.Failed, invalid.FinalState);
        Assert.Contains("regex", invalid.Summary, StringComparison.OrdinalIgnoreCase);

        Cleanup(ws);
    }

    [Fact]
    public void Search_glob_filters_matches_by_file_name()
    {
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, "a.txt"), "needle one\n");
        File.WriteAllText(Path.Combine(ws, "b.cs"), "needle two\n");

        var outcome = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle", glob: "*.cs"), false,
            CancellationToken.None);

        Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
        var matches = ParseJson(outcome.Preview!).RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Single(matches);
        Assert.Equal("b.cs", matches[0].GetProperty("path").GetString());

        Cleanup(ws);
    }

    [Fact]
    public void Search_skips_binary_files_with_nul_in_first_8kb()
    {
        var ws = TempDir();
        // "needle\0needle": si no se detectara como binario, el patrón aparecería.
        File.WriteAllBytes(Path.Combine(ws, "blob.bin"), new byte[]
        {
            (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e', 0, (byte)'n', (byte)'e',
            (byte)'e', (byte)'d', (byte)'l', (byte)'e',
        });
        File.WriteAllText(Path.Combine(ws, "text.txt"), "needle here\n");

        var outcome = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle"), false,
            CancellationToken.None);

        Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
        var matches = ParseJson(outcome.Preview!).RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Single(matches);
        Assert.Equal("text.txt", matches[0].GetProperty("path").GetString());

        Cleanup(ws);
    }

    [Fact]
    public void Search_maxResults_cap_marks_the_result_truncated()
    {
        var ws = TempDir();
        var lines = Enumerable.Range(0, 10).Select(i => "hit " + i).ToArray();
        File.WriteAllText(Path.Combine(ws, "many.txt"), string.Join("\n", lines) + "\n");

        var outcome = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("hit", maxResults: 4), false,
            CancellationToken.None);

        Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
        var doc = ParseJson(outcome.Preview!);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean(), "El tope corta la búsqueda: truncated");

        var matches = doc.RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal(4, matches.Length);
        for (var i = 0; i < matches.Length; i++)
        {
            Assert.Equal(i + 1, matches[i].GetProperty("line").GetInt32());
        }

        Cleanup(ws);
    }

    [Fact]
    public void Search_never_returns_secret_or_git_content()
    {
        var ws = TempDir();
        File.WriteAllText(Path.Combine(ws, "readme.txt"), "needle visible\n");
        File.WriteAllText(Path.Combine(ws, ".env"), "needle SECRET=abc\n");
        Directory.CreateDirectory(Path.Combine(ws, ".git"));
        File.WriteAllText(Path.Combine(ws, ".git", "config"), "needle in git\n");

        // Recursivo: .env y .git/ no se buscan; su contenido nunca se devuelve.
        var listing = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle"), false,
            CancellationToken.None);

        Assert.True(listing.Succeeded, "summary=" + listing.Summary);
        var matches = ParseJson(listing.Preview!).RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Single(matches);
        Assert.Equal("readme.txt", matches[0].GetProperty("path").GetString());

        // Ruta de secretos directa: rechazo en Prepare (ToolCallRejected, nunca se ejecuta).
        var direct = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle", path: ".env"), false,
            CancellationToken.None);

        Assert.False(direct.Succeeded, "Un path de secretos debe rechazarse. summary=" + direct.Summary);
        Assert.Equal(ToolCallState.Rejected, direct.FinalState);
        Assert.Contains("secreto", direct.Summary, StringComparison.OrdinalIgnoreCase);

        Cleanup(ws);
    }

    [Fact]
    public void Search_path_escape_is_rejected()
    {
        var ws = TempDir();
        var outsideDir = Path.Combine(Path.GetTempPath(), "omnicore-m2-search-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "outside.txt"), "needle outside\n");
        try
        {
            var relativeEscape = Path.GetRelativePath(ws, outsideDir).Replace('\\', '/');
            var outcome = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle", path: relativeEscape),
                false, CancellationToken.None);

            Assert.False(outcome.Succeeded, "Un path que escapa del workspace debe fallar. summary=" + outcome.Summary);
            Assert.Equal(ToolCallState.Failed, outcome.FinalState);
            Assert.Contains("fuera del workspace", outcome.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(outsideDir);
        }

        Cleanup(ws);
    }

    [Fact]
    public void Search_does_not_traverse_a_directory_link_outside_the_workspace()
    {
        var ws = TempDir();
        var outside = TempDir();
        Directory.CreateDirectory(Path.Combine(ws, "sub"));
        File.WriteAllText(Path.Combine(ws, "sub", "inside.txt"), "needle inside\n");
        File.WriteAllText(Path.Combine(outside, "leak.txt"), "needle outside\n");
        if (!TryCreateDirectoryLink(Path.Combine(ws, "escape"), outside))
        {
            Cleanup(ws);
            Cleanup(outside);
            Assert.Skip("El entorno no permite crear enlaces de directorio.");
        }

        try
        {
            var outcome = SearchExecutor(ws).ExecuteToolWithoutJournal(SearchCall("needle"), false,
                CancellationToken.None);

            Assert.True(outcome.Succeeded, "summary=" + outcome.Summary);
            var doc = ParseJson(outcome.Preview!);
            var matches = doc.RootElement.GetProperty("matches").EnumerateArray().ToArray();
            var paths = matches.Select(m => m.GetProperty("path").GetString()).ToArray();

            Assert.Contains("sub/inside.txt", paths);
            Assert.DoesNotContain(paths, p => p!.Contains("escape", StringComparison.Ordinal));
            Assert.DoesNotContain(paths, p => p!.Contains("leak", StringComparison.Ordinal));
            Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            Directory.Delete(Path.Combine(ws, "escape"));
            Cleanup(ws);
            Cleanup(outside);
        }
    }
}
