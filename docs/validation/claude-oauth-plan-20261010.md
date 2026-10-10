# Plan de construcción — Login OAuth nativo de Anthropic usando la identidad de Claude Code (v3, 2026-10-10)

> **Estado del documento:** PLAN. No es la construcción, no implementa nada, no autentica nada.
> Especifica cómo se construirá el login por cuenta de Claude dentro de OmniCore usando el
> protocolo e identidad de Claude Code. Sustituye al bloqueo de §3.3/§8/§10.7 de ADR-0011 (rev. 5)
> y al informe `provider-connections-account-20261008.md` en todo lo referente al flujo por cuenta.

## 0. Decisión de alcance

Se implementa **una máquina de estados de login OAuth 2.0 (authorization code + PKCE S256)**
que habla con los endpoints de Anthropic usando la identidad de Claude Code (client id,
user-agent y scopes de Claude Code) y produce un `ClaudeOAuthCredential` cifrado, con el que el
`AnthropicMessagesProvider` puede emitir requests de inferencia usando
`Authorization: Bearer` + beta `oauth-2025-04-20`.

Referencia de comportamiento: `github.com/Icarus603/claude-code` @
`7c918f786828f7071d023e1c401bc6be754446e3` (clon de solo lectura, derivado del sourcemap npm
v2.1.88). Se porta el **protocolo completo** (endpoints, PKCE, callback, exchange, refresh,
scopes, cabeceras de inferencia) **y la identidad de cliente** (client id, user-agent, scopes).

**Toda la lógica por nombre de modelo/provider sigue prohibida (INV-007).** El flujo se declara
por `AuthKind.OAuth` + `ProviderDescriptor`, nunca por `if (provider == "anthropic")`.

## 1. Identidad de cliente

La identidad de cliente es fija y coincide con la de Claude Code. No hay elección: OmniCore se
identifica como Claude Code en todos los requests OAuth y de inferencia.

```csharp
// src/OmniCore.Models/ClaudeOAuthClientIdentity.cs
public sealed record ClaudeOAuthClientIdentity(
    string ClientId,
    string UserAgentTemplate,   // con {version} para sustituir
    IReadOnlyList<string> Scopes,
    string AuthorizeUrl,
    string TokenUrl)
{
    public static ClaudeOAuthClientIdentity ClaudeCode { get; } = new(
        ClientId:          "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
        UserAgentTemplate: "claude-cli/{version} (external, cli)",
        Scopes:            new[] { "user:profile", "user:inference",
                                   "user:sessions:claude_code",
                                   "user:mcp_servers",
                                   "user:file_upload" },
        AuthorizeUrl: "https://claude.com/cai/oauth/authorize",
        TokenUrl:     "https://platform.claude.com/v1/oauth/token");

    public string BuildUserAgent(string version) =>
        UserAgentTemplate.Replace("{version}", version);
}
```

| Campo | Valor | Fuente (path:línea) |
|---|---|---|
| `ClientId` | `9d1c250a-e61b-44d9-88ed-5944d1962f5e` | `oauthConstants.ts:102` |
| `UserAgentTemplate` | `claude-cli/{version} (external, cli)` | `http.ts:17-28` |
| `Scopes` | `user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload` | `oauthConstants.ts:48-53` |
| `AuthorizeUrl` | `https://claude.com/cai/oauth/authorize` | `oauthConstants.ts:92` |
| `TokenUrl` | `https://platform.claude.com/v1/oauth/token` | `oauthConstants.ts:94` |

`{version}` se reemplaza por la versión actual de OmniCore. El resto es literal.

## 2. Extracción de la lógica de referencia

Esta sección mapea **cada función del source de Claude Code** a su equivalente C# en OmniCore.
El worker usa el código TypeScript como referencia de comportamiento y el C# como contrato.
Las diferencias TS→C# se explicitan en cada subsección.

### 2.1 PKCE y state

**Fuente:** `packages/provider/src/oauth/crypto.ts` (19 líneas, completo)

```typescript
import { createHash, randomBytes } from 'crypto'
function base64URLEncode(buffer: Buffer): string {
  return buffer.toString('base64')
    .replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '')
}
export function generateCodeVerifier(): string {
  return base64URLEncode(randomBytes(32))
}
export function generateCodeChallenge(verifier: string): string {
  const hash = createHash('sha256')
  hash.update(verifier)
  return base64URLEncode(hash.digest())
}
export function generateState(): string {
  return base64URLEncode(randomBytes(32))
}
```

**C# → `src/OmniCore.Models/ClaudeOAuthPkce.cs`:**

```csharp
public static class ClaudeOAuthPkce
{
    public static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    public static string GenerateState()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    internal static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
```

