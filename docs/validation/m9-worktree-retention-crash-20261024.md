# Acta M9-L: retención ante terminación del Host

Fecha: 2026-10-24

Base de trabajo: `825614359d05514975f88f0c14a010f9d66d9667`

Alcance: una interrupción controlada del proceso OS Host entre el commit de una
poda Git real y la entrega de su resultado al Tool Runtime, seguida de reapertura
del mismo fixture y replay autenticado del mismo comando.

Estado: aceptado localmente por Luna bajo: **2/2 casos aprobados** en la imagen
final, sin recompilar durante la revalidación. El rechazo independiente anterior
y sus logs permanecen intactos y no se suman al conteo aceptado.
No cambia producción ni agrega seam/capability/API. No es una prueba de caída
eléctrica, kernel panic, atomicidad de lectores Git ni guarantee exactly-once.
M9 global y M8 manual permanecen abiertos.

## Contrato y evidencia previa

La retención existente exige una acción humana firmada y vigente sobre Session,
Run y workspace actuales y las refs/OIDs exactas. El binding histórico solo
identifica la propiedad. `allow_once` conserva su flujo Ask vigente. La transacción
real es `git update-ref --stdin -z --no-deref`; errores, cancelación y resultados
inciertos permanecen conservadoramente Unknown y no se reintentan automáticamente.
La acta aceptada `m9-worktree-retention-20261024.md` prueba replay posterior a
éxito, no la terminación intermedia.

Este foco preparará en una fixture temporal un repositorio Git real y una
propiedad histórica terminal para ejercitar el comando público `worktree.retention`.
La propiedad/binding preexistente de la fixture se etiquetará como preparación
sembrada; no acredita provisión de worktree desde una delegación y no modifica
producción. El Host que recibe y ejecuta la acción será `dotnet omni.dll host
--stdio` como proceso OS propio, con configuración, workspace, data, agent roots,
bootstrap key y PATH exclusivos del fixture. No se heredarán credenciales reales
ni se escribirán keys/nonces a logs.

Un helper ejecutable de `OmniCore.SandboxTestProcess`, copiado bajo el nombre
`git.exe` a un directorio temporal, actuará como proxy solo para ese proceso Host.
Delegará al ejecutable Git absoluto resuelto por la fixture, conservando argv,
cwd, stdin exacto/NUL, stdout, stderr, exit status y el entorno recibido. El proxy
rechaza invocaciones cuyo cwd, `-C`, `--git-dir`, `--work-tree` o variables Git de
ruta salgan de la raíz temporal, o que atraviesen reparse points. El ejecutable Git
resuelto no puede ser el alias/apphost ni un enlace al mismo. Fuera de la poda,
solo pasa consultas `rev-parse`, `symbolic-ref` y `worktree list --porcelain`, con
el prefijo de opciones runtime exacto; otras operaciones se rechazan. La poda
requiere argv runtime completo exacto, cwd del repo fixture, stdin/hash esperado,
refs allowlisted y OIDs esperados. Un `update-ref` distinto se rechaza y nunca se
reenvía. Solo la poda exacta podrá crear una barrera durable de fixture después
de que Git real salga con código cero; después esperará sin devolver resultado.
La barrera registra que el PID proxy es hijo directo del PID Host esperado. El
padre comprobará la barrera, la ref fixture eliminada y la ref centinela ajena
intacta, y terminará únicamente ese Host y su árbol de procesos propios. PATH del
proceso de prueba, Git global, configuración global de Git, repositorios/ref ajenos y
procesos ajenos no se modifican ni se terminan.

