# `!comando` humano nativo en Core — candidata local 2026-10-26

## Alcance

Esta candidata implementa el contrato de `!comando` definido por UX-03 y P0-COMPOSER-SHELL-NATIVO. El despacho humano pasa por admisión nativa firmada, resolución de Session/Run, catálogo y claims de `shell.exec`, ToolRuntime, permisos de Session, sandbox, ejecución persistente, journal y recibos. La salida no se añade al historial de conversación: la TUI ofrece una acción localizada para añadirla al borrador. La CLI y la TUI consumen la fachada Host in-process; no hay envío de `!` al proveedor ni fallback a un ModelTurn.

La ruta preserva Ask, `allow_once` y WeakSandboxConsent. Al continuar de Ask hacia WeakConsent, el puente temporal reutiliza solo la resolución humana `allow_once` del mismo ToolCall, Run, Task, Lane y Turn, con fingerprint idéntico. No crea un grant duradero ni cambia Deny o los techos de permiso. Las consultas del recibo son pasivas tras reiniciar el Host; procesos cuyo efecto quedó incierto se mantienen Unknown y no se relanzan.

## Cambios de esta candidata

- Host/Protocol: acción humana tipada y firmada, admission/revisión/scope, recibos y continuaciones de permiso/sandbox. La finalización de un recibo in-process sigue el resultado terminal real y no duplica el dueño de recibos del transporte stdio.
- Ejecución: herramienta humana `shell.exec` sobre ToolRuntime y el manager persistente, con claims de ejecutable/argumentos/cwd, streaming de salida, cancelación del árbol propio y vista humana separada de las vistas del agente, incluso después de recuperación.
- Permisos: reanudación Ask→WeakConsent dentro del mismo scope, manteniendo la decisión `allow_once` sin persistir autoridad adicional.
- CLI: `!` ejecuta por `CliApp.RunAsync` in-process a través del mismo contrato; el camino ejecuta un proceso OS real. Esta evidencia no representa un `omni` CLI lanzado en otro proceso OS.
- TUI: el composer despacha `!` a `ITuiTurnHost.ExecuteHumanShellAsync`; errores conservan el texto enviado y salida exitosa abre una vista con opt-in localizado para el borrador.
- Recuperación: una ejecución humana recuperada aparece como Unknown/gap pasivo y queda fuera de `process.start`/`process.control` del agente.

## Validación

Construcción final después de cablear:

```text
dotnet build OmniCore.slnx --no-restore
Compilación correcta; 0 advertencias, 0 errores.
Incluye Protocol, Client y Sandbox en net8.0/net10.0; Host, CLI y pruebas en net10.0.
```

Focos ejecutados después del último cambio de scope del puente Ask→Weak y del nuevo test TUI:

| Método | Resultado | Qué demuestra |
|---|---:|---|
| `OmniCore.Tests.StdioTransportTests.Native_human_shell_permission_and_weak_consent_use_signed_resume_without_provider` | 4/4 PASS, Windows | Ask y WeakConsent por la ruta firmada; reanudación sin proveedor extra y receipt terminal. El puente Once queda limitado al scope exacto y valida fingerprint. |
| `OmniCore.Tests.TuiWiringTests.Composer_bang_dispatches_to_human_shell_and_requires_explicit_context_opt_in` | 1/1 PASS, Windows | TUI Terminal.Gui real: `!` llama `ExecuteHumanShellAsync`, no `ExecuteAsync`; exit 7 conserva el borrador, la salida no se añade automáticamente y el botón localizado la añade solo después de una acción explícita. El Host de esta prueba es un fixture controlado; la ejecución OS real se acredita por separado. |

Focos pertinentes ejecutados en la DLL inmediatamente anterior al endurecimiento del scope y al test TUI (el código probado por esos métodos no cambió en esa última edición):