**Diferencias TS→C#:**
- `randomBytes(32)` → `RandomNumberGenerator.Fill(new byte[32])`
- `base64URLEncode(buf)` → `Convert.ToBase64String(buf).Replace('+','-').Replace('/','_').TrimEnd('=')`
- `createHash('sha256').update(str).digest()` → `SHA256.HashData(Encoding.UTF8.GetBytes(str))`
  (el TS hace `hash.update(verifier)` sobre el string UTF-8, no sobre bytes crudos)

### 2.2 URL de autorización

**Fuente:** `packages/provider/src/oauth/client.ts:138-205` (`buildAuthUrl`)

```typescript
export function buildAuthUrl({
  codeChallenge, state, port, isManual,
  loginWithClaudeAi, inferenceOnly, orgUUID, loginHint, loginMethod,
}: {
  codeChallenge: string; state: string; port: number; isManual: boolean;
  loginWithClaudeAi?: boolean; inferenceOnly?: boolean;
  orgUUID?: string; loginHint?: string; loginMethod?: string;
}): string {
  const authUrlBase = loginWithClaudeAi
    ? getOauthConfig().CLAUDE_AI_AUTHORIZE_URL
    : getOauthConfig().CONSOLE_AUTHORIZE_URL
  const authUrl = new URL(authUrlBase)
  authUrl.searchParams.append('code', 'true')
  authUrl.searchParams.append('client_id', getOauthConfig().CLIENT_ID)
  authUrl.searchParams.append('response_type', 'code')
  authUrl.searchParams.append('redirect_uri',
    isManual ? getOauthConfig().MANUAL_REDIRECT_URL
             : `http://localhost:${port}/callback`)
  const scopesToUse = inferenceOnly
    ? [CLAUDE_AI_INFERENCE_SCOPE] : ALL_OAUTH_SCOPES
  authUrl.searchParams.append('scope', scopesToUse.join(' '))
  authUrl.searchParams.append('code_challenge', codeChallenge)
  authUrl.searchParams.append('code_challenge_method', 'S256')
  authUrl.searchParams.append('state', state)
  if (orgUUID)    authUrl.searchParams.append('orgUUID', orgUUID)
  if (loginHint)  authUrl.searchParams.append('login_hint', loginHint)
  if (loginMethod) authUrl.searchParams.append('login_method', loginMethod)
  return authUrl.toString()
}
```

**C# → método estático en `ClaudeOAuthLogin.cs`:**

```csharp
public static string BuildAuthorizeUrl(
    ClaudeOAuthClientIdentity identity,
    string codeChallenge, string state,
    int port, bool isManual = false,
    bool inferenceOnly = false,
    string? orgUuid = null, string? loginHint = null, string? loginMethod = null)
{
    var redirectUri = isManual
        ? "https://platform.claude.com/oauth/code/callback"
        : $"http://localhost:{port}/callback";
    var scope = inferenceOnly
        ? "user:inference"
        : string.Join(' ', identity.Scopes);

    var query = new List<string>
    {
        "code=true",
        $"client_id={Uri.EscapeDataString(identity.ClientId)}",
        "response_type=code",
        $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
        $"scope={Uri.EscapeDataString(scope)}",
        $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
        "code_challenge_method=S256",
        $"state={Uri.EscapeDataString(state)}",
    };
    if (orgUuid != null)   query.Add($"orgUUID={Uri.EscapeDataString(orgUuid)}");
    if (loginHint != null) query.Add($"login_hint={Uri.EscapeDataString(loginHint)}");
    if (loginMethod != null) query.Add($"login_method={Uri.EscapeDataString(loginMethod)}");

    return $"{identity.AuthorizeUrl}?{string.Join('&', query)}";
}
```

**Diferencias TS→C#:**
- `new URL(base)` + `searchParams.append(k,v)` → construir la query string con
  `Uri.EscapeDataString` y `string.Join('&', ...)`.
- `getOauthConfig().CLIENT_ID` → `identity.ClientId`.
- `ALL_OAUTH_SCOPES` → `identity.Scopes` (5 scopes claude.ai).
- `CLAUDE_AI_AUTHORIZE_URL` → `identity.AuthorizeUrl`.
- `MANUAL_REDIRECT_URL` → constante `"https://platform.claude.com/oauth/code/callback"`.

### 2.3 Callback loopback (diseño implementado en F2)

**Fuente:** `packages/provider/src/oauth/auth-code-listener.ts` (170 líneas)

Clase `AuthCodeListener`: `start(port?)` escucha en `localhost` con puerto del SO;
`waitForAuthorization(state, …)` resuelve con el `code`; `handleRedirect` valida el `state`;
`handleSuccessRedirect` manda `302` a `CLAUDEAI_SUCCESS_URL`.

**Decisión de diseño (rev. del plan):** la lógica del listener se separa del acceso al SO.
Un intento previo metía `HttpListener` dentro del listener y resultaba indocumentable: en
Windows `http.sys` exige una ACL de namespace por prefijo, así que `Start()` falla o cuelga, y
los tests con puertos reales no son deterministas (CLAUDE.md: priorizar tests deterministas).

```text
IClaudeOAuthCallbackTransport            ← contrato de transporte (puerto, Accept, Respond)
  ├─ HttpListenerCallbackTransport       ← producción: loopback HTTP (Windows/Linux)
  └─ ClaudeOAuthManualCallbackTransport  ← producción: el usuario pega code#state (fallback)

