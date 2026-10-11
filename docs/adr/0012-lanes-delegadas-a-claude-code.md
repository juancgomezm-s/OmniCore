# ADR-0012 — Lanes delegadas a Claude Code

- **Estado:** Aceptada — rev. 3 (2026-09-24). Rev. 2: §3.6–3.7 integran el Effect Journal y el Plan. **Rev. 3** (decisión del usuario en la revisión integral):
  - se implementa en **M7**, después de los worktrees; ya no en M6;
  - **riesgo residual aceptado:** el proceso `claude` corre con sandbox `Basic` (control del árbol y límites, ADR-0038 §3) y red, así que **puede leer fuera del worktree**;
  - como mitigación, se neutralizan `.claude/` y `.mcp.json` del repo dentro del worktree;
  - la opción `--permission-prompt-tool` requiere MCP y queda para M8 o después.
- **Relacionado:** ADR-0011 §3.3 (conexión por cuenta en reevaluación desde rev. 4), ADR-0008 (aislamiento)
- **Spec:** §10, §14 (Delegated), §16, INV-004, INV-005, INV-015

## Contexto

Este ADR define la ejecución delegada al CLI, no decide el diseño de un provider OAuth nativo. La exclusión arquitectónica de ese flujo se retiró en ADR-0011 rev. 4 por petición del usuario. La página [Legal and compliance](https://code.claude.com/docs/en/legal-and-compliance) describe la integración del binario **sin modificar** de Claude Code, incluso cuando una plataforma lo aloja. Condiciones de esa modalidad:

- El binario se ejecuta tal como lo publica Anthropic, sin quitar ni restringir sus métodos de autenticación.
- Cada usuario se autentica con sus propias credenciales. No se intermedia ni se revende uso.
- Ningún tercero recolecta, almacena ni intermedia credenciales o tokens de Claude.ai. El login se completa en el flujo de Anthropic.
- No se usa el nombre ni el logo de Claude Code como nombre de producto o función, ni de forma que sugiera respaldo de Anthropic. Se permite decir en texto plano que se ejecuta Claude Code.
- Los límites anunciados de Pro/Max asumen uso ordinario e individual.

## Decisión

### 1. Un segundo tipo de ejecutor de Lane

`AgentExecution` tiene dos implementaciones detrás de `ILaneExecutor`:

| Ejecutor | Quién corre el loop agentic | Uso |
|---|---|---|
| `NativeAgentExecutor` | OmniCore (spec §13) | Por defecto; modelos locales y API |
| `ExternalAgentExecutor` → adaptador `claude-code` | El CLI oficial de Claude Code en modo no interactivo | Tasks delegadas en ORQ que se benefician de un modelo frontera con la suscripción del usuario |

No es un `IModelProvider`: Claude Code es un agente completo, con tools propias. Se integra como **Lane** (INV-004), nunca como `SpawnAgent(prompt)`.

### 2. Invocación (verificada con Claude Code 2.1.269)

```text
claude -p <TaskPacket renderizado>
  --output-format stream-json --verbose [--include-partial-messages]
  --json-schema <esquema de AgentResult>
  --permission-mode dontAsk --permission-prompts none
  --allowedTools <derivadas del PermissionScope de la Lane>
  --add-dir <worktree de la Lane>
  --strict-mcp-config --mcp-config <solo lo que OmniCore provea>
  --setting-sources <restringido>
  --max-budget-usd <TaskBudget>
  --model <alias del ModelPolicy>
  --no-session-persistence            (o --resume <id> para reanudar)
```

- **No se usa `--bare`:** ese modo nunca lee el login por suscripción.
- La contrapartida es que sin `--bare` Claude Code carga hooks y `.mcp.json` del proyecto sin diálogo de confianza. Se mitiga con `--strict-mcp-config`, `--setting-sources` y la ejecución en un worktree aislado. Qué combinación exacta de `--setting-sources` excluye la configuración del proyecto sin perder el login se verifica al implementar.
- OmniCore **nunca** lee, copia ni almacena credenciales de Claude. Si el usuario no ha iniciado sesión, el adaptador reporta `AuthenticationFailed` y le pide ejecutar `claude` y completar el login de Anthropic.

### 3. Frontera de seguridad

Dentro de esta Lane, las tools las ejecuta Claude Code, no el Tool Runtime de OmniCore. Es una excepción acotada a INV-001/INV-003, compensada así:

1. **Aislamiento obligatorio:** la Lane corre siempre en `GitWorktree` (spec §48). El resultado entra al workspace solo por la decisión de integración.
2. **Permisos derivados:** `--allowedTools` y `--add-dir` se calculan desde el `PermissionScope` efectivo de la Lane (spec §44). Con `dontAsk` + `--permission-prompts none`, todo lo no permitido se deniega.
3. **Opción a evaluar:** `--permission-prompt-tool` apuntando a una tool MCP de OmniCore. El Permission Engine resolvería los `Ask` de Claude Code y la autoridad volvería a OmniCore (ADR-0003 aplica igual).
4. **Completion Gates siguen aplicando** (INV-015). El `AgentResult` que devuelve Claude Code es una *propuesta*; Build/Test/Acceptance gates corren en OmniCore sobre el worktree.
5. **Context isolation** (INV-005): Claude Code recibe solo el `TaskPacket` renderizado, nunca el transcript del padre.
6. **Effect Journal a nivel de Lane (rev. 2, ADR-0004):**
   - OmniCore no observa cada efecto con garantías; los eventos de tools de Claude Code son informativos.
   - La Lane entera se trata como una ToolCall `NonIdempotent` confinada a su worktree.
   - Si OmniCore muere a mitad de la Lane, al reanudar se evalúa el estado del worktree: se continúa con `--resume` o se descarta el worktree y se reinicia desde la base (ADR-0021).
7. **Plan (ADR-0016):** el `TaskPacket` incluye el `WorkingState` compacto. El `AgentResult` puede traer `PlanMutation`s propuestas, que el `PlanService` valida como cualquier otra propuesta.

### 4. Mapeo de eventos

| stream-json de Claude Code | Evento OmniCore |
|---|---|
| `system/init` (modelo, tools, `capabilities`) | `LaneStarted` + `ModelSelected` |
| `assistant` / `user` (tool_use, tool_result) | `ExternalToolObserved` (informativo; no entra al ciclo de vida de ToolCall de ADR-0004) |
| `stream_event` (deltas) | progreso coalescido, no canónico (ADR-0002 §5) |
| `system/api_retry` (`error`: `rate_limit`, `overloaded`…) | `LaneHeartbeat` + causa |
| `system/permission_denied` | `PermissionDenied` |
| `result` (`session_id`, `total_cost_usd`, `usage`, `is_error`, `structured_output`, `permission_denials`) | `AgentResult` + `LaneCompleted` / `LaneFailed` |

- `--json-schema` hace que `structured_output` cumpla el contrato `AgentResult` (spec §16).
- `total_cost_usd` es una estimación del cliente; se registra como tal.

### 5. Cancelación y reanudación

- `SIGTERM` deja el turno sin terminar (exit 143). `SIGINT` o un interrupt del protocolo cierran el turno limpio. En Windows, el mecanismo de interrupción limpia (control request por `--input-format stream-json`, feature `interrupt_receipt_v1` en `capabilities`) **se verifica al implementar**. Mientras no esté verificado, se usa Job Object + kill y el Turn cuenta como abandonado (ADR-0004).
- Reanudar: `--resume <session_id>` guardado en el evento `LaneStarted`.

### 6. Nombres

El adaptador se identifica internamente como `claude-code`. En la interfaz se describe en texto plano ("ejecuta Claude Code"), sin usar el nombre como nombre de función de OmniCore.

## Consecuencias

- ORQ puede combinar Explorer/Verifier locales con una Lane de implementación en Claude Code usando la suscripción del usuario, de forma permitida.
- El consumo cuenta contra los límites del plan del usuario. El router debe tratarlo como un recurso limitado (disponibilidad y cuota) y no como gratuito.
- Dependencia de un binario externo cuya CLI evoluciona. Se detectan capacidades por `system/init.capabilities`, no por versión, y hay un test de contrato con salida grabada de stream-json.
- **Milestone:** **M7** (rev. 3), después de los worktrees. `--permission-prompt-tool` requiere M8.

### 7. Implementación M7 v1 (2026-10-10)

Esta sección registra la implementación vigente; las secciones anteriores conservan el diseño original. El adaptador validado usa Claude CLI 2.1.296. En ejecución limita las herramientas con `--tools`, concede únicamente las declaradas por la política efectiva con `--allowedTools`, usa `--safe-mode`, excluye fuentes de configuración del proyecto/usuario que puedan añadir comportamiento y entrega una configuración MCP vacía estricta. La autenticación existente se hereda del entorno del proceso, sin leer ni copiar credenciales al journal. La política de modelo, permisos efectivos del workspace y del usuario, grants y restricciones de perfil se comprueban antes del lanzamiento; permisos parciales que no se pueden expresar como un ámbito completo se rechazan. Una política enteramente de solo lectura limita el CLI a herramientas de lectura; un perfil de verificación no recibe escritores.

El binding de sesión queda en `ExternalLaneSessionBound`; cada invocación queda precedida por `ExternalLaneInvocationReserved`, y los resultados y contadores externos se registran mediante `ExternalLaneUsageRecorded` y `ExternalLaneActivityObserved`. La reserva es no reembolsable y permite como máximo dos invocaciones: una cuota inicial y, si existe binding verificable, una cuota restante asociada exactamente a la misma sesión. Un intento incierto no devuelve su presupuesto ni se relanza como generación nueva. Tras reanudar, el resultado puede recuperarse, pero el gasto anterior sigue marcado desconocido; no se convierte en cobertura completa por el éxito del proceso nuevo. El costo comunicado por CLI es estimado; el resultado acumulado de una sesión se contrasta con el presupuesto total, y la cuota de proceso limita cada invocación.

El lector de stream JSON espera y persiste el primer `system/init` antes de aceptar un resultado terminal. Deduplica uso repetido por `message.id`, rechaza uso inconsistente para un mismo identificador, no suma `assistant.output_tokens` de placeholder y toma la salida final de `result`. Las señales externas y contadores de tools son visibles como actividad de la Lane; no se convierten en llamadas canónicas del ciclo de vida local de `ToolCall`. La implementación actual cubre observación/progreso coalescido según el contrato del Host; una auditoría canónica individual de cada efecto ejecutado dentro de Claude continúa siendo futura.

La aceptación de un resultado es una interacción durable y puede reabrirse sin ejecutar de nuevo la Lane. El Host vuelve a validar que la captura aceptada siga correspondiendo al árbol actual antes de aceptar o limpiar; una captura desactualizada requiere nueva resolución. Los gates del resultado se evalúan dentro de la raíz aislada.

Queda como diseño futuro cualquier permiso interactivo que requiera `--permission-prompt-tool`, eventos canónicos individuales `ExternalToolObserved` con atribución de cada efecto interno, reanudación que permita sumar de forma probada el costo de intentos cuyo uso no quedó persistido, y soporte de ámbitos/grants por ruta que no puedan traducirse fielmente al allowlist del CLI. No se presenta la actividad final como heartbeat en vivo para cada retry.
