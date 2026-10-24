# M9 — SEC-CONV Core: snapshots de seguridad por Session

**Estado:** corte Core construido y aceptado localmente por Luna bajo: tres
métodos/casos aprobados, sin fallos ni omisiones, sobre binarios congelados.
Base local `c34612a4c682069dfa66c0f82679c01c59b584d4`. Agrega un recorrido de
Host/stdio en un proceso OS separado para el snapshot entre dos Sessions.
No requirió cambios de producción. No cierra SEC-CONV completo, M9 ni M8 manual;
sin cambios de OmniCoder, merge, publicación, instalación ni CI remota.

## Corte y evidencia

La prueba nueva `StdioTransportTests.Native_session_security_defaults_are_snapshotted_per_session_across_selection_and_host_reopen`
usa el Host real como proceso separado, `conversation.new/resume` con proof HMAC,
el journal SQLite del workspace y un `session-security.yaml` temporal. Crea A bajo
`ObserveOnly`, revisa el default fixture a `PatchOnly`, crea B, cambia el default
otra vez a `FullAgent`, alterna A/B y reinicia el proceso Host. Comprueba para cada
Session su propio ID, categoría, revision 1, procedencia `Native` y origen
`UserDefault`, sin aplicar retroactivamente el default posterior. Las categorías
son datos de fixture aislada, no cambios de la configuración real del usuario.
Consulta SQLite y afirma que A/B no tienen eventos `UserInputReceived` ni
`RunCreated`; por tanto el caso no admite input conversacional. No mide contador
de requests de proveedor ni se presenta como prueba de inferencia.

La evidencia Core histórica relacionada se conserva en sus actas:

- `Native_conversation_lifecycle_uses_hmac_receipts_sequence_cas_and_persists_policy`
  ya comprueba en Host OS separado creación, `Native/UserDefault`, selección
  revisionada, receipt/replay y reapertura de una Session.
- `Trusted_stdio_actions_are_connection_bound_scoped_and_receipt_idempotent` y
  `Conversation_admission_is_separate_from_result_and_cancel_is_serialized_with_runtime`
  comprueban HMAC/scope/receipts, admisión de conversación, cancelación y rechazo
  de política obsoleta antes de `UserInputReceived`/`RunCreated` y de un request
  HTTP adicional. El proveedor de ese caso es un fixture local determinista.
- `Native_model_selection_is_session_cas_scoped_and_catalog_does_not_expose_route_configuration`
  comprueba binding de modelo por Session, aislamiento, reapertura y que el binding
  no altere la política de seguridad.
- Las actas de M9-K, elecciones, selección de modelo, Native/Imported y ADR-0056
  contienen evidencia previa para ciclo de vida, autorización y procedencia; no se
  vuelven a ejecutar salvo los dos métodos de proceso indicados en la campaña de
  esta candidata.

## Matriz SEC-CONV-01–13

La columna «evidencia» distingue el foco nuevo ejecutado en este corte de evidencia
local histórica aceptada. La matriz es un mapa de alcance Core, no una declaración
de aceptación integral de cada escenario del plan de OmniCoder.

«Histórica» identifica el origen de la evidencia. En esta campaña se revalidaron
también los dos métodos existentes de lifecycle y admisión enumerados abajo;
los demás métodos citados no se ejecutaron de nuevo. Los IDs de la matriz no
equivalen a casos adicionales: la campaña final contiene exactamente tres casos.

