# ADR-0011 — Mecanismo de conexión a proveedores de modelos

- **Estado:** Aceptada — rev. 4 (2026-10-08); conexión por cuenta Anthropic en reevaluación por petición del usuario.
- **Rev. 4:** se retira el veto arquitectónico interno a investigar OAuth de Anthropic. El flujo por cuenta se evalúa separadamente del adaptador API y de la lane delegada; la existencia de un protocolo no acredita autorización del proveedor ni una integración autenticada.
- **Rev. 2:** se alinea con ADR-0005 rev. 2 (familias provider-native en lugar de "dialectos" sobre OpenAI-compatible) y con ADR-0018 (`ISecretProvider`).
- **Rev. 3:** especifica la conexión al provider Anthropic imitando la lógica de Claude Code (§10): wire protocol, streaming, thinking con firma, reintentos y timeouts, prompt caching concreto y OAuth de suscripción como descarte. Verificado contra la documentación oficial de la API y contra la copia filtrada del source de Claude Code (leak del source-map de npm, 2026-03-31). No cambia las decisiones vigentes: Anthropic se usa solo con API key (§3.3).
- **Complementa:** ADR-0005 (contratos del Model Runtime)
- **Spec:** §17–§22, §70–§73, INV-010, INV-011

## Contexto

OmniCoder delega casi toda la conexión a Pi (`@earendil-works/pi-coding-agent`): 38 proveedores, login por suscripción vía el `/login` de Pi, streaming, reintentos y catálogo (`get_available_models`). El único camino C# directo es el local (`LocalModelEngine`, `LocalAssistantGateway`). OmniCore no puede depender de Pi, así que debe asumir todo eso.

Observaciones de OmniCoder que motivan decisiones:

- Pi solo acepta tres dialectos (`openai-completions`, `openai-responses`, `anthropic-messages`) y con ellos cubre sus proveedores.
- Las credenciales quedan en texto plano en `~/.pi/agent/auth.json` porque Pi no lee almacenes cifrados.
- `LocalModelEngine` no maneja caídas del servidor (sin handler de `Exited` ni reinicio) y usa `GET /v1/models` como readiness.
- No hay manejo de 429 / `Retry-After` / backoff en el código propio.

## Decisión

### 1. Adapters por familia de protocolo nativo, no por proveedor

Las familias y contratos están en ADR-0005 rev. 2. Este ADR define cómo se conectan.

| Adapter (familia) | Perfil / endpoint | Cubre |
|---|---|---|
| `OpenAiChatCompatibleProvider` | `/v1/chat/completions` | ik_llama / llama.cpp, OpenRouter, DeepSeek, Groq, xAI, Mistral, Together, Fireworks, Cerebras… |
| `OpenAIResponsesProvider` | perfil `api`: Responses API con API key | Modelos OpenAI |
| `OpenAIResponsesProvider` | perfil `codex`: backend ChatGPT con OAuth | Modelos OpenAI con la suscripción ChatGPT del usuario (§3.4) |
| `AnthropicMessagesProvider` | Messages API nativa | Anthropic — protocolo completo en §10 (prompt caching, thinking/firma, structured outputs via `output_config`) |

- Cada adapter implementa `IModelProvider` con un cliente HTTP propio y delgado (`HttpClient` + System.Text.Json con source generation). No se usan SDKs oficiales: añaden peso y no exponen las extensiones de llama.cpp.
- Diferencias entre endpoints "compatibles" se declaran como **capacidades/quirks** en el `ModelDescriptor` / `ProviderDescriptor`, nunca con `if (provider == ...)`.
- Extensiones llama.cpp (gramática GBNF, `json_schema`, `/tokenize`, `/props`, slots) se activan por capacidad declarada.

### 2. Registro declarativo de proveedores, modelos y alias

```yaml
providers:
  local:      { family: OpenAiChatCompatible, baseUrl: auto, auth: none, host: managed }
  openrouter: { family: OpenAiChatCompatible, baseUrl: https://openrouter.ai/api/v1, auth: { kind: apiKey, ref: openrouter } }
  anthropic:  { family: AnthropicMessages, auth: { kind: apiKey, ref: anthropic } }
models:
  qwen-27b: { provider: local, params: 27B, context: 65536, toolFormats: [Native, Grammar] }
  sonnet:   { provider: anthropic, context: 200000, cost: { inPerMTok: 3, outPerMTok: 15 } }
aliases:
  local-worker:    qwen-27b
  frontier-strong: sonnet
```

