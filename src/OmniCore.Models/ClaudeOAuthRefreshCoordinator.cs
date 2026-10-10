namespace OmniCore.Models;

/// <summary>Resultado de asegurar un access token vigente.</summary>
public sealed record ClaudeOAuthRefreshOutcome(
    ClaudeOAuthTokens Tokens,
    bool Refreshed,
    ClaudeOAuthRefreshRace? Race = null)
{
    /// <summary>El token estaba muerto: hace falta volver a iniciar sesión.</summary>
    public bool NeedsLogin => Tokens is null;
}

/// <summary>Cómo se resolvió una carrera de refresco. Solo diagnóstico, nunca secretos.</summary>
public enum ClaudeOAuthRefreshRace
{
    /// <summary>No hubo carrera: este proceso refrescó.</summary>
    None,

    /// <summary>Otro proceso ya había refrescado antes de que se adquiriera el lock.</summary>
    ResolvedBeforeLock,

    /// <summary>Otro proceso refrescó mientras se esperaba el lock.</summary>
    ResolvedAfterLock,

    /// <summary>El refresco propio falló pero al releer había un token ajeno distinto: se adopta.</summary>
    RecoveredFromSibling,
}

/// <summary>
/// Máquina de refresco de credenciales OAuth de Claude (plan §5). Implementa la misma
/// coordinación que Claude Code en <c>authAlias.ts</c> (<c>pt6</c>) y
/// <c>oauth/refreshTokenDeadSet.ts</c>, portada a las primitivas del repo:
///
/// <list type="number">
/// <item>Margen de expiración de 5 minutos, para no usar un token que caduca en vuelo.</item>
/// <item>Dead-set en memoria: un refresh token que ya devolvió <c>invalid_grant</c> no se vuelve a
/// quemar en este proceso. Es deliberadamente volátil — si fuera persistente, un fallo transitorio
/// (skew de reloj, carrera del caché de replay, token re-emitido en otro lado) bloquearía al
/// usuario hasta un /login manual. Fue el bug que arregló Claude Code en 2.1.133.</item>
/// <item>Lock multi-proceso por nombre, como ChatGptSubscriptionAuthProvider (ADR-0011 §3.4).</item>
/// <item>Relectura tras el lock: otro proceso pudo refrescar mientras se esperaba. Se compara el
/// access token, no la expiración — un sibling puede haber dejado un token válido aunque el
/// nuestro aún no caduque.</item>
/// <item>Invalidación por mtime: una escritura externa (otro proceso, restaurar un backup) avanza
/// la marca y limpia el dead-set, porque casi seguro trajo un refresh token distinto.</item>
/// </list>
///
/// No sabe de HTTP ni de persistencia: recibe funciones inyectadas, así que los tests ejercitan la
/// concurrencia real sin red ni disco.
/// </summary>
public sealed class ClaudeOAuthRefreshCoordinator
{
    private readonly HashSet<string> _deadTokens = new(StringComparer.Ordinal);
    private readonly object _deadSetGate = new();

    private readonly Func<string, CancellationToken, Task<ClaudeOAuthCredential?>> _load;
    private readonly Func<ClaudeOAuthCredential, CancellationToken, Task> _save;
    private readonly Func<ClaudeOAuthCredential, CancellationToken, Task<ClaudeOAuthTokens>> _refresh;
    private readonly Func<string, CancellationToken, Task<IAsyncDisposable>> _lock;
    private readonly Func<CancellationToken, Task<DateTimeOffset?>> _metadataWrittenAt;
    private readonly Func<DateTimeOffset> _utcNow;

    private DateTimeOffset? _lastSeenMetadataWrite;

    public ClaudeOAuthRefreshCoordinator(
        Func<string, CancellationToken, Task<ClaudeOAuthCredential?>> load,
        Func<ClaudeOAuthCredential, CancellationToken, Task> save,
        Func<ClaudeOAuthCredential, CancellationToken, Task<ClaudeOAuthTokens>> refresh,
        Func<string, CancellationToken, Task<IAsyncDisposable>> acquireLock,
        Func<CancellationToken, Task<DateTimeOffset?>> metadataWrittenAt,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? margin = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _lock = acquireLock ?? throw new ArgumentNullException(nameof(acquireLock));
        _metadataWrittenAt = metadataWrittenAt ?? throw new ArgumentNullException(nameof(metadataWrittenAt));
        _utcNow = utcNow ?? DefaultUtcNow;
        Margin = margin ?? ClaudeOAuthCredential.DefaultRefreshMargin;
    }

