using OmniCore.Models;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class ClaudeOAuthRescueRegressionTests
{
    private const string Access = "rescue-access-fixture-0123456789";
    private const string Refresh = "rescue-refresh-fixture-0123456789";

    [Fact]
    public async Task Mutex_release_survives_thread_switch()
    {
        using var ownerCanExit = new ManualResetEventSlim();
        var result = new TaskCompletionSource<MutexHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            try
            {
                result.SetResult(MutexHandle.TryAcquire("fixture-" + Guid.NewGuid().ToString("N"))!);
                ownerCanExit.Wait();
            }
            catch (Exception ex) { result.TrySetException(ex); }
        }) { IsBackground = true };
        owner.Start();
        try
        {
            var held = await result.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotNull(held);
            // Keep a second handle alive: closing the owner's handle must not hide an
            // unreleased, still-owned kernel mutex by destroying its last reference.
            using var observer = new Mutex(false, held.Name);
            await Task.Run(held.Release, TestContext.Current.CancellationToken);
            var acquired = observer.WaitOne(TimeSpan.Zero);
            try { Assert.True(acquired, "The async disposer left the mutex owned by another thread."); }
            finally { if (acquired) observer.ReleaseMutex(); }
        }
        finally { ownerCanExit.Set(); Assert.True(owner.Join(TimeSpan.FromSeconds(5))); }
    }

    [Fact]
    public void Credential_token_and_bearer_diagnostics_do_not_reveal_secrets()
    {
        var credential = new ClaudeOAuthCredential(Access, Refresh, DateTimeOffset.UtcNow.AddHours(1),
            [ClaudeOAuthScopes.Inference], "fixture-client");
        var tokens = credential.ToTokens();
        var bearer = new ClaudeOAuthBearer(Access, "fixture-account", credential.Scopes);
        object[] diagnostics = [credential, tokens, bearer, new ClaudeOAuthRefreshOutcome(tokens, false)];
        foreach (var diagnostic in diagnostics)
        {
            var text = diagnostic.ToString()!;
            Assert.DoesNotContain(Access, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Refresh, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Reload_preserves_account_enrichment_before_refresh()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omni-oauth-rescue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(),
                Path.Combine(directory, "metadata.json"));
            var credential = new ClaudeOAuthCredential(Access, Refresh, DateTimeOffset.UtcNow.AddHours(1),
                [ClaudeOAuthScopes.Inference], "fixture-client")
            {
                EmailAddress = "fixture@example.invalid", DisplayName = "Fixture",
                SubscriptionType = "fixture-plan", RateLimitTier = "fixture-tier",
                AuthenticatedAt = DateTimeOffset.FromUnixTimeSeconds(1700000000),
            };
            store.Save("fixture-ref", credential, TestContext.Current.CancellationToken);
            var loaded = Assert.IsType<ClaudeOAuthCredential>(store.Load("fixture-ref", TestContext.Current.CancellationToken));
            Assert.Equal(credential.EmailAddress, loaded.EmailAddress);
            Assert.Equal(credential.DisplayName, loaded.DisplayName);
            Assert.Equal(credential.SubscriptionType, loaded.SubscriptionType);
            Assert.Equal(credential.RateLimitTier, loaded.RateLimitTier);
            Assert.Equal(credential.AuthenticatedAt, loaded.AuthenticatedAt);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