La barrera demuestra que el efecto Git ocurrió antes de la terminación controlada;
no se fabricará `ToolCallEffectUnknown` ni un terminal. Se comprobará el `ToolCallStarted`
durable previo al efecto y la ausencia de terminal para ese ToolCall. Luego se
abrirá el mismo workspace/data/journal/CAS con Git normal y PATH original. El
resultado del comando conserva exactamente el estado durable `deferred` del
receipt y la recuperación nativa reporta `blocked /
execution_started_or_uncertain`; nunca se presenta como éxito por la ausencia de
refs. Al iniciar el Host reabierto, la recuperación productiva clasifica el
`ToolCallStarted` como `ToolCallEffectUnknown` y `ToolCallReconciled(Unresolvable)`
para el mismo ToolCallId. Esta clasificación no es sembrada por el test ni prueba
metadata corrupta: el `ReconciliationJson` válido de retención contiene Version/Claim,
pero no es una especificación de reconciliación de filesystem. El reconciliador
existente falla cerrado; este bloque no agrega un reconciliador nuevo. Reenvío
autenticado del mismo CommandId/hash debe devolver el ACK original de admisión y
no iniciar otra ToolCall ni mutar de nuevo las refs observadas. Ese ACK Accepted solo acredita
admisión, no éxito de efecto.

Negativos del mismo método: firma equivocada rechazada; scope Session
ajeno rechazado antes de efecto; mismo CommandId con payload cambiado produce
conflicto; y la ref sentinel ajena mantiene su OID. No se afirma exactamente una
vez para un nuevo CommandId explícitamente autorizado ni atomicidad de lecturas
concurrentes Git. Cualquier ejecución legítima separada queda fuera del criterio.

## Evidencia y preservación

Antes de matar el Host se conservarán para la fixture: configuración redactada,
hashes y bytes de la imagen ejecutable real, argv/cwd no sensibles, PID propio,
hash de payload, respuestas stdio, journal/CAS y estado Git con procedencia
explícita. Para SQLite se hará backup coherente incluyendo WAL o copia offline
solo después de detener el Host y cerrar conexiones propias. Originales y copias
redactadas se identificarán por separado. No se registrarán HMAC keys ni nonces.
Teardown limitado y resiliente restaurará environment/CWD y cerrará solo recursos
propios; cualquier proceso propio que no termine se informa y no se cuenta como
PASS.

Artefactos esperados en `artifacts/m9-worktree-retention-crash/`: build final,
log del único foco, diagnóstico preterminal del intento, snapshots coherentes
de la fixture, manifiesto SHA-256 de fuentes e imágenes ejecutadas, y log seguro
del proxy (argumentos/códigos y hashes fixture, sin secretos). No sobrescribir
actas/logs históricos de retención.

## Resultado candidato

El primer recorrido focal alcanzó el Host OS real y pasó los rechazos por proof
HMAC inválido y Session ajena, pero la preview del Host reabierto dio
`unavailable`; el comando firmado terminó `worktree.retention.scope_changed`, sin
ToolCall ni efecto. La causa comprobada fue de la fixture: el Host recibe TEMP/TMP
debajo de su raíz temporal propia, así que `Path.GetTempPath()` en el proxy ya no
era el padre de la fixture. El proxy rechazaba su configuración antes de ejecutar
Git. No fue un defecto de producción ni un resultado de retención.

La candidata corregida incluye `FixtureTempRoot` absoluto capturado por el runner
antes del Host. La raíz debe ser descendiente de ese padre, tener basename
`omni-native-command-handoff-<GUID N>`, y el CWD, las rutas Git, el ejecutable del
proxy y sus directorios padres deben permanecer dentro de ella sin reparse points.
El temp aislado del Host puede quedar debajo de esa raíz sin alterar la ancla.
Las consultas Git tienen allowlist read-only; el único cambio admitido exige argv,
stdin, refs y OIDs completos exactos. No cambia producción ni PATH del runner.

La campaña M9-L aceptada (13/13) permanece histórica y no se repite ni se suma.
La primera ejecución con el cerco corregido llegó a la barrera: el Host OS obtuvo
preview `eligible` y ACK `Accepted`; Git real eliminó la ref esperada, preservó el
sentinel y el padre verificó `ToolCallStarted` sin terminal. Tras terminar el Host,
el receipt de admisión reportó `ExecutionState=deferred` (acción en curso), no una
conclusión de efecto. Una ejecución posterior pasó esa aserción pero detectó una
carrera de fixture al abrir la barrera mientras el proxy aún la escribía. La
candidata ahora cierra y vacía un archivo temporal antes de renombrarlo al nombre
durable final. Los intentos parciales permanecen preservados y no cuentan como
aceptación.

