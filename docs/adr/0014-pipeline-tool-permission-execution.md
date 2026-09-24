# ADR-0014 — Pipeline Tool → Permission → Execution

- **Estado:** Aceptada (2026-09-24)
- **Reemplaza:** el contrato `ITool.ValidateAsync` + `ExecuteAsync(ToolCall)` de la spec §33
- **Spec:** §30–§33, §42–§45, INV-001, INV-002, INV-003
- **Diagrama:** [arquitectura §7](../architecture/arquitectura.md#7-pipeline-raw-toolcall--execute)

## Contexto

Con `ValidateAsync(ToolCall)` una tool puede terminar haciendo I/O observable antes de estar autorizada. Además, nada impide estructuralmente llamar a `ExecuteAsync` sin pasar por el Permission Engine. Se quiere que ejecutar sin autorización sea **imposible por tipos**.

## Decisión

### 1. Etapas

```text
RawToolCall → SchemaValidation → Prepare (pura) → ToolIntent → Permission Engine → AuthorizedToolIntent → ExecuteAsync
```

| Etapa | Componente | I/O | Resultado |
|---|---|---|---|
| `RawToolCall` | Model Runtime / Agent Runtime | — | Nombre, argumentos JSON, `ProviderCallId`; se le asigna `ToolCallId` (ADR-0004) |
| Validación de esquema | Tool Runtime (ADR-0006) | **No** | Si es inválida, `ToolCallRejected` con el error exacto, que alimenta el repair loop (ADR-0007) |
| `Prepare` | la Tool | **No** (síncrona y pura) | `ToolIntent` o `ToolCallRejected` |
| Autorización | Permission Engine (Security) | Solo lectura (resolución física de rutas, ADR-0008 §7) | `Allow → AuthorizedToolIntent`, `Ask → cola (ADR-0003)`, `Deny` |
| `ExecuteAsync` | la Tool vía Tool Runtime | Sí | `ToolResult` + `EffectOutcome` |

### 2. Contratos

```csharp
public interface ITool
{
    ToolDescriptor Descriptor { get; }

    // PURA: sin I/O, sin efectos, sin Task/ValueTask. Solo normaliza y declara.
    ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context);

    ValueTask<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context, CancellationToken cancellationToken);
}

public abstract record ToolPreparation;
public sealed record Prepared(ToolIntent Intent) : ToolPreparation;
public sealed record PreparationRejected(string Reason, JsonElement? Details) : ToolPreparation;

public sealed record ToolIntent
{
    public required ToolCallId ToolCallId { get; init; }
    public required ToolId ToolId { get; init; }
    public required JsonElement NormalizedArguments { get; init; }
    public required EffectClass Effect { get; init; }                 // ADR-0004
    public required ResourceClaims Claims { get; init; }              // lo que el intent va a tocar
    public required ToolRisk Risk { get; init; }
    public ReconciliationSpec? Reconciliation { get; init; }          // pre/post hashes, trailer, idempotency key
}

public sealed record ResourceClaims(
    IReadOnlyList<NormalizedPath> Reads,
    IReadOnlyList<NormalizedPath> Writes,
    IReadOnlyList<NetworkTarget> Network,
    ProcessClaim? Process,                                            // ADR-0015
    IReadOnlyList<SecretRef> Secrets);                                // ADR-0018

public sealed class AuthorizedToolIntent
{
    internal AuthorizedToolIntent(ToolIntent intent, PermissionDecisionRecord decision) { ... }
    public ToolIntent Intent { get; }
    public PermissionDecisionRecord Decision { get; }                 // qué capas y grants lo autorizaron
}
```

### 3. Garantías estructurales

1. **`AuthorizedToolIntent` solo lo construye Security.** Su constructor es `internal` en Abstractions, con `InternalsVisibleTo("OmniCore.Security")` y ningún otro assembly (ADR-0009 §2.1, verificado por un test de arquitectura).
2. **`ExecuteAsync` solo acepta `AuthorizedToolIntent`.** No existe sobrecarga con `ToolCall`, así que ejecutar sin autorizar no compila.
3. **`Prepare` es síncrono** y su `ToolPreparationContext` solo expone datos puros:
   - raíz del workspace como string;
   - normalizador léxico de rutas;
   - configuración de la tool;
   - reloj congelado.

   No expone servicios, filesystem, red ni procesos.
4. **Tests:**
   - `Prepare` se ejecuta en un harness sin servicios y con un filesystem que falla ante cualquier acceso;
   - dos llamadas con la misma entrada deben producir el mismo `ToolIntent` (determinismo).
5. **La ejecución no puede exceder los `Claims`.**
   - Las tools de filesystem escriben a través de un `IWorkspaceFileSystem` restringido a los `Writes` autorizados.
   - Los procesos se lanzan con el sandbox derivado de `ProcessClaim` (ADR-0015).
   - Si una tool intenta salir de sus claims, el error es `InternalInvariantViolation`.
6. **La tool nunca ve ni decide su autorización.** Solo recibe el intent ya autorizado.

### 4. Tools internas del Core

`plan.propose` (ADR-0016) y `tool.search` siguen el mismo pipeline. Su permiso lo concede la capa `CoreBoundary`. No hay un atajo que las saque del pipeline.

## Clasificación

| Elemento | Categoría |
|---|---|
| Tipos `ToolIntent`, `ResourceClaims`, `AuthorizedToolIntent`, `EffectClass`, `ITool` con `Prepare`/`ExecuteAsync`; en M1 solo con `FakeTool` | **Necesario desde M1** |
| Tools reales de lectura (M2) y escritura (M3) | **Contract now / implementation later** |
| Analizador Roslyn que prohíba APIs de I/O dentro de `Prepare` | **Deferable** (los tests del harness cubren M1–M3) |
