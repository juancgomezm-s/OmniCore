# Architecture Decision Records

Cada ADR registra una decisión que precisa o modifica `docs/spec/OmniCore-v1.md`. Si un ADR contradice la spec, prevalece el ADR. Si dos ADR se contradicen, prevalece el de número mayor, salvo que el menor tenga una revisión posterior que diga lo contrario.

- **Estados:** Aceptada, Propuesta, Reemplazada por ADR-XXXX.
- **Revisiones:** un ADR revisado conserva su número y deja constancia de la versión anterior en su encabezado ("rev. 1: …").
- **Revisiones arquitectónicas del 2026-09-24:**
  - v0.2: ADRs 0001–0021;
  - v0.3: 0022–0029;
  - v0.4, cliente/TUI: 0030–0034;
  - **v0.5, revisión integral y entrevista:** 0035–0043, con correcciones en ADRs anteriores.
  - **v0.6:** 0044–0045, política operativa por modelo y cuestionarios estructurados, con correcciones en ADRs anteriores.
  - **v0.7 (2026-09-30):** 0046–0047, fronteras pre-M6 (anexo de OmniCoder contrastado con el código; ver `docs/architecture/gap-pre-m6.md`) y modos explícitos con UltraCode.

  Diagramas, tabla de cambios, preguntas abiertas y roadmap: [docs/architecture/arquitectura.md](../architecture/arquitectura.md).