- El router trabaja con **alias** y con el `EffectiveModelProfile` de cada modelo (ADR-0007), nunca con ids de modelo. Nombres como Luna o Sol viven aquí (spec §21).
- Descubrimiento (`/v1/models`, `/props`, catálogos) **completa** el descriptor; lo declarado prevalece.
- **Ubicación (revisión integral):** `providers.yaml` en el directorio de configuración de usuario (ADR-0039 §2). **Providers, `baseUrl` y `auth.ref` solo se definen en scope User**; un repo nunca puede redefinirlos (ADR-0039 §4). `~/.omnicore/` no existe.
- **Catálogo (models.dev):** se usa un **snapshot embebido** en el binario. La actualización por red es opt-in (`models.catalog.refresh: true`), coherente con local-first.
- **Registro mínimo en M2:** un provider, un modelo por defecto, sin alias ni router. Si no hay modelo configurado, se emite el error tipado `NoModelConfigured`, con la sugerencia de `/doctor` y un asistente de configuración en el CLI.

### 3. Credenciales y autenticación

1. Los secretos se leen solo mediante `ISecretProvider` (ADR-0018). Detrás está `ICredentialStore` (escritura: **Windows Credential Manager** o DPAPI), con variables de entorno y comandos como fuentes alternativas. Los secretos nunca aparecen en configuración, eventos, `ContextSnapshot`, artifacts ni logs; el redactor de ADR-0018 los elimina en cada sink.
2. `IAuthProvider` por proveedor con tipos `None | ApiKey | Bearer | OAuth`. La arquitectura soporta OAuth **desde el inicio** (refresh de tokens, expiración, revocación), para no tener que rediseñar cuando se habilite un flujo.
3. **Login por suscripción:**
   - **Anthropic (cuenta y API): evaluación independiente.** Se retira la exclusión interna de OAuth. Investigar el flujo de cuenta y sus capacidades reales usando el código de referencia y fuentes del proveedor; no confundir API, suscripción y ejecución delegada. La implementación entregada hasta ahora usa API key o el ejecutable oficial. No hay aceptación de un flujo OAuth nativo autenticado. Ver §10.7.
   - **OpenAI (ChatGPT): requerimiento obligatorio, se implementa.** Ver §3.4.
   - **Regla general: no se reutilizan client IDs de otros productos ni se suplanta a otro producto.** Pi usa el client ID de Claude Code y el de VS Code Copilot, y **se hace pasar por Claude Code**: `user-agent: claude-cli/…`, remapeo de nombres de tools e inyecta "You are Claude Code…" en el system prompt. Nada de eso se porta. La única excepción es el flujo de ChatGPT de §3.4, porque OpenAI respalda públicamente que harnesses de terceros lo usen, y aun así OmniCore se identifica como OmniCore.
