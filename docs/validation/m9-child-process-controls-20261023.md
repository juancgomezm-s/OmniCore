# M9 — controles de procesos de delegaciones genéricas

**Estado:** aceptado localmente tras corregir el hallazgo de Luna bajo y aprobar su revalidación focal sin recompilar. No implica CI, publicación ni cierre global de M9.

## Alcance y decisiones

- Los perfiles `DelegatedAgent` que ya permiten proceso reciben un manager persistente aislado por Execution y ligado a la raíz física de su Lane/worktree. Se conservan las cuotas agregadas de Session y el ownership de Task, Run, Lane y Execution. Las queries de sesiones/salida son pasivas, acotadas y no crean ni restauran managers.
- La autoridad nativa se deriva solo de una delegación aceptada vigente y de la capacidad de elección nativa negociada para el `process.control` autenticado. El handle opaco resuelve el proceso del hijo; el cliente no aporta PID ni ruta. El control se atiende en el `ExplorerTurn` original abierto del hijo, con el `ToolRuntime`, la política, los claims, el sandbox y los límites existentes.
- Un Permission `Ask` del proceso hijo se responde en ese mismo Turn. La respuesta exige el receipt diferido y el payload protegido exactos, el admission `core:process.control`, la llamada `native-action`, el Permission/Interaction y el scope Task/Lane/Execution/Turn/ToolCall correlacionados. La continuación queda encolada detrás de la tarea que publicó el receipt; no inicia efecto antes de terminar esa tarea. No se crea otro Turn ni se llama de nuevo al proveedor.
- La proyección de `RunInteractionResumed` acepta esta ruta sólo con el admission canónico y una única llamada/Permission/Interaction/resolución con scope idéntico. `RunProjection` y `CanonicalStateTracker` aplican la misma condición. Outcomes parciales o desconocidos dejan el receipt fuente en estado reconciliable `unknown`; un fallo definitivo sólo se registra cuando el ToolRuntime demuestra `EffectOutcome.None`. El receipt de la respuesta humana representa `completed/permission_choice_accepted`.
- Un Run/Turn/Execution cerrado pierde autoridad de control. Se conservan los receipts y la salida pasiva; no hay fallback `ownsTurn` para reactivar el hijo. El cierre/stop y cleanup respetan Session/Run/Execution y no actúan sobre procesos ajenos.
- `WorkflowStage`/`Verify` conserva sus builders, catálogo y ejecución de una sola llamada. Este incremento no le agrega `process.start` ni otra superficie de proceso. La admisión de la delegación del fixture se hizo con `SendUserAction` Core real y `DelegationAsync` real; la fixture de controles sí entra por Host/stdio autenticado. No se afirma admisión de esa delegación por stdio end-to-end.

## Evidencia focal

