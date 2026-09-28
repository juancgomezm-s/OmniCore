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

Estado: 125 tests verdes (dominio, codec de eventos, pipeline de tools con Allow/Deny/Ask,
reconciliación ADR-0004, PlanService, ProgressReconciler R1–R7, simulación, crash/resume
end-to-end, cliente/renderers/TUI, arquitectura, y 12+ pruebas de integración del cierre de M2).

## Estado de M2 (Explorer — turn real persistido + cableado e2e)

125 tests verdes, build 0 errores. El Turn end-to-end persiste en el journal (TurnStarted,
eventos del pipeline de tools, ModelCompleted con la respuesta como artifact, TurnCompleted) y
el replay tras reiniciar está probado. plan.propose aplica desde el Explorer con las
proyecciones del mismo Run; los secretos (`.env`, `.pem`, `.key`, `.ssh/`, Bearer/JWT) se
bloquean y redactan antes de journal, contexto, errores, audit y tool results; el presupuesto
del Run (TaskBudget) con costo real y topes de sesión/día emite InteractionRequested(BudgetExceeded);
el ContextOverflow se marca cuando un pinned supera el presupuesto; y el criterio
`omni "explícame este repositorio"` tiene test automatizado con el plain renderer.

- **Model runtime (ADR-0005/0011):** `IModelProvider`, `OpenAiChatCompatibleProvider` (mapeo y
  parse de `chat.completions`, `function.arguments` como string JSON, `ProviderCallId` correlacionado,
  escapes JSON completos, Content-Type `application/json`, `ModelProviderException` con el body,
  TLS relajado solo para loopback/privado) y registro de providers/modelos.
- **Turn real (`ExplorerTurn`):** contexto + fingerprint + plan + tools + permisos (asks se
  deniegan sin cliente interactivo, ADR-0003), presupuesto (SpendGuard integrado, ADR-0037 §7) y
  overflow de contexto con WorkingState pinned.
- **Permisos (ADR-0037):** `ModeDefaultsPolicy` + `ScriptedPermissionPolicy` con capa por modo,
  Ask aprobado ejecuta exactamente una vez (`AuthorizeApproved`, INV-002); `FileCredentialStore`
  con secretos ofuscados (nunca texto plano) y `ScopeResolver` (ADR-0022 §25).
- **Procesos/frontera:** `SystemProcessRuntime` (kill-tree `Kill(true)`, timeout real con drenaje
  concurrente, `IsAlive`), `PathBoundaryValidator` canónico (GetFullPath + symlinks/junctions
  intermedios + case-insensitive Windows), `LocalModelHost` (puerto efímero real + API key
  criptográfica + readiness + supervisión de muerte).
- **Planning (ADR-0035 §4):** modo Plan emite `PlanApproval` → ACT vía `RunModeChanged`;
  `plan.propose` aplica la mutación de verdad vía `PlanService` (eventos reales en el journal).
- **Configuración (ADR-0011):** `ConfigLoader` YAML respeta `auth: none` / `{apiKey}` y los
  `context`/`maxOutput` de los modelos; el CLI no pide key a providers sin auth.
- **Diagnóstico:** `omni doctor`, `omni explain "pregunta"` (contexto real redactado con PII
  removal) y `omni ask` end-to-end.

Pendiente de M3: workflows interactivos `/permissions`/`/context` con UI, `process.exec` real,
credenciales por plataforma (DPAPI/Credential Manager) y reinicio con backoff del managed host.
