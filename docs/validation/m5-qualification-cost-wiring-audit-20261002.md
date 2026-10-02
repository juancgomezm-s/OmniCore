# Auditoría M5 — cableado de costo de la cualificación (`max_tokens` + estimación/tope reales)

> **Estado: auditoría interina de una corrección concreta, NO cierre de M5.** Documento hermano de
> `docs/validation/m5-closure-20261001.md`. No cambia el veredicto de aquel: M5 sigue abierto.
>
> **Fecha:** 2026-10-02 · **Rama:** `codex/m5-nvidia-closure-20261001` · **Base:** `be8c9e1` ·
> **Toolchain:** .NET SDK 10.0.401 ·
> **Objetivo declarado:** NVIDIA `deepseek-ai/deepseek-v4.1-flash`.

Esta corrección arregla un defecto **de seguridad de gasto** de la cualificación (M5): las peticiones
capturadas no llevaban límite de salida y el costo por probe quedaba en cero incluso para providers
remotos de pago. Se arregla el cableado; **no** se ejecutó ninguna llamada real nueva y **no** se declara
M5 cerrado.

---

## 0. Resumen ejecutivo

| Defecto | Antes | Después | Evidencia |
|---|---|---|---|
| Los requests de cualificación no enviaban límite de salida | Sin `max_tokens` / `max_output_tokens` | `ModelRequest.MaxOutputTokens` se traduce al campo nativo de cada familia | 8 tests deterministas (Chat, Anthropic, Responses, runner) |
| `ProbeResult.CostUsd` y el tope eran cero incluso en remoto pago | `decimal`, siempre `0` | `decimal?`; `0` solo si es gratis explícito, `null` si es desconocido | 21 tests nuevos; fail-closed sin requests |
| Estimación previa al consentimiento para 3 probes pagos | `0` | Conservadora y **no nula** con precios declarados | `Host_preview_is_nonzero_…`; CLI muestra coste ≠ 0 |
| Pricing desconocido | Se asumía gratis | **Falla en cerrado**: sin request, sin persistir nada | `Host_fails_closed_…`, CLI exit 2 |

- **Tests:** `dotnet test --solution OmniCore.slnx` → **1157 total · 1153 correctos · 4 omitidos ·
  0 errores** (los 4 omitidos son los de siempre: el entorno no permite symlinks). Se añaden **21 tests**
  nuevos en `QualificationCostWiringTests.cs`; la suite previa (1136) sigue verde.
- **Peticiones reales consumidas en esta corrección: 0.** El presupuesto real declarado en la auditoría
  previa dejaba **1** petición, insuficiente para una suite `quick` de 3 probes; **no se consumió**.
- **No se desactivó el SpendGuard ni se ocultó el caso de histórico incompleto** (`uso histórico
  incompleto`): ese comportamiento queda intacto y es el pendiente M5.5/B1, no de esta corrección.

---

## 1. El defecto (evidencia)

1. **Sin límite de salida.** `ProbeRunner.ToModelRequest` construía `ModelRequest` sin ningún tope de
   salida; `ModelRequest` no tenía siquiera el campo. Un probe podía pedir al modelo una salida tan larga
   como el provider permitiera, gastando sin cota.
2. **Costo falso.** `ProbeResult.CostUsd` era `decimal` y el runner lo dejaba en `0`; `ProbeRunner` no
   leía el `TokenUsage` de la respuesta. `QuickProbeSuite`/CLI presentaban `0.0000 USD` para providers
   remotos de pago, y `ModelQualificationHost` estimaba la suite como la suma de `Probe.MaxCostUsd`
   (siempre `0` en `quick`). Con tope `--max-cost 0.01` la preflight "pasaba" sin medir nada.

Reproducción del síntoma original: cualquier captura del body de un probe (fixture HTTP) no contiene
`max_tokens`, y el resultado de la suite muestra costo `0` aunque el provider haya facturado.

---

## 2. Diseño del arreglo (acotado, sin rediseño de M5.5)

