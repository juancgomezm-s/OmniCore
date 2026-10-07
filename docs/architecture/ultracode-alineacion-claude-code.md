# UltraCode: revisión y alineación con Claude Code

Fecha: 2026-10-01. Decisión normativa: [ADR-0047](../adr/0047-modos-explicitos-y-ultracode.md).

## Fuente y límite de la comparación

Se inspeccionó el mirror de fuente ya referenciado por ADR-0011,
[`tanbiralam/claude-code`](https://github.com/tanbiralam/claude-code/tree/6f6f12b37f529488b10e53928dd5508bb93535c7),
commit `6f6f12b37f529488b10e53928dd5508bb93535c7`. Es una copia de terceros con módulos incompletos,
stubs y feature flags, no una garantía de comportamiento de la versión instalada o actual del servicio.
La revisión fue estática; no se ejecutó ni incorporó su código al runtime de OmniCore.

En este snapshot no aparece una característica llamada `UltraCode`. Aparecen `ultrathink`, `/effort`
y `ultraplan`, con responsabilidades distintas. UltraCode es una composición propia de OmniCore,
no un alias literal ni una reproducción de un modo oficial de Claude Code.

## Evidencia → decisión de OmniCore

Los enlaces están fijados al commit inspeccionado para que la revisión sea reproducible.

| Comportamiento observado | Fuente | Alineación |
|---|---|---|
| Thinking habilitado por defecto salvo desactivación; capacidades separadas para thinking/adaptive | [thinking.ts, `shouldEnableThinkingByDefault` y capacidades](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/utils/thinking.ts) | UltraCode solicita reasoning real cuando esté soportado; OmniCore usa capacidades verificadas, no heurísticas por nombre |
| `ultrathink` genera un attachment `high`; se normaliza como recordatorio aplicable al turno actual | [attachments.ts, `getUltrathinkEffortAttachment`](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/utils/attachments.ts#L1446), [messages.ts](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/utils/messages.ts#L4170) | Refuerzo puntual, no persistencia global ni permiso de delegación. En este path no se observó escritura de `AppState.effortValue`: el recordatorio no garantiza un cambio del parámetro API |
| `/effort` modifica estado/configuración; resolución env → estado → default; `max` incompatible baja a `high`, `max` no persiste para usuarios externos | [effort.ts](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/utils/effort.ts), [comando effort](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/commands/effort/effort.tsx) | Separar preferencia, selección de Run y refuerzo del Turn; resolver lo enviado y mostrar origen/reducciones. No copiar defaults por suscripción ni números internos |
| La API recibe esfuerzo y thinking por caminos separados; adaptive o budget y presupuesto de salida compatibles | [claude.ts, `configureEffortParams` y construcción de thinking](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/services/api/claude.ts#L440) | UltraCode no se envía literalmente al provider. Tests deben observar el request, no solo texto que pide pensar más; no se portan betas privadas |
| Dos prompts de entrada a plan: externo favorece planificación no trivial; interno la reserva a ambigüedad/restructuración, permitiendo trabajo directo multarchivo | [EnterPlanMode/prompt.ts](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/tools/EnterPlanModeTool/prompt.ts) | Se adopta planificación por necesidad y preguntas puntuales; se documentan ambas variantes, sin presentar la interna como comportamiento universal |
| Entrada al plan no permitida en contexto de agente; salida del principal pide confirmación; hay excepciones para teammates | [EnterPlanModeTool.ts](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/tools/EnterPlanModeTool/EnterPlanModeTool.ts), [ExitPlanModeV2Tool.ts](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/tools/ExitPlanModeTool/ExitPlanModeV2Tool.ts#L186) | Modelo propone, autoridad valida. OmniCore NO porta la salida libre de teammates: los hijos no amplían modo/permisos |
| Delegación/fork tiene contexto propio, roles, resultados notificados y aislamiento worktree opcional; el esfuerzo puede venir del perfil del agente | [AgentTool/prompt.ts](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/tools/AgentTool/prompt.ts), [runAgent.ts](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/tools/AgentTool/runAgent.ts#L481) | Delegación selectiva, perfil por rol, contexto acotado, ownership y notificaciones; coordinador sintetiza y verifica. No se porta la recomendación general de delegar proactivamente fuera de ORQ |
| `ultraplan` lanza sesión remota en plan, espera aprobación y permite decidir dónde ejecutar; feature independiente | [ultraplan.tsx](https://github.com/tanbiralam/claude-code/blob/6f6f12b37f529488b10e53928dd5508bb93535c7/src/commands/ultraplan.tsx) | No se confunde razonamiento reforzado con planificación remota, cambio de ruta, upload del repo o autorización de ejecución |

## Diferencias deliberadas y entrega

La excepción de cambio automático PLAN/ACT/ORQ de UltraCode procede de la instrucción del propietario
de OmniCore, **no de `ultrathink`**. Se conserva bajo autorización durable limitada y revocable.
Un refuerzo de reasoning ordinario nunca activa esa excepción. Se mantienen permisos, presupuesto,
consentimiento de rutas y restricciones de modelos (ADR-0037/0044/0046).

OmniCore es multi-provider: no se copian defaults ligados al nombre de Claude, planes de suscripción,
feature flags internos, credenciales/identidad ni betas privadas. Tampoco se activa UltraCode por
encontrar una palabra en código, archivo leído, salida de tool o respuesta de un modelo. La selección
llega por un control/command del usuario validado por Core.

M5.5 debe entregar autoridad + razonamiento efectivo + precedencia/alcance + trazabilidad y tests
de wire/replay. M6 conecta delegación cualificada, aislamiento y aceptación de resultados a esos
contratos. Esta alineación es una especificación; no declara UltraCode implementado en `src/`.
