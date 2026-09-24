# ADR-0011 — Mecanismo de conexión a proveedores de modelos

- **Estado:** Aceptada (2026-09-24), salvo §3.3 (Propuesta, pendiente de decisión)
- **Amplía:** ADR-0005
- **Spec:** §17–§22, §70–§73, INV-010, INV-011

## Contexto

OmniCoder delega casi toda la conexión a Pi (`@earendil-works/pi-coding-agent`): 38 proveedores, login por suscripción vía el `/login` de Pi, streaming, reintentos y catálogo (`get_available_models`). El único camino C# directo es el local (`LocalModelEngine`, `LocalAssistantGateway`). OmniCore no puede depender de Pi, así que debe asumir todo eso.

Observaciones de OmniCoder que motivan decisiones:

- Pi solo acepta tres dialectos (`openai-completions`, `openai-responses`, `anthropic-messages`) y con ellos cubre sus proveedores.
- Las credenciales quedan en texto plano en `~/.pi/agent/auth.json` porque Pi no lee almacenes cifrados.
- `LocalModelEngine` no maneja caídas del servidor (sin handler de `Exited` ni reinicio) y usa `GET /v1/models` como readiness.
- No hay manejo de 429 / `Retry-After` / backoff en el código propio.

## Decisión

### 1. Adaptadores por dialecto, no por proveedor

| Dialecto | Cubre |
|---|---|
| `OpenAiChatCompletions` | ik_llama / llama.cpp, OpenRouter, DeepSeek, Groq, xAI, Mistral, Together, Fireworks, Cerebras… |
| `OpenAiResponses` | modelos recientes de OpenAI |
| `AnthropicMessages` | Anthropic nativo (prompt caching, thinking) |

- Cada dialecto implementa `IModelProvider` con un cliente HTTP propio y delgado (`HttpClient` + System.Text.Json con source generation). No se usan SDKs oficiales: añaden peso y no exponen las extensiones de llama.cpp.
- Diferencias entre endpoints "compatibles" se declaran como **capacidades/quirks** en el `ModelDescriptor` / `ProviderDescriptor`, nunca con `if (provider == ...)`.
- Extensiones llama.cpp (gramática GBNF, `json_schema`, `/tokenize`, `/props`, slots) se activan por capacidad declarada.

### 2. Registro declarativo de proveedores, modelos y alias

```yaml
providers:
  local:      { dialect: OpenAiChatCompletions, baseUrl: auto, auth: none, host: managed }
  openrouter: { dialect: OpenAiChatCompletions, baseUrl: https://openrouter.ai/api/v1, auth: { kind: apiKey, ref: openrouter } }
  anthropic:  { dialect: AnthropicMessages, auth: { kind: apiKey, ref: anthropic } }
models:
  qwen-27b: { provider: local, params: 27B, context: 65536, toolFormats: [Native, Grammar] }
  sonnet:   { provider: anthropic, class: frontier, context: 200000, cost: { inPerMTok: 3, outPerMTok: 15 } }
aliases:
  local-worker:    qwen-27b
  frontier-strong: sonnet
```

- El router trabaja con **alias y categorías** (ADR-0007), nunca con ids de modelo. Nombres como Luna o Sol viven aquí (spec §21).
- Descubrimiento (`/v1/models`, `/props`, catálogos) **completa** el descriptor; lo declarado prevalece.
- Se resuelve por scopes (spec §51): User (`~/.omnicore/`) → Project (`.omnicore/`) → overrides de Run/Task.

### 3. Credenciales y autenticación

