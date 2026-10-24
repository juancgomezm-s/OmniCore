# Acta M9-L: retención nativa de refs privadas

Fecha: 2026-10-24

Base: `8ded1f2953d659a61696a39c9d42c365aa941fe4`

Rama: `codex/m9-completion-20261022`

Estado: aceptado localmente tras revisión independiente sobre los binarios congelados. Sin nueva CI ni publicación. OmniCoder permanece en solo lectura; M9 global y M8 manual siguen abiertos.

## Resultado

Quedó implementado el flujo nativo para revisar y podar refs Git privadas de snapshots vencidas que pertenecen a un Run terminado. Un Run actual aporta la autoridad y los permisos vigentes; el Run propietario histórico solo prueba la relación canónica con la misma Session, proyecto/workspace, Task, Lane y ejecución. No se recupera su autorización anterior ni se crea autoridad persistente.

El preview nativo es pasivo, bounded y de solo lectura. Deriva los recursos desde la identidad y el journal canónicos, verifica el metadata durable propio y exige coincidencia entre el binding del Lane y los campos del metadata (`SnapshotCommit`, `GitCommonDirectory`, `WorktreePath`). No crea refs, metadata, eventos ni directorios. Si la historia supera 100 000 eventos informa `unavailable/history_limit_exceeded`; no la presenta como ausencia de refs. La lectura del metadata inspeccionado queda limitada a 64 KiB.

La acción exige una selección humana firmada y revisada sobre Session/Run actual, Run propietario terminal, workspace/ownership y el conjunto exacto de refs/OIDs. El contrato intersecta permisos actuales, mutación `Full`, capacidad `DeleteFile`, política destructiva, perfil y `UserPolicy`; el rechazo `deny`, una revisión obsoleta o una pertenencia ajena no producen efectos. La reanudación de `allow_once` reconstruye el coordinator con el `ToolCall` nativo canónico de esa continuación. Repetir el mismo command tras reabrir el Host devuelve el recibo existente sin volver a ejecutar el proveedor ni repetir el efecto.

