# M9-M — Persistencia Weak con ownership nativo de procesos (2026-10-24)

**Estado:** corte Windows construido, cableado y aceptado localmente tras la revisión final de Luna bajo: **28 casos aprobados**. La revisión independiente inicial quedó **NO APROBADO** por un fallo de cleanup en `DisposeAsync`; su informe original se conserva sin cambios en `artifacts/m9-weak-persistence/low-validation-20261024.md`. M9 global y M8 manual continúan abiertos. Este corte se conserva como checkpoint local; no se publicó, no se integró en `main` y no ejecutó CI remota.

## Corrección posterior al NO APROBADO

`DisposeAsync` ahora captura errores de enumeración, terminación, espera y timeout, y libera en `finally` Job, proceso, pipes y pseudo-console. Cierra primero el Job con `KILL_ON_JOB_CLOSE`, espera al proceso raíz y luego cierra HPCON para evitar que un descendiente mantenga vivo el transporte. Los errores de espera también se capturan para que no interrumpan la liberación restante. La misma secuencia se usa si falla la creación del control tras reanudar.

El helper nativo marca internamente el éxito de `ResumeThread`; el launcher Weak convierte cualquier fallo posterior a esa frontera en `ProcessLaunchOutcomeUnknownException`, incluso si cleanup termina confirmando el proceso. Strong pasa por el mismo helper sin consumir ni alterar esta política. El test de fábrica inyectada espera el marcador del descendiente antes de lanzar el fallo, por lo que demuestra que ya corrió código usuario y que no se tipa como prestart. `PersistentProcessSessionTools` mantiene la excepción unknown por su catch general; no se cambió Host.

Se agregó `IWindowsWeakProcessHooks`, limitado al assembly, para inyectar fallos de query/terminate/espera/fábrica con un Job propio. Los cuatro métodos nuevos cubren query fallida con descendiente real y marcador diferido, terminate fallida con HPCON real y cierre sin deadlock, espera del proceso fallida con comprobación de cierre de handles, y fallo de fábrica tras comprobar ejecución y cleanup de árbol. No se inducen fallos globales de Windows.

## Contrato entregado

`PlatformSandboxProcessLauncher` enruta una solicitud persistente Weak de Windows a `WindowsWeakPersistentProcessLauncher`. Weak conserva su etiqueta: el Job Object garantiza ownership y terminación del árbol, sin aplicar AppContainer ni anunciar aislamiento Strong.