### 2.1 Tope explícito de salida en el contrato de provider

- `ModelRequest.MaxOutputTokens` (`long?`, parámetro final opcional) — retrocompatible: los 6 call-sites
  existentes siguen compilando y pasan `null`, así que el runtime conserva su semántica previa (decide el
  provider/servidor).
- Traducción nativa en cada familia:
  - `OpenAiChatCompatibleProvider` → `max_tokens` (DTO `MaxTokens`); se omite si no hay tope.
  - `AnthropicMessagesProvider` → `max_tokens`; el tope explícito **gana** sobre
    `DefaultMaxOutputTokens`/presupuesto de razonamiento.
  - `OpenAIResponsesProvider` → `max_output_tokens`; gana sobre la opción del adapter.

### 2.2 Política de costo propia de Qualification (frontera ADR-0009)

`QualificationCostPolicy` + `QualificationPricing` viven en `OmniCore.Qualification`, que solo puede
depender de `Abstractions` y `Domain`. No puede referenciar `ModelPricing` (vive en Host), así que copia
su *shape* (`InputPricePerMillionUsd`, `OutputPricePerMillionUsd`, `IsComplete`, `CostUsd(TokenUsage)`).
El Host convierte `ModelPricing` → `QualificationPricing`. **No** se movió `ModelPricing` ni se tocó el
router/`BillingMode`.

Tres clases de costo, todas explícitas:

- `LocalZeroCost`: provider local o fake inyectado → costo `0m` explícito (nunca "se asume").
- `DeclaredPricing`: precios de entrada/salida completos → se estima y se mide.
- `UnknownPaidPricing`: provider remoto con credencial (`AuthKind.ApiKey` y base URL no privada) sin
  precios completos → **fail-closed**.

Estimación conservadora por probe: `ceil(len(prompt)/3) + 64` tokens de entrada (caracteres/3, más
conservador que caracteres/4 del runtime) por precio de entrada + `MaxOutputTokens` por precio de salida,
tomando el máximo con el `Probe.MaxCostUsd` declarado para no relajar topes ya existentes. El tope de
salida es `model.MaxOutputTokens > 0 ? … : 4096`.

### 2.3 Costo real y fail-closed en el runner

- `ProbeResult.CostUsd` pasa a `decimal?`: `null` = desconocido (**nunca** gratis), `0m` = gratis
  explícito.
- `RunSuiteAsync`:
  1. rechaza `UnknownPaidPricing` **antes de cualquier request**;
  2. compara la estimación conservadora con `MaxTotalCostUsd` y rechaza sin requests si no alcanza;
  3. acumula el costo real medido y re-verifica el tope agregado;
  4. tras un `Error`/`Timeout` **detiene** la suite y marca el resto `NotRun` (no se gasta más tras fallar);
     la cancelación también marca `NotRun`.
- El uso reportado (`UsageUpdated` o la respuesta final) se **retiene** aunque el probe falle o expire. Con
  pricing declarado y uso incompleto (tokens de entrada y salida en 0), el probe queda en `Error` con
  `CostUsd = null` ("uso incompleto"): un cobro no evidenciado no se reporta como gratis.
- `RunProbeAsync` lanza `ProbeTimeoutException` en timeout: **nunca** produce un perfil exitoso falso.

### 2.4 Host y CLI

- `ModelQualificationHost.PreviewCost(modelId, suite)` devuelve `ModelQualificationCostPreview`
  (`IsUnknownPaidPricing`, `EstimatedUsd` nullable, `MaxOutputTokens`). `Create` ahora carga la
  configuración completa (`OmniHost.LoadUserConfiguration`) cuando no hay `registryOverride`, para poder
  leer los precios.
- `ModelQualificationHost.QualifyAsync` clasifica igual y lanza
  `ModelQualificationUnknownPricingException` antes de construir el provider.
- El CLI usa `PreviewCost` para el aviso de consentimiento y **falla en cerrado** (exit 2, mensaje, sin
  ejecutar) si el pricing es desconocido. El costo nullable se imprime como `—` cuando es desconocido.
  Recursos nuevos/ajustados (es/en): `cli.model.qualify.unknown_pricing`, `cli.model.qualify.probe.line`.
