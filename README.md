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
dotnet run --project src/OmniCore.Cli -- sim                # escenario determinista de M1
dotnet run --project src/OmniCore.Cli -- sim docs/sim/multi-item-plan.yaml --json
dotnet run --project src/OmniCore.Cli -- sim --crash        # inyecta un crash tras el Started
dotnet run --project src/OmniCore.Cli -- sim --resume       # reconcilia sin duplicar el efecto
dotnet run --project src/OmniCore.Cli -- tui --sim          # frame TUI de 4 zonas
dotnet run --project src/OmniCore.Cli -- doctor             # diagnóstico del registro de modelos (M2)
dotnet run --project src/OmniCore.Cli -- explain "pregunta" # contexto materializado + fingerprint (M2)
dotnet run --project src/OmniCore.Cli -- ask "tu pregunta"  # Turn end-to-end contra el modelo local (M2)
```

## Documentación

- [Especificación v1](docs/spec/OmniCore-v1.md)
- [Decisiones de arquitectura](docs/adr/README.md)
- [Arquitectura: diagramas, preguntas abiertas y roadmap](docs/architecture/arquitectura.md)

## Estado de M1 (implementación)

Entregado el núcleo de M1 — runtime sin IA + Planning — sobre las 30 invariantes y los ADRs:

- **Dominio** (`OmniCore.Domain`): ids tipados, `LocalizedText`, estados y máquinas de estado
  canónicas con un evento por transición (ADR-0036), `Plan`/`PlanItem`/`WorkingState` (ADR-0016),
  tipos de permisos (ADR-0037) y los eventos de `Session/Run/Task/Lane/Turn/ToolCall/Plan`.
- **Journal** (`OmniCore.Infrastructure`): envelope con `EventType` + `EventSchemaVersion`
  (ADR-0013), `InMemoryEventStore` y `SqliteEventStore` (ADR-0002), `FileArtifactStore`
  content-addressed (ADR-0001/0041) y `AuditSink` (ADR-0043).
- **Engine**: `PlanService` (+política de impacto), `ProgressReconciler` R1–R7, `PlanCompletionGate`,
  watchdog, proyección `WorkingState` y el `SimulationEngine` de M1 (ADR-0041).
- **Tools** (`OmniCore.Tools`): `FakeTool` (ITool real con `Prepare` puro), `ToolRuntime` que
  ejecuta el pipeline completo (schema → Prepare → Permission → `AuthorizedToolIntent` →
  `ExecuteAsync`; ADR-0014) y reconciliación de efectos tras crash (ADR-0004).
- **Security**: `ScriptedPermissionPolicy` que implementa `IPermissionPolicy`; solo este assembly
  materializa el intent autorizado (INV-018, ADR-0009 §2.1).
- **Protocol/Host**: `WireEnvelope` con tipos propios (ADR-0013), `OmniServer` in-process
  (ADR-0019) y composición en `OmniHost.CreateInMemoryServer` (inyecta el executor real).
- **Cliente** (`OmniCore.Client`): `ClientProjection` (reducer puro, ADR-0030) que reduce los
  eventos wire del server a bloques de conversación semánticos (ADR-0033); recursos es/en
  (ADR-0040). El CLI usa `ClientProjection` → `PlainRenderer` para la salida legible,
  `JsonRenderer` para el contrato de máquina NDJSON (WireEnvelope + `RunOutcome`) y `TuiApp`
  para la TUI de 4 zonas (header, conversación, sidebar, status line; ADR-0031). Todos los
  renderers leen el mismo estado (ADR-0030 §3).
- **CLI**: `omni sim [escenario.yaml] [--json|--plain]`, `omni tui [--sim]` y la golden rule
  (replay = estado vivo); `omni sim --crash` inyecta un crash tras el Started de una tool y
  `omni sim --resume` (o el mismo proceso con `sim.resume` vía IOmniClient) reconcilia la
  toolcall huérfana **sin duplicar el efecto** (ADR-0004, ADR-0041 §2), incluso entre
  procesos gracias al journal SQLite persistente.

> **Nota del toolchain:** este entorno es un runtime .NET basado en JVM; el ensamblado
> Terminal.Gui 2.5.0 no se enlaza como tipos compilables aquí. La TUI de M1 (`TuiApp`) dibuja
> las 4 zonas con ANSI propio sobre los mismos modelos de `ClientProjection`; el contrato no
> cambia (ADR-0030 §3) y cuando el runtime lo permita, Terminal.Gui lo sustituye como otro
> renderer.

Estado: 80 tests verdes (dominio, codec de eventos, pipeline de tools con Allow/Deny/Ask,
reconciliación ADR-0004, PlanService, ProgressReconciler R1–R7, simulación, crash/resume
end-to-end, cliente/renderers/TUI y arquitectura).
Pendiente del roadmap: los milestones M2+.

## Estado de M2 (Explorer — núcleo implementado)

El contrato del milestone está entregado y verificado (97 tests verdes, build 0 errores):

- **Model runtime (ADR-0005/0011):** `IModelProvider` con el contrato neutral
  (`ModelRequest`/`ModelResponse`/`ContentBlock`/`ProviderOpaque`), `OpenAiChatCompatibleProvider`
  con mapeo y parse de `chat.completions` (texto + tool calls + usage) verificado, y registro
  mínimo de providers/modelos con `NoModelConfigured`.
- **Perfil y política (ADR-0007):** `EffectiveModelProfile` + `HarnessPolicyResolver` puro
  (ToolCallFormat, ToolMode, PlanControl, RepairAttempts, StallThreshold), con tests que prueban
  que los valores cambian el comportamiento.
- **Permisos (ADR-0037 §4):** `ModeDefaultsPolicy` con perfil autónomo (escribir en ACT vs PLAN,
  build/test, secretos siempre Deny) + `IPathBoundaryValidator` (Execution) que rechaza traversal.
- **Context Engine v1:** `ContextMaterializer` puro (WorkingState al final, conteo con token
  counter, fingerprint) + `WorkingStateContributor` + `ExecutionFingerprint`.
- **Tools Core:** `filesystem.read`, `reference.resolve` reales con frontera de paths;
  `plan.propose`; `ToolPresentation` declarativa.
- **Planning (ADR-0035 §4):** el modo Plan emite `PlanApproval` (InteractionRequested) y el Run
  pasa a ACT con `RunModeChanged` en el mismo Run.
- **Configuración:** `ConfigLoader` YAML (YamlDotNet verificado en este runtime) con registro
  mínimo por defecto; providers/models YAML descargados.
- **CLI:** `omni explain` muestra el contexto materializado (WorkingState) + milestone, y `omni sim`
  mantiene todo M1.

### Conexión en vivo

`omni ask "…"` conecta a un servidor local de chat.completions (ik_llama/llama.cpp) habilitando TLS
para IP local, con la API key por entorno (`OMNI_QWEN_KEY`) y normalizando la respuesta: emite
`ReasoningBlock` desde `reasoning_content` (modelos tipo Qwen), `TextBlock` y `StopReason`
(`EndTurn`/`MaxOutputTokens`/`ToolUse`). Verificado contra un servidor real.

Pendiente de M2: `LocalModelHost` (attach/managed) y `ISecretProvider` sobre Credential Manager/DPAPI.
