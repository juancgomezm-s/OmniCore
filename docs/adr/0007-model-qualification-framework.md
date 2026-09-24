# ADR-0007 — Model Qualification Framework y EffectiveModelProfile

- **Estado:** Aceptada — rev. 2 (2026-09-24)
- **Rev. 1:** cuatro categorías por número de parámetros (Small / Medium / Large / Frontier) gobernaban el tool calling. Queda **reemplazada**: el tamaño es solo una heurística inicial.
- **Spec:** §2, §17, §20, §21, §35, §37, §104, INV-010
- **Diagramas:** [arquitectura §11–§13](../architecture/arquitectura.md#11-effectivemodelprofile)

## Contexto

El número de parámetros no predice bien el comportamiento: influyen MoE, especialización, distillation, cuantización, fine-tuning, adapters, entrenamiento para tool calling y diferencias entre runtimes. Se conservan los aprendizajes de OmniCoder (rev. 1):

- Los parámetros configurados que no llegan al runtime son un bug; cada valor necesita un test que demuestre que tiene efecto.
- Para modelos pequeños, menos guía de tools puede ser más: una guía llevó a Qwen 4B de 100 % a 0 % de éxito.

## Decisión

### 1. Cuatro fuentes → `EffectiveModelProfile`

```text
DeclaredCapabilities + HeuristicDefaults + EmpiricalModelProfile + UserOverrides → EffectiveModelProfile
```

| Fuente | Contenido | Origen | Autoridad |
|---|---|---|---|
| `DeclaredCapabilities` | Context window, contexto usable, output máximo, modalidades, soporte de tools, structured output, reasoning/effort, tools en paralelo, `ToolMode`s soportados, visión | Registro de providers, catálogo (models.dev), `/props` | **Hechos**, que ninguna heurística contradice |
| `HeuristicDefaults` | Estimaciones conservadoras de traits a partir de parámetros, dense/MoE, contexto y familia | Tabla de configuración | Solo mientras no haya datos empíricos |
| `EmpiricalModelProfile` | Traits medidos (§2) | Model Qualification Suite + calibración | Reemplaza a las heurísticas para los traits que mide |
| `UserOverrides` | Correcciones de una configuración concreta, por ejemplo "esta cuantización falla con tools nativas" | Usuario o proyecto (Scope Resolver) | **Máxima**, siempre gana |

Resolución **por campo**:

- **Hechos** = `UserOverrides ?? Declared`.
- **Traits** = `UserOverrides ?? Empirical (si el estado es Qualified o superior) ?? Heuristic`.

Un override siempre queda registrado en el `ExecutionFingerprint` (ADR-0017).

**Nombre (revisión integral):** `EffectiveModelProfile` es **por modelo** (por `ModelQualificationKey`). La intersección **por Turn** que describe la spec §20 se llama `EffectiveExecutionProfile` (ver la tabla de propiedad de ADR-0005).

### 2. Traits empíricos

Cada trait se modela como `TraitScore { Value (0..1), Confidence, SampleSize, Source, MeasuredAt }`.

| Trait | Mínimo v1 | Qué mide |
|---|---|---|
| `ToolCallReliability` | ✓ | Tool calls válidas contra el esquema al primer intento |
| `StructuredOutputReliability` | ✓ | JSON válido contra el esquema pedido |
| `InstructionFollowing` | ✓ | Restricciones explícitas respetadas |
| `ContextReliability` | ✓ | Recuperación de hechos en contextos largos |
| `CodingCapability` | ✓ | Fixtures de código resueltas con tests verdes |
| `ToolErrorRecovery` | ✓ | Recuperación tras un error de tool |
| `ParallelToolReliability` | ✓ | Uso correcto de tools en paralelo |
| `TaskPacketAdherence` | | Mantenerse dentro de scope, recursos y contrato de salida |
| `CompletionDiscipline` | | No declarar "done" antes de tiempo; tasa de rechazo en Completion Gates |
| `FileSelectionAccuracy` | | Elegir los archivos relevantes |
| `ContextRetrievalAccuracy` | | Usar las `ArtifactRef` y el contexto externalizado correctamente |
| `MultiStepExecutionReliability` | | Encadenar pasos sin perder el hilo |
| `PlanTrackingReliability` | ✓ | Respetar el plan, identificar el siguiente paso, proponer revisiones coherentes y no repetir trabajo terminado (ADR-0016) |

### 3. Del perfil a la política del harness

`HarnessPolicyResolver` es una función **pura** y configurable, `EffectiveModelProfile → HarnessPolicy`:

| Campo de `HarnessPolicy` | Depende de |
|---|---|
| `ToolCallFormat` (`Native | Grammar | PromptedJson`) | `ToolCallReliability` + soporte declarado (gramática, tools nativas) |
| `ToolMode` y `MaxVisibleTools` | `ToolCallReliability`, contexto usable |
| `GuidanceLevel` (`Off | DomainOnly | Full`) | `InstructionFollowing`, contexto usable |
| `RepairAttempts` | `ToolErrorRecovery` |
| `PlanControl` (`RuntimeDriven | Assisted | ModelDriven`) | `PlanTrackingReliability` (ADR-0016 §8) |
| `StallThresholdTurns` | `MultiStepExecutionReliability` |
| `CompletionStrictness` | `CompletionDiscipline` |

- La tabla de umbrales es configuración.
- Las cuatro categorías de la rev. 1 sobreviven solo como filas de `HeuristicDefaults` (≤9B / 9–27B / ≥27B / frontier → traits provisionales), no como lógica del runtime.
- El pipeline de reparación de tool calls de la rev. 1 se mantiene:
  1. parse tolerante;
  2. validación de esquema;
  3. devolver el error concreto al modelo;
  4. agotado `RepairAttempts`, cambiar a formato `Grammar` o escalar.

### 4. Estados de cualificación

```text
Unknown → Declared → ProvisionallyClassified → Qualified → Calibrated
```

| Estado | Condición |
|---|---|
| `Unknown` | Solo se conoce el id |
| `Declared` | Hay metadata oficial del provider o del catálogo |
| `ProvisionallyClassified` | Se aplicaron `HeuristicDefaults` conservadores; el modelo **ya es usable** |
| `Qualified` | Pasó la suite reproducible para su `ModelQualificationKey` |
| `Calibrated` | `Qualified` + suficiente uso real (N muestras por trait) para ajustar los scores |

- Un cambio en la clave (§5) crea un perfil nuevo; no modifica el anterior.
- El perfil nuevo puede **heredar** de una clave hermana (mismo modelo, otro build) con la confianza reducida, y queda en estado `ProvisionallyClassified`.
- Una nueva versión mayor de la suite marca los perfiles `Qualified` como `Stale` hasta que se recualifican; mientras tanto siguen usándose con la confianza reducida.

### 5. `ModelQualificationKey`

La cualificación pertenece a una **configuración concreta**:

```csharp
public sealed record ModelQualificationKey(
    string ProviderId,
    string ModelId,
    string? ModelRevision,          // revisión del provider o hash del GGUF (sha256)
    string? Quantization,           // p. ej. Q4_K_M
    IReadOnlyList<string> Adapters, // LoRA, fine-tunes
    string? Backend,                // p. ej. ik_llama
    string? BackendBuild,           // p. ej. baac291, cuando influye
    string? ChatTemplateHash,       // en modelos locales la plantilla cambia el tool calling
    string AdapterProfile,          // familia y versión del adapter + compat flags (ADR-0005)
    ToolCallFormat ToolCallFormat,
    ToolMode ToolMode,
    string PromptProfileVersion);   // p. ej. prompt-profile-v3
// Clave canónica: SHA-256 de su serialización canónica.
```

Ejemplos:

- **Local:** `Qwen-X` + hash del GGUF + `Q4_K_M` + `ik_llama` build `baac291` + plantilla + `Direct` + `prompt-profile-v3`.
- **Cloud:** `Claude-X` + Anthropic + `AnthropicMessages` v1 + `Native` + `prompt-profile-v3`.

### 6. Almacenamiento y versionado

- **Ubicación:** los perfiles viven en el almacén de scope **User**, no por workspace, porque la cualificación es de la máquina y el modelo.
- **Registros:**
  - `model_profiles(key_hash, key_json, state, profile_revision, suite_id, suite_version, created_at)`
  - `model_traits(key_hash, profile_revision, trait, value, confidence, samples, source)`
- **Resultados completos** (probes, respuestas, puntajes) como artifacts content-addressed, para auditoría.
- **`BenchmarkIdentity`:** `suite_id`, `suite_version`, hash del conjunto de tareas, semilla, temperatura y versión de OmniCore.

### 7. Model Qualification Suite

- **Perfiles:**
  - `quick`: 10–20 probes deterministas, sin escritura en repos reales y con tope de costo.
  - `full`: batería con fixtures de código en sandbox (worktree desechable).
- **Evaluación determinista:** esquemas, tests y comparaciones exactas. Nunca se usa un LLM como juez en los traits mínimos.
- **UX futura:**
  - `omni model inspect <model>`
  - `omni model qualify <model> --quick | --full`
- **Nunca se ejecuta sola al descubrir un modelo.** En providers de pago exige consentimiento explícito y un presupuesto máximo.
- **Calibración:** la alimentan contadores del runtime por clave:
  - fallos de parseo de tool calls;
  - éxito de reparaciones;
  - `ProgressStalled`;
  - rechazos en Completion Gates.
- **Separación de responsabilidades:** agregar la suite o traits no modifica el Router, el Agent Runtime ni el Task runtime. El Router consume solo `EffectiveModelProfile`; no ejecuta benchmarks, no contiene lógica de cualificación y no infiere calidad por nombre.

## Clasificación

| Elemento | Categoría |
|---|---|
| Tipo `EffectiveModelProfile` y los campos de `HarnessPolicy` que consume el runtime | **Necesario antes de M2**; en M1 solo el tipo |
| `DeclaredCapabilities` + `HeuristicDefaults` + `UserOverrides` y el resolver puro (M2); `ModelQualificationKey`, estados y store (contract en M2, uso en M5) | **Contract now / implementation later** |
| Runner de la suite `quick` (M5), suite `full` y calibración (M10+) | **Contract now / implementation later** |
| Traits no mínimos | **Deferable** (el enum es extensible) |
