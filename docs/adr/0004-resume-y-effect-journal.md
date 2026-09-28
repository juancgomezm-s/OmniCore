# ADR-0004 — Resume y Effect Journal para ToolCalls

- **Estado:** Aceptada — rev. 2 (2026-09-24)
- **Rev. 1:** solo reanudar desde el último Turn completo. No resolvía tools con efectos laterales cuyo outcome no llegó a persistirse.
- **Spec:** FR-RUN-006, §12, §41, §70–§72
- **Relacionado:** ADR-0002 (commits Barrier), ADR-0003 (Ask sin cliente), ADR-0014 (pipeline de tools), ADR-0015 (procesos)
- **Diagrama:** [arquitectura §6](../architecture/arquitectura.md#6-toolcall-durable-effect-journal-y-recovery)

## Problema

```text
ToolCallStarted → el filesystem, Git, una API o un proceso producen un efecto → OmniCore muere → ToolCallCompleted nunca se persiste
```

Al reanudar no se puede repetir la tool a ciegas.

## Decisión

### 1. Identidad estable

- Cada ToolCall recibe un **`ToolCallId` propio de OmniCore** (UUIDv7) en `ToolCallRequested`, antes de cualquier otra etapa.
- El id que asigna el provider (`ProviderCallId`) se guarda como mapeo, para devolver el resultado al provider (ADR-0005).
- El `ToolCallId` es la clave de idempotencia desde la perspectiva del journal. También se usa como idempotency key hacia sistemas externos y como trailer en commits de Git.

### 2. Ciclo de vida durable

```text
ToolCallRequested → ToolCallPrepared | ToolCallRejected
ToolCallPrepared  → PermissionRequested → PermissionGranted | PermissionDenied
PermissionGranted → ToolCallAuthorized → ToolCallStarted (Barrier si EffectClass ≠ None)
ToolCallStarted   → ToolCallSucceeded | ToolCallFailed
                  → ToolCallEffectUnknown (solo en recovery) → ToolCallReconciled(Applied | NotApplied | Conflict | Unresolvable)
```

- `ToolCallFailed` incluye un `EffectOutcome` (`None | Applied | Partial | Unknown`), porque una tool puede fallar después de producir un efecto.
- `ToolCallEffectUnknown` significa que la ejecución empezó pero no se sabe si el efecto final ocurrió.

### 3. Clase de efecto

La **declara el `ToolIntent`** en `Prepare` (ADR-0014); no se infiere en tiempo de ejecución.

| `EffectClass` | Ejemplos | ¿Reejecutar tras un crash? |
|---|---|---|
| `None` | `filesystem.read`, `search.text`, `git.status` | Sí, siempre |
| `Rerunnable` | build y test cuyos efectos quedan confinados a outputs regenerables | Sí, según la política de la tool |
| `Reconcilable` | write/patch con version token, operaciones Git con resultado esperado | Solo tras reconciliar |
| `NonIdempotent` | APIs externas sin idempotency key, `shell.exec` y procesos con efecto desconocido | Nunca automáticamente |

### 4. `ReconciliationPolicy` por tipo de operación

| Operación | El intent registra | Reconciliación |
|---|---|---|
| Filesystem write/patch | ruta, `ExpectedPreHash` (version token, spec §41), `ExpectedPostHash` | Se relee el hash actual. Si coincide con el post-hash, el efecto está `Applied`. Si coincide con el pre-hash, está `NotApplied` y se puede reintentar si la política lo permite. Cualquier otro valor es `Conflict`: el agente relee y reconcilia. |
| Git (commit, apply, checkout) | HEAD, tree e index previos; tree esperado; trailer `OmniCore-ToolCall: <id>` | Se inspecciona HEAD, reflog e index. Si el commit con ese trailer y el tree esperado ya existe, está `Applied`. Si el estado sigue igual al previo, está `NotApplied`. Otro estado es `Conflict`. |
| Network/API | host, método e idempotency key igual al `ToolCallId` | Si el destino soporta idempotency key, se reenvía con la misma key y es seguro. Si no la soporta, es `Unresolvable` y se resuelve por `Ask` (sin cliente interactivo es `Deny` y la Task queda `Blocked`, ADR-0003). |
| Proceso | `ProcessEffect` declarado (ADR-0015) | `Observational` se reejecuta. `Rerunnable` se reejecuta según la política. `WorkspaceEffect` dentro de un worktree aislado permite restaurar el worktree al checkpoint y rehacer; fuera de uno es `Unresolvable`. `External` es `Unresolvable`. |

- `Conflict` y `Unresolvable` nunca se resuelven solos: se entregan al agente como resultado de la tool (para releer o replanificar) o escalan a `Ask`.
- **El aislamiento con worktree simplifica la recuperación:** el efecto queda contenido y se puede descartar.

### 5. Integración con resume

1. **Detección:** al reanudar un Run se buscan ToolCalls en `Started` sin outcome, y a cada una se le emite `ToolCallEffectUnknown`.
2. **Reconciliación:** se ejecuta la `ReconciliationPolicy` de cada una y se emite `ToolCallReconciled`.
3. **Resultado hacia el modelo:** el resultado reconciliado se entrega al Turn como `ToolResult`, por ejemplo "el efecto se aplicó; verificado por hash".
4. **Turn:**
   - Si su respuesta de modelo quedó registrada completa (`ModelCompleted` con artifact), **no se vuelve a inferir**. Se continúa con las ToolCalls autorizadas que aún no empezaron, revalidando la vigencia de sus permisos.
   - Si la respuesta está incompleta, el Turn se abandona (evento `TurnAbandoned`) y se re-infiere desde su contexto, como en la rev. 1.
5. **Runs terminales:** un Run solo es reanudable si su estado proyectado no es terminal.

### 5bis. Raíz durable con identidad verificable (recuperación del Host)

Para reconciliar efectos contra el filesystem del workspace, la recuperación del Host (ADR-0041 §2)
usa la raíz del run como frontera y autoridad de paths. Solo acepta como origen la raíz durable
emitida en `WorkspaceRootEstablished` — nunca el cwd de un proceso posterior ni una ruta de display
(`WorkspaceDisplayPath`).

La integridad de esa raíz se refuerza con una **identidad durable**: al crear la sesión se escribe un
token de marcador DENTRO del workspace (`{root}/.omnicore/workspace-id`) y el mismo token viaja en
`WorkspaceRootEstablished.DurableIdentity`. Al reabrir (arranque del Host), la verificación exige que
esa ruta siga resolviendo al MISMO árbol (marcador idéntico), además de `Directory.Exists`. Esto cierra
el hueco en que una ruta sustituida por symlink/junction a otro árbol pasaría el check de existencia
y la recuperación clasificaría `Applied` contra un árbol equivocado.

- Identidad ausente en el evento (evento legacy/fallo al establecer), ruta movida/borrada o marcador
  distinto/ausente → la recuperación **falla cerrado**: bloqueada, visible (`LastRecoveryProblem`),
  sin re-ejecutar ni clasificar `Applied`, y el Run no se continúa automáticamente.

**Limitación documentada (no se declara el crash recovery “cerrado”):** la identidad detecta la
SUSTITUCIÓN DE LA RUTA por un enlace (junction/symlink) a otro árbol, no la falsificación del árbol
completo replicando además el marcador idéntico dentro del sustituto (toma de control del árbol con
acceso de escritura, fuera del alcance de esta barrera). Ese caso queda como limitación conocida y no
se presenta la identidad como cierre total del resume.

## Clasificación

| Elemento | Categoría |
|---|---|
| `ToolCallId`, estados y eventos del ciclo de vida, `EffectClass`, `EffectOutcome` (dominio y máquina de estados, probados con simulación) | **Necesario desde M1** |
| `ReconciliationPolicy`: filesystem en M3, Git en M7, red cuando exista una tool de red | **Contract now / implementation later** |
| Reconciliación de APIs específicas | **Deferable** |
