# M9 — provisión de worktree desde workflow Orchestrate por stdio

**Estado:** aceptado localmente por Luna bajo: **2/2 casos aislados aprobados**, sin recompilar durante la revisión. Este resultado no cierra SEC-CONV-02, SEC-CONV, M9 ni M8.

## Alcance

Este corte prueba una delegación de etapa del workflow compilado `core:orq-auth` iniciada por comandos confiables de stdio existentes. No acredita la admisión genérica `/delegate`, la UI Electron ni el workflow completo como producto.

La ruta ejercitada fue `run.mode.select` con Orchestrate y límites finitos, `command.invoke.user` para el `core:orq-auth` exacto del catálogo con sus revisiones y HMAC, y la continuación normal. La etapa Implement hizo admisión interna mediante `WorkflowRuntimeAuthorization`, `DelegationExecution` y `LaneWorktreeProvisioner`; el test no sembró un evento de ownership, binding, TaskPacket ni admisión.

La prueba usa `StdioProtocolHost` y un `RecoveryDuplex` framed en el mismo proceso del test. No es un Host en proceso OS separado. El provider fue un HTTP fixture local. La verificación invocó el `dotnet.exe --version` instalado en el host, pasó por el tool/runtime y recibió una aprobación humana firmada de Weak sandbox `consent_once`, ligada a los IDs exactos Session/Run/Task/Lane/Turn/ToolCall/invocation/interaction y revisión vigente. En este entorno Strong no estuvo disponible; por eso el resultado de la verificación corresponde a esta ruta Weak con consentimiento y no demuestra Strong.

No se amplió autoridad, interfaz, API, capability de delegación, grant ni permisos productivos. La configuración temporal autorizó el verificador fixture con su regla exacta; el consentimiento Weak se concedió mediante el comando confiable ya existente, no por autoridad del modelo. La capacidad de responder se optó explícitamente en el hello de esta prueba. No se usaron credenciales reales ni proveedor remoto.

## Evidencia que reutiliza

- `StdioTransportTests.Native_workflow_ack_cut_stays_passive_until_explicit_continue_and_duplicate_is_idempotent` cubre catálogo, Orchestrate firmado, admisión de `core:orq-auth`, receipt/recovery y continuación; no ejecuta una etapa ni crea su binding.
- `m9-worktree-lifecycle-20261023.md` y `m9-worktree-disposition-20261024.md` cubren preview/acciones con hechos `LaneWorktreeCreated`/terminales sembrados, no la provisión end-to-end.
- `m9-child-process-controls-20261023.md` usa admisión in-process de delegación para controles stdio; no prueba la provisión desde el workflow stdio.

## Caso ejecutado

El único método nuevo ejecutado fue `Native_stdio_orq_workflow_publishes_real_owned_implementation_worktree_and_passive_queries_resolve_it` en `tests/OmniCore.Tests/StdioTransportTests.NativeCommandHandoff.cs`.

El test creó una Session/Run y un repo Git temporales; comprobó que no hubiera ownership inicial; autorizó Orchestrate firmado con límites finitos; rechazó una revisión caducada, un proof de otra Session y un proof de otro workspace sin alterar el journal; consultó e invocó el descriptor `core:orq-auth` con fingerprint y revisiones actuales. El comando wire directo `delegation.create` no se admite en el allowlist stdio; esta comprobación fue estática con `IsSupportedCommand`, no un dispatch de un comando desconocido.

El provider fixture respondió a 10 solicitudes. Los stages Explore, Implement y Verify finalizaron. En Verify, el tool `dyn.core.verify_integration` ejecutó `dotnet.exe --version`; el journal conserva `ToolCallSucceeded` con código 0 y la interacción Weak resuelta con `consent_once`. La prueba aserta ambos hechos y el Verify plan completado.

El test encontró un único `TaskCreated`, `DelegationCreated`, `LaneCreated`, `AgentExecutionStarted` y `LaneWorktreeCreated` relacionados con la etapa Implement y verificó el scope Session/Run/Task/Lane/Execution. Deserializó el TaskPacket con el `TaskPacketJsonContext` productivo y cotejó IDs, profile, SelectedProjectionRef, ownership, repo/gitdir/worktree, HEAD, snapshot/tree y base. El worktree Git físico tuvo el commit/árbol esperado, mientras el repo principal conservó su HEAD inicial.

Consultas pasivas `agents` y `worktree.preview:<laneId>` resolvieron el binding publicado sin cambiar secuencia del journal; una Lane aleatoria no atribuida no tuvo preview. El bundle preservado está en `artifacts/m9-worktree-workflow-provision/20261024T211253Z-31282279d8a0433e95d5a82aeb42795c/` e incluye fixture, DB/CAS/config/worktree, requests/responses ficticios, acciones con proofs redactados, logs, `candidate-manifest.txt`, `fixture-files.sha256` y hashes del output del test.

## Compilación y conteos

- Build: `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -f net10.0 --no-restore` — correcto, 0 warnings, 0 errores. Log: `artifacts/m9-worktree-workflow-provision/build-net10-final.log`.
- Selección directa del DLL xUnit v3: `dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method "OmniCore.Tests.StdioTransportTests.Native_stdio_orq_workflow_publishes_real_owned_implementation_worktree_and_passive_queries_resolve_it"` — 1 total, 1 pass, 0 errors, 0 fail, 0 skipped, 0 no ejecutados. Log: `artifacts/m9-worktree-workflow-provision/focus-final.log`.
- No se ejecutó suite general ni compilación net8; los proyectos compartidos no cambiaron.

Los intentos de desarrollo previos no cuentan como aceptación. Hubo un primer build con errores del nuevo test; intentos focales posteriores expusieron la limpieza del pool SQLite con connection string distinto, archivos Git read-only al limpiar el fixture y valores incorrectos en el fixture (timeout de Verify, nombre de revisión, forma JSON del TaskPacket). Se corrigieron en el fixture sin cambiar producción. La interacción Weak fallida anterior a opt-in/consentimiento tampoco se cuenta como autorización: la pasada final usa el consentimiento firmado y ligado al alcance antes descrito. `artifacts/m9-worktree-workflow-provision/build-net10.log` conserva el primer error de build y `focus.log` conserva una falla de cleanup; las carpetas de intentos preservadas no se mezclan con el bundle final.

## Congelación y límites

Fuente final: `tests/OmniCore.Tests/StdioTransportTests.NativeCommandHandoff.cs` SHA-256 `D2F9A6DE9144EFFE87AB35EB9929913088EEF4CEE245C5203A4062802286D85D`.

Binario ejecutado: `tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll` SHA-256 `2DD310220547BC8EC956ED6CEAB910915B818973E37492EA960191E9D12C1A38`. El output de test preservado incluye un manifiesto SHA-256 de sus archivos. El verificador real fue `C:\Program Files\dotnet\dotnet.exe`, SHA-256 `21A46F1E5235CF4E844B9DE5429F0E198B9C97A41F0503A66442F1D639CA3EE6`.

El archivo protegido `x` conserva SHA-256 `84E292A1C73B431B64FBC2C8F0FA8B08CC771E58B98F05FB968C842CBBF6FE45`. No se modificó producción, OmniCoder ni otra copia del usuario. El constructor entregó la candidata sin commit; el incremento se registra en un checkpoint local posterior a la aceptación.

## Aceptación independiente

Luna bajo ejecutó el método nuevo y `Native_workflow_ack_cut_stays_passive_until_explicit_continue_and_duplicate_is_idempotent`: un caso aprobado en cada selección, **2/2 PASS**, cero errores/fallos/omisiones/no ejecutados. No hubo build ni cambios de código. Logs: `artifacts/m9-worktree-workflow-provision/low-final-01.log` y `low-final-02-isolated.log`. No se suman campañas verdes anteriores ni intentos de desarrollo.

La primera ejecución de ack-cut no aislaba las raíces externas y se excluyó; no se inspeccionó ni copió su contexto. La repetición aceptada usó un runner hijo con HOME/USERPROFILE/OMNICORE_AGENT_HOME y raíces CODEX/CLAUDE/XDG/OPENCODE/APPDATA/.NET/NuGet temporales vacías y credenciales omitidas. El temporal se retiró al terminar. El caso nuevo ya aísla esas raíces en su fixture.

Informe: `artifacts/m9-worktree-workflow-provision/low-final-validation-20261024.md`, SHA-256 `BAABAF1C1E4F194FB6CFB8B2CD038A9919012CD8E27123EF2194E3BB711D2116`. La evidencia de la ejecución focal de Low está en `20261024T211737Z-47fafbba432c41e1a37eb4089c38aa22/`, con `low-run-fixture.sha256` (156/156 rutas verificadas). Low verificó también las 152 rutas del paquete candidato, sus diez elementos de manifiesto y los 99 archivos preservados del nivel superior del output de Tests. Los hashes coincidieron antes y después; esta acta congelada se actualiza editorialmente tras el dictamen sin cambiar las fuentes de prueba ni los binarios.

El `candidate-manifest.txt` del paquete candidato conserva SHA-256 `BC2ECBB1757BEBA755F4185166E8B96311C7BA53C76E27580FDB7067804DD88A`. Tras editar la documentación, root verificó sus nueve entradas no editoriales sin recompilar ni repetir pruebas.

| Binario validado | SHA-256 |
|---|---|
| Tests DLL | `2DD310220547BC8EC956ED6CEAB910915B818973E37492EA960191E9D12C1A38` |
| Host DLL, copias CLI/Tests | `E3AB4052910E5C2E83D2CBE9015F241C487B0FE03604D870724C9C29104E34A7` |
| CLI `omni.dll`, copias CLI/Tests | `E4899D3AF2AD381B59D6D9089DCD1D626E34BA6D00B202302EA6D9B19FB67576` |

La preservación es una copia quiescente después de detener Host/runtime, con `journal.db` y sidecars WAL/SHM; no es `BackupDatabase`. Low obtuvo `integrity_check=ok` para journal, user y spend-reservations y validó los 73 blobs CAS por hash. Los TaskPackets de Explore e Implement resuelven por hash/tamaño y scope. Tras capturar evidencia, el fixture desactiva la conservación del root original y completa el borrado; no se aceptó omitir cleanup para esconder un fallo. Se restauraron entorno/CWD, se borró la clave y no quedaron procesos propios activos.

## Límites y decisión fuera de este corte

Esta evidencia se limita a provisión real del binding de una etapa de workflow desde Host/stdio in-process, su consulta pasiva y el verificador Weak con consentimiento explícito. `dotnet --version` verifica el recorrido declarado; no compila ni prueba funcionalmente FeatureApp. No acredita frontera de proceso OS para Host, formas de dispatch no ejercitadas, `/delegate`, GUI/live/plataformas ni aceptación global SEC-CONV/M9/M8.

La admisión genérica `delegation.create` por stdio no está expuesta. Habilitarla requeriría una decisión explícita de interfaz/capability y scopes humanos confiables conforme a ADR-0046; no se deduce de este límite de cobertura ni se implementó aquí. La ruta especializada del workflow conserva su capability no serializable y las etapas compiladas vigentes.

Checkpoint local únicamente: sin push, merge, CI remota, instalación ni cambios de OmniCoder.
