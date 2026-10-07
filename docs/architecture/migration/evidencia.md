# Evidencia de migración OmniCoder → OmniCore

Fecha: 2026-10-01. Informe de **inspección estática focalizada**, no certificación AS-IS completa ni prueba de paridad.

## Baseline y autoridad

- TO-BE: [arquitectura](../arquitectura.md), ADR-0001–0047, spec; ADRs prevalecen. OmniCore HEAD `2e3277c3a267deaa35f700df24ccff287ef7c9ae` más cambios documentales locales de ADR-0047. No se cambia el roadmap.
- C#: `C:/Users/juanc/source/repos/OmniCoder/`, HEAD `ef7e747a295a8d23b431c0070d479cab1827e0d5`; la inspección usa archivos del working tree, no certifica que sean idénticos al commit.
- Electron/TS: `C:/Users/juanc/Documents/JayCoder/`, HEAD `d69e43bc84aee9f83ee8462d8894c7442e983866`. Su AGENTS.md confirma que JayCoder es el fork TypeScript de OmniCoder. Inspección del working tree; sin mutaciones.
- Los adjuntos AS-IS localizados (`6d03bed6...` y `62e675e6...`) contienen **prompts de auditoría, no reportes terminados**. No se atribuyen conclusiones a informes inexistentes. [Plan de transición C#](C:/Users/juanc/source/repos/OmniCoder/docs/PLAN_OMNICODER_SOBRE_OMNICORE.md) es documentación auxiliar, no sustituye TO-BE ni prueba código TS.
- Cobertura: 86 unidades en la [matriz](capability-transition-matrix.md). Un campo UNKNOWN significa que no se demostró la equivalencia, no que se haya demostrado inexistencia en todo el repositorio.
- No se ejecutaron suites, builds, Pi, proveedores ni aplicaciones. Tests abajo son activos localizados y casos inspeccionados, **no tests verdes certificados en esta revisión**. Se necesita paridad ejecutada antes de extracción/integración/retiro.

## Nivel de evidencia

Se inspeccionaron cuerpos seleccionados de journal/state machine/plan validator, promoción/executor, sandbox contract, delivery store, guards/intervention, usage, selector/assembler, framing, Pi/kernel, routing, server y composition roots. Para cada familia se incluyen rutas concretas; no implica que todas sus clases o todos sus tests se leyeran completos. LSP, diff y extensibility conservan decisiones limitadas/UNKNOWN donde solo se localizó el contrato o la suite.

Composition comprobada: C# `CompositionRoot.CreateOrchestrationApplicationService` conecta executor, validator, usage, worktrees y promotion; `App.xaml.cs` crea DelegationApplicationService. PiRpcAgentRuntime crea AgentExecutionGuard/InterventionController y broker AppContainer. TS `packages/worker/src/cli.ts` crea WorkerBridgeSupervisorService; Electron main compone GoalService y runtimes/Pi stores. `server.ts` crea sus sesiones/runtime; no se demostró que Electron consuma ese servidor, así que no se presenta HTTP como recorrido productivo único.

## Comparaciones que cambian la selección del donante

1. **Journal — C# más próximo en durabilidad física:** AtomicWriteAsync utiliza WriteThrough + Flush(flushToDisk:true); TS atomicWrite usa writeFileSync/renameSync sin fsync en el cuerpo inspeccionado. Ambos aportan crash event-before-snapshot y contigüidad. Ninguno sustituye SQLite/barriers/CAS actuales.
2. **Mailbox — C# más próximo en persistencia del delivery:** Pending→Dispatching→Acknowledged, id determinista run/task/revisión y parentSessionId explícito. TS BridgeRecord tiene Maps/Promise y review receipts; no se demostró ACK durable de padre equivalente. No se porta el arranque limpio ante journal corrupto al Core.
3. **Worktrees — ambos:** C# aporta PromotionRecovery, baseline/branch/token/ArtifactSet comprobados; TS aporta tests de blob exacto con CRLF, archivos untracked y repo unborn. Esto justifica importar casos TS aunque la implementación destino sea C#.
4. **AppContainer — C#:** launcher/Job Object/broker productivos; TS isolation.ts solo define IsolationBackend y wrap, no demuestra aislamiento kernel equivalente.
5. **Permisos — ninguno como motor completo:** TS evaluate usa findLast y defaults Ask; OmniCore combina por mínimo y autoriza intents tipados. Se aprovechan wildcard/casos negativos, se reemplaza la semántica de precedencia.
6. **Framer — ambos equivalentes en algoritmo observado:** LF único, CR final, fragmentación, límite y U+2028/U+2029. C# ReadOnlySpan/StringBuilder evita traducción mecánica TS; TS tests aportan corpus portátil. Overflow en transporte canónico debe reportar error/reconectar, nunca éxito tras perder evento.
7. **Contexto — ambos parciales:** C# ContextAssembler controla budget de Knowledge y preserva prefijo; TS registry incorpora source/trust/budget y ranking lexical. No se demostró vector DB ni ContextInheritancePolicy canónica: no afirmar RAG vectorial ni copia de transcript como solución.
8. **Producto — TS:** renderer/preload/IPC y E2E de ventana son activos del producto Electron. C# ViewModels se inspeccionan para descubrir comportamiento/política, no para llevar WPF al Core.

## Familias y referencias concretas

<a id="life"></a>

### life — Lifecycle y coordinación

- C# source: [src/OmniCoder.Core/Orchestration/OrchestrationStateMachine.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/OrchestrationStateMachine.cs); [src/OmniCoder.Core/Orchestration/OrchestrationCoordinator.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/OrchestrationCoordinator.cs); [src/OmniCoder.Presentation/CompositionRoot.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Presentation/CompositionRoot.cs)
- TS source: [packages/core/src/orchestration/state-machine.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/orchestration/state-machine.ts); [packages/core/src/goals/goal-scheduler.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/goals/goal-scheduler.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs); [tests/OmniCoder.Core.Tests/OrchestrationCoordinatorTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/OrchestrationCoordinatorTests.cs)
- TS tests candidatos: [packages/core/test/orchestration.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/orchestration.test.ts); [packages/core/test/goals-domain.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/goals-domain.test.ts)
- Alcance de reuse: Rechazo de transiciones ilegales, dependencias no liberadas prematuramente y separación producir/validar/aceptar/promover. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="dag"></a>

### dag — Grafo y validación pura

- C# source: [src/OmniCoder.Core/Orchestration/OrchestrationPlanValidator.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/OrchestrationPlanValidator.cs)
- TS source: [packages/core/src/orchestration/plan-validator.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/orchestration/plan-validator.ts); [packages/core/src/goals/goal-contracts.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/goals/goal-contracts.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/OrchestrationPlanValidatorTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/OrchestrationPlanValidatorTests.cs)
- TS tests candidatos: [packages/core/test/orchestration.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/orchestration.test.ts); [packages/core/test/goals-domain.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/goals-domain.test.ts)
- Alcance de reuse: DFS visiting/visited, detección de ciclos, referencias inexistentes y ids duplicados. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="pi"></a>

### pi — Compatibilidad Pi e input

- C# source: [src/OmniCoder.Core/Rpc/PiRpcClient.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Rpc/PiRpcClient.cs); [src/OmniCoder.Core/Orchestration/Execution/PiRpcAgentRuntime.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Execution/PiRpcAgentRuntime.cs); [src/OmniCoder.Core/Orchestration/Execution/IAgentRuntime.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Execution/IAgentRuntime.cs)
- TS source: [packages/core/src/rpc/pi-rpc-client.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/rpc/pi-rpc-client.ts); [packages/core/src/hosting/agent-runtime.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/hosting/agent-runtime.ts); [packages/core/src/kernel/kernel.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/kernel/kernel.ts); [packages/core/src/kernel/pi-kernel.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/kernel/pi-kernel.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/PiRpcClientTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/PiRpcClientTests.cs); [tests/OmniCoder.Core.Tests/Orchestration/PiRpcToolGatingTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Orchestration/PiRpcToolGatingTests.cs)
- TS tests candidatos: [packages/core/test/pi-rpc-e2e.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/pi-rpc-e2e.test.ts); [packages/core/test/kernel.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/kernel.test.ts); [packages/core/test/runtime.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/runtime.test.ts)
- Alcance de reuse: ACK correlacionado, cancelación, mensajes sin pérdida, error explícito y respuesta a UI pendiente. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="journal"></a>

### journal — Journal y recuperación

- C# source: [src/OmniCoder.Core/Orchestration/OrchestrationJournalStore.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/OrchestrationJournalStore.cs)
- TS source: [packages/core/src/orchestration/journal-store.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/orchestration/journal-store.ts); [packages/core/src/goals/goal-store.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/goals/goal-store.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/OrchestrationJournalTests.cs)
- TS tests candidatos: [packages/core/test/orchestration.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/orchestration.test.ts); [packages/core/test/goals-domain.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/goals-domain.test.ts)
- Alcance de reuse: Sequence contigua, evento durable antes de snapshot, replay idempotente y rechazo de snapshot adelantado. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="artifact"></a>

### artifact — Artifacts y hashes

- C# source: [src/OmniCoder.Core/Orchestration/Artifacts/ArtifactSet.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Artifacts/ArtifactSet.cs)
- TS source: [packages/core/src/orchestration/artifact-set.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/orchestration/artifact-set.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/ArtifactSetStoreTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/ArtifactSetStoreTests.cs)
- TS tests candidatos: [packages/core/test/orchestration.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/orchestration.test.ts); [packages/core/test/worker-bridge.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/worker-bridge.test.ts)
- Alcance de reuse: Hash SHA-256 de patch congelado, manifiesto ordenado y verificación de contenido antes de promoción. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="delivery"></a>

### delivery — Entrega, ACK y mailbox

- C# source: [src/OmniCoder.Core/Orchestration/Services/DelegationApplicationService.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Services/DelegationApplicationService.cs); [src/OmniCoder.Core/Orchestration/Services/DelegationDeliveryStore.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Services/DelegationDeliveryStore.cs); [src/OmniCoder.App/App.xaml.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.App/App.xaml.cs)
- TS source: [packages/core/src/worker-bridge/service.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/worker-bridge/service.ts); [packages/core/src/worker-bridge/contracts.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/worker-bridge/contracts.ts); [packages/worker/src/cli.ts](C:/Users/juanc/Documents/JayCoder/packages/worker/src/cli.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/DelegationDeliveryTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/DelegationDeliveryTests.cs); [tests/OmniCoder.Core.Tests/DelegationDeliveryIntegrationTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/DelegationDeliveryIntegrationTests.cs)
- TS tests candidatos: [packages/core/test/worker-bridge.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/worker-bridge.test.ts)
- Alcance de reuse: Resultado guardado antes de notificar, deliveryId determinista por revisión, ACK durable y sesión padre explícita. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="worktree"></a>

### worktree — Worktrees, promoción y undo

- C# source: [src/OmniCoder.Core/Orchestration/OrchestrationWorktreeManager.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/OrchestrationWorktreeManager.cs); [src/OmniCoder.Core/Orchestration/Promotion/ProductionResultPromotionService.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Promotion/ProductionResultPromotionService.cs); [src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs)
- TS source: [packages/core/src/orchestration/worktree-manager.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/orchestration/worktree-manager.ts); [packages/core/src/worker-bridge/service.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/worker-bridge/service.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/OrchestrationWorktreeManagerTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/OrchestrationWorktreeManagerTests.cs); [tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs)
- TS tests candidatos: [packages/core/test/worktree.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/worktree.test.ts); [packages/core/test/worker-bridge.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/worker-bridge.test.ts)
- Alcance de reuse: Baseline/branch/patch verificados, preview antes de aplicar, recovery durable, conflicto explícito y protección de cambios ajenos. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="sandbox"></a>

### sandbox — Sandbox y broker

- C# source: [src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs); [src/OmniCoder.Core/Orchestration/Sandbox/SandboxBridgeEndpoint.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Sandbox/SandboxBridgeEndpoint.cs); [src/OmniCoder.Core/Orchestration/Sandbox/AppContainerSandboxWorker.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Sandbox/AppContainerSandboxWorker.cs); [src/OmniCoder.Core/Interop/AppContainerProcessLauncher.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Interop/AppContainerProcessLauncher.cs); [src/OmniCoder.Core/Interop/ProcessJobObject.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Interop/ProcessJobObject.cs)
- TS source: [packages/core/src/platform/isolation.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/platform/isolation.ts); [packages/worker/src/security.ts](C:/Users/juanc/Documents/JayCoder/packages/worker/src/security.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs); [tests/OmniCoder.Core.Tests/SandboxBridgeEndpointTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/SandboxBridgeEndpointTests.cs); [tests/OmniCoder.Core.Tests/AppContainerSandboxWorkerTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/AppContainerSandboxWorkerTests.cs)
- TS tests candidatos: [packages/worker/test/security.test.ts](C:/Users/juanc/Documents/JayCoder/packages/worker/test/security.test.ts)
- Alcance de reuse: ACL/Job Object, límites físicos, validación antes de tocar disco, version hash para reemplazo y rechazo de escape. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="process"></a>

### process — Procesos y framing

- C# source: [src/OmniCoder.Core/Processes/AgentProcessManager.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Processes/AgentProcessManager.cs); [src/OmniCoder.Core/Processes/NewlineFramer.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Processes/NewlineFramer.cs); [src/OmniCoder.Core/Interop/ProcessJobObject.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Interop/ProcessJobObject.cs)
- TS source: [packages/core/src/processes/agent-process-manager.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/processes/agent-process-manager.ts); [packages/core/src/processes/newline-framer.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/processes/newline-framer.ts); [packages/core/src/platform/process-tree.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/platform/process-tree.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/AgentProcessManagerIntegrationTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/AgentProcessManagerIntegrationTests.cs); [tests/OmniCoder.Core.Tests/NewlineFramerTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/NewlineFramerTests.cs)
- TS tests candidatos: [packages/core/test/framer.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/framer.test.ts); [packages/worker/test/process-tree.test.ts](C:/Users/juanc/Documents/JayCoder/packages/worker/test/process-tree.test.ts)
- Alcance de reuse: Framing solo LF, CRLF, chunks/U+2028, control de overflow y kill de árbol. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="permission"></a>

### permission — Permisos

- C# source: [src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Sandbox/SandboxToolContract.cs); [src/OmniCoder.Core/Orchestration/OrchestrationPlanValidator.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/OrchestrationPlanValidator.cs)
- TS source: [packages/core/src/permission/permission-service.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/permission/permission-service.ts); [packages/core/src/security/plan-mode-policy.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/security/plan-mode-policy.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/SandboxToolPolicyValidatorTests.cs)
- TS tests candidatos: [packages/core/test/permission.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/permission.test.ts); [packages/worker/test/security.test.ts](C:/Users/juanc/Documents/JayCoder/packages/worker/test/security.test.ts)
- Alcance de reuse: No efectos antes de autorización, scopes hijos subconjunto y Ask sin respuesta no es Allow. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="model"></a>

### model — Modelos y routing

- C# source: [src/OmniCoder.Core/Orchestration/Model/RoleModelRouter.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Model/RoleModelRouter.cs); [src/OmniCoder.Core/Orchestration/Model/ModelRouting.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Model/ModelRouting.cs)
- TS source: [packages/core/src/models/router.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/models/router.ts); [packages/core/src/providers/model-effort-capabilities.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/providers/model-effort-capabilities.ts); [packages/core/src/providers/provider-probe.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/providers/provider-probe.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/Orchestration/RoleModelRouterTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Orchestration/RoleModelRouterTests.cs); [tests/OmniCoder.Core.Tests/Orchestration/ModelRoutingTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Orchestration/ModelRoutingTests.cs)
- TS tests candidatos: [packages/core/test/router.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/router.test.ts)
- Alcance de reuse: Mapeo explícito por rol y clases de coste observables; capacidades separadas de UI. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="budget"></a>

### budget — Presupuestos y usage

- C# source: [src/OmniCoder.Core/Orchestration/Usage/OrchestrationUsageMeter.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Usage/OrchestrationUsageMeter.cs)
- TS source: [packages/core/src/goals/goal-scheduler.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/goals/goal-scheduler.ts); [packages/core/src/providers/usage-parsers.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/providers/usage-parsers.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/OrchestrationUsageMeterTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/OrchestrationUsageMeterTests.cs)
- TS tests candidatos: [packages/core/test/goals-domain.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/goals-domain.test.ts); [packages/core/test/usage.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/usage.test.ts)
- Alcance de reuse: Reserva previa con precio observado, liberación/commit idempotente y falla cerrada con precio desconocido. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="context"></a>

### context — Contexto y Knowledge

- C# source: [src/OmniCoder.Core/Knowledge/KnowledgePlane.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Knowledge/KnowledgePlane.cs); [src/OmniCoder.Core/Knowledge/KnowledgeSelector.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Knowledge/KnowledgeSelector.cs); [src/OmniCoder.Core/Knowledge/ContextAssembler.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Knowledge/ContextAssembler.cs); [src/OmniCoder.Presentation/ViewModels/MainViewModel.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Presentation/ViewModels/MainViewModel.cs)
- TS source: [packages/core/src/knowledge/registry.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/knowledge/registry.ts); [packages/core/src/kernel/pi-kernel.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/kernel/pi-kernel.ts); [packages/core/src/plan/plan-memory.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/plan/plan-memory.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/Knowledge/KnowledgeSelectorTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Knowledge/KnowledgeSelectorTests.cs); [tests/OmniCoder.Core.Tests/Knowledge/KnowledgeRegistryTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Knowledge/KnowledgeRegistryTests.cs); [tests/OmniCoder.Core.Tests/Orchestration/ContextBudgetTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Orchestration/ContextBudgetTests.cs)
- TS tests candidatos: [packages/core/test/knowledge.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/knowledge.test.ts); [packages/core/test/plan-memory.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/plan-memory.test.ts)
- Alcance de reuse: Presupuesto de selección, fuentes/trust, separación datos/instrucciones y prefijo estable. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="guard"></a>

### guard — Guards y recuperación acotada

- C# source: [src/OmniCoder.Core/Orchestration/Execution/AgentExecutionGuard.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Execution/AgentExecutionGuard.cs); [src/OmniCoder.Core/Orchestration/Execution/ProgressDetector.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Execution/ProgressDetector.cs); [src/OmniCoder.Core/Orchestration/Execution/InterventionController.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Execution/InterventionController.cs)
- TS source: [packages/core/src/scaffold/guards.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/scaffold/guards.ts); [packages/worker/src/hooks/baseline.ts](C:/Users/juanc/Documents/JayCoder/packages/worker/src/hooks/baseline.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/ProgressDetectorTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/ProgressDetectorTests.cs); [tests/OmniCoder.Core.Tests/Orchestration/RuntimeGuardTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Orchestration/RuntimeGuardTests.cs); [tests/OmniCoder.Core.Tests/Orchestration/InterventionControllerTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Orchestration/InterventionControllerTests.cs)
- TS tests candidatos: [packages/worker/test/security-hooks.test.ts](C:/Users/juanc/Documents/JayCoder/packages/worker/test/security-hooks.test.ts)
- Alcance de reuse: Límites host-owned, separar transporte vivo de progreso, retry acotado y cancelación terminal. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="validation"></a>

### validation — Validación y aceptación

- C# source: [src/OmniCoder.Core/Orchestration/Validation/ProductionOrchestrationTaskValidator.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Validation/ProductionOrchestrationTaskValidator.cs); [src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Orchestration/Execution/ProductionOrchestrationTaskExecutor.cs)
- TS source: [packages/core/src/goals/goal-scheduler.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/goals/goal-scheduler.ts); [packages/core/src/worker-bridge/service.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/worker-bridge/service.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/Orchestration/OrchestrationValidatorBehaviorTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Orchestration/OrchestrationValidatorBehaviorTests.cs); [tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/Orchestration/OrchestrationPromotionTests.cs)
- TS tests candidatos: [packages/core/test/goals-domain.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/goals-domain.test.ts); [packages/core/test/worker-bridge.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/worker-bridge.test.ts); [packages/worker/test/no-runtime-fails-loud.test.ts](C:/Users/juanc/Documents/JayCoder/packages/worker/test/no-runtime-fails-loud.test.ts)
- Alcance de reuse: Evidencia congelada, validator independiente, no promover dos veces y no aparentar éxito sin runtime/servicio. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="protocol"></a>

### protocol — Protocol/Host

- C# source: [src/OmniCoder.Core/Rpc/PiRpcClient.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Rpc/PiRpcClient.cs); [src/OmniCoder.Core/Processes/NewlineFramer.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Processes/NewlineFramer.cs)
- TS source: [packages/protocol/src/index.ts](C:/Users/juanc/Documents/JayCoder/packages/protocol/src/index.ts); [packages/server/src/server.ts](C:/Users/juanc/Documents/JayCoder/packages/server/src/server.ts); [packages/app/src/preload/index.ts](C:/Users/juanc/Documents/JayCoder/packages/app/src/preload/index.ts); [packages/app/src/main/index.ts](C:/Users/juanc/Documents/JayCoder/packages/app/src/main/index.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/PiRpcClientTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/PiRpcClientTests.cs); [tests/OmniCoder.Core.Tests/NewlineFramerTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/NewlineFramerTests.cs)
- TS tests candidatos: [packages/server/test/server.test.ts](C:/Users/juanc/Documents/JayCoder/packages/server/test/server.test.ts); [packages/core/test/framer.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/framer.test.ts); [packages/app/test/e2e/workbench.e2e.test.ts](C:/Users/juanc/Documents/JayCoder/packages/app/test/e2e/workbench.e2e.test.ts)
- Alcance de reuse: DTOs sin tipos de renderer, requestId correlacionado, capabilities explícitas y runtime unavailable visible. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="product"></a>

### product — Producto y proyecciones

- C# source: [src/OmniCoder.Presentation/ViewModels/MainViewModel.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Presentation/ViewModels/MainViewModel.cs); [src/OmniCoder.Presentation/CompositionRoot.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Presentation/CompositionRoot.cs); [src/OmniCoder.App/App.xaml.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.App/App.xaml.cs)
- TS source: [packages/app/src/main/index.ts](C:/Users/juanc/Documents/JayCoder/packages/app/src/main/index.ts); [packages/app/src/preload/index.ts](C:/Users/juanc/Documents/JayCoder/packages/app/src/preload/index.ts); [packages/app/src/renderer/app.tsx](C:/Users/juanc/Documents/JayCoder/packages/app/src/renderer/app.tsx); [packages/core/src/threading/coalescers.ts](C:/Users/juanc/Documents/JayCoder/packages/core/src/threading/coalescers.ts)
- C# tests candidatos: [tests/OmniCoder.Core.Tests/MainViewModelOrchestrationRoutingTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/MainViewModelOrchestrationRoutingTests.cs)
- TS tests candidatos: [packages/app/test/e2e/workbench.e2e.test.ts](C:/Users/juanc/Documents/JayCoder/packages/app/test/e2e/workbench.e2e.test.ts); [packages/app/test/e2e/goals.e2e.test.ts](C:/Users/juanc/Documents/JayCoder/packages/app/test/e2e/goals.e2e.test.ts); [packages/core/test/coalescer.test.ts](C:/Users/juanc/Documents/JayCoder/packages/core/test/coalescer.test.ts)
- Alcance de reuse: IPC/renderer desacoplado de secretos, UI real, descarte de flush obsoleto y experiencia de preguntas. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="lsp"></a>

### lsp — LSP y diagnóstico

- C# source: [src/OmniCoder.Core/Lsp/LspLanguageClient.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Lsp/LspLanguageClient.cs); [src/OmniCoder.Core/Lsp/LspProtocol.cs](C:/Users/juanc/source/repos/OmniCoder/src/OmniCoder.Core/Lsp/LspProtocol.cs)
- TS source: No demostrado.
- C# tests candidatos: [tests/OmniCoder.Core.Tests/LspProtocolTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/LspProtocolTests.cs); [tests/OmniCoder.Core.Tests/LspLanguageClientEndToEndTests.cs](C:/Users/juanc/source/repos/OmniCoder/tests/OmniCoder.Core.Tests/LspLanguageClientEndToEndTests.cs)
- TS tests candidatos: No localizado equivalente en esta inspección.
- Alcance de reuse: Diagnósticos por documento y protocolo de language server. No se traslada automáticamente la API ni se da por demostrada su composición completa.

<a id="unknown"></a>

### unknown — Donante no demostrado

- C# source: No demostrado.
- TS source: No demostrado.
- C# tests candidatos: No localizado equivalente en esta inspección.
- TS tests candidatos: No localizado equivalente en esta inspección.
- Alcance de reuse: Sin implementación equivalente demostrada; solo requisitos aprobados. No se traslada automáticamente la API ni se da por demostrada su composición completa.

## Tests como activos: selección concreta

| Test/caso inspeccionado | Clase | Destino y razón |
|---|---|---|
| OrchestrationJournalTests.Resume_is_idempotent_and_preserves_exact_state_and_sequence | ADAPT | OmniCore.Tests, SQLite/replay; conserva idempotencia sin copiar snapshot propietario |
| Journal_rejects_duplicate_run_non_contiguous_event_and_path_escape_id | ADAPT | EventStore/JournalVerifier: contigüidad y paths; no solo JSON filesystem |
| Produced_artifacts_and_integral_hash_survive_journal_resume | ADAPT | Journal+CAS, con refs y verificación de hash |
| DelegationDeliveryTests.El_resultado_duplicado_no_crea_segunda_entrega | ADAPT | M6 mailbox: identidad por revisión |
| Crash_antes_del_ack_retienen_y_el_reintento_es_legitimo | ADAPT | M6 delivery durable/restart |
| El_ack_de_una_entrega_que_nadie_reclamo_se_rechaza | ADAPT | M6 control de owner/lease y ACK |
| El_diario_corrupto_no_bloquea_y_el_store_arranca_limpio | REPLACE | No arrancar vacío ante pérdida de resultados canónicos; error observable, recuperación/inspección |
| OrchestrationUsageMeterTests.Reservations_are_atomic_across_meter_instances | ADAPT | RunBudgetPool; agregar procesos distintos, el original solo prueba instancias en proceso |
| Commit_reconciles_actual_tokens_and_is_idempotent_after_restart | ADAPT | Settlement por ModelStep sin sumar de nuevo ModelCompleted |
| Unknown_price_fails_closed_before_creating_a_reservation | ADAPT | Consentimiento por ModelRoute/BillingMode |
| OrchestrationPromotionTests.The_same_result_cannot_be_promoted_twice | ADAPT | M7 promoción idempotente y atribución |
| A_dependent_task_waits_for_promotion_not_merely_for_acceptance | ADAPT | Join/completion según integración requerida |
| TS worktree.test: producePatch modificado+untracked/blob exacto, promote integral CRLF, unborn y conflicto | PORT | C# tests con Git real, no snapshots de texto normalizado |
| TS framer.test: chunks, CRLF, U+2028/U+2029, oversized y flush | PORT | Transport/process, corpus de caracteres y límites |
| TS permission.test: última regla coincidente gana | REPLACE | Expectativas restrictivas de ADR-0037; no copiar política opencode |
| TS permission.test: deny falla cerrado/default ask | PORT | Casos de interacción; adaptar defaults según operación/scopes OmniCore |
| TS coalescer.test: discard invalida flush encolado | COPY | Permanece TS en cliente; no portar UI dispatcher a Engine |
| TS goals-domain.test: DAG, idempotency, presupuesto, store corrupto y fake runner | ADAPT | Fixtures al dominio; runner fake solo prueba control, no inferencia/cableado real |
| TS server.test: health/capabilities sin Electron, runtime ausente explícito, prompt/eventos | PORT | Host stdio/Protocol; no imponer HTTP/SSE |
| TS app/test/e2e workbench/goals/preferences | ADAPT | Quedan en Electron, cambiar adapter manteniendo ventana real |
| PiRpc ACK/parser exacto y types privados Pi | DROP | Solo al retirar ese adapter; reemplazo debe tener tests de Protocol/input/timeout, no conservar acoplamiento por compatibilidad falsa |

COPY conserva test en su lenguaje/capa; PORT cambia lenguaje/framework; ADAPT cambia contrato con garantía conservada; REPLACE cambia semántica; DROP elimina una comprobación exclusiva de una dependencia retirada, nunca una garantía sin reemplazo.

## ARCHITECTURAL DELTA CANDIDATE (sin incorporar)

| ID | Source / observado | Cobertura pendiente del plan / recomendación | Impacto y decisión requerida |
|---|---|---|---|
| D1 | Journal/artifact redaction frente a bytes opacos y pre-imágenes | Opaque replay/undo exactos no deben pasar por redactor de texto. Definir almacenamiento protegido/visibilidad/replay y prohibición de exponer secretos | Critical; resolver contrato ADR-0018/0046/M7, no desactivar redacción unilateralmente |
| D2 | Framer legacy descarta línea oversized y continúa | Si la línea era evento durable, continuar sin marcar gap pierde causalidad. Error typed/reconnect desde sequence; logs no canónicos pueden descartar con contador | High; contrato de transporte M9 debe decidir tratamiento antes de adoptar helper |
| D3 | Pi kernel referencia tipos Pi de imágenes/UI y runtime ejecuta tools propios | Compatibilidad no equivale al pipeline/barrier nativo. Delimitar adapter del producto, capabilities efectivas y restricciones de escritura; no ofrecer native guarantees no demostradas | Critical; aprobar frontera concreta antes de enchufar Pi como runtime Core |
| D4 | Hash ArtifactSet TS usa localeCompare; CAS/undo exige bytes exactos | Formato interlenguaje canónico, orden ordinal, encoding/BOM/CRLF y hashing de bytes necesitan fixtures dorados | High; decisión de formato importer/evidence; no cambiar hashes históricos en sitio |

No se modifican ADRs ni se aceptan automáticamente estos deltas.

## FLAG CONFLICT y pendientes externos

- El encargo dice no crear M5.5; el baseline ya lo tiene aprobado en ADR-0046. Se conserva. PRE-M6 MIGRATION es etiqueta de trabajo, no nuevo hito. Cualquier supresión de M5.5 necesita decisión del propietario.
- Plan C# de producto aún presenta UI WPF como sobreviviente; el encargo actual define Electron como destino. Se conserva WPF durante transición, se marca retiro sujeto a paridad, sin borrar ni modificar ese repo.
- Faltan los dos reportes AS-IS terminados. Adjuntarlos permite reconciliar esta matriz con su coverage y fuentes. También falta ejecución de paridad, revisión profunda de diff/LSP/extension host y auditoría de datos reales de usuario.
