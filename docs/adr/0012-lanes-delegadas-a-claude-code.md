# ADR-0012 — Lanes delegadas a Claude Code

- **Estado:** Aceptada (2026-09-24)
- **Relacionado:** ADR-0011 §3.3 (sin OAuth de Anthropic), ADR-0008 (aislamiento)
- **Spec:** §10, §14 (Delegated), §16, INV-004, INV-005, INV-015

## Contexto

La suscripción Claude Pro/Max no puede usarse desde OmniCore mediante tokens OAuth, y OmniCore tampoco puede hacerse pasar por Claude Code (ADR-0011 §3.3). Sí existe una vía permitida. La página [Legal and compliance](https://code.claude.com/docs/en/legal-and-compliance) dice que la restricción no impide que un usuario final inicie sesión, con su propia suscripción, en el binario **sin modificar** de Claude Code, incluso cuando una plataforma lo aloja. Condiciones de esa página:

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

### 4. Mapeo de eventos

| stream-json de Claude Code | Evento OmniCore |
|---|---|
| `system/init` (modelo, tools, `capabilities`) | `LaneStarted` + `ModelSelected` |
| `assistant` / `user` (tool_use, tool_result) | `ToolStarted` / `ToolCompleted` (origen: externo) |
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
- **Milestone:** M6 (multi-agent), después de M5 (router) y con M7 (worktrees) como requisito del aislamiento obligatorio.
