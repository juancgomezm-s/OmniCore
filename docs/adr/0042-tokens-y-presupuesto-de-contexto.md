# ADR-0042 — Conteo de tokens y política de contexto

> **Rev. (2026-09-30):** EPIC-025 implementa la política de contexto completa. Esta revisión sustituye la estrategia provisional anterior a M4 de §2.

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

### 2. Política de gestión de contexto implementada (EPIC-025)

Cada Turn reconstruye una proyección nueva del estado y aplica las etapas siguientes, en orden:

1. **Prune:** elimina lecturas de archivos supersedidas cuando existe una lectura más reciente de la misma versión/ruta. Las decisiones quedan registradas como diagnósticos del `ContextSnapshot`.
2. **Externalize:** si un resultado de tool supera `HarnessPolicy.ContextManagement.ExternalizeAboveCharacters`, lo redacta y guarda como artifact inmutable; el contexto conserva un preview y una instrucción con hash para recuperarlo mediante la tool de lectura de artifacts (`artifact.read`, con `offset` y `limit`). El contenido externalizado sigue siendo legible por el modelo y no se pierde al compactar el contexto.
3. **Compress:** comprime el cuerpo de la conversación anterior a la cola reciente, cuyo tamaño se determina por `RecentTailItems`. Conserva la estructura de llamadas/resultados de tools y protege el contenido fijado. El máximo del cuerpo comprimido se toma de `CompressBodyCharacters`.
4. **Compact:** al alcanzar `CompactAfterItems` elementos antiguos todavía no compactados, crea un checkpoint de la conversación. `MetaModelService` solicita un resumen al mismo provider/modelo nativo y conserva la semántica propia del provider; si falla o no está disponible, usa el resumen determinista de fallback. El resumen se redacta, guarda en un artifact inmutable y se registra mediante `ContextCheckpointRecorded` (`ContextCheckpoint`), con fingerprint de operación y secuencia cubierta. Los elementos compactados se omiten de la siguiente proyección; el checkpoint se vuelve a contribuir en cada Turn.
5. **Barrera final de presupuesto:** cuenta los items con el `ITokenCounter` seleccionado y recorta contra el presupuesto de contexto del modelo. Primero descarta conversación antigua no fijada, después contribuciones regenerables/de prioridad baja y, como último recurso, trunca otros items no protegidos. Si los items protegidos por sí solos exceden el presupuesto, conserva esos items y marca overflow; no los trunca para aparentar que el contexto cabe. `ExplorerTurn` abandona ese Turn con `StopReason.ContextOverflow`.

Los umbrales de externalización, compresión, cola reciente, compactación y tamaño máximo del checkpoint proceden de `HarnessPolicy.ContextManagement`, que se deriva para la capacidad efectiva a partir de `EffectiveModelProfile` (ADR-0007). No se decide por el nombre ni por el tamaño nominal del modelo. Los valores predeterminados generales son `4096` caracteres para externalizar, `1200` para el cuerpo comprimido, `12` elementos recientes, `24` elementos antiguos antes de compactar y `6000` caracteres para el checkpoint; perfiles con contexto utilizable menor reciben umbrales más conservadores. Un valor configurado debe tener tests que demuestren que modifica el comportamiento.

`WorkingState` siempre se reconstruye fresco desde el estado canónico del Run en cada Turn. Es `Pinned`/`RegenerateEachTurn` y queda protegido en Prune, Compress, Compact y la barrera final; nunca se sustituye por el checkpoint ni por un resumen de conversación. El primer mensaje del usuario también se conserva ante el recorte de presupuesto. La compactación no modifica el Journal ni los artifacts ya referenciados.

Cada decisión de incluir, podar, externalizar, comprimir, compactar, omitir o truncar queda representada en el `ContextSnapshot` (ADR-0029 §1). Una salida externalizada mantiene su procedencia y referencia al artifact; la tool de lectura de artifacts permite recorrerla por partes.

## Clasificación

| Elemento | Categoría |
|---|---|
| `ITokenCounter` + `FakeTokenCounter`, conteo exacto/estimado y fingerprint del tokenizer | **Necesario desde M1** |
| Política Prune → Externalize → Compress → Compact → barrera final; `ContextCheckpoint`; externalización recuperable vía artifact read | **Implementado en EPIC-025** |
| Umbrales derivados de `HarnessPolicy`/`EffectiveModelProfile` y calibración de estimaciones | Calibración automática **M5** |