Comandos ejecutados en `C:\Users\juanc\.codex\worktrees\m9-completion\OmniCore`:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.StdioTransportTests.Accepted_generic_process_profile_child_is_controlled_inside_its_open_turn_without_an_extra_provider_request"
```

- Validación del cambio final, una sola build: `artifacts/m9-child-process-controls/build-replay-binding-fix-20261024T032858Z.log`; compilación correcta, cero advertencias y cero errores.
- Foco integral con los dos negativos de cruce, ejecutado una sola vez en la DLL nueva: `artifacts/m9-child-process-controls/test-replay-binding-fix-20261024T032909Z.log`; 1 caso, 1 correcto, 0 fallidos, 0 omitidos, 9.202 s.
- La primera validación Low de 6 casos corresponde al corte anterior: encontró la carencia que ahora cubren los negativos. La fase final revalidó el único caso afectado con la DLL actual, sin repetir las otras regresiones; véase el cierre de esta acta.
- La fixture integral usa proveedor HTTP local determinista y el helper Windows Strong `OmniCore.SandboxTestProcess`. Comprueba Ask de `process.send_input`, retención explícita de la tarea source tras persistir `deferred`, ausencia de efecto mientras está retenida, continuación en el Turn hijo, entrada `ñ`/EOF, salida UTF-8 `PIPE:ñ`, proceso B y stop; confirma recibos y replay, cero requests extra al proveedor, y que las queries pasivas no mutan el journal.
- En la misma fixture, `RunProjection.Replay` y `CanonicalStateTracker.Replay` aceptan el marcador válido y rechazan los casos sin admission, con ExecutionId distinto, ToolCallId distinto, InteractionId de resolución distinto y expiración del target seguida por resolución de otra InteractionId del mismo scope.
- El test comprueba también el rechazo del perfil que niega `process.start`, replay de stop sin duplicar efecto, consulta de estado terminado y control rechazado cuando el child ya no es elegible.
- El proceso auxiliar usa lectura/escritura explícitas UTF-8 sin BOM para que la aserción de pipes sea independiente de la codepage de consola de Windows.

El PASS es **un caso integral** con múltiples aserciones, no varios tests. La admisión se realiza dentro del fixture mediante `SendUserAction` y `DelegationAsync` reales de Core; `Host/stdio` realiza el control autenticado del proceso. No se afirma admisión de una delegación desde stdio end-to-end. La comprobación `PIPE:ñ` usa UTF-8 explícito únicamente en `OmniCore.SandboxTestProcess`; no cambia codepage ni configuración de consola del host.

### Correlaciones y causas observadas

- En el artefacto diagnóstico durable `diagnostic-generic-child-20261024T031547650Z-2cd9c96217254849a0736bcfa3dfb2a3.log`, el scope de la delegación que mostró el orden es Session `01a16be7-e7a0-7208-a8f3-e6375200e37a`, Run `01a16be7-ea86-7206-98aa-9423f885b0bf`, Task `01a16be7-f007-764f-ac6f-ed8e6811367a`, Lane `01a16be7-f007-7622-b162-ea8a5dbbdf91`, Execution `01a16be7-f08c-7204-9a27-67d875ca56e0`. El journal registró `PermissionRequested` seq 62 (`01a16be7-fa18-7cae-b5cd-185054708ad6`) y después `InteractionRequested` seq 63 (`01a16be7-fa1b-721b-9080-9a0eca1e5da7`). La comparación de continuación exigía el orden inverso; corregirla al orden causal observado permitió continuar sin rebajar la correlación. No se atribuyen a este scope eventos posteriores de otros artefactos o fixtures.
- Los diagnósticos `diagnostic-generic-child-20261024T031256940Z-46b5abf7d532497a99809d4cdfa44a82.log` y `...031547650Z-2cd9c96217254849a0736bcfa3dfb2a3.log` preservan el fallo de proyección y la secuencia que lo siguió. La excepción observada fue `interaction resume lacks matching resolved user consent`: `RunProjection` reconocía `worktree.integrate`, pero no el caso acotado del control de proceso delegado. El mismo predicado puro y limitado al caso canónico se aplicó a `RunProjection` y `CanonicalStateTracker`; los asserts de replay positivo y negativos están en el único caso integral final.
- El diagnóstico anterior a la revisión final encontró que `System.Console` decodificaba el pipe con la codepage OEM de Windows. La corrección se limitó a lectura/escritura UTF-8 explícita en el helper de prueba. El PASS final verifica `PIPE:ñ` interpretando el contenido del proceso.
- El archivo `test-focused-final-20261024T044100Z.log` registra el PASS previo a la revisión baja y no cubre el defecto descubierto allí. Los diagnósticos históricos guardan metadatos y algunos cuerpos de solicitudes del proveedor; no se reproducen aquí ni forman parte del resultado versionado.

### Archivos modificados por este corte

```text
src/OmniCore.Engine/CanonicalStateTracker.cs
src/OmniCore.Engine/Projections.cs
src/OmniCore.Engine/RunControlService.cs
src/OmniCore.Host/DelegationExecution.cs
src/OmniCore.Host/ExplorerTurn.cs
src/OmniCore.Host/HostTools.cs
src/OmniCore.Host/OmniCliRuntime.cs
src/OmniCore.Host/OmniServer.NativeCommands.cs
src/OmniCore.Host/OmniServer.cs
src/OmniCore.Host/PersistentProcessSessionTools.cs
src/OmniCore.Host/ScriptedToolExecutor.cs
src/OmniCore.Host/StdioProtocolHost.cs
tests/OmniCore.SandboxTestProcess/StandardInputProbe.cs
tests/OmniCore.Tests/StdioTransportTests.NativeCommandHandoff.cs
tests/OmniCore.Tests/StdioTransportTests.NativeChildProcessControls.cs
docs/validation/m9-child-process-controls-20261023.md
```

`docs/plans/m8-m10-user-localization.md` pertenece al coordinador y no es cambio de este corte. El archivo untracked `x` se preservó sin modificación; SHA-256 `84E292A1C73B431B64FBC2C8F0FA8B08CC771E58B98F05FB968C842CBBF6FE45`.

Los diagnósticos previos se preservan en `artifacts/m9-child-process-controls/diagnostic-generic-child-*.log`; registran las causas corregidas durante el wiring, incluida la proyección que sólo aceptaba `worktree.integrate`, el orden causal `PermissionRequested` → `InteractionRequested` y la decodificación de consola. No se usaron para ocultar fallos ni forman parte del PASS final.

## Hashes congelados del PASS final

SHA-256 de los fuentes y binarios principales en el checkout compilado:

| Archivo | SHA-256 |
|---|---|
| `src/OmniCore.Engine/Projections.cs` | `C715EC3FE7A52922B29BFB97F87D336760ABF04855817BE7DC72C9F93253EAB7` |
| `src/OmniCore.Engine/CanonicalStateTracker.cs` | `F13C623CF8C7F2C400BAB988ED7367217D4417AA9CB144AC7822D37F157367D4` |
| `src/OmniCore.Engine/RunControlService.cs` | `45564CA5022F7A9629EAC71DDAACD65262147F559EA998C58B23499150EC0AC9` |
| `src/OmniCore.Host/DelegationExecution.cs` | `815B91DD301597DC266114B0550D8B3EB94986F2B6D15E75F8DD054F6561E6FD` |
| `src/OmniCore.Host/ExplorerTurn.cs` | `317C268B31ECA50B2C637A7D86302C999F92F562561440FC93ACC66F67D404F0` |
| `src/OmniCore.Host/HostTools.cs` | `51F6B661B17A371B73A9F61AD7247B20637BB24E7B12FBE79158558621CE0778` |
| `src/OmniCore.Host/OmniCliRuntime.cs` | `A42A2C5806F792AA34D85E5990EAAD3B6B4AB5BD6CB7735135048EEE72266886` |
| `src/OmniCore.Host/OmniServer.NativeCommands.cs` | `DCD5586A6AF76F974741CDC8690245609CF672268A56F0FB88E66EF5606DA365` |
| `src/OmniCore.Host/OmniServer.cs` | `2317347DE263DD932C2F378C521D5D6D9AE34BF3D8EC8DC689B2F3833CCCDEC9` |
| `src/OmniCore.Host/PersistentProcessSessionTools.cs` | `244DCEE592AAE208A2618F7484AB5B77691F3285120D342BBEC5F25B4CABB07A` |
| `src/OmniCore.Host/ScriptedToolExecutor.cs` | `E43D11CF4039E253BF7F6CD2A7C8248B3C5B2EBEE1215433F231FC9D23D4224D` |
| `src/OmniCore.Host/StdioProtocolHost.cs` | `5997D27D869E8897E2E7D409D4CB2B8D9B9C6D1CFF76442D52994D45701B635B` |
| `tests/OmniCore.Tests/StdioTransportTests.NativeCommandHandoff.cs` | `238622225D13B5AB5876DB0EFACEA953EF05CD50750CA8EDAC8EA24367D8BAA8` |
| `tests/OmniCore.Tests/StdioTransportTests.NativeChildProcessControls.cs` | `C4D3EE57AD2DFB0F5F2591F37A4A36261F9E1395458907B61C011829398471CE` |
| `tests/OmniCore.SandboxTestProcess/StandardInputProbe.cs` | `93ED902CB67F30653CFEE7FCA135B69C490EEF0F646A50EA5D5DE205A1A063B6` |
| `src/OmniCore.Engine/bin/Debug/net10.0/OmniCore.Engine.dll` | `0F73647A432B82E75F8F198A696E4F4FFB20D7CAFD7A19473C2667F47EBFB181` |
| `src/OmniCore.Host/bin/Debug/net10.0/OmniCore.Host.dll` | `68D76F3F74FF9E4ACF2FC0B41406A03A3808F0F8C41FFB810D6D38D4019F0804` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll` | `C2C3553962AE708854F6283BE25CC0B411022DBB4C85E2C898E034E1F14A5BE2` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.SandboxTestProcess.exe` | `73D24BF77D664E2675E85170F50609E728674EB4A97C4AC2149347E4CBA7D2DE` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.SandboxTestProcess.dll` (helper realmente copiado junto a los tests) | `B2A871576D801D1F2E3746EF6F2ABE5AC916A9CB4F7C4E339FF36CD8D7B1067D` |

## Límites del corte

La verificación es local y focal; no se corrió la suite completa ni CI. No hubo cambios de OmniCoder, registro, instalación, perfiles de usuario, publicación, GUI, push ni merge. El checkpoint Git local conserva el incremento para revisión. No se probó admisión de la delegación desde stdio end-to-end ni se amplió Verify. El foco ya compilado se reproduce, sin restaurar ni construir, con:

```powershell
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.StdioTransportTests.Accepted_generic_process_profile_child_is_controlled_inside_its_open_turn_without_an_extra_provider_request"
```

Luna bajo ejecutó las regresiones afectadas de ACK, proceso, permiso y worktree en la primera fase; los resultados constan abajo. Verify mantiene su catálogo y ejecución one-shot. Los hashes anteriores fijan los binarios del foco final; los cambios posteriores de fuentes requieren validación pertinente. Las actualizaciones documentales de coordinación no cambian esos binarios.

## Revisión Luna bajo y corrección del replay puro

La revisión baja ejecutó una vez el foco integral y las regresiones ACK, proceso, permiso y worktree (6 casos únicos en total), todos verdes; el corte quedó **NO APROBADO** por una carencia que esos casos no cubrían. `ValidNativeProcessControlPermissionResume` podía contar la resolución de otra interacción del mismo scope y su conjunto `pending` permitía que expirara la interacción objetivo. Host sí correlacionaba el InteractionId operativo exacto; el hallazgo concierne la aceptación pura de replay/proyección, que no acredita por sí sola `DelegationAccepted` ni `AgentExecutionStarted`.

