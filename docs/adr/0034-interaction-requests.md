# ADR-0034 — InteractionRequest: human-in-the-loop como protocolo y overlays

- **Estado:** Aceptada — rev. 2 (2026-09-27; cuestionarios tipados en ADR-0045)
- **Generaliza:** los `Ask` de ADR-0003 (permisos, reconciliación, scope del plan, promoción de memoria, conflictos de integración)
- **Relacionado:** ADR-0003, ADR-0004, ADR-0016 §6, ADR-0021, ADR-0028 §6, ADR-0030, ADR-0032
- **Diagrama:** [arquitectura §34](../architecture/arquitectura.md#34-conversación-composer-e-interacciones)

## Decisión

### 1. Un solo mecanismo en el protocolo

Todo `Ask` del runtime se publica como `InteractionRequested` y se resuelve con `RespondToInteraction`. El **servidor decide las opciones**; el cliente solo las muestra.

```csharp
public sealed record InteractionRequest(          // WireEvent: InteractionRequested
    InteractionId Id,
    InteractionKind Kind,                         // Permission | ReconciliationConflict | PlanScopeChange | PlanApproval
                                                  // | MemoryPromotion | IntegrationConflict | WorkspaceTrust
                                                  // | WeakSandboxConsent | BudgetExceeded | Question
    InteractionSubject Subject,
    IReadOnlyList<InteractionOption> Options,     // las que la política permite
    string DefaultOptionId,                       // la más restrictiva (Deny)
    DateTimeOffset? ExpiresAt,                    // al vencer, se aplica PermissionTimeout = Deny (ADR-0003)
    LaneId? Lane, TaskId? Task, PlanItemId? PlanItem,
    int QueuePosition, int QueueLength);

public sealed record InteractionSubject(
    string Operation,                             // "Ejecutar proceso", "Escribir archivo"
    string? ToolOrExecutable,                     // "process.exec · dotnet"
    string? Target,                               // "test OmniCore.slnx" · ruta · host
    RiskLevel Risk,                               // Low | Medium | High | Critical
    string Reason,                                // por qué se pide (capa que resolvió Ask)
    IReadOnlyList<KeyValuePair<string, string>> Details);   // redactados

public sealed record InteractionOption(string Id, LocalizedText Label, OptionIntent Intent, GrantLifetime? Lifetime);
// Intent: Allow | Deny | Choose. Lifetime: Once | Run | Session | Workspace (ADR-0037 §5; "Project" pasa a llamarse Workspace)
// Revisión integral: los textos visibles (Operation, Reason, Label) son LocalizedText (ADR-0040);
// los ids (LaneId, TaskId…) son tipos propios del protocolo (string), no de Domain (ADR-0013).

public sealed record RespondToInteraction(InteractionId Id, string OptionId);  // WireCommand
```

**Rev. 2:** la forma anterior queda como `ChoicePrompt`/`ChoiceResponse`. ADR-0045 generaliza
`InteractionRequest` y `RespondToInteraction` con payloads discriminados. Para `Kind = Question`,
el payload es un `QuestionnairePrompt` con una o varias preguntas y la respuesta es
`QuestionnaireResponse`; no se fuerza un cuestionario dentro de un solo `OptionId`.

- **Permiso típico:** opciones `Allow once`, `Allow for this Run` y `Deny`. `Session` y `Project` solo aparecen si la política del scope lo permite.
- **Validación en el servidor:** el Host verifica que `OptionId` pertenezca a la solicitud vigente. Una respuesta tardía o duplicada se rechaza.
- **Cola:** las solicitudes de Lanes en paralelo se atienden **una a la vez** (ADR-0003), y cada una informa su posición en la cola.

### 2. Presentación

- **`OverlayHost`:** muestra la solicitud activa como overlay modal **sin destruir `MainView`**. La conversación y el sidebar siguen actualizándose detrás.
- **Contenido del overlay:** operación, tool o ejecutable, destino, riesgo (rol por nivel: `Attention` para Medium y `Error` para High/Critical, siempre con texto), razón y detalles. Las opciones se muestran como botones en el orden del servidor, con el default en foco y la tecla de cierre asignada a la opción default, que es `Deny`.
- **En paralelo:**
  - la Lane afectada muestra `WAITING FOR PERMISSION` en el `AgentsWidget` (`Attention`, que la sube arriba; ADR-0032);
  - la conversación registra un `InteractionBlock` (ADR-0033);
  - la status line muestra un contador `! 2 pending` si hay cola.
- **Otros overlays** (`CommandPalette`, `LaneInspector`, `DiffPreview`, `ModelSelector`,
  `ModelPolicySetup`, `ModelPolicies`, `SessionSearch`) usan el mismo `OverlayStack`. Un
  `InteractionRequest` siempre tiene prioridad sobre ellos. El onboarding de ADR-0044 es un flujo
  de preferencias iniciado por el usuario, no un permiso que el modelo pueda responder.
- **Plain renderer:**
  - en una TTY interactiva, pregunta en línea con las mismas opciones;
  - sin TTY o con `--json`, aplica ADR-0003: `Deny` para permisos y riesgo. Para `Question`,
    prevalece ADR-0045: queda durable y el resultado es `InputRequired`, sin inventar respuesta.
    En `--json`, `InteractionRequested` y su resolución se emiten como eventos.

## Eventos de dominio y recuperación (revisión integral, 2026-09-24)

- **Eventos canónicos:** `InteractionRequested`, `InteractionResolved` e `InteractionExpired`.
  Las choices simples conservan `optionId`; los cuestionarios referencian artifacts tipados según
  ADR-0045. `PermissionRequested`, `Granted` y `Denied` (ADR-0036 §5) son los específicos de
  `Kind = Permission`.
- **Tras un resume:** las interacciones no resueltas se vuelven a publicar. Sin cliente, permisos y
  riesgo aplican ADR-0003 (`Deny`); `Question` queda `InputRequired` según ADR-0045.
- **`Question`** (el modelo pregunta al usuario) **entra en v1** mediante `user.ask`; admite una o
  varias preguntas, single/multi-select, texto libre y `Otro`. Mientras espera, la Lane queda en
  `WaitingForInput` (ADR-0036 §3).

## Clasificación

| Elemento | Categoría |
|---|---|
| DTOs `InteractionRequest`/`InteractionOption`/`RespondToInteraction` y cola (M1 los simula con `FakeTool` + Ask; el plain renderer pregunta en línea) | **Necesario desde M1** |
| `user.ask`, cuestionarios tipados, respuesta/durabilidad y formulario plain TTY | **M3 (ADR-0045)** |
| Overlays TUI (track TUI, después de M3; incluye `QuestionnaireOverlay`), kinds `ReconciliationConflict` (M3), `PlanScopeChange` (M2), `IntegrationConflict` (M7), `MemoryPromotion` (M8) | **M4+ / según milestone** |
| Aprobaciones remotas o desde otro dispositivo | **Deferable** |
