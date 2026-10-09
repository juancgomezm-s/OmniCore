# Integración recuperada y continuación de M6 — 2026-10-08

Por instrucción del usuario se recuperó el avance de los worktrees vigentes,
se integró a `main` y se inició la continuación hacia los criterios de salida
de M6. La implementación pendiente se encargó a un worker `gpt-6-luna` con
esfuerzo `xhigh` (muy alto), también por instrucción explícita del usuario.

## Inventario e integración

- Principal: `C:/Users/juanc/source/repos/OmniCore`, `main` en `1e30c79` al iniciar.
- `codex/rescue-glm-20261008`, HEAD `ab1837f`: ya era ancestro de `main`;
  no requirió un segundo merge. Su worktree permanece disponible.
- `codex/tui-markdown-polish`, HEAD `06c5781`: nueve commits nuevos respecto
  a `main`. Se integraron mediante merge `a95b089`, sin conflictos: 81 archivos,
  6909 líneas añadidas y 133 eliminadas. Ambas ramas son ahora ancestros de main.
- Respaldo del punto anterior: rama `codex/pre-m6-integration-20261008`.
- Se preservaron temporalmente y luego restauraron los cambios locales de
  `docs/adr/0011-conexion-a-proveedores.md`, `docs/adr/README.md` y
  `docs/validation/claude-oauth-plan-20261008.md`. No forman parte del merge
  ni de la implementación de M6.
- No se eliminaron worktrees, backups ni ramas; no se hizo push. El inventario
  y la recuperación de los worktrees históricos permanecen descritos en
  [consolidación anterior](worktree-consolidation-20261007.md) y
  [archivos recuperados](recovered-worktrees-20261007.md).

## Avance recuperado

Conversación canónica y resumen por Run, aislamiento y herencia de contexto
SelectedProjection, paquetes durables de delegación, cola con admisión acotada,
ejecución de hijos lectores, resultados inmutables en CAS, dispositions
explícitas y joins All/Any/Quorum/Explicit sobre resultados aceptados.
También se integraron el sidebar funcional, diffs atribuibles, preferencias
responsive y la compatibilidad del codec histórico de tools.

Los bloques anteriores conservan sus actas focales y su alcance; no se suman
sus conteos ni se extrapolan a un cierre integral de M6.

## Estado reconocido al integrar

M1, M4, M5 y M5.5 cerrados en Windows. M2 y M3 conservan código completo y
aceptación manual con el modelo local pendiente. La cualificación real de M5
no sustituye esos dos criterios. M6 pasa de la declaración antigua «sin empezar»
a implementación parcial; M7–M10 no se inician por esta integración.

Pendientes de cierre M6 identificados en el código y actas recuperadas:

- Scheduling paralelo de lectores y background con capacidad real, prioridades
  y exclusión de escritores; journal atómico entre streams concurrentes.
- Pool compartido de Run con reservas y liberaciones reconstruibles; límites
  efectivos tanto para principal como para hijos.
- FanOut/FanIn Direct/Aggregate y supervisión en proceso por commands/eventos,
  handshake, fallo Wait observable, mailbox y wake determinista.
- Recuperación de ejecución interrumpida sin adoptar ownership desconocido ni
  repetir efectos o inferencia a ciegas.
- WorkflowCommands, tools Dynamic, inspector y criterio Explore → Implement →
  Verify como Tasks diferenciadas con Plan reconciliado.
- Autoridad/routing heredados sin ampliación; revocación durable; módulo sin
  cablear produce ReworkRequested en vez de una aceptación implícita.

## Verificación de la base integrada

- `dotnet build OmniCore.slnx --no-restore -v quiet`: exit0, 0 advertencias,
  0 errores, 57.50 s.
- `dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
  -class '*M4ProcessRecoveryTests' -noColor`: exit0, 1 PASS, 0 FAIL/SKIP,
  100.137 s. Log `C:/Users/juanc/.codex/m6-merged-m4-20261008.log`.

