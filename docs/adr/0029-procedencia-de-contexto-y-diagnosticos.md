# ADR-0029 — Procedencia de contexto y diagnósticos de extensibilidad y memoria

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §24, §29, §64, §66
- **Relacionado:** ADR-0017 (fingerprint), ADR-0023 (`ComponentSource`), ADR-0026, ADR-0027, ADR-0028

## Decisión

### 1. Todo `ContextItem` lleva procedencia

```csharp
public sealed record ContextProvenance(
    ContributorId Contributor,          // "core.working-state", "core.files", "skill:progress-compile", "memory.project", "knowledge.rag"…
    ContributionCategory Category,      // PlanWorkingState | System | Task | Skill | ProjectMemory | WorkspaceMemory | SessionMemory
                                        // | GlobalMemory | Knowledge | File | ToolObservation | Conversation | SubagentResult | Checkpoint
    ComponentSource Source,             // ADR-0023
    IReadOnlyList<ProvenanceRef> Refs,  // skill id+versión, memoryIds, ruta+hash, ArtifactRef, (SessionId, seq)
    Sensitivity Sensitivity);
```

- **Contrato:** `IContextContributor.GetContextAsync` devuelve items **con** procedencia, y el Materializer rechaza items sin ella.
- **Snapshot:** `ContextSnapshot` guarda, por item, procedencia, tokens y la decisión del materializer (incluido, podado, comprimido, externalizado o excluido por presupuesto).
- **Fingerprint:** `ExecutionFingerprint` agrega el componente `context.contributors`, con los contributors activos y sus versiones.

### 2. Qué se puede inspeccionar

Contestar "qué contribuyó efectivamente al comportamiento de esta ejecución":

| Pregunta | Fuente |
|---|---|
| Qué entró al contexto del Turn N, por categoría y origen | `ContextSnapshot` (procedencia + tokens) |
| Qué tools vio el modelo y de dónde venían | `ToolPlan` (ADR-0027, nombres visibles → `ToolId` + `Source`) |
| Qué skills se activaron, cargaron o liberaron | eventos `SkillActivated` y `SkillReleased` + fingerprint |
| Qué memorias se usaron | `MemoryUsed` + procedencia |
| Qué hooks restringieron o anotaron | eventos `HookApplied { hookId, result }` |
| Qué configuración y overrides aplicaron | `ExecutionFingerprint` + `Resolved<T>.Contributions` (ADR-0022) |

**Contenido sensible:** el diagnóstico muestra metadata (categoría, origen, tokens, ids) y **nunca** contenido `Sensitive` ni secretos. El contenido de un item normal se ve solo con una acción explícita (`/context show <item>`).

### 3. `/context` por categoría

```text
Context: 22,418 / 32,768                          Turn 14 · fingerprint 7c2e…
Plan / WorkingState    1,118  pinned              core.working-state
System                 2,731                      core.system
Task                   1,420                      core.task
Skills                 1,903  2 cargadas          skill:progress-compile@1.4 (project) · skill:dotnet-test@2 (builtin)
Project Memory           612  4 memorias          memory.project
Session Memory           240  2 memorias          memory.session
Knowledge / RAG            0
Files                  6,224  5 archivos          core.files
Tool observations      2,991  3 externalizados    core.tools
Conversation           5,149
Generation reserve     6,350
```

### 4. Commands de diagnóstico

| Command | Muestra | Milestone |
|---|---|---|
| `/context` (y `/context show <item>`) | desglose por categoría y origen | M2 |
| `/tools` | origen, protección, alternativas y rechazos | M2 |
| `/commands` | catálogo, fuentes, conflictos y ambigüedades | M8 (M1: lista mínima) |
| `/keybindings` | bindings efectivos, fuentes y conflictos | M10 |
| `/skills` | skills por estado del ciclo de vida, origen y conflictos | M8 |
| `/extensions` | extensiones, versión, confianza, capacidades concedidas y compatibilidad | M8 |
| `/memory`, `/memory session\|project\|global` | registros, candidatos pendientes, uso reciente, sin contenido sensible | M8+ |

## Clasificación

| Elemento | Categoría |
|---|---|
| `ContextProvenance` en `ContextItem` y `ContributionCategory` (los persiste `ContextSnapshot`, así que cambiarlos después afectaría el dominio) | **Necesario desde M1** (solo tipos) |
| `/context` con categorías (M2), `/tools` (M2), el resto según la tabla | **Contract now / implementation later** |
| Diff de contexto entre Turns | **Deferable** |
