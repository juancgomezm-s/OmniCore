# Validación: M5.5 Phase A Bug 3 — ContextPolicyHash

**Fecha:** 2026-10-01
**Commit base:** `d643162` (incluye M5.5 bug2 envelope generation y UTC bug1)
**Rama:** `codex/nemotron-m55-context-hash`

---

## Resumen del bug

**Problema:** Los callers de producción (`OmniCliRuntime` y `OmniServer`) establecían `ExecutionFingerprint.ContextPolicyHash` igual a `tokenCounter.Id.Value` (el id del tokenizer, p. ej. `"fake:words/1"`).

**Por qué está mal:** Según ADR-0017, el componente `context.policy` del fingerprint debe describir la **política efectiva de ensamblaje/presupuesto de contexto**, no la identidad del tokenizer. La identidad del tokenizer pertenece exclusivamente a `TokenizerHash`.

---

## Cambios realizados

### 1. `src/OmniCore.Host/OmniCliRuntime.cs`

- Añadido método privado estático `ComputeContextPolicyHash(ContextManagementPolicy, long usableContext)` que computa un hash SHA-256 determinista versión 1 sobre:
  - Prefijo de versión: `"ctx-policy-v1"`
  - `ExternalizeAboveCharacters`
  - `CompressBodyCharacters`
  - `RecentTailItems`
  - `CompactAfterItems`
  - `MaxCheckpointCharacters`
  - `usableContext` (budget de tokens)

- En la creación del `ExecutionFingerprint` (línea ~397):
  - **Antes:** `new ExecutionFingerprint(model, harnessHash, "core-tools-1", tokenCounter.Id.Value, "none", act ? "M3" : "M2", effectivePolicy.Fingerprint(), tokenCounter.Id.Value)`
  - **Después:** `new ExecutionFingerprint(model, harnessHash, "core-tools-1", contextPolicyHash, "none", act ? "M3" : "M2", effectivePolicy.Fingerprint(), tokenCounter.Id.Value)`
    - 4º parámetro (`ContextPolicyHash`) = `ComputeContextPolicyHash(harness.ContextManagement, usableContext)`
    - 8º parámetro (`TokenizerHash`) = `tokenCounter.Id.Value` (sin cambios, ahora en su posición correcta)

### 2. `src/OmniCore.Host/OmniServer.cs`

- Añadido método privado estático `ComputeContextPolicyHash(ContextManagementPolicy, long usableContext)` con la misma lógica que en `OmniCliRuntime`.

- En `MaterializeSnapshot()`:
  - **Antes:** `new ExecutionFingerprint("qwen38-27b-local", "harness-v1", "core-tools", "ctx-v1", "none", "M2")` (constructor de 6 params, `ContextPolicyHash` = `"ctx-v1"`)
  - **Después:** `new ExecutionFingerprint("qwen38-27b-local", harnessHash, "core-tools", contextPolicyHash, "none", "M2", "", counter.Id.Value)` (constructor de 8 params)
    - `contextPolicyHash` = `ComputeContextPolicyHash(ContextManagementPolicy.Default, 8192L)`
    - `TokenizerHash` = `counter.Id.Value` (`"fake:words/1"`)

### 3. Tests: `tests/OmniCore.Tests/ContextPolicyFingerprintTests.cs` (10 tests)

