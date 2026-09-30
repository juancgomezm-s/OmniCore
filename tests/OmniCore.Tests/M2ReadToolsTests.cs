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
}