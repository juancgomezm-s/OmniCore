# SEC-CONV-02: grants y dispatch entre Sessions

**Estado:** contribución Core construida y aceptada localmente por Luna bajo sobre la
base `2325006cf8fa361b2f52d8005bf8b116f048287d`: seis métodos/siete casos aprobados.
Se conserva como checkpoint local, sin merge, publicación, CI remota ni instalación.
No cierra SEC-CONV-02 global, SEC-CONV-13/14, GUI/Electron ni M9/M8 manual.

## Contrato y recorrido

La prueba integral
`StdioTransportTests.Native_permission_grants_respect_run_scope_share_workspace_scope_and_session_ceiling_in_dispatch`
usa un Host como proceso OS separado, stdio con acciones firmadas, SQLite, provider HTTP
fixture, `ToolRuntime` y `filesystem.patch` sobre un archivo en workspace temporal propio.
Una sola instancia del Host selecciona A → B → A.

- A usa la instantánea `FullAgent`. A1 obtiene `Ask` con la `ClaimsKey` observada desde el
  `ToolCallRequested` real, elige `allow_run`, y el patch tiene efecto. El Run vivo tras
  `TurnCompleted` se termina por el comando nativo firmado `run.cancel`; el journal contiene
  `RunCancelled` y el receipt del comando está `Completed`.
- Se cambia el default a `PatchOnly` y `conversation.new` crea B en el mismo `WorkspaceId`.
  B conserva la categoría `PatchOnly`. Su llamada con las mismas claims sigue en `Ask` pese
  al grant Run de A1 y no tiene `ToolCallStarted`. B concede `allow_workspace`; el grant
  persistente del store contiene el mismo `ClaimsKey`, `WorkspaceId`, lifetime `Workspace`
  y `Run=null`; el patch autorizado tiene efecto. B1 termina mediante `run.cancel` firmado,
  con `RunCancelled` y receipt `Completed`.
- B2 usa un Run nuevo en B. La evaluación observa `Allow` y `AppliedGrant` igual al ID del
  Workspace grant. El patch intenta superar el techo de 200 líneas, registra
  `ToolCallFailed(LimitExceeded)` con causa de 200 líneas y deja el archivo intacto. Se espera
  el receipt de ejecución de `conversation.start` y se exige un evento terminal del Run antes
  de cambiar Session; el test no fuerza cancelación de ese Run terminal.
- Al reanudar A en el mismo Host, A2 tiene un `RunId` distinto de A1, conserva la instantánea
  `FullAgent`, observa el mismo `WorkspaceGrantId` aplicado con la misma `ClaimsKey` y produce
  el estado `state-3`. El test espera `TurnCompleted` y un receipt de ejecución con estado
  informado. No afirma que el Run A2 sea terminal: la aserción del test se limita a esos dos
  hitos, al grant y al efecto.

`PermissionEvaluated.AppliedGrant` era un campo del evento, pero `ToolRuntime` lo rellenaba
siempre con `null`. Ahora persiste `decision.AppliedGrant`: B2/A2 comprueban el ID exacto;
las solicitudes `Ask` de A1/B1 y las evaluaciones `allow_once`/`deny` siguen sin ID.
El regresor de permiso Once verifica que todas las evaluaciones de esa ToolCall guardan
`AppliedGrant=null`.
Ese ID explica la decisión de permiso; no acredita efecto exitoso ni sustituye
los límites de capacidad/mutación posteriores, como demuestra el rechazo de B2.

El fixture de este recorrido opera en `ToolMode.Direct`. B declara `PatchOnly`, cuyo contrato
`allowToolDiscovery=false`; B1 comprueba que no se solicitó `tool.search`. Este corte no
ejecuta un positivo de tool discovery bajo PatchOnly.

## Correcciones de evidencia de lectura y pruebas

`ScriptedToolExecutor.RestoreVisibleReadCoverage` compara tipos de evento mediante
`EventType.Equals` (el operador `==` no comparaba el valor), y reconstruye cobertura solo con
los eventos canónicos filtrados por ToolCallId, Run/Lane, path, token y rango. El parser acepta
los aliases camelCase/PascalCase conocidos y rechaza casing ambiguo o duplicado. El comentario
de `FileReadRegistry` aclara que la cobertura durable válida es la de la misma Session/Run/Lane
según M9-L-MUT-01; no cambia la semántica de permisos.

El test integral observa los ToolCallRequested reales, calcula `ClaimsKey` con
`FilesystemPatchTool.Prepare` y exige que A1, B1, B2 y A2 coincidan. El caso de más de 200 líneas
prueba el límite PatchOnly y no se etiqueta como denegación del grant.

## Descubrimiento: diagnóstico histórico no resuelto

Un intento previo preservado en `artifacts/m9-sec-conv02-dispatch/integral-debug-causal.log`
registró el error genérico `Cannot resume Turn: discovered tool plan failed integrity or
eligibility validation` con receipt `Completed/Accepted/failed/runtime_failed`. El fixture
temporal eliminó su journal/snapshot al salir y el log no conserva el contenido del plan ni el
branch interno que lo rechazó. Se inspeccionó `TryRecoverDiscoveredToolPlan`; su error genérico
cubre comprobaciones de integridad, elegibilidad, esquemas y fingerprint, por lo que no se
puede adjudicar una causa concreta a partir de ese log.