4. **Login con suscripción de ChatGPT (obligatorio).**
   - **Base de legitimidad:** Tibo Sottiaux (OpenAI, Codex) anunció que Pi se sumó a "la lista de agentes abiertos que soportan iniciar sesión con tu cuenta de ChatGPT y usar el mismo uso que obtienes en Codex" (traducido; [x.com/thsottiaux](https://x.com/thsottiaux/status/2012030806169121160)). El programa *Codex for Open Source* de OpenAI nombra a OpenCode, Cline y pi como herramientas que apoya. Son declaraciones públicas, **no contractuales**: OpenAI puede cambiar la política.
   - **Flujo** (el mismo que Pi y OpenCode, portado desde su código):
     - OAuth 2.0 + PKCE contra `https://auth.openai.com` con el client público de Codex, `scope openid profile email offline_access`, callback en loopback `http://localhost:1455/auth/callback`.
     - Alternativa **device code** (`/api/accounts/deviceauth/usercode` → `/codex/device`) para entornos sin navegador o con el puerto 1455 ocupado.
     - Referencias: Pi `pi-ai/dist/auth/oauth/openai-codex.js`, OpenCode `packages/opencode/src/plugin/openai/codex.ts`.
   - **Identificación honesta:** `originator: omnicore` y `User-Agent: omnicore/<versión> (<os>; <arch>)`. Nunca se imita el user-agent ni los prompts de Codex CLI.
   - **Tokens:**
     - `access`, `refresh`, `expires` y `chatgpt_account_id` (claim `https://api.openai.com/auth` del JWT) se guardan en `ICredentialStore` (Credential Manager / DPAPI), nunca en texto plano.
     - El refresh es preventivo antes de expirar y está serializado entre procesos. Si un 401 persiste tras un refresh, el error es `AuthenticationFailed`.
   - **Adapter:** perfil `codex` de `OpenAIResponsesProvider` (ADR-0005).
     - Endpoint `https://chatgpt.com/backend-api/codex/responses` con header `ChatGPT-Account-Id`.
     - Quirks declarados como compat flags: `store: false` obligatorio, `instructions` como system prompt.
     - Transporte SSE primero. WebSocket (beta `responses_websockets`) queda como optimización posterior, con fallback a SSE como hace Pi.
   - **Uso individual:** cada usuario inicia sesión con su propia cuenta. OmniCore no comparte, revende ni intermedia el acceso.
   - **Contención del riesgo:**
     - Queda aislado en `ChatGptSubscriptionAuthProvider` + el perfil `codex`.
     - Tests de contrato con respuestas grabadas detectan cambios del backend.
     - El circuit breaker y el router hacen fallback a API key o a modelos locales si el backend rechaza o cambia.
     - `/doctor` reporta el estado de la sesión de ChatGPT.
   - **Complemento opcional:** una lane delegada al CLI oficial de Codex (`codex exec --json`), análoga a ADR-0012, si algún día el flujo directo deja de estar disponible.
5. Resolución de secretos por comando (idea de Pi, `resolve-config-value`): `auth.ref` puede apuntar a un gestor de contraseñas (`cmd:op read …`). El comando lo define el usuario en su configuración de scope User, nunca un proyecto ni el modelo.
6. Validación de endpoints portada de `ProviderEndpointValidator` de OmniCoder: URL absoluta http(s), sin `user:pass`, redirecciones sin downgrade HTTPS→HTTP ni cambio de host.

### 4. Servidor local: ambos modos desde M2, referencia ik_llama

- `LocalModelHost` soporta dos modos:
  - **attach:** se conecta a un `llama-server` ya levantado.
  - **managed:** lo lanza y supervisa.
- El modo managed porta de `LocalModelEngine`: puerto efímero asignado por el SO en loopback, `--api-key` aleatoria por proceso y control del árbol de procesos (`IProcessTreeControl`, ADR-0038 §2: Job Object en Windows, cgroup o grupo de procesos en Linux). Por eso **M2 incluye un `IProcessRuntime` mínimo** (lanzar, controlar el árbol, cancelar); el confinamiento de filesystem y red, y la tool `process.exec`, siguen en M3.
- El modo managed añade: **detección de caída, reinicio con límite y backoff**, y los eventos `ProviderUnavailable` / `ProviderRestarted`.
- **Cableado (A3, 2026-10-09).** El modo se declara por provider en `providers.yaml` (solo scope User, ADR-0039 §4) y lo supervisa `LocalServerSupervisor` en el Host:
  - `host: attach`: antes de cada Turn comprueba por TCP que el servidor ya levantado responde, y falla con un error tipado (`localServer.unreachable`) en lugar de gastar reintentos contra un servidor caído. Sin `host`, un provider OpenAI-compatible con `billingMode: Local` es un attach **implícito**: `omni doctor` lo diagnostica, pero el Turn no lo sondea.
  - `host: managed` + `managed: { executable, args, workingDirectory, readinessTimeoutSeconds }`: arranca el servidor al primer uso, con puerto efímero y API key aleatoria generados en runtime (la clave solo viaja por el entorno del proceso y se registra en el redactor de secretos), espera su readiness real (`GET /models` con la clave), lo reutiliza entre Turns y lo detiene en `Dispose` del runtime o al terminar el proceso. `{port}` en `args` se sustituye por el puerto elegido. Con `baseUrl: auto` (o sin `baseUrl`) el puerto es efímero; un `baseUrl` loopback con puerto lo fija.
  - **Identidad de la ruta:** con puerto efímero la ruta conserva un endpoint lógico estable (`http://127.0.0.1/v1`); el endpoint real solo se usa para conectar. Así la autorización de ruta, la clave de cualificación y los consentimientos (ADR-0046) no cambian de un arranque a otro.
  - **Reinicio:** si el servidor managed cae, el siguiente Turn lo relanza con espera creciente (1 s × n); tras 3 arranques en la vida del proceso no se reintenta y el error es `localServer.restartLimit`. Un endpoint distinto del configurado (`OMNI_BASE_URL`) se trata como attach, nunca como un segundo servidor managed.
  - La salida del servidor se drena y descarta (`ProcessLaunch.DiscardOutput`): sin drenarla, el buffer de la tubería lo bloqueaba al escribir.
  - **Pendiente:** los eventos `ProviderUnavailable` / `ProviderRestarted` en el journal (hoy el estado solo se refleja en el error del Turn y en `omni doctor`).
- **Referencia: ik_llama.** Verificado en `OmniCoder/installer/ik-llama/llama-server.exe` (build `baac291`):
  - `--help` expone `--jinja`, `--chat-template[-file]`, `--grammar[-file]`, `--json-schema`, `--parallel-tool-calls`, `--parallel`, `--api-key[-file]`, `--metrics` y el endpoint de slots.
  - **Pendiente de verificar en M2 con prueba en vivo:** `grammar` / `json_schema` **por request** (no solo por CLI), tool calls nativas con `--jinja`, y existencia de `/health` (OmniCoder usa `/v1/models` como readiness).
  - Las capacidades se declaran por servidor y se prueban; no se asumen. Esto mantiene abierta la compatibilidad con llama.cpp upstream.

### 5. Resiliencia normalizada

- Stream normalizado (`ModelStreamEvent`: texto, razonamiento, tool call parcial/completa, uso, fin con causa).
- Tres timeouts: conexión, **primer token**, **inactividad entre tokens**. El último alimenta `LaneHeartbeat` (spec §11) para distinguir generación lenta de provider muerto.
- Reintentos con backoff exponencial y jitter en 429/5xx/errores de red, respetando `Retry-After`. Nunca se reintenta tras haber emitido tool calls en el stream.
- Circuit breaker por proveedor: su estado alimenta `RuntimeAvailability` del router (spec §20).
- Errores tipados (spec §71): `ProviderUnavailable`, `RateLimited`, `ContextOverflow`, `ModelFailure`, `Cancellation`, `AuthenticationFailed`.

### 6. Uso, costo y presupuesto

- `TokenUsage` normalizado: input, output, cache read/write, reasoning.
- Costo calculado desde el precio declarado en el descriptor cuando el proveedor no lo informa.
- Patrón portado de `OrchestrationUsageMeter`: **reservar el costo máximo antes de una llamada pagada**, validado contra `TaskBudget`, y liquidar con el uso real.

### 7. Cola por endpoint

- Cola con prioridad por endpoint (portada de `LocalAssistantGateway`: interactivo/background 4:1).
- Concurrencia limitada a los slots (`--parallel`) del servidor local o al límite declarado del proveedor.

### 8. Código de referencia: portar desde Pi y OpenCode

El código de referencia viene de:
- Pi (`github.com/earendil-works/pi`, autor Mario Zechner).
- OpenCode (copia local en `repos/opencode-src`).

Se **portan a C#** (no se traducen línea a línea).

| Pieza | Origen principal | Por qué |
|---|---|---|
| Flags de compatibilidad tipados (`supportsDeveloperRole`, `maxTokensField`, `thinkingFormat`, `requiresToolResultName`, …) + `detectCompat`/`getCompat` | Pi `pi-ai/types.d.ts:468-622`, `api/openai-completions.js:1236-1356` | Es la forma más limpia de declarar quirks. Encaja con §1: registros C# con overrides opcionales |
| Normalización entre familias (replay de thinking, IDs de tool call, degradación de imágenes) | Pi `api/transform-messages.js` | Lógica pura |
| Implementaciones de dialecto como referencia | Pi `api/*.js`, código async simple; OpenCode `llm/src/protocols/*.ts`, diseño Protocol/Route | Guían los adapters de §1 |
| Prompt caching (breakpoints Anthropic, `prompt_cache_key`) | Pi `anthropic-messages.js`, `openai-prompt-cache.js`; OpenCode `provider/transform.ts:358-407` | Reglas pequeñas, mucho ahorro |
| Clasificador de overflow (25 regex anotadas + overflow silencioso) | Pi `utils/overflow.js`; OpenCode `llm/src/provider-error.ts` | Tablas de datos |
| Política de reintentos (`x-should-retry`, `Retry-After`, tope 60 s, backoff + jitter, lista no reintentable de cuota/billing) | Pi `utils/provider-retry.js`, `utils/retry.js`; OpenCode `session/retry.ts` | Alimenta §5 |
| Reparación de JSON y coerción de argumentos guiada por esquema | Pi `utils/json-parse.js`, `utils/validation.js` | Primer paso del pipeline de reparación de ADR-0007 |
| Cálculo de uso y costo (tiers, cache 1 h a 2×, reasoning ⊂ output) | Pi `models.js:530-549`; OpenCode `session/session.ts:338-405` | Alimenta §6 |
| Catálogo de modelos | models.dev (`models.dev/api.json`); esquema de referencia en OpenCode `core/src/models-dev.ts:66-129` | Completa `ModelDescriptor` (§2); lo declarado prevalece |

**No se portan:**
- **cabeceras de identidad y betas internas de Claude Code** (ver §10.8): la conexión nunca se hace pasar por Claude Code ni por otro producto (§3.3);
- flujos OAuth que reutilizan client IDs ajenos o suplantan otros productos (§3.3);
- el runtime de OpenCode sobre Vercel AI SDK / Effect (habría que rediseñarlo, no traducirlo);
- la instalación de paquetes npm en runtime;
- el almacenamiento `auth.json` en texto plano de ambos (`0o600` no protege nada en Windows).

**Lo que ninguno resuelve y OmniCore añade:** gramática GBNF / `json_schema` para modelos locales (ADR-0007), conteo exacto con `/tokenize` (ambos estiman con chars/4) y supervisión del servidor local.

### 9. Estrategias de ahorro con modelos frontera

Tres mecanismos, sin ninguna forma de evasión:

1. **Lanes delegadas a Claude Code** con la suscripción del propio usuario, por la vía que Anthropic permite. Ver ADR-0012.
2. **Prompt caching como política, no como detalle.**
   - El `ContextMaterializer` ordena el contexto de lo estable a lo volátil: system → tools → skills → task → historia → turno actual. Así el prefijo cacheable es máximo.
   - Los breakpoints se colocan según la familia: en **Anthropic** se sigue el patrón concreto de §10.6 (bloques estables de `system` + un marcador a nivel de mensaje, TTL latchado por sesión, tope de 4 breakpoints). OpenAI usa `prompt_cache_key`.
   - `TokenUsage` registra cache read/write por Turn. `/context` y `/stats` muestran la tasa de aciertos de caché.
   - Tests deterministas verifican que dos Turns consecutivos comparten el prefijo byte a byte.
3. **Enrutamiento por costo.**
   - El router (ADR-0007) manda a modelos locales todo lo que no requiere un modelo frontera: meta-tareas, exploración, verificación.
   - La escalación a frontera es explícita y lleva una causa registrada (spec §73).
   - Tareas no interactivas (resúmenes masivos, clasificación, evaluación de la regression suite) pueden ir a la **Message Batches API** de Anthropic, más barata a cambio de latencia. Se modela como un modo de ejecución del provider, no como otro provider.

### 10. Conexión a Anthropic: protocolo portado de Claude Code (rev. 3)

**Alcance y fuentes.** Esta sección fija cómo `AnthropicMessagesProvider` (ADR-0005) habla con la Messages API. La referencia de comportamiento es Claude Code, cuyo source (leak del source-map de npm, 2026-03-31; mirror de consulta `github.com/tanbiralam/claude-code`) documenta las decisiones de producto que la doc oficial deja abiertas, y se contrasta con tres capas verificadas hoy: (a) doc oficial de la Messages API y del streaming, (b) doc oficial de prompt caching, (c) comportamiento observado de Claude Code (reintentos, timeouts, TTL de caché, loop de tools). **No se copia ninguna cabecera de identidad ni beta interna de Claude Code** (coherente con §3.3 y con las reglas de §8).

#### 10.1 Endpoint, cabeceras y request

```text
POST https://api.anthropic.com/v1/messages
```

- Cabeceras fijas: `anthropic-version: 2023-06-01`, `content-type: application/json`, `x-api-key: <desde ISecretProvider>` (nunca `Authorization: Bearer` salvo el flujo descartado de §10.7).
- `User-Agent: omnicore/<versión> (<os>; <arch>)` (la misma identificación honesta de §3.4). Nunca `claude-cli/…`.
- `x-client-request-id: <uuid>` por request para correlacionar timeouts (patrón de Claude Code, `client.ts`).
- `anthropic-beta`: por defecto ninguna. Solo se envían **betas públicas** que un modelo o característica requiera, declaradas como compat flag `requiredBetaHeaders` del `ProviderDescriptor` y cubiertas por un test de contrato. Quedan excluidas las betas de producto de Claude Code (`claude-code-20250219`) y las internas 1P (`prompt-caching-scope-…`, `cli-internal-…`, `redact-thinking-…`).

Cuerpo (mapeo desde `ModelRequest`, ADR-0005):

| Campo API | Fuente | Regla |
|---|---|---|
| `model` | `ModelSelection` | id canónico (se degradan sufijos de fecha) |
| `max_tokens` | perfil del modelo / presupuesto | tope por modelo; por defecto el patrón de Claude Code: 8k con escalación única a 64k por slot |
| `system` | `Instructions` | **array** de bloques de texto; `cache_control` en los estables (§10.6) |
| `messages` | `Messages` normalizados | §10.2 |
| `tools` | proyección del ToolPlan | `{name, description, input_schema}` en JSON Schema 2020-12 |
| `tool_choice` | `ToolMode` | omitido en el loop agent (equivale a `auto`); ver §10.2 |
| `thinking` | `Reasoning` | §10.4 |
| `temperature` | `Reasoning` | solo si el thinking está deshabilitado (con thinking la API exige `1`) |
| `metadata.user_id` | opaque por workspace | uuid sin datos personales |
| `output_config` | `OutputConstraint` (ADR-0007) | `format:{type:"json_schema", schema}` para salida estructurada; `effort` cuando el perfil lo declara |
| `stream` | — | siempre `true` (§10.3); variante no-streaming en §10.5 |

- No se envían `service_tier` ni `stop_sequences` (comportamiento de Claude Code; la API los expone como opcionales y el runtime no los necesita).
- El cache se activa con `cache_control` top-level (automático) o con un marcador a nivel de mensaje: §10.6.

#### 10.2 Normalización de mensajes y reglas del turno

Portadas de `normalizeMessagesForAPI` / `ensureToolResultPairing` de Claude Code, adaptadas al contrato:

- **Cada bloque vive en su propio mensaje.** Se fusionan mensajes `assistant` consecutivos y se evitan dos `user` seguidos (la API los fusiona sola; la regla se respeta por uniformidad).
- Cada `tool_use` de historia se re-emite con `{type,id,name,input}`; los bloques de thinking se re-emiten con su `signature` intacta (§10.4).
- **Pareo obligatorio despejado (Anthropic no repara solo; un `tool_use` sin resultado da 400).** Si una tool queda pendiente por cancelación, el adapter inyecta `tool_result` con `is_error:true` y el texto de la interrupción (regla de `yieldMissingToolResultBlocks` de Claude Code). Esto satisface ADR-0004 para llamadas pendientes del Turn.
- **Nunca cerrar un mensaje en un bloque no-texto:** si tras insertar bloques el último es `tool_use` / `tool_result` / imagen, se anexa `{type:"text", text:"."}`.
- `tool_choice` en el loop agent se omite. Un `ToolMode` puntual (obligar o prohibir) se traduce a `{type:"tool", name}` / `{type:"none"}`.

#### 10.3 Streaming SSE → `ModelStreamEvent`

Parser propio de SSE con System.Text.Json + source generation (el cliente delgado de §1). Modelo de eventos tomado de `services/api/claude.ts` de Claude Code:

- **Accumulación de argumentos sin partial-parse:** los `input_json_delta` se acumulan en un string y se parsean una sola vez en `content_block_stop`. Claude Code evita explícitamente el parseo incremental (coste O(n²) por delta).
- El bloque `text` de `content_block_start` llega vacío; el parser trata el primer `text_delta` como contenido y no duplica el bloque.

Mapeo de eventos:

| Evento SSE | `ModelStreamEvent` |
|---|---|
| `message_start` (+ `usage`) | `ResponseStarted`, `UsageUpdated` inicial (TTFT → métricas) |
| `content_block_start` (`text` / `thinking` / `tool_use`) | `BlockStarted(index, kind)` |
| `content_block_delta` → `text_delta` | `TextDelta` |
| `content_block_delta` → `input_json_delta` | `ToolArgumentsDelta(index, partialJson)` |
| `content_block_delta` → `thinking_delta` / `signature_delta` | `ReasoningDelta` (+ `signature` para el `ProviderOpaque` al cerrar) |
| `content_block_stop` | materializa el bloque y emite `BlockCompleted(index, ContentBlock)` |
| `message_delta` | `stop_reason` + `UsageUpdated` final; `max_tokens` / `model_context_window_exceeded` → `ContextOverflow` |
| `message_stop` | `ResponseCompleted(ModelResponse)` |
| `error` (`overloaded_error`) | se trata como 529 (§10.5) |
| `ping` | no-op (mantiene el stream vivo; no cuenta como actividad) |

- **Quirk de usage:** en `message_delta` la API reenvía `input_tokens` en cero; solo se sobreescriben los contadores que llegan `>0` (patrón `updateUsage` de Claude Code). `cache_read_input_tokens` y `cache_creation_input_tokens` alimentan `TokenUsage` (cacheRead/cacheWrite).
- El `usage` de `message_start` es parcial; el definitivo llega en `message_delta`.

#### 10.4 Thinking y firma de continuidad

- Tipo decidido por **capacidad declarada** (`adaptiveThinking` en `DeclaredCapabilities`, ADR-0007), nunca por nombre o versión de modelo:
  - `{type:"adaptive", display:"summarized"}` si la capacidad lo permite (modelos 4.6+/Sonnet 4.6+);
  - si no, `{type:"enabled", budget_tokens}` con `1024 ≤ budget < max_tokens`, budget por defecto `min(declarado en perfil, max_tokens-1)`.
- El bloque `thinking` se emite como `ReasoningBlock` (`Visibility` según `display`); la `signature_delta` (o el `signature` del bloque `redacted_thinking` con `display:"omitted"`) se guarda en `ProviderOpaque` con `ReplayPolicy.SameModel` (ADR-0005 §4): se reenvía solo al mismo destino, en el mismo Turn con tools.
- **Regla de continuidad:** las firmas de los bloques del Turn anterior se devuelven en el siguiente request dentro de mensajes `assistant`, exactas y en orden; alterarlas produce 400 `invalid_request_error`.
- Con caché: los thinking de turns previos sí se cachean como contenido de mensajes `assistant` (sin llevar `cache_control` directo; §10.6).

#### 10.5 Reintentos, timeouts y 529

Política portada de `services/api/withRetry.ts` de Claude Code, alineada con §5 y con los invariantes (nunca reintentar tras tool calls emitidas en el stream):

- **Reintentables:** 408, 409, 429 (con `Retry-After`), 5xx, 529 (`overloaded_error`, incluido el que llega como evento `error` en streaming) y errores de red. No reintentables: 400/401/403 (auth/validación) y errores de cuota/billing.
- **Backoff:** `min(500 ms · 2^(intento-1), 32 s)` + jitter aleatorio del 25%. `Retry-After` (o `anthropic-ratelimit-unified-reset`, si el provider lo expone) tiene prioridad.
- **Intentos:** por defecto 10, configurable.
- **529:** máximo 3 consecutivos; después el adapter **no decide**: emite `ProviderUnavailable` y el router (§5) hace fallback a otro alias. Es el mismo reparto del `FallbackTriggeredError` de Claude Code, movido al router.
- **Timeouts:** conexión; **primer token** (TTFT, métricas); **inactividad entre eventos** (watchdog por defecto 90 s, configurables; alimenta `LaneHeartbeat`); global de 600 s (default de Claude Code).
- **Variante no-streaming:** para outputs de gran tamaño (tope de 64k tokens de salida) o cuando un stream falla sin `message_start` ni bloques completados, existe un path no-streaming de la misma request. Se desactiva si el stream ya emitió tool calls (evita ejecutar efectos dos veces).
- **Overflow de contexto:** el 400 `"input length and max_tokens exceed context limit"` o el `stop_reason` `model_context_window_exceeded` disparan `ContextOverflow`; el materializador recontrata con presupuesto y el adapter recomputa `max_tokens` con margen (piso razonable de salida).

#### 10.6 Prompt caching concreto para Anthropic

Refina §9.2 para esta familia:

- **Tope duro de 4 breakpoints** (la API devuelve 400 si se excede; el cache automático ocupa uno de esos slots).
- **Estrategia de marcadores (comportamiento actual de Claude Code):**
  - `cache_control` en los **bloques estables del `system`** (instrucciones y herramientas estáticas), y
  - **un único marcador a nivel de mensaje** en el último mensaje: en un `user`, el último bloque; en un `assistant`, el último bloque que no sea thinking/redacted. Un solo marcador deja libres las páginas KV no reanudables y mantiene el prefijo máximo cacheado (el cache cubre `tools → system → messages` en ese orden, sea cual sea el orden en el JSON).
  - **Alternativa equivalente y más simple:** cache automático (`cache_control` top-level), donde el servidor coloca el breakpoint en el último bloque cacheable y lo mueve solo. Se elige por config del provider (`caching.mode: automatic | explicit`).
  - Opcional: un breakpoint explícito al final de `tools` cuando la lista es grande y cambia poco entre Requests.
- **TTL latchado por sesión:** se decide `5m` (default) u `1h` (2× write) **una vez por sesión** según política de costo, y no se cambia a mitad de conversación: cambiar el TTL cambia la cache key y produce misses en cadena.
- **La caché solo encuentra _writes previos_, no contenido estable:** el `cache_control` va al final de un prefijo idéntico entre requests (fin de herramientas, fin de system, última `tool_result` del turno anterior). Nunca sobre un bloque que cambie con cada request (p. ej. el mensaje con la hora), o no habrá aciertos.
- **Lookback de 20 bloques:** mientras cada turno añada pocos bloques, el marcador del último mensaje acierta la write anterior. Si un turno añadiera ≥20 bloques distintos (por encima de la ventana), se coloca un segundo breakpoint estático. Una racha de `tool_use`/`tool_result` consecutivos cuenta como **una** posición, así que el tool use en paralelo no empuja la write fuera de la ventana por sí solo.
- **Mínimos cacheables por modelo** (512…4096 tokens según modelo; por debajo no se cachea, sin error). Se registran `cache_read` / `cache_creation` / `input` en `TokenUsage` (§6) y se exponen en `/context` y `/stats`; `total_input = cache_read + cache_creation + input`.
- **Thinking y caché:** en modelos antiguos, un mensaje de `user` que no sea `tool_result` invalida el cache y descarta los thinking previos; en Opus 4.5+/Sonnet 4.6+ los thinking se preservan. Se declara como capacidad (`thinkingCachePreservation`).
- **Instrucciones a mitad de conversación:** si un hook/skill añade instrucciones en turnos posteriores, en los modelos que lo soportan se anexan como mensaje `role:"system"` dentro de `messages` en lugar de editar `system` top-level (no invalida el prefijo). Se detecta por capacidad.
- **Pre-warming** (`max_tokens: 0`) queda opt-in y bajo presupuesto, para tareas previsibles antes de un turno largo.

#### 10.7 OAuth de suscripción: referencia y reevaluación

La rev. 4 retira el descarte interno del flujo por cuenta. Los datos siguientes son una referencia histórica de protocolo extraída de `constants/oauth.ts`, `services/oauth/*` y Pi (`packages/ai/src/auth/oauth/anthropic.ts`, inspección 2026-09-24), no una prueba de disponibilidad o autorización actual. La nueva referencia solicitada es `https://github.com/Icarus603/claude-code.git`; toda reevaluación debe registrar revisión y archivos inspeccionados, separar identidad de cliente de protocolo OAuth y mantener almacenamiento protegido y redacción de secretos.

| Hecho | Valor |
|---|---|
| console authorize | `https://platform.claude.com/oauth/authorize` |
| claude.ai authorize | `https://claude.com/cai/oauth/authorize` |
| Token (exchange / refresh) | `https://platform.claude.com/v1/oauth/token` |
| PKCE | S256; verifier `random(32)` base64url |
| Scopes (claude.ai) | `user:profile`, `user:inference`, `user:sessions:claude_code`, … |
| Storage | access, refresh, `expiresAt` (+5 min de margen), scopes, tier |
| Refresh | `grant_type=refresh_token`; re-pide los scopes originales en cada refresh |
| Transmisión | `authorization: Bearer` + beta `oauth-2025-04-20` |

El diseño por cuenta queda abierto a reevaluación, sin sustituir la revisión técnica por tests que vetan literales OAuth. Esta revisión de la documentación no modifica condiciones externas del proveedor ni acredita autenticación real. La lane delegada de ADR-0012 conserva su propio alcance; no es equivalente a un provider OAuth nativo.

#### 10.8 Fuentes y límites del port

| Hecho de comportamiento | Fuente |
|---|---|
| Request, cabeceras, `updateUsage`, fallback no-streaming | `services/api/claude.ts` (leak 2026-03-31) |
| Parser de stream (acumulación de JSON, TTFT) | `services/api/claude.ts` |
| Reintentos / backoff / 529 / `Retry-After` | `services/api/withRetry.ts` |
| Marcador de cache único + TTL latch | `claude.ts` (`addCacheBreakpoints`, `getCacheControl`) |
| Reglas `max_tokens` por modelo y 400 de contexto | `utils/context.ts` |
| Normalización y pareo de mensajes | `utils/messages.ts`, `utils/contentArray.ts` |
| OAuth de suscripción | `constants/oauth.ts`, `services/oauth/*` |
| Wire protocol vigente | Doc oficial: Messages API, streaming, prompt caching (2026-09) |

**No se portan:** betas 1P-only e internas; cabeceras de identidad de Claude Code (`claude-cli/…`, `x-anthropic-billing-header`); el prefijo de system "You are Claude Code…"; cache con scope org/global; attestation; casos de `cache_reference`/cache editing del microcompact; fast mode y task budgets internos (se usan sus equivalentes públicos: `output_config.effort`, `TaskBudget`). Los tests de contrato con respuestas grabadas protegen contra cambios del backend y validan los compat flags de esta sección.

## Plan por milestone

| Milestone | Alcance |
|---|---|
| M2 | `OpenAiChatCompatibleProvider`; `LocalModelHost` attach + managed; `ISecretProvider` + API key; timeouts y errores tipados; verificación en vivo de ik_llama |
| M3 | Reintentos, circuit breaker, cola por endpoint |
| M5 | `OpenAIResponsesProvider` (perfiles `api` y `codex`), **login con suscripción ChatGPT (§3.4)**, `AnthropicMessagesProvider` (§10), registro completo con alias, descubrimiento, costo/presupuesto, OAuth genérico |

### Modificación formal del alcance M2 (AC-2026-09-27, revisada 2026-09-29)

> **Revisión 2026-09-29:** la versión original de esta modificación decía que la plataforma era
> un "runtime JVM-based sobre .NET 10" sin DPAPI y aceptaba ofuscación XOR con la clave en otro
> archivo. Era incorrecto: el entorno es el SDK oficial de .NET 10 y `ProtectedData` funciona. La
> ofuscación XOR se retiró en la corrección de seguridad P0 (auditoría 2026-09-28).

**Decisión:** el `ICredentialStore` del milestone M2 es `FileCredentialStore`, en el directorio
de datos del usuario (nunca en el repo, ADR-0039 §2), con cada valor cifrado:

- **Windows:** DPAPI con ámbito `CurrentUser` (`System.Security.Cryptography.ProtectedData`).
- **Linux/macOS (provisional):** AES-256-GCM con una clave aleatoria de 32 bytes en
  `credentials.key`, creada con modo 0600. Protege frente a copiar el archivo de credenciales,
  no frente a otro proceso del mismo usuario.

Un valor que no se puede descifrar (manipulado, de otra máquina o del formato XOR antiguo) se
trata como ausente. Redacción PII en todos los sinks (ADR-0018 §4).

**Impacto:** el redactor de rutas de secretos (ADR-0018 §6) bloquea `.env`, `*.pem`, `*.key`,
`.ssh/`, credenciales de AWS y archivos de servicio en `filesystem.read`/`reference.resolve`,
evaluando el destino físico real de la ruta. `FileCredentialStore` ofrece
`Save/Load/Delete/Purge` revocables. Windows Credential Manager, Secret Service (libsecret) y
Keychain siguen pendientes; sustituyen al archivo cifrado cuando se integren.

## Consecuencias

- El Agent Runtime sigue viendo solo `IModelProvider` (INV-011); cambiar de ik_llama a un modelo frontera es cambiar un alias.
- OmniCore protege credenciales mejor que OmniCoder, al no depender de Pi.
- OmniCoder expone login OAuth de Anthropic a través de Pi (`PiProviderCatalog.cs`); es una referencia técnica que no acredita por sí sola disponibilidad o autorización para OmniCore.
- El provider Anthropic replica el wire protocol de Messages API (§10). El flujo por cuenta se reevalúa en §10.7 separadamente de la lane delegada (ADR-0012).
- Tests de contrato con respuestas grabadas cubren la evolución del backend de Anthropic y validan los compat flags de §10 (betas públicas, TTL de caché, campos de usage).