| Test | Qué verifica |
|------|--------------|
| `ComputeContextPolicyHash_SamePolicyAndBudget_ProducesSameHash` | Determinismo: misma política + budget → mismo hash |
| `ComputeContextPolicyHash_DifferentBudget_ProducesDifferentHash` | Cambio de budget → hash distinto |
| `ComputeContextPolicyHash_DifferentPolicy_ProducesDifferentHash` | Cambio de política (cualquier campo) → hash distinto |
| `ComputeContextPolicyHash_VersionPrefix_EnsuresDeterministicFormat` | Formato canónico incluye prefijo de versión `ctx-policy-v1` |
| `ExecutionFingerprint_TokenizerChange_ChangesOnlyTokenizerHash` | Cambio de tokenizer afecta solo a `TokenizerHash`, no a `ContextPolicyHash` |
| `ExecutionFingerprint_ContextPolicyChange_ChangesContextPolicyHashAndAggregate` | Cambio de política de contexto afecta a `ContextPolicyHash` y al hash agregado |
| `OmniCliRuntime_Fingerprint_UsesCorrectContextPolicyHash_NotTokenizerId` | El path de producción usa `ContextPolicyHash` correcto, no el tokenizer id |
| `OmniServer_MaterializeSnapshot_UsesCorrectContextPolicyHash` | El path de simulación usa `ContextPolicyHash` correcto |
| `BothCallers_ProduceCompatibleContextPolicyHashes_ForSameInputs` | Ambos callers producen hashes idénticos para mismos inputs |
| `BothCallers_UseCultureInvariantNumericSerialization` | La serialización canónica no depende de la cultura actual |

---

## Evidencia de limitaciones actuales

**No existe aún un objeto `ContextPolicy` unificado** que agrupe toda la configuración de contexto efectiva. Los inputs reales disponibles en los call sites son:

1. **En `OmniCliRuntime`:**
   - `harness.ContextManagement` (`ContextManagementPolicy` con 5 campos numéricos)
   - `usableContext` (long, derivado de `ModelDefinition.RecommendedUsableContext` o `ContextWindow`)

2. **En `OmniServer.MaterializeSnapshot`:**
   - `ContextManagementPolicy.Default` (hardcoded para simulación)
   - `8192L` (hardcoded para simulación)

**No se incluyen (porque no existen como objeto unificado accesible en estos call sites):**
- Contributors activos y sus versiones (ADR-0029)
- Políticas de memoria (ADR-0028)
- Overrides de contexto específicos del usuario
- Configuración de checkpoint/compaction más allá de `ContextManagementPolicy`

**Decisión tomada:** En lugar de inventar un objeto `ContextPolicy` que no existe, el hash se computa sobre los **inputs concretos y estables que sí están disponibles** en los call sites de producción. Esto evita "constantes que se hacen pasar por política" y deja trazada la limitación para cuando se implemente el componente `context.policy` completo (ADR-0017, M2/M5/M8).

El prefijo de versión `"ctx-policy-v1"` permite evolucionar el formato canónico sin romper compatibilidad hacia atrás.

---

## Verificación

```bash
# Build completo
dotnet build OmniCore.slnx
# ✓ Compilación correcta

# Tests específicos del bug 3
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --filter "FullyQualifiedName~ContextPolicyFingerprintTests"
# ✓ 10 tests passed

# Tests de arquitectura (grafo de dependencias)
dotnet test tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj
# ✓ Debe pasar sin cambios
```

---

## Compatibilidad

- **Serialización legacy:** El constructor `ExecutionFingerprint` de 6 parámetros sigue existiendo y delega al de 8 parámetros con `ModelPolicyHash=""` y `TokenizerHash=""`. Los fingerprints persistidos con el constructor viejo se deserializan correctamente (JSON constructor usa el de 8 params).
- **Constructores:** Se preservan todos los constructores existentes. El nuevo campo `TokenizerHash` tiene valor por defecto `""` en los constructores legacy.
- **Hash agregado:** `ExecutionFingerprint.Hash()` ahora incluye `TokenizerHash` y `ModelPolicyHash` en el canonical string, por lo que fingerprints antiguos (sin esos campos) tendrán hash distinto — esto es correcto porque el fingerprint ahora captura más información.

---

## Archivos modificados (owned)

1. `src/OmniCore.Host/OmniCliRuntime.cs` — caller de producción (CLI)
2. `src/OmniCore.Host/OmniServer.cs` — caller de producción (Server/simulación)
3. `tests/OmniCore.Tests/ContextPolicyFingerprintTests.cs` — tests nuevos
4. `docs/validation/nemotron-context-hash-20261001.md` — este documento

**No se modificaron:** Domain, ContextMaterializer, tests existentes, roadmap, ni archivos compartidos.
