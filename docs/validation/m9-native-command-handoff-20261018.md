# M9-L: handoff nativo durable de comandos

Estado: prompt y recuperación previa al inicio construidos, cableados y validados
localmente. Publicado en Core `768c92c` y Coder `9b0e24c`, SHAs remotos
comprobados. [CI Coder 39113234258](https://github.com/juancgomezm-s/OmniCode/actions/runs/39113234258)
aprobada completa, incluido package-gate. Core aprobó Ubuntu, npm de ambos
sistemas y paquete Linux; su suite Windows sigue en curso. Las otras familias
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

El executor compilado de workflows existe, pero OmniCoder aún no ofrece
autorización Orchestrate ni continuación de workflows que esperan tras iniciar.
No se declara ese acceso construido. Las continuaciones de Permission/cuestionario
deben conservar el Turn original. MemoryPromotion, Lanes hijas, controles de
plan/lanes/worktrees, SEC-CONV completo y procesos persistentes/ConPTY siguen
abiertos; esta acta no los cierra. Tampoco acredita proveedor live, aceptación
manual, otros sistemas operativos ni preferencia efectiva/cambio de idioma.

## Hallazgo posterior que detiene la integración de extensiones

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
