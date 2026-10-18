# M9-L: handoff nativo durable de comandos

## Estado actual: cierre de denegación y aceptación de reapertura (2026-10-18)

**Publicación comprobada:** Core `c56123e`, Coder `2c31071`, ambos en main remoto.
La [CI Coder 39168400950](https://github.com/juancgomezm-s/OmniCode/actions/runs/39168400950)
terminó completa aprobada (Windows, Ubuntu y package gate). La primera publicación
Coder `f76aed3` falló el mismo unitario en los tres jobs: el fixture usaba
`command.recovery` y el código consulta `workflow.recovery`. La corrección
test-only `da9ce39` conserva fencing/CAS; archivo completo del adapter 23/23 PASS,
1.13 s. Bundle de producción sin cambios. En la
[CI Core 39167620380](https://github.com/juancgomezm-s/OmniCore/actions/runs/39167620380)
aprobaron los cinco jobs completos: suites Windows/Ubuntu, instaladores npm
Windows/Ubuntu y paquete Linux. El log Ubuntu registra 4,888 aprobadas, 0 fallos
y 24 omisiones. No se extrapola ese resultado al incremento ACT posterior.

**Aceptación local del bloque:** Core `582dc9c`, Coder test `b919802`, fuente/bundle
de aplicación `1fb3639`. `deny` revalida autoridad, etapa Verify Accepted,
recibo, interacción, causación y scope exactos, sin reconstruir la ejecución ni
recapturar el filesystem para denegar. Los drops de steering pendientes, el Turn,
AgentExecution, Task, Lane, Delegation y NativeCommandInvocationFinished(failed)
se cierran en una sola barrera. Los terminales del hijo conservan causación de la
respuesta; el cierre de la invocación conserva la suya. La atomicidad se revisó
en código; no se ejecutó una inyección de crash dentro del commit SQLite.
`consent_once` conserva las validaciones físicas y de autorización.

| Validación sobre el freeze final | Resultado |
|---|---|
| `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore` (incluye CLI) | PASS, 0W/0E, 49.94 s |
| Runner serial, `WeakSandboxConsentPreflightTests.*` | 7/7 PASS, 0 fallos/omisiones/no ejecutados, 0.763 s |
| Electron real, workflow `reopen-deny` | 1/1 PASS, 47.404 s; 23 casos ajenos excluidos por filtro |
| Electron real, workflow `reopen-consent_once` | 1/1 PASS, 84.825 s; 23 casos ajenos excluidos por filtro |
| `scripts/Assert-NoOrphanWorkers.ps1 -Etapa 'M9 workflow reinicio aceptado'` | NO_ORPHANS |

Las dos E2E se ejecutaron desde la raíz OmniCoder, con
`OMNICORE_E2E_ENTRY=<Core>/src/OmniCore.Cli/bin/Debug/net10.0/omni.dll`:

```powershell
npm exec --workspace @omnicoder/app -- vitest run --config vitest.e2e.config.ts test/e2e/omnicore-host.e2e.test.ts -t 'verificador reales \(reopen-deny\)' --reporter verbose
npm exec --workspace @omnicoder/app -- vitest run --config vitest.e2e.config.ts test/e2e/omnicore-host.e2e.test.ts -t 'verificador reales \(reopen-consent_once\)' --reporter verbose
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method '*OmniCore.Tests.WeakSandboxConsentPreflightTests.*' -parallelMode none
```

El último comando corresponde a la raíz Core. SHA256 del CLI usado:
`1F7EC65CD9A5854B82107E948B13B6C64F88110BDA9DFE12AF23F259650DF7F7`;
bundle main Coder:
`F09D17EFD34DF947F33BA20235C629B93886542E7194FBEE50CE322CCF72488B`.
Los logs privados completos son `m9-reopen-deny-582dc9c-vitest.private.log` y
`m9-reopen-consent-once-582dc9c-vitest.private.log` en Temp; root comprobó ambos.

La reapertura conserva Session/Run y la interacción exacta. Consultar y reconectar
son pasivos. Denegar conserva nueve HTTP locales, cero inicio del verificador y
exige un único terminal de cada entidad antes de Finished. Consentir acredita
diez HTTP locales, un único verificador real exitoso, su resultado exacto al
modelo, workflow completed y raíz intacta, sin integración automática. El helper
de reapertura conserva la reserva de backend en settings del store aislado.
Se retiraron las trazas temporales y la observación posterior al timeout.

Límites: HTTP fixture, sin inferencia pagada/live; los siete unitarios prueban
ToolRuntime y no son pruebas negativas del matcher Host para receipt cambiado o
ToolCallStarted previo. El foco CLI de tres casos descrito abajo aprobó sobre
`c84a2e7`, no se repitió sobre `582dc9c`. El primer build de `47a440b` detectó
CS1503 y no llegó a pruebas; `582dc9c` lo corrige. Este bloque no acredita
procesos escritores generales, ConPTY, SEC-CONV ni aceptación global M9. M8
espera pruebas manuales. La publicación y su CI se comprueban por separado.

## Histórico de construcción y diagnósticos del verificador

**Fuentes en aceptación:** Core `c84a2e7` elimina las trazas temporales y añade
dos correcciones: no consulta supervisión global cuando ya existe disposición
del resultado exacto de la ejecución (los gates posteriores siguen vigentes),
y cierra el Turn hijo sólo tras revalidar una denegación Weak humana canónica,
su causación, rango y todos los IDs. La ruta de fallo existente debe cerrar
Task/Lane/AgentExecution/Delegation. Se amplía la E2E para exigir esos terminales,
además de cero inicio del verificador. No se declara aceptada hasta pasar.

Sobre `c84a2e7`, build de `OmniCore.Tests.csproj --no-restore` **PASS, 0W/0E,
69.31 s** (incluye CLI). Runner serial del ensamblado, método
`CliEndToEndTests.Typed_workflow_runs_real_filesystem_and_verifier_tools_and_reworks_unconnected_module`:
**3/3 PASS, sin fallos/omisiones/no ejecutados, 71.372 s**. Cubre módulo conectado,
módulo sin conectar y deuda/quiescencia al añadir una solicitud de tool posterior
a Verify mediante hook de test. Ese tercer caso no modifica físicamente archivos
después de verificar. No sustituye la E2E de consentimiento/reinicio.

La E2E ampliada `reopen-deny` todavía falla **69.935 s**: reabre y registra la
decisión, pero no alcanza los terminales dentro de su espera. La observación
diagnóstica posterior conserva el recibo **antes del cleanup** con ExecutionState
y ExecutionCode nulos, journal hasta 173 y sin finalización tras 30.128 s extra.
La etiqueta `failed/runtime_failed` de una consulta SQL posterior al cierre no
describe ese estado vivo y no se usa como evidencia de retorno.

El sampler muestra avance por numerosos Git cortos; un `wait.begin` final sin
`wait.exit` no demuestra bloqueo, pues las salidas se muestrean. No se identificó
un Git individual detenido. Un preview de sólo lectura sobre una fixture anterior
equivalente aprueba en **3.259 s**, y con stdin pipe abierto en **3.240 s**; con
sampler, cien llamadas Git completan en **3.379 s**. No se reejecuta el ToolCall.
El diagnóstico más reciente procede de workspace `AevM7P` / store `idyWnd`;
las rutas `aITCdu` / `0dhbSC` corresponden al intento anterior.

La reparación siguiente cierra la denegación desde sus hechos canónicos y el
fallo real de la delegación, sin reconstruir ni lanzar el child para rechazarlo.
Debe conservar todos los checks y recapturas físicas para `consent_once`; no se
declara implementada ni aceptada todavía.

**Corte histórico:** Core `1e9b7dd`, Coder fuente/bundle `1fb3639`:
`consent_once` **PASS (95.348 s)** completo; CLI **0W/0E, 1:47.24**.
Los dos casos de reapertura fallaron al conectar el Host nuevo, antes de iniciar
el runtime o probar la continuación corregida. Una conexión pasiva posterior al
mismo store/workspace conservado aprobó **1/1 (5.94 s)**, sin errores de página,
sin iniciar runtime ni llamar al proveedor. No sustituye la aceptación de la
secuencia de reinicio del workflow. Se conserva la corrección y se observa esa
secuencia antes de publicar; no se atribuye el fallo de conexión al replay sin
evidencia.

La repetición `reopen-deny` con captura de conexión volvió a fallar **99.219 s**:
status `disconnected`, sin PID/Session/generación ni errores de página. No llegó
a la respuesta humana. La inspección localizó un defecto del harness: `launchApp`
sobrescribe `userdata/settings.json` al reusar el store y borra `workspaces`,
incluida la reserva OmniCore. El arranque automático del renderer entra entonces
en legacy/demo y compite con Connect. Se corrige la conservación del archivo y
se exige comprobar el backend guardado antes de validar otra reapertura; no se
relajan los guards del producto.

Coder test `68a0c76` conserva/valida ese archivo y comprueba la reserva del
workspace antes y después de reabrir. El siguiente `reopen-deny` supera conexión
y restauración de la misma Session/Run, conserva nueve HTTP y registra
resolución/PermissionDenied/RunInteractionResumed (171–173). Sigue **FAIL,
71.407 s**: no aparece Finished ni arranca el verificador; la corrección `1e9b7dd`
no acredita aún el cierre. Store `%TEMP%/e02-e2e-store-WcISSG`. Se localiza el
punto de salida con instrumentación temporal acotada, que debe retirarse antes
de publicar.

**Corrección de la hipótesis anterior:** el diagnóstico posterior llega a
`implement.verify-stage.Accepted`, con identidad y TaskPacket válidos, y entra
al preview físico. Los Git observados terminan normalmente (25–272 ms); la
captura de 8 KiB se trunca durante ese trabajo. El receipt de respuesta leído
**antes del cleanup** conserva ExecutionState/ExecutionCode nulos: la ejecución
todavía estaba activa al vencer la espera. No demuestra retorno 3/deferred ni un
Git individual colgado. La reparación en construcción eliminará la consulta
global de supervisión repetida para una ejecución que ya tiene disposición
durable, manteniendo las comprobaciones posteriores. También se audita el cierre
terminal del Turn tras denegación explícita.
Estas reparaciones todavía requieren su aceptación real.

`1e9b7dd` evita un segundo preview no ligado al reproducir Implement si Verify
ya está admitido: exige su dependencia exacta y vuelve a validar admisión,
ownership, artifact/TaskPacket y árbol físico actual. Un binding inválido registra
deuda de evidencia y falla. La ejecución inicial conserva el preview existente.
Esta ruta fría todavía no tiene aceptación de extremo a extremo.

**Corte anterior:** Core `e07dbdc`, Coder fuente/bundle `1fb3639`:
`consent_once` **PASS (84.095 s)** completo. La reapertura ya restaura la tarjeta
y limpia la alerta obsoleta de scope. Denegar persiste resolución User, luego
PermissionDenied y RunInteractionResumed, con los mismos IDs y cero inicio del
verificador; conserva nueve HTTP. Falta cierre: el receipt de respuesta termina
`deferred/input_required`, la invocación no tiene Finished y el caso falla.
El store `%TEMP%/e02-e2e-store-gZFtqJ` conserva seq171–173 y recibos originales;
Triage privado y diagnóstico del Host nuevo se usan para localizar el retorno 3,
sin cambiar límites ni reintentar la llamada original. No se declara aceptado
ni publicado este incremento.

**Histórico de la aceptación normal y reparación de reapertura:** Core `e07dbdc`, bundle Coder `c90ffef`, test `49a0968`:
escenario `consent_once` **PASS (80.621 s)**. Acredita diez HTTP locales,
un único proceso verificador exitoso, resultado exacto al modelo, workflow
`completed`, identidad Session/Run, razonamiento y raíz intacta. Escenario
`reopen-deny` **FAIL (60.810 s)**: la tarjeta no reaparece después de reabrir,
con `native_command_scope_unavailable`; la interacción sigue pendiente y no
hay lanzamiento ni HTTP adicional. Store `%TEMP%/e02-e2e-store-o335Nn`.
Se repara esta restauración antes de aceptar/publicar el incremento. El fallo
no se cuenta como aceptación de denegación ni recuperación completa.

Coder `9b0ac66` corrige la rama de reapertura: el replay Weak ya no evita la
consulta de `interaction.recovery.weakSandbox`. La tarjeta exige concordancia
exacta de IDs, opciones, ejecutable y directorio con la solicitud replayada.
No crea grants ni despacha un comando al consultar. Foco adapter **4/4 PASS**
(19 filtrados, 476 ms): coincidencia canónica, ToolCall distinto, sujeto distinto
y solicitud ya cerrada. Build app **PASS, 5.426 s**; scanner **PASS**, 37 archivos,
157 canales main/preload y 155 métodos Bridge. Bundle main SHA-256
`F09D17EFD34DF947F33BA20235C629B93886542E7194FBEE50CE322CCF72488B`.
La E2E afectada de reapertura/denegación queda pendiente de resultado; no se
repite el recorrido normal ya aprobado porque esta corrección sólo cambia
la restauración de una solicitud Weak replayada al abrir la Session.

M9 sigue abierto y M8 espera aceptación manual. La publicación citada más abajo
no acredita este incremento de rama. El consentimiento de una sola llamada ya
tiene ruta Host/Protocol/SDK/adapter y presentación en OmniCoder; su aceptación
de extremo a extremo sigue pendiente. No se ofrece un grant Weak para todo el Run.

Evidencia local: seis casos del pipeline Weak aprobaron con launcher contado de
prueba; dos casos Host/stdio aprobaron negociación explícita de capacidad; dos
casos del adapter aprobaron recuperación pasiva y cierre por replay. Estas pruebas
no acreditan por sí solas lanzamiento OS ni el diálogo de Electron. Core CLI
`fe95168` compiló sin advertencias/errores en 46.17 s; el bundle de OmniCoder
`8af7afa` compiló y su scanner de arquitectura aprobó (37 archivos; 157 canales
main/preload y 155 métodos Bridge). No son CI ni aceptación global.

La primera E2E posterior falló en 68.22 s, con nueve HTTP locales: Explore e
Implement completan, Verify llega a Prepared/Authorized y pide consentimiento,
pero el diálogo no aparece. El journal conservado de
`%TEMP%/e02-e2e-store-fWYYxP` termina con InteractionRequested (169),
RunAwaitingInput (170) y NativeCommandInvocationFinished failed (171), sin
ToolCallStarted ni proceso verificador. La interacción conserva Task/Lane/Turn
pero omite ToolCallId en su envelope: el consumidor exacto no puede vincularla.
Se corrige la atribución al persistirla, conservando los demás IDs del scope;
no se infiere una llamada por cercanía ni se relajan los guards. También se
conserva la corrección para reanudar el ejecutor del worktree hijo. Ninguno de
estos cambios se cuenta como aceptado hasta completar el recorrido real.

Core `3c278cf` corrige la atribución directa en ScriptedToolExecutor. El foco
posterior aprueba **7/7**, sin fallos ni omisiones (0.895 s): conserva los seis
casos de ToolRuntime y añade un caso ScriptedToolExecutor/EventStream/SQLite.
Éste inicia con ToolCallId ausente del scope ambiente y comprueba el ID exacto
del call en la interacción persistida, manteniendo Session/Run/Task/Lane/Turn y
ExecutionId. No siembra la interacción. Usa un preflight/launcher controlado y
no acredita proceso OS. Build del proyecto de tests y CLI: 0W/0E; el incremental
tras ajustar el caso final tomó 13.19 s. La E2E sobre ese artefacto sigue pendiente.

Los diagnósticos posteriores acreditan recuperación pasiva real: Core `5192ce4`
devuelve Weak `pending` con todos los IDs y receipt `deferred/input_required`.
OmniCoder `bf9ea87` decodifica los 33 descriptores del Host; `c90ffef` también
lo acredita por el bridge de Electron, sin ejecutar un comando. El SDK acepta
la gramática de la capacidad `weak-sandbox` y conserva un límite de 64
descriptores; no amplía herramientas del modelo ni permisos. El foco SDK prueba
33 entradas, binding exacto y rechazo de 65 o de un binding inconsistente.

La E2E siguiente (`5192ce4`/`c90ffef`, 48.24 s) muestra la tarjeta y pulsa
`consent_once`, pero el Engine rechaza RunInteractionResumed porque sus dos
validadores todavía sólo admitían ModelRouteConsent. Conserva nueve HTTP y
cero ToolCallStarted. Core `870d60f` extiende tracker y replay al consentimiento
Weak exacto, con resolución humana, misma causación y scope, llamada preparada
y autorizada, delegación/ejecución canónicas y ningún lanzamiento previo. La
auditoría corrigió el orden usando el journal real: Created → Started → Accepted
→ InteractionRequested. Esta corrección sigue sin aceptación de extremo a
extremo; denegación y recuperación del pendiente también deben acreditarse.

El recorrido posterior Core `1d7c7c4`/Coder `c90ffef` acredita la tarjeta,
resolución humana `consent_once`, RunInteractionResumed y el inicio real del
proceso verificador. Sigue fallando: nueve HTTP, exit 1 y ToolCallFailed
`PROCESS_FAILURE`, sin gate completado (106.449 s). Journal conservado en
`%TEMP%/e02-e2e-store-WjIUpO`. No acredita el cierre del workflow.

La causa se aisló con copias nuevas del proyecto hijo, sin reintentar la llamada
original: el launcher Weak real y su entorno filtrado reproducen NETSDK1060
(`path1` nulo); añadir únicamente `APPDATA` mediante la allowlist existente da
exit 0. No se hereda el entorno completo ni se amplía el permiso del proceso.
Core `e07dbdc` añade esa variable sólo en Windows y un test de proceso real que
comprueba su transmisión y la exclusión de un secreto sintético no permitido.
Build tests con referencias de producción: **0W/0E, 47.02 s**. Foco
`SystemProcessRuntimeEnvironmentTests`: **1/1 PASS, 0.244 s** mediante runner
serial del ensamblado. Engine/Execution/CLI se actualizaron en la salida CLI;
Execution SHA-256 `0084A8B1466EB267ED9D8AC04CD25D49FC4499834EFA73109B27B6E2ED7B75BF`.
La E2E posterior alcanza diez HTTP y ToolCallSucceeded del verificador
(seq175 Started, seq176 Succeeded, mismo ToolCallId/Execution; 73.966 s),
pero aborta por exigir JSON `exitCode` en el resultado al modelo. El contrato
real persiste `{"summary":"Proceso terminó con código 0"}` y la continuación
recuperada envía ese JSON como string. Se corrige sólo la expectativa de representación,
comprobando el provider call exacto `verify-integration`; se conservan las
aserciones de recovery/gate. El cleanup del intento fallido canceló el Run,
por lo que este journal no acredita cierre. Store `%TEMP%/e02-e2e-store-AuV0To`.
El intento posterior comprueba correctamente ese JSON para el provider call
exacto. También acredita recovery `completed`, misma Session/Run en `orq`,
implementación única en el worktree hijo y HEAD/status raíz intactos. Falla
después en la comprobación de evidencia de razonamiento UltraCode (76.87 s);
no es una E2E aprobada ni CI. Store `%TEMP%/e02-e2e-store-e0qy8B`. Se audita
esa discrepancia antes de modificar la comprobación. La causa demostrada es
de lectura: el journal guarda Source=4 (UltraCode) y Reductions=[0] (Capability),
mientras el helper esperaba nombres. Se normalizan sólo los enums conocidos.
La siguiente ejecución de dos escenarios también detecta dos errores del test:
los tres inputs `TrustedDelegation` se contaban como humanos adicionales y la
consulta del PID intentaba abrir Diagnóstico bajo el modal. Test `49a0968`
clasifica el Origin/scope durable de cada input y lee `processId` del estado
existente, sin cambiar producción ni forzar clicks. Denegación y reapertura
del consentimiento pendiente siguen sin aceptación hasta ese recorrido.

Estado del checkpoint publicado anterior: prompt y recuperación previa al inicio construidos, cableados y validados
localmente. Publicado en Core `768c92c` y Coder `9b0e24c`, SHAs remotos
comprobados. [CI Coder 39113234258](https://github.com/juancgomezm-s/OmniCode/actions/runs/39113234258)
aprobada completa, incluido package-gate. Core también terminó completa aprobada:
tests Windows/Ubuntu, npm de ambos sistemas y paquete Linux. Las otras familias
siguen abiertas. Esta matriz no cubre las fuentes posteriores de workflows.
M9 sigue abierto. M8 espera las pruebas manuales del usuario.

## Alcance y autoridad

[ADR-0059](../adr/0059-native-command-handoff.md) conserva `command.invoke`
legacy y añade `command.invoke.user`, catálogo, recovery y continuación explícita.
La admisión cifra argumentos/expansión en un artefacto indexado; persiste inicio
antes del efecto y cierre tras el pipeline real. HMAC fija scope y payload;
Session/Run, revisiones y fingerprints se revalidan en Core. Query, reapertura
y duplicación de receipt no vuelven a ejecutar. No hay garantía exactly-once general.

Core fuente `163f122`, correcciones `e3f3dfe`/`dc049ed`, y proof inicial
`e34b763` y composición del Run `37bdf4f`; Coder fuente `f0c4965`, correcciones `f6e4658` y `a8b9bd8`.
El consumidor Main/preload/SDK/adapter y composer existente permiten seleccionar
un comando y consultar su recuperación; Accepted no se presenta como resultado
terminado. El readback del nuevo Run usa la invocación exacta en recovery.

## Evidencia local obtenida

- Core `c2af68b`: **1/1 PASS**, stdio con runtime, SQLite, artefactos y HTTP
  loopback reales. Tras un chat preparatorio, el writer entrega el ACK completo
  y falla antes del scheduler. Se cierran runtime/store/Host y se crean de nuevo
  sobre la misma SQLite. La lectura muestra ready sin otro HTTP; la continuación
  humana produce un Turn y un HTTP adicionales. Repetir el mismo receipt no
  produce otro efecto. Se comprueba referencia indexada y argumentos ausentes
  del evento de admisión/query. No es muerte de un proceso OS ni primer Run.
  Build de tests: 0 warnings/0 errores. Logs
  `%TEMP%/m9-command-handoff-tests-build-dc049ed-ackcmdid.log` y
  `%TEMP%/m9-command-handoff-focus-dc049ed-ackcmdid.log`.
- Coder tests `3dc7364`: **25/25 PASS** en NativeApi y adapter; cuatro casos
  específicos nuevos de comandos (dos en cada archivo). No son 25 casos nuevos.
  Log `%TEMP%/m9-command-coder-focal-20261018.log`.
- Coder tests `7d8e860`: **3/3 PASS** del transporte stdio con subprocess fixture
  para capability invoke/continue y rechazo sin envío cuando falta continue;
  **2/2 PASS** SDK específicos. El transporte usa la clase de producción;
  no acredita inferencia ni el Host .NET por sí solo.
- Build app sobre `a8b9bd8` aprobado; log
  `%TEMP%/m9-command-invoke-capability-appbuild-a8b9bd-20261018.log`.
- Scanner de arquitectura aprobado: 37 archivos, 155 Main/155 preload/153 Bridge,
  con cuatro IPC nuevos y catálogo alineado. El cierre de procesos del E2E posterior aprobó.

- Core tests `320d6cf`, fuente `37bdf4f`: **2/2 PASS, cero omitidas** en
  ejecución serial: primer Explain sin Run y recovery ACK-cut. El primer caso
  rechaza policy revision incorrecta, fingerprint cambiado, Session ajena y
  continue sin Run, con cero admisiones/input/HTTP; luego ejecuta Explain en
  la Session original con grafo canónico, un input y una petición reales.
  Build tests + referencias Host/CLI: **0 warnings/0 errores**, 45.45 s.
  Comandos:
  `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore` y
  `dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --parallel none --filter-method OmniCore.Tests.StdioTransportTests.First_native_explain_admits_a_blank_session_and_stale_scopes_never_reach_the_provider --filter-method OmniCore.Tests.StdioTransportTests.Native_command_ack_cut_recovers_ready_invocation_only_after_explicit_continue`.
  Log `%TEMP%/m9-command-first-explain-core-focus-37bdf4-serial-20261018.log`.
- Electron test `e515b88`, CLI Core `37bdf4f`, bundle Coder `a8b9bd8`:
  **1/1 PASS**, 20 filtradas (no cuentan como PASS), 5.69 s.
  Desde los controles UI reales sobre Session vacía: una petición HTTP local,
  respuesta visible, misma Session/Run, un input legítimo y recovery completed.
  `OMNICORE_E2E_ENTRY` fija la CLI real; comando
  `npm run test:e2e --workspace @omnicoder/app -- test/e2e/omnicore-host.e2e.test.ts -t "ejecuta core:explain desde una Session vacía por el pipeline nativo"`.
  Log `%TEMP%/m9-native-command-first-explain-e2e-a8-37bdf4-20261018.log`.
  `scripts/Assert-NoOrphanWorkers.ps1` aprobó NO_ORPHANS; comprobación adicional
  acotada de Host/testhost/Electron: cero procesos de este recorrido.

## Fallos detectados durante integración

El primer E2E de Explain desde Session vacía detectó primero la capability
incorrecta `command.command.invoke.user.user` del transporte. `a8b9bd8` corrige
el mapeo exacto sin cambiar la firma. El intento siguiente llegó al Host y
recibió `trusted_action_invalid`: el validador exigía un Run aun para Explain
inicial. `e34b763` permite Run null sólo para invoke core:explain, con modo null;
continue sigue exigiendo Run. El foco posterior admitió la acción y recibió
HTTP, pero descubrió que el runtime legacy creaba otra Session/Run después de
validar el scope. El journal original quedaba Started sin Turn ni cierre,
aunque el receipt indicaba ejecución terminada. `37bdf4f` impide crear
explore.start/act cuando ya existe expectedSession/expectedRun. La ejecución serial y el E2E anteriores validan
la corrección sin relajar las assertions de identidad/cierre.
El timeout inicial y los intentos de diagnóstico no cuentan como pruebas verdes.
Log del rechazo HMAC: `%TEMP%/m9-command-first-explain-e2e-a8b9bd-20261018.log`.

El fallo posterior de scope está en
`%TEMP%/m9-command-first-explain-core-focus-e34b763-serial-20261018.log`.

La reparación Git incluida en este consumidor procede de un fallo remoto T11.6.
`7813b9e` expuso una regresión F17, corregida en `2cbb325`. Los focos locales
F17 6/6, propagación 7/7 y T11.6 1/1 aprobaron sin relajar assertions.
Log `%TEMP%/m9-command-git-refreeze-2cbb325-20261018.log`.

## Aceptación pendiente y límites

Primer Explain y rechazo de scope/CAS/fingerprint ya están validados como se
detalla arriba. Falta una extensión real con efecto y Ask por el handoff nativo.
La auditoría de fuente confirma consumidor forcedExtension en ToolRuntime,
pero las fixtures de catálogo falso no acreditan ese recorrido integrado.

El slice publicado anterior no incluía autorización Orchestrate ni continuación
de workflows. El consumidor posterior está en rama y su aceptación se detalla
abajo; todavía no se acredita su recorrido completo. Permission/cuestionario
deben conservar el Turn original. MemoryPromotion, Lanes hijas, controles de
plan/lanes/worktrees, SEC-CONV completo y procesos persistentes/ConPTY siguen
abiertos; esta acta no los cierra. Tampoco acredita proveedor live, aceptación
manual, otros sistemas operativos ni preferencia efectiva/cambio de idioma.

## Hallazgo posterior que detiene la integración de extensiones

El usuario pidió continuar M9 después de esta detención. La construcción se
reanudó con el mismo worker Luna alto y el mismo worker Luna bajo; la evidencia
fallida siguiente se conserva como diagnóstico, sin convertirla en aceptación.

La aceptación con proceso Strong real no pasó y no se publica como construida.
Los intentos revelaron un setup Act que completaba su Run (fixture corregida a
turno Plan real y selección Act por HMAC), falta de exposición de la tool exacta
en Discovered (`3a6ff35`) y creación prematura del boundary sin catálogo Extension/
MCP (`23e2fc3`). La captura final sobre `3a6ff35` confirmó Session y modelo FullAgent,
choice exacto, definición ausente y rechazo ModelCapabilityBoundary en 1 caso
focal de 2.3 s; no se contaron esos fallos como aceptación. Logs
`%TEMP%/m9-forced-extension-3a6ff35-focus-20261018.log` y
`%TEMP%/m9-forced-extension-allow-policy-diagnostic-3a6ff35-20261018.log`.

La revisión estática posterior confirma otra frontera: `GeneralProcess` con
Writes no tiene contabilidad de mutaciones en el ledger y ambas barreras lo
rechazan. El permiso de proceso y FileMutationMode son condiciones distintas;
FullAgent no permite omitir el techo ni medir por inferencia. Reclasificar una
extensión como validador o quitar sus claims sería un bypass. Se detiene el
bloque por instrucción del usuario ante impedimentos, preservando cambios en
rama y sin publicar esta aceptación. Hace falta ejecución con cambios atribuibles
(acotación/aislamiento/diff y ledger durable) antes de habilitar escritura genérica.
La finalización nativa también debe distinguir un rechazo técnico de herramienta
del texto final del modelo: EndTurn por sí solo no acredita el efecto forzado.

## Reanudación y correcciones verificadas (2026-10-18)

Fuentes Core `fd86c92` / `f4aee7f` / `5d3eb88` vinculan el cumplimiento de una
extensión forzada al ToolCallRequested exacto y su ToolCallSucceeded en el Turn
raíz original. No sirven llamadas de otra Lane/Turn; prompt y workflow conservan
sus criterios propios. La revisión corrigió antes de aceptación un gate que
habría tratado un workflow exitoso como extensión sin recibo.

Core `06102b4` separa autoridad/CAS de readiness para registrar deuda cuando
aparece una mutación tardía: admitir/continuar trabajo sigue exigiendo contexto
raíz settled, mientras registrar el rechazo no necesita crear efectos nuevos.
La regresión CLI de mutación posterior a Verify pasó con su expectativa de deuda
intacta. Un wrapper temporal seleccionó sólo ese caso y luego se retiró para no
duplicarlo en CI; su PASS está en la tanda mixta
`%TEMP%/m9-workflow-focus-06102b4-20261018.log` (otro caso falló en preparación).

El Fact nuevo
`Native_forced_extension_without_required_tool_effect_is_terminal_failed`
pasó **1/1, cero omisiones**, 2.457 s sobre `06102b4`: modelo termina sin llamar
la tool exacta, invocación durable `failed`, receipt Accepted/failed, ninguna
solicitud de esa herramienta, marker intacto y dos HTTP (setup + comando).
Es una prueba de rechazo/ausencia de efecto; no acredita escritura de procesos.
Log `%TEMP%/m9-forced-terminal-06102b4-final-20261018.log`.
Build de tests **0 warnings / 0 errores**, 6.27 s en la última compilación del
fixture: `%TEMP%/m9-workflow-core-tests-build-06102b4-terminal-20261018.log`.

Coder `ab7df15` conecta recuperación prestart de compiledWorkflow en el panel
especializado mediante `command.continue`; después de Started sigue usando
`workflow.continue`. Foco adapter nuevo **1/1 PASS**, 17 filtrados: consultar no
despacha, sólo la intención humana manda el payload CAS actual. No equivale a
un corte ACK real desde Electron. Build app sobre `ab7df15` PASS; HTML normalizado.
Scanner **37 archivos, 157 Main /157 preload /155 Bridge, PASS**.
La aceptación Electron del workflow normal sigue en ejecución; estos resultados
no cierran esa aceptación, los procesos mutadores ni M9.

La prueba posterior de modo descubrió una discrepancia wire: `humanActionScope`
y la autoridad serializaban `orchestrate`, mientras el consumidor tipado usa
`orq`. Core `1c4873d` normaliza scope, autoridad/allowedModes y eventos, conservando
la preferencia legacy. El foco stdio posterior encontró además que el verificador
HMAC de `run.mode.select` sólo aceptaba Plan/Act y cuatro campos, rechazando el
payload Orchestrate con sus límites explícitos antes de dispatch. Se corrige en
una rama específica, sin extender los permisos de los comandos de chat.

La auditoría detectó otra brecha de composición: las etapas compiladas no
registran tool.search, pero heredaban Discovered y ocultaban write/patch/verifier.
El antecedente CLI con política Custom/Direct no cubría el perfil FullAgent común.
La corrección especializa el disclosure del catálogo de etapa a Direct,
conservando su intersección de capacidades, límites y autorización de cada tool.
Ambas correcciones requieren nuevo foco y E2E; no se declaran aceptadas aquí.

`65c3d65` deja la forma ORQ estricta de trece campos; build Core **0W/0E** y
Fact `Native_orchestrate_selection_reads_back_the_canonical_orq_wire_mode`
**1/1 PASS**. La siguiente E2E ya aplicó Orchestrate, pero el catálogo permaneció
deshabilitado. Se identificó una causa distinta: `ModelPolicyHost.Set` reconstruía
el preset al añadir `--note` sin copiar sus controles opcionales, desactivando
AllowOrchestration. `2d3cfc8` preserva esos campos; el fixture conserva FullAgent
configurado por CLI, perfil raíz y proveedor HTTP local reales. No se elimina la
nota para esconder el defecto ni se relaja el requisito del catálogo.
La teoría `Setting_a_note_preserves_all_controls_of_the_selected_preset` pasó
**2/2, cero omisiones**, FullAgent/ObserveOnly con controles comparados
estructuralmente; log `%TEMP%/m9-model-policy-note-2d3cfc8-retry.log`.
Build CLI `2d3cfc8` 0W/0E (49.68 s) y tests 0W/0E (7.49 s).

### Recuperación de workflow anterior a Started

`Native_workflow_ack_cut_stays_passive_until_explicit_continue_and_duplicate_is_idempotent`
pasó **1/1, cero omisiones**, 2.943 s sobre `2d3cfc8`. Un turno Plan ordinario
crea el contexto raíz y un grant ORQ firmado habilita la admisión. Se entrega el
ACK y se corta antes de Started, se cierran runtime/Host/store y se reabre la
misma SQLite. Consultar no ejecuta; `command.continue` explícito crea un solo
Started/Finished y repetir el recibo conserva secuencias, HTTP y gates.
No es kill OS ni demuestra que el workflow o sus hijos terminen correctamente.
Logs `%TEMP%/m9-workflow-ackcut-2d3cfc8-build-final.log` (0W/0E) y
`%TEMP%/m9-workflow-ackcut-2d3cfc8-test-final.log`.

### E2E completa aún fallida

Coder `482db45` conserva los fences de identidad para consultar recuperación
cuando el adapter informa failed; el envío ordinario conserva su guard más
estricto. Las continuaciones siguen exigiendo readiness/CAS canónicos en Core.
Build app aprobado: `%TEMP%/m9-workflow-recovery-app-482db459.log`.

La E2E `ejecuta el workflow nativo desde la UI con contexto raíz, herramientas y verificador reales`
falló **1 caso, 21 filtrados**, 28.79 s con Core `2d3cfc8` / Coder `482db45`.
El uso explícito del proveedor local deja CostIncomplete=false. Hay admisión,
PlanGateAdded, Task/Lane/worktree de hijo y delegación aceptada reales; después
de model_step.started (51) aparece turn.abandoned (52), sin HTTP hijo. Sólo se
observa el HTTP del setup. Las consultas de recuperación ya funcionan y muestran
el bloqueo; esto no convierte el workflow en exitoso. Log
`%TEMP%/m9-workflow-ui-e2e-482db459-usage.log`; diagnóstico acotado
`%TEMP%/m9-workflow-root-context-1792337338720.json`.

Una única corrida diagnóstica posterior preservó el journal y confirmó el motivo
`Selected reasoning has no representation in this adapter.` antes de HTTP.
Log `%TEMP%/m9-workflow-ui-e2e-reason-sidecar-482db459.log` (1 fallo, 21 filtrados,
28.22 s); no se cuenta como aceptación. Se revisa la composición de reasoning
y su representación de protocolo, sin apagarla para forzar un PASS.

Core `ee47d00` / `76f6a5b` corrige la resolución automática de UltraCode según
el protocolo. Chat-compatible conserva Requested=high, Applied=null y reducción
Capability con advertencia; Responses conserva high declarado y Anthropic su
presupuesto/reserva previos. Las selecciones explícitas incompatibles se rechazan
antes de HTTP. El foco CLI real
`Automatic_ultracode_reasoning_follows_the_configured_protocol` (cuatro filas)
y `Explicit_chat_reasoning_preferences_remain_fail_closed` (dos filas) pasó
**6/6, cero omisiones**, 4.508 s. Comprueba resolución durable, warning/rechazo,
HTTP sin campos inventados, esfuerzo Responses y presupuesto Anthropic.
Build de tests (incluye CLI) **0W/0E**, 6.37 s. Logs
`%TEMP%/m9-ultracode-protocol-76f6a5b-build-final.log` y
`%TEMP%/m9-ultracode-protocol-76f6a5b-tests-final.log`.
Los intentos previos fallaron por errores de compilación/setup del fixture
(alias Task, out variable, query/revisión y declaración budget); se corrigió
el fixture sin cambiar guardas de producto. La E2E completa aún debe terminar;
el foco no acredita cierre del workflow.

La E2E posterior a `76f6a5b` superó el fallo de razonamiento: cinco HTTP, Explore
terminado y entrada real en Implement. Falló al faltar filesystem.write en el
catálogo ofrecido, mientras filesystem.patch sí estaba presente. Log
`%TEMP%/m9-workflow-ultracode-76f6a5b-e2e.log` (1 fallo, 21 filtrados, 26.89 s).
La hipótesis de que Implement excluía ReplaceFile era incorrecta: el catálogo
compilado real sí lo declara. El ajuste `a0c5dfe` no activa su rama en ese caso
y no se acepta como corrección ni se ejecuta una E2E para aparentar progreso.
Se revisa la intersección efectiva de políticas y el techo Session con el journal
preservado; la ausencia de write continúa sin resolver.

El journal preservado dio el motivo preciso antes de Prepared:
`Tool request receipt does not match its canonical event.` El request contiene
código C# con `=>`; la reserialización del JSON anidado cambia `>` por `\u003E`.
Session y modelo efectivos son FullAgent/Full y el perfil permite escribir;
no se justifica ampliar ningún permiso. `3fa1691` retira el ajuste inoperante y
`96545f2` compara identidad exacta y estructura JSON ordenada del receipt en
append/readback. Sólo tolera escapes/espaciado de serialización; conserva orden,
forma, nombres, strings y representación numérica. MatchesCall sigue ligado
al digest del argumento original y una redacción que altere un valor rechaza.
El foco inicial sobre `96545f2` pasó 1/1 (0.585 s), build 0W/0E (49.65 s).
La auditoría posterior añadió otro caso real: un content que contiene texto JSON
con formato tampoco debe compactarse al guardarse. `f9d4438` conserva la cadena
original cuando la redacción no cambia el árbol ordenado; si cambia valores,
mantiene la versión redactada y el receipt sigue rechazándola.

Foco final sobre `f9d4438`: **2/2 PASS, cero omisiones**, 0.567 s; build tests/CLI
**0W/0E**, 5.99 s. Los Facts
`MemoryProposalReceiptBoundaryTests.Request_receipt_accepts_equivalent_json_escaping_but_rejects_changed_values`
y `Nested_json_file_content_keeps_its_exact_format_when_redaction_changes_nothing`
usan el EventStream real; el primero fuerza con un codec de test diferencias de
escape y alteración real, además de rechazar IDs/scope/argumentos distintos y
PII modificada. El segundo acredita el string exacto tras roundtrip, no una
escritura de archivo por sí solo. La E2E completa posterior sigue en ejecución.

La E2E sobre `f9d4438` ya no falla por receipt: filesystem.write llega a
ToolCallRejected UNKNOWN_TOOL (`tool no disponible para este modelo`) y el gate
termina blocked. La herramienta realmente estaba fuera de la oferta. El motivo
es el orden antes del cupo visible: read/search/mailbox, patch y artifact tienen
prioridad anterior a write/verifier; el harness acotado los trunca.
Diagnóstico `%TEMP%/m9-workflow-root-context-1792339129185.json`, 1 E2E fallida,
27.9 s. No se amplió el contexto del fixture ni los permisos para ocultarlo.

Core `c30d667` especializa la prioridad sólo de children de workflows compilados:
respeta elegibilidad y cap, reserva la lectura previa de Implement y prioriza
los IDs de éxito declarados. OR sigue requiriendo al menos uno y Every todos;
un mínimo no permitido o que no cabe rechaza con WorkflowStageToolDisclosureException
antes del proveedor. Las prioridades ordinarias de otros turnos se conservan.
Build final **0W/0E**, 6.08 s; Fact
`ModelPolicyProfileControlsTests.Compiled_workflow_stage_tools_stay_visible_within_the_existing_cap`
**1/1 PASS**, 0.124 s. Comprueba oferta read/write/patch/verifier dentro de cap 6
y PatchOnly con read/patch sin write; no acredita por sí solo el rechazo Every.
Una primera aserción incorrecta que exigía llenar el cupo se corrigió: el cupo
es un máximo, no un mínimo.

La E2E posterior acredita ocho HTTP y `missingOfferedTools=[]`: Explore e
Implement terminan con módulo y entrada conectados en el worktree hijo. Root
comprobó esos bytes y el checkout principal limpio. Verify sólo llega a
TaskReady/PlanItemReady (hasta seq148), sin DelegationAccepted ni HTTP; el cierre
nativo no completa dentro de la espera de esa corrida. Diagnóstico
`%TEMP%/m9-workflow-root-context-1792339558491.json`. No se acepta el workflow.
Una corrida diagnóstica test-only no observó stderr del Host; no produjo código
de causa y no se cuenta como validación. Se prepara una observación pasiva del
child Host con confirmación previa a invocar, sin tocar guards ni presupuesto.

La captura pasiva posterior sí observó el Host. Sobre el commit diagnóstico
`eeb0216`, build CLI 0W/0E (76.83 s), la E2E volvió a fallar (28.31 s, ocho
HTTP locales). La traza termina en Verify/BeforeExecuteDelegation, después de
validar ruta, registrar el verificador y componer el perfil; no hay ACK ni salida.
El journal de esta corrida termina en seq148, **sin seq149 ni
CommandInvocationFinished**. Es una espera pendiente dentro de ExecuteDelegation,
no evidencia de un rechazo retornado. Se conserva el fixture y el sidecar privado;
no se publican su contenido ni esta instrumentación temporal. El workflow sigue
sin aceptación. `c5419ed` añade checkpoints temporales antes/después de identidad,
preview, capacidad y admisión para localizar la espera, sin cambiar las guardas.

El diagnóstico posterior corrige la hipótesis de espera: `c5419ed` compiló
0W/0E en 43.27 s y la única E2E, con espera diagnóstica de 60 s, falló en
71.31 s. El sidecar alcanzó 8192 bytes y quedó truncado: su último checkpoint
no identifica por sí solo la causa. El journal conservado sí muestra Verify
admitido (seq156), binding aceptado (159), Turn iniciado (162), solicitud real
`dyn.core.verify_integration` (165) e interacción WeakSandboxConsent (170).
El runtime resolvió `deny` con causa **NoClient**, no una decisión del usuario,
y la herramienta falló PERMISSION_DENIED (172). La etapa retornó y quedó
bloqueada; command.invocation_finished aparece en seq187. No existe un bloqueo
demostrado del supervisor. `93e7db6` y `5511e3c` retiran toda la instrumentación.

Hallazgo productivo pendiente: el Host stdio no permite responder a ese
consentimiento del child y OmniCoder descarta ese scope. Se construye una
suspensión explícita correlacionada a llamada/intención, Session/Run,
Task/Lane/Turn y workflow, con consentimiento de una sola ejecución y respuesta
HMAC/CAS. No se ofrecerá consentimiento Run sin un grant durable. El launcher
incierto no se reintenta; replay/reconexión permanecen pasivos. Esta descripción
es alcance en construcción, no evidencia de implementación ni cierre.

Coder bundle `482db459`: un nuevo foco de fallo operativo aprobó 1/1 (18 fuera
del filtro), 150 ms. Acredita lectura fresca de catálogo/recuperación y continuación
del invocation canónico ready del mismo scope, con re-invoke ordinario e ID
distinto bloqueados. No acredita el consentimiento WeakSandbox ni la E2E.
