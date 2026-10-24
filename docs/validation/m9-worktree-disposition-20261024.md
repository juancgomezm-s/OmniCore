# M9-L: limpieza y descarte nativos de worktrees

Fecha: 2026-10-24

Worktree: `C:/Users/juanc/.codex/worktrees/m9-completion/OmniCore`

Rama/base: `codex/m9-completion-20261022`, desde `b9be5d0e6a35cdb2c1bb24419585af3bb7ce1ebc`
Estado: bloque aceptado localmente tras corregir la composición del catálogo y completar la revisión independiente. Sin merge, push, CI, publicación ni instalación nuevos. M9 global y M8 manual siguen abiertos.

## Comportamiento construido

Añade los comandos humanos nativos `core:worktree.cleanup` y `core:worktree.discard`, ligados a `worktree.cleanup` y `worktree.discard`. Reciben el identificador público de Lane y su HEAD/árbol revisados, disponibles en `worktree.preview`. El Host deriva ownership y rutas desde el estado canónico de la Session; el cliente no entrega rutas de archivos.

El recorrido reutiliza autorización de herramientas, fronteras de capacidades, lease, sobre protegido de reconciliación, recibos durables y deduplicación. La procedencia nativa no amplía permisos de modelo, Session, usuario o perfil. Cleanup permite retirar la Lane terminal limpia y verifica de nuevo la captura revisada y los archivos ignorados antes de la eliminación. Discard archiva el árbol exacto revisado antes de retirar el checkout. La retención de referencias existente conserva sus siete días; no se añade barrido nativo ni undo.

La operación conserva el Run y no usa inferencia para pedir/aplicar permisos. Reutiliza el Turn abierto o abre el Turn canónico de acción nativa que ya usa el runtime cuando el Run está quiescente; la reanudación de Ask conserva ese Turn y ToolCall, sin Turn paralelo. Ask usa la interacción nativa existente si el cliente negoció esa capacidad; en caso contrario conserva la negativa `NoClient` y no hay efecto. La aprobación y su huella quedan ligadas a operación, workspace, Lane, ownership y commit/árbol revisados. Una edición posterior invalida la aprobación; un grant no se reutiliza para otro ownership o árbol. El perfil debe cubrir recursivamente la escritura del workspace, por ejemplo `**`; patrones limitados como `README.md`, `.` o `*` no autorizan esta disposición de todo el árbol.

La Task y Lane objetivo deben estar terminadas, sin consumidores ni Verify/procesos activos. El Run quiescente puede seguir abierto. La limpieza de una Lane integrada conserva las comprobaciones de integración completa existentes; no se sustituye por un descarte implícito.

## Verificación del constructor