| ID | Evidencia Core y estado | Límite que queda explícito |
|---|---|---|
| 01 | **Nueva en este corte:** `Native_session_security_defaults_are_snapshotted_per_session_across_selection_and_host_reopen`; complementa `Native_conversation_lifecycle_uses_hmac_receipts_sequence_cas_and_persists_policy`. Default fixture, HMAC y Host OS separado. | Prueba Core y query de Host; no valida Sidecar/composer ni ausencia de diálogo en OmniCoder. |
| 02 | **Nueva en este corte:** snapshots distintos A/B bajo defaults fixture revisionados y alternancia/reapertura. **Histórica:** `Native_model_selection_is_session_cas_scoped_and_catalog_does_not_expose_route_configuration` valida binding de modelo por Session. **Incremento posterior:** [grants y dispatch Direct](m9-sec-conv02-dispatch-20261024.md) aceptado localmente: Run aislado, Workspace compartido y techo PatchOnly con efecto real. | El snapshot de este corte no acreditaba grants/dispatch; el incremento posterior acredita su recorrido de patch Direct, sin cerrar SEC-CONV-02 completo ni discovery/GUI/live. |
| 03 | **Nueva en este corte:** ambas Sessions se consultan tras reiniciar Host y reabrirse en orden alternado. **Histórica:** lifecycle prueba política seleccionada y receipt tras reopen. | Sin reconexión GUI ni prueba de renderer. |
| 04 | **Histórica:** `Trusted_stdio_actions_are_connection_bound_scoped_and_receipt_idempotent` valida proof, scope/workspace y replay; `Conversation_admission_is_separate_from_result_and_cancel_is_serialized_with_runtime` valida revisión de política obsoleta. | No verifica Main/renderer que falsifique una categoría; ese gate pertenece a OmniCoder. |
| 05 | **Histórica:** los dos métodos anteriores y `Native_conversation_lifecycle_uses_hmac_receipts_sequence_cas_and_persists_policy` prueban comando firmado por HMAC y receipt/idempotencia en Host. | La ausencia de modal entre edición y envío es del consumidor UI; no se infiere desde Core. |
| 06 | **Histórica:** `Native_model_selection_is_session_cas_scoped_and_catalog_does_not_expose_route_configuration`; ADR-0057 y acta de modelo/routing. | Foco de fixture local; no prueba proveedor live ni todas las decisiones UI de consentimiento de ruta. |
| 07 | **Histórica:** `Native_permission_choice_resumes_exact_turn_with_one_effect_and_receipt`, `Native_plan_approval_choice_applies_only_its_server_defined_effect` y acta de elecciones validan decisión Core/efecto exacto. | No representa todas las tools ni valida presentación/click UI en este corte. |
| 08 | **Histórica:** `Native_plan_approval_choice_applies_only_its_server_defined_effect` y las cuatro opciones de PlanApproval, según acta de elecciones. | La selección Plan/Act sin modal redundante y sus escalaciones en UI quedan fuera. |
| 09 | **Histórica:** `Conversation_admission_is_separate_from_result_and_cancel_is_serialized_with_runtime` cubre cancelación serializada en Host; `Trusted_stdio_actions_are_connection_bound_scoped_and_receipt_idempotent` cubre scope y rechazo de proof anterior en proceso OS separado. | No valida un click Electron ni todas las carreras entre Sessions en renderer. |
| 10 | **Histórica:** `A_child_within_the_parent_writer_ceiling_uses_the_exclusive_writer_lease_and_real_tool_pipeline`, `Admission_fails_closed_without_writing_or_launching` y actas de child controls/Delegation. | Esos recorridos acreditan Core/Host y sus fixtures, no delegación desde UI ni paridad de todo agente externo. |
| 11 | **Histórica:** `Startup_with_owned_legacy_session_migrates_once_before_query_but_query_only_host_does_not_write`; `Native_transcript_import_is_receipted_passive_observe_only_and_feeds_later_authorized_context`; actas M9-K y Native/Imported. | No valida aviso visual, formatos de importación ajenos ni migración iniciada desde GUI. |
| 12 | **Histórica:** `Conversation_admission_is_separate_from_result_and_cancel_is_serialized_with_runtime` presenta una admisión firmada con `expectedPolicyRevision` stale y comprueba ausencia de input/Run/request extra. `Durable_allow_to_ask_change_during_preflight_blocks_started_and_effect_without_approval` prueba concurrencia en la barrera Core. | El caso de stdio stale es secuencial tras avanzar la revisión, no un envío concurrente desde UI; preservación/recarga del borrador requiere el consumidor. |
| 13 | **Histórica:** procesos OS reales stdio con HMAC, scope, journal/receipts y provider HTTP fixture en métodos citados. M9-K/elecciones documentan los E2E Electron→Host previamente aceptados. | Este corte nuevo no corre Electron; fixtures no prueban inferencia live, aceptación multiplataforma ni el E2E completo de creación/envío/escalación/cancelación/invalidez. |