La corrección exige que el `InteractionResolved` utilizado como consentimiento tenga `resolved.InteractionId == resumed.InteractionId`. El foco integral incluye dos historiales negativos: resolución de otra interacción bajo el mismo scope, y expiración del target seguida de request/resolución de otra interacción bajo ese scope. Ambos se deben rechazar por `RunProjection.Replay` y `CanonicalStateTracker.Replay`.

Los seis casos previos no se presentan como aprobación del predicado corregido. Luna bajo inspeccionó la corrección y ejecutó el caso integral actualizado una sola vez, sin build/restore: **1 PASS, 0 fallos, 0 omisiones, 9.027 s**. Los dos historiales cruzados se rechazan por ambos validadores. Los hashes de Tests, Engine, Host y helper coincidieron antes y después; `x` permanece intacto y no quedaron procesos propios de prueba activos.

La fase final queda **APROBADA** para el defecto revisado y coordinación acepta el incremento Core en su alcance local. El foco repetido no es un séptimo caso único ni una nueva suite completa. El informe local no versionado `artifacts/m9-child-process-controls/low-validation-20261023.md` conserva la primera fase NO APROBADA, sus seis casos y la revalidación final; el log final es `artifacts/m9-child-process-controls/low-replay-fix-20261023.log`. M9 global, M8 manual, la integración Electron y las aceptaciones no ejecutadas conservan sus límites.
