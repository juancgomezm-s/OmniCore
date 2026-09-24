# ADR-0006 — Validación de JSON Schema con librería existente

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §32, §33, §82 (Tools: schema validation)

## Contexto

`ToolDescriptor.InputSchema` es `JsonSchema`, tipo que no existe en la librería base. .NET 9+ trae `System.Text.Json.Schema.JsonSchemaExporter` para **generar** esquemas desde tipos, pero no para **validar** instancias.

## Decisión

1. Generación de esquemas: `JsonSchemaExporter` (BCL) cuando la entrada de la tool sea un tipo C#.
2. Validación: librería existente. Candidata principal **JsonSchema.Net** (json-everything, basada en System.Text.Json). Se confirma versión y licencia al integrarla en M2 (EPIC-006).
3. `OmniCore.Abstractions` no expone el tipo de la librería: el contrato usa una representación propia (`JsonElement` del esquema) para que el proveedor de validación sea reemplazable.

## Consecuencias

- La misma definición de esquema alimenta la validación y la gramática GBNF de ADR-0007.
