# Consolidación OmniCore — 2026-10-04

Estado: ejecución parcial verificada; NO cierre de M4, M5 ni M5.5.

## Alcance y propiedad

- Rama de integración: `codex/omnicore-consolidation-20261004`, base `281dcf1`.
- Workspace: `C:\Users\juanc\.codex\worktrees\m55-artifactrefs-roundtrip\OmniCore`.
- Main permanece en `2e3277c`; sus cambios existentes no se editan, restauran ni incorporan.
- M4 permanece en `8a492e9` y M5 en `d4d0c70`, con sus cambios conservados. Los resultados de sus tests NO prueban integración de todos sus diffs en esta rama.
- Coordinador implementa/consolida; un revisor Luna revisó bloques completos en modo read-only. No nuevas rondas Pi, solicitudes a providers reales ni cambios de servidores/credenciales.
- El monitor anterior sigue pausado. Este plan sustituye la estrategia de mantener slots ocupados con microrevisiones.

## Bloques consolidados

1. ArtifactRefs: round-trip de ocho campos, compatibilidad de lectura con formato histórico de tres campos, delimitadores escapados, marcado de referencias vivas en GC, verificador de tamaño/redacted. Reparación nueva: lector y verificador rechazan kind/sensitivity no canónicos y tamaños inválidos consistentemente.
2. ExplorerTurn: ProviderState local entre pasos de una misma Ask, agrupación de bloques assistant ToolUse, redacción conservada, auditoría de gasto sin copiar contenido de respuesta. NO persistencia/replay de estado opaco entre Ask/restart.
3. Escalación: exclusión automática de rutas API-key con precios incompletos y rechazo antes de aprobación si falta credencial. Credencial disponible NO implica consentimiento de gasto; no se redefine la política configurada.
4. Controles y regresiones pendientes: lifetime del ledger, consumo de gates, cap dentro de sesión y guard de cuestionario. Se conservan también cinco reproducciones rojas conocidas, sin omitirlas ni debilitar assertions.

## Evidencia ejecutada por coordinador

| Batería local | Resultado | Limitación |
|---|---|---|
| Nueva teoría metadata de ArtifactRefs, antes del fix | 8/8 casos fallan | Verifier aceptaba metadata inválida |
| ArtifactRefs/store/verifier/escaping/redacted, después del fix | 56/56 pasan | No cierre global M5.5 |
| MaintenanceTests, incluido GC | 14/14 pasan | Fixtures temporales, no datos del usuario |
| Batería combinada M5.5 de bloques y controles | 111/111 pasan | No suite completa ni providers reales |
| Cinco clases de defectos abiertos | 7 casos: 5 fallan, 2 pasan | Baseline rojo reproducido, no reparación |
| GLM58 passing-gate y gate-consumption | 6/6 pasan | Gates inyectados; no prueba proceso externo |
| M5 qualification host + empty-suite | 7/7 pasan | Scripted; no cualificación real |
| M4 checkpoint + controles TUI locales seleccionados | 1297/1297 pasan | Headless; no TTY visual ni reinicio OS |

Todos los resultados anteriores: 0 errores del runner y 0 casos omitidos. Los grupos se solapan; NO sumar sus cifras como tests únicos.

## Pendientes, por prioridad

1. Validaciones pendientes desaparecen al recrear boundary/runtime: requiere contrato mínimo de persistencia antes de cambiar journal/codec.
2. Daily cap no agrega gasto de otras sesiones: decidir scope duradero de gasto; no inventar un agregado global de IEventStore.
3. Usage previo a suspensión se pierde al reanudar: necesita registro aditivo por ModelStep compatible con ADR-0046, sin doble ModelCompleted.
4. Texto ordinario recibido al reanudar se descarta: distinguir input nuevo de reanudación interna, preservar estados de interacción y journal canónico.
5. Opaque continuation no tiene ArtifactRef durable: aprobar política de redacción/replay y contrato antes de persistir datos opacos.
6. M4: TTY real/sesión larga/checkpoints tras reinicio OS. M5: acceso/cualificación real con presupuesto explícito. Una prueba upstream restante NO alcanza para repetir suite.
7. Gramática estricta de escapes malformados no emitidos por writer queda fuera de este bloque; no se declara validada.

