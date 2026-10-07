# C. Retirement Map y arquitectura de transición

Fecha: 2026-10-01. [Matriz A](capability-transition-matrix.md), [anotaciones B](roadmap-anotado.md), [evidencia/límites](evidencia.md).

RETIRE significa destino posterior, **no autorización para borrar hoy**. No se eliminaron datos, procesos, repos ni dependencias. Este mapa mantiene hitos existentes y no certifica paridad todavía.

## Arquitectura transitoria

```text
A — actual (dos productos/hosts existentes, no fusionados)
    WPF → aplicación C# → Pi / executor local / orquestación legacy
    Electron → main/preload → Pi AgentRuntime / WorkerBridge / Goal
    OmniCore nativo se desarrolla y valida por CLI Lab

B — corte por backend/capability, no big-bang (integración M9)
    Electron → adapter de producto → Omni Protocol → OmniCore
    Producto conserva Pi compatibility adapter para capacidades pendientes
    Cada Session/Run/ejecución tiene exactamente un backend-owner
    Legacy observa resultados nativos; NO reschedule ni autoriza tools nativas

C — destino tras paridad (M9/M10 y dependencias M6/M7/M8)
    Electron + política Supervisor/Goal → Protocol → OmniCore nativo
    Journal/permissions/scheduler Core son la autoridad de ejecución
    Pi retirado solo para capacidades reemplazadas y certificadas
    UI WPF retirada tras paridad de producto; datos/recovery conservados
```

El adapter Pi es una costura del producto ya contemplada como transición en spec §96–98. Colocarlo como runtime interno de OmniCore con garantías nativas requeriría resolver D3; no se introduce ese contrato aquí. Si Pi puede ejecutar efectos fuera del pipeline/barrier, esa ejecución se declara compatibilidad, no ejecución nativa segura. No puede publicar eventos de éxito nativo inventados ni escribir el journal por fuera del servidor.

## Transferencia de autoridad

- Selector explícito por ejecución con backend/owner/revisión persistidos; no ruta simultánea a Pi y OmniCore.
- Cerrar o suspender de manera segura trabajo legacy; drenar interacciones, entregas, reservas y efectos pendientes. No migrar una Task activa a mitad de efecto.
- Importar snapshots como proyecciones/fixtures, historial como Imported/Partial cuando corresponda. No fabricar causalidad, permisos, ModelStep ni éxito no observado.
- Core recibe commands y es único writer de eventos nativos. Stores legacy quedan consulta/importer para sus ejecuciones históricas, no fuente writable competidora.
- Activar solo con prueba de paridad y fallback explícito. Si hubo efectos nativos, rollback de configuración NO autoriza reejecutarlos en Pi; reconciliar con Effect Journal.
- Backup/export probado antes de retirar datos; respetar retención de artifacts/recovery, secretos y leases. No convertir GC en mecanismo de migración.

## Mapa