La implementación de `conversation.result` devuelve directamente el estado durable
del receipt; la consulta tras reopen es `deferred`, no `unknown`. En el estado
offline, inmediatamente después de matar el árbol Host propio, el journal conserva
solo `ToolCallStarted` para el ToolCall objetivo. Durante startup del Host reabierto,
la recuperación productiva agrega exactamente un `ToolCallEffectUnknown` y un
`ToolCallReconciled(Unresolvable)` para ese mismo ToolCallId. El helper de retención
no prueba mutación de filesystem: `Unresolvable` es fail-closed; no se afirma
`Applied`, éxito, metadata ausente/corrupta ni que ausencia de refs demuestre
exactamente una vez. La recuperación nativa observa la invocación admitida/iniciada
como `blocked / execution_started_or_uncertain`.

La candidata ejecutó un `command.continue` firmado, mismo invocation ID y capability
existente `command.invoke.continue.user`; recibe `command.recovery_blocked` y no
avanza el journal. Proof HMAC inválido y payload cambiado del mismo ID también se
rechazan; el replay del comando original devuelve sus campos ACK originales y no
modifica secuencia, refs, ToolCall ni conteo de requests del proveedor. El intento
real llegó a todas estas comprobaciones, pero terminó en una aserción de test que
incorrectamente esperaba ausencia de terminales después del startup recovery. Esa
expectativa se corrigió para diferenciar el estado offline del resultado real de
recovery. No se cambió producción ni se afirma exactamente una vez para un nuevo
CommandId. El único foco final se ejecutará una vez sobre el build congelado; Low
validará después sin reconstruir.

### Primera candidata, anterior al rechazo independiente

Build focal net10: `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj
--no-restore`, 0 warnings y 0 errors. Ejecución directa del runner xUnit v3:
`dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method
"OmniCore.Tests.StdioTransportTests.Native_retention_host_termination_after_real_git_commit_keeps_deferred_receipt_passive"`;
1 método, 1 caso, 1 passed, 0 failed, 0 skipped, 0 not run (30.079 s).

En el estado offline post-terminación había un `ToolCallStarted` y cero eventos
terminales para la ToolCall. Tras el startup del Host nuevo, recuperación agregó
un `ToolCallEffectUnknown` y un `ToolCallReconciled(Unresolvable)` del mismo ID,
sin `ToolCallSucceeded`/`ToolCallFailed`. El receipt permanece `deferred`,
`command.recovery` permanece `blocked / execution_started_or_uncertain`, y el
`command.continue` firmado del mismo invocation fue rechazado con
`command.recovery_blocked` sin avanzar el journal. Proof inválido y payload
conflictivo fueron rechazados; replay idéntico retornó el ACK Accepted original
sin mutar journal ni añadir ToolCalls o requests del proveedor. Conteos de esa candidata:
ToolCall Started=1, EffectUnknown=1, Reconciled Unresolvable=1, Succeeded=0,
Failed=0; requests del proveedor=2; el proxy observó una única mutación Git real;
refs objetivo ausentes y sentinel ajeno intacto.

La evidencia está en
`artifacts/m9-worktree-retention-crash/20261024T220549742Z-bfef67e2ca36463ea7f4bec1b97bba79/`.
Incluye snapshots coherentes de la fixture, `attempt.jsonl`, hashes de fixture y
`frozen-manifest.sha256.txt`, además de las imágenes ejecutadas completas de los
directorios tests/net10 y CLI/net10 (386 archivos, 1,142,997,350 bytes). El
manifiesto incluye SHA-256 de las fuentes del foco, imágenes, `dotnet.exe` y Git
real. `OmniCore.Tests.dll` =
`DA61443764E487EB0E060AE8C30203F7C0A07F48F4568E089292D3C92832145A`; `omni.dll` =
`FF5D489F91A32A9FD1D919DC5083A8ED88C9DE509DE5A304836A7A641F5D3E85`;
`OmniCore.SandboxTestProcess.exe` =
`01FA17CE58AA02F2E05F7792665DAF43148CF123614FB764A8F0B10B85E69D98`.
El intento anterior que solo falló por la expectativa post-recovery sigue intacto
en su propio directorio y no se cuenta en la revalidación final. Esta imagen fue
reemplazada por la candidata corregida descrita abajo; el corte no acredita atomicidad de lectores Git, exactly-once
para un nuevo CommandId ni cierre global M9.