| Método | Resultado | Límite |
|---|---:|---|
| `OmniCore.Tests.StdioTransportTests.Native_human_shell_runs_through_host_without_provider_and_is_not_inherited_by_later_model_context` | 1/1 PASS | Host real, proceso OS, salida/error/cwd/cancelación propia y ausencia de herencia al contexto posterior. Incluye `CliApp.RunAsync` in-process. No es un proceso CLI separado. |
| `OmniCore.Tests.PersistentProcessSessionManagerTests.Recovered_human_shell_is_passive_unknown_and_never_enters_agent_process_views` | 1/1 PASS | Reapertura pasiva, estado Unknown y aislamiento de vistas del agente. |
| `OmniCore.ArchitectureTests.CliAssemblyReferenceTests.Omni_assembly_references_only_allowed_projects` | 1/1 PASS | La CLI conserva las referencias permitidas Client/Host/Protocol. |
| `OmniCore.Tests.StdioTransportTests.Native_stdio_orq_workflow_publishes_real_owned_implementation_worktree_and_passive_queries_resolve_it` | 2/2 PASS, validación previa de la candidata | Regresión causada por la espera de workflow; una respuesta fixture retrasada supera el timeout local anterior sin exceder el deadline global. El cambio final de permiso/TUI no modifica ese recorrido. |

No se ejecutó la suite completa local. La prueba TUI verifica wiring del composer, no aceptación visual/manual. Los focos del Host comprueban el proceso OS real; el CLI ejercitado es in-process. Weak persistente tiene backend Windows autorizado en este corte; Linux no se acredita como Weak. No se usó proveedor live. Esta acta acredita solo esta candidata local y no cierra M9 global, M8 manual, gates live, GUI o plataformas diferidas.

## Observaciones de corrección

Un intento de compilación intermedio detectó tres referencias ambiguas entre `OmniCore.Domain.Task` y `System.Threading.Tasks.Task` en el nuevo fixture TUI. Se cualificaron los tipos; la compilación final indicada arriba es la única aceptada. Una ejecución anterior del test TUI pasó 1/1 y su salida de Terminal.Gui produjo escape sequences extensas, por eso no se conserva el volcado ANSI como log.

El filtro del puente Once parte de la sesión y del scope exacto del `InteractionRequested`/`PermissionRequested` del Weak. Para los eventos de resolución que pueden no llevar scope en el envelope, acepta la correlación por InteractionId solo dentro de la misma Session y rechaza cualquier Run/Task/Lane/Turn/ToolCall envelope presente que difiera del Weak actual.

## Manifiesto

Los hashes SHA-256 de fuentes modificadas y de binarios del build final están en `artifacts/m9-composer-shell-native/final-manifest.json`. Las salidas y conteos de los focos nuevos están en `artifacts/m9-composer-shell-native/focus-results.txt`. Los resultados de focos previos están identificados como validación previa, no como ejecución sobre el último DLL recompilado.

## Corrección framed stdio — candidata corregida pendiente de validación Low

La revisión Low de la candidata anterior quedó **NO APROBADA** (`artifacts/m9-composer-shell-native/low-final-validation.md`, SHA-256 `DCA78F3C2BDE4381D78C56D52086BEA112A8AC0FDC30174B18FF76713D066EA2`). Sus 10 casos previamente verdes no demostraban la ruta framed. La copia rechazada y sus manifiestos se preservan en `artifacts/m9-composer-shell-native/rejected-review-20261026/`.

La ruta framed tenía defectos en ambas continuaciones. La respuesta Weak fijaba `NativeCommandKind` como `compiledWorkflow`, y el finalizador worktree se intentaba para cualquier `nativeTool`, dejando sin finalización el recibo fuente de `core:shell.exec`. Después de corregirlos, el foco reveló que el recuperador de choices no llevaba `NativeWeakSandboxCapability` en el binding nativo del shell. Se corrigió el recuperador solo para una invocación cuya llamada/journal, recibo, sesión/Run, admisión y payload protegidos coinciden con `core:shell.exec`. Así `Ask` → `allow_once` vuelve a evaluar el preflight Strong y solicita el consentimiento Weak existente si Strong no está disponible; no se concede consentimiento por el binding ni se omite el permiso.

La ejecución framed completa usa un solo runner de stdio. La continuación shell llama el finalizador humano ya existente para validar el efecto y completar el recibo fuente; no usa el finalizador de worktree. El recibo de la respuesta final toma el estado/código durable del shell; para un exit distinto de cero con efecto confirmado conserva `completed` y `native_shell_exit_nonzero`. El choice `allow_once` queda `deferred/input_required` mientras espera el consentimiento Weak independiente. La respuesta `consent_once` y el recibo fuente pasan a terminal después del único efecto.

