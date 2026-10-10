using OmniCore.Host;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests del lock de refresco entre procesos (§5 del plan, parte del grupo 7). Usa disco temporal
// real porque un lock simulado no prueba nada: lo que importa es que dos poseedores no puedan
/// coexistir.
/// </summary>
public sealed class ClaudeOAuthRefreshLockTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "omni-oauth-lock-" + Guid.NewGuid().ToString("N"));

    public ClaudeOAuthRefreshLockTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Acquire_then_release_leaves_the_lock_free_for_the_next_caller()
    {
        var first = await ClaudeOAuthRefreshLock.AcquireAsync(_directory, "ref", TestContext.Current.CancellationToken);
        await first.DisposeAsync();

        await using var second = await ClaudeOAuthRefreshLock.AcquireAsync(_directory, "ref",
            TestContext.Current.CancellationToken);

        Assert.NotNull(second);
    }

    [Fact]
    public async Task Two_holders_of_the_same_secret_ref_cannot_coexist()
    {
        await using var held = await ClaudeOAuthRefreshLock.AcquireAsync(_directory, "ref",
            TestContext.Current.CancellationToken);

        // Sin liberar el primero, el segundo debe fallar tras reintentar, no colgarse.
        var ex = await Assert.ThrowsAsync<ClaudeOAuthLockException>(() =>
            ClaudeOAuthRefreshLock.AcquireAsync(_directory, "ref", TestContext.Current.CancellationToken,
                TimeSpan.FromMilliseconds(200)));

        Assert.True(ex.Attempts >= 1);
        Assert.Contains("claude-oauth-", ex.LockFilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Different_secret_refs_do_not_block_each_other()
    {
        await using var one = await ClaudeOAuthRefreshLock.AcquireAsync(_directory, "anthropic-oauth",
            TestContext.Current.CancellationToken);
        await using var other = await ClaudeOAuthRefreshLock.AcquireAsync(_directory, "otro-oauth",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(one.LockFilePath, other.LockFilePath);
    }

    [Fact]
    public async Task The_lock_file_records_which_process_holds_it()
    {
        // omni doctor tiene que poder decir QUE proceso esta refrescando, sin adivinarlo: se lee
        // mientras el lock sigue tomado, con la misma apertura compartida que usaria el comando.
        await using var held = await ClaudeOAuthRefreshLock.AcquireAsync(_directory, "ref",
            TestContext.Current.CancellationToken);

        string content;
        using (var read = new FileStream(held.LockFilePath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite))
        using (var reader = new StreamReader(read))
        {
            content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        }

        Assert.StartsWith(Environment.ProcessId.ToString(), content, StringComparison.Ordinal);
        Assert.Contains("@", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_while_waiting_stops_immediately()
    {
        await using var held = await ClaudeOAuthRefreshLock.AcquireAsync(_directory, "ref",
            TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ClaudeOAuthRefreshLock.AcquireAsync(_directory, "ref", cts.Token, TimeSpan.FromSeconds(30)));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "la cancelacion no surtio efecto");
    }

    [Fact]
    public async Task A_second_waiter_gets_in_once_the_first_releases()
    {
        var held = await ClaudeOAuthRefreshLock.AcquireAsync(_directory, "ref", TestContext.Current.CancellationToken);
        var waiter = ClaudeOAuthRefreshLock.AcquireAsync(_directory, "ref",
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(20));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        await held.DisposeAsync();
        await using var acquired = await waiter;

        Assert.NotNull(acquired);
    }

    [Fact]
    public async Task The_lock_file_name_is_derived_from_the_secret_ref_and_is_path_safe()
    {
        // La clave puede llevar caracteres que no caben en un nombre de archivo: el hash la
        // normaliza y dos refs distintas siguen dando nombres distintos.
        var nested = Path.Combine(_directory, "weird");
        Directory.CreateDirectory(nested);
        const string Weird = "provider/oauth:con caracteres?";

        await using var held = await ClaudeOAuthRefreshLock.AcquireAsync(nested, Weird,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(held.LockFilePath));
        Assert.Equal("weird", Path.GetFileName(Path.GetDirectoryName(held.LockFilePath)));
        Assert.StartsWith("claude-oauth-", Path.GetFileName(held.LockFilePath), StringComparison.Ordinal);

        await using var other = await ClaudeOAuthRefreshLock.AcquireAsync(
            Path.Combine(_directory, "other"), "otra-ref", TestContext.Current.CancellationToken);
        Assert.NotEqual(Path.GetFileName(held.LockFilePath), Path.GetFileName(other.LockFilePath));
    }

    [Fact]
    public async Task Arguments_are_validated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ClaudeOAuthRefreshLock
            .AcquireAsync("", "ref", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => ClaudeOAuthRefreshLock
            .AcquireAsync(_directory, " ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_exception_message_names_the_ref_and_the_path_without_secrets()
    {
        await using var held = await ClaudeOAuthRefreshLock.AcquireAsync(_directory, "mi-ref",
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ClaudeOAuthLockException>(() =>
            ClaudeOAuthRefreshLock.AcquireAsync(_directory, "mi-ref", TestContext.Current.CancellationToken,
                TimeSpan.FromMilliseconds(100)));

        Assert.Contains("mi-ref", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<ClaudeOAuthException>(ex);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