## Decisión mínima solicitada (aún NO implementada)

Para el pendiente 1 se propuso añadir registros canónicos aditivos de edición pendiente y validación consumida, asociados al Run y a IDs de ediciones cubiertas. Mantener la regla actual: un build O test exitoso consume pendientes; no cambiarla a todos los gates exitosos, ni inferir confianza de prefijos de provider IDs.

Tras aprobación: concretar schema/codec, replay y scope; reproducir RED, implementar, verificar conservación tras reopen/recreación y revisar el bloque completo con Luna. No escribir nuevos eventos antes de esa decisión.

La rama contiene regresiones rojas explícitas y NO está lista para integrar en principal ni etiquetar un hito cerrado.

## Actualización — contrato aprobado e implementado (2026-10-04 23:48 UTC / 17:48 local)

El usuario aprobó el contrato mínimo. Se implementaron dos eventos aditivos v1:

- `post_edit_validation.pending`: RunId, ToolCallId (ID estable de edición) y rutas declaradas. Solo las herramientas Core filesystem.patch/write que ya alimentaban MutationLedger reservan deuda cuando la política exige validación; claims de procesos no crean deuda de edición.
- `post_edit_validation.consumed`: RunId, IDs de ediciones cubiertas y clave build/test. La captura de IDs precede al delegate del gate. Un build O test exitoso consume solo esa captura, aunque otro gate falle; no consume una edición posterior.

El hook corre después de autorización y antes del efecto. Pending y ToolCallStarted se confirman en UN lote atómico Barrier; fallo del commit impide ejecutar la herramienta. El hook no autoriza efectos ni eleva permisos. El executor primitivo sin journal/Run conserva su uso de pruebas histórico.

La proyección de deuda usa el Run explícito del journal, incluso con ledger ausente/nuevo. Outcome None y reconciliación NotApplied liberan la reserva; Partial/Unknown/Applied/Conflict la conservan hasta una validación cubierta. El consumo se confirma Barrier antes de quitar su representación en memoria. Los contadores de mutación por Turn/Run NO se restauran como parte de este cambio.

Evidencia nueva, ejecutada por coordinador:

- 163/163 pruebas focales pasan; incluye 12 casos de replay/reopen/consumo/scope, 2 fallos inyectados alrededor del commit atómico, 2 escenarios del filesystem real con boundary recreado (ahora también SQLite cerrado/reabierto), controles de gates/ledger, Barrier, codecs, filesystem, lanes y verifier. Sin omitidos ni errores del runner.
- Luna encontró la reserva huérfana en la primera versión; corregida mediante lote atómico y revisada nuevamente sin otro defecto concreto.
- Recuento de las cinco clases rojas anteriores: 7 casos, 3 pasan y 4 fallan. Deuda perdida deja de fallar; cross-session daily cap, suspended usage, resume input y opaque ArtifactRef durable siguen abiertos.
- Pruebas de gates devuelven evidencia sintética. No prueban reinicio OS ni proceso real build/test ni cualificación de proveedor.
- Journals antiguos sin estos eventos no se migran ni se les inventa deuda retrospectiva; este contrato protege las ediciones registradas por el runtime actualizado. Runs anteriores requieren validación explícita antes de confiar en su deuda pendiente.

El pending 1 de la lista anterior queda reparado para el contrato nuevo. La propuesta ya no está esperando aprobación. Main permanece intacto, monitor anterior pausado, resto de contratos y gates externos abiertos.

## Reparación delegada integrada — 2026-10-05 01:42 UTC / 2026-10-04 19:42 local

Tres de los cuatro defectos restantes quedan reparados en `codex/omnicore-consolidation-20261004`, código hasta `90d3530`. Implementación de Luna en su worktree aislado, auditada e integrada mediante cherry-picks por el coordinador. Main y sus cambios preexistentes no se modificaron.

