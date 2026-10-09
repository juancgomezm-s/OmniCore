# Integración y revisión del cableado — 2026-10-09

Revisión posterior al [cierre integral de M6](m6-integration-20261008.md#cierre-de-m6-en-windows), solicitada para integrar el avance en `main` y comprobar las entradas reales. La revisión raíz y dos workers Luna xhigh revisaron rutas de producción y corrigieron los hallazgos descritos abajo.

## Integración recuperada

`main` ya contenía los avances de los worktrees `codex/rescue-glm-20261008` (`ab1837f`) y `codex/tui-markdown-polish` (`06c5781`). Ambos tips son ancestros de `6ce2581`; sus checkouts estaban limpios y se conservaron. Tras actualizar las referencias remotas, `origin/main` era ancestro de `main`, con 31 commits locales de avance. Los respaldos históricos no se fusionan indiscriminadamente: su reconciliación y equivalencia de árboles están registradas en [el acta anterior](branch-integration-20261008.md).

La rama `codex/provider-connections-claude-20261008` conserva `c89625c` y `5f30718` fuera del historial de `main`. Su recuperación fue selectiva en `ab1837f`, como documenta [el acta de rescate](glm-main-rescue-20261008.txt): API backend, fachada y helpers de cuenta están incorporados. El antiguo renderer de conexiones tenía fallos de menú y una dependencia prohibida de Domain; esta revisión lo sustituye con el menú API probado, sin restaurar sus regresiones. El adaptador headless `ClaudeAccountDelegationService` queda preservado en aquella rama, sin importarse como código sin consumidor.

## Hallazgos corregidos

1. **Entrada CLI y límites de argumentos.** `CliApp.RunTypedCommand` unía los argumentos recibidos del proceso y los volvía a parsear como una línea TUI. La shell ya había quitado las comillas; por ello un objetivo, ruta o JSON con espacios perdía sus límites. `CommandLineParser.TryParseArguments` conserva los tokens originales y los comandos canónicos (`/core:...`). El modo de una sola cadena conserva el parser de texto existente. La vertical Explore/Implement/Verify ahora entrega un `string[]` tokenizado, igual que `Program.cs` a `CliApp.RunAsync`.
2. **Conexiones API recuperadas sin entrada de usuario.** `TuiProviderConnectionHost` y el servicio de conexión estaban construidos, pero ninguna pantalla de producción los consumía. La entrada TUI conecta esta fachada al menú de configuración. Las acciones de guardar, probar, descubrir y desconectar pasan al Host; el renderer no administra el almacén de credenciales.
3. **Descubrimiento de comandos.** `/fanout` tenía handler y descripción, pero faltaba en las sugerencias de comandos de la TUI. Se incorpora al autocompletado. El catálogo también expone el motivo de deshabilitación de un workflow que todavía no tiene autorización vigente.
4. **Destino de la API key.** La fachada inicial no recibía el ID de la fila seleccionada y el servicio guardaba siempre en la referencia fija `anthropic`. Con varias conexiones o un `authRef` personalizado, la clave podía quedar asociada a otra conexión o no ser utilizable por la seleccionada. La conexión debe propagar el ID exacto, validar ese endpoint y guardar sólo en su referencia configurada; la regresión cubre destinos y referencias distintos.
5. **Conexiones Anthropic con otro método de autenticación.** Una fila `AnthropicMessages` con `auth:none` contaba como conexión API configurada y ocultaba la fila sintética. El criterio común requiere familia Anthropic, autenticación API key y referencia no vacía. Una conexión existente con otro método se conserva mientras se agrega una conexión API independiente. Los IDs y referencias nuevos evitan colisiones con la configuración existente.

## Recorridos revisados

| Funcionalidad | Recorrido de producción |
| --- | --- |
| Workflow tipado | `Program` / TUI → `CliApp` / `command.invoke` → `CommandService` → `OmniCliRuntime.ExecuteWorkflowAsync` |
| Explore / Implement / Verify | Runner → admisión `delegation.create` → `DelegationExecution` → herramientas de etapa y verificador Dynamic → supervisión, disposición y gate del Plan |
| Capacidad y presupuesto | Turn raíz y ejecución delegada → `AgentCapacity` y reserva jerárquica del pool; lectores en paralelo, escritoras exclusivas |
| Mailbox | Catálogo de herramientas de la Lane → callback de recepción del runtime → boundary del Tool Runtime → entrega, ACK, Wake y recuperación del Host |
| FanOut y joins | Handlers TUI → comandos Host → `FanOutGroups` / `DelegationControl` → reconciliación durable |
| Observabilidad | `OmniServer.Query("agents")` → `AgentLaneReader` → proyecciones Client → `/agents` e inspector de Lane |
| Conexiones API | TUI de producción → `ITuiProviderConnectionHost` → servicio, almacenamiento protegido y registro de modelos del Host |

La búsqueda de callsites también identificó adaptadores públicos de compatibilidad sin consumidores internos actuales, como `CommandService.Expand` y `HostTools.DelegatedReader`. Las rutas operativas usan `InvokeAsync` y `DelegatedReaderWithMailbox`; no representan funcionalidades pendientes de activación. Se conservan los contratos públicos.

## Límites

La vertical del workflow usa un provider HTTP scripted y filesystem/verificador de proceso reales en un workspace temporal. Los argumentos entran por `CliApp.RunAsync`; la prueba no lanza otro proceso de la CLI. Los hooks de sandbox del fixture no acreditan un proveedor autenticado ni sandbox Strong nuevo. Las pruebas TUI ejercitan controles y polling reales de Terminal.Gui; los servicios inyectados deben distinguirse de las comprobaciones con el backend real.

Las lanes externas de Claude Code siguen asignadas a M7 por ADR-0012. El nuevo diseño OAuth de Claude, la aceptación manual local de M2/M3, Linux y los milestones M7–M10 siguen pendientes. Los tres documentos OAuth locales ajenos a esta revisión se preservan fuera de los commits.

Existe una pieza construida sin entrada de usuario en `main`: `ClaudeAccountService` y su factory `OmniHost.CreateClaudeAccountService`, rescatados como helpers del CLI oficial, tienen pruebas pero no consumidores en CLI/TUI. Se registra como pendiente de la integración de cuenta, no como conexión de suscripción terminada ni como parte del workflow M6. La integración OAuth nativa tiene un diseño distinto; abrir el CLI oficial no satisface ese plan. Por tanto, esta auditoría acredita los recorridos de M6 y la conexión API, pero no afirma que todo contrato futuro del repositorio tenga una entrada operativa.

## Revisión adicional con Haiku 5.5

Claude Code informó autenticación `claude.ai` activa. Se delegó la revisión de un snapshot textual del diff al modelo exacto `claude-haiku-5-5`, confirmado por `modelUsage` en el resultado, con `--print`, `--restricted`, `--safe-mode`, `--strict-mcp-config`, sin herramientas y sin persistencia de sesión. La ejecución terminó exit0, `is_error:false`. Prompt y resultado: `C:/Users/juanc/.codex/m6-wiring-haiku-{prompt,result}-20261009.{txt,json}`. No se suministraron datos de cuenta ni credenciales reales; las claves del diff son fixtures.

La revisión propuso tres hipótesis. La comprobación sobre el código completo confirmó el bloqueo de la opción API cuando existe Anthropic con `auth:none`, que se corrigió. El reporte había atribuido incorrectamente `CanConnect:true` a esa fila; la causa real era el criterio para ofrecer la fila sintética. Las referencias de credencial compartidas explícitamente por la configuración conservan su semántica compartida. Una cancelación después de crear el descriptor y antes de guardar la credencial puede dejar una conexión `NotConfigured`, recuperable al reabrir el menú; no se añade una garantía de transacción entre YAML y el credential store ni se ignora la cancelación para forzar el guardado. La cancelación previa sí se prueba sin escrituras de configuración o credenciales.

## Verificación de esta revisión

Commit de implementación: `db756dcb3302d902bf0c941cc05ce1aba2c6fe23`, integrado directamente en `main`. Árboles Git: `src` = `2ae433111bd2aa397229cbd6479ba1f6541058da`; `tests` = `5b25bbc0026035584b6be625795764bb74bc6641`. SDK 10.0.401, runtime 10.0.12, Windows. Las fuentes y los binarios se mantuvieron congelados durante la verificación final; los hashes de los cuatro binarios coinciden antes y después de la suite.

| Comprobación | Resultado | Log en `C:/Users/juanc/.codex/` |
| --- | --- | --- |
| Build de la solución, `--no-restore -v quiet` | exit0, 0 advertencias/errores, 23.77 s; targets net8/net10 | `m6-wiring-build-complete-20261009.log` |
| Arquitectura, runner net10 serial | 56 PASS, 0 FAIL/SKIP, 0.497 s, exit0 | `m6-wiring-architecture-complete-20261009.log` |
| Client + API/fachada de conexiones, runner net10 serial | 52 PASS, 0 FAIL/SKIP, 0.986 s, exit0 | `m6-wiring-client-provider-complete-20261009.log` |
| Suite integral, runner net10 serial | 3296 casos: 3292 PASS, 0 FAIL, 4 SKIP, 0 Not Run; 407.125 s, exit0 | `m6-wiring-full-20261009.log` |

Los checkpoints preliminares también incluyeron 31 PASS de `CliEndToEndTests`, 82 PASS de la TUI antes de la corrección de selección exacta y 5 PASS de las nuevas pruebas TUI después de esa corrección. El ajuste posterior del criterio API para `auth:none` se verifica con el último build, focales y suite integral; los checkpoints anteriores no se presentan como una pasada del commit final.

La pasada integral final incluye la vertical CLI, todas las pruebas TUI y la recuperación M4 en ejecución serial. Los cuatro SKIP corresponden a restricciones de enlaces simbólicos en Windows: `SecurityP0Tests.Read_of_a_link_pointing_to_env_is_denied`, `SecurityP0Tests.Symlink_chain_escaping_the_workspace_is_outside`, `SecurityP0Tests.Symlink_cycle_is_unresolvable_and_outside` y `FileArtifactStoreTests.GetText_is_null_when_blob_is_a_symlink_outside_blobs`.

No quedan hallazgos P1/P2 de cableado abiertos en los recorridos revisados. M6 conserva su cierre en Windows, con la entrada de conexiones y las regresiones integradas en `main` local. Esta revisión no publica cambios en `origin/main`; los pendientes descritos en Límites mantienen su estado.

SHA256 comprobados antes y después de la suite integral:

| Binario net10 Debug | SHA256 |
| --- | --- |
| `OmniCore.Tests.dll` | `F2E74354E6E3F3BAF2F5AFDF16ED0D6DB665A79F2E2E3979D090D5AF511C0ABA` |
| `OmniCore.ArchitectureTests.dll` | `7D720F131C2FB955BB46418387D03672A5084A695F48953AA27D9C3444693B7F` |
| `OmniCore.Host.dll` | `CE0F581933BAFF88AC1383B882368AF0B6D5F49C0B0D785D13289E3937EC1A89` |
| `omni.dll` | `7DD79443505BE775CC84171F773893B40607A56CAC74EE6FAF082C6E4395E71F` |