| Component | Current owner | Replacement | Disposition principal | Parity criterion | Earliest retirement milestone | Final retirement trigger |
|---|---|---|---|---|---|---|
| UI WPF y wiring de App/MainViewModel | OmniCoder C# Presentation/App | Electron + adapter Protocol | RETAIN TEMPORARILY | Chat/input/modelos/permisos/changes/Git/Knowledge/delegación accesibles en ventana real, sesiones migradas | M9/M10 | Usuario acepta paridad de producto, export/import y recuperación; no quedan flujos exclusivos WPF necesarios |
| Electron renderer/preload/preferences/timeline | OmniCoder TS app/presentation | Mismo producto, adapter nuevo | KEEP IN OMNICODER | Eventos/commands tipados, secretos nunca en renderer; E2E ventana | No aplica | No retirar UI; eliminar solo accesos runtime duplicados |
| TS AgentRuntime/PiAgentKernel | OmniCoder TS core/hosting/kernel | Adapter OmniCoreRuntime del producto | RETAIN TEMPORARILY | Texto/thinking/tools/interactions/cancel/steering/followup/end real | M9, depende M2–M5+PRE-M6 | Backend nativo cubre capabilities usadas, opt-in verificado y ninguna sesión activa depende del kernel antiguo |
| C# PiRpcAgentRuntime/chat directo PiRpcClient | C# Core/Rpc + Presentation | Adapter de producto hacia IOmniClient | RETAIN TEMPORARILY | Turn lógico, error/timeout/cancel, preguntas y uso; no pérdida tras restart | M9 | Regression parity + desacoplar Pi de API pública + preservar históricos |
| Pi provider/auth/models stores | Producto/instalación Pi | Registry/credentials/provider adapters nativos | RETAIN TEMPORARILY | Acceso autenticado real por ruta, reasoning/tools/opaque/usage; sin secretos expuestos | M9, depende M5 | Migración/import consentido, rutas efectivamente disponibles y ninguna config activa dependiente |
| Pi tools/guardas Assets JS | Producto Pi | Tools/Security/Sandbox nativos | RETAIN TEMPORARILY | Permisos previos a efecto, mutations/hash, sandbox real, interruption/reconcile | M9, depende M3/M8 | Todas las tools usadas tienen reemplazo parity; no desactivar protección antes del switch |
| Pi compaction/context/session JSONL | Pi/producto | Context checkpoint + Canonical Journal + import provenance | RETAIN TEMPORARILY | Pinned/overflow, restart, import/export y fidelidad; opaque mismo destino | M9, depende M4+PRE-M6 | Sesiones necesarias importadas o conservadas read-only, no ejecuciones activas legacy |
| Pi bootstrap/installer patches/RPC private tests | Instalador/producto | Packaging nativo + adapter/config | RETIRE | Lanzamiento/rutas/versiones/modelos funcionales sin Pi; tests sustitutos | M10 | Dependencias activas cero y packaging/regression real; DROP solo tests exclusivos Pi con reemplazo |
| TS WorkerBridgeSupervisorService runtime/queue | worker CLI + core WorkerBridge | AgentExecution/CapacityScheduler/mailbox/ResultDisposition + workspace | RETIRE | Delegate→produce→review→rework→accept, failure, restart, ownership y queue; freeze/promote/undo | M9, depende M6/M7 | WorkerBridge es solo fachada o desaparece; nunca crea otro scheduler ni aplica patch por fuera de Core |
| C# OrchestrationCoordinator/StateMachine legacy | C# application composition | Engine/RunControl/PlanService/AgentExecution nativos | RETIRE | State transitions, pause/cancel/dependencies, recoverable work y completion proporcional | M9, depende M6 | Nueva ejecución siempre Core; viejas terminadas/importadas con lectura de recovery |
| Goal scheduler/evidence/control TS | Producto TS | Producto conserva objetivo/policy; ejecución por Core commands | ADAPT | goal→tasks canónicos, idempotency, criterios sin duplicate schedule/accept | M9, depende M6 | Goal no posee lifecycle de ejecución ni implementa segundo TaskGraph; su estado de intención/UI permanece producto |
| TS server.ts SessionRecord/runtime/events owner | Servidor experimental TS | OmniCore Host/Protocol; fachada opcional producto | RETIRE | Negotiation, health unavailable, command correlation, reconnect after sequence y runtime ownership | M9 | No AgentRuntime/Session authority propia; si HTTP se conserva, proxy solo por Protocol aprobado |
| C# journal JSON y TS journal/Goal execution stores duplicados | Subsistemas legacy | SQLite journal + CAS; importers/readers históricos | RETIRE | Sequence/replay/crash equivalentes, refs, redacción y causalidad import explícita | M9 | Autoridad writable exclusivamente Core para nuevas ejecuciones; antiguos datos exportados/retención cumplida |
| ArtifactSet stores legacy | C# artifacts / TS orchestration | CAS + immutable evidence refs | ADAPT | Hash manifiesto/patch y bytes exactos, baseline, corrupción detectada | M7/M9 | Referencias resueltas/importadas y cero recovery pendiente antes de borrar blobs legacy |
| Ledger usage legacy | C# UsageMeter + TS Goal/usage | Gasto ModelStep + pool/gates nativos | RETIRE | Reserve/settle/release, coste observado vs estimado y restart sin doble cuenta | M9, depende PRE-M6/M6 | Todas las reservas cerradas/importadas y único owner por llamada; histórico auditable permanece |
| TS PermissionService / legacy broker approvals | TS core + Pi/C# broker | Security Permission Engine/grants/AuthorizedIntent | RETIRE | Deny restrictivo, scopes, Ask sin cliente, tool approve exactamente una vez | M9, depende M3 | Backend nativo único para permisos; no traducir always TS en grant más amplio |
| Legacy promotion/worktree manager como autoridad | C#/TS workspace services | WorkspaceSnapshotStore/promoción/UndoPlan | RETIRE | Dirty/untracked/CRLF/branch moved/corruption/preview/restart/undo foreign changes | M9, depende M7 | Owner único de efectos, recoveries reconciliados, referencias protegidas |
| Armour/role policy/Supervisor/product preferences | OmniCoder | Mismo producto con commands Core | KEEP IN OMNICODER | Restringe/recomienda sin otorgar tools, cambiar modo fuera de authority ni scheduler LLM | No aplica | No retirar política/UI: adaptar fronteras |
| Knowledge y LSP del producto | OmniCoder C#/TS | Contributor/extension/evidence adapter | ADAPT | Provenance/trust/budget y diagnósticos tipados sin dep Engine de UI/lenguaje | M8/M9 | Cero inyección de contexto competidora; servicios opcionales producto permanecen |

## Invariantes de no pérdida

1. Hash+baseline+preview validados antes de promoción; no aceptar resumen como patch aprobado.
2. Cambios del usuario, branch movida y drift posterior bloquean aplicación/undo destructivos.
3. Intent confirmado antes de efecto, recuperación atribuible y ninguna reejecución por retry/cambio de backend.
4. Resultado durable antes de notificar; ACK idempotente, owner padre real, aceptación y wake separados.
5. Reservas/coste se reconcilian por llamada; no gasto desconocido autoaprobado ni doble cuenta.
6. Interacción permanece pendiente sin respuesta; worker/modelo no inventa aceptación ni modifica modo.
7. Context/opaque/artifacts no se destruyen al compactar, importar o retirar un store.
8. Timeline/topología/Goal observan, no constituyen una autoridad de ejecución alternativa.

## Gate de retiro por capability

Adjuntar source revision + test parity reproducible + recorrido productivo real + pruebas de restart/crash/cancel + export/import/rollback + aprobación del responsable. Mock inferencia prueba flujo, no quality/access real. Mantener backup/readers hasta cumplir retención. Si falta cualquiera de las garantías aplicables, RETAIN TEMPORARILY; no marcar retirada como completada.
