# Validación — bloque 2: backend de cuenta Claude (lifecycle/status, vía CLI oficial) (2026-10-08)
#
# REEVALUACIÓN OAuth (v2, 2026-10-08): ver §"Reevaluación OAuth (v2)" al final de este archivo.
#

## Alcance

Solo el backend de cuenta/suscripción para Claude, por la única vía que Anthropic permite
(ADR-0011 §3.3, ADR-0012): lanzar el binario **oficial sin modificar** de Claude Code para que
el usuario complete login/logout en el flujo de Anthropic, con estado real del runtime y
elegibilidad honesta Free/Pro. Sin OAuth propio, sin client IDs de terceros, sin TUI ni menú,
sin invocación de inferencia, sin credenciales leídas o guardadas por OmniCore y **sin
fallback a la vía de API pagada** (el bloque 1, `ProviderConnectionService`, queda intacto y
separado). Login es una acción futura explícita del usuario: no se ejecutó ninguna
autenticación real.

## Determinación de la ruta soportada (antes de implementar)

**Referencia source real (rev. del bloque OAuth-reference, 2026-10-08):** el humad reemplazó la
referencia docs-only por el source no oficial **Icarus603/claude-code @ `7c918f786828f7071d023e1c401bc6be754446e3`**
(«fix(release): generate attestable CycloneDX SBOM», 2026-08-11 +0800; shallow-clone de solo
lectura, sin ejecutar ni instalar nada). El repo es un derivado reconstruido del **sourcemap npm
v2.1.88** (2026-03-31), marcado `UNLICENSED` y sin LICENSE: según su propio ATTRIBUTION.md, la
visibilidad pública no es un grant de reuso. Consecuencia aplicada: **cero código ni texto
propietario transplantado**; el source se usó solo como evidencia del comportamiento del
protocolo, y la implementación de OmniCore sigue siendo independiente. Rutas de autenticación
inspeccionadas (relativas al repo): `packages/provider/src/oauthConstants.ts` (endpoints, client
id, scopes, beta `oauth-2025-04-20`, TTL 1 año), `packages/provider/src/oauth/{crypto.ts,
index.ts, auth-code-listener.ts, client.ts, getOauthProfile.ts, refreshTokenDeadSet.ts,
types.ts}` (PKCE S256, callback loopback, código manual `AUTHORIZATION_CODE#STATE`, exchange,
refresh, dead-set), `packages/provider/src/authAlias.ts` (persistencia protegida, lockfile,
coordinación entre procesos), `packages/cli/src/handlers/auth.ts` (installOAuthTokens, auth
login/status/logout), `packages/repl/src/components/ConsoleOAuthFlow.tsx` (UI de login y
setup-token) y `packages/provider/src/{auth.ts, http.ts, anthropic/client.ts}` (adjunto del
token en inferencia). El detalle completo y la matriz de comportamiento están en el handoff
`C:/Users/juanc/.codex/pi-provider-menu-glm-split-20261008/oauth-contract.md`. La conclusión de
ruta soportada no cambia: Anthropic (página legal oficial consultada hoy) prohíbe a terceros
ofrecer login de Claude.ai en sus aplicaciones y enrutar requests con credenciales de planes
Free/Pro/Max, así que OmniCore no implementa el flujo OAuth y su integración de suscripción
sigue siendo la sesión interactiva del binario oficial sin modificar (ADR-0012).

**Fuentes primarias oficiales actuales** (públicas, sin autenticación, consultadas 2026-10-08):