Compilación final: cero advertencias y errores, 6,00 s.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
```

Siete métodos del runner binario xUnit v3: diez casos correctos. La teoría nativa tiene cuatro filas (49,310 s); los seis focos restantes tienen un caso cada uno. El constructor conservó la salida directa del runner, sin guardar logs; no se atribuyen archivos de evidencia inexistentes.

```powershell
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.StdioTransportTests.Native_disposition_requires_terminal_lane_permission_and_cleans_only_the_reviewed_tree"
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.GitWorktreeStoreTests.Disposition_snapshot_check_is_read_only_and_rejects_a_changed_lane"
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.GitWorktreeStoreTests.Explicit_cleanup_rechecks_the_reviewed_clean_tree_at_the_removal_fence"
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.GitWorktreeStoreTests.Explicit_discard_rechecks_ignored_outputs_at_the_removal_fence"
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.UserPermissionsTests.Exact_worktree_disposition_claim_uses_workspace_write_policy_and_still_obeys_user_deny"
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.EffectiveAgentProfileTests.Worktree_disposition_requires_profile_coverage_of_the_entire_workspace"
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.PermissionGrantsTests.Worktree_disposition_grant_is_bound_to_exact_ownership_and_reviewed_tree"
```

Las cuatro filas cubren cleanup correcto, edición durante Ask, cliente sin capacidad de respuesta y discard con cambios/untracked. La acción firmada rechaza una Lane activa sin iniciar herramientas ni efectos. Sin capacidad, Ask termina como `NoClient`/Deny antes de Started. Para discard, el cliente toma las coordenadas del query público y la referencia archivada contiene los bytes editados. Tras reabrir el Host, el mismo InvocationId/hash conserva recibo y secuencia, con un Started y un Succeeded; otro InvocationId contra el ownership retirado se rechaza sin efectos nuevos. La edición durante Ask conserva el checkout y se rechaza antes del efecto mediante `PermissionDenied`.

La fixture usa Host/stdio y Git reales, con binding y terminales de Lane sembrados en el journal. No acredita aquí la provisión de una delegación completa por stdio. Todos los retiros afectan repositorios temporales aislados; no se retiró ni archivó ningún worktree real del usuario. No hubo suite general ni repetición por el constructor de las campañas verdes anteriores.

## Diagnóstico y límites

- El primer recorrido no completó dentro del límite: repetir previews de integración generaba centenares de comandos Git. Se separó la validación canónica de la captura exacta de la Lane, eliminando el merge repetido de la disposición. No se aumentó el timeout para ocultarlo. Las capturas históricas son diagnóstico, no aceptación.
- `GitWorktreeStore` vuelve a verificar identidad física, HEAD/árbol, estado detached y archivos ignorados antes de retirar. Discard archiva primero. Un efecto `Unknown` no se reintenta automáticamente; la ausencia del directorio no demuestra éxito. No se acredita recuperación tras caída a mitad del efecto con estos casos de reapertura posterior al éxito.
- Las comprobaciones Git no hacen atómicas las escrituras de un editor externo con la eliminación. El lease coordina escritores de OmniCore; no excluye procesos OS ajenos.
- La regla de perfil es conservadora: disponer el árbol completo requiere cobertura recursiva. No se amplían perfiles ni autoridad delegada.
- No hay nuevo barrido de retención, undo, persistencia Weak ni importador Native/Imported. OmniCoder permanece en solo lectura y su consumidor corresponde a su coordinación.

## Revisión independiente

La primera revisión quedó **no aprobada**: root detectó y Luna bajo confirmó un `else` del fallback de procesos asociado al nuevo coordinador en `HostTools`. La teoría nativa pasó cuatro casos en ese candidato, pero el defecto impedía aceptarlo. Ese resultado histórico no se suma a los casos finales.

Luna alto corrigió únicamente la composición y congeló los binarios de nuevo. Luna bajo revisó el diff y ejecutó diez métodos sobre el candidato corregido: **13/13 casos correctos, cero errores, fallos, omisiones o casos sin ejecutar**, sin build ni restore. Incluye los diez casos del bloque y tres regresiones pertinentes: integración nativa previa, controles de procesos raíz y controles de hijos genéricos. Los veinte hashes finales coincidieron antes y después; no quedaron procesos propios huérfanos. La repetición del caso de hijos está justificada por la regresión del catálogo descubierta en esta entrega.

El informe local conserva ambas fases y los logs de la campaña final: `artifacts/m9-worktree-disposition/low-validation-20261024.md`, SHA-256 `82F365B7E34C5844CF633552B525AB3252C182E4A28CEAB441212C191A03C467`. No se versionan esos logs. Root verificó también las 49 rutas únicas de ambos manifiestos, tomando el último hash cuando hubo corrección: todas coinciden. Esta aceptación no sustituye CI ni aceptación del consumidor.

## Manifiesto del candidato

Los hashes identifican el candidato inicial entregado a revisión, después de la compilación final del constructor. Cualquier corrección posterior tendrá manifiesto y revalidación propios. `x` es un archivo ajeno preservado sin cambios (SHA-256 `84E292A1C73B431B64FBC2C8F0FA8B08CC771E58B98F05FB968C842CBBF6FE45`).

<!-- M9-L FREEZE MANIFEST -->

Binarios probados, SHA-256:

```text
01116B153AB5D171AC00DD896E8C165FFDED45580822FDF9507EA9CCD76589A8  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
B3B6E6A18A8F8036B2E6DB223B6F6C8747914A9EDCA02DCAFBC5BB9BB2341555  src/OmniCore.Host/bin/Debug/net10.0/OmniCore.Host.dll
```

Fuentes y pruebas cambiadas, SHA-256:

```text
ACB3770942CD8D2480A000FE4B83A6E4C1C1AF00CBEAF8269A780F1C0696986D  src/OmniCore.Abstractions/ModelCapabilityBoundary.cs
6AAF627FFFE85D7DECB2701075170E18300C722D720F376F97523F4BE3E50BAB  src/OmniCore.Abstractions/Tools.cs
436DEFFB2E0990D49F1020EF2EB50B3A3C5ACEA3C9966ED8955800CD4BA6EEFB  src/OmniCore.Domain/GitWorktreeContracts.cs
46B98F0A7219B8C9AB951660BEB277725353EF7FFAA6743AEB33E4A248C53B42  src/OmniCore.Domain/Permissions.cs
5B3E77E165FA78B37F31DD9DC3C68B675169EDF2845AC1A72D1E6A85715DEF66  src/OmniCore.Engine/Projections.cs
F215282190A44F159CED0130DC597354A27178C6AE02F33B01C2A5CE638D90B1  src/OmniCore.Host/ExplorerTurn.cs
EB98EE476FBEF5604772CA659762AE974CF828D7C6DED0AFC8DB44CDCA2D44F2  src/OmniCore.Host/ExtensionRpcProcessInvoker.cs
04EF58332028EB94925D8E79E4E01447D234A4C7CBB8F251BD334B953DACABAA  src/OmniCore.Host/HostTools.cs
B6D09B830F1E574B38D9087E85F7572983247DB341C7935C32CB5AF55C02AD1D  src/OmniCore.Host/OmniCliRuntime.cs
B504B087E8CDC40DB06D493EFB6546F7211E6A157BED94C26A7CB41176B1BFEE  src/OmniCore.Host/OmniHost.cs
A6DA51E5F72019E3321A034FA9A2B4F47A49C6699C0ACCBAF5EBF76858A5C3D8  src/OmniCore.Host/OmniServer.NativeCommands.cs
62F57C022EA98723C53EA88AAD9A42E383D749D95A5EF46D430F4186D315FC24  src/OmniCore.Host/OmniServer.WorkspacePanel.cs
9531A142950D793A7E29FFE6EA4880701CBF6064B7B2B3B4D9869E3FA38F1981  src/OmniCore.Host/PersistentProcessSessionTools.cs
6736DC7E190697317B788E1480923A9A322978B2CD95FA1B498A406DB27D242A  src/OmniCore.Host/ScriptedToolExecutor.cs
AFDEB4EA886FCC9CFFDC791B63682F692D4696C53992EAB95939AC5D03F6E39B  src/OmniCore.Host/StdioProtocolHost.cs
CFB893D6B68252C1B7E0A8BB3C5F44DE7D8DB8B63B816C8D876E5B30A13A4449  src/OmniCore.Host/WorktreeCleanupHost.cs
0DEC6E5FE66A88D92B2E03D8F49A824145F67F2E91B7190F68E6926656ED0108  src/OmniCore.Host/WorktreeDispositionHostCoordinator.cs
3116EAD6C64234C1655C4636B9CC10E2AEF09D27221DF6DA417FB3F9E61E477B  src/OmniCore.Infrastructure/GitWorktreeStore.cs
BFE85F903A9E35A03EBFFC01CB70A84B30896C6102E04F97493ECB2B01BF85B1  src/OmniCore.Protocol/StdioProtocol.cs
92F1C7EC3213DA3E7A710EADB96F2C0FA9C77F0C4AE2A795484B143D003A6A50  src/OmniCore.Security/AgentProfilePermissionPolicy.cs
CE161150BEE3310884A73CAD0AF68B17BC4FED95BA4D8E54DB8F3F2460CCBCBE  src/OmniCore.Security/FilePermissionGrantStore.cs
B32610C84C5DBE422D48072B29AA3D83A1278C91181895B21E07572213693585  src/OmniCore.Security/ScriptedPermissionPolicy.cs
F71522748795080C9FC511F7AD9F8AF38D9990E98BD20AE4A6573A44971191B6  src/OmniCore.Tools/ToolRuntime.cs
95A400EDF1F4645801FEFACC45FFA04AA2B9F8AFA1AAF75F3DA560FA1620684E  src/OmniCore.Tools/WorktreeDispositionTool.cs
AF9AAFD489D0BD70A8009CC770394A127724BACD939B32326288883B85C96D26  tests/OmniCore.Tests/EffectiveAgentProfileTests.cs
A4A25EB8E51F1D14B3F5465B6FB6267641DE2232B16CEB86065B26909EE93739  tests/OmniCore.Tests/GitWorktreeStoreTests.cs
CF917E8141D15ACB8E842FA459F0C04907FE71846EEBED7CF0548664C733A58D  tests/OmniCore.Tests/PermissionGrantsTests.cs
72063C9751FD9B529ED218DE7B87D73A01A5819712C6BE21CB355E1725CA0C45  tests/OmniCore.Tests/StdioTransportTests.NativeCommandHandoff.cs
9BE9A7EA91604371F8DA4280BA221F965960F62D9EAAB4403525BB212C268429  tests/OmniCore.Tests/StdioTransportTests.NativeWorktreeLifecycle.cs
26E4FB5B01DE249ABA87F921216D8E0EBACADC97829487D71A3D767432FA1690  tests/OmniCore.Tests/UserPermissionsTests.cs
```

## Corrección posterior a la revisión Low — 2026-10-24

La revisión Low detectó que el registro de `WorktreeDispositionTool` había interpuesto su `if/else` entre la rama de `processSessionManager` y las herramientas legacy one-shot. Se corrigió la composición mínima: el `else` vuelve a pertenecer a `if (processSessionManager is not null)` dentro de `includeProcessTools`, y el registro opcional de cleanup/discard ocurre después como rama independiente. No cambia seguridad ni comportamiento de disposición.

Validación final del candidato corregido:

| Comprobación | Resultado |
| --- | ---: |
| `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore` | Correcto, 0 advertencias/errores, 49.17 s |
| `Accepted_generic_process_profile_child_is_controlled_inside_its_open_turn_without_an_extra_provider_request` | 1 caso, 0 errores/fallos/omitidos, 9.198 s |

Esa integración ejecuta la ruta de proceso persistente recién aceptado en su Turn abierto sin pedir otra solicitud al provider. No se repitieron los otros diez casos focales: no atraviesan la composición corregida. El binario anterior a esta corrección tuvo la teoría nativa 4/4; el intento incompleto del matcher quedó registrado en el informe histórico de Low y no se cuenta como resultado. No se ejecutó suite general.

Logs de la corrección (locales, fuera de Git): `artifacts/m9-worktree-disposition/low-correction-build-20261024.log` y `artifacts/m9-worktree-disposition/low-correction-generic-process-20261024.log`. No hubo otros logs persistidos.

Manifiesto posterior a la corrección (solo la fuente modificada en esta corrección, binarios probados y dependencias `OmniCore.*` del directorio del runner):

```text
51656FA1DDCC957117DF4F60BC9BA1F22C75655A5806D830475CC6C8559E9C60  src/OmniCore.Host/HostTools.cs
6DEFF3A30FD5499D40F94520955145E4FAAAD203C1E7D21D5DD373CC0F9F1D0A  src/OmniCore.Host/bin/Debug/net10.0/OmniCore.Host.dll
6DEFF3A30FD5499D40F94520955145E4FAAAD203C1E7D21D5DD373CC0F9F1D0A  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Host.dll
D49AC5130918DA6CCC993A068647510468F58EC0E014460C28F0898093896046  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
D36ABBF8EB375FE2DFB399D083F4C1918168B47AE0A9E2810F17E02DCEA14CE4  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Abstractions.dll
3809193D51CFC110953116898368A8751FB068F7396CAFD72B9F0D0959D14121  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Client.dll
EBC24509991B183B85F86457C12090F529E40141D7CF49A674EF386B99620445  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Context.dll
462D1D38A5B852CD3D479C521FE9648E4760175881693FE096ABA4B1F1777F70  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Domain.dll
86160CD2AB7086CFAF6B8BA057D43E441563ACFC62F7361B1435D4FDCAC3860D  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Engine.dll
CEB47A2B8466289E3EB539C1AB1DD2C2624CFD349F30B4A5CE32FEAAA6DDBB02  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Execution.dll
7B8755837497026F1F8FB829C7104EA4D24B6785E26FC43CAF32B6F1BF153A2F  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Extensions.dll
6D5888EA285C509C80DFF8E54083010DBBFC608425EC219C3AEFE26212CB4628  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Infrastructure.dll
3D3E82F31535B2D122CA1A12A411A2E166E2AE68A00D0D24963ABFAB46BE735D  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Memory.dll
14A3916DDDCCD18D529374608BC5F9A774FC610F504C0E328FAE283DFD55E122  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Models.dll
73965AE3C27CF8A9522EC3CCC4D65F4DE658F693B354E0A84B29385B7F461D0A  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Protocol.dll
599D5D18FDBBE7E74AF180D52F1FE9BE295B4D31B7473DED3437E72C8164E315  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Qualification.dll
57C173CB89CA0427156677E81ABAB9EE1FB846B64BCBE3FF55F2440A78EAA8B6  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Sandbox.dll
BCCC06063873FF95DD82788A8282331D60F633B713B70E4E24A3EABE4A281638  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.SandboxTestProcess.dll
FF363CBDEB2EE73972EBC2544B297FF001E4F04CE143EEC5678FE903B82CB6D8  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Security.dll
26E1327BD3A307E09584510404396FBBD7B0C17B4A5182861893A709A2C43D71  tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tools.dll
```

`x` sigue con SHA-256 `84E292A1C73B431B64FBC2C8F0FA8B08CC771E58B98F05FB968C842CBBF6FE45`. La corrección queda congelada sin commit ni publicación.
