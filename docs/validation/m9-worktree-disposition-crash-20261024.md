# M9-L: cleanup/discard ante terminación intermedia del Host

Fecha: 2026-10-24
Base: `613a0fda7755613803822eef7c119ae9361c5991`
Estado: aceptado localmente por Luna bajo, 3/3 casos; no cambia producción.

## Contrato y hueco

La aceptación de `m9-worktree-disposition-20261024.md` comprueba Ask, ownership,
snapshot revisado, archivado, barrera y recibos, pero su reapertura ocurre después
del éxito. El bloque de retención ante terminación del Host valida el patrón de
recovery general para una poda distinta; no acredita cleanup/discard.

`WorktreeDispositionHostCoordinator.Prepare` conserva receipt protegido y
`Reversibility.Unknown`. `GitWorktreeStore.CleanupCoreAsync` ejecuta la mutación
física final `git worktree remove` y luego persiste metadata `State=removed`. El
corte elegido está después del `worktree remove` real con código 0, antes de
devolver el resultado al Host. El proceso será el Host `omni.dll host --stdio`
propio del test y se terminará solo por PID/árbol creado por la fixture. No se
siembra ToolCallStarted ni un estado Unknown.

## Recorrido previsto

Una única teoría parametrizada ejercita `cleanup` y `discard` con la acción humana
nativa firmada, política `Ask` y respuesta `allow_once`. El binding/Task/Lane
terminal se prepara en el fixture como en la aceptación de disposición anterior;
esta prueba no acredita provisión de delegación. El Host OS recibe la petición por
stdio y el helper Git real/proxy se limita al repositorio y rutas de esa fixture.

En cleanup, la barrera conserva el snapshot/ref original mientras el checkout ya
fue retirado. En discard, la ruta previa autorizada captura el árbol revisado,
escribe el retained ref y metadata de archivo; la barrera se publica después de
que `worktree remove --force` retire el checkout. El padre comprueba el tree y
bytes archivados antes de matar el Host.

Antes del corte debe existir exactamente un ToolCallStarted sin terminal. Al
reabrir los mismos datos/CAS, se observarán `deferred` y `blocked /
execution_started_or_uncertain`, más la clasificación productiva
`ToolCallEffectUnknown` y `ToolCallReconciled(Unresolvable)`. La ausencia del
checkout no se presenta como éxito ni como prueba suficiente del resultado.
`command.continue` firmado del mismo invocation se rechaza; reenvío idéntico
devuelve el ACK original sin mutar journal ni repetir Git, ToolCall o proveedor.
Se incluyen rechazos previos al efecto para proof inválido y scope Session ajeno,
y conflicto al reutilizar el mismo ID con payload diferente.

## Fences del helper

La configuración de esta fixture cerca CWD, repo, worktree y variables `GIT_*`
de índice/objetos al root temporal canónico, sin reparse points. El Git real se
resuelve por ruta absoluta y se distingue del apphost proxy. Las lecturas Git
tienen formas/argumentos acotados. Para discard, los
comandos previos que escriben solo pueden usar el índice/objects directory privado
de la operación, árbol revisado y parent revisado; `update-ref` solo puede crear
la ref retained de este ownership hacia el commit cuyo tree/parent coinciden.
La única mutación que produce barrera es `worktree remove` con ruta exacta y modo
force esperado. Cualquier otra ref, OID, cwd, índice, worktree u operación
destructiva se rechaza. No se toca PATH global, Git global, procesos externos ni
repositorios reales.

## Criterios de evidencia

Después de construir se ejecutará el foco parametrizado (dos filas) y, si el
helper Git compartido cambia, una regresión del corte de retención ya aceptado.
Un ACK `Accepted` solo prueba admisión. El acta final separará disposición limpia
de archivo discard, crash controlado por barrera de caída aleatoria del SO, y
receipts de admisión de outcomes de efecto. Los snapshots SQLite se harán con
`BackupDatabase`, conexiones sin pooling y `integrity_check`; se preservará el
fixture original ante falla de captura. Cualquier resultado incierto permanece
incierto y bloqueado; no se declara exactly-once para otro CommandId.

## Resultado de la candidata congelada

Estado: candidata construida y aceptada por validación independiente Low.
No hubo cambios de producción ni de autoridad.

El corte se alcanzó en un Host OS real (`dotnet omni.dll host --stdio`) y un
helper Git apphost propio, con un repositorio temporal propio. Cleanup y discard
corrieron bajo la misma Session/Run propietaria, con Lane/Task terminales y
acción firmada `allow_once`. Cada fila observó un único `ToolCallStarted` antes
de matar por PID/árbol el Host de la fixture. Discard verificó el retained ref,
árbol y bytes revisados; cleanup conservó el snapshot ref. El checkout había
sido retirado por Git con exit code 0 antes del corte, mientras metadata aún no
registraba `removed`.