La poda usa `git update-ref --stdin -z --no-deref` con una transacción normal: `start`, `delete <ref> <expected-old-oid>` para cada ref, `prepare` y `commit`. Git bloquea y valida todas las refs/OIDs antes de efectuar las actualizaciones; los lectores concurrentes aún pueden observar un subconjunto durante el commit. No se usa `--batch-updates`. Una cancelación, error o resultado incierto se registra como `Unknown`, sin retry automático. `--no-deref` impide que una ref privada reemplazada por symref desvíe el borrado hacia una branch ajena. Fuente primaria: [git-update-ref](https://git-scm.com/docs/git-update-ref).

Solo se podan las refs privadas exactas aprobadas que superan la política existente de siete días. Se conservan refs recientes, refs ajenas, refs asociadas a worktrees activos, metadata y directorios. No se borra un workspace ni metadata de usuario.

## Verificación del constructor

Build final: `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore`; compilación correcta, 0 warnings, 0 errors. Los seis focos se ejecutaron con el runner comprobado `dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "<nombre completo>"`; cada método seleccionó 1 caso y pasó 1/1.

| Método | Resultado |
| --- | ---: |
| `StdioTransportTests.Native_retention_uses_current_run_authority_for_expired_refs_of_a_terminal_run` | 1/1 |
| `StdioTransportTests.Native_retention_deny_preserves_expired_reference_and_metadata` | 1/1 |
| `GitWorktreeStoreTests.Reviewed_retention_prunes_two_refs_atomically_and_rejects_a_stale_review` | 1/1 |
| `GitWorktreeStoreTests.Retention_preserves_symbolic_private_ref_and_its_foreign_branch_target` | 1/1 |
| `GitWorktreeStoreTests.Snapshot_retention_prunes_only_removed_ownership_refs_after_seven_days` | 1/1 |
| `GitWorktreeStoreTests.Snapshot_retention_preserves_a_removed_ref_when_an_active_worktree_uses_the_same_commit` | 1/1 |

El foco Host/stdio usa un Git real creado dentro del fixture: verifica preview pasivo, Run B actual frente a Run A terminado en la misma Session/workspace, `allow_once`, refs exactas ausentes, metadata/directorio intactos y replay idempotente tras reabrir Host. El test separado de `deny` comprueba `PermissionDenied`, cero `ToolCallStarted`/éxito/efecto incierto y conservación de refs, metadata y directorio. El test de dos refs valida además que un OID stale rechaza el conjunto sin efecto. El negativo de symref preserva tanto el symref privado como el OID de la branch centinela y el OID original de `main`.

Los trece casos aceptados del checkpoint `8ded1f2` no se repitieron ni se suman al conteo de esta acta. No hubo build ni pruebas de OmniCoder.

Logs y evidencia local: `artifacts/m9-worktree-retention/`. Incluyen `build-final.log`, los seis `focus-*.log`, `loaded-omnicore-dlls.txt` y `source-manifest-sha256.txt`. El inventario de assemblies se capturó temporalmente desde la prueba Host/stdio después del efecto; se retiró esa instrumentación y los hashes de DLL corresponden al build final.

## Revisión independiente

Luna bajo revisó el diff y ejecutó secuencialmente diez métodos sobre el candidato
congelado, sin compilar ni restaurar: **13 casos correctos, cero errores, fallos
y omisiones**. Además de los seis focos del constructor, verificó el modo de
stdin exacto, la regresión de EOF y los recorridos previos de disposición
(cuatro filas) e integración nativa (un caso), afectados por el pipeline común.
No se suman como casos nuevos las repeticiones del constructor ni las campañas
históricas de `8ded1f2`.

Los 28 hashes de fuentes y 17 de DLL coincidieron antes y después. El informe
local está en `artifacts/m9-worktree-retention/low-validation-20261024.md`,
SHA-256 `CA98A866428360239A5579D4C9E53BD08165B5090B017EDDB10B496135EAB9AB`;
los diez logs son `low-final-*.log`. No se versionan logs de ejecución.
Root cotejó los mismos manifiestos sin recompilar. La fixture siembra el
binding de la Lane histórica; no acredita aquí la provisión completa de una
delegación por un cliente externo. Reapertura tras éxito no prueba caída
intermedia, inferencia de un proveedor real ni aceptación visual/consumidor.

Entorno local: ZephyrusG14, Windows, Git `2.54.0.windows.1`. El helper de stdin
del directorio del runner tiene SHA-256
`9FC55C57A8A4EC0CDCC153F3F35370AD2F69704B892F791EA8AA7B23C6EE6691`.

## Límites del bloque

- El journal por encima del límite bounded de 100 000 eventos queda como no disponible; no hay paginación en este bloque.
- El metadata propio debe caber en 64 KiB y el contrato nativo admite como máximo dos refs por acción.
- El commit normal de Git valida el conjunto completo antes de escribir, pero no ofrece a los lectores una vista atómica durante el commit ni garantiza el resultado tras caída del proceso. Esos casos permanecen conservadoramente como `Unknown`.
- Esta acta valida solo retención nativa y los focos listados; no declara M9 global ni M8 manual cerrados.

## Freeze local

El archivo local `source-manifest-sha256.txt` tiene SHA-256 bruto
`BB13477A9056229D6968EB09FE79B61DD61CB0421A0A972DF841052D07EA36D1`.
Su representación canónica sin BOM, con LF y sin salto final produce el digest
indicado debajo. Estas son las 28 entradas, preservadas aquí sin logs:

```text
src/OmniCore.Abstractions/ModelCapabilityBoundary.cs=F39F3911C30C5E800A31DCBC4D3E99BD52E6EAE072B00A512B4174A414076EDB
src/OmniCore.Abstractions/ProcessRuntime.cs=2164F48D62676A7B7E94A91C848ED43BC694695D53CB255AE48F522CEE990209
src/OmniCore.Abstractions/Tools.cs=0173F73F06D8A813B3E0DFE9EF896542363036B2BD35483CBD3550808FEBA6D8
src/OmniCore.Domain/GitWorktreeContracts.cs=362A621642D18A48383653528F7500FC11294E6DBD3B471F56C58181947A3E1B
src/OmniCore.Domain/Permissions.cs=D46F34B6569FCC5E9749C9E941F967C6099215587ADD9074AB77D79061B05CD0
src/OmniCore.Engine/Projections.cs=EF89ED0491A24F3B72D1D225E6CD013B5E6C9FD81EDD8E7D61E0113D3FED294F
src/OmniCore.Execution/SystemProcessRuntime.cs=75E53F0804A38FEF77349F87E21C7C72188DE5D874A653900D137F76A17D8F66
src/OmniCore.Host/ExplorerTurn.cs=3C86A12975382E45DE5466BD59F8886BBC244E01B7A2D440362762F6D6CEE71B
src/OmniCore.Host/ExtensionRpcProcessInvoker.cs=57460FC32A555D6024F611C4826E57B9EF49196E4B27EBE41F8D7921A995D619
src/OmniCore.Host/HostTools.cs=9B0A114464C60557E5B61141C802A88AA4111CF60F1B7E95430635EE92897D4C
src/OmniCore.Host/OmniCliRuntime.cs=7048EDBFBF885F4C0B0DC2110CA54382236B9F2F9EBF28DFDA30BD8DE4C933A0
src/OmniCore.Host/OmniHost.cs=3413391401BD7757616C901DD480947C574C03FCCB41DCBF755A5799380EF7D5
src/OmniCore.Host/OmniServer.cs=BB733DC807F6F2D89E5C0F4426F75D9DE033855150B9283FC703C44B537B44C4
src/OmniCore.Host/OmniServer.NativeCommands.cs=E6DE5B6673B47AF145A9D77196CE99235783ECA76BA36B708A8CD0F1B9BA2F1E
src/OmniCore.Host/PersistentProcessSessionTools.cs=83A635F0CE76F0F34FCB2002CCF4AD9B049BFA66540DE3BBC2EEAED35A07A466
src/OmniCore.Host/StdioProtocolHost.cs=739872267CDDB7F1D4E00667051B94FFBB96C917E38CC4FC1C607F3956944585
src/OmniCore.Host/WorktreeRetentionHostCoordinator.cs=593146096FD4F603A22A8DF2DC88C112F5917FBF5C5CE9DB36CFAAB9405775F9
src/OmniCore.Infrastructure/GitWorktreeStore.cs=159E821D98C2627BBA64461FA8CDD6999B5B0059EB2747078B917502643F8AE3
src/OmniCore.Protocol/StdioProtocol.cs=ED7D0930196EC1424E8C7AD43DF25BEE79E06C4F7EE39B4CD004B3AAD56E50E6
src/OmniCore.Security/AgentProfilePermissionPolicy.cs=F7BAE13142F87A6F03D7C13A56761C31AA9EF18D7E317837CB7362B229502FAA
src/OmniCore.Security/FilePermissionGrantStore.cs=59605CB1C91FA62B294B2E27CF72E50D086DBEBBCC10800C9EC140463C42C7C3
src/OmniCore.Security/ScriptedPermissionPolicy.cs=0FA3EFBB00D5FC60A4146F617AEF25DC2DD973BF9174C5EFCC00DEC0E2996FB9
src/OmniCore.Tools/ToolRuntime.cs=CE2C2C01817DBEA1840DEE509323075E427215B40ED346DE497A7043A73D1365
src/OmniCore.Tools/WorktreeRetentionTool.cs=E05C68C811F33EE92F63523A019C08869EE195158DC0F0C8CB5CDC3E7B9AF2A4
tests/OmniCore.Tests/GitWorktreeStoreTests.cs=B1F98B1F326EA05FDB9744B36A403A1F8ED5680933BA351A4D8D82341AC1DF7B
tests/OmniCore.Tests/StdioTransportTests.NativeCommandHandoff.cs=6604FA13FFFD7FD519E371860A619366398C8E5CF83BEDAA170C78AD0DE9A0B9
tests/OmniCore.Tests/StdioTransportTests.NativeWorktreeLifecycle.cs=5267508D9A2CEDEDF7CB211905827766EE3C2008888F87D2A59F86A4C7C3517A
tests/OmniCore.Tests/SystemProcessRuntimeEnvironmentTests.cs=2B07DE26350B0488D04F8EA486CF6EE8E3AD0A6CB0BEEE840B4664431DEA86AB
```

HEAD de base: `8ded1f2953d659a61696a39c9d42c365aa941fe4`. Hash SHA-256 de las 28 líneas del manifiesto ordenado, codificadas en UTF-8 con LF entre líneas y sin salto final:

`1DB500D6AD858B302E12B5B2C17401E1175D524E69166EAA84D86C77559DC6DC`

DLLs `OmniCore.*` cargadas por el test Host/stdio desde `tests/OmniCore.Tests/bin/Debug/net10.0` y SHA-256 del build final:

```text
OmniCore.Abstractions.dll=9C2CD897D56B8AE61FC734A8969AE986792DC15CAC9F609B257E5248477A3A44
OmniCore.Client.dll=6A7D71D9ED3196D53CC97067E2A8CA4E53CB959EC3621CDD989149C79AEFE05B
OmniCore.Context.dll=14104377C71CF27F0427CD8FC4200D79797A809E355BFA673A7138D5A55838AE
OmniCore.Domain.dll=AC0498F02B795B97133BF63CA46F11E361D401419132E4A7BA8AFE155A62D268
OmniCore.Engine.dll=8130DDE8FF240D6CBDC563ED21064A5A6013741DBB212951CD1A44E9FECC7363
OmniCore.Execution.dll=F3D31E390C8264814AB4E74458B94767F5B642E660A31135F7BB8E2C7990D712
OmniCore.Extensions.dll=2B981B6B8C30CD3A5D99DBD738508CE74A95B41FD6C0ED2C4B1BE63F7C7960CE
OmniCore.Host.dll=68EF94BACE64A1B61091FE61410C261A9D7F5AE56FA1312FC09A78CE9E8E19CA
OmniCore.Infrastructure.dll=BFE8F5B1BAE9D54D4365EF780291419CFAF7C704F6161A0ED69275AC4A9E1853
OmniCore.Memory.dll=CD1FC528822E76C78F853753394829226AB67D1F4B6E2DC53553B0C6F234B9FE
OmniCore.Models.dll=47DBA86FACBFAB6E11CA58D9D62CEEB9F1B50BAAD80E069A473FE9872F23BCD9
OmniCore.Protocol.dll=53DBFC938F010CE56CCA16178035DF35AB318156F63C943559ADE274E1C16C2F
OmniCore.Qualification.dll=878E2A37A6E3F2AAC74AFD2C4064AA6BD58A2ADE3306C5A636986AD2DF7E7760
OmniCore.Sandbox.dll=0706957725215246B41A8B378346505FA22AB799FD4EBD8794D81F367ED02C51
OmniCore.Security.dll=9E2AA541C60F974D59E849B55670EACF536003D04736B5F49AC67AE7165D008F
OmniCore.Tests.dll=4F12269886DBEFC0328F8AF1EE74943C36C2D3AEB75724474ED5A894D1C1E03B
OmniCore.Tools.dll=A1C9B33590D67E460B5294742FDC3FBA58C3C621F97E87F9F1B10DCF77CFCA63
```
