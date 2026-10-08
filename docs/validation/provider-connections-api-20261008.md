# Validación — bloque 1: backend de conexión API de Anthropic (2026-10-08)

## Alcance

Solo el backend de conexión por API key de Anthropic (ADR-0011 §3, ADR-0018, ADR-0039 §4,
ADR-0046): guardar/reemplazar/probar/desconectar la credencial y descubrir modelos actuales
contra la Models API pública (`GET /v1/models` con paginación `after_id`, consultado
2026-10-08). Sin TUI, sin cuentas/suscripciones/OAuth, sin scheduler/M6, sin invocación de
inferencia ni fallback. Los bloques 2 (cuentas) y 3 (menú) siguen por separado.

## Archivos entregados

| Archivo | Cambio |
|---|---|
| `src/OmniCore.Host/ProviderConnections.cs` | Reescrito (parcial previo reemplazado): `ProviderConnectionService`, `AnthropicConnectionValidator`, `AnthropicModelCatalog`, `ProviderConnectionMetadataStore`, `ProviderConnectionRegistration` y tipos de estado. Sin localizador de CLI ni fila de cuenta de Claude (fuera de este bloque). |
| `src/OmniCore.Models/Registry.cs` | Accessor de solo lectura `ModelRegistry.Providers()` (era necesario para listar; sin cambios de semántica). |
| `src/OmniCore.Client/Localization.cs` | Claves `providers.error.*` / `providers.notice.*` en es/en. |
| `tests/OmniCore.Tests/ProviderConnectionApiTests.cs` | 22 tests enfocados (ver abajo). |

## Operaciones soportadas (backend, `ProviderConnectionService`)

- **List** — estado de conexiones: `NotConfigured` / `Unknown` (no medido) / `Connected` /
  `Invalid` / `Unreachable`; `Expired` existe en el enum pero una API key nunca se reporta
  expirada (solo credenciales con expiración propia, bloque de cuentas). Los providers con
  perfil `codex` se muestran como `Subscription`/`Unknown` hasta que el bloque de cuentas los
  mida. Tener la key guardada **no** implica Connected: sin medición, `Unknown`.
- **ConnectAnthropicAsync(key, validate)** — registra el provider Anthropic en
  `providers.yaml` User (id respetando los existentes, `billingMode: MeteredCurrency`),
  guarda/reemplaza la key por `ICredentialStore` cifrado (`FileCredentialStore`, DPAPI/AES-GCM)
  y metadatos sin secretos (`(data)/provider-connections.json`, solo key enmascarada y marcas
  de tiempo). Con `validate`, la Models API debe aceptar la key (2xx) antes de guardar; 401/403
  → error tipado `invalid` sin guardar nada. La key se registra en `SecretRedactorRegistry`
  antes de cualquier uso (ADR-0018).
- **TestAsync(providerId)** — prueba la credencial guardada contra `GET /v1/models?limit=1`
  del baseUrl del provider; actualiza metadatos (`valid`/`invalid`/`unreachable`).
- **DiscoverAnthropicModelsAsync(providerId)** — lista modelos con paginación acotada
  (límite 1000/página, máx. 20 páginas, cuerpo acotado 4 MiB) y registra en `models.yaml`
  User los modelos nuevos, preservando los ya registrados del usuario. **Lo que la Models API
  no informa queda desconocido**: no se escriben `context`/`maxOutput` inventados (el loader
  aplica su default conservador preexistente; no se afirman como medida). La cuota solo se lee
  de cabeceras reales `anthropic-ratelimit-*` vía `RateLimitQuotaParser` existente; sin
  cabeceras, vacío. Si el provider afirma más páginas al llegar al tope, `Truncated: true`.
- **Disconnect(providerId)** — operación local: borra la entrada de ese provider en el
  credential store y sus metadatos; preserva credenciales de otros providers y la
  configuración de modelos.

## Seguridad

- La credencial viaja solo en `x-api-key` al endpoint validado (URL absoluta http(s), sin
  `user:pass`, del `providers.yaml` User); redirecciones **desactivadas**
  (`AllowAutoRedirect = false`): nunca se reenvía la credencial a otro host.
- TLS con validación estándar del sistema (sin overrides); `User-Agent: omnicore/<versión>`
  — nunca `claude-cli/…` ni betas internas (ADR-0011 §3.3/§10.8).