Al abrir los mismos datos, ambos casos conservaron el receipt de admisión
`Completed/Accepted`, con `ExecutionState=deferred`; `conversation.result`
devolvió `deferred` y `command.recovery`, `blocked / execution_started_or_uncertain`.
La recuperación registró los eventos productivos
`ToolCallEffectUnknown` y `ToolCallReconciled(Unresolvable)`. La continuación
firmada del invocation quedó bloqueada. Repetir el mismo command devolvió el ACK
original sin cambiar journal, refs/árbol/bytes, número de llamadas al proveedor
ni volver a iniciar el efecto. Proof inválido se rechazó antes de admisión;
Session ajena se rechazó con receipt `Rejected` y sin mutar el journal de la
Session propietaria; reutilizar el mismo ID con payload alterado se rechazó como
conflicto. No se infiere éxito por la ausencia del checkout y no se afirma
exactly-once para un ID nuevo.

| Método ejecutado | Casos | Resultado |
| --- | ---: | --- |
| `Native_worktree_disposition_host_crash_after_real_git_remove_recovers_uncertain_and_passive` | cleanup, discard (2) | 2/2 PASS, 143.543 s |
| `Native_retention_host_termination_after_real_git_commit_keeps_deferred_receipt_passive` | 1 | 1/1 PASS, 30.212 s |

Build final: `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj
--no-restore`, 0 advertencias y 0 errores. Ejecutor: xUnit v3 In-Process Runner
4.0.1, .NET 10.0.12; ambos métodos seleccionados directamente por FQN desde la
DLL, con 3 casos ejecutados y 0 omitidos. No se ejecutó suite completa.

El primer watchdog de 30 s no alcanzó la barrera aunque la copia SQLite mostraba
el único `ToolCallStarted`, sin terminal, y el proxy había permitido 268
invocaciones Git sin rechazo. Se amplió solo el watchdog del test a 120 s por
operación. En la candidata, cleanup y discard llegaron a la barrera y las dos
filas terminaron en 143.543 s. La fila discard reveló además que las capturas
productivas sucesivas de un mismo tree/parent generan OID de commit distintos
por timestamp. El helper conserva el primer OID que luego fija la ref y permite
los commits auxiliares posteriores solo con tree, parent, comando y CWD exactos;
el estado/ref fijados no se reemplazan. Esto es lógica de la fixture, no un
cambio de producción.

Intentos de desarrollo fallidos (0/2 de aceptación) quedan preservados en
`artifacts/m9-worktree-disposition-crash/` junto con sus copias SQLite/CAS,
diagnósticos del proxy y hashes. No se suman a los 3 casos de la candidata.
Los logs finales son `final-theory-high.log` y `final-retention-high.log`; los
resúmenes por operación y snapshots quiescentes están en
`20261025T031359162Z-c3543c0391c549518c09708d502d0589` (cleanup) y
`20261025T031504929Z-684ee2a142234556928063049a3a99b6` (discard), ambos con
`success:true`. El manifiesto
`candidate-manifest.json` identifica fuentes, DLL de tests, CLI y helper
realmente usados.

La caída es controlada por una barrera después del efecto Git; no demuestra una
caída aleatoria del sistema operativo ni atomicidad entre Git y SQLite. La
recuperación permanece fail-closed y pasiva. Esta acta no cierra M9 ni otros
bloques de SEC-CONV.

## Aceptación independiente

Luna bajo revisó la candidata y ejecutó secuencialmente los mismos dos métodos,
sin build ni restore: **3/3 casos aprobados**, cero fallos, omisiones o casos sin
ejecutar. Las filas cleanup/discard tardaron 145.740 s; retención, 30.514 s.
Antes y después coincidieron las cuatro entradas de fuentes/documentación y los
140 binarios del manifiesto, SHA-256
`F69995978E9134D05792305004A2B94139D2017CA6FADEF5DE5984ECEAE85E9C`.

Informe local: `artifacts/m9-worktree-disposition-crash/low-validation-20261024.md`,
SHA-256 `B47DAA7B2F7114D21C4B47323BF1F187B596AF15CB2E117DDC809F0B13D0E9A7`.
Los logs `low-validation-01.log` y `low-validation-02.log` contienen la salida
capturada. Un intento inicial perdió salida/exit code por asignar la variable
reservada PowerShell `$pid`; se esperó su salida antes de repetir únicamente
el método afectado. Quedó excluido del conteo en `low-launch-01.log`.

Los manifiestos nuevos de cleanup, discard y retención verificaron 459, 474 y
666 archivos respectivamente, con tres backups SQLite íntegros por captura.
Los PIDs propios habían terminado; `x` conservó su hash. La edición posterior
de esta acta es documental: no modifica código ni binarios validados.

El binding y los terminales de Task/Lane se preparan en fixture; no acredita
provisión. Tras reabrir se usa Git normal, por lo que no se afirma auditoría
exhaustiva de procesos Git posteriores. El alcance es Windows local. No hubo
push, merge, CI remota, instalación ni cambios de OmniCoder. M9 global y M8
manual permanecen abiertos.