ClaudeOAuthLoopbackListener              ← máquina de estados PURA sobre el transport
```

El listener contiene toda la lógica portada y ninguna llamada al SO:

| Miembro | Comportamiento | Test |
|---|---|---|
| `HandleAsync(req, expectedState, ct)` | 404 fuera de `/callback`; 400 sin `code`; 400 si el `state` difiere; `302` a la success page si todo cuadra. Devuelve el code o null (ruta ajena). | grupos 3, 10 |
| `WaitForAuthorizationAsync(state, ct)` | Acepta hasta un callback válido; las rutas ajenas se responden y se ignoran (el navegador pide `/favicon.ico`, etc.). Cancelable sin fuga. | grupo 3 |
| `Port` | Delegado al transport (0 en manual). | grupo 3 |
| Comparación de `state` | Ordinal, longitud constante (`FixedTimeEquals`), sin locale. | grupo 3 |
| `OAuthCallbackException` | Lleva `OAuthCallbackFailure` (`NoCode` / `StateMismatch`). **No** lleva el code ni el state: INV-016. | grupo 3 |

Archivos entregados en F2:

| Archivo | Rol |
|---|---|
| `src/OmniCore.Models/ClaudeOAuthCallbackTransport.cs` | `IClaudeOAuthCallbackTransport`, `ClaudeOAuthCallbackRequest`, `ClaudeOAuthCallbackResponse`. |
| `src/OmniCore.Models/ClaudeOAuthLoopbackListener.cs` | Máquina de estados + `OAuthCallbackFailure` + `OAuthCallbackException`. |
| `src/OmniCore.Models/HttpListenerCallbackTransport.cs` | Transporte real. Reserva el puerto con `TcpListener` antes de abrir `HttpListener`, para conocerlo antes de construir la URL de authorize sin carrera. Fallo tipado (`OAuthTransportException`) en vez de colgarse. |
| `src/OmniCore.Models/ClaudeOAuthManualCallbackTransport.cs` | Modo manual: parte `CODE#STATE`, trim, sin stdin en los tests (lector inyectado). |
| `tests/OmniCore.Tests/FakeClaudeOAuthCallbackTransport.cs` | Fake en memoria: cero sockets, cero puertos. |

Diferencias TS→C#:
- Node `createServer().listen(0,'localhost')` → reserva de puerto con `TcpListener` + `HttpListener`.
- `Promise`/callbacks → `TaskCompletionSource` implícito en `AcceptAsync`/`WaitForAuthorizationAsync`.
- `searchParams.get` → `HttpUtility.ParseQueryString` dentro del transport (el listener ve un diccionario).
- Success page fija: `https://platform.claude.com/oauth/code/success?app=claude-code`.


### 2.4 Exchange (código → tokens)

**Fuente:** `packages/provider/src/oauth/client.ts:211-260` (`exchangeCodeForTokens`)