1. Daily cap entre sesiones: lector opcional `IWorkspaceJournalReader`, SQLite y memoria. El diario agrega todas las sesiones del mismo journal/workspace por día UTC; sesión y Run conservan su scope. Evidencia incompleta/metadata inválida, incluso costo legacy negativo, impide una nueva llamada cuando se exige ese presupuesto. Un cap sólo por Run no exige lectura global.
2. Uso suspendido: eventos aditivos v1 `model_step.started` y `model_step.completed`, confirmados Barrier antes de la llamada y después de su respuesta, antes de herramientas/suspensión. Reanudar recupera uso y costo de pasos anteriores. Resumen legacy no se suma otra vez si hay pasos; costo confirmado se suma sin recalcular con tarifa nueva. Los índices Started/Completed deben corresponder; un paso huérfano deja el costo desconocido, no parcial conocido. El lector valida metadata contra el artifact de uso.
3. Reasoning opaque CAS: conserva una ArtifactRef original sólo con kind/sensitivity/hash/size/redacted/metadata admitidos y CAS verificado. Texto visible continúa redactado; referencias ausentes, corruptas o inválidas se descartan. No se serializa ProviderState crudo ni se implementa replay entre Ask/reinicio. Los dos Facts originales de continuidad siguen intactos; casos negativos CAS están en archivo separado.

Commits integrados: `f111c12`, `fff536a`, `0715c33`, `1a5eb47`, `322b28f`, `2bad7db`, `5ed0c6c`, `9f27ce8`, `0ab0b32`, `90d3530`. Origen Luna: `878fa3d`, `be0341d`, `1bace80`, `3d66034`, `fac67b5`, `14610c5`, `54c3524`, `6fbdced`, `e697d60`, `b4731e6`.

### Verificación independiente final

- Build de OmniCore.Tests: 0 errores, 0 advertencias.
- Batería integrada: 156 casos, 155 PASS, 1 FAIL, 0 errores del runner, 0 omitidos. Cubre ExplorerTurn, caps, suspended spend, costo por paso, continuidad/CAS, deuda durable, ArtifactRefs/store/durabilidad, codecs/verifier, Barrier, GC y guard del cuestionario.
- Reejecución de las cinco clases de regresión originales (ahora ampliadas): 15 casos, 14 PASS, 1 FAIL. Los grupos se solapan; no sumar cifras.
- Único RED: `ExplorerTurnResumeInputRegressionTests.Resume_input_after_questionnaire_reaches_provider_as_user_message`, línea 115: marcador esperado una vez en mensajes user, actual cero. NO es ausencia de llamada al provider. No se alteró ni omitió su assertion.
- `git diff --check` limpio. Sólo pruebas locales/scripted y fixtures temporales; ningún gasto de cualificación real.

### Reparto y límites reales

GLM59 produjo exploración sin patch durante aproximadamente 40 minutos; se canceló sólo su Pi identificado y se redujo el paquete, preservando trabajo/logs. GLM60 terminó por length (16384 tokens) sin texto ni código; no se repitió el bucle. Luna tomó los bloques restantes. GLM61 entregó revisión cerrada sin herramientas, stop normal: sugirió un OverflowException hipotético al decodificar StepIndex. No se acepta como defecto reproducido: el codec usa JsonSerializer, envuelve JsonException en EventParseException, ya cubierta por el catch. No vio/ejecutó el codec. Pese solicitar thinking off, el provider reportó 8203 tokens reasoning de 8520 output. GLM aportó revisión, NO implementación aceptada.

La entrada ordinaria al reanudar sigue pendiente de decisión: ADR-0046 exige FollowUp del siguiente Turn sin declaración explícita de Steering; el RED espera entrega al Turn activo. No se cambia ese contrato silenciosamente. Después de elegir, falta implementar cola/promoción durable FollowUp o entrada explícitamente Steering con controles.

Los eventos ModelStep son un subconjunto mínimo para contabilizar uso: NO cierran el contrato completo ADR-0046/0047 (RouteId y replay de ProviderState siguen pendientes). El cap global es lectura de journal al inicio de Ask, NO reserva atómica entre llamadas concurrentes; puede excederse durante una respuesta y detener el siguiente paso. No inventa deuda/uso en journals antiguos sin evidencia. No se cierra M4 TTY/sesión larga/reinicio OS ni M5 cualificación real. Monitor anterior sigue PAUSED; ningún push ni integración en main.
