# ADR-0005 — Un único provider OpenAI-compatible como base

- **Estado:** Aceptada (2026-09-24) — ampliada por [ADR-0011](0011-conexion-a-proveedores.md) (tres dialectos, registro, credenciales, servidor local)
- **Spec:** §17–§19, INV-011

## Contexto

llama.cpp server, OpenAI y OpenRouter exponen `/v1/chat/completions` con el mismo formato. Implementar tres providers duplicaría código.

## Decisión

1. `OpenAiCompatibleProvider` (en `OmniCore.Models`) implementa `IModelProvider` para cualquier endpoint compatible. Base URL, API key y headers vienen de configuración.
2. `LlamaCppProvider` extiende el anterior solo con lo propio de llama.cpp: gramáticas GBNF / `json_schema` para tool calling restringido (ADR-0007), `/tokenize` para conteo exacto, `/props` para ventana de contexto real y slots.
3. Anthropic nativo u otros protocolos distintos serán providers separados, más adelante.
4. El Agent Runtime solo conoce `IModelProvider` (INV-011).

## Consecuencias

- M2 necesita un solo provider para trabajar con modelos locales y frontera.
- Diferencias de dialecto entre endpoints "compatibles" se resuelven con flags en el `ModelDescriptor`, no con `if (provider == ...)`.