- La recomendación se sigue calculando y mostrando, pero **jamás** aplica política operativa (lo fija
  `Qualify_recommends_a_category_but_never_changes_the_operational_policy`, ya existente).

---

## 3. Tests nuevos (`tests/OmniCore.Tests/QualificationCostWiringTests.cs`, 21)

| Área | Cobertura |
|---|---|
| Estimación | 3 probes pagos → estimado no nulo e igual a la suma; input tokens conservador; `UnknownPaid` sin estimado; local exactamente `0` |
| Fail-closed | `UnknownPaid` sin llamar al provider; tope por debajo del estimado sin ningún request |
| Costo real | probes completados retienen el costo real del uso; pricing declarado con uso vacío → `Error`/`null`, nunca gratis |
| Local | costo `0m` explícito y ejecuta aun con tope `0` |
| `max_tokens` | el request lleva el tope; cuerpo Chat con `"max_tokens":777` y omitido sin tope; Anthropic y Responses prefieren el tope explícito |
| Timeout/cancelación | timeout → `Timeout` con uso parcial retenido y sin `Output`, resto `NotRun`; `RunProbeAsync` lanza `ProbeTimeoutException`; cancelación previa sin requests |
| Host/CLI | `PreviewCost` no nulo con precios declarados y cualificación que retiene coste real sin tocar política; fail-closed end-to-end; proveedor local en `0`; aviso CLI con estimado ≠ 0; CLI exit 2 con pricing desconocido |

---

## 4. Pendientes honestos (no se declaran cerrados)

1. **Cualificación real NVIDIA no ejecutada.** El presupuesto real previo dejaba **1** petición; la suite
   `quick` consume **3**. No se consumió la restante. La ruta CLI→Host→store→perfil queda verificada solo
   de forma determinista.
2. **No es M5 closure.** Responsive/Anthropic reales, login OAuth real y la suite real siguen pendientes,
   como en `m5-closure-20261001.md` §8.
3. **`ModelPricing` sigue en Host** y se replica en Qualification por la frontera ADR-0009. Unificarlo es
   una decisión de M5.5, no de esta corrección.
4. **Histórico de costo incompleto** (`uso histórico incompleto`) del runtime no se toca: sigue siendo el
   candidato a bug M5.5/B1. No se desactivó el SpendGuard para "pasar" ninguna prueba.
5. **La status line de sesión no cambia** en esta corrección; esto es solo el costo de la cualificación.

---

## 5. Reproducción

```bash
dotnet build OmniCore.slnx
dotnet test --solution OmniCore.slnx                                  # 1157 total, 1153 ok, 4 omitidos
dotnet test tests/OmniCore.Tests -- --filter "FullyQualifiedName~QualificationCostWiringTests"
dotnet test tests/OmniCore.Tests -- --filter "FullyQualifiedName~AnthropicMessagesProviderTests"
dotnet test tests/OmniCore.Tests -- --filter "FullyQualifiedName~OpenAIResponsesProviderTests"
dotnet test tests/OmniCore.Tests -- --filter "FullyQualifiedName~ModelProviderResilienceTests"
```

Verificación determinista del fail-closed sin gasto real: `QualificationCostWiringTests` no abre red
(usa un `HttpMessageHandler` de captura y providers scripted); no toca datos del usuario (directorios
temporales propios).

---

## 6. Conclusión

La corrección cierra el defecto de gasto de la cualificación **en su parte determinista**: los requests
llevan un tope de salida explícito, la preflight es conservadora y no nula para pricing pago declarado,
nada se ejecuta cuando el tope no alcanza o el precio es desconocido, el uso real se retiene y un uso
incompleto nunca se reporta como gratis. **M5 sigue abierto**: no se ejecutó la suite real (presupuesto
insuficiente) y los pendientes de providers/OAuth siguen vigentes.
