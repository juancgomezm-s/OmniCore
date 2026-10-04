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