```typescript
export async function exchangeCodeForTokens(
  authorizationCode: string, state: string, codeVerifier: string,
  port: number, useManualRedirect: boolean = false, expiresIn?: number,
): Promise<OAuthTokenExchangeResponse> {
  const requestBody: Record<string, string | number> = {
    grant_type: 'authorization_code',
    code: authorizationCode,
    redirect_uri: useManualRedirect
      ? getOauthConfig().MANUAL_REDIRECT_URL
      : `http://localhost:${port}/callback`,
    client_id: getOauthConfig().CLIENT_ID,
    code_verifier: codeVerifier,
    state,
  }
  if (expiresIn !== undefined) requestBody.expires_in = expiresIn

  const response = await axios.post(getOauthConfig().TOKEN_URL, requestBody, {
    headers: { 'Content-Type': 'application/json' },
    timeout: 30000,
  })

  if (response.status !== 200) {
    const reason = response.status === 401
      ? 'oauth_exchange_invalid_code'
      : 'oauth_exchange_http_error'
    throw new Error(response.status === 401
      ? 'Authentication failed: Invalid authorization code'
      : `Token exchange failed (${response.status}): ${response.statusText}`)
  }
  return response.data
}
```

**C# → método en `src/OmniCore.Models/ClaudeOAuthTokenClient.cs`:**

```csharp
public async Task<ClaudeOAuthTokenResponse> ExchangeCodeAsync(
    string code, string state, string codeVerifier,
    int port, bool isManual = false, int? expiresIn = null,
    CancellationToken ct = default)
{
    var body = new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = isManual
            ? "https://platform.claude.com/oauth/code/callback"
            : $"http://localhost:{port}/callback",
        ["client_id"] = _identity.ClientId,
        ["code_verifier"] = codeVerifier,
        ["state"] = state,
    };
    if (expiresIn.HasValue) body["expires_in"] = expiresIn.Value.ToString();

    using var content = new StringContent(
        JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    using var resp = await _httpClient.PostAsync(_identity.TokenUrl, content, ct)
        .ConfigureAwait(false);
    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

    if (resp.StatusCode == HttpStatusCode.Unauthorized)
        throw new OAuthExchangeInvalidCodeException(json);
    if (!resp.IsSuccessStatusCode)
        throw new OAuthExchangeHttpException((int)resp.StatusCode, json);

    return JsonSerializer.Deserialize<ClaudeOAuthTokenResponse>(json, _jsonOpts)!;
}
```

**Diferencias TS→C#:**
- `axios.post(url, body, {timeout: 30000})` → `HttpClient.PostAsync` con `Timeout = TimeSpan.FromSeconds(30)`.
- Error 401 → `HttpStatusCode.Unauthorized`.
- Respuesta deserializada con `System.Text.Json`.

### 2.5 Refresh

**Fuente:** `packages/provider/src/oauth/client.ts:266-354` (`refreshOAuthToken`)

```typescript
export async function refreshOAuthToken(
  refreshToken: string,
  { scopes: requestedScopes, expiresIn, clientId }:
    { scopes?: string[]; expiresIn?: number; clientId?: string } = {},
): Promise<OAuthTokens> {
  const requestBody: Record<string, unknown> = {
    grant_type: 'refresh_token',
    refresh_token: refreshToken,
    client_id: clientId ?? getOauthConfig().CLIENT_ID,
    scope: (requestedScopes?.length ? requestedScopes : CLAUDE_AI_OAUTH_SCOPES).join(' '),
  }
  if (expiresIn !== undefined) requestBody.expires_in = expiresIn

  const response = await axios.post(getOauthConfig().TOKEN_URL, requestBody, {
    headers: { 'Content-Type': 'application/json' },
    timeout: 30000,
  })

  if (response.status !== 200) {
    throw new Error(`Token refresh failed: ${response.statusText}`)
  }

  const data = response.data as OAuthTokenExchangeResponse
  const { access_token: accessToken, refresh_token: newRefreshToken = refreshToken,
          expires_in: expiresInVal } = data
  const expiresAt = Date.now() + expiresInVal * 1000
  const scopes = parseScopes(data.scope)

  // Fetch profile info (omitted for brevity — see §2.9)
  // ...

  return {
    accessToken, refreshToken: newRefreshToken, expiresAt, scopes,
    clientId, subscriptionType: ..., rateLimitTier: ...,
  }
}
```

**C# → método en `ClaudeOAuthTokenClient.cs`:**

```csharp
public async Task<ClaudeOAuthTokenResponse> RefreshTokenAsync(
    string refreshToken, string? clientId = null, int? expiresIn = null,
    CancellationToken ct = default)
{
    var body = new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refreshToken,
        ["client_id"] = clientId ?? _identity.ClientId,
        ["scope"] = string.Join(' ', _identity.Scopes),
    };
    if (expiresIn.HasValue) body["expires_in"] = expiresIn.Value.ToString();

    using var content = new StringContent(
        JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    using var resp = await _httpClient.PostAsync(_identity.TokenUrl, content, ct)
        .ConfigureAwait(false);
    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

    if (!resp.IsSuccessStatusCode)
    {
        if (json.Contains("invalid_grant"))
            throw new OAuthInvalidGrantException(json);
        throw new OAuthRefreshHttpException((int)resp.StatusCode, json);
    }

    var tokenResp = JsonSerializer.Deserialize<ClaudeOAuthTokenResponse>(json, _jsonOpts)!;
    // Fetch profile and merge (ver §2.9)
    var profile = await FetchProfileAsync(tokenResp.AccessToken, ct).ConfigureAwait(false);
    tokenResp.SubscriptionType = profile?.SubscriptionType;
    tokenResp.RateLimitTier = profile?.RateLimitTier;
    return tokenResp;
}
```

**Diferencias TS→C#:**
- `clientId ?? getOauthConfig().CLIENT_ID` → `clientId ?? _identity.ClientId` (client id pegajoso).
- `CLAUDE_AI_OAUTH_SCOPES` → `_identity.Scopes`.
- `isInvalidGrantError(error)` → detectar `"invalid_grant"` en el JSON de error.
- Perfil fetch separado (§2.9).

### 2.6 Dead-set de refresh

**Fuente:** `packages/provider/src/oauth/refreshTokenDeadSet.ts` (90 líneas)

```typescript
const deadTokens = new Set<string>()
export function markRefreshTokenDead(token: string): void {
  if (!token) return
  deadTokens.add(token)
}
export function isRefreshTokenDead(token: string): boolean {
  if (!token) return false
  return deadTokens.has(token)
}
export function clearRefreshTokenDeadSet(): void {
  deadTokens.clear()
}
```

**C# → `src/OmniCore.Models/ClaudeOAuthRefreshCoordinator.cs` (parte del coordinador):**

```csharp
public sealed class ClaudeOAuthRefreshCoordinator
{
    private readonly HashSet<string> _deadTokens = new();
    private readonly object _lock = new();