- Cuerpos de error nunca se usan como detalle (solo código HTTP); sin key en claro en YAML,
  metadatos, errores, logs ni artifacts (verificado por test escaneando todos los archivos del
  directorio temporal).
- Configuración protegida: backup + escritura atómica + validación previa con `ConfigLoader` +
  detección de cambio concurrente (patrón `ModelCatalogRegistration`).
- Sin cambios de `BillingMode` semánticos ni consentimiento: `MeteredCurrency` declarado no
  entra en routing automático (ADR-0046) y este backend no invoca inferencia.

## Comandos y resultados (Windows, SDK .NET 10.0.401)

- `dotnet build OmniCore.slnx` → exit 0, 0 warnings/0 errors.
- `dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --filter "FullyQualifiedName~ProviderConnectionApiTests"`
  → exit 0; **22/22 correctos, 0 fallos**.
- `dotnet test --solution OmniCore.slnx` → exit 0; **3051 total, 3047 correctos, 0 fallos,
  4 omitidos** (symlinks, restricción del entorno). Nota: en una corrida anterior del solution
  falló 1 test de forma transitoria; dos corridas completas consecutivas con esta entrega
  dieron 0 fallos. Logs: `C:/Users/juanc/.codex/pi-provider-menu-glm-split-20261008/results/`.

## Cobertura de tests (22)

1. Estado sintético Anthropic antes de configurar (NotConfigured, solo conectar).
2. Estados `Unknown`/`Invalid`/`Unreachable`/`Connected` derivados de medición y metadatos; key
   presente ≠ Connected.
3. Connect con 2xx: guarda key cifrada, registra provider, metadatos `valid`, máscara, y
   ningún archivo en disco contiene la key en claro; una sola petición con `x-api-key` solo al
   endpoint declarado y `User-Agent: omnicore/…`.
4. Connect con 401: error tipado `invalid`, nada guardado, mensaje sin la key.
5. Key vacía/demasiado corta: tipado, sin guardado ni red.
6. Reemplazo de key: store actualizado, sin rastro de la anterior en disco.
7. Endpoint con `user:pass`: rechazado antes de ninguna petición.
8. 429 y fallo de red en test: sin reclamar válido; metadatos honestos.
9. Test sin credencial: tipado `notConfigured`.
10. Cancelación: propaga, no muta metadatos.
11. Descubrimiento con paginación: `after_id` correcto, cuota real de cabeceras, modelos
    registrados consumibles, sin `context`/`maxOutput` inventados.
12. Límites/capacidades no informadas quedan desconocidas (`null`, sin `reasoning:`).
13. Tope de páginas con `Truncated`.
14. Payload fuera de contrato (id inválido): tipado `catalogInvalid`, config intacta.
15. Descubrimiento sin credencial: tipado.
16. Redirecciones no seguidas (test y catálogo), credencial sin fuga.
17. Disconnect: borra solo la credencial del provider; otras credenciales intactas.
18. Disconnect de provider desconocido: tipado.
19. Registro preserva providers/modelos existentes del usuario.
20. Descubrimiento repetido no duplica modelos.
21. El registro cargado alimenta el adapter nativo `AnthropicMessagesProvider` con respuesta
    SSE de fixture (mensajes/cabeceras correctos).
22. Claves es/en resuelven texto real.

## Limitaciones de los fixtures

- Handlers HTTP en memoria (no loopback TCP real); no cubren TLS real, timeouts de socket ni
  proxies.
- No hay llamada externa autenticada ni uso de la Messages API de inferencia; la Models API se
  fixturea según el contrato documentado (sin límites de contexto en la respuesta: por eso el
  registro no escribe medidas).
- La sesión de suscripción (ChatGPT) y la elegibilidad del CLI de Claude no se tocan aquí.

## Bloqueos restantes

- Bloque 2: cuentas/suscripciones (ChatGPT sesión, fila `Subscription` real; `Expired`).
- Bloque 3: menú TUI sobre `ProviderConnectionService` y entrada secreta de la key por
  consola/TUI (el backend ya acepta la key como parámetro y la redacta al instante).
- `Expired` queda sin productor hasta el bloque de cuentas (decisión deliberada: no se inventa
  expiración para API keys).