`SEC-CONV-14` (fallback de consumidor tras caída del Host) no se construye en este
corte. El plan de OmniCoder sólo se consultó en lectura para identificar la
matriz; no se ejecutaron herramientas, pruebas, builds ni cambios de ese producto.

## Validación de esta candidata

Build del proyecto de tests net10: **0 warnings, 0 errors**. No cambia proyectos
compartidos multi-target; no se ejecutó build net8. Tres métodos, una vez cada
uno contra la DLL net10 construida: **3 PASS, 0 FAIL, 0 SKIP, 0 Not Run**:

- `StdioTransportTests.Native_session_security_defaults_are_snapshotted_per_session_across_selection_and_host_reopen` — 1 caso, Host OS separado.
- `StdioTransportTests.Native_conversation_lifecycle_uses_hmac_receipts_sequence_cas_and_persists_policy` — 1 caso histórico pertinente, repetido una vez.
- `StdioTransportTests.Conversation_admission_is_separate_from_result_and_cancel_is_serialized_with_runtime` — 1 caso histórico pertinente, repetido una vez.

Comandos ejecutados desde la raíz OmniCore:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -f net10.0 --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.StdioTransportTests.Native_session_security_defaults_are_snapshotted_per_session_across_selection_and_host_reopen"
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.StdioTransportTests.Native_conversation_lifecycle_uses_hmac_receipts_sequence_cas_and_persists_policy"
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.StdioTransportTests.Conversation_admission_is_separate_from_result_and_cancel_is_serialized_with_runtime"
```

Logs: `artifacts/m9-sec-conv-core/build-net10-final.log`,
`new-snapshots.log`, `lifecycle.log` y `admission.log`. El manifiesto
`artifacts/m9-sec-conv-core/manifest-sha256.txt` congela las fuentes/documentación
modificadas, las DLL de tests y las copias CLI/Host ejecutadas por los procesos.

## Validación independiente final

Luna bajo revisó el diff, la matriz y los helpers reales del proceso Host; después
ejecutó los mismos tres métodos secuencialmente, sin recompilar: **3 PASS, 0 FAIL,
0 SKIP, 0 Not Run**. Cada método seleccionó exactamente un caso. Los tres casos del
constructor y su repetición independiente no se suman como seis casos únicos.

El manifiesto de 17 rutas coincidió antes y después. La revisión adicional
identificó la imagen realmente lanzada:
`dotnet src/OmniCore.Cli/bin/Debug/net10.0/omni.dll host --stdio`. Comprobó los
19 archivos de esa imagen Core —`omni.dll`, deps/runtimeconfig y los 16 ensamblados
`OmniCore.*.dll` adyacentes— antes/después; coinciden con sus copias en tests/bin.
No quedaron Hosts propios; los nodos MSBuild ajenos no se tocaron.

La nueva prueba está serializada por `ProcessEnvironmentCollection`. Su finally
anidado restaura siempre clave, variables de fixture y CWD, incluso si falla la
parada del proceso. La configuración temporal no modifica el perfil del usuario.

Informe independiente: `artifacts/m9-sec-conv-core/low-validation-20261024.md`,
SHA-256 `DB42B6B7DC4C6D620A25C2FEDE2B4BF2998BF879CAF98846ADBD42337102ACED`.
Logs: `low-final-01.log` a `low-final-03.log`. Manifiesto original de la candidata:
SHA-256 `6BB6CF1512770C0BF5ECB6DEE5A0BC877B5A4D3BED04B3F256D8A072122D2460`.
Esta acta y los documentos generales se actualizaron editorialmente después del
dictamen; fuentes de prueba y binarios siguen iguales a los validados.

## Exclusiones

No hay cambios de producción, UI, CLI, permisos, perfil ni configuración global.
No se ejecuta la suite completa ni se modifica/compila OmniCoder. Los resultados
Core no cierran SEC-CONV, M9 global o M8 manual.

## Fuentes y binarios aceptados

Producción corresponde a la base `c34612a`; sólo cambia la prueba identificada.
La tabla conserva la identidad de la fuente nueva, el runner y la imagen Core
del proceso hijo revisados por Luna bajo.

| Archivo | SHA-256 |
|---|---|
| `tests/OmniCore.Tests/StdioTransportTests.NativeLifecycle.cs` | `74C95444F3EA9EA694BACF2C4605A176A9E9FD05F6A4D926D306103DF484C6EC` |
| `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll` | `8C8517DD8165F3337D2D0D6BCFBFD8DF1C067195EF61B6711B4380B9E13607D9` |
| `src/OmniCore.Cli/bin/Debug/net10.0/omni.dll` | `280211A7727C108D409F012B5DA7F012069A23A0D1F856B863224D7D3AB3E739` |
| `src/OmniCore.Cli/bin/Debug/net10.0/omni.deps.json` | `72DBE4102B6B59929941A1BE12941E7823BB37FFCFCB036D0195EFC6D506B95F` |
| `src/OmniCore.Cli/bin/Debug/net10.0/omni.runtimeconfig.json` | `256DDA2706E1151BB95987B6B5BE610BF68D64E058285B19E7E958222FC90414` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Abstractions.dll` | `1EAC2CECDCF5575CBB46D431211C640F8104559CE8575B6C0EB592E68177AB6F` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Client.dll` | `6348684B386C73CABF648956C12DD62810542C1C368A0454474C3198551137EE` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Context.dll` | `871ED60AB7A507ADCDB86E05BF11CFF30DB8D14D703D5CBDC916D89F4B2587D1` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Domain.dll` | `42227D7452CB88EF80E47AFA6D8E6E65EEACCF66D4C70E16C116FA9784060488` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Engine.dll` | `87F47888BE4CE611B183742AFCDC9844C95F4E9A3679723A03D4B96D4A0F7F23` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Execution.dll` | `0280A191C6016A6A9EAC7027D802B98E5509EDC204F4CA32620403AD95CF86B0` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Extensions.dll` | `3B44F24903688900E6CDE1AD1E3AACFB22AA485745E62CC3898A677FBAA05F1C` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Host.dll` | `B06F969BF7CF879E30A988597433A83EDDDC0BBD7CE536530743F21B584E441F` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Infrastructure.dll` | `2578EEB68E966B6734165F32D4D0D61C2923AF27A9C209F06E399B6621D65D91` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Memory.dll` | `77032BE53184F811D532AAA3F863F2AC3D5CBC0A55248C0045B37BBF475D3C7B` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Models.dll` | `79BC0B834347B961A66F713163BEEFA5E99A8E25D894D6C713E5ED9954E90187` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Protocol.dll` | `B5D5A15B6608C2B1790487BC0DF49C147C400E9D1F07D83939C3668E376419EB` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Qualification.dll` | `E6A1FA90B928E36822A63A8E2164AA458FC8EA7C00AF71171C99CC6184E208A5` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Sandbox.dll` | `DDF5F738F23BCF0D05487A09B4847A854F379C474936F276AFC184FAE5F826DC` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Security.dll` | `DE9A639B8B861C0E525485E6729897F3A85EA043D1BB7062FDC30CE461160EC9` |
| `src/OmniCore.Cli/bin/Debug/net10.0/OmniCore.Tools.dll` | `136F820B85B1B845055AD922E4A463809D86A540F4ED7ED0761AA26A0A4285DF` |