## Correcciones tras el rechazo Low

El informe independiente `artifacts/m9-worktree-retention-crash/low-final-validation-20261024.md`
se conserva sin cambios y permanece como NO APROBADO. Encontró que el primer método
llegó al efecto y recovery, pero el manifiesto intentó leer `data/user.db` desde la
raíz fixture original con `File.ReadAllBytes`; Windows rechazó esa lectura mientras
un handle SQLite seguía abierto. No se atribuye ese handle a un proceso no probado.

La captura ahora detiene primero el Host/runtime in-process propio y limpia los
pools exactos `DataSource=<fixture>/data/user.db`, `DataSource=<fixture>/data/spend-reservations.db`
y `DataSource=<fixture>/.../journal.db`; el Host OS propio se espera terminado,
con el proxy/Git bajo ese árbol. `ReadEvents`/`ReadReceipt` usan stores read-only
desechables. Para cada captura se omiten los bytes crudos `.db`, WAL, SHM y rollback
journal; se usa `SqliteConnection.BackupDatabase` hacia una copia nueva con source
readonly/unpooled, se cierra cada conexión, y se verifica `PRAGMA integrity_check`
y SHA-256 de cada backup. `sqlite-backups.json` identifica los paths y resultados.
El manifiesto SHA enumera solo el árbol de evidencias copiado, no lee la raíz fuente;
se escribe mediante archivo temporal, se vuelve a leer/verificar contra los archivos
copiados e incluye el `attempt.jsonl` final. El root temporal original permanece
marcado para retención hasta que snapshot y manifiesto verifiquen; cualquier error
de captura/sellado conserva el root original y falla el método. En un fallo del
método por otro motivo también se conserva el root para diagnóstico.

El segundo fallo Low ocurrió en setup Git bajo `HOME` aislado. La hipótesis inicial
de identidad quedó refutada: producción ya entrega identidad explícita a
`commit-tree`. El helper de test deja `user.name=OmniCore Fixture` y
`user.email=fixture@example.invalid` en el `.git/config` temporal como configuración
propia, sin atribuirle la causa del fallo ni consultar/modificar identidad global.
La validación corregida ejecuta solo los dos
métodos Low dentro de runner aislado desde el primer intento, con HOME/USERPROFILE,
agent roots, data/config, TEMP/TMP, DOTNET_CLI_HOME y NUGET_PACKAGES propios, PATH
original y sin pasar variables de credenciales.

### Resultado después de Low y candidata actual

El informe Low permanece sin cambios y **NO APROBADO** para la candidata anterior.
La hipótesis de identidad no explica el segundo fallo observado: en la fixture
aislada el `commit-tree` y la ref de snapshot ya existían; la operación exacta
`git -C <repo fixture> worktree add --detach -- <target fixture> <snapshot commit>`
terminó con código 128 y `fatal: '$GIT_DIR' too big`. La ruta del target medía
233 caracteres y el Git common dir 155. Una reproducción Git mínima, propia y
separada, con repo de 51 caracteres, `.git` de 56 y target de 49, completó
`git init`, commit y `worktree add` con código 0. No se cambió `longpaths`, Git
global, OS ni registro.

