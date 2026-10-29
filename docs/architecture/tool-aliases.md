# Nombres de herramientas expuestos al modelo

OmniCore mantiene los IDs canónicos en el catálogo, permisos, journal y ejecución. La capa de proveedores presenta nombres con la convención de OpenAI/Codex. Responses, Chat Completions y Anthropic usan el mismo vocabulario; no se alterna entre `Read`/`Grep` y nombres de Codex según el proveedor.

La [guía oficial de Codex](https://developers.openai.com/cookbook/examples/gpt-5/codex_prompting_guide) documenta nombres como `read_file`, `list_dir`, `exec_command`, `write_stdin` y `apply_patch`. La tabla distingue las equivalencias de las operaciones específicas de OmniCore. Los alias nombran funciones de OmniCore: no activan herramientas alojadas de OpenAI ni sustituyen sus schemas JSON. En particular, `apply_patch` sigue recibiendo `path`, `expectedVersion`, `oldText` y `newText`; `update_plan` sigue proponiendo una mutación validada por el runtime.

| ID canónico | Nombre expuesto al modelo | Equivalencia o alcance |
| --- | --- | --- |
| `filesystem.read` | `read_file` | Lectura de archivo, convención Codex |
| `filesystem.list` | `list_dir` | Listado de directorio, convención Codex |
| `filesystem.search` | `grep_files` | Búsqueda de texto local; equivalente funcional de Grep/rg |
| `filesystem.patch` | `apply_patch` | Edición localizada; conserva el schema de OmniCore |
| `filesystem.write` | `write_file` | Escritura completa; operación de OmniCore |
| `media.read` | `view_image` | Lectura de imagen, convención Codex |
| `shell.exec` | `exec_command` | Comando interpretado por shell |
| `process.exec` | `exec_process` | Operación de OmniCore: ejecutable y argv literales |
| `process.start` | `start_process` | Operación de OmniCore: inicio persistente con handle |
| `process.read` | `read_process_output` | Operación de OmniCore: lectura paginada por secuencia |
| `process.send_input` | `write_stdin` | Entrada al proceso; la lectura se ofrece por separado |
| `process.stop` | `stop_process` | Operación de OmniCore: detención explícita |
| `plan.propose` | `update_plan` | Mutación de plan validada por el runtime |
| `user.ask` | `request_user_input` | Pregunta estructurada al usuario |
| `tool.search` | `tool_search` | Descubrimiento de herramientas |
| `search.web` | `web_search` | Búsqueda web; función de OmniCore con presupuesto compartido |
| `web.read` | `web_fetch` | Función de OmniCore: lectura, evaluación y estado de evidencia web |
| `artifact.read` | `read_artifact` | Operación de OmniCore |
| `reference.resolve` | `resolve_reference` | Operación de OmniCore |
| `skill.load` | `load_skill` | Operación de OmniCore |
| `memory.propose` | `propose_memory` | Operación de OmniCore |
| `mode.propose` | `propose_mode` | Operación de OmniCore |
| `companion.pet` | `companion_pet` | Operación de OmniCore |
| `core.agents.mailbox.receive` | `receive_agent_messages` | Operación de OmniCore |
| `worktree.integrate` | `integrate_worktree` | Operación de OmniCore |
| `extension.batch.apply` | `apply_extension_batch` | Operación de OmniCore |
| `dyn.core.verify_integration` | `dyn_core_verify_integration` | Workflow dinámico; conserva su namespace |

Acciones reservadas al usuario nativo, cuando están registradas: `worktree.cleanup` → `cleanup_worktree`, `worktree.discard` → `discard_worktree` y `worktree.retention` → `set_worktree_retention`. Un alias no las habilita para el modelo.

Las herramientas MCP, de extensiones y de workflows mantienen su namespace legible: por ejemplo, `mcp.docs.web.search` → `mcp_docs_web_search`. El catálogo efectivo depende de configuración, perfil, permisos y herramientas descubiertas; la tabla no significa que todas se expongan en cada request.

`ToolNameWireMap` aplica la misma asignación a definiciones, selección forzada e historial estructurado. Restaura el ID canónico al recibir llamadas, tanto en streaming como en la respuesta final. También adapta referencias completas a herramientas Core en instrucciones y descripciones; no cambia texto del usuario, argumentos ni resultados. Los nombres válidos ya registrados se reservan primero; colisiones o exceso de longitud reciben un sufijo determinista y reversible. Los límites son 64 caracteres en OpenAI y 128 en Anthropic. `search.text` queda únicamente como compatibilidad histórica de permisos, no como alias nuevo al modelo.

Verificación del cambio de nombres: compilación de `OmniCore.Tests` con cero advertencias y errores; **70/70** pruebas seleccionadas de `ToolNameWireMapTests`, `OpenAIResponsesProviderTests`, `AnthropicMessagesProviderTests`, `OpenAiChatCompatibleMediaSerializationTests` y `WebResearchGenerationRetryTests`. Se verificaron colisiones, límites, selección forzada, historial, instrucciones/descripciones y restitución canónica, usando transportes ficticios sin llamar APIs reales.