Esta evidencia corresponde a la base del merge, antes de los cambios pendientes
de M6. No acredita su cierre, llamadas autenticadas nuevas ni ejecución Linux.
La aceptación integral posterior debe identificar los binarios y pruebas finales.

## Continuación en curso: concurrencia y presupuestos

El worker implementó serialización de validación y append por journal/Session,
atribución exacta de herramientas por Lane/Turn y bloqueo visible de registros
históricos ambiguos. Pasaron 44 casos focales de completion, proyecciones,
watchdog y escritura concurrente; la build terminó sin advertencias ni errores.

El pool incorpora reservas SQLite de tokens y dinero antes del despacho,
consumo canónico antes de settlement y ownership temporal de invocaciones vivas.
La admisión relee consumo dentro de la transacción de reserva: no usa un saldo
anterior a la finalización de un hermano. Pasaron 56 casos focales de admisión,
ceiling y ciclo de delegación. Una ejecución adicional de 12 casos incluyó
`ParallelRunBudgetPoolIntegrationTests`: dos proveedores scripted bloqueados
simultáneamente en lanes distintas, reservas finitas compartidas de 18 432 tokens,
consumo total de 200 y saldo de 18 232 tras cerrar y reabrir SQLite.

Son resultados intermedios, comunicados por el worker y revisados en el código;
los conteos se solapan y no deben sumarse. El cierre exige aún el dispatcher
integrado, capacidad, supervisión/workflow y la verificación del estado final.
La revisión también identificó la necesidad de admisión atómica para turnos y
herramientas cuando dos lanes compiten por el último cupo compartido.

El siguiente bloque pasó build 0/0 y 57 casos focales integrados en 8.514 s:
`AgentProfileSuspensionIntegrationTests`, `DelegationAdmissionTests`,
`DelegationLifecycleTests`, `CanonicalWriterArchitectureTests`,
`ParallelRunBudgetPoolIntegrationTests` y `QuestionnaireVerticalTests`.
Incluye dos delegaciones reales con proveedores bloqueados simultáneamente,
leases de capacidad y ledger SQLite compartido; el padre queda bloqueado por
join All y, tras reapertura, sólo se desbloquea al aceptar ambos resultados.
También verifica capacity llena, respuesta inmediata `WaitingForCapacity`,
waiter de background cancelado antes de obtener lease y ninguna invocación
posterior. Se preservaron la suspensión con atribución nullable exacta, schema
de cuestionarios en CAS y acceso exclusivo de Security a los internos de
Abstractions. FanOut, supervisión/workflow y validación integral siguen pendientes.

Los bloques anteriores quedaron guardados en `25434a9` (admisión paralela y
pool de presupuesto) y `d845169` (supervisión en proceso, Wait e inspector).
El segundo checkpoint pasó compilación sin advertencias ni errores y 86 casos
focales en 15.326 s, incluidos los de CLI end-to-end. Log:
`%TEMP%/m6-supervisor-wait-focal-20261008.log`; los conteos se solapan con los
anteriores y no representan una suite completa.

La supervisión usa una capacidad interna de `IOmniClient` ligada a Session,
Run y Execution: handshake por command, consultas y suscripción acotadas,
dispositions explícitas y mailbox con publicación CAS. Un `origin` enviado en
JSON no concede esa capacidad. La pérdida del supervisor después de Accepted,
con el proveedor hijo activo, registra fallo del binding y bloquea Task/Lane
por Wait. Al liquidar la respuesta no se publica un resultado aceptable ni se
resuelve el join. La prueba verifica causation desde el evento terminal del
supervisor y reapertura SQLite sin repetir la llamada ni adoptar ownership.

M6 sigue abierto: FanOut/FanIn, consumo y resolución operativa de mailbox/wake,
WorkflowCommand, Dynamic tools, la triada Explore/Implement/Verify y la
validación integral de los binarios finales continúan en el worker Luna.

## Continuación en curso: FanOut/FanIn y recuperación de capacidad