La variante de diagnóstico válida con PatchOnly continuó en Direct y pasó sin `tool.search`,
por lo que no llegó a `TryRecoverDiscoveredToolPlan` en B y no resuelve ese fallo histórico.
La diferencia de política es material: PatchOnly declara `ToolMode.Direct` y
`allowToolDiscovery=false`. No se relajó ese contrato ni se afirma soporte de discovery en B.
La causa exacta del error genérico previo queda como pendiente técnico independiente; la ruta
Direct no se presenta como solución de discovery. Logs temporales/diagnósticos quedan en
`artifacts/m9-sec-conv02-dispatch/` y no se suman al conteo final.

El corte posterior [recuperación Discovered con ScopedCoder](m9-sec-conv02-discovered-20261024.md)
añade 2/2 casos aprobados independientemente bajo una política que permite discovery,
con journal/CAS y binarios preservados. No sustituye el snapshot histórico perdido
ni determina su guarda de rechazo; tampoco amplía el contrato PatchOnly.

## Build y campaña focal sobre la candidata congelada

Build:

```text
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -f net10.0 --no-restore
Compilación correcta; 0 warnings, 0 errores.
```

Runner xUnit v3 directo contra `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll`;
cada método seleccionó casos distintos de cero:

| Método | Casos | PASS | FAIL | SKIP | Log |
| --- | ---: | ---: | ---: | ---: | --- |
| `StdioTransportTests.Native_permission_grants_respect_run_scope_share_workspace_scope_and_session_ceiling_in_dispatch` | 1 | 1 | 0 | 0 | `integral-direct-final-freeze.log` |
| `PriorReadEnforcementTests.Durable_read_coverage_from_another_lane_does_not_authorize_patch` | 1 | 1 | 0 | 0 | `lane-negative-final-freeze.log` |
| `PriorReadEnforcementTests.Patch_without_prior_read_is_rejected_and_file_intact` | 1 | 1 | 0 | 0 | `prior-read-absent-final-freeze.log` |
| `PriorReadEnforcementTests.Partial_read_does_not_authorize_direct_patch_outside_emitted_range` | 1 | 1 | 0 | 0 | `prior-read-partial-final-freeze.log` |
| `PriorReadEnforcementTests.Read_then_external_modification_still_rejects_stale_token` | 1 | 1 | 0 | 0 | `prior-read-stale-final-freeze.log` |
| `StdioTransportTests.Native_permission_choice_resumes_exact_turn_with_one_effect_and_receipt` (theory: `allow_once`, `deny`) | 2 | 2 | 0 | 0 | `permission-once-final-freeze.log` |
| **Total** | **7** | **7** | **0** | **0** | |

Build y pruebas se ejecutaron sobre la misma candidata net10. No se compiló net8: los cambios
no tocan `Client`, `Protocol`, `Sandbox` ni otros proyectos multi-target del contrato ADR-0038.

## Validación independiente final

Luna bajo revisó el diff y ejecutó secuencialmente los seis métodos de la tabla
contra la DLL congelada, sin recompilar: **7 PASS, 0 FAIL, 0 SKIP, 0 Not Run**.
La teoría Once/deny seleccionó dos casos y los otros cinco métodos uno cada uno.
La repetición de los siete casos del constructor no se suma como catorce casos
únicos. Dictamen: **APROBADO en el alcance focal Direct de esta acta**.

El positivo usa JSON PascalCase real del productor. Los aliases camelCase,
duplicados ambiguos y tipos JSON inválidos se revisaron estáticamente: no hay
una matriz dinámica adicional de payloads malformados. El negativo de otra Lane
ejercita el executor real; absent/partial/stale conservan las comprobaciones del
requisito de lectura. No se declara una prueba OS de todas las combinaciones de
Session/Run/Lane ni de replay después de reiniciar el proceso.

Se verificaron antes/después las siete fuentes/documentos del manifiesto y sus
73 binarios inventariados. Es un inventario del directorio de ejecución, no una
afirmación de que los 73 se cargaron dinámicamente. El Host realmente lanzado es
`dotnet tests/OmniCore.Tests/bin/Debug/net10.0/omni.dll host --stdio`.
No quedó un Host propio activo y los nodos MSBuild ajenos se conservaron.

Informe: `artifacts/m9-sec-conv02-dispatch/low-validation-20261024.md`, SHA-256
`F115F5ACF50AD6B952E08E7CA671C9085B56EA1150F374F2B58D1A9EA62513B3`.
Logs independientes: `low-final-01.log` a `low-final-06.log`.

## Freeze

El manifiesto SHA-256 de fuentes, inventario de binarios y la imagen del Host está en
`artifacts/m9-sec-conv02-dispatch/freeze-manifest.json`. `NativeChoiceFixture.StartHost` usa
`AppContext.BaseDirectory/omni.dll`, es decir,
`tests/OmniCore.Tests/bin/Debug/net10.0/omni.dll`; el manifiesto incluye esa copia, no solo
`src/OmniCore.Cli/bin`.