El foco framed usa HMAC negociado por stdio y `ExchangeTrusted`, sin invocar manualmente los métodos Resume del runtime. Es evidencia distinta del canal confiable in-process usado por CLI/TUI; no se denomina firma criptográfica a la llamada local. La teoría nueva se ejecutó en Windows: 2/2 PASS. Cubre Strong/Allow con exit 7 y Ask→WeakConsent con el lanzador Weak real después de que el preflight Strong declare indisponibilidad antes de lanzar. En ambas filas comprueba un solo ToolCall/proceso/efecto, recibos terminales, ningún request al proveedor y replay del comando original sin delta del journal; la fila Weak también repite y reabre el receipt de consentimiento pasivamente. La prueba anterior a la corrección de binding falló porque no se emitió WeakConsent; ese intento no se cuenta como PASS.

El foco de regresión existente `Native_human_shell_permission_and_weak_consent_use_signed_resume_without_provider` pasó 4/4 en la imagen final. Build final de `OmniCore.Tests` net10: 0 advertencias, 0 errores. No se modificaron Protocol/Client/Sandbox compartidos en este fix; no se repitió el build net8. No se ejecutó la suite completa local.

Las fuentes e imágenes exactas se registran en `artifacts/m9-composer-shell-native/final-framed-manifest.json`; los resultados de la imagen corregida están en `artifacts/m9-composer-shell-native/framed-focus-final.log` y `framed-build-final.log`. El informe Low NO APROBADO y el bundle de la candidata anterior permanecen intactos. La candidata corregida queda pendiente de revisión Low; esto no cierra M9 global ni M8 manual.

## Corrección de enrutamiento de receipts worktree — candidata posterior pendiente de Low

La revisión Low de la imagen framed anterior fue **NO APROBADA**: el manifiesto de revisión registró `core:worktree.integrate` como admission canónica, pero `StartPendingAfterAck` comprobaba el prefijo `worktree.`. Por eso no ejecutaba `CompleteNativeWorktreeExecutionReceipt` para integraciones con `Ask`. Se preservaron el informe y los snapshots previos; no se contaron sus cero tests como aceptación.

La candidata ahora reconoce únicamente los IDs canónicos `core:worktree.integrate`, `core:worktree.cleanup`, `core:worktree.discard` y `core:worktree.retention`, y confirma que el ID del payload protegido coincide con la admisión. Shell sigue usando su finalizador específico; el canal `process.control` no se redirige a ningún finalizador de worktree.

Se fortaleció el método existente `Native_worktree_preview_is_passive_and_apply_uses_ask_receipt_and_deduplicated_tool_pipeline`: después de la aprobación framed de `worktree.integrate`, verifica que el receipt fuente termine como `completed/native_tool_completed` y que el receipt de la respuesta `interaction.respond` también quede terminal. La ejecución de este único método pasó 1/1 en la imagen candidata. Build net10 de `OmniCore.Tests` pasó con 0 advertencias y 0 errores. No se repitieron los focos shell/Weak, pues el cambio está limitado al enrutamiento de receipts de worktree; Low validará esta imagen y el método afectado.

El manifiesto de esta corrección es `artifacts/m9-composer-shell-native/final-routing-manifest.json`, con salida en `artifacts/m9-composer-shell-native/routing-build.log` y `routing-worktree-focus.log`. La corrección queda pendiente de Low; M9 global y M8 manual siguen abiertos.

## Revisión independiente de la candidata corregida

Luna bajo aprobó esta candidata para commit/push y CI exacta. Ejecutó cinco métodos secuencialmente y sin recompilar: framed shell (2 filas), permisos shell (4), Host/CLI/cancelación/contexto (1), workflow con demora controlada (2) e integración nativa de worktree con Ask y recibos terminales (1). Resultado: **10/10 casos, cero fallos u omisiones**. Las 106 entradas del manifiesto coincidieron antes y después; los cinco runners terminaron. TUI, recuperación y arquitectura conservan los tres antecedentes pertinentes ya identificados, sin contarlos de nuevo.

Informe: `artifacts/m9-composer-shell-native/low-routing-validation.md`, SHA-256 `CC9024FCBCF0EFCBF63A7849117C76B6A3BAE832865899CAAFD3609E4DC667A5`. Manifiesto: `65C997F265077EFC79D7C58297B9D8E6B3CF48FE24F90104D4779BAE6746825D`. Esta entrada editorial posterior no cambia fuentes ejecutables ni imágenes probadas. Los informes rechazados permanecen como antecedentes; la integración en main requiere los cinco jobs de la CI del commit exacto. M9 global, M8 manual y demás gates de aceptación mantienen sus límites.