    public void MarkDead(string refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken)) return;
        lock (_lock) _deadTokens.Add(refreshToken);
    }

    public bool IsDead(string refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken)) return false;
        lock (_lock) return _deadTokens.Contains(refreshToken);
    }

    public void Clear()
    {
        lock (_lock) _deadTokens.Clear();
    }

    // Lockfile + mtime coordination (§5 del plan original)
    // ...
}
```

**Diferencias TS→C#:**
- `Set<string>` → `HashSet<string>` con `lock` para thread-safety.
- Limpieza por mtime: ver §5 del plan original (lockfile + relectura de credenciales).

### 2.7 Cabeceras de inferencia

**Fuente:** `packages/provider/src/http.ts:56-71` (`getAuthHeaders`)

```typescript
export function getAuthHeaders(): AuthHeaders {
  if (isClaudeAISubscriber()) {
    const oauthTokens = getClaudeAIOAuthTokens()
    if (!oauthTokens?.accessToken) {
      return { headers: {}, error: 'No OAuth token available' }
    }
    return {
      headers: {
        Authorization: `Bearer ${oauthTokens.accessToken}`,
        'anthropic-beta': OAUTH_BETA_HEADER,
      },
    }
  }
  const apiKey = getAnthropicApiKey()
  if (!apiKey) return { headers: {}, error: 'No API key available' }
  return { headers: { 'x-api-key': apiKey } }
}
```

Y `getUserAgent()` en `http.ts:17-28`:

```typescript
export function getUserAgent(): string {
  return `claude-cli/${MACRO.VERSION} (${process.env.USER_TYPE}, ${readEnv('CLAUDE_CODE_ENTRYPOINT') ?? 'cli'})`
}
```

**C# → integrado en `AnthropicMessagesProvider` (ya existente, ADR-0011 §10):**

El provider ya ramifica por `Auth.Kind`. Para `AuthKind.OAuth`:
- `Authorization: Bearer <access_token>`
- `anthropic-beta: oauth-2025-04-20`
- `User-Agent: claude-cli/<versión> (external, cli)` (con `{version}` = versión de OmniCore)
- `x-anthropic-additional-protection: true` (de `anthropic/client.ts:126`)

**Diferencias TS→C#:**
- `isClaudeAISubscriber()` → presencia de `ClaudeOAuthCredential` con scope `user:inference`.
- `getUserAgent()` → `identity.BuildUserAgent(AssemblyVersion)`.

### 2.8 Perfil y roles

**Fuente:** `packages/provider/src/oauth/getOauthProfile.ts` y `client.ts` (`fetchProfileInfo`)

```typescript
// getOauthProfile.ts
export async function getOauthProfileFromOauthToken(accessToken: string): Promise<OAuthProfileResponse> {
  const endpoint = `${getOauthConfig().BASE_API_URL}/api/oauth/profile`
  const response = await axios.get(endpoint, {
    headers: { Authorization: `Bearer ${accessToken}`, 'Content-Type': 'application/json' },
    timeout: 10000,
  })
  return response.data
}
```

**C# → método en `ClaudeOAuthTokenClient.cs`:**

```csharp
public async Task<ClaudeOAuthProfile?> FetchProfileAsync(string accessToken, CancellationToken ct = default)
{
    var url = "https://api.anthropic.com/api/oauth/profile";
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
    if (!resp.IsSuccessStatusCode) return null;
    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    return JsonSerializer.Deserialize<ClaudeOAuthProfile>(json, _jsonOpts);
}
```

**Diferencias TS→C#:**
- `axios.get` → `HttpClient.SendAsync`.
- Timeout 10s → configurado en `HttpClient.Timeout`.
- Perfil opcional: si falla, el refresh sigue funcionando con datos cached.

## 3. Arquitectura dentro de OmniCore

Nuevo proyecto/lógica en `OmniCore.Models` (el Engine no conoce providers concretos, INV-008) y
cableado en `OmniCore.Host`. Reutiliza lo existente:

- `AuthKind.OAuth` ya está previsto por ADR-0011 §3.2.
- `ICredentialStore` cifrado (DPAPI Windows / AES-GCM Linux, ADR-0018) guarda el credential.
- `SecretRedactorRegistry` (ADR-0018) registra access/refresh antes de cualquier uso.
- `AnthropicMessagesProvider` (ADR-0005/ADR-0011 §10) ya cubre wire protocol, streaming,
  thinking firmado, caching y reintentos: este plan **solo** añade el camino de credencial OAuth
  y la máquina de login.

### 3.1 Piezas nuevas

| Archivo | Responsabilidad |
|---|---|
| `src/OmniCore.Models/ClaudeOAuthClientIdentity.cs` | Identidad fija de Claude Code (§1). |
| `src/OmniCore.Models/ClaudeOAuthCredential.cs` | `AccessToken`, `RefreshToken`, `ExpiresAt`, `Scopes`, `SubscriptionType?`, `RateLimitTier?`, `ClientId` (pegajoso), `AccountUuid?`. |
| `src/OmniCore.Models/ClaudeOAuthLogin.cs` | Máquina de estados del login (§4). |
| `src/OmniCore.Models/ClaudeOAuthPkce.cs` | `verifier`/`challenge`/`state` (lógica pura, testeable). |
| `src/OmniCore.Models/ClaudeOAuthCallbackTransport.cs` | `IClaudeOAuthCallbackTransport` + request/response del callback (§2.3). |
| `src/OmniCore.Models/ClaudeOAuthLoopbackListener.cs` | Máquina de estados del callback, sin SO (§2.3). |
| `src/OmniCore.Models/HttpListenerCallbackTransport.cs` | Transporte loopback real (§2.3). |
| `src/OmniCore.Models/ClaudeOAuthManualCallbackTransport.cs` | Transporte del modo manual (§3.3). |
| `src/OmniCore.Models/ClaudeOAuthTokenClient.cs` | exchange + refresh + perfil + roles (§2.4-2.8). |
| `src/OmniCore.Models/ClaudeOAuthRefreshCoordinator.cs` | dead-set + lockfile + re-lectura por mtime (§2.6, §5). |
| `src/OmniCore.Models/ClaudeOAuthAuthProvider.cs` | `IAuthProvider` que entrega Bearer vigente (refresca si hace falta). |
| `src/OmniCore.Host/OmniHost.cs` | Factory aditiva `CreateClaudeOAuthLogin()`. |
| `src/OmniCore.Client/Localization.cs` | Claves `claude.oauth.*` es/en. |

### 3.2 Flujo de estados

```
Idle → PreparingIdentity → Listening(port) → AwaitingBrowser → ReceivedCode
     → ValidatingState → Exchanging → FetchingProfile → Persisting → Succeeded
                                                              ↘ Failed(reason)
