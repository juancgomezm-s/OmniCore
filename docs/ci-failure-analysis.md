# Reparación y validación de CI/Linux de OmniCore

**Checkpoint vigente de publicación (2026-10-18):** Core `38b81f4` aprobó
[la matriz completa 39108921103](https://github.com/juancgomezm-s/OmniCore/actions/runs/39108921103):
tests Windows/Ubuntu, instalación npm Windows/Ubuntu y paquetes Linux. Incluye
skills/memoria Session y la reparación EOL `eb03d4a` del test de modelos.
Los fallos y checkpoints inferiores se conservan como historial; esta matriz
no acredita las fuentes posteriores de comandos nativos todavía en validación.

OmniCoder `c2c64a3` aprobó Windows/Ubuntu, pero su
[package-gate 39109171682](https://github.com/juancgomezm-s/OmniCode/actions/runs/39109171682)
falló durante la prueba T11.6 de cancelación Git, tras PACKAGE_OK, GATE_OK y
NO_ORPHANS. Se diagnosticó el orden de terminación del wrapper/árbol Windows;
el primer intento `7813b9e` expuso una regresión F17 (cancelación antes del
evento spawn), corregida en `2cbb325`. Sin relajar assertions, F17 6/6,
propagación 7/7 y T11.6 1/1 aprobaron localmente. Log:
`%TEMP%/m9-command-git-refreeze-2cbb325-20261018.log`. La CI del siguiente
consumidor sigue pendiente; no se presenta ese run fallido como aprobado.

**Estado vigente (2026-10-17):** la reparación `91b07fb` aprobó su matriz completa
Windows/Ubuntu, npm y paquetes Linux, según el checkpoint de este informe.
La ampliación posterior de elecciones nativas y recuperación tiene
[evidencia local propia](validation/m9-native-choices-recovery-20261017.md).
Su integración `6953dde` falló en dos casos comunes de Windows/Ubuntu en
[run 39090154269](https://github.com/juancgomezm-s/OmniCore/actions/runs/39090154269).
La reparación se sigue en la sección siguiente. Los 121 fallos del bloque
anterior son diagnóstico histórico, no el estado de esta integración.

Seguimiento del bloque de modelos publicado `e0474a5`:
[run 39100693817](https://github.com/juancgomezm-s/OmniCore/actions/runs/39100693817)
aprobó Ubuntu, instalación npm de ambos OS y paquetes Linux. Windows terminó
con **4871 PASS, 1 FAIL, 9 SKIP** de 4881. El único fallo fue
`StdioTransportTests.Native_model_selection_is_session_cas_scoped_and_catalog_does_not_expose_route_configuration`,
línea 355: el raw string de providers usa CRLF después del checkout Windows,
pero el bloque buscado usa LF. No es un rechazo de selección/inferencia.
Se normaliza sólo el YAML de la fixture antes de escribirlo/compararlo;
se conservan el cambio real de endpoint y las assertions de rechazo antes
de input/HTTP. El foco corregido aprobó **1/1**, sin repetir toda la suite.
Log CI: `%TEMP%/m9-ci-job-117596179195.log`; foco local:
`%TEMP%/m9-native-model-routing-windows-eol-fix.log`. La corrección `eb03d4a`
se incluye en la integración del bloque de skills/memoria; su nueva matriz
remota debe comprobarse. No se declara esa CI fallida como verde.
La arquitectura CLI ya pasa en esta matriz.

El consumidor OmniCoder `476ce06` aprobó Windows, Ubuntu y package gate en
[run 39100698529](https://github.com/juancgomezm-s/OmniCode/actions/runs/39100698529).
No acredita las fuentes posteriores de skills/memoria, que tienen
[aceptación local propia](validation/m9-native-session-preferences-20261018.md).

## Seguimiento de elecciones nativas: dos regresiones de integración

La matriz sobre `6953dde` ejecutó 4880 casos por plataforma: Windows
4869 PASS, 2 FAIL, 9 SKIP; Ubuntu 4856 PASS, 2 FAIL, 22 SKIP. La instalación
npm de ambas plataformas y los paquetes Linux pasaron. Los fallos exactos son:

- `CliEndToEndTests.Act_permission_ask_without_tty_stays_awaiting_input_without_inventing_answer`:
  salida esperada 3 (`InputRequired`), real 1. El opt-in diferido nuevo se había
  cableado sólo al stdio nativo; faltaba en los entrypoints confiables CLI/TUI.
- `StdioTransportTests.Versioned_golden_frames_are_valid_and_match_the_capability_contract`:
  el test confunde el ID lógico `interaction.respond.choice` con el wire name
  `interaction.respond`. La nueva variante exige comprobar el binding completo.

El consumidor OmniCoder `f737d1d` aprobó
[su CI completa](https://github.com/juancgomezm-s/OmniCode/actions/runs/39090163680).
Esto no convierte la CI fallida de Core en aprobada ni cierra M9.
Logs completos descargados en `%TEMP%/m9-ci-39090154269/`.

`f364a48` añade un parámetro explícito `allowDeferredPermission` (default false)
a `ActAsync`. Sólo los entrypoints CLI/TUI optan por dejar el permiso sin
respuesta; stdio conserva la negociación y el runtime genérico sigue Deny.
No se emiten resolución, grant ni efecto al devolver `InputRequired`. La prueba
CLI comprueba también ausencia de `PermissionGranted`; el test del catálogo
valida wire name, variante y capability exactos, además de los IDs lógicos.

Build del proyecto de tests y sus referencias Host/CLI: cero advertencias y
errores, 1m24s. Foco serial: **5 PASS, 0 FAIL, 0 SKIP** (golden, CLI sin TTY,
NoClient sin opt-in y Permission nativa allow_once/deny). No se repitió la suite
completa local. Logs `%TEMP%/m9-ci39090154269-f364a48-testbuild-20261017.log`
y `m9-ci39090154269-f364a48-focused-20261017.log`. La nueva matriz remota debe
validar esta reparación; no se presenta aún como aprobada.

Comandos efectivamente ejecutados desde la raíz Core:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --parallel none --filter-method '*Versioned_golden_frames_are_valid_and_match_the_capability_contract*' '*Act_permission_ask_without_tty_stays_awaiting_input_without_inventing_answer*' '*NoClient_ask_denies_even_when_resolver_exists_without_native_defer_opt_in*' '*Native_permission_choice_resumes_exact_turn_with_one_effect_and_receipt*'
```

La nueva matriz de reparación es
[39098271400](https://github.com/juancgomezm-s/OmniCore/actions/runs/39098271400),
SHA `7dfa102`. Ubuntu terminó con **4857 PASS, 1 FAIL, 22 SKIP** de 4880:
los dos casos anteriores pasan, pero falla
`CliAssemblyReferenceTests.Omni_assembly_references_only_allowed_projects`.
La llamada CLI al overload con parámetros opcionales de tipo Domain introdujo
una referencia IL a `OmniCore.Domain`. La fachada debe ser primitiva; no se
amplía la allowlist de arquitectura. Windows seguía en curso al comprobarlo;
npm de ambas plataformas y paquetes Linux pasaron.

`3e69d11` añade `ActWithDeferredPermissionAsync` en Host y retira esa llamada
con firma Domain desde CLI. Está en la rama del siguiente bloque nativo,
todavía sin publicar. El foco arquitectónico sobre `7029910` aprobó **1/1**,
sin omisiones; no se repitió el foco verde al cambiar sólo el snapshot inicial
de rutas. Ese commit también
repara dos errores de compilación encontrados en el primer build del bloque
de modelos `126e953`: variable local `session` duplicada y routing nullable.
No se presenta ese build fallido como validación del bloque.

El push de documentación OmniCoder `b24d166` también detectó un fallo en
[run 39098276410](https://github.com/juancgomezm-s/OmniCode/actions/runs/39098276410):
T11.6 de cancelación Git agotó 20 segundos en Windows. Ubuntu y package gate
pasaron; la CI global falló. El caso contiene ventanas internas de 15 y 5
segundos, además de consultas de procesos y cancelación. `4653e34` acota la
espera de cancelación, añade diagnóstico por etapa y amplía el timeout exterior
a 40 segundos sin eliminar árbol muerto, commit ausente ni estado cancelado.
El foco aprobó **1/1** (65 filtrados); la cancelación observada fue de 2011 ms.
La [CI de esa reparación](https://github.com/juancgomezm-s/OmniCode/actions/runs/39099365577)
terminó **aprobada**. El código de producto de ese push no cambió.

Logs de jobs descargados en `%TEMP%/m9-ci-job-117589108261.log` (Core Ubuntu)
y `%TEMP%/m9-ci-job-117589122216.log` (Coder Windows).

## Regresiones detectadas tras seguridad de Session (2026-10-17)

El run [39077479124](https://github.com/juancgomezm-s/OmniCore/actions/runs/39077479124),
sobre `5c315ba`, informa **121 fallos de 4860 casos** en el job Ubuntu
`117527528666`. Windows también falló en **120 casos**, job `117527528701`:
son exactamente los mismos nombres de prueba, salvo la ruta base literal que
sólo falla en Linux. Instalación npm en Windows/Ubuntu y empaquetado Linux pasaron;
eso no acredita la suite de Core. OmniCoder pasó su matriz Windows/Ubuntu y
su package gate en el run independiente
[39077489183](https://github.com/juancgomezm-s/OmniCode/actions/runs/39077489183).

La inspección inicial agrupa los fallos, sin presentarlos todavía como reparados:

- Reanudación: varias fixtures escriben un Run sin `SessionCreated`. La migración
  conservadora de seguridad rechaza ese journal al recuperar escritura. El estado
  se informa como `busy/session_writer_owned`, aunque esa causa no demuestra una
  lease ocupada; deben distinguirse ambos motivos sin permitir un journal inválido.
- Delegación: fixtures que antes sólo seleccionaban ORQ y una política de modelo
  permisiva no seleccionan ahora un techo de Session que permita orquestación.
  Deben declarar su autorización explícita; no se debe ampliar el default ni
  omitir la intersección de políticas para hacer pasar las pruebas.
- Atomicidad: las expectativas numéricas de inicialización todavía cuentan sólo
  `SessionCreated` y omiten su nuevo snapshot durable de seguridad. Se debe
  comprobar el par canónico, causalidad, rango del ACK y rollback completo.
- Portabilidad: `SessionSecurityPolicyBoundaryTests` usa una ruta base Windows
  literal que no es absoluta en Linux. La fixture necesita una ruta de la plataforma.

Log completo descargado en el equipo de trabajo:
`C:/Users/juanc/.codex/omnicode-ci-repair-20261013/117527528666-job.log`
y `117527528701-job.log` en el mismo directorio.
### Reparación y validación local

`4b1091a` conserva el rechazo de journals sin Session canónica y distingue
`session_security.recovery_blocked` (Rejected) de `session_writer_owned` (Deferred)
en Host, stdio y el diagnóstico CLI/TUI. La prueba nueva comprueba ambos motivos
y recuperación. Las fixtures declaran su Session y autorizan FullAgent sólo donde
su escenario requiere delegación o escritura; la política de modelo se guarda
antes del primer ModelStep. No se amplía el default de producción ni se restauran
grants de lectura desde el journal.

Build de solución `4b1091a`: **0 errores, 0 advertencias**, 1m37.06s,
`%TEMP%\m9-core-4b1091ac-build.log`. El primer foco local de 28 clases dio
228 casos: 214 PASS, 11 FAIL y 3 omitidos opt-in/live. **Esa invocación omitió
`--parallel none`, presente en CI**; sus fixtures de entorno global podían competir.
Se conserva el resultado como diagnóstico y no como equivalente a la matriz CI.
Log: `%TEMP%\m9-core-4b1091ac-focused-tests.log`.

`d051c128` aísla la configuración de plataforma de SessionInput y añade snapshots
de seguridad desde la creación a fixtures nativas que no prueban migración.
Repara también el setup de LiveMailbox: Session/modelo explícitos antes del Run.
El foco de los 11 casos pendientes con `--parallel none` dio **9 PASS, 2 FAIL,
0 omitidos**, 24.414s, en `%TEMP%\m9-core-d051c128-11-focus.log`.
SessionInput pasa con aislamiento temporal y ejecución serial; no se atribuye
su resultado anterior a una sola causa, pues cambiaron ambas condiciones.

Los dos ACT restantes aplicaban el parche pero omitían gates de Session vigentes.
El diagnóstico acotado (`8cf93f9`) comprobó `run.validation_rejected`, gates
`post-edit-validation` y `plan`; no era un fallo de ejecución del tool.
`4b35a0e` verifica ahora ese contrato: archivo modificado, barrera antes del efecto,
evidencia real de fiabilidad, rechazo exacto de gates y ausencia de `RunCompleted`.
No se desactiva la validación de Session para forzar un código de salida cero.
La prueba del primer escenario se renombra para expresar el resultado bloqueado.
El foco siguiente detectó 16 rechazos del mismo Run: el bucle ACT acotado repetía
la propuesta de cierre aunque no había ningún validador admisible. No se cambió
la aserción para aceptar esa repetición.

El ajuste inicial `cf24851` conserva el rechazo durable y evita reenviar al
modelo el mismo bloqueo externo cuando no encuentra una fuente de validación.
Su detección incluía skills/hook como posibles sustitutos, supuesto corregido
por la auditoría adicional descrita abajo. Este commit intermedio no acredita
el cierre de esa auditoría.

Build de solución `cf24851`: **0 errores, 0 advertencias**, 49.27s, log
`%TEMP%\m9-core-cf248510-solution-build.log`. El foco serial de ACT, gates M3 y
comandos de completion dio **28 PASS, 0 FAIL, 0 omitidos**, 17.169s, log
`%TEMP%\m9-core-cf248510-act-gates-focus.log`. La prueba adicional por el loop
real encontró un error de su propio setup: respondía `allow_once` al diálogo
`WeakSandboxConsent`, que requiere `consent_once`. Se corrige la respuesta por
tipo de interacción, sin cambiar seguridad de producción ni aceptar una
denegación como validación exitosa.

El caso adaptado en `46ceee8` pasa **1/1**, cero omisiones, por `RunActLoop`
real con callback de disponibilidad `false`: el gate `git diff --check` falla,
su salida llega al modelo, éste repara el archivo y el gate permite completar.
Se comprueban un rechazo inicial, seis requests y `RunCompleted`. Es el mismo
caso reforzado de la clase M3, no un caso único adicional a los 28 anteriores.
Build de tests limpio y foco aislado en
`%TEMP%\m9-core-cf248510-loopfixture-build4.log` y
`%TEMP%\m9-core-cf248510-loopfixture-focused3.log`.

La variante adicional sin Build/Test encontró el límite contractual real:
`PostEditValidationConsumed`, su proyección y `RunCoupon` sólo aceptan `build`
o `test` para consumir deuda de edición. Un skill validator aprobado es un
gate adicional y no cubre esa deuda. Por ello se retira el callback de skills
elegibles del ajuste inicial y se exige configurar Build/Test en el diagnóstico.
No se cambia el evento, la proyección ni el criterio de cierre para hacer pasar
la prueba. El caso incompatible se sustituye por una comprobación negativa:
un skill real aprobado no permite completar sin el Build/Test obligatorio.

El ajuste de producción final es `7c55714`; los tests se congelan en `069bc74`.
El caso negativo conserva también el gate `plan`: el objetivo raíz no se cierra
mientras falle validación. No se altera `RunCoupon` ni se amplía qué evidencia
puede consumir `PostEditValidationConsumed`.

Build final de solución: **0 errores, 0 advertencias**, 49.09s, log
`%TEMP%\m9-core-7c557142-solution-build.log`. El foco serial de 29 casos dio
**28 PASS, 1 FAIL, 0 omitidos**, en
`%TEMP%\m9-core-7c557142-act-gates-focus29.log`. El único fallo fue la
aserción del diagnóstico del caso nuevo: ese fixture no instala localizer y
`LocalizedText.Render()` devuelve la clave estable, no la frase inglesa.
Las comprobaciones de gates, deuda post-edición y archivo real ya pasaban.
Se corrige únicamente la expectativa de presentación del test; no cambia
la validación de producción.

La expectativa corregida está en `ce83d56`. Build del proyecto de tests:
**0 errores, 0 advertencias**, log
`%TEMP%\m9-core-7c557142-skill-test-build.log`. Sólo se repitió el caso que
había fallado: **1 PASS, 0 FAIL, 0 omitidos**, con `--parallel none`, log
`%TEMP%\m9-core-7c557142-skill-test-focused.log`. Son 29 casos únicos del
foco comprobados entre esas dos ejecuciones; no una única ejecución limpia
de 29/29. No se repitieron los otros 28 casos verdes por un cambio de aserción.

OmniCoder `2b4c0d2` tiene CI completo aprobado en Windows/Ubuntu y package gate:
[39079686251](https://github.com/juancgomezm-s/OmniCode/actions/runs/39079686251).
Core necesita aún la nueva matriz CI serial sobre el checkpoint reparado; no se
declara CI Core verde ni M9 cerrado con estos resultados focales.

### Resultado remoto del checkpoint reparado

La [CI 39082408888](https://github.com/juancgomezm-s/OmniCore/actions/runs/39082408888),
sobre `91b07fb`, terminó **success** en los cinco jobs. Esta evidencia sustituye
el estado pendiente de los párrafos anteriores para ese checkpoint:

| Comprobación | Resultado |
| --- | --- |
| Suite completa Windows | 4862 casos: 4853 PASS, 0 FAIL, 9 SKIP; 32m52.184s |
| Suite completa Ubuntu | 4862 casos: 4840 PASS, 0 FAIL, 22 SKIP; 11m34.333s |
| Precheck Windows de recuperación/Git/aislamiento | 9 PASS, 0 FAIL, 0 SKIP |
| Instalación y actualización npm Windows/Ubuntu | Ambos jobs PASS |
| Paquetes Linux | PASS |

Logs de suites descargados como `117541912666-job.log` (Windows) y
`117541912830-job.log` (Ubuntu) en el mismo directorio de evidencia local anterior.
En Ubuntu también se verificaron las líneas PASS de los cinco casos clave:
dos ACT CLI, rework del gate configurado, skill que no sustituye Build/Test y
admisión que distingue recuperación bloqueada de lease ocupada.
Las omisiones no se cuentan como aceptación de proveedores/manual. Se cierran
las regresiones de CI de este checkpoint, no M9: la construcción posterior de
respuestas nativas Permission/PlanApproval y recuperación explícita requiere
su propio build, pruebas e integración.

## Reparación focal de integración (2026-10-17)

El run [39004749905](https://github.com/juancgomezm-s/OmniCore/actions/runs/39004749905)
falló en siete casos de Core, con el mismo resultado en Windows y Ubuntu: dos
variantes de publicación CAS del tokenizer, cuatro cuadros de animación y una
prueba de reflow del centro de preferencias. Esta reparación modifica únicamente
instrumentación y lecturas de pruebas; no cambia código de producto.
El run independiente de OmniCoder [39004732300](https://github.com/juancgomezm-s/OmniCode/actions/runs/39004732300)
fue exitoso, pero no acredita Core.

| Área | Causa confirmada | Ajuste y límite conservado |
| --- | --- | --- |
| Tokenizer/CAS (2 casos) | El pase de presión prepara una referencia en memoria y el plan final otra referencia del mismo hash. El test sumaba ambas como publicaciones. La misma referencia final puede enraizarse en `TurnStarted` y `ModelStepStarted`. | Distinguir referencias preparadas de publicaciones, y contar aparte llamadas de publicación, IDs publicados y blobs SHA únicos. Se conservan las comprobaciones de tokenizer sin lease, lease durante publicación, raíces del journal y recolección tras fallo. |
| Animación (4 casos) | El frame dibuja nombres localizados (“Pulso Omega” y “Giro braille”), no IDs internos `omega-pulse`/`braille-spinner`; la espera nunca podía observar su marcador. | Esperar el rótulo humano y verificar la selección persistida por su ID canónico. Permanecen las comprobaciones de celdas braille, color `#38BDF8`, tamaño y ajuste del overlay. No se amplió el timeout. |
| Reflow de preferencias (1 caso) | El test enumeraba `View.SubViews` y leía foco mientras el loop UI reemplazaba el overlay al redimensionar. La enumeración se hacía fuera del hilo que posee el árbol de vistas. | Añadir lectura síncrona en el hilo UI para snapshots y assertions de foco, idioma, búsqueda y borrador; volver a localizar vistas por ID tras cada reflow. No se cambia el reflow de producción ni se oculta la excepción copiando colecciones desde el hilo incorrecto. |

Validación local focal, con salida stdout real conservada en `%LOCALAPPDATA%\Temp`:

| Log | Filtro | Resultado |
| --- | --- | --- |
| `omnicore-ci-repair-tokenizer-20261017.log` | `Tokenizer_runs_unlocked_before_CAS_publication_and_snapshot_roots_commit_under_lease` | 2 PASS, 0 FAIL |
| `omnicore-ci-repair-animations-20261017.log` | `Visual_frames_export_the_real_driver_cells` + `DisplayName~animations` | 4 PASS, 0 FAIL |
| `omnicore-ci-repair-reflow-20261017.log` | `Preferences_center_reflows_live_without_losing_search_category_locale_or_focus` | 1 PASS, 0 FAIL |
| `omnicore-ci-repair-preferences-20261017.log` | `Preferences_center_navigates_by_keyboard_and_cancelled_locale_preview_preserves_chat` | 1 PASS, 0 FAIL; comprueba la lectura UI-thread compartida |
| `omnicore-ci-repair-build-tests-net10-20261017.log` | `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore` | Build net10.0 correcto, 0 warnings, 0 errores |

Las siete variantes que fallaron en CI pasan en esta reparación focal. La
validación local no acredita por sí sola Windows y Ubuntu del head corregido;
la matriz de CI debe volver a ejecutarse después de integrar el commit.
La construcción nueva de conversaciones nativas y seguridad por Session (M9-K)
se pausó al detectar el fallo de main; el contrato tiene una propuesta revisada,
pero su implementación y los criterios SEC-CONV siguen pendientes.

## Seguimiento: regresiones de Core tras M9-F/F2 — 2026-10-15

Rama de corrección: `codex/core-ci-regressions`. Se conserva la interfaz integrada
y el recorrido de instalación Windows/Linux. Las secciones siguientes a este
seguimiento documentan la reparación histórica del 14 de octubre.

La [ejecución 38767223307 sobre df3d148](https://github.com/juancgomezm-s/OmniCore/actions/runs/38767223307/job/116533681326)
falla en `All tests, including architecture and process recovery`, comando
`dotnet test --solution OmniCore.slnx --no-build --no-restore --max-parallel-test-modules 1 --parallel none --output Detailed --no-progress`.
Ubuntu informa 16 fallos y salida 2. El empaquetado independiente pasa. La
reproducción focal **antes de corregir**, en Windows y Debian, da el mismo
resultado: 54 casos, 38 PASS, 16 FAIL, 0 SKIP, salida 2.

| Causa raíz y origen | Casos y evidencia exacta | Reparación |
|---|---|---|
| Regresión de disponibilidad del costo en `22128b1` (M9-F2): separó facturas de tokens, pero permitió también estimaciones sin evidencia válida de tokens. | 11: `Unrepresentable_token_sum...` (5), `Assert.Null`: `Money { Amount = 0, Currency = USD }`; `Contradictory_reported_breakdown...` (6), `Expected: Unknown / Actual: Estimated`. | Exigir evidencia completa, coherente y representable para las estimaciones; mantener las facturas reales independientes de los contadores de tokens. Pruebas adicionales de sesiones mixtas, persistencia y reapertura. |
| Pruebas de versiones desactualizadas tras M9-F/F2 (`67c9a03`, `22128b1`). | 2: `EventCodecTests.cs:55`, `Expected: 2 / Actual: 3`; `GenerationRequestAttemptEvidenceTests.cs:59`, `Expected: 3 / Actual: 5`. | Conservar schemas actuales y verificar explícitamente input v1/v2 y completions v2/v3/v4, con defaults y evidencia de intentos intactos. No se bajan versiones ni se retiran assertions de compatibilidad. |
| `22128b1` cambió el JSON de `model.profile` sin cambiar su etiqueta v2. | 2: hashes exactos de `QualificationFingerprintTests.cs:44,56` difieren. | Restituir exactamente el perfil v2; registrar modalidades de salida y cota de imagen en `model.output` v1. Los hashes históricos esperados permanecen sin editar. Se comprueba hash canónico, aislamiento de componentes y contenido CAS. ADR-0017 documenta el componente. |
| Golden de negociación sin actualizar al añadir lectura de artifacts en `22128b1`. | 1: `StdioTransportTests.cs:1045`, falta `artifact.read.chunk` en `goldens.jsonl`. | Añadir la capacidad real al golden; mantener la comparación exacta con el contrato del Host. |

No se modifica el workflow para obtener verde ni se deshabilitan pruebas. Son
fallos reproducibles de lógica y contratos, sin dependencia de cuentas, claves,
red de proveedores ni diferencias de mayúsculas/rutas entre sistemas.

### Validación final de la corrección

El [PR #2](https://github.com/juancgomezm-s/OmniCore/pull/2) valida el head `fa16299`
en la [ejecución real 38781560543](https://github.com/juancgomezm-s/OmniCore/actions/runs/38781560543).
Conserva también el commit local `594f2e3` de Main: panel lateral limitado a
un tercio del ancho y 40 columnas, y la integración M9-G `f42d16e` de skills,
instrucciones y memorias externas. Los cinco jobs pasan con toda la combinación:

| Job | Resultado confirmado |
|---|---|
| [test (windows-latest)](https://github.com/juancgomezm-s/OmniCore/actions/runs/38781560543/job/116583130514) | PASS; 4664 casos, 4656 PASS, 8 SKIP existentes, 0 FAIL |
| [test (ubuntu-latest)](https://github.com/juancgomezm-s/OmniCore/actions/runs/38781560543/job/116583130082) | PASS; 4664 casos, 4643 PASS, 21 SKIP existentes, 0 FAIL |
| [npm-install (windows-latest)](https://github.com/juancgomezm-s/OmniCore/actions/runs/38781560543/job/116583130600) | PASS; pruebas Node, instalación y actualización reales con journal conservado |
| [npm-install (ubuntu-latest)](https://github.com/juancgomezm-s/OmniCore/actions/runs/38781560543/job/116583130737) | PASS; mismas comprobaciones reales |
| [linux-package](https://github.com/juancgomezm-s/OmniCore/actions/runs/38781560543/job/116583130645) | PASS; self-contained, Native AOT, SQLite, seis escenarios y terminales PTY |

Los nueve casos focales de recuperación Windows también pasan antes de la
integral y permanecen incluidos en ella; sus cantidades no se suman.
Las omisiones corresponden a pruebas existentes de plataforma, entorno o
aceptación opt-in de proveedores; no se añade ninguna omisión ni se desactiva
ninguna validación. La fuente C# integrada coincide con el head validado.

Validación local adicional:

- Windows: builds multi-target sin advertencias ni errores; focal amplia
  de Core 198/198 PASS. Tras corregir la referencia E2E, compilación nueva de
  `31fdf8b` y focal final 59/59 PASS, incluidos los 16 casos originales y ese E2E.
- Combinación con el panel lateral `594f2e3` y M9-G `f42d16e`: 538/538 PASS en Windows,
  incluyendo TUI, presentación, fingerprints, costos, transporte, skills externas
  y E2E de ruta. Compilación de la solución sin advertencias ni errores.
- Debian 13.5 / WSL2, Core `31fdf8b`: solución completa 4626 casos, 4605 PASS,
  21 SKIP existentes, 0 FAIL, salida 0; 8m34s. Build sin advertencias/errores.
  La combinación final con el panel lateral y M9-G se valida íntegra en Ubuntu CI.
- Instalaciones locales Windows y Debian actualizadas desde `fa16299` con los
  scripts públicos; se verifica el hash/contenido del Host instalado y `omni --help`.
  WSL queda apagado tras la comprobación, sin procesos vmmem activos.
- La integral Windows intermedia `2c05388` registró 4626 casos, 4613 PASS,
  12 SKIP de entorno y el único FAIL de la lista E2E corregida después.
  El resultado definitivo de toda la suite Windows es el CI del head final.

Logs completos y evidencia local en
`C:/Users/juanc/.codex/omnicore-linux-20261014/core-ci-*.log` y los logs de
los jobs enlazados. Las suites son superpuestas; no se suman sus casos.
El [CI de Main](https://github.com/juancgomezm-s/OmniCore/actions/workflows/ci.yml?query=branch%3Amain)
ejecuta de nuevo los mismos cinco jobs al publicar la integración. El PR
conserva además el enlace al resultado posterior de Main.

Los 16 fallos de esta reparación quedan cerrados; no hay regresiones conocidas
introducidas por el cambio. No se atribuye aceptación de proveedores reales,
ARM64, macOS, Strong Linux, OR5 ni el actualizador automático M10.

### Pasadas intermedias (no son el resultado final)

Pasadas intermedias: la focal ampliada de Windows pasa 198/198 sin omisiones.
La primera integral Debian sobre `2c05388` obtiene 4626 casos, 4604 PASS,
21 SKIP existentes y 1 FAIL: `CliEndToEndTests` aún esperaba la lista anterior
de componentes. Se añade `model.output` a esa lista exacta, conservando todos
sus elementos y la comprobación del orden. Se repite la integral; esa pasada
intermedia no se acredita como cierre.

## Reparación inicial del 2026-10-14 (histórico)

Fecha: 2026-10-14, Ciudad de México. Rama `codex/cierre-linux` y [PR #1](https://github.com/juancgomezm-s/OmniCore/pull/1). El [acta Linux](validation/linux-milestones-closure-20261014.md) conserva el inventario, logs, hashes y pasadas intermedias. La recuperación de la interfaz de preferencias confirmada por el usuario se prepara en otra rama; esta reparación no modifica su diseño.

## Workflow y criterio de cierre

`.github/workflows/ci.yml` se ejecuta con push a main, PR hacia main y despacho manual. La matriz `test` restaura, compila y ejecuta toda la solución en Ubuntu y Windows; `fail-fast: false` permite observar ambas plataformas. `linux-package` es independiente: publica self-contained y Native AOT y ejecuta escenarios CLI, recuperación de SQLite y terminales PTY reales, con PATH privado vacío y sin variables de localización del host .NET. No depende de la conclusión de los tests. Un fallo suyo es independiente; no es una consecuencia de `test`.

Los tres jobs finales conservan todas las pruebas y validaciones de empaquetado. Windows añade una focal previa para obtener fallo temprano en los nueve casos críticos de recuperación, Git, aislamiento y servidor administrado; esos casos también siguen en la integral y sus cantidades no se suman. La preparación de NUL se revierte con `always()`; cualquier fallo de preparación/restauración falla el job. No hay `continue-on-error`, resultados simulados ni supresión de códigos de salida. El job de diagnóstico y los hooks nativos temporales fueron retirados.

La validación final corresponde al **head del PR** y se consulta en [sus tres checks](https://github.com/juancgomezm-s/OmniCore/pull/1/checks). La descripción del PR registra el run final y sus resultados. No se autoriza la fusión mientras alguno no sea exitoso; las pasadas históricas siguientes no sustituyen esa validación.

## Causas y evidencia

| Fallo confirmado | Evidencia y clasificación | Reparación |
| --- | --- | --- |
| Compilación/publicación Native AOT | La carga YAML por deserialización dinámica emitía IL3050. Fallo de publicación, independiente de la matriz. | `ScenarioLoader` construye el árbol desde nodos YAML sin reflexión; valida profundidad antes de materializarlo. Se conserva el rechazo real de entradas profundas y se retira la supresión IL3050. |
| Diferencias Linux/Windows de fixtures | Nombre de apphost, comandos/rutas Windows, separador de identidad Unix, stdout/input y vida de owners M9. Pasadas Linux fallidas y correcciones detalladas en el acta. | Apphost nativo, argv de plataforma y cierres de store antes de reabrir/purgar. Se conserva la prueba de cancelación de procesos real en Linux y las assertions de ownership, dedup y replay. |
| Reapertura tras matar Host: SQLite 10/1546 | [Run 38677276693, diagnóstico Windows](https://github.com/juancgomezm-s/OmniCore/actions/runs/38677276693/job/116233729188): `SetEndOfFile failed: Win32=1224` sobre `journal.db-shm`, al ejecutar `PRAGMA journal_mode=WAL`, con el Host ya terminado. Reproducido también localmente. Fallo de ejecución Windows. | Espera acotada sólo para IOERR_TRUNCATE en la inicialización WAL de Windows, antes del esquema/efectos. Trace informa la espera y su recuperación. A los cinco segundos se propaga el error original; los demás errores y plataformas no se reintentan. Dos pruebas con mapeo real verifican recuperación, persistencia y fallo permanente. No se borra WAL/SHM ni se reenvían commands. |
| Git no puede abrir `/dev/null` | [Run 38671700600](https://github.com/juancgomezm-s/OmniCore/actions/runs/38671700600/job/116216065675): `could not open '/dev/null' for reading and writing: Permission denied`, exit 128. El runner nuevo carecía de ACE AppContainer en el dispositivo NUL. | Preparación explícita mediante handle en la VM efímera de CI, snapshot de su DACL y restauración obligatoria. Enable/Restore se rechazan fuera de GitHub Actions. No se cambia NUL en el equipo del usuario. |
| Git no puede normalizar cwd | [Run 38672460009](https://github.com/juancgomezm-s/OmniCore/actions/runs/38672460009/job/116218578138): superado NUL, `Unable to read current working directory: Permission denied`. Expandir RUNNER~1 a ruta larga no lo corrigió. | Fixtures bajo un TEMP sintético de la VM con lectura de metadata de directorios para AppContainer, sin herencia de lectura a archivos. No se modifica la ACL de la raíz del volumen. El caso Strong sigue exigiendo denegación del archivo externo. |
| Git reconoce el repo pero `diff` falla | El diagnóstico 38677276693 lee `.git/HEAD/config`, obtiene `--is-inside-work-tree=true` y `core.bare=false`; `diff --check` devuelve `this operation must be run in a work tree`, exit 128, incluso declarando la raíz de instalación. `mingw_chdir` usa GetFinalPathNameByHandleW/VOLUME_NAME_DOS cuando core.symlinks está activo. | El repo sintético de M3 no contiene symlinks: fija `core.symlinks=false` en Windows. Mantiene `git diff --check`, el error de whitespace, el feedback al modelo y la reparación comprobada. No amplía permisos del runtime ni modifica repos reales. La compatibilidad Git Strong con symlinks permanece limitada por esa API de Windows. |
| stdin de procesos Strong sin payload | Probe nativo de GetStdHandle: handle inválido, exit 28. Era independiente del dispositivo NUL abierto internamente por Git. | Pipe heredable válido con EOF aunque no haya payload; los dos casos de stdin siguen probándose. |
| Assertions TUI sensibles a foco/hilo | Marcador de captura de conversación dependía del footer de enlaces; lectura de layout del catálogo desde el hilo del test. | Marcador de contenido estable y assertion de layout en el hilo UI, conservando texto y límites de tamaño. Se conserva RGB al rasterizar evidencia. |
| Servidor PowerShell de prueba no llega a Ready | [Run 38683541843, Windows](https://github.com/juancgomezm-s/OmniCore/actions/runs/38683541843/job/116253174663), único fallo de 4562: `Managed_local_server_is_launched_with_its_own_key_reused_across_turns_and_stopped_on_dispose`, exit 1 de la primera consulta, «El servidor local de «scripted» no estuvo listo a tiempo», línea 2107 de esa revisión. Al cerrar una conexión sin HTTP, ReadLine devuelve null y el parser de headers repetía indefinidamente porque null es distinto de cadena vacía. Reproducido con el proceso real y un socket vacío. | Sólo el fixture ignora EOF en request/headers y continúa aceptando clientes. Dos casos nuevos fuerzan conexión vacía y request truncado, seguidos de readiness HTTP autenticada. Se conserva el límite original de 90 segundos, drenaje de 3 MB, claves, dos consultas, reutilización, parada y ausencia de secretos. No se modifica el servidor de producción ni se amplían sus plazos. |

La instrumentación nativa registrada en el run de evidencia llamaba a SetEndOfFile real y devolvía el BOOL/error sin alterarlos. Se usó para diagnosticar, no para acreditar una pasada. Microsoft documenta [1224 como ERROR_USER_MAPPED_FILE](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-); SQLite [IOERR_TRUNCATE](https://www.sqlite.org/rescode.html#ioerr_truncate) y la [inicialización del WAL-index](https://www.sqlite.org/walformat.html). La incompatibilidad del path DOS en AppContainer está documentada en [microsoft/STL #6286](https://github.com/microsoft/STL/issues/6286); [Git for Windows](https://raw.githubusercontent.com/git-for-windows/git/v2.56.0.windows.2/compat/mingw.c) confirma la rama usada por symlinks. El [harness de libuv](https://github.com/libuv/libuv/blob/v1.x/test/appcontainer.c) documenta el requisito de NUL.

## Reproducibilidad y versiones

Debian 13.5/WSL2 x64, usuario sin privilegios; .NET SDK 10.0.401 y runtime 10.0.12. `global.json` fija el SDK también en CI. Se verifican Protocol/Client/Sandbox net8 y net10. No se cambiaron versiones de dependencias para obtener una pasada. Git del runner evolucionó de 2.55.0.windows.5 a 2.56.0.windows.2; la evidencia de las rutas y la API, y no la diferencia con Git local 2.54, sustenta el diagnóstico.

La matriz ejecuta solución completa en serie con stdin cerrado. Linux publica ambas distribuciones con warnings tratados como errores y valida escenarios y PTY reales. Windows requiere el host preparado para AppContainer; ese requisito de CI no se presenta como provisión automática del runtime en instalaciones nuevas.

## Pruebas ejecutadas y resultados

| Evidencia | Resultado observado | Alcance |
| --- | --- | --- |
| [Run 38668015994](https://github.com/juancgomezm-s/OmniCore/actions/runs/38668015994), fuente 0789ebf | Ubuntu 4553 total / 4536 PASS / 17 SKIP / 0 FAIL; linux-package PASS; Windows 4543 PASS / 8 SKIP / 2 FAIL | Pasada integral que expuso los dos fallos Windows; no era cierre de CI. |
| Debian sobre integración M9-D 793e1fa | Build 0 warnings/errores; 20/20 `StdioTransportTests` | Procesos reales, ownership, recibos, replay y nuevas acciones de M9-D. |
| Windows `windows-wal-git-final.log` | 23/23 PASS, 0 omitidas | Los 20 casos de transporte M9-D, dos mapeos reales y el gate Git. Incluye doce caídas independientes; cualquier fallo aborta el estrés. |
| Debian integral, fuente C# b673091 | 4557 total / 4538 PASS / 19 SKIP / 0 FAIL, 11m37s; build sin warnings/errores, runner exit 0 | `acceptance-b673-full.log`, `acceptance-b673-exit.txt`; ambas distribuciones publicadas y ejecutadas. |
| Pruebas de mapeo real | Liberado: reapertura y recibo Completed persistido; retenido: error original 10/1546 tras cinco segundos, sin truncar el archivo; luego se puede reabrir | No se sustituye SQLite ni se simula el resultado del sistema operativo. |
| [Run 38683541843](https://github.com/juancgomezm-s/OmniCore/actions/runs/38683541843), combinación M9-E f8c9b49 | Ubuntu 4562 total / 4543 PASS / 19 SKIP / 0 FAIL; linux-package PASS; Windows 4553 PASS / 8 SKIP / 1 FAIL | Conserva M9-E y el tema actual de main. Expone el EOF del fixture administrado; los errores anteriores de SQLite y Git ya pasan. No acredita cierre de CI. |
| Reproducción y reparación EOF PowerShell, Windows local | Antes: regresión con socket vacío falla Ready/Unreachable. Después: 3/3 PASS, 0 omitidas, 23s | `managed-empty-probe-baseline.log` y `managed-empty-probe-fixed.log`: proceso y sockets reales; dos variantes nuevas y el recorrido administrado original. |
| [Run 38686930511, focal Windows](https://github.com/juancgomezm-s/OmniCore/actions/runs/38686930511/job/116263817546) | 7 PASS / 2 FAIL; el caso administrado original pasa en 33,8s; las dos regresiones nuevas agotan sus 10s sin alcanzar siquiera el primer socket | Los tests nuevos habían introducido un presupuesto de arranque inferior al de la fixture original. Se alinean a los 90s ya existentes, sin cambiar plazos de producción ni del caso original. Se conserva el fallo real si el servidor no arranca o queda bloqueado tras EOF. Paquetes Linux PASS; esta pasada no acredita cierre. |
| [Checks del head del PR](https://github.com/juancgomezm-s/OmniCore/pull/1/checks) | Registro definitivo de Windows, Ubuntu y linux-package; run y cantidades en la descripción del PR | Deben aprobarse los tres antes del merge. |

Los conjuntos son superpuestos y no se suman. El acta conserva los intentos fallidos y las evidencias anteriores. Los siete casos opt-in de cuentas/modelos reales continúan declarados; las pruebas específicas de Windows se omiten explícitamente en Linux. Ningún test existente se deshabilitó.

La revisión final detectó un hueco de evidencia: los primeros smoke eliminaban DOTNET_ROOT pero heredaban PATH del proceso de publicación. Se corrigieron ambos scripts para usar un directorio privado vacío como PATH, retirar también DOTNET_HOST_PATH y verificar que `dotnet` no es descubrible. Las pasadas anteriores no acreditan ese aislamiento del entorno. La nueva validación no desinstala el SDK necesario para compilar: aísla el entorno de los ejecutables publicados.

Los paquetes Debian recién publicados de b673091 se ejecutaron de nuevo con los scripts corregidos: seis escenarios verificados por paquete y cuatro PTY en total, todos con exit 0 (`strict-sdk-path-b673/`). La modificación final sólo cambia scripts/evidencia; la fuente C# coincide con b673091, cuya integral Debian está registrada arriba.

## Pendientes fuera de esta reparación

M2/M3 requieren aceptación con el modelo local; Strong Linux no tiene backend implementado. OR5 autenticado y los consumidores pendientes de metadata de skills siguen en M8. M9/M10 conservan sus pendientes de producto/consumidor. Las pruebas de este informe no acreditan cuentas, inferencias pagadas, aceptación humana de Electron ni soporte de filesystem de red. Los cambios nuevos de main/M9-D se conservan; no se ha fusionado el PR sin la validación final.