Manifiesto original: SHA-256
`790028F73AFF61C6401D2B664A228C7ECB4A7867C7C823A1870B7B73D03E6FA4`.
Esta acta y los documentos generales se actualizaron editorialmente después del
dictamen. Las seis fuentes de código/pruebas y los binarios siguen iguales a los
validados; la tabla siguiente conserva en Git su identidad. No hubo push, merge,
CI remota, publicación, instalación, cambio global de configuración ni cambios
en OmniCoder. El archivo ajeno `x` permaneció sin editar.

## Fuentes y ensamblados propios identificados

Se listan las fuentes modificadas y los ensamblados propios del output congelado,
incluido el runner. La presencia en este inventario no implica que cada ensamblado
se haya ejecutado; el punto de entrada del Host es
`tests/OmniCore.Tests/bin/Debug/net10.0/omni.dll`.

| Archivo | SHA-256 |
|---|---|
| `src/OmniCore.Tools/ToolRuntime.cs` | `8666045853203AB44B534374DF5525DA580CF8DFAFBF036439F9D72AAD2FF6F7` |
| `src/OmniCore.Host/ScriptedToolExecutor.cs` | `A7DFDC9C3EBDD2906A3EB81A54C944C197476E6DA07F33C339C89548E293B84E` |
| `src/OmniCore.Abstractions/FileReadRegistry.cs` | `341EBCCF0963473AC0BDAC8D7AFB3C5E7BF81B1207C60388E2154187CF47B901` |
| `tests/OmniCore.Tests/PriorReadEnforcementTests.cs` | `FF59E770983952CC2F0F74835DB4F3D71B188317933761C1F2B521360C5CA108` |
| `tests/OmniCore.Tests/StdioTransportTests.NativePermissionPlanApproval.cs` | `E9B93FA0E27B4EC9DF94A887D52BFD69FDB1B74F693B71592713C7A2FEAD548A` |
| `tests/OmniCore.Tests/StdioTransportTests.NativeGrantDispatch.cs` | `00B7A83E070BC0CAC285BB4976268C98168CEEFB6549154255A4CB46383BD050` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/omni.dll` | `3596A843C10B08E6D98F2CF2597B624323D57276BE05E9F00E58957BC4C830F2` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Abstractions.dll` | `9CDAFAD15F5DBC034C00A72F74AC14315C6AF69F89D124D6310865006FCEA174` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Client.dll` | `DCD454FD8C501A8BEAC91D6FAEA3ABB1DFC0764DF0C7592D47B049C9A56FB9FE` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Context.dll` | `BF623D8453BE30E6A0E758317EA994CDEC6E97C7218E0C9F9711FC4B5F7D7432` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Domain.dll` | `8D91FF8C560D454D28FE4408CCD81F5108E0BEF03FE2E4B6E5462277A5D4990C` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Engine.dll` | `096233E5A420CE47524513E9CB12FB8995E57AF8E5DCEB68B643308D4AF25289` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Execution.dll` | `898692EFF468C25CBDD52B248B8F4F7F1173C65AEDC69BE9A60187A79557E29E` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Extensions.dll` | `939A7D6518AE87CC157667C8BA5FC8395CA2DAF313A2E19391E2A081C9B48778` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Host.dll` | `46F748D28D9A97805FA3B1EFB919F92B588ED5E3B3B3565628CCD3A84D093ADF` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Infrastructure.dll` | `18DA72ED86BFD3360ECACB1D664E9E23E06630B87B755AED8017F2C3C8C716E6` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Memory.dll` | `038E767B15035CEBA951257F7864774BB98FD81A2AF5E149950F9B0312AE24D1` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Models.dll` | `3B5B04A1D3FBFD4FDD5BFAA5CE5ACA520BB7335722F5EEA955145816B0E05655` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Protocol.dll` | `2820F71A068BB5B463B91AA5EF84597E179D4F1C9A7143EEFA1CE014CB9AB4A5` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Qualification.dll` | `056E9060A266D75CF2E5D41C981A97853CCE66F011105AD4D9369920F9BC580B` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Sandbox.dll` | `375BB655105310C2F52163EBB2BADA0E3D97B2208894BCC578C26342ACD7D97A` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.SandboxTestProcess.dll` | `21912DC907F75B6A7DF3F96F14FE7B8F3DADDAF5A43E6A771A5A9C61B2E920BC` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Security.dll` | `240CFA36D6FE88049E4B971E69D870692711E5930B42B6EF7A6171822DB14BF4` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll` | `CDB3C3C364BFE068B42CDBE7E49B8326401C123B42A4E712B06D7FD3C9A058EC` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tools.dll` | `FB013B29A69092AB432796A0F69933E29462C8D04885858985138D57209E0E3F` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/omni.dll` | `3596A843C10B08E6D98F2CF2597B624323D57276BE05E9F00E58957BC4C830F2` |