- [code.claude.com/docs/en/authentication](https://code.claude.com/docs/en/authentication):
  - Tipos de cuenta con login: **Claude Pro o Max (suscripción), Claude for Teams/Enterprise,
    Claude Console, providers de nube (Bedrock/Vertex/Foundry) y Claude apps gateway**.
    **Free no está entre los tipos de cuenta** → no se le promete login ni uso de Claude Code.
  - Login/logout/status son flujos de la sesión interactiva: `claude` (primer arranque),
    `/login`, `/logout` («removes and revokes the credential»), `/status` (fila `Login`, v2.1.210+).
  - `claude setup-token`: token OAuth de larga vida para CI/scripts; «requires a Pro, Max,
    Team, or Enterprise plan»; imprime el token en la terminal del usuario y no lo guarda.
  - Credenciales de Claude Code viven en `.claude(.credentials.json)`/Keychain gestionadas por
    `/login` y `/logout` — OmniCore no las lee, copia ni almacena (excluido por la tarea y por
    higiene); también `CLAUDE_CONFIG_DIR` para múltiples cuentas.
- ADR-0011 §3.3 (rev. 3): prohibición de reutilizar OAuth de suscripción Anthropic en terceros
  (incluido Agent SDK), vigente y verificada; OmniCore se usa con Anthropic solo por API key.
- ADR-0012: la única vía legítima con la suscripción del usuario es el binario oficial sin
  modificar; se ejecuta tal como lo publica Anthropic, sin suplantar identidad.

## Archivos entregados

| Archivo | Cambio |
|---|---|
| `src/OmniCore.Host/ClaudeAccount.cs` | Nuevo: `ClaudePlan`, `ClaudeAccountEntitlement`, `ClaudePlanEligibility`, `ClaudeAccountEligibility`, `ClaudeAccountSessionMode`, `ClaudeAccountStatus`, `ClaudeSessionOutcome`, `ClaudeAccountException`, `ClaudeCodeRuntimeLocator`, `ClaudeAccountService`. Rev. OAuth-reference: `PrimarySource` cita ambas páginas oficiales y se añaden las constantes de evidencia `SetupTokenPlanRequirement` y `ThirdPartyProhibition` (datos, sin cambios de firmas). |
| `src/OmniCore.Host/OmniHost.cs` | Factory aditiva `CreateClaudeAccountService()` (SystemProcessRuntime real). Único punto de cableado de runtime; el menú (trabajador paralelo) la consume vía el contrato. |
| `tests/OmniCore.Tests/ClaudeAccountTests.cs` | 33 tests enfocados (ver abajo). |
| `tests/OmniCore.Tests/ClaudeAccountOAuthReferenceTests.cs` | Nuevo (rev. OAuth-reference): 5 tests de contrato que fijan la consecuencia de la investigación (sin identidad/client id de Claude Code en el Host, cita de la prohibición vigente, cableado de la factory, registros sin datos de sesión inventados). |
| Handoff | `C:/Users/juanc/.codex/pi-provider-menu-glm-split-20261008/account-contract.md` (CONTRACT FROZEN): firmas exactas, semántica de acciones, cancelación, elegibilidad y claves de recursos es/en para el menú. |

No se tocó `Client/Localization.cs`, Protocol, `Models/Registry.cs`, `Cli/Tui/TuiApp.cs`,
`Cli/CliApp.cs`, `TuiAccountHost.cs`, `ProviderConnections.cs` ni ningún otro archivo ajeno.

## Operaciones soportadas (`ClaudeAccountService`)

- **GetStatusAsync** — localiza `claude` (PATH: `claude`/`claude.exe`/`claude.cmd`; reserva del
  instalador nativo `<profile>/.local/bin`) y mide `claude --version` (capturado, 30 s).
  Sin binario → `RuntimePresent=false` sin excepción ni estados fingidos. El probe solo
  extrae la primera secuencia `x.y.z`; el resto de la salida del hijo nunca se copia.
  **Nunca informa login, plan, cuota ni ventanas de modelo**: no hay comando no interactivo
  documentado para estado de sesión y no se inventa un «Connected» por tener el CLI.
- **RunInteractiveAsync(mode)** — lanza la sesión interactiva real (stdio sin capturar, sin
  tocar stdin): `Login`/`Logout`/`OpenSession` → `claude`; `SetupToken` → `claude setup-token`.
  El resultado (`ClaudeSessionOutcome`) solo lleva exit code/timed out/cancelled: ningún token
  puede llegar a OmniCore por este canal. Timeout por defecto 60 min; timeout o cancelación
  cortan el árbol de procesos (`CancelTree`) y se reportan como `TimedOut`/`Cancelled`.
  Sin binario → error tipado `runtimeNotInstalled`; spawn fallido → `runtimeLaunchFailed`.
- **Elegibilidad** — `ClaudeAccountEligibility.ForPlan`: Free → `NotEligible` (fuente exacta);
  Pro/Max/Team/Enterprise → `Eligible`; desconocido → `RequiresMeasurement` (el plan real del
  usuario solo lo muestra su propia sesión `/status`; este backend no lo infiere).
- Windows shim npm: un `claude.cmd` localizado se lanza vía `cmd.exe /d /s /c <ruta>` con argv
  tipado, mismo entorno restringido y misma política de stdio.

## Seguridad y prohibiciones respetadas

- Cero llamadas HTTP en el backend de cuenta; sin OAuth, sin client IDs inventados, sin
  suplantación de identidad de Claude Code, sin tokens de suscripción contra clients ajenos.
- El entorno del hijo parte de la allowlist de plataforma (ADR-0037/ADR-0038 §2) con delta
  vacío: `CLAUDE_CODE_OAUTH_TOKEN`/`ANTHROPIC_API_KEY` nunca se heredan de OmniCore (y el
  usuario no los pasa a través de este backend; el token de `setup-token` lo gestiona el
  usuario fuera de OmniCore).
- Sin lectura de `.claude*`, `~/.pi`, sesiones, perfiles de navegador ni valores de secretos;
  sin consultas de red autenticadas y sin mutaciones de cuenta en tests (solo proceso local
  falso o, en el smoke manual, `claude --version` que no autentica nada).
- Sin fallback a API pagada: el backend no toca la credencial `anthropic` ni el bloque 1.

## Matriz de capacidades Free/Pro (UI ≠ elegibilidad ≠ integración)

| Capa | Free | Pro | Max | Team | Enterprise |
|---|---|---|---|---|---|
| Fila de cuenta mostrada en el menú (decisión del trabajador del menú) | sí | sí | sí | sí | sí |
| Elegibilidad de suscripción según docs oficiales | **No** | sí | sí | sí | sí |
| Integración autenticada usable (sesión interactiva oficial, ADR-0012) | no | sí | sí | sí | sí |
| Facturación por API key (bloque 1 aceptado, `MeteredCurrency`) | sí (Console) | sí | sí | sí | sí |

## Comandos y resultados (Windows, SDK .NET 10.0.401)

Builds/tests con `ArtifactsPath` aislado
`C:/Users/juanc/.codex/pi-provider-menu-glm-split-20261008/account-artifacts` (el grafo
compartido podía estar en edición simultánea):

- `dotnet build src/OmniCore.Host/OmniCore.Host.csproj -p:ArtifactsPath=…account-artifacts`
  → exit 0, 0 warnings/0 errors.
- `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -p:ArtifactsPath=…account-artifacts`
  → exit 0, 0 warnings/0 errors.
- `dotnet run -c Debug -p:ArtifactsPath=…account-artifacts --project tests/OmniCore.Tests
  -- -class OmniCore.Tests.ClaudeAccountTests` → exit 0; **33/33 correctos, 0 fallos, 0 skip**.
- Rev. OAuth-reference (artefactos aislados `…oauth-reference-artifacts/build`):
  `-class OmniCore.Tests.ClaudeAccountTests` → exit 0, 33/33; `-class
  OmniCore.Tests.ClaudeAccountOAuthReferenceTests` → exit 0, 5/5 (el scan de código fuente
  pasa en build con artefactos por defecto y hace skip con el ArtifactsPath aislado porque el
  binario queda fuera del árbol del repo; ambiental, ya documentado); regresión del bloque 1
  `-class OmniCore.Tests.ProviderConnectionApiTests` → exit 0, 22/22.
- Suite de arquitectura (rev. OAuth-reference, artefactos por defecto): **55/56**; el único
  fallo (`CliAssemblyReferenceTests.Omni_assembly_references_only_allowed_projects`) lo causa
  el archivo en curso del trabajador del menú (`src/OmniCore.Cli/Tui/ProviderConnectionMenu.cs`,
  `using OmniCore.Domain;`), ajeno a este bloque.
- Regresión enfocada del bloque 1: `-class OmniCore.Tests.ProviderConnectionApiTests` → exit 0,
  **22/22 correctos** (criterios del commit aceptado 3247b30 intactos).
- `dotnet run --project tests/OmniCore.ArchitectureTests` (artefactos por defecto) → exit 0,
  **56/56**. Nota de entorno: con el `ArtifactsPath` aislado los 4 tests que suben desde la
  carpeta del binario para hallar el repo (`OmniCore.slnx`) fallan de forma transitoria/ambiental
  (el binario queda fuera del árbol del repo); con artefactos por defecto pasan 56/56. No es
  un cambio de este bloque; el runbook de esta entrega usa los artifacts aislados para los
  tests enfocados y el grafo por defecto para arquitectura.
- Smoke manual real (sin login, sin red autenticada): `ClaudeAccountService.GetStatusAsync`
  contra la máquina real → `RuntimePresent=True Version=2.1.269`,
  `Free=NotEligible Pro=Eligible`, fuente citada. El binario `claude` 2.1.269 instalado en el
  PATH del usuario se localizó y midió correctamente.

## Cobertura de tests (33)

1. Elegibilidad: Free `NotEligible` con fuente primaria; Pro/Max/Team/Enterprise `Eligible`;
   Unknown `RequiresMeasurement` (mención explícita de `/status`).
2. Locator: binario en PATH; reserva del instalador nativo `<profile>/.local/bin`; null si no
   hay instalación.
3. Estado: sin runtime → `RuntimePresent=false` y **cero procesos lanzados**; con runtime →
   medición real vía `IProcessRuntime.Launch` con argv `["--version"]`, stdio capturado,
   delta de entorno vacío y probe acotado; versión solo-secuencia extraída (la salida del hijo
   con un token falso nunca aparece en `Version` ni `Detail`); probe con exit≠0 o timeout →
   presente sin versión inventada y árbol cortado; cancelación previa → `OperationCanceledException`
   y cero lanzamientos.
4. Sesiones interactivas: Login/Logout/OpenSession → binario oficial, argv vacío (OmniCore no
   maneja stdin), stdio sin capturar, entorno vacío, sin cortes al terminar; `SetupToken` →
   `["setup-token"]` sin capturar y detalle solo técnico; sin runtime → `runtimeNotInstalled`
   con clave `claude.auth.runtimeNotInstalled`; timeout → `CancelTree` + `TimedOut`; cancelación
   → `CancelTree` + `Cancelled`; lanzamiento fallido → `runtimeLaunchFailed` sin eco del
   mensaje del SO ni de rutas; shim `.cmd` en Windows delega por `cmd.exe /d /s /c`.
5. Versión: escáner con 9 casos (`2.1.269 (Claude Code)`, `v12.0.0`, basura, `1.2`,
   `1.2.3.4`, …). Contrato de `ClaudeSessionOutcome` sin campo de salida del hijo (4 campos).

## Fixtures vs consultas reales

- Todos los tests usan un `IProcessRuntime` falso: **ningún test lanza el CLI real, autentica,
  consulta red ni muta cuentas**. La medición real se acreditó solo con el smoke manual de
  `claude --version` (sin autenticación, sin costo).
- Lo que las fixtures **no** acreditan: el flujo real de `/login`/`/logout`/`setup-token` en
  el navegador de Anthropic (acción explícita futura del usuario), el comportamiento de
  terminación limpia por control request en Windows (ADR-0012 §5 lo marca para verificación
  al implementar M7) y el plan real del usuario.

## Problemas/limitaciones sin resolver

1. **Estado de sesión no medible sin el usuario**: no existe comando no interactivo
   documentado para el estado de login; un probe delegado (`claude -p`) gastaría suscripción y
   requiere consentimiento explícito — queda fuera de este bloque (ADR-0012/M7).
2. Las claves de recursos `claude.auth.*` y las sugeridas para el menú (`claude.account.*`)
   aún no tienen textos es/en: los añade el trabajador del menú según el handoff (la
   resolución degrada a la clave cruda mientras tanto).
3. El cableado del menú real es del trabajador paralelo (`TuiProviderConnectionHost.cs`); esta
   entrega no edita `TuiAccountHost.cs` ni `CliApp.cs` (superficie CLI existente intacta).
4. No se creó ninguna fila provider/model para la lane delegada: es M7 (ADR-0012 rev. 3);
   este bloque solo asegura runtime, sesión y elegibilidad honesta.

Sin commits: la raíz recoge los commits por archivos exactos tras las dos entregas finales.

---

# Reevaluación OAuth (v2, 2026-10-08)

Tras retirar la raíz el veto arquitectónico interno (ADR-0011 rev. 4), este bloque reevaluó el
flujo por cuenta desde cero, separando tres caminos. Referencia de source: el mismo clone de
solo lectura ya fijado — `https://github.com/Icarus603/claude-code.git` @
`7c918f786828f7071d023e1c401bc6be754446e3` (`oauth-reference-artifacts/claude-code`, derivado
del sourcemap npm v2.1.88, UNLICENSED: solo evidencia de comportamiento, cero código
transplantado). Documentación primaria descargada hoy, sin autenticación, a
`oauth-reevaluation-artifacts/docs/`: authentication, legal-and-compliance, headless (y setup).
No se ejecutó ninguna autenticación, ninguna llamada autenticada y ninguna mutación de cuenta.

## Camino 1 — OAuth nativo con client registration de OmniCore: NO disponible (falta autorización)

**Hechos de protocolo (evidencia del source de referencia, no política):**
- Client id fijo del binario: `9d1c250a-…` (`packages/provider/src/oauthConstants.ts`).
- El binario admite **client ids ajenos provistos por Anthropic** vía
  `CLAUDE_CODE_OAUTH_CLIENT_ID` (comentario del propio repo: «e.g., for Xcode integration»;
  `refreshOAuthToken` mantiene el client id ajeno pegado entre refreshes). Es decir: los client
  ids de terceros existen, pero se **provisionan bilateralmente por Anthropic** — no hay
  registro público de aplica­ciones ni consent-screen genérica.
- Scopes de suscripción: `user:inference` (inferencia), `user:profile`, `user:sessions:claude_code`,
  `user:mcp_servers`, `user:file_upload`; TTL del token largo 1 año; beta `oauth-2025-04-20`.

**Lo que haría falta para implementarlo legítimamente (exacto):**
1. Un client id OAuth **provisionado por Anthropic para OmniCore** (equivalente al de la
   integración Xcode), emitido por acuerdo bilateral — no hay programa público.
2. Que Anthropic otorgue a ese client los scopes claude.ai de suscripción
   (`user:inference`, opcionalmente `user:profile`).
3. Un cambio de la página oficial «Legal and compliance» (consultada hoy): dice expresamente
   que OAuth «is intended exclusively for purchasers of Claude Free, Pro, Max, Team, and
   Enterprise subscription plans» y que a los desarrolladores de terceros no se les permite
   ofrecer login de Claude.ai en sus aplicaciones, enrutar requests con credenciales de planes
   Free/Pro/Max, ni recolectar/almacenar/intermediar credenciales o tokens de sesión de
   Claude.ai («sign-in to a Claude account must complete through Anthropic's own flow»).
   Hoy dirige a los desarrolladores a API key por Console o «contact sales».

Sin esos tres elementos, alojar el flujo en OmniCore sería reutilizar el client id de Claude
Code o suplantarlo: prohibido por la tarea, por ADR-0011 §3.3 y por la propia página legal.

## Camino 2 — Console vs suscripción: dos credenciales distintas

- **Console** (facturación API, MeteredCurrency): el login Console del CLI oficial con el
  scope `org:create_api_key` **crea una API key** vía `/api/oauth/claude_cli/create_api_key` y
  la guarda el CLI; la alternativa documentada v2.1.242+ guarda el token OAuth como perfil.
  Para OmniCore esta vía ya está implementada y aceptada como bloque 1
  (`ProviderConnectionService`, `AnthropicConnectionValidator`): la API key de Console se
  conecta directamente, sin OAuth, porque Console emite keys estáticas. No se tocó.
- **Suscripción** (Pro/Max/Team/Enterprise): token OAuth de claude.ai con `user:inference`,
  refrescado por el propio binario (coordinación con lockfile y dead-set de `invalid_grant`
  demostrada en el source de referencia). OmniCore no puede legítimamente participar del
  login (Camino 1) ni tocar esos tokens.

## Camino 3 — binario oficial que devuelve respuestas a OmniCore: IMPLEMENTADO (v2)

La entrega v1 era locator + sesión interactiva; el humad pidió «la integración autenticada más
cercana y fiel que funcione, no una fachada de locator». El modo no interactivo documentado del
CLI oficial (`claude -p`, página «Run Claude Code programmatically») cumple exactamente eso:

- `claude -p <prompt> --output-format json` → un único mensaje `result` JSON con
  `result` (texto de la respuesta), `session_id`, `num_turns`, `total_cost_usd` (estimación del
  cliente), `usage` y `structured_output` con `--json-schema` (doc oficial + esquema
  `SDKResultMessage` en `packages/headless-sdk/src/coreSchemas.ts` de la referencia).
- Exit code 0 en éxito, ≠0 en fallo; «When a failure happens inside the run, such as missing
  authentication, Claude Code prints the failure as the result on stdout» (doc oficial).
- **Sin `--bare`**: ese modo nunca lee el login por suscripción (doc oficial; ya lo registraba
  ADR-0012 rev. 3).
- Permisos: `--permission-mode dontAsk` + `--permission-prompts none` deniegan todo lo que
  pediría aprobación (flags verificados en el CLI 2.1.269 instalado; `--permission-prompts`
  requiere ≥2.1.259). Sin `--allowedTools`, ninguna tool con prompt queda permitida. Es un
  acote del bloque de cuenta: **no es la lane delegada de ADR-0012** (esa sigue en M7 con
  worktree, TaskPacket y gates; aquí no hay scheduler ni subagentes).

### Entrega v2

| Archivo | Cambio |
|---|---|
| `src/OmniCore.Host/ClaudeAccountDelegation.cs` | Nuevo: `ClaudeDelegationRequest`, `ClaudeDelegationOutcome`, `ClaudeDelegationTerminalKind`, `ClaudeDelegationUsage`, `ClaudeAccountDelegationService` (argv tipado, stdout capturado, delta de entorno vacío, timeout 10 min, `CancelTree`, parser del `result` documentado). |
| `src/OmniCore.Host/ClaudeAccount.cs` | Solo refactor interno: `WrapWindowsShim` extraído como `internal static` (compartido con la delegación). Firmas y semántica del contrato congelado intactas. |
| `src/OmniCore.Host/OmniHost.cs` | Factory aditiva `CreateClaudeAccountDelegationService()`. |
| `tests/OmniCore.Tests/ClaudeAccountDelegationTests.cs` | Nuevo: 24 tests de comportamiento (ver abajo). |
| `tests/OmniCore.Tests/ClaudeAccountOAuthReferenceTests.cs` | **Eliminado**: sus tests de veto por literales OAuth no probaron comportamiento (lección registrada; sustituidos por tests del camino implementado). |
| Handoff | `C:/Users/juanc/.codex/pi-provider-menu-glm-split-20261008/oauth-reevaluation-contract.md` (aditivo; `account-contract.md` sigue congelado). |

**Sin fallback a la vía pagada (estructural):** el delta de entorno del hijo es vacío y la
allowlist de plataforma (ADR-0037) excluye `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN`; como
el modo `-p` usará una API key del entorno si estuviera presente (doc oficial «Authentication»),
no heredarla de OmniCore es la garantía: el CLI usará el login de suscripción del usuario. El
servicio no acepta credential stores, secretos ni HTTP (test constructor-dependencies).

**Refresh/expiry:** no aplica en OmniCore — el CLI oficial refresca y expira sus propios tokens
en su propio flujo; OmniCore nunca ve tokens, así que no tiene (ni tendrá en este bloque)
lógica de refresh. El fallo de autenticación del CLI se transmite honestamente como
`error_during_execution` + `errors` del result parseado; la guía al usuario es abrir la sesión
interactiva (`RunInteractiveAsync(Login)`) y verificar con su `/status`.

## Cobertura de tests v2 (24, `OmniCore.Tests.ClaudeAccountDelegationTests`)

Flujo request→result (respuesta real parseada: texto, session_id, num_turns, coste, usage con
cache read/write, `structured_output`), argv tipado documentado (incluye `-p`, json,
dontAsk, prompts none, opt-ins `--model/--max-turns/--max-budget-usd/--json-schema`), errores
del CLI sin excepciones (`error_during_execution`, `error_max_turns`, `NoResultMessage`), sin
eco de stdout/stderr del hijo (con token falso sembrado en ambas corrientes), delta de entorno
vacío, registro `ClaudeDelegationOutcome` sin campos de salida cruda, dependencias del
constructor sin Credential/Secret/Http, exactamente un proceso por delegación (no hay segunda
vía ni fallback), timeout→CancelTree, cancelación→CancelTree, pre-cancel→OCE sin lanzar,
`runtimeNotInstalled`, `runtimeLaunchFailed`, prompt vacío rechazado, shim `.cmd` por
`cmd.exe /d /s /c` con captura, parser puro (ruido alrededor del result, usage ausente → null
sin ceros inventados, coste 0 informado como 0) y cableado de la factory.

## Comandos y resultados v2 (Windows, SDK .NET 10.0.401)

Con artefactos aislados `C:/Users/juanc/.codex/pi-provider-menu-glm-split-20261008/oauth-reevaluation-artifacts/build`:

- `dotnet build src/OmniCore.Host/OmniCore.Host.csproj -p:ArtifactsPath=…` → exit 0, 0 warnings.
- `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -p:ArtifactsPath=…` → exit 0, 0 warnings.
- `-class OmniCore.Tests.ClaudeAccountDelegationTests` → exit 0, **24/24**.
- `-class OmniCore.Tests.ClaudeAccountTests` → exit 0, **33/33** (contrato congelado intacto).
- `-class OmniCore.Tests.ProviderConnectionApiTests` → exit 0, **22/22** (bloque 1 intacto).
- `dotnet build OmniCore.slnx` → exit 0, 0 warnings.
- Suite completa `dotnet test --solution OmniCore.slnx` (grafo por defecto) → **3118 casos:
  3103 correctos / 11 fallos / 4 skip (symlink)**. Los 11 fallos están TODOS fuera de la
  propiedad de este bloque: 10 × `ProviderConnectionMenuApiTests` + 1 ×
  `CliAssemblyReferenceTests.Omni_assembly_references_only_allowed_projects`
  (`OmniCore.Cli → OmniCore.Domain`), ambos causados por los archivos WIP sin commit del
  trabajador del menú (`src/OmniCore.Cli/Tui/TuiApp.cs`, `Tui/ProviderConnectionMenu.cs`,
  `tests/OmniCore.Tests/TuiWiringTests.cs`, `ProviderConnectionMenuApiTests.cs` — este bloque
  no los tocó; el fallo de menú es «menú de conexiones visible (no alcanzado en el tope de
  espera)» y ninguna pieza de ese camino referencia a `ClaudeAccount*`/`ClaudeDelegation*`,
  verificado por grep). Detalle del fallo del menú:
  `results/oauth-reevaluation/menu-tests-fail-detail.log` del task root.

## Fixtures vs consultas reales (v2)

- Ningún test autentica, consulta la red ni muta cuentas: `IProcessRuntime` falso.
- La invocación headless se verificó contra la doc oficial y los flags del binario 2.1.269
  instalado (`claude --help`, sin autenticación); la respuesta real del CLI no se capturó aquí
  porque cada `claude -p` consume la suscripción del usuario: **la primera delegación real es
  una acción explícita del usuario** y consumirá su cuota.
- Lo que las fixtures no acreditan: la forma exacta del `result` cuando el backend cambie
  (proteger con tests de contrato grabados al primar en vivo), y el plan real del usuario.

## Problemas sin resolver (v2)

1. Camino 1 requiere lo de §Camino 1 (sin cambios desde Anthropic).
2. Los fallos de la suite del menú son del trabajador del menú; este bloque no puede
   arreglarlos sin tocar archivos ajenos.
3. `--permission-prompts none` exige CLI ≥2.1.259: con un CLI más viejo la delegación termina
   en `NoResultMessage` (flag rechazado) con exit ≠0; opción futura: degradar a
   `--permission-mode dontAsk` solo, tras verificar en vivo el comportamiento de prompts.