Checkpoint `fc00197`: FanOut Direct/Aggregate usa un evaluador determinista,
requiere resultados aceptados de todos los miembros y preserva su orden. Una
disposition ReworkRequested mantiene el grupo pendiente; el reemplazo explícito
con una delegación nueva conserva su posición. El agregado se publica bajo
lease CAS y sus referencias, junto con las de los resultados, quedan indexadas
en el envelope durable. Codec y replay validan payload, schema, identidad,
scope y resolución única antes de persistir, sin retirar la validación de los
contratos pre-M6 congelados.

La recuperación cubre aceptación duradera seguida de fallo de append de la
resolución: al reabrir SQLite resuelve grupo y join una vez, sin repetir llamadas
al proveedor. La causa apunta al EventId de la aceptación que satisface el grupo
o join. Cancelar el último join permite el desbloqueo atribuido al join fallido;
un owner cuyo AgentExecution terminó conserva Task/Lane bloqueados.

Capacidad deja avanzar al lector que precede al escritor en la cola y evita
contar dos veces el cupo principal activo: principal y dos lectores ocupan
exactamente MaxAgents=3. Evidencia revisada en
`%LOCALAPPDATA%/Temp/m6-fanout-capacity-20261008.log`: build 0/0,
FanOut 10 PASS (5.186 s), Capacity 2 PASS (0.699 s), y focos de contratos,
proyección y escritor canónico sin fallos. Sigue siendo evidencia intermedia;
mailbox/wake operativo, workflow/triada, background/heartbeat/transcript y
validación integral final permanecen pendientes.

## Continuación en curso: mailbox y wake en el pipeline de tools

Checkpoint `8add0da`: `core.agents.mailbox.receive` entra por el catálogo y el
pipeline normal de Prepare, autorización, ejecución y journal. Confirma su
Started con Barrier antes de esperar una señal en proceso; no usa polling con
LLM. El wake exige el ToolCall vivo exacto, binding aceptado, supervisor vivo,
autoridad ORQ, presupuesto y ruta autorizada. Un mensaje no desbloquea una
espera por join, interacción humana o pérdida de supervisor.

La confirmación del mensaje y WakeResolved se escriben juntos después del
ToolCallSucceeded durable, con su EventId como causa. Una entrega fallida
cierra el wake sin ACK y una nueva llamada explícita puede consumir el mismo
mensaje. La recuperación de un Started sin outcome conserva la semántica
existente de las tools sin efectos: ToolCallFailed(CANCELLATION), wake Failed y
mensaje pendiente, sin repetir tools ni llamadas al proveedor. La prueba usa
una copia SQLite aislada; no acredita dos Hosts activos sobre el mismo journal.
La concurrencia de M6 ocurre dentro de un mismo Host/store compartido; el lease
entre procesos corresponde a M9.

El scoped client creado antes de revocar autoridad pierde su capacidad efectiva:
la revocación se difiere durante el ToolCall, la cancelación retira el waiter y,
una vez asentada la revocación, se rechaza el envío sin MessageReceived/Wake/ACK.
Logs revisados: `%TEMP%/m6-mailbox-lifecycle-20261008.log`, 21 PASS en 8.169 s;
`%TEMP%/m6-mailbox-admission-20261008.log`, 18 PASS en 2.802 s, sin FAIL/SKIP.
Son focos intermedios; queda comprobar el fallo posterior a Succeeded y anterior
a ACK, además de workflow/triada, background/inspector y la suite final.

## Continuación en curso: catálogo y capacidad efectiva de tools

Checkpoint `2788606`: el catálogo Host expone descriptores Prompt/Workflow y
disponibilidad según el Run ORQ y su autorización vigente. `/orq-auth` devuelve
una intención tipada WorkflowRequested; este checkpoint todavía no conecta su
runner ni acredita la triada. El registro valida nombres y aliases reservados
antes de mutar; la query real `commands` publica JSON válido para los clientes.

