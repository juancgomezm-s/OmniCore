# M9-L: ciclo nativo de integración de worktree

Esta evidencia cierra un bloque acotado de M9-L: preview pasivo por Lane público e integración nativa mediante la ruta existente `worktree.integrate`. No cierra M9 global ni M8 manual.

## Recorrido comprobado

El cliente stdio consulta `agents`, toma el `LaneId` público y llama `worktree.preview:<laneId>`. El servidor resuelve en la Session/Run actual el binding canónico `LaneWorktreeCreated`, devuelve `ownershipId`, proposal y paths acotados, y no concede autoridad por leer ni exponer rutas físicas. `worktree.integrate` se transporta por `command.invoke.user` con el identificador canónico `core:worktree.integrate`; pasa por admisión protegida, lease del escritor, prelectura y la barrera Ask existentes antes de ejecutar Git real. El permiso se reanuda con `RunInteractionResumed` solo si la cadena nativa durable prueba admisión, invocation, tool call/preparación, permiso y respuesta de usuario en el scope exacto. El resultado físico, `ToolCallSucceeded` y el recibo cerrado se correlacionan; el recibo original y el recibo de aprobación conservan sus identidades distintas.

La fixture siembra el binding `LaneWorktreeCreated` mediante el lifecycle canónico y ejecuta el recorrido Host/stdio→tool→Git. No prueba aquí la creación ni el retorno de una delegación completa.

## Verificación

Comando ejecutado en el worktree `C:\Users\juanc\.codex\worktrees\m9-completion\OmniCore`:

```powershell
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --artifacts-path artifacts/m9-worktree-lifecycle --parallel none --output Detailed --filter-method '*Native_worktree_preview_is_passive_and_apply_uses_ask_receipt_and_deduplicated_tool_pipeline*' --filter-method '*Native_worktree_deny_resumes_without_user_input_or_git_effect*' --filter-method '*Apply_capture_rejects_a_stale_proposal_and_requires_exact_claimed_paths*' --filter-method '*Integration_permission_ask_and_missing_lease_stop_before_barrier_or_effect*'
```

Resultado: 6 correctas, 0 errores y 0 omitidas; duración 1m 14s 434ms. La prueba positiva usa stdio Host real y un repositorio Git aislado, comprueba que preview no cambia journal/refs/worktree, que Ask no ejecuta el efecto antes de aprobación, que la aplicación física y recibos coinciden, que hubo una sola solicitud al provider, y que retry y cierre/reapertura ordenada del Host no duplican evento, provider ni efecto. También comprueba selector de Lane público, ownership ajeno bien formado, proposal stale, SQL de recibo idempotente para mismo terminal/hash y conflicto para hash/terminal distinto, rechazo de resume forjado y ausencia de `NativeCommandInvocationFinished` en este recorrido tool-only. La prueba Deny confirma que no se emite `ToolCallStarted` ni se altera el worktree. Casos existentes del store confirman stale/paths exactos, Ask/Deny y lease ausente antes de barrera/efecto.

La salida íntegra está en `artifacts/m9-worktree-lifecycle/final-run-20261023-final.log` (SHA-256 `340EF27116AB1F42322DB89B9226B2E2700ECD45E4504E2C618A393FC14D6433`). El assembly probado es `artifacts/m9-worktree-lifecycle/bin/OmniCore.Tests/debug/OmniCore.Tests.dll` (SHA-256 `C9CA7608F70A79C61C3899FFEDD3B8E097FDAB20EDB16111FD7AAD52F8900459`).

## Causa raíz demostrada y validación independiente

El rechazo `conversation_questionnaire_resume_unavailable` se reprodujo con un
recibo `Completed/Accepted/deferred/input_required`: Luna alto midió **Running**
en `RunProjection` antes de responder, mientras `CanResumeChoice` exigía
**AwaitingInput**. La corrida de reproducción terminó en fallo; no se cuenta como
aceptación. Después de restaurar `EnsureRunAwaitingInput`, el mismo recorrido
midió AwaitingInput y aceptó la respuesta. La correlación comprobó Session/Run y
Task/Lane/Turn/ToolCall/Interaction del permiso y la llamada originales. El
checkpoint del fallo conservó Task `01a16acf-a4ba-7f53-a482-d234d466156a`, Lane
`01a16acf-a4bb-735f-a1d3-6a5dcd9ffe12` y Turn
`01a16acf-f425-747c-9287-75d2f0740a7d`; no se reconstruyen los otros IDs que no
quedaron en ese reporte.

El contraste con el cuestionario mostró dos supuestos de conversación que no
sirven para una acción tool-only: buscar `UserInputReceived` como admisión y exigir
otro input como prueba de reanudación. Este recorrido usa la admisión protegida,
su recibo y `RunInteractionResumed`; conserva el camino previo para cuestionarios
y permisos conversacionales. El evento de inicio de la invocación precede al Turn:
se correlaciona por Invocation/Session/Run y orden, mientras el scope completo se
comprueba en llamada, preparación, permiso y resolución. Las validaciones
canónicas y de replay rechazan reanudaciones sin esa cadena.

La aceptación por el Host y la ejecución son hechos distintos. El recibo original
se terminaliza desde deferred sólo con su hash, Session/Run y resultado durable
comprobados, bajo la autoridad de escritura vigente. El ACK original se conserva;
no se emite `NativeCommandInvocationFinished` para suplir la prueba de esta acción.

Luna bajo aprobó **6/6 casos del bloque y 6/6 regresiones pertinentes**, sin fallos
ni omisiones, con `--no-build --no-restore`: procesos nativos, cuestionario normal y
tras reinicio, permiso nativo y recuperación. No se suman las repeticiones del
constructor como casos nuevos. El ensamblado, prueba y log del constructor
conservaron sus hashes antes y después. La evidencia independiente está en
`artifacts/m9-worktree-lifecycle/low-validation-20261023.md`, SHA-256
`1026F1987F3C6E426D0DA6F07BA24F4219213539B7DFF21B1FA81A4A77866B2C`.

Esta aceptación es local en Windows/ZephyrusG14. No hay CI ni publicación del
incremento; la CI de `f02d3b5` acredita únicamente la base ya publicada.

## Pendientes del ciclo completo

No se añaden rutas nuevas para cleanup, retention, undo o discard. Siguen pendientes los bloques de childLane, Weakpersistent, SEC-CONV, Native/Imported, manual/live y multiOS. El consumidor Electron queda sujeto al contrato de preview y `command.invoke.user`; no se construyó UI. La prueba no cubre delegación/creación end-to-end ni claims de cierre global.
