# ADR-0042 — Conteo de tokens y política de contexto antes de M4

- **Estado:** Aceptada (2026-09-24)
- **Resuelve:** modelos F09 (sin estrategia de conteo) y F22 (qué pasa al exceder el presupuesto antes de M4)
- **Spec:** §24, §26, §29, §82
- **Relacionado:** ADR-0005, ADR-0011 (`/tokenize`), ADR-0017, ADR-0029

## Decisión

### 1. `ITokenCounter`

```csharp
public interface ITokenCounter
{
    TokenizerId Id { get; }                           // p. ej. "llamacpp:<hash de plantilla y vocabulario>", "estimate:chars/3.6"
    TokenCountAccuracy Accuracy { get; }              // Exact | Estimated
    ValueTask<int> CountAsync(ContextItem item, CancellationToken cancellationToken);
}
```

- **Selección por capacidad declarada del modelo:**
  - `Exact` con `/tokenize` del servidor llama.cpp/ik_llama, o con un tokenizer local si se declara;
  - `Estimated` para familias cloud sin tokenizer local, con una razón de caracteres por token calibrada por familia y un **margen de seguridad del 10 %**.
- **Caché:** los conteos se cachean por `(hash del contenido del item, TokenizerId)`. `EstimatedTokens` de un `ContextItem` es por tokenizer, no una propiedad fija del item.
- **Fingerprint:** el `TokenizerId` entra en el componente `context.policy` (ADR-0017).
- **Tests:** usan un `FakeTokenCounter` determinista (por ejemplo, palabras × 1). Así el test "deterministic budget" (spec §82) no depende de ningún servidor.
- **Calibración de la estimación:** tras cada Turn se compara con el `Usage` real del provider y se ajusta la razón por familia (telemetría local, ADR-0043).

### 2. Política de contexto antes de M4

Mientras no existan Prune, Compress y Compact, el Materializer aplica, en este orden:

1. **Externalizar:** outputs de tools por encima de su umbral → `ArtifactRef` + preview (spec §40).
2. **Recortar la conversación:** se descartan primero los turnos más antiguos que no están fijados, conservando el primer mensaje del usuario del Run.
3. **Nunca recortar** el `WorkingState`, el system, la Task ni los items fijados.
4. **Si aun así no entra:** se emite `ContextOverflow` (error tipado) y la Lane queda `Blocked`, con la sugerencia de escalar a un modelo de más contexto.

Cada decisión (incluido, externalizado, recortado) queda en el `ContextSnapshot` (ADR-0029 §1).

## Clasificación

| Elemento | Categoría |
|---|---|
| `ITokenCounter` + `FakeTokenCounter` (M1 los usa en los tests de presupuesto del WorkingState) | **Necesario desde M1** |
| Contadores reales y política de §2 | M2 |
| Calibración automática | M5 |
