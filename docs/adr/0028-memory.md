# ADR-0028 — Memory como servicio externo al Core; separación Memory / Knowledge / Skills

- **Estado:** Aceptada — rev. 2 (2026-09-24). Contratos ahora; implementación a partir de M8.
- **Rev. 2** (decisión del usuario en la revisión integral): **toda la memoria entra en v1**: Session en M8; Project, Workspace y Global en M10. La spec §3 se corrige en consecuencia.
- **Confianza** (revisión integral):
  - en un workspace no confiable no se lee ni se promueve memoria de proyecto (ADR-0039 §3);
  - el ranking pondera por origen: `UserStated` > `Promoted` con evidencia > `Observed` > origen `Model` (el de menor peso);
  - la memoria nunca se presenta al modelo como instrucción, sino como dato con procedencia.
- **Spec:** §54 (Knowledge, RAG y Memory)
- **Relacionado:** ADR-0016 (WorkingState), ADR-0018 (secretos), ADR-0022 (scopes), ADR-0026 (skills), ADR-0029 (procedencia)
- **Diagrama:** [arquitectura §30](../architecture/arquitectura.md#30-memory)

## Decisión

### 1. Memory no es parte del núcleo determinista

- Memory vive en servicios detrás de contratos de Abstractions. El futuro `OmniCore.Memory` (ADR-0009 §2.5) llega al contexto **solo** como `IContextContributor`.
- El Engine, el `PlanService` y los Completion Gates **nunca** dependen de la memoria para decidir estado. El runtime funciona igual con la memoria deshabilitada.

### 2. Working Context ≠ Memory

| Concepto | Qué es | Durabilidad | Dueño |
|---|---|---|---|
| **Working Context** | Estado operativo actual: plan y PlanItem actuales, Task actual, blockers, trabajo pendiente y observaciones recientes relevantes | Ninguna propia: es una proyección (ADR-0016 §7) | Context Engine |
| Session Memory | Lo aprendido durante una Session | La Session | Memory service |
| Project Memory | Lo aprendido sobre un proyecto (`ProjectId`), reutilizable entre Sessions y clones | Indefinida, con expiración | Memory service |
| Workspace Memory | Lo aplicable a un workspace concreto (`WorkspaceId`), que puede abarcar varios repos | Indefinida, con expiración | Memory service |
| Global/User Memory | Preferencias y conocimiento técnico del usuario reutilizable entre proyectos. **Nunca contiene secretos** | Indefinida | Memory service |
| Run/Task/Lane scratch | Notas estructuradas temporales de una ejecución | La vida de esa entidad | Memory service (opcional) |

El `WorkingState` sigue `Pinned` y **separado** de la memoria de largo plazo. La memoria nunca ocupa su presupuesto.

### 3. Tres conceptos, tres ciclos de vida

| | **Skill** | **Knowledge** | **Memory** |
|---|---|---|---|
| Qué es | cómo realizar algo | información de dominio relativamente estable | lo aprendido sobre usuario, proyecto o sesión durante el trabajo |
| Origen | autores (humanos) | documentación, RAG, diccionarios, referencias | observaciones de ejecución, promovidas por política |
| Storage | archivos (`skill.yaml`…) | índices externos (RAG; `sqlite-vec` en el futuro) | `MemoryStore` por scope (ADR-0022 §3) |
| Ciclo de vida | `Discovered → … → Released` (ADR-0026) | ingesta y reindexado | `Candidate → Promoted → (Superseded \| Expired \| Forgotten)` |
| Política | activación | relevancia | promoción, expiración y privacidad |
| Entra al contexto como | `ContextItem(Kind=Skill)` | `ContextItem(Kind=Knowledge)` | `ContextItem(Kind=Memory)` |

Los tres producen `ContextItem`s, pero **no comparten** storage, ciclo de vida ni políticas.

### 4. `MemoryRecord`

```csharp
public sealed record MemoryRecord(
    MemoryId Id,
    MemoryScope Scope,                  // ScopeLevel + id concreto (ProjectId, WorkspaceId, SessionId…)
    MemoryKind Kind,                    // Fact | Preference | Decision | Convention | Procedure | Pitfall | Architecture | Environment | Summary
    string Content,                     // ya redactado (ADR-0018)
    double Importance,                  // 0..1
    double Confidence,                  // 0..1; sube con evidencia repetida
    MemorySource Source,                // Observed | UserStated | Imported | Promoted
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    ExpirationPolicy Expiration,        // Never | AfterUnused(TimeSpan) | At(DateTimeOffset) | WithScope
    IReadOnlyList<string> Tags,
    IReadOnlyList<ProvenanceRef> Provenance,   // eventos o artifacts de origen: (SessionId, seq), ArtifactRef, ruta+hash
    MemoryId? Supersedes);              // reemplazo explícito; no last-write-wins
```

### 5. Recuperación

```text
Memory Stores → retrieve → rank → dedupe → ContextBudget → ContextItems
```

1. **Retrieve:** candidatos por scope aplicable (ADR-0022, incluida la afinidad de proyecto) y por relevancia al `TaskPacket` y al `WorkingState`, usando tags, ruta, lenguaje y búsqueda léxica. La búsqueda vectorial es opcional y futura.
2. **Rank:** `score = relevancia × importance × confidence × recencia × afinidad de scope`. **No es last-write-wins**: un registro más específico no anula a uno general salvo que lo haga por `Supersedes`.
3. **Dedupe:** por hash de contenido normalizado; los casi-duplicados semánticos llegarán después. Los registros superseded no se recuperan.
4. **ContextBudget:** hay un slot `Memory` en el presupuesto (spec §26). Si se excede, se corta por score, nunca se recorta el `WorkingState`.
5. **Registro:** cada `ContextItem` de memoria lleva su procedencia (ADR-0029), y se emite `MemoryUsed { memoryIds }` para actualizar `LastUsedAt`.

### 6. Promoción: el LLM no decide solo

```text
Observation → MemoryCandidate → MemoryPolicy → Promote | Reject | Ask
```

```csharp
public sealed record MemoryCandidate(
    MemoryCandidateId Id, MemoryScope ProposedScope, MemoryKind Kind, string Content,
    IReadOnlyList<ProvenanceRef> Evidence, int Occurrences, CandidateOrigin Origin); // Model | Reconciler | User | Heuristic
```

- **Fuentes de candidatos:** el modelo, con la tool `memory.propose` (pipeline ADR-0014), heurísticas del runtime (por ejemplo, el mismo comando de build tres veces) o el usuario.
- **`MemoryPolicy`** (configurable), con estos defaults:

| Scope propuesto | Promoción por defecto |
|---|---|
| Run/Task/Lane scratch | automática |
| Session | automática si pasa el redactor y no hay conflicto con otra memoria |
| Project / Workspace | requiere **evidencia**: N ocurrencias en ≥ 2 Sessions, o confirmación del usuario. Si no, queda como candidato |
| Global/User | **siempre `Ask`** |

- **Rechazo automático:** candidatos con posibles secretos (ADR-0018), datos personales de terceros o contenido que contradice una memoria de mayor confianza. Este último caso escala a `Ask`.
- **Journal:** las decisiones de promoción se registran como eventos canónicos de la sesión (`MemoryCandidateProposed`, `MemoryPromoted`, `MemoryRejected`), aunque el registro promovido viva en el store de su scope.

## Clasificación

| Elemento | Categoría |
|---|---|
| `ContextItemKind.Memory` y `Knowledge`, slot `Memory` en `ContextBudget`, procedencia en `ContextItem` (ADR-0029) | **Necesario desde M1** (solo tipos; el Context Engine llega en M2) |
| `MemoryRecord`, `MemoryCandidate`, `MemoryPolicy`, `IMemoryStore`, `MemoryContextContributor`, `memory.propose` | **Contract now / implementation later**: Session Memory en M8; Project, Workspace y Global después |
| Búsqueda vectorial, dedupe semántico, consolidación automática | **Fully deferable** |
