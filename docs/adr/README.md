# Architecture Decision Records

Cada ADR registra una decisión que precisa o modifica `docs/spec/OmniCore-v1.md`. Si un ADR contradice la spec, prevalece el ADR.

- **Estados:** Aceptada, Propuesta, Reemplazada por ADR-XXXX.
- **Revisiones:** un ADR revisado conserva su número y deja constancia de la versión anterior en su encabezado ("rev. 1: …").
- **Revisión arquitectónica del 2026-09-24** (v0.2: ADRs 0001–0021; v0.3: ADRs 0022–0029): diagramas, tabla de cambios, preguntas abiertas y roadmap en [docs/architecture/arquitectura.md](../architecture/arquitectura.md).

| ADR | Título | Estado |
|---|---|---|
| [0001](0001-canonical-journal.md) | Canonical Journal: Event Store + artifacts inmutables | Aceptada, rev. 2 |
| [0002](0002-event-store-sqlite.md) | Event Store en SQLite desde M1 y política de durabilidad | Aceptada, rev. 2 |
| [0003](0003-ask-sin-cliente.md) | `Ask` sin cliente interactivo se resuelve como `Deny` | Aceptada, rev. 2 |
| [0004](0004-resume-y-effect-journal.md) | Resume y Effect Journal para ToolCalls | Aceptada, rev. 2 |
| [0005](0005-model-runtime-provider-native.md) | Model Runtime provider-native | Aceptada, rev. 2 (reemplaza "único provider OpenAI-compatible") |
| [0006](0006-json-schema.md) | Validación de JSON Schema con librería existente | Aceptada |
| [0007](0007-model-qualification-framework.md) | Model Qualification Framework y EffectiveModelProfile | Aceptada, rev. 2 (reemplaza "categorías por tamaño") |
| [0008](0008-sandbox-compartido.md) | Sandbox como librería compartida `OmniCore.Sandbox` | Aceptada, rev. 2 |
| [0009](0009-grafo-de-dependencias.md) | Grafo de dependencias entre proyectos | Aceptada, rev. 2 |
| [0010](0010-plataforma-y-tooling.md) | .NET 10 en todo, tooling de build y tests | Aceptada |
| [0011](0011-conexion-a-proveedores.md) | Mecanismo de conexión a proveedores de modelos | Aceptada, rev. 2 |
| [0012](0012-lanes-delegadas-a-claude-code.md) | Lanes delegadas a Claude Code con la suscripción del usuario | Aceptada, rev. 2 |
| [0013](0013-event-schema-vs-protocol.md) | Versión de schema de evento separada del Omni Protocol | Aceptada |
| [0014](0014-pipeline-tool-permission-execution.md) | Pipeline Tool → Permission → Execution | Aceptada |
| [0015](0015-process-runtime.md) | Process Runtime unificado: `process.exec` como primitive | Aceptada |
| [0016](0016-plan-y-working-state.md) | Plan/Todo canónico, WorkingState y ProgressReconciler | Aceptada |
| [0017](0017-execution-fingerprint.md) | ExecutionFingerprint por Turn | Aceptada |
| [0018](0018-secretos-y-redaccion.md) | Gestión de secretos y redacción | Aceptada |
| [0019](0019-cli-transport-host.md) | CLI → IOmniClient / IOmniTransport → Host → Engine | Aceptada |
| [0020](0020-hooks-trust-capabilities.md) | Modelo de confianza y capacidades de hooks | Aceptada, rev. 2: niveles unificados con 0023 (implementación en M8) |
| [0021](0021-git-worktree-y-workspace-sucio.md) | GitWorktree isolation con workspace sucio | Aceptada (implementación en M7) |
| [0022](0022-modelo-de-scopes.md) | Modelo de scopes, identidades (`ProjectId`/`WorkspaceId`) y estrategias de resolución | Aceptada |
| [0023](0023-modelo-de-extensiones.md) | Modelo de extensiones y niveles de confianza unificados | Aceptada (implementación en M8) |
| [0024](0024-commands.md) | Commands como subsistema formal | Aceptada |
| [0025](0025-client-actions-keybindings.md) | Client Actions, KeyBindingService y Command Palette | Aceptada (solo cliente) |
| [0026](0026-skills.md) | Skills: scopes, precedencia y ciclo de vida | Aceptada (implementación en M8) |
| [0027](0027-tool-sources-y-precedencia.md) | Origen de tools y precedencia en el ToolCatalog | Aceptada |
| [0028](0028-memory.md) | Memory como servicio externo; separación Memory / Knowledge / Skills | Aceptada (implementación M8+) |
| [0029](0029-procedencia-de-contexto-y-diagnosticos.md) | Procedencia de contexto y diagnósticos | Aceptada |