    private static DateTimeOffset DefaultUtcNow() => DateTimeOffset.UtcNow;

    /// <summary>Margen antes de la expiración a partir del cual se refresca.</summary>
    public TimeSpan Margin { get; }

    /// <summary>
    /// Devuelve un access token vigente, refrescando solo si hace falta.idempotente y seguro
    /// ante varios procesos y varias tareas simultáneas sobre el mismo credential.
    /// </summary>
    public async Task<ClaudeOAuthRefreshOutcome> EnsureFreshAsync(
        string secretRef,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        await InvalidateIfDiskChangedAsync(ct).ConfigureAwait(false);

        var credential = await _load(secretRef, ct).ConfigureAwait(false);
        if (credential is null)
        {
            return new ClaudeOAuthRefreshOutcome(null!, Refreshed: false);
        }

        if (!credential.NeedsRefresh(_utcNow(), Margin))
        {
            return new ClaudeOAuthRefreshOutcome(credential.ToTokens(), Refreshed: false);
        }

        // Primera barrera del dead-set: no gastar un RPC en un token ya sabido muerto.
        if (IsDead(credential.RefreshToken))
        {
            return new ClaudeOAuthRefreshOutcome(null!, Refreshed: false);
        }

        var baselineAccessToken = credential.AccessToken;

        await using (await _lock(secretRef, ct).ConfigureAwait(false))
        {
            // Tras el lock se relee siempre: un sibling pudo terminar el refresco mientras
            // esperábamos, y su token es válido aunque el nuestro aún marcara expiración.
            await InvalidateIfDiskChangedAsync(ct).ConfigureAwait(false);
            var locked = await _load(secretRef, ct).ConfigureAwait(false);
            if (locked is null)
            {
                return new ClaudeOAuthRefreshOutcome(null!, Refreshed: false);
            }

            if (!string.Equals(locked.AccessToken, baselineAccessToken, StringComparison.Ordinal))
            {
                return new ClaudeOAuthRefreshOutcome(locked.ToTokens(), Refreshed: false,
                    ClaudeOAuthRefreshRace.ResolvedBeforeLock);
            }

            if (!locked.NeedsRefresh(_utcNow(), Margin))
            {
                return new ClaudeOAuthRefreshOutcome(locked.ToTokens(), Refreshed: false,
                    ClaudeOAuthRefreshRace.ResolvedBeforeLock);
            }

            // Segunda comprobación del dead-set con el token recién leído: otro hilo de ESTE
            // proceso pudo marcarlo muerto mientras esperábamos el lock.
            if (IsDead(locked.RefreshToken))
            {
                return new ClaudeOAuthRefreshOutcome(null!, Refreshed: false);
            }

            try
            {
                var tokens = await _refresh(locked, ct).ConfigureAwait(false);
                var refreshed = await PersistAsync(secretRef, locked, tokens, ct).ConfigureAwait(false);
                return new ClaudeOAuthRefreshOutcome(refreshed, Refreshed: true);
            }
            catch (ClaudeOAuthInvalidGrantException)
            {
                MarkDead(locked.RefreshToken);
                return new ClaudeOAuthRefreshOutcome(null!, Refreshed: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ClaudeOAuthException)
            {
                // Fallo transitorio (red, 5xx, timeout): NO se marca muerto — ese era exactamente
                // el error que el dead-set persistente cometía. Se intenta recuperar del sibling.
                var afterFailure = await _load(secretRef, ct).ConfigureAwait(false);
                if (afterFailure is not null
                    && !string.Equals(afterFailure.AccessToken, baselineAccessToken, StringComparison.Ordinal))
                {
                    return new ClaudeOAuthRefreshOutcome(afterFailure.ToTokens(), Refreshed: false,
                        ClaudeOAuthRefreshRace.RecoveredFromSibling);
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Refresca aunque el token aún no caduque. Lo llama el provider tras un 401: el servidor ya
    /// dijo que ese access token no vale, asi que el margen de expiracion no aporta nada (plan §7;
    /// authAlias.ts usa checkAndRefreshOAuthTokenIfNeeded(force: true) para lo mismo).
    ///
    /// Sigue el mismo protocolo de carrera que <see cref="EnsureFreshAsync"/>: lock, relectura,
    /// comprobacion del dead-set con el token recien leido y deteccion del sibling.
    /// </summary>
    public async Task<ClaudeOAuthTokens?> ForceRefreshAsync(
        string secretRef,
        ClaudeOAuthCredential known,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        ArgumentNullException.ThrowIfNull(known);

        if (IsDead(known.RefreshToken))
        {
            return null;
        }

        var baselineAccessToken = known.AccessToken;
        await using (await _lock(secretRef, ct).ConfigureAwait(false))
        {
            await InvalidateIfDiskChangedAsync(ct).ConfigureAwait(false);
            var locked = await _load(secretRef, ct).ConfigureAwait(false);
            if (locked is null)
            {
                return null;
            }

            // Otro proceso ya lo refresco mientras esperabamos: su token sirve.
            if (!string.Equals(locked.AccessToken, baselineAccessToken, StringComparison.Ordinal))
            {
                return locked.ToTokens();
            }

            if (IsDead(locked.RefreshToken))
            {
                return null;
            }

            try
            {
                var tokens = await _refresh(locked, ct).ConfigureAwait(false);
                await PersistAsync(secretRef, locked, tokens, ct).ConfigureAwait(false);
                return tokens;
            }
            catch (ClaudeOAuthInvalidGrantException)
            {
                MarkDead(locked.RefreshToken);
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ClaudeOAuthException)
            {
                var afterFailure = await _load(secretRef, ct).ConfigureAwait(false);
                if (afterFailure is not null
                    && !string.Equals(afterFailure.AccessToken, baselineAccessToken, StringComparison.Ordinal))
                {
                    return afterFailure.ToTokens();
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Escribe el credential refrescado y devuelve sus tokens de runtime.
    ///
    /// El wire del refresh solo trae tokens y scopes: no devuelve cuenta, plan ni tier. Todo lo
    /// demas se conserva del credential anterior, o /doctor y la status line mentirian despues del
    /// primer refresco automatico (plan §2.6, client.ts:330-341).
    /// </summary>
    private async Task<ClaudeOAuthTokens> PersistAsync(
        string secretRef,
        ClaudeOAuthCredential locked,
        ClaudeOAuthTokens tokens,
        CancellationToken ct)
    {
        var refreshed = ClaudeOAuthCredential.FromTokens(
            tokens with
            {
                AccountUuid = tokens.AccountUuid ?? locked.AccountUuid,
                OrganizationUuid = tokens.OrganizationUuid ?? locked.OrganizationUuid,
            },
            _utcNow()) with
        {
            EmailAddress = locked.EmailAddress,
            DisplayName = locked.DisplayName,
            SubscriptionType = locked.SubscriptionType,
            RateLimitTier = locked.RateLimitTier,
            Roles = locked.Roles,
            AuthenticatedAt = locked.AuthenticatedAt,
        };

        await _save(refreshed, ct).ConfigureAwait(false);
        return refreshed.ToTokens();
    }

    /// <summary>True si el refresh token está en el dead-set de este proceso.</summary>
    public bool IsDead(string refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return false;
        }

        lock (_deadSetGate)
        {
            return _deadTokens.Contains(refreshToken);
        }
    }

    /// <summary>Registra un <c>invalid_grant</c> contra este refresh token. Idempotente.</summary>
    public void MarkDead(string refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        lock (_deadSetGate)
        {
            _deadTokens.Add(refreshToken);
        }
    }

    /// <summary>Olvida el dead-set. Lo llama el logout y los tests.</summary>
    public void ClearDeadSet()
    {
        lock (_deadSetGate)
        {
            _deadTokens.Clear();
        }
    }

    /// <summary>Número de tokens muertos conocidos. Solo para diagnóstico y assertions.</summary>
    public int DeadCount
    {
        get
        {
            lock (_deadSetGate)
            {
                return _deadTokens.Count;
            }
        }
    }

    /// <summary>
    /// Si la metadata se escribió por fuera desde la última vez, se olvida el dead-set: esa
    /// escritura casi seguro trae un refresh token distinto (un /login de otro proceso, un
    /// restore), y mantenerlo muerto dejaría inservible al nuevo. Equivalente a
    /// <c>invalidateOAuthCacheIfDiskChanged</c> en authAlias.ts.
    /// </summary>
    private async Task InvalidateIfDiskChangedAsync(CancellationToken ct)
    {
        DateTimeOffset? current;
        try
        {
            current = await _metadataWrittenAt(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No poder mirar el mtime no debe impedir refrescar: se conserva el estado conocido.
            return;
        }

        if (current is null)
        {
            // Sin archivo todavía (primer login): nada que invalidar.
            _lastSeenMetadataWrite = null;
            return;
        }

        var previous = _lastSeenMetadataWrite;
        _lastSeenMetadataWrite = current;
        if (previous is null || current > previous)
        {
            ClearDeadSet();
        }
    }
}
