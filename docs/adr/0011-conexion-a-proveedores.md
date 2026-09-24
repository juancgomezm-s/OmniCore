# ADR-0011 — Mecanismo de conexión a proveedores de modelos

- **Estado:** Aceptada — rev. 2 (2026-09-24)
- **Rev. 2:** se alinea con ADR-0005 rev. 2 (familias provider-native en lugar de "dialectos" sobre OpenAI-compatible) y con ADR-0018 (`ISecretProvider`).
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
| `AnthropicMessagesProvider` | Messages API nativa | Anthropic (prompt caching, thinking, firmas) |

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
- Se resuelve por scopes (spec §51): User (`~/.omnicore/`) → Project (`.omnicore/`) → overrides de Run/Task.

### 3. Credenciales y autenticación

1. Los secretos se leen solo mediante `ISecretProvider` (ADR-0018). Detrás está `ICredentialStore` (escritura: **Windows Credential Manager** o DPAPI), con variables de entorno y comandos como fuentes alternativas. Los secretos nunca aparecen en configuración, eventos, `ContextSnapshot`, artifacts ni logs; el redactor de ADR-0018 los elimina en cada sink.
2. `IAuthProvider` por proveedor con tipos `None | ApiKey | Bearer | OAuth`. La arquitectura soporta OAuth **desde el inicio** (refresh de tokens, expiración, revocación), para no tener que rediseñar cuando se habilite un flujo.
3. **Login por suscripción:**
   - **Anthropic (Claude Free/Pro/Max): no se implementa.** La página de cumplimiento legal de Anthropic ([code.claude.com/docs/en/legal-and-compliance](https://code.claude.com/docs/en/legal-and-compliance)) prohíbe usar tokens OAuth de esas cuentas en cualquier otro producto, herramienta o servicio, incluido el Agent SDK. Se hace cumplir desde abril de 2026. Anthropic se usa solo con API key.
   - **OpenAI (ChatGPT): requerimiento obligatorio, se implementa.** Ver §3.4.
   - **Regla general: no se reutilizan client IDs de otros productos ni se suplanta a otro producto.** Pi usa el client ID de Claude Code y el de VS Code Copilot, y **se hace pasar por Claude Code**: `user-agent: claude-cli/…`, remapeo de nombres de tools e inyecta "You are Claude Code…" en el system prompt. Nada de eso se porta. La única excepción es el flujo de ChatGPT de §3.4, porque OpenAI respalda públicamente que harnesses de terceros lo usen, y aun así OmniCore se identifica como OmniCore.
4. **Login con suscripción de ChatGPT (obligatorio).**
   - **Base de legitimidad:** Tibo Sottiaux (OpenAI, Codex) anunció que Pi se sumó a "la lista de agentes abiertos que soportan iniciar sesión con tu cuenta de ChatGPT y usar el mismo uso que obtienes en Codex" (traducido; [x.com/thsottiaux](https://x.com/thsottiaux/status/2012030806169121160)). El programa *Codex for Open Source* de OpenAI nombra a OpenCode, Cline y pi como herramientas que apoya. Son declaraciones públicas, **no contractuales**: OpenAI puede cambiar la política.
   - **Flujo** (el mismo que Pi y OpenCode, portado desde su código MIT):
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
- El modo managed porta de `LocalModelEngine`: puerto efímero asignado por el SO en loopback, `--api-key` aleatoria por proceso y Job Object.
- El modo managed añade: **detección de caída, reinicio con límite y backoff**, y los eventos `ProviderUnavailable` / `ProviderRestarted`.
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

Ambos son **MIT**:
- Pi (`github.com/earendil-works/pi`, autor Mario Zechner). Los paquetes instalados no traen el archivo LICENSE; hay que copiarlo del repo upstream.
- OpenCode ("Copyright (c) 2025 opencode", copia local en `repos/opencode-src`).

Se **portan a C#** (no se traducen línea a línea) conservando el aviso de copyright en `THIRD-PARTY-NOTICES.md` y un comentario de origen en cada archivo portado.

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
| Catálogo de modelos | models.dev (MIT, `models.dev/api.json`); esquema de referencia en OpenCode `core/src/models-dev.ts:66-129` | Completa `ModelDescriptor` (§2); lo declarado prevalece |

**No se portan:**
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
   - Los breakpoints se colocan según la familia: Anthropic admite hasta 4 `cache_control`, con TTL de 5 min o 1 h. OpenAI usa `prompt_cache_key`.
   - `TokenUsage` registra cache read/write por Turn. `/context` y `/stats` muestran la tasa de aciertos de caché.
   - Tests deterministas verifican que dos Turns consecutivos comparten el prefijo byte a byte.
3. **Enrutamiento por costo.**
   - El router (ADR-0007) manda a modelos locales todo lo que no requiere un modelo frontera: meta-tareas, exploración, verificación.
   - La escalación a frontera es explícita y lleva una causa registrada (spec §73).
   - Tareas no interactivas (resúmenes masivos, clasificación, evaluación de la regression suite) pueden ir a la **Message Batches API** de Anthropic, más barata a cambio de latencia. Se modela como un modo de ejecución del provider, no como otro provider.

## Plan por milestone

| Milestone | Alcance |
|---|---|
| M2 | `OpenAiChatCompatibleProvider`; `LocalModelHost` attach + managed; `ISecretProvider` + API key; timeouts y errores tipados; verificación en vivo de ik_llama |
| M3 | Reintentos, circuit breaker, cola por endpoint |
| M5 | `OpenAIResponsesProvider` (perfiles `api` y `codex`), **login con suscripción ChatGPT (§3.4)**, `AnthropicMessagesProvider`, registro completo con alias, descubrimiento, costo/presupuesto, OAuth genérico |

## Consecuencias

- El Agent Runtime sigue viendo solo `IModelProvider` (INV-011); cambiar de ik_llama a un modelo frontera es cambiar un alias.
- OmniCore protege credenciales mejor que OmniCoder, al no depender de Pi.
- OmniCoder expone hoy el login OAuth de Anthropic a través de Pi (`PiProviderCatalog.cs`), que además se hace pasar por Claude Code. Choca con la política citada en §3.3. Es un tema de OmniCoder, fuera del alcance de este repo.
- Todo archivo portado lleva un comentario de origen, y `THIRD-PARTY-NOTICES.md` mantiene los avisos MIT de Pi, OpenCode y models.dev.