El launcher usa `CreateProcessW` suspendido. Antes de `ResumeThread`, crea un Job Object con `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, asigna el proceso y conserva los handles de proceso, Job, entrada y salida. No activa flags de breakaway ni `CREATE_BREAKAWAY_FROM_JOB`. Los pipes capturan stdin/stdout/stderr; ConPTY reutiliza la primitiva HPCON y resize ya existente.

El control espera la salida del proceso raíz. Si aún quedan miembros del Job, termina el Job y espera hasta que quede vacío antes de cerrar ConPTY o depender de EOF en los pipes. Así se evita que un descendiente que conserva stdout/stderr impida terminar el monitor. `process.stop`, cancelación aceptada, disposición y cierre del Host actúan sobre el Job retenido. La salida real sigue fluyendo por `PersistentProcessSessionManager`, que decodifica UTF-8, asigna secuencias y conserva límites/gaps. Entrada y EOF usan su canal estándar.

El hijo recibe solo la lista Windows exacta de variables no sensibles conocida por el launcher AppContainer existente, junto con overrides explícitos de la solicitud y el pseudo-environment de directorio actual. No copia claves por prefijo. El test instala `OMNICORE_M9_WEAK_SENTINEL=fake-secret-environment-sentinel` dentro de la colección serializada `ProcessEnvironmentCollection`, comprueba que el hijo no lo ve y restaura el valor anterior en `finally`; el log solo muestra el override ficticio permitido y el estado `excluded`.

Los errores de creación nativa previos a reanudar el hilo se informan como `ProcessLaunchUnavailableBeforeStartException` cuando el helper confirma la limpieza del proceso suspendido. Si la limpieza no se puede confirmar, se conserva el resultado ambiguo; no se reintenta como Weak por cuenta propia. Strong mantiene su selector y políticas actuales. El corte no cambia perfiles, permisos, grants, sesión, modelo ni configuración del usuario.

## Recorrido Host y consentimiento

El caso `Native_weak_process_controls_use_the_real_persistent_job_without_an_extra_provider_request` fija Weak explícitamente solo en el fixture. Pasa por ToolRuntime, Host, `PersistentProcessSessionManager`, el helper persistente real y el canal nativo de control. Demuestra `process.start`, lectura secuenciada, `send_input`, `stop`, EOF y salida terminal; el proveedor recibe exactamente tres requests planeados y ninguno adicional para controles. La política del fixture permite los cuatro tools; no crea grants.

La frontera existente de WeakConsent se conserva: la elección se negocia con la capability nativa existente, la preflight espera antes de `started`, Deny no lanza, `consent_once` reanuda solo el ToolCall/claims exactos, la política se vuelve a consultar y un resultado Strong desconocido no abre fallback Weak. Esos casos usan los predicados de preflight existentes; el test Host anterior representa una selección Weak explícita y no se presenta como consentimiento por Strong fallido.

ConPTY Weak se probó por separado con HPCON, entrada real, resize `100x32` y ACK `ACK:terminal-test;size=100x32`. También pasó una regresión del ConPTY Strong existente tras el refactor de la primitiva nativa compartida.

## Pruebas focales y conteo del candidato anterior

La lista de 24 casos y sus resultados que sigue corresponde al candidato previo a la corrección. Ese candidato tuvo 24 PASS, pero Low confirmó después el defecto de cleanup y su validación independiente se detuvo antes de correr métodos (0 de 24). Por eso esos resultados son históricos y no aceptan esta candidata corregida.

Build final de Tests/Core: `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore`, 0 advertencias y 0 errores. Resultado final: **24 casos seleccionados, 24 PASS, 0 FAIL, 0 SKIP**, un método por ejecución con el runner xUnit v3. Cada log individual está en `artifacts/m9-weak-persistence/`.

1. `WindowsWeakPersistentProcessTests.Weak_persistent_pipes_preserve_utf8_and_deliver_eof`
2. `WindowsWeakPersistentProcessTests.Weak_session_manager_sequences_real_process_output_and_routes_stop_to_its_job`
3. `WindowsWeakPersistentProcessTests.Weak_terminal_uses_real_conpty_for_input_resize_and_output`
4. `WindowsWeakPersistentProcessTests.Weak_child_receives_only_allowlisted_parent_environment_and_explicit_launch_overrides`
5. `WindowsWeakPersistentProcessTests.Weak_job_kills_descendant_and_prevents_its_delayed_marker`
6. `WindowsWeakPersistentProcessTests.Root_exit_terminates_descendant_before_waiting_for_inherited_pipe_eof`
7. `WindowsWeakPersistentProcessTests.Weak_process_cancellation_before_launch_does_not_create_a_child`
8. `WindowsWeakPersistentProcessTests.Weak_native_launch_failure_is_typed_as_prestart_and_never_runs_user_code`
9. `WindowsWeakPersistentProcessTests.Cancelled_stop_has_no_effect_and_host_disposal_closes_the_owned_job`
10. `StdioTransportTests.Native_weak_process_controls_use_the_real_persistent_job_without_an_extra_provider_request`
11. `StdioTransportTests.Weak_sandbox_consent_requires_explicit_opt_in_in_addition_to_native_choice`
12. `StdioTransportTests.Trusted_legacy_hello_without_native_choice_does_not_advertise_weak_sandbox_consent`
13. `WeakSandboxConsentPreflightTests.Unavailable_preflight_pends_before_started_without_invoking_the_launcher`
14. `WeakSandboxConsentPreflightTests.Explicit_deny_resumes_the_exact_call_without_starting_a_process`
15. `WeakSandboxConsentPreflightTests.Consent_once_retries_only_the_same_call_as_weak_and_starts_one_process`
16. `WeakSandboxConsentPreflightTests.Resume_rejects_changed_arguments_or_resolved_process_claim_before_launch`
17. `WeakSandboxConsentPreflightTests.Resume_rechecks_current_permission_policy_and_does_not_treat_consent_as_a_grant`
18. `WeakSandboxConsentPreflightTests.Unknown_or_unproven_strong_launch_never_opens_weak_consent_or_falls_back`
19. `StdioTransportTests.Accepted_generic_process_profile_child_is_controlled_inside_its_open_turn_without_an_extra_provider_request`
20. `WindowsAppContainerSandboxTests.Strong_terminal_uses_ConPTY_for_real_duplex_input_resize_and_output`
21. `WindowsAppContainerSandboxTests.Strong_request_selects_the_available_launcher_without_starting_the_weak_runtime`
22. `WindowsAppContainerSandboxTests.Strong_request_is_not_silently_downgraded_when_unavailable`
23. `StdioTransportTests.Native_process_controls_cover_active_and_quiescent_run_then_passively_read_terminal_output`
24. `SystemProcessRuntimeEnvironmentTests.Windows_runtime_passes_APPDATA_but_excludes_unrelated_environment_values`

La compilación multiplaforma compartida requerida por ADR-0038 también pasó: `dotnet build src/OmniCore.Sandbox/OmniCore.Sandbox.csproj -f net8.0 --no-restore`, 0 advertencias y 0 errores. Evidencia: `artifacts/m9-weak-persistence/build-final.log` y `build-sandbox-net8.log`.

Una ejecución inicial del test root-exit reveló que esperar a que el Job se vaciara sin terminar miembros supervivientes dejaba correr el marcador del descendiente. El completion path se corrigió para terminar el Job antes de esperar EOF; el foco final y la ruta Host volvieron a pasar. Los diagnósticos y candidatos anteriores se preservan en subdirectorios `candidate-*` y archivos `diagnostic-*` del mismo artifact root.

## Límite Linux observado

Debian ya estaba presente en Zephyrus WSL2: kernel `6.18.33.2-microsoft-standard-WSL2`, Bubblewrap `0.12.0`, UID efectivo `0`, cgroup v2, raíz writable, controllers `cpuset cpu io memory hugetlb pids rdma`, y `cpu memory pids` habilitados para subtree. La sesión actual está en `/init.scope`. La inspección fue de lectura; no creó procesos fixture, no creó/movió grupos cgroup y no tocó `subtree_control`.

El launcher Linux existente es Bubblewrap Strong y usa `Process.Kill(entireProcessTree)` para cleanup. Eso no establece ownership persistente Weak y permanece como rechazo tipado; no se anuncia soporte Linux ni se amplía el selector de plataforma. Una fase futura deberá diseñar y aportar evidencia del ciclo de vida de grupos cgroup v2 efímeros propios para fixtures, usando la sesión WSL observada, sin tocar `cgroup.subtree_control`, la configuración global ni procesos/grupos ajenos. Evidencia de lectura: `artifacts/m9-weak-persistence/wsl-linux-boundary.log`.

La implementación Linux permanece fuera de esta candidata. La siguiente fase deberá aportar diseño y evidencia de ownership cgroup v2 en la sesión WSL observada; esta acta no declara una necesidad adicional de autorización por crear grupos efímeros propios. No se realizaron efectos Linux en esta corrección.

## Revalidación de la candidata corregida

Build net10 de Tests: `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore`, 0 advertencias y 0 errores. Build net8 compartido: `dotnet build src/OmniCore.Sandbox/OmniCore.Sandbox.csproj -f net8.0 --no-restore`, 0 advertencias y 0 errores. Logs: `artifacts/m9-weak-persistence/build-fix-tests-net10.log` y `artifacts/m9-weak-persistence/build-fix-sandbox-net8.log`.

Focos finales de la candidata corregida: 7 métodos, 7 PASS, 0 FAIL, 0 SKIP. Los cuatro métodos nuevos se enumeran primero. `Control_factory_failure_after_resume_returns_unknown_and_closes_owned_tree` prueba el launcher directamente; no se presenta como prueba de ese error atravesando el manager. Los otros focos recorren el manager, ConPTY y root-exit. Cada método tiene un log en `artifacts/m9-weak-persistence/fix-*.log`.

1. `WindowsWeakPersistentProcessFailureTests.Dispose_query_failure_closes_owned_job_before_hpc_on_and_kills_descendant`
2. `WindowsWeakPersistentProcessFailureTests.Dispose_terminate_failure_closes_job_before_conpty_teardown_without_deadlock`
3. `WindowsWeakPersistentProcessFailureTests.Dispose_wait_failure_still_releases_owned_handles_after_job_termination`
4. `WindowsWeakPersistentProcessFailureTests.Control_factory_failure_after_resume_returns_unknown_and_closes_owned_tree`
5. `WindowsWeakPersistentProcessTests.Weak_session_manager_sequences_real_process_output_and_routes_stop_to_its_job`
6. `WindowsWeakPersistentProcessTests.Weak_terminal_uses_real_conpty_for_input_resize_and_output`
7. `WindowsWeakPersistentProcessTests.Root_exit_terminates_descendant_before_waiting_for_inherited_pipe_eof`

Esta revisión focal del constructor no repitió la lista histórica completa de 24. La validación independiente final sí ejecutó la lista original más los 4 métodos nuevos.

## Aceptación independiente final

Luna bajo revisó las rutas corregidas y ejecutó secuencialmente los **28 métodos**
contra la DLL congelada, sin recompilar: **28 PASS, 0 FAIL, 0 SKIP y 0 no
ejecutados**. Cada método seleccionó exactamente un caso. Los 19 archivos del
manifiesto y `x` coincidieron antes y después; las copias cargadas de Sandbox,
Execution y Host en `tests/bin/Debug/net10.0` también coinciden con sus equivalentes
de `src/bin`. No quedaron procesos `OmniCore.SandboxTestProcess` propios.

El dictamen es **APROBADO para este corte local Windows Weak**. Los casos nuevos
comprueban cierre ante fallos de consulta, terminación y espera, además de
clasificación desconocida y terminación del árbol cuando falla la creación del
control después de ejecutar código. La propagación de ese error específico por
el catch del manager se revisó en código; el caso Host usa el recorrido Weak
normal real, sin solicitudes adicionales al proveedor por los controles.

Informe independiente local:
`artifacts/m9-weak-persistence/low-final-validation-20261024.md`, SHA-256
`42C9A64259E0C0BF51D0296482BFD7A67362133EAC968938545FE6298B359214`.
Logs: `low-final-01.log` a `low-final-28.log` en el mismo directorio ignorado.
La revisión inicial rechazada permanece separada; sus cero pruebas ejecutadas
no se suman a las 28 finales ni convierten la campaña previa en aceptación.

## Límites de aceptación

La validación es local y focal en Windows más compilación net8 de Sandbox. No se ejecutó la suite completa ni CI; Linux permanece sin backend Weak. El checkpoint es local, sin publicación, merge ni push. M9 global y M8 manual permanecen abiertos. Los fixtures no acreditan aceptación interactiva ni un proveedor live; el sentinel ficticio no prueba ausencia universal de secretos desconocidos.

## Manifiesto congelado aceptado por Luna bajo

Los hashes identifican las fuentes, los binarios probados y el helper ejecutable después de los builds y siete focos finales. `x` conserva su hash anterior.

| Archivo | SHA-256 |
|---|---|
| `src/OmniCore.Execution/PlatformSandboxProcessLauncher.cs` | `F4326F45385A3FF7D9119B05977A4BC3E66829AEC3239F07B7DB19B6DB960DEF` |
| `src/OmniCore.Execution/SystemProcessDuplexLauncher.cs` | `D5936F4CB96FDF3998C01F9075846039B9882564CAE49772EE6508AC8C0D65A2` |
| `src/OmniCore.Sandbox/OmniCore.Sandbox.csproj` | `930B00758F6BFAACA26EB05659B9804A760EAA6241A487E1E4B1B8390AA439C0` |
| `src/OmniCore.Sandbox/ProcessSandbox.cs` | `633C361941A7938B1544175882B06A0478A90C8787832E003744A61ACFBCB4E8` |
| `src/OmniCore.Sandbox/WindowsAppContainerProcessLauncher.cs` | `DD4BFAE63E70315EB021889B6F25E4CF68815097A8619EA4C97E0446B4B7ADB2` |
| `src/OmniCore.Sandbox/WindowsWeakPersistentProcessLauncher.cs` | `E9CC912B7D4E41D1BBA7145B21870CE12616B4B2AB5D17A78D852E238C2B3DCB` |
| `tests/OmniCore.SandboxTestProcess/Program.cs` | `E6A48EC951D6E084845351923CDDB65F2CA23A84C9EA78862B30067684238B0D` |
| `tests/OmniCore.Tests/WindowsWeakPersistentProcessTests.cs` | `BA5E40273DC6B2AF4EFE0BCC74D3EE346BFD48ED6E707892EB2B357508BEEAC5` |
| `tests/OmniCore.Tests/StdioTransportTests.NativeProcessControl.cs` | `CCCE2EC67DC474049ADCD22E2CAF5167AFEF897880CE24997ED7B6D656BE5334` |
| `tests/OmniCore.Tests/StdioTransportTests.NativeCommandHandoff.cs` | `005A13E00AFFC463B9BEFF2F8DD14954B68884F78D61A094999EE8C2B75BCC23` |
| `tests/OmniCore.Tests/WindowsWeakPersistentProcessFailureTests.cs` | `DE8C703B950E28AB0AB36A5D6BCD685549C5F5F82091BCE9C263E65C4F83C816` |
| `src/OmniCore.Sandbox/bin/Debug/net10.0/OmniCore.Sandbox.dll` | `50C3B7DAE79A31BD5500A6044DA368774E1AD0DF39E7C6276D69310E9D8EDA0D` |
| `src/OmniCore.Sandbox/bin/Debug/net8.0/OmniCore.Sandbox.dll` | `2AFAA488AC0D1AF4BEBDE2B247BDA4F715F1A9767ADC95E3CEF67BAFFBA6B1FB` |
| `src/OmniCore.Execution/bin/Debug/net10.0/OmniCore.Execution.dll` | `D8D30BBCDD8AFB1D1B8D45CB5B55E72C8A699E7B9F58303541CA7881BDF1BC1D` |
| `src/OmniCore.Host/bin/Debug/net10.0/OmniCore.Host.dll` | `0A6C9C8CF0525D6278A88C7B72F48CB52B97579E21E695ACB91B2B06D63C98CF` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll` | `20E8DFC8A75F50B3A302BA1BB73FC80C02C793C64DCC86790876052B2E2187CD` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.SandboxTestProcess.exe` | `29C1FDBD1A61C01A5C3D9E9BFB9D442C3F56FC3AD9CFCEA31B01CCF9D9258E2C` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.SandboxTestProcess.dll` | `704BA98C0494D2327AF8EEADE2EF24095FB69C4AADF80CE3420723CA7F440D0D` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.SandboxTestProcess.runtimeconfig.json` | `256DDA2706E1151BB95987B6B5BE610BF68D64E058285B19E7E958222FC90414` |

El archivo ajeno sin seguimiento `x` se conservó sin cambios; SHA-256 `84E292A1C73B431B64FBC2C8F0FA8B08CC771E58B98F05FB968C842CBBF6FE45`.