WorkflowToolFactory registra SourceKind.Dynamic con owner/version del workflow
compilado, rechaza colisiones y mantiene identidad, efecto y riesgo del intent.
La composición sólo clasifica los ids dinámicos efectivamente registrados y los
intersecta con la política del modelo: no habilita un prefijo `dyn.*` genérico.
El buzón tiene la capacidad específica AgentMailboxWait, incluida en los presets
actuales de lectura; una política anterior con allowlist explícita que no la
incluya conserva su rechazo. No se migran permisos ni se amplía autoridad.

Los focos del buzón ahora ejecutan ExplorerTurn y ToolRuntime con una frontera
efectiva de modelo. También cubren fallo de append del ACK tras Succeeded
durable: la primera reapertura confirma ACK/WakeResolved con la causa exacta y
la segunda no añade eventos ni repite llamadas. Logs revisados en
`C:/Users/juanc/.codex/`: `m6-mailbox-boundary-20261008.log`, 22 PASS (8.285 s);
`m6-workflow-factory-20261008.log`, 3 PASS (0.523 s);
`m6-command-registry-20261008.log`, 2 PASS (0.145 s);
`m6-client-commands-20261008.log`, 16 PASS (0.620 s), sin FAIL/SKIP.
Son resultados focales, con solapamiento; no sustituyen la suite integral.

## Continuación en curso: escritoras serializadas y permisos heredados

Checkpoint `b7c7adb`: la admisión acepta perfiles escritores sólo si su techo es
subconjunto del padre. El chequeo conserva los matchers Ask/Deny de procesos y
red para listas no vacías; una lista hija vacía niega esa capacidad completa.
No se infiere inclusión entre globs distintos ni se levantan restricciones.
El dispatch elige lease lector o escritor a partir del perfil durable y revalida
el perfil antes de ejecutar. Las escritoras usan el catálogo y executor normal
de mutación, con frontera de modelo y política del perfil.

La prueba vertical escribe un archivo en workspace temporal y observa
WriterActive dentro del callback y su liberación al terminar. El resultado queda
Returned/Execution Completed, con Task aún Running y sin disposition implícita.
El presupuesto del fixture positivo es explícito; una reserva que no cabe se
rechaza antes de invocar, sin aumentar el budget del producto.
Log revisado `C:/Users/juanc/.codex/m6-writer-permission-20261008.log`:
20 PASS, 0 FAIL/SKIP, 3.136 s. Runner/triada, background/heartbeat/transcript y
validación integral continúan pendientes.

## Criterios que quedan para el cierre integral

La continuación no se considera cerrada por la suma de checkpoints. El estado
final debe acreditar, sobre la misma revisión de código y sus binarios:

- Entrada WorkflowCommand desde CLI/TUI y Explore → Implement → Verify como
  Tasks distintas, con aceptación explícita de cada etapa y Plan reconciliado.
- Check de integración declarado y tool dinámica en el pipeline efectivo de
  permisos, fingerprint y presupuesto. Un proceso arbitrario exitoso o una
  afirmación del modelo no satisfacen Verify. La identidad de la instancia debe
  distinguir especificaciones diferentes para el mismo objetivo.
- Caso positivo que ejercita el punto de entrada real y caso negativo de módulo
  sin cablear, con ReworkRequested observable y sin avance implícito.
- Lectores en background desde el runtime compartido, un único Host, límites
  de capacidad y escritoras serializadas; cancelación y estado propios de cada
  Lane, heartbeat agregado y transcript atribuible en LaneInspector.
- Recuperación idempotente: aceptaciones durables preservadas, sin repetir
  tools/proveedores ni adoptar ownership incierto. La concurrencia de varios
  Hosts sobre SQLite sigue fuera del alcance de M6.
- Compilación completa y targets compatibles, arquitectura, suite integral y
  recuperación M4 serial; registro de revisión, hashes de binarios y resultados
  exactos. Esta comprobación final todavía está pendiente.
