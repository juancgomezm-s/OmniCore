# OmniCore

Runtime de agentes local-first para .NET 10, con el CLI `omni` como primer consumidor.

OmniCore ejecuta objetivos sobre repositorios mediante **Tasks** y **Lanes**, con contexto presupuestado por modelo, herramientas seleccionadas dinámicamente, permisos deterministas y finalización validada por Completion Gates. El mismo Task puede ejecutarse con un modelo local de 4B o con un modelo frontera cambiando solo la política.

## Estructura

| Proyecto | Responsabilidad |
|---|---|
| `OmniCore.Domain` | Tipos y entidades puras |
| `OmniCore.Abstractions` | Contratos públicos mínimos |
| `OmniCore.Engine` | Orquestación de Runs, Tasks y Lanes |
| `OmniCore.Context` | Materialización y gestión del contexto |
| `OmniCore.Models` | Providers, registry y routing de modelos |
| `OmniCore.Tools` | Catálogo, planner y runtime de tools |
| `OmniCore.Security` | Permisos y fronteras de workspace |
| `OmniCore.Execution` | Procesos, aislamiento, worktrees |
| `OmniCore.Sandbox` | Aislamiento de procesos en Windows, compartido con OmniCoder |
| `OmniCore.Infrastructure` | SQLite, artifacts, stores |
| `OmniCore.Protocol` | Commands, eventos y DTOs wire-safe |
| `OmniCore.Host` | Composition root |
| `OmniCore.Client` | Estado de cliente independiente del framework visual (`ClientProjection`, presentación, acciones) |
| `OmniCore.Cli` | Cliente `omni`: TUI (Terminal.Gui v2), plain (Spectre.Console) y JSON |

## Uso

```bash
dotnet build OmniCore.slnx
dotnet test --solution OmniCore.slnx
dotnet run --project src/OmniCore.Cli
```

## Documentación

- [Especificación v1](docs/spec/OmniCore-v1.md)
- [Decisiones de arquitectura](docs/adr/README.md)
- [Arquitectura: diagramas, preguntas abiertas y roadmap](docs/architecture/arquitectura.md)