Para la nueva validación se usó runner aislado con raíz exclusiva
`%TEMP%\rfb9c3807` (43 caracteres) y `TEMP/TMP` directamente en su subdirectorio
`t` (45 caracteres). Se verificó el marcador aleatorio de propiedad y la
contención antes de cada lanzamiento. HOME/USERPROFILE, agent roots, data/config,
DOTNET_CLI_HOME, NuGet y credenciales siguieron aislados/falsos; PATH del hijo
fue el original. Las dos invocaciones iniciales con `-method` no cualificado
seleccionaron 0 pruebas y no se cuentan. Con el nombre completo se ejecutaron
dos focos sobre la DLL entonces vigente: el crash llegó a todas las comprobaciones
de recovery pero falló únicamente porque `VerifyOwnedSnapshot` exigía que el
SHM temporal creado por su propia lectura read-only también midiera cero; el WAL
era de cero bytes. El foco de autoridad pasó 1 seleccionado/1 aprobado/0 fallidos
(11.196 s). Ese pase fue con una DLL anterior al ajuste final del verifier y no
se presenta como validación de la imagen final.

El verifier final exige hashes de DB iguales antes/después de `integrity_check`
y sidecars ausentes antes de abrir cada copia. Tras cerrar la conexión no pooled,
solo en la copia capturada acepta/elimina un WAL o rollback journal de cero bytes
y el SHM creado por la lectura; si WAL/journal tienen datos, el hash cambia, no
se puede cerrar, o no se logra quitar los sidecars, falla y conserva el original.
Nunca lee/elimina sidecars de la fuente SQLite. La copia final registra integridad
`ok` y SHA por DB en `sqlite-backups.json`.

Build final después de ese ajuste: `dotnet build
tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore`, 0 advertencias/0 errores
(7.38 s). Sobre esa imagen se ejecutó únicamente
`OmniCore.Tests.StdioTransportTests.Native_retention_host_termination_after_real_git_commit_keeps_deferred_receipt_passive`:
1 seleccionado, 1 aprobado, 0 fallidos, 0 omitidos/no ejecutados (30.325 s).
El `attempt.jsonl` final registra Host/fixture Host terminados, snapshot verificado,
manifiesto verificado, ACK/replay pasivo estable, `deferred` + `blocked /
execution_started_or_uncertain`, y la clasificación productiva 1
`ToolCallEffectUnknown` + 1 `ToolCallReconciled(Unresolvable)` para el mismo ID;
no hubo terminal falso, efecto Git repetido ni request provider adicional. El
foco de autoridad pasó antes del ajuste final y debe ejecutarse de nuevo por Low
en los binarios congelados; no se repitió aquí.

Intentos fallidos preservados y excluidos de aceptación: selección no cualificada
con 0 casos; crash con verifier que rechazaba SHM de 32768 bytes; y anteriores
capturas Low. El corte sigue sin demostrar una caída arbitraria del SO, atomicidad
de lectores Git o exactly-once para un nuevo CommandId. La candidata actual queda
validada por Low sobre ambos métodos según el dictamen final inferior, sin cerrar SEC/M9/M8 global.
El manifiesto final de fuente e imagen y los tres binarios distintos respecto de
la imagen base anterior se conservan en
`artifacts/m9-worktree-retention-crash/final-candidate-freeze-20261024T223131095Z/`;
la imagen base preexistente se cita por ruta y se complementa mediante esos
overrides exactos, sin duplicar cientos de MB de archivos idénticos.

El árbol actual del runner net10 contiene 225 archivos y 580,044,333 bytes;
comparado con la imagen base congelada, son 225 archivos y 580,020,477 bytes,
con cero altas/bajas y exactamente tres diferencias. Los overrides byte exactos
son `OmniCore.SandboxTestProcess.dll` (66,048 bytes,
`F089A61820102688D4DE221DAEB08D08B82182DF7BAF014A1EB5D3DFC30A6541`),
`OmniCore.Tests.dll` (8,627,200 bytes,
`BF0F9DDD1A85A82E27CA22C70D78C73FCE45CEBC50B76C4714C5CF560F9879ED`) y su
PDB (1,850,408 bytes,
`8444EE9EC0E2854E1F97503AEE2FD81C47AD944ABD33A351E925502A24B16FC5`). Los 222
archivos restantes son idénticos a la imagen base. El Host ejecutado fue el
`omni.dll` copiado a la fixture (851,968 bytes,
`FF5D489F91A32A9FD1D919DC5083A8ED88C9DE509DE5A304836A7A641F5D3E85`); el
apphost `git.exe` derivado del helper tiene SHA-256
`01FA17CE58AA02F2E05F7792665DAF43148CF123614FB764A8F0B10B85E69D98`. La
captura final incluye tres backups SQLite con integridad `ok`: `user.db` 94,208
bytes, `spend-reservations.db` 20,480 bytes y el journal workspace 155,648 bytes;
`sqlite-backups.json` guarda los hashes individuales y si el origen tenía WAL.
Los manifiestos SHA de la imagen reconstruible, overrides, fuentes, intento y
captura están en el directorio freeze indicado arriba.