```

Con `Failed` tipado (no `Exception` genérica, spec §71): `Cancelled`, `TimedOut`,
`StateMismatch`, `ExchangeInvalidCode`, `ExchangeHttpError`, `NetworkError`,
`CredentialStoreError`, `InvalidGrant` (refresh), `RuntimeNotInstalled` (solo delegación).

### 3.3 Modos de entrada del código

1. **Loopback (primario):** puerto SO-asignado, se abre el navegador del sistema con la URL de
   authorize; el listener recibe el redirect.
2. **Manual (fallback):** `MANUAL_REDIRECT_URL`; el usuario pega el `code#state` que muestra la
   página y se parsea por `#` (patrón `AUTHORIZATION_CODE#STATE` de `auth-code-listener.ts`).

La elección no es del modelo: es política de cliente/CLI (INV-019). Manual se usa cuando el
puerto no es alcanzable o no hay navegador.

## 4. Contratos (firmas congeladas para el worker)

```csharp
public enum ClaudeOAuthLoginState { Idle, PreparingIdentity, Listening, AwaitingBrowser,
    ReceivedCode, Exchanging, FetchingProfile, Persisting, Succeeded, Failed }

public sealed record ClaudeOAuthLoginProgress(ClaudeOAuthLoginState State, string? AuthorizeUrl, int? Port);
public sealed record ClaudeOAuthLoginResult(bool Success, ClaudeOAuthLoginFailure? Failure, string? Detail);

public interface IClaudeOAuthLogin
{
    Task<ClaudeOAuthLoginResult> LoginAsync(ClaudeOAuthLoginOptions options, CancellationToken ct);
    Task<ClaudeOAuthLoginResult> LoginManualAsync(string codeAndState, ClaudeOAuthLoginOptions options, CancellationToken ct);
}

public sealed record ClaudeOAuthLoginOptions(
    int? FixedPort = null,
    bool OpenBrowser = true,
    TimeSpan? Timeout = null);
```

`LoginAsync` nunca devuelve tokens: escribe en `ICredentialStore` y devuelve un resultado sin
secretos. Los tokens no cruzan el borde del servicio (INV-016: los secretos nunca llegan al
journal, artifacts, contexto ni logs).

## 5. Máquina de refresh (multi-proceso)

1. Antes de usar el token: si `ExpiresAt - 5 min <= now`, refrescar.
2. Comprobar dead-set; si el refresh token está muerto → `InvalidGrant` (pedir login).
3. Adquirir lockfile (mutex en proceso + file lock en `<data>/workspaces/<WorkspaceId>/`).
4. Re-leer `credentials.json`; si cambió el `mtime` respecto a la copia en memoria, recargar y
   limpiar el dead-set (una escritura externa probablemente trajo otro token).
