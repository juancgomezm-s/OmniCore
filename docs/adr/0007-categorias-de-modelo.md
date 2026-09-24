# ADR-0007 — Cuatro categorías de modelo que gobiernan el tool calling

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §2, §17, §20, §35, §37, INV-010

## Contexto

El mayor riesgo técnico de M2 es que modelos locales de 4B–14B emitan tool calls mal formadas. La spec no define formato, parsing ni reparación.

OmniCoder ya clasifica modelos (`ModelSizeClassifier`: Small ≤9B, Medium 9–27B, Large ≥27B, Frontier, Unknown) con `HarnessProfiles` configurables. Aprendizajes de ese trabajo:

- La categoría solo cambiaba guidance y techo de salida; el formato de tool calling era siempre nativo y el número de tools igual para todas.
- `ToolCallLimit`, `LoopThreshold` y `RepairAttempts` se persistían pero **no llegaban al runtime** (el `harnessPolicy` nunca se leía).
- Una ablación mostró que una guía de tools llevó a Qwen 4B de 100 % a 0 % de éxito: para modelos pequeños, menos contexto es más.

## Decisión

1. Cuatro categorías configurables (más `Unknown`, que se trata como `Small`):

   | Categoría | Parámetros | Formato tool call | Tools visibles | Guidance | Repair |
   |---|---|---|---|---|---|
   | Small | ≤ 9B | Grammar | 4 | Off | 2 |
   | Medium | 9–27B | Native | 8 | DomainOnly | 2 |
   | Large | ≥ 27B | Native, `Discovered` | 16 | Full | 1 |
   | Frontier | — | Native, `Discovered` | 32 | Full | 1 |

   Los valores son **defaults de configuración**, no código.
2. La categoría se deriva del **`ModelDescriptor` declarado** (número de parámetros / clase explícita en configuración), no de una regex sobre el nombre (INV-010). La regex solo sugiere un valor al autodescubrir modelos.
3. Precedencia: override por modelo → categoría → default global.
4. Formatos:
   - **Native:** `tools` estándar OpenAI (llama-server con `--jinja`).
   - **Grammar:** GBNF / `json_schema` generado desde la unión de esquemas de las tools visibles; el modelo no puede emitir JSON inválido.
   - **PromptedJson:** último recurso para endpoints sin gramática ni tools nativas.
5. Pipeline de reparación: parse tolerante (fences, comas finales, comillas simples) → validación de esquema → devolver el error concreto al modelo como resultado de la tool → agotado `repairAttempts`, bajar a `Grammar` o escalar según `ModelPolicy` (evento `ModelEscalationRequested`, causa `tool reliability`).
6. **Cada parámetro configurable tiene un test que demuestra que cambia el comportamiento del runtime.**

## Consecuencias

- La diferencia entre un 4B y un frontier es configuración + `EffectiveModelProfile`, cumpliendo §104.
- `ToolPlanner` recibe `maxVisibleTools` y `ToolMode` desde el perfil efectivo.