| ADR | Título | Estado |
|---|---|---|
| [0001](0001-canonical-journal.md) | Canonical Journal: Event Store + artifacts inmutables | Aceptada, rev. 2 (+ auditoría sobrevive a la purga) |
| [0002](0002-event-store-sqlite.md) | Event Store en SQLite desde M1 y política de durabilidad | Aceptada, rev. 2 |
| [0003](0003-ask-sin-cliente.md) | `Ask` sin cliente interactivo se resuelve como `Deny` | Aceptada, rev. 3 (vía `InteractionRequest`) |
| [0004](0004-resume-y-effect-journal.md) | Resume y Effect Journal para ToolCalls | Aceptada, rev. 2 (complementado por 0036 §5) |
| [0005](0005-model-runtime-provider-native.md) | Model Runtime provider-native | Aceptada, rev. 2 (+ tabla de propiedad de tipos de modelo) |
| [0006](0006-json-schema.md) | Validación de JSON Schema con librería existente | Aceptada |
| [0007](0007-model-qualification-framework.md) | Model Qualification Framework y EffectiveModelProfile | Aceptada, rev. 2 |
| [0008](0008-sandbox-compartido.md) | Sandbox como librería compartida `OmniCore.Sandbox` | Aceptada, rev. 3 (multiplataforma, multi-target net8; ver 0038) |
| [0009](0009-grafo-de-dependencias.md) | Grafo de dependencias entre proyectos | Aceptada, rev. 3 (+ `OmniCore.Client`) |
| [0010](0010-plataforma-y-tooling.md) | .NET 10, tooling de build y tests | Aceptada (+ excepción net8, AOT, YAML) |
| [0011](0011-conexion-a-proveedores.md) | Mecanismo de conexión a proveedores de modelos | Aceptada, rev. 3 (+ protocolo Anthropic de Claude Code, §10) |
| [0012](0012-lanes-delegadas-a-claude-code.md) | Lanes delegadas a Claude Code con la suscripción del usuario | Aceptada, rev. 3 (M7, riesgo residual aceptado) |
| [0013](0013-event-schema-vs-protocol.md) | Versión de schema de evento separada del Omni Protocol | Aceptada (+ nombres, `EventType`, ids de DTOs) |
| [0014](0014-pipeline-tool-permission-execution.md) | Pipeline Tool → Permission → Execution | Aceptada |
| [0015](0015-process-runtime.md) | Process Runtime unificado: `process.exec` como primitive | Aceptada, rev. 2 (sandbox según 0038) |
| [0016](0016-plan-y-working-state.md) | Plan/Todo canónico, WorkingState y ProgressReconciler | Aceptada (transiciones en 0036, modos en 0035) |
| [0017](0017-execution-fingerprint.md) | ExecutionFingerprint por Turn | Aceptada |
| [0018](0018-secretos-y-redaccion.md) | Gestión de secretos y redacción | Aceptada |
| [0019](0019-cli-transport-host.md) | CLI → IOmniClient / IOmniTransport → Host → Engine | Aceptada |
| [0020](0020-hooks-trust-capabilities.md) | Modelo de confianza y capacidades de hooks | Aceptada, rev. 2 (niveles unificados con 0023; implementación en M8) |
| [0021](0021-git-worktree-y-workspace-sucio.md) | GitWorktree isolation con workspace sucio | Aceptada (implementación en M7) |
| [0022](0022-modelo-de-scopes.md) | Modelo de scopes, identidades y estrategias de resolución | Aceptada (rutas en 0039, grants por `WorkspaceId`) |
| [0023](0023-modelo-de-extensiones.md) | Modelo de extensiones, confianza unificada y MCP | Aceptada (implementación en M8) |
| [0024](0024-commands.md) | Commands como subsistema formal | Aceptada |
| [0025](0025-client-actions-keybindings.md) | Client Actions, KeyBindingService y Command Palette | Aceptada, rev. 2 (solo cliente) |
| [0026](0026-skills.md) | Skills: scopes, precedencia y ciclo de vida | Aceptada (implementación en M8) |
| [0027](0027-tool-sources-y-precedencia.md) | Origen de tools y precedencia en el ToolCatalog | Aceptada (+ `MaxEffect`) |
| [0028](0028-memory.md) | Memory como servicio externo; separación Memory / Knowledge / Skills | Aceptada, rev. 2 (toda la memoria en v1) |
| [0029](0029-procedencia-de-contexto-y-diagnosticos.md) | Procedencia de contexto y diagnósticos | Aceptada |
| [0030](0030-arquitectura-del-cliente.md) | Arquitectura del cliente: `OmniCore.Client`, ClientProjection, renderers | Aceptada |
| [0031](0031-layout-status-line-y-tema.md) | Layout, header, status line (`UsageSnapshot`), responsive y tema | Aceptada |
| [0032](0032-sidebar-y-widgets.md) | SidebarHost y contrato de widgets | Aceptada |
| [0033](0033-conversacion-y-composer.md) | Render de conversación y composer con referencias estructuradas | Aceptada |
| [0034](0034-interaction-requests.md) | InteractionRequest: human-in-the-loop como protocolo y overlays | Aceptada (+ eventos de dominio y kinds nuevos) |
| [0035](0035-conversacion-run-y-modos.md) | Conversación ↔ Run, modos (PLAN → ACT) y cancelación | Aceptada |
| [0036](0036-maquinas-de-estado.md) | Máquinas de estado canónicas y eventos por transición | Aceptada |
| [0037](0037-modelo-de-permisos.md) | Modelo de permisos: tipos, capas, defaults autónomos, grants y gasto | Aceptada |
| [0038](0038-plataformas-sandbox-y-distribucion.md) | Plataformas (Windows + Linux), sandbox, distribución y multi-target | Aceptada |
| [0039](0039-workspace-trust-y-configuracion.md) | Confianza de workspace y configuración YAML por scope | Aceptada |
| [0040](0040-localizacion.md) | Localización: UI localizable, español por defecto | Aceptada |
| [0041](0041-simulacion-m1.md) | Simulación de M1: `omni sim` y componentes mínimos | Aceptada |
| [0042](0042-tokens-y-presupuesto-de-contexto.md) | Conteo de tokens y política de contexto antes de M4 | Aceptada |
| [0043](0043-auditoria-y-telemetria.md) | Auditoría y telemetría | Aceptada |
| [0044](0044-politica-de-modelos-y-mutaciones.md) | Política de modelos, onboarding y seguridad de mutaciones | Aceptada |
| [0045](0045-cuestionarios-estructurados.md) | Cuestionarios estructurados y respuestas humanas | Aceptada |
| [0046](0046-fronteras-pre-m6.md) | Fronteras pre-M6: ejecución, routing autorizado, eventos y control | Aceptada (con ajustes al anexo, §8) |
| [0047](0047-modos-explicitos-y-ultracode.md) | Modos explícitos, ejecución directa y esfuerzo UltraCode | Aceptada (implementación en M5.5; delegación real en M6) |