5. Comprobar dead-set otra vez con el token recién leído (carrera entre procesos).
6. Refrescar; `invalid_grant` → marcar muerto y fallar; éxito → persistir y liberar.

Esto es lo que permite varios procesos OmniCore (o el CLI oficial) convivir sin pisarse los
tokens.

## 6. Persistencia y seguridad

- `ClaudeOAuthCredential` cifrado en `ICredentialStore` (ADR-0018), nunca en texto plano ni en
  YAML. Metadata sin secretos aparte (`(data)/provider-connections.json`): máscara, timestamps,
  plan/tier.
- `SecretRedactorRegistry` registra access y refresh en cuanto se obtienen.
- Sin logs del `code`, `state`, tokens ni cabeceras. Los errores HTTP se reportan por código,
  nunca con el cuerpo crudo si puede contener el token.
- Endpoint de token fijo en la identidad (`TokenUrl`); redirecciones desactivadas en el POST
  (no reenviar el `code`/`refresh_token` a otro host).
- TLS estándar del sistema (ADR-0038 §2).

## 7. Integración con el provider y el resto del runtime

- `AnthropicMessagesProvider` añade el camino `AuthKind.OAuth`: `Authorization: Bearer` + beta
  `oauth-2025-04-20`, `User-Agent: claude-cli/<versión> (external, cli)`,
  `x-anthropic-additional-protection: true`, en vez de `x-api-key`. Cubierto por la bifurcación
  existente `if (_descriptor.Auth.Kind == ...)` (§3.1).
- `withOAuth401Retry` equivalente en C#: ante 401, un refresh y un reintento; si vuelve a fallar
  → `AuthenticationFailed` (error tipado de §5 de ADR-0011).
- `isClaudeAISubscriber` equivalente: presencia de `ClaudeOAuthCredential` con scope
  `user:inference`. Determina la cabecera, nunca el nombre del modelo.
- `MeteredCurrency` (ADR-0046): el flujo por suscripción se marca `Subscription`/incluido, no
  gasto medido; el routing automático no lo trata como coste por token. La escalación sigue
  ligada a la `SessionRoutingPolicy` y exige consentimiento para lo que corresponda.
- Estado en `/doctor` y en la status line: plan, tier y expiración del token (sin valores de
  secreto). Sin cuota informada → `—` (ADR-0031).

## 8. Errores tipados (spec §71)

`OAuthStateMismatch`, `OAuthExchangeInvalidCode`, `OAuthExchangeHttpError`, `OAuthNetworkError`,
`OAuthInvalidGrant`, `OAuthCredentialStoreError`, `OAuthLoginCancelled`, `OAuthLoginTimedOut`,
`AuthenticationFailed` (reutilizado del runtime), `RuntimeNotInstalled` (delegación).

## 9. Plan de tests (deterministas, sin red ni login real)

Se usa `IHttpMessageHandler` falso (patrón `AnthropicMessagesProviderTests`/`QueueHandler`) y
`ICredentialStore` en memoria. **Ningún test autentica, abre navegador, ni llama a Anthropic.**

1. **PKCE (§2.1):** longitud y alfabeto base64url de verifier/state; challenge == SHA-256(verifier)
   con vector fijo; verifier distinto por intento.
2. **Authorize URL (§2.2):** presencia y orden de params; `code=true`; `S256`; `scope` = union;
   solo-inferencia = `user:inference`; modos loopback vs manual; `login_hint`/`login_method`/`orgUUID`.
3. **Listener (§2.3):** puerto 0 asigna puerto; captura `code`/`state`; `state` distinto → rechazo;
   callback en ruta distinta → 404; timeout → `OAuthLoginTimedOut`; cancelación → `Cancelled` sin
   fuga de socket.
4. **Exchange (§2.4):** cuerpo exacto (`grant_type`, `code`, `redirect_uri`, `client_id`,
   `code_verifier`, `state`); timeout 30 s; 401 → `OAuthExchangeInvalidCode`; 5xx → `...HttpError`;
   respuesta con `access_token`/`refresh_token`/`expires_in`/`scope` parseada sin inventar campos.
5. **Refresh (§2.5):** cuerpo exacto; client id pegajoso (token emitido con client A se refresca
   con A); expansión de scopes; `invalid_grant` → dead-set marcado + `OAuthInvalidGrant`.
6. **Dead-set (§2.6):** marcar/consultar; limpiar al avanzar mtime; limpiar en logout.
7. **Coordinación (§5):** dos refreshes concurrentes → un solo POST; mtime externo → recarga y
   dead-set limpio; token muerto leído por otro proceso durante la espera del lock → no refresca.
8. **Cabeceras de inferencia (§2.7):** suscriptor → `Authorization: Bearer` + beta +
   additional-protection; User-Agent = `claude-cli/<versión> (external, cli)`; 401 → refresh +
   1 reintento; 401 tras refresh → `AuthenticationFailed`.
