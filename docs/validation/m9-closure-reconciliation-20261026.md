# Reconciliación acotada de cierre M9 Core — 2026-10-26

Base inspeccionada: `2624af658321bb87ffd96f5e9a7795fe00665411` en el worktree M9 local. Esta nota compara los criterios M9 vigentes con las actas aceptadas y el contrato/código Core. No es cierre global de M9, no reabre los bloques aceptados y no acredita GUI, proveedor live, aceptación manual, plataformas diferidas ni publicación.

## Resultado

Los cortes Core M9-L/M y SEC-CONV trabajados en este ciclo tienen evidencia local aceptada. En la base inspeccionada, el hueco Core explícito era construir y conectar la ruta humana `!comando` definida por UX-03 y P0-COMPOSER-SHELL-NATIVO. El presente corte implementa el contrato Host y lo conecta con CLI y el composer TUI; su evidencia y límites están en [el acta de `!comando`](m9-composer-shell-native-20261026.md). Esto no cierra M9 global ni los gates de GUI visual, aceptación manual, proveedor live o plataformas diferidas.

El Core actual ofrece `command.invoke.user`, que invoca un comando Host catalogado dentro del pipeline confiable de conversación, y `process.control`, que autoriza controles tipados sobre un proceso ya iniciado. `process.start` y `shell.exec` son herramientas ejecutadas por ToolRuntime. Ninguno de esos hechos demuestra la ejecución de un comando `!` sin LLM. En particular, `command.invoke.user` puede recorrer proveedor/tools y `process.control` no crea procesos.

La construcción debe reutilizar `shell.exec` y su `ToolRuntime`/claims/policy/sandbox actuales mediante una acción humana tipada y firmada. La entrada no fabrica una inferencia ni un resultado de modelo, no incorpora la salida al contexto automáticamente y no relaja Deny, PLAN, ObserveOnly, Ask, WeakConsent, grants ni requisitos de lectura. Las rutas TUI/CLI envían el mismo command Host; el Host liga la admisión y el resultado a la Session/Run vigentes, captura cwd/scope/revisiones y mantiene recibos/estado incierto. Este bloque no implementa PTY interactiva ni aceptación visual/manual/live.

## Matriz de evidencia Core

| Criterio | Evidencia existente | Estado y límite relevante |
|---|---|---|
| M9-M: proceso persistente, ownership y controles | [Contrato de procesos Core](m9-core-process-contract-20261023.md), [Weak Windows](m9-weak-persistence-20261024.md), [controles de procesos hijos](m9-child-process-controls-20261023.md) | Aceptado localmente en Windows. Strong/Weak conservan barreras separadas; los controles del hijo corren en su Turn/ToolRuntime autorizado. No reabrir por el texto histórico de Lane raíz ni presentar otros OS como cubiertos. |
| M9-M: publicación privada de efectos | [Contrato Core de proceso](m9-core-process-contract-20261023.md), acta del incremento de publicación | Staging privado, prelectura, política, claims, historia y lease; queries no aplican. No equivale a ejecución humana `!comando`. |
| M9-L: lifecycle, workflow y worktrees | [Lifecycle](m9-worktree-lifecycle-20261023.md), [disposition](m9-worktree-disposition-20261024.md), [retention](m9-worktree-retention-20261024.md), [workflow provision](m9-worktree-workflow-provision-20261024.md) | Aceptaciones locales separadas. Provisión desde workflow genera ownership sin sembrarlo; el foco stdio de esa provisión es framed in-process y no crea `/delegate` genérico. |
| M9-L: terminación entre efecto Git y resultado | [retention crash](m9-worktree-retention-crash-20261024.md), [cleanup/discard crash](m9-worktree-disposition-crash-20261024.md) | Aceptado localmente; Unknown/Unresolvable y bloqueo de continue son fail-closed. La barrera es crash controlado, no prueba de crash aleatorio ni atomicidad Git/SQLite. No reabrir este bloque. |
| Native/Imported | [importación neutral](m9-native-import-20261024.md), [procesos OS](m9-native-import-processes-20261024.md) | Formato canónico, ObserveOnly/ImportReview y replay receipt aceptados; no GUI, formatos de terceros ni proveedor live. |
| SEC-CONV-01–13, contribución Core | [matriz SEC-CONV](m9-sec-conv-core-20261024.md), [dispatch Direct](m9-sec-conv02-dispatch-20261024.md), [recuperación Discovered](m9-sec-conv02-discovered-20261024.md) | Snapshots, aislamiento de grants y recuperación Discovered tienen aceptaciones locales con límites por acta. No se declara SEC-CONV completo: sus gates de GUI/manual/live/plataformas siguen aparte. |
| UX-03 / `!comando` | [Directiva UI-OWNERSHIP §6 y UX-03](../plans/m8-m10-user-localization.md#directiva-m9-m10-ui-ownership--un-producto-interfaz-preservada-2026-10-23); P0-COMPOSER-SHELL-NATIVO y T18.3/T18.5 en `C:\Users\juanc\Documents\JayCoder\PLAN_REALISTA.md`; [acta de implementación Core](m9-composer-shell-native-20261026.md) | Host/CLI/TUI están construidos en esta candidata. La aceptación de proceso real, CLI in-process y despacho del composer se especifica en el acta. No demuestra aceptación visual/manual, proveedor live ni compatibilidad de terminal interactiva multiplataforma. |

## Superficie actual comprobada por inspección

- `command.invoke.user`: autenticación/firma, catálogo Host, fingerprint y CAS de política/modelo; describe invocar un comando exacto del Host en el pipeline confiable de conversación (`src/OmniCore.Protocol/StdioProtocol.cs`, descriptor de línea `command.invoke.user`; implementación Host en `OmniServer.NativeCommands.cs`). No certifica una invocación de shell libre sin modelo.
- `process.control`: comando/capability firmado para `process.read`, `process.send_input`/EOF y `process.stop` de un handle scoped existente; no lanza el proceso (`StdioProtocol.cs`, descriptor `process.control`; `OmniServer.NativeCommands.cs`).
- `process.start` y `shell.exec`: herramientas de catálogo invocadas por ToolRuntime dentro de un Turn y su política. Su existencia queda expresamente excluida como evidencia de UX-03 por el plan.

## Contrato de construcción existente

El command de `!` es una invocación de shell humana directa; el Host la liga a la Session/Run actual y la procesa con el descriptor, claims y evaluador del tool `shell.exec`. El caller tiene que usar la admisión humana firmada y los checks de revisión/catálogo/scope actuales. Los procesos no reciben claves ni entorno no permitido, no producen requests al proveedor y su salida no se convierte en input hasta que una acción explícita lo indique. Ask/WeakConsent usan las interacciones y grants existentes. Los estados aceptados/deferred/Unknown siguen los recibos actuales y no reejecutan automáticamente efectos inciertos. No se crea autoridad de shell paralela.

La nota anterior describía incorrectamente la brecha como falta de decisión semántica. Se corrige: el hueco era construcción/wiring del contrato existente. M10 no tiene una dependencia necesaria identificada en este corte.
