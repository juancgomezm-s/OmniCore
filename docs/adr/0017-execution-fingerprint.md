# ADR-0017 — ExecutionFingerprint por Turn

- **Estado:** Aceptada (2026-09-24)
- **Reemplaza:** `ContextSnapshot.ModelDescriptorHash` (spec §29)
- **Spec:** §12, FR-TURN-005, §29

## Decisión

Cada Turn registra un `ExecutionFingerprint` que explica **qué configuración efectiva recibió**:

```csharp
public sealed record ExecutionFingerprint(
    FingerprintHash Aggregate,                  // SHA-256 sobre los componentes ordenados
    IReadOnlyList<FingerprintComponent> Components);

public sealed record FingerprintComponent(
    string Name,            // p. ej. "model.descriptor"
    string Version,         // versión lógica del componente
    ContentHash Hash,       // hash de su serialización canónica
    ArtifactRef? Content);  // contenido completo, deduplicado por hash (ADR-0001)
```

Componentes:

| Componente | Incluye |
|---|---|
| `model.descriptor` | `ModelDescriptor` + `ModelSelection` (esfuerzo, `ToolMode`, presupuesto) |
| `model.profile` | `EffectiveModelProfile` + `ModelQualificationKey` + revisión del perfil y del estado de cualificación (ADR-0007) |
| `model.harness` | `HarnessPolicy` resuelta |
| `provider.adapter` | familia, versión del adapter, compat flags y perfil (`api`, `codex`…) (ADR-0005) |
| `context.policy` | `ContextPolicy` y presupuesto |
| `tools.plan` | `ToolPlan`: ids, versiones y hashes de esquema de las tools visibles, mapeo de nombres visibles → `ToolId` y `Source` (ADR-0027) |
| `tools.preferences` | `toolPreferences` aplicadas (ADR-0027 §3) |
| `context.contributors` | contributors activos y sus versiones (ADR-0029) |
| `extensions.active` | ids, versiones, `TrustLevel` y capacidades concedidas de las extensiones que participaron (ADR-0023) |
| `memory.policy` | versión de la `MemoryPolicy` y de los stores consultados (ADR-0028) |
| `prompt.template` | id, versión y hash del system prompt o template renderizado |
| `agent.profile` | `AgentProfile` |
| `skills.active` | ids, versiones y hashes de contenido de las skills activas |
| `plan.revision` | `PlanId` + revisión (ADR-0016) |
| `overrides` | `UserOverrides` aplicados |
| `runtime.build` | versión y commit de OmniCore |

- **Dónde se guarda:** `TurnStarted` lleva el fingerprint con **todos los componentes** (hash + ref), no solo el agregado. El contenido completo de cada componente se guarda una sola vez gracias al CAS.
- **Qué permite:**
  - explicar un Turn (futuro `omni turn explain <id>`);
  - comparar dos Turns y ver qué componente cambió;
  - agrupar métricas de calibración (ADR-0007) por configuración efectiva.

### Estado de implementación (A12, 2026-10-09)

`omni explain` explica ya el último Turn del Run: además del estado del plan muestra el **contexto** materializado (presupuesto de tokens, y de qué contributor y tipo viene cada pieza, ADR-0029) y su **fingerprint** (modelo y cada componente con su versión y un prefijo de su hash). Los datos salen de las consultas `context` y `turnFingerprint` del servidor; esta última solo expone identidades y hashes, nunca el contenido de los componentes. `omni turn explain <id>` (un Turn concreto) y el diff de fingerprints siguen diferidos.

## Clasificación

| Elemento | Categoría |
|---|---|
| Tipo `ExecutionFingerprint` en `TurnStarted` (en M1 con componentes simulados) | **Necesario desde M1** |
| Componentes reales: M2 (modelo, tools, prompt, contexto, plan); M5 (perfil y cualificación); M8 (skills) | **Contract now / implementation later** |
| `omni turn explain`, diff de fingerprints | **Deferable** |
