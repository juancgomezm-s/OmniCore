# Architecture Decision Records

Cada ADR registra una decisión que precisa o modifica `docs/spec/OmniCore-v1.md`. Si un ADR contradice la spec, prevalece el ADR.

Estados: **Aceptada**, **Propuesta**, **Reemplazada por ADR-XXXX**.

| ADR | Título | Estado |
|---|---|---|
| [0001](0001-eventos-fuente-de-verdad.md) | Eventos como única fuente de verdad, secuencia por sesión | Aceptada |
| [0002](0002-event-store-sqlite.md) | Event Store en SQLite desde M1; vectorial después con sqlite-vec | Aceptada |
| [0003](0003-ask-sin-cliente.md) | `Ask` sin cliente interactivo se resuelve como `Deny` | Aceptada |
| [0004](0004-resume-por-turn.md) | Reanudación solo desde el último Turn completo | Aceptada |
| [0005](0005-provider-openai-compatible.md) | Un único provider OpenAI-compatible como base | Aceptada, ampliada por 0011 |
| [0006](0006-json-schema.md) | Validación de JSON Schema con librería existente | Aceptada |
| [0007](0007-categorias-de-modelo.md) | Cuatro categorías de modelo que gobiernan el tool calling | Aceptada |
| [0008](0008-sandbox-compartido.md) | Sandbox como librería compartida `OmniCore.Sandbox` | Aceptada |
| [0009](0009-grafo-de-dependencias.md) | Grafo de dependencias entre proyectos | Aceptada |
| [0010](0010-plataforma-y-tooling.md) | .NET 10 en todo, tooling de build y tests | Aceptada |
| [0011](0011-conexion-a-proveedores.md) | Mecanismo de conexión a proveedores de modelos | Aceptada |
| [0012](0012-lanes-delegadas-a-claude-code.md) | Lanes delegadas a Claude Code con la suscripción del usuario | Aceptada |