## Aceptación independiente final

Luna bajo ejecutó secuencialmente los dos métodos acordados en la imagen final,
sin build: `Native_retention_host_termination_after_real_git_commit_keeps_deferred_receipt_passive`
y `Native_retention_uses_current_run_authority_for_expired_refs_of_a_terminal_run`.
Resultado aceptado: **2 métodos, 2 casos, 2 PASS, 0 errores/fallos/omisiones/no
ejecutados**. No se suman los 13 casos históricos, los intentos fallidos ni las
selecciones de cero casos. El rechazo anterior permanece en
`artifacts/m9-worktree-retention-crash/low-final-validation-20261024.md`, SHA-256
`5F5C6727F295E323B71563853C3AE0241DD8D3E91DA1E0DC17E7DC4EE04C6EA7`.

El dictamen nuevo es `artifacts/m9-worktree-retention-crash/low-revalidation-20261024.md`,
SHA-256 `63D53F5613822F837C2377446740D145F393A41EB073AC0F0234B7E6320EF0E4`.
Logs: `low-revalidation-01.log` y `low-revalidation-02.log`. El runner usó la raíz
exclusiva corta `r6848f839`, TEMP de 45 caracteres y raíces HOME/agentes/config
aisladas, sin credenciales heredadas ni cambio del PATH global.

Low verificó los 225 outputs, seis entradas de fuentes/acta y tres overrides
antes/después, sin discrepancias. Los manifiestos congelados
`source-sha256.txt` y `tests-net10-current.sha256.txt` conservan respectivamente
SHA-256 `D7276D0CCD8D2EBF49E9CCA99A9BCB1DF16BF245050D685B3F516F12F9199802`
y `57AA1B00C6B90B8F9C1BFD473DFE07647C6D61EBB540D3528DF35A47869FDC41`.
Esta acta se edita después del dictamen; código y binarios validados no cambian.

La captura independiente está en
`artifacts/m9-worktree-retention-crash/20261024T223801936Z-054900535145485a90babaaebfc46188/`.
Su manifiesto cubre 670 archivos verificados y sus backups SQLite conservaron
hashes, tamaños e integridad. No se eliminaron sidecars de fuentes; los temporales
del lector solo se retiraron en copias después de cerrar conexiones, con WAL
vacío y SHA de la base intacto. Los PIDs propios registrados ya no estaban activos.
Quedan raíces y capturas propias retenidas para auditoría, sin ejecución activa.

El receipt offline conserva `ExecutionState=deferred`; la consulta pública de
ejecución después de reabrir también devuelve `deferred`. Recovery produce
EffectUnknown/Unresolvable y bloquea la continuación. No se presenta el ACK
Accepted ni la ausencia física de refs como prueba de éxito. El proxy solo
instrumenta la poda previa al corte: después se usa Git normal, por lo que no
se afirma una auditoría exhaustiva de todos los procesos Git posteriores.

El bloque acepta una terminación forzada controlada del Host OS con barrera
externa de fixture. Binding histórico preparado, no provisión; ningún cambio de
producción, capability o autoridad. Crash de cleanup/discard sigue como evidencia
separada pendiente; tampoco se garantizan caídas del sistema, otros puntos de
corte, atomicidad de lectores o exactly-once de un nuevo ID. Sin cambios de
OmniCoder, push, merge, CI remota, instalación ni cierre global M9/M8.
