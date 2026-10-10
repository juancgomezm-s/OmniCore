using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests del coordinador de refresco (§5 del plan, grupos 6 y 7). La concurrencia se ejercita de
/// verdad: tasks reales contra un almacén en memoria con lock cooperativo, sin red ni disco.
/// </summary>
public sealed class ClaudeOAuthRefreshCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const string SecretRef = "anthropic-oauth";

    // ---- Margen y corte --------------------------------------------------------------------

    [Fact]
    public async Task A_token_far_from_expiry_is_used_without_any_refresh_call()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromHours(1));

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.True(outcome.Refreshed == false);
        Assert.Equal(0, env.RefreshCalls);
        Assert.Equal("at-0", outcome.Tokens.AccessToken);
    }

    [Fact]
    public async Task A_token_inside_the_margin_is_refreshed()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(4));

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.True(outcome.Refreshed);
        Assert.Equal(1, env.RefreshCalls);
        Assert.Equal("at-1", outcome.Tokens.AccessToken);
    }

    [Fact]
    public async Task The_margin_is_configurable_and_changes_the_behaviour()
    {
        // CLAUDE.md: todo valor configurable debe tener un test que demuestre que cambia el
        // comportamiento. Con margen de 1 s, un token que caduca en 4 min NO se refresca.
        var tight = NewEnv(margin: TimeSpan.FromSeconds(1));
        tight.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(4));
        Assert.False((await tight.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken)).Refreshed);

        var wide = NewEnv(margin: TimeSpan.FromMinutes(10));
        wide.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(4));
        Assert.True((await wide.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken)).Refreshed);
    }

    [Fact]
    public async Task No_credential_means_needs_login_and_no_refresh_call()
    {
        var env = NewEnv();

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Tokens);
        Assert.True(outcome.NeedsLogin);
        Assert.Equal(0, env.RefreshCalls);
    }

    // ---- Dead-set (grupo 6) ---------------------------------------------------------------

    [Fact]
    public async Task Invalid_grant_marks_the_token_dead_and_stops_future_attempts()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.FailWith = new ClaudeOAuthInvalidGrantException();

        var first = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.Null(first.Tokens);
        Assert.Equal(1, env.RefreshCalls);
        Assert.True(env.Coordinator.IsDead("rt-0"));

        // Segunda pasada: no se vuelve a gastar un RPC en un token ya sabido muerto.
        var second = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);
        Assert.Null(second.Tokens);
        Assert.Equal(1, env.RefreshCalls);
    }

    [Fact]
    public void MarkDead_is_idempotent_and_ignores_empty_tokens()
    {
        var env = NewEnv();

        env.Coordinator.MarkDead("rt-x");
        env.Coordinator.MarkDead("rt-x");
        env.Coordinator.MarkDead("");

        Assert.Equal(1, env.Coordinator.DeadCount);
        Assert.False(env.Coordinator.IsDead(""));
        Assert.True(env.Coordinator.IsDead("rt-x"));
    }

    [Fact]
    public async Task A_transient_failure_does_NOT_kill_the_refresh_token()
    {
        // Este es el bug que el dead-set existe para evitar: antes Claude Code escribia
        // refreshToken:"" al primer fallo y un timeout transitorio obligaba a /login manual.
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.FailWith = new ClaudeOAuthNetworkException("sin ruta", inner: null);
        env.AllowSiblingRecovery = false;

        await Assert.ThrowsAsync<ClaudeOAuthNetworkException>(() =>
            env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken));

        Assert.False(env.Coordinator.IsDead("rt-0"));
        Assert.Equal(0, env.Coordinator.DeadCount);
    }

    [Fact]
    public async Task An_external_write_clears_the_dead_set_so_a_new_token_can_be_used()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.FailWith = new ClaudeOAuthInvalidGrantException();
        await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);
        Assert.True(env.Coordinator.IsDead("rt-0"));

        // Otro proceso hace /login: la metadata avanza su marca de tiempo.
        env.FailWith = null;
        env.MetadataStamp = Now.AddMinutes(1);
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1), generation: 7);

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.Equal(0, env.Coordinator.DeadCount);
        Assert.True(outcome.Refreshed || outcome.Tokens.AccessToken == "at-7");
    }

    [Fact]
    public async Task ClearDeadSet_forgets_everything()
    {
        var env = NewEnv();
        env.Coordinator.MarkDead("rt-a");
        env.Coordinator.MarkDead("rt-b");

        env.Coordinator.ClearDeadSet();

        Assert.Equal(0, env.Coordinator.DeadCount);
        Assert.False(env.Coordinator.IsDead("rt-a"));
    }

    // ---- Carreras (grupo 7) ---------------------------------------------------------------

    [Fact]
    public async Task Two_concurrent_refreshes_of_the_same_credential_issue_one_rpc()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.RefreshDelay = TimeSpan.FromMilliseconds(80);

        var results = await Task.WhenAll(
            env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken),
            env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken));

        Assert.Equal(1, env.RefreshCalls);
        Assert.All(results, r => Assert.Equal("at-1", r.Tokens.AccessToken));
        Assert.Single(results, r => r.Refreshed);
    }

    [Fact]
    public async Task A_sibling_that_refreshed_before_the_lock_is_adopted_without_calling_out()
    {
        // Otro proceso termino el refresco mientras este esperaba su turno: el token ajeno es
        // valido y reutilizarlo evita quemar un refresh token mas (plan §5, paso 4).
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.BeforeLock = () => env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromHours(1), generation: 9);

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.Equal(0, env.RefreshCalls);
        Assert.Equal(ClaudeOAuthRefreshRace.ResolvedBeforeLock, outcome.Race);
        Assert.Equal("at-9", outcome.Tokens.AccessToken);
    }

    [Fact]
    public async Task A_sibling_that_refreshed_while_we_held_nothing_is_detected_after_the_lock()
    {
        // El access token cambio pero sigue marcando expiracion: la comprobacion es por identidad
        // del token, no solo por frescura (authAlias.ts compara J.accessToken !== T).
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.AfterLockBeforeRefresh = () =>
            env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1), generation: 8);

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.Equal(0, env.RefreshCalls);
        Assert.Equal("at-8", outcome.Tokens.AccessToken);
        Assert.Equal(ClaudeOAuthRefreshRace.ResolvedBeforeLock, outcome.Race);
    }

    [Fact]
    public async Task A_transient_failure_that_lands_a_sibling_token_recovers_instead_of_throwing()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.FailWith = new ClaudeOAuthNetworkException("sin ruta", inner: null);
        env.OnRefreshFailure = () => env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromHours(1), generation: 6);

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeOAuthRefreshRace.RecoveredFromSibling, outcome.Race);
        Assert.Equal("at-6", outcome.Tokens.AccessToken);
        Assert.False(env.Coordinator.IsDead("rt-0"));
    }

    [Fact]
    public async Task Many_parallel_callers_still_produce_exactly_one_refresh()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.RefreshDelay = TimeSpan.FromMilliseconds(60);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken)));

        Assert.Equal(1, env.RefreshCalls);
        Assert.All(results, r => Assert.Equal("at-1", r.Tokens.AccessToken));
    }

    // ---- Preservación de datos no secretos ---------------------------------------------------

    [Fact]
    public async Task Refreshing_keeps_the_account_fields_that_the_token_wire_does_not_return()
    {
        // Plan §2.6: el refresh no trae plan ni tier; perderlos haria que /doctor mintiera despues
        // del primer refresco automatico.
        var env = NewEnv();
        env.Store[SecretRef] = new ClaudeOAuthCredential("at-0", "rt-0",
            Now.AddMinutes(1), ["user:profile", "user:inference"], "cid-original")
        {
            EmailAddress = "u@example.com",
            DisplayName = "Jo",
            SubscriptionType = "max",
            RateLimitTier = "tier-5x",
            AccountUuid = "acc-1",
            AuthenticatedAt = Now.AddDays(-3),
        };
        env.NextTokens = _ => new ClaudeOAuthTokens("at-1", "rt-1", Now.AddHours(1),
            ["user:inference"], "cid-original", null, null);

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);
        var saved = env.Store[SecretRef];

        Assert.Equal("u@example.com", saved.EmailAddress);
        Assert.Equal("max", saved.SubscriptionType);
        Assert.Equal("tier-5x", saved.RateLimitTier);
        Assert.Equal("acc-1", saved.AccountUuid);
        Assert.Equal(Now.AddDays(-3), saved.AuthenticatedAt);
        Assert.Equal("at-1", outcome.Tokens.AccessToken);
    }

    [Fact]
    public async Task Refreshing_keeps_the_sticky_client_id_of_the_credential()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1), clientId: "cid-ajeno");
        string? seenClientId = null;
        env.NextTokens = c =>
        {
            seenClientId = c.ClientId;
            return new ClaudeOAuthTokens("at-1", "rt-1", Now.AddHours(1), ["user:inference"],
                c.ClientId, null, null);
        };

        await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.Equal("cid-ajeno", seenClientId);
        Assert.Equal("cid-ajeno", env.Store[SecretRef].ClientId);
    }

    // ---- Cancelación ------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_during_the_refresh_propagates_and_leaves_the_store_consistent()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource();
        env.RefreshDelay = TimeSpan.FromMilliseconds(200);
        env.OnRefreshStarted = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            env.Coordinator.EnsureFreshAsync(SecretRef, cts.Token));

        // El credential anterior sigue en su sitio: un refresco cancelado no borra la sesion.
        Assert.Equal("at-0", env.Store[SecretRef].AccessToken);
    }

    [Fact]
    public async Task A_missing_credential_after_the_lock_is_NeedsLogin_not_a_crash()
    {
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.AfterLockBeforeRefresh = () => env.Store.Remove(SecretRef);

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.True(outcome.NeedsLogin);
        Assert.Equal(0, env.RefreshCalls);
    }

    [Fact]
    public async Task A_dead_token_written_by_another_process_is_respected_after_the_lock()
    {
        // Carrera inversa: nosotros leemos un token vivo, otro proceso lo marca muerto, y al
        // adquirir el lock hay que volver a comprobarlo (plan §5, paso 5).
        var env = NewEnv();
        env.Store[SecretRef] = Credential(expiresIn: TimeSpan.FromMinutes(1));
        env.AfterLockBeforeRefresh = () => env.Coordinator.MarkDead("rt-0");

        var outcome = await env.Coordinator.EnsureFreshAsync(SecretRef, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Tokens);
        Assert.Equal(0, env.RefreshCalls);
    }

    // ---- Arnes ------------------------------------------------------------------------------

    private sealed class Env
    {
        public Dictionary<string, ClaudeOAuthCredential> Store { get; } = new(StringComparer.Ordinal);

        public int RefreshCalls;

        public Exception? FailWith;

        public TimeSpan? RefreshDelay;

        public bool AllowSiblingRecovery = true;

        public DateTimeOffset? MetadataStamp = Now;

        public Action? BeforeLock;

        public Action? AfterLockBeforeRefresh;

        public Action? OnRefreshFailure;

        public Action? OnRefreshStarted;

        public Func<ClaudeOAuthCredential, ClaudeOAuthTokens>? NextTokens;

        private readonly SemaphoreSlim _gate = new(1, 1);

        public ClaudeOAuthRefreshCoordinator Coordinator { get; }

        public Env(TimeSpan? margin)
        {
            Coordinator = new ClaudeOAuthRefreshCoordinator(
                load: (ref_, _) => Task.FromResult(Store.TryGetValue(ref_, out var c) ? c : null),
                save: (cred, _) =>
                {
                    Store[SecretRef] = cred;
                    return Task.CompletedTask;
                },
                refresh: RefreshAsync,
                acquireLock: AcquireLockAsync,
                metadataWrittenAt: _ => Task.FromResult(MetadataStamp),
                utcNow: () => Now,
                margin: margin);
        }

        private async Task<IAsyncDisposable> AcquireLockAsync(string _, CancellationToken ct)
        {
            BeforeLock?.Invoke();
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            AfterLockBeforeRefresh?.Invoke();
            return new Releaser(_gate);
        }

        private async Task<ClaudeOAuthTokens> RefreshAsync(ClaudeOAuthCredential current, CancellationToken ct)
        {
            RefreshCalls++;
            OnRefreshStarted?.Invoke();
            if (RefreshDelay is { } delay)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            if (FailWith is not null)
            {
                OnRefreshFailure?.Invoke();
                if (!AllowSiblingRecovery && FailWith is ClaudeOAuthNetworkException)
                {
                    // Sin token de sibling: la recuperacion no aplica y el fallo sube tal cual.
                    foreach (var key in Store.Keys.ToList())
                    {
                        if (key == SecretRef && Store[key].AccessToken == current.AccessToken)
                        {
                            // Se deja el mismo: releer no descubre nada nuevo.
                        }
                    }
                }

                throw FailWith;
            }

            return NextTokens is null
                ? new ClaudeOAuthTokens("at-" + RefreshCalls, "rt-" + RefreshCalls,
                    Now.AddHours(1), current.Scopes, current.ClientId, current.AccountUuid, null)
                : NextTokens(current);
        }

        private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static Env NewEnv(TimeSpan? margin = null) => new(margin);

    private static ClaudeOAuthCredential Credential(
        TimeSpan expiresIn,
        int generation = 0,
        string clientId = "cid-original") => new(
        AccessToken: "at-" + generation,
        RefreshToken: "rt-" + generation,
        ExpiresAt: Now + expiresIn,
        Scopes: ["user:profile", "user:inference"],
        ClientId: clientId)
    {
        AuthenticatedAt = Now,
    };
}