1. `ICredentialStore` con implementación en **Windows Credential Manager** (o DPAPI) y variables de entorno como alternativa. Los secretos nunca aparecen en configuración, eventos, `ContextSnapshot` ni logs; un redactor los elimina de mensajes de error.
2. `IAuthProvider` por proveedor con tipos `None | ApiKey | Bearer | OAuth`. La arquitectura soporta OAuth **desde el inicio** (refresh de tokens, expiración, revocación), para no tener que rediseñar cuando se habilite un flujo.
3. **Login por suscripción:**
   - **Anthropic (Claude Free/Pro/Max): no se implementa.** La página de cumplimiento legal de Anthropic ([code.claude.com/docs/en/legal-and-compliance](https://code.claude.com/docs/en/legal-and-compliance)) prohíbe usar tokens OAuth de esas cuentas en cualquier otro producto, herramienta o servicio, incluido el Agent SDK. Se hace cumplir desde abril de 2026. Anthropic se usa solo con API key.
   - **OpenAI (ChatGPT Plus/Pro): Propuesta, pendiente.** OpenAI lo ha tolerado públicamente, pero no hay un flujo OAuth documentado para terceros. Los harnesses que reutilizan el client OAuth de Codex han sido cortados en ocasiones. Si se habilita, será un `IAuthProvider` aislado, desactivable y marcado como no oficial.
   - **No se reutilizan client IDs de otros productos.** Pi y OpenCode usan el client ID de Codex CLI (`app_EMoamEEZ73f0CkXaXp7hrann`). Pi además usa el de Claude Code y el de VS Code Copilot, y **se hace pasar por Claude Code**: `user-agent: claude-cli/…`, remapeo de nombres de tools e inyecta "You are Claude Code…" en el system prompt. OmniCore solo usará flujos OAuth documentados o apps OAuth registradas a su nombre, identificándose como OmniCore.
5. Resolución de secretos por comando (idea de Pi, `resolve-config-value`): `auth.ref` puede apuntar a un gestor de contraseñas (`cmd:op read …`). El comando lo define el usuario en su configuración de scope User, nunca un proyecto ni el modelo.
4. Validación de endpoints portada de `ProviderEndpointValidator` de OmniCoder: URL absoluta http(s), sin `user:pass`, redirecciones sin downgrade HTTPS→HTTP ni cambio de host.

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
| Normalización entre dialectos (replay de thinking, IDs de tool call, degradación de imágenes) | Pi `api/transform-messages.js` | Lógica pura |
| Implementaciones de dialecto como referencia | Pi `api/*.js`, código async simple; OpenCode `llm/src/protocols/*.ts`, diseño Protocol/Route | Guían los tres adaptadores de §1 |
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
   - Los breakpoints se colocan según el dialecto: Anthropic admite hasta 4 `cache_control`, con TTL de 5 min o 1 h. OpenAI usa `prompt_cache_key`.
   - `TokenUsage` registra cache read/write por Turn. `/context` y `/stats` muestran la tasa de aciertos de caché.
   - Tests deterministas verifican que dos Turns consecutivos comparten el prefijo byte a byte.
3. **Enrutamiento por costo.**
   - El router (ADR-0007) manda a modelos locales todo lo que no requiere un modelo frontera: meta-tareas, exploración, verificación.
   - La escalación a frontera es explícita y lleva una causa registrada (spec §73).
   - Tareas no interactivas (resúmenes masivos, clasificación, evaluación de la regression suite) pueden ir a la **Message Batches API** de Anthropic, más barata a cambio de latencia. Se modela como un modo de ejecución del provider, no como otro provider.

## Plan por milestone

| Milestone | Alcance |
|---|---|
| M2 | `OpenAiChatCompletions`; `LocalModelHost` attach + managed; `ICredentialStore` + API key; timeouts y errores tipados; verificación en vivo de ik_llama |
| M3 | Reintentos, circuit breaker, cola por endpoint |
| M5 | `OpenAiResponses`, `AnthropicMessages`, registro completo con alias, descubrimiento, costo/presupuesto, OAuth genérico |

## Consecuencias

- El Agent Runtime sigue viendo solo `IModelProvider` (INV-011); cambiar de ik_llama a un modelo frontera es cambiar un alias.
- OmniCore protege credenciales mejor que OmniCoder, al no depender de Pi.
- OmniCoder expone hoy el login OAuth de Anthropic a través de Pi (`PiProviderCatalog.cs`), que además se hace pasar por Claude Code. Choca con la política citada en §3.3. Es un tema de OmniCoder, fuera del alcance de este repo.
- Todo archivo portado lleva un comentario de origen, y `THIRD-PARTY-NOTICES.md` mantiene los avisos MIT de Pi, OpenCode y models.dev.