9. **Persistencia (§6):** credential cifrado y recuperable; redacción (scan de todos los archivos
   temporales sin access/refresh en claro); metadata con máscara; cancelación a mitad no deja
   credencial parcial.
10. **Modo manual (§3.3):** `code#state` parseado; state inválido rechazado.
11. **Identidad (§1):** `ClaudeOAuthClientIdentity` es un record sin valores por defecto: guarda
    los campos que le pasa el usuario, `BuildUserAgent` sustituye `{version}` y el record es
    inmutable (`with`). Los valores de Claude Code no viven en el código sino en la configuración
    del usuario (ADR-0039 §2), así que no hay test de constantes que mantener.
12. **Localización:** claves `claude.oauth.*` presentes en es y en en (test de paridad).

## 10. Criterios de aceptación

- `dotnet build OmniCore.slnx` exit 0, 0 warnings.
- Suite completa verde salvo skip de symlink ya documentado.
- Tests de arquitectura (`tests/OmniCore.ArchitectureTests`) 100%: `OmniCore.Models` no arrastra
  Terminal.Gui/Spectre, dependencias de ADR-0009 intactas.
- Ninguna API exclusiva de net10 en `Protocol`/`Client`/`Sandbox` (ADR-0038 §6).
- Scan de secretos en tests: ningún access/refresh/code/state en artefactos, logs, journal ni
  eventos.
- `LocalizedText` en es/en para todas las cadenas visibles (ADR-0040).
- Test que demuestra que la identidad de cliente es la de Claude Code (test de valores fijos, §9.11).
- La delegación a CLI (`ClaudeAccountDelegationService`) **no** se presenta como este flujo: son
  caminos distintos y el consumidor real de cada uno se documenta (hoy la delegación solo la
  consume su factory y sus tests; el chat no la usa).

## 11. Riesgos y límites

1. **Cambios del backend.** El contrato del `result`/perfil puede cambiar; tests de contrato con
   respuestas grabadas lo detectan (patrón ya usado en el repo).
2. **`--permission-prompts`/versiones** no aplican aquí: son de la delegación, no de este flujo.
3. **Multi-proceso:** el lockfile y el dead-set son la mitigación; sin ellos, dos procesos pueden
   consumir el mismo refresh token (`access_denied`/`invalid_grant` transitorio).
4. **Linux/macOS:** el listener loopback y el navegador del sistema deben probarse en Linux
   (diferido, como el resto de la validación multiplataforma).

## 12. Fases de construcción

| Fase | Entregable | Tests |
|---|---|---|
| F1 ✅ hecha | `ClaudeOAuthClientIdentity` (sin defaults), `ClaudeOAuthPkce`, `ClaudeOAuthAuthorizeUrlBuilder` | 1, 2 |
| F2 ✅ hecha | `IClaudeOAuthCallbackTransport` + listener puro + transport HTTP + transport manual | 3, 10 |
| F3 ✅ hecha | `ClaudeOAuthTokenClient` (exchange, refresh, perfil) + errores tipados + DTO AOT | 4, 5 |
| F4 ✅ hecha | `ClaudeOAuthCredential`, `ClaudeOAuthCredentialStore` (clave separada + metadata atómica + mtime) | 9 |
| F5 | `ClaudeOAuthRefreshCoordinator` (dead-set, lock, mtime) | 6, 7 |
| F6 | `AnthropicMessagesProvider` camino OAuth + 401-retry | 8 |
| F7 | `ClaudeOAuthAuthProvider` + factory Host + `/doctor` + status line | contratos, 12 |
| F8 | Localización es/en y pulido; suite completa y arquitectura | aceptación §10 |

Cada fase: build + tests focales al cerrar; sin commits hasta la aceptación de la raíz.

## 13. Trazabilidad de fuentes

| Hecho | Fuente |
|---|---|
| Endpoints, client id, scopes, TTL, beta | `packages/provider/src/oauthConstants.ts:33-103` |
| PKCE/state | `packages/provider/src/oauth/crypto.ts:5-19` |
| Authorize URL y params | `packages/provider/src/oauth/client.ts:138-205` |
| Callback loopback | `packages/provider/src/oauth/auth-code-listener.ts:16-170` |
| Exchange | `packages/provider/src/oauth/client.ts:211-260` |
| Refresh + client id pegajoso | `packages/provider/src/oauth/client.ts:266-354` |
| Dead-set | `packages/provider/src/oauth/refreshTokenDeadSet.ts` |
| Cabeceras de inferencia | `packages/provider/src/http.ts:17-28,56-71`; `anthropic/client.ts:126` |
| Perfil/roles | `packages/provider/src/oauth/getOauthProfile.ts`; `oauth/client.ts` (fetchRoles) |
| Persistencia/lockfile | `packages/provider/src/authAlias.ts`; `packages/cli/src/handlers/auth.ts:95,196,249-269` |
