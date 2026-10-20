# Revisión y operaciones Git en la TUI después de M10

Decisión del usuario del 13 de octubre de 2026: construir el frame completo de revisión y operaciones Git después de M10. Este trabajo amplía la experiencia de TUI sobre el núcleo existente; no es un requisito nuevo para cerrar M8, M9 ni M10. La interfaz funcional, accesibilidad y traducciones de los recorridos ya asignados a esos hitos conservan sus criterios de aceptación.

## Ubicación en el plan

Bloque **Post M10 Git Review**, pendiente de implementación. Depende de las bases de integración, preimágenes, recuperación y protección de cambios ajenos de M7; del transporte y autoridad del Host de M9; y del producto undo, acciones, teclado y adapter OmniCoder de M10. No se crea un hito M11 ni se reabren los hitos cerrados por añadir esta superficie.

TUI-P2 conserva el visor histórico de efectos atribuibles a una sesión: sus pre/postimágenes en CAS no representan todo el estado Git actual. El nuevo frame distingue esa vista de la revisión del repositorio vivo, del índice y de las comparaciones entre revisiones.

## Experiencia de revisión

- Entrada desde el panel lateral y desde una acción o comando equivalente; frame propio con árbol de archivos, cambios, historial y ramas.
- Vista de diff unificada en línea y lado a lado, seleccionables por el usuario. Numeración, contexto, resaltado, navegación por bloque y desplazamiento sincronizado; adaptación a ancho y virtualización para diffs grandes.
- Comparación explícita de working tree, índice, HEAD, rama o commit, con base y destino visibles. Diferenciar cambios atribuibles al agente, cambios ajenos y atribución desconocida; no inferir autoría del estado Git.
- Selección por archivo o programa, bloque de cambios y línea para preparar, quitar del índice y descartar. Las selecciones inválidas, parciales no representables o basadas en versiones obsoletas tienen un diagnóstico, no una aplicación aproximada.
- Estado de operaciones, conflictos y recuperación dentro del frame; los detalles técnicos extensos se despliegan bajo demanda. Preferencias de vista e idioma se integran con el centro existente.

## Operaciones y seguridad

Ofrecer fetch, pull, push, commit selectivo, comparación de ramas, merge, stash y resolución de conflictos mediante acciones del Host, con disponibilidad acorde al repositorio y a la autoridad del usuario. Ver un estado o abrir el frame no ejecuta operaciones remotas ni prepara, integra o descarta cambios.

Toda mutación presenta alcance, base, versión y efectos antes de confirmar. El descarte requiere preimagen o respaldo recuperable y revalidación de la versión inmediatamente antes de aplicar. Una modificación posterior del usuario bloquea la aplicación obsoleta. Reutilizar mecanismos M7 donde su semántica corresponda; un descarte de worktree no sustituye el descarte selectivo de líneas de un archivo.

Pull y merge explican los cambios locales, operaciones pendientes y posibles conflictos. No introducir stash automático ni políticas de resolución silenciosa como consecuencia de abrir el frame. Continuar y abortar identifican la operación exacta. No usar reset destructivo como fallback ante un fallo de aplicación parcial.

La vista histórica de CAS permanece inmutable. Las acciones sobre el repositorio vivo conservan índices, archivos ajenos y cambios posteriores. No copiar directamente operaciones del antiguo GitWindow de OmniCoder que no cumplan el contrato de autoridad y recuperación del Host.

## Entregas

| Bloque | Construcción | Aceptación |
| --- | --- | --- |
| G1 Inspección y diff | Estado Git tipado, bases identificables, árbol, unificado/lado a lado y navegación. | Abrir y comparar son de solo lectura; cambios ajenos y estados sin evidencia aparecen diferenciados. Diffs grandes, renames, archivos sin seguimiento y binarios no rompen el visor. |
| G2 Cambios selectivos | Preparar, quitar del índice y descartar por archivo, bloque y línea con preview, versiones y respaldo. | Solo cambia lo seleccionado; modificaciones concurrentes se preservan; fallo o cancelación no se presenta como operación completa. Recuperación verificable en repositorios fixture. |
| G3 Ramas y remoto | Fetch/pull/push, commits, merge, stash y continuar/abortar conflictos sobre acciones autorizadas. | No hay operaciones implícitas al consultar; working tree sucio, conflictos y errores remotos tienen recorrido de recuperación. Pruebas con remoto bare local antes de una aceptación externa autorizada. |
| G4 Experiencia y paridad | Preferencias de vista, acciones/CLI equivalentes donde corresponda, teclado, es/en/fr/it y responsive. | Mismo efecto y misma autoridad desde cada entrada; capturas reales de TUI y validación de teclado, NO_COLOR/ASCII, selección y resize. No se exige replicar el layout en la CLI plain ni convertir MCP en una interfaz visual. |

## CLI MCP y otros clientes

Estos frentes existen antes de este bloque y no se trasladan después de M10:

| Superficie | Ubicación vigente |
| --- | --- |
| CLI y TUI | Ya existen sobre el runtime in-process. M9 conecta la CLI al Host separado; M10 completa CLI v1, acciones, keybindings/palette, búsqueda, undo y evaluación. |
| MCP y web mediante MCP | M8 incluye el bridge hacia servidores configurados, transportes stdio Strong y HTTP, confianza, permisos y tools web declaradas. No equivale a publicar OmniCore como servidor MCP universal ni a soportar toda revisión o capacidad MCP. |
| Transporte CLI y cliente hacia Host | M9: framing, negociación, lease, reconexión, persistencia de read models y supervisor por transporte real. El stdio de MCP no satisface este transporte propio. |
| OmniCoder | M9 conecta el cliente multi-target; M10 completa el adapter y sus recorridos. Los controles WPF y los componentes de presentación TUI son propios de cada cliente. |
| Extensiones y skills | M8 construye sus contratos y consumidores. M9/M10 conservan las integraciones y criterios de los recorridos nuevos; su interfaz no concede autoridad adicional. |

La referencia de implementación histórica es `OmniCoder/docs/GIT_FRAME_CHECKLIST.md`, `GitPanelViewModel`, `GitClient` y `GitWindow`. Antes de donar piezas se audita su código, licencia, granularidad y garantías: la existencia de un visor unificado o de descarte por archivo no acredita lado a lado ni descarte por línea.

## Estado

### Incremento nativo para OmniCoder 2026-10-20

El usuario autoriza una primera integración A–C sin adelantar el frame completo de revisión TUI. Orden: consultas de workspace/Git y contratos nativos; cronología estructurada y checkpoints privados ampliando GitWorktreeStore sin cambiar la semántica de Undo; alternativa en worktree/rama aislados con conversación y lineage; consumidor Electron y pruebas cruzadas. Core mantiene toda autoridad, persistencia y reglas; OmniCoder únicamente presenta y solicita acciones autorizadas. La alternativa no restaura servicios ni efectos externos. Ver aceptación coordinada y alcance en PLAN_REALISTA.md de OmniCoder.

Construido: contrato aditivo stdio 1.0 `workspace.panel` v1 (`query.workspacePanel`) y comando `conversation.alternative` con HMAC/Session/Run/recibo. Árbol diferido, búsqueda, Git/ramas/worktrees y diff acotados; timeline del journal con atribución; checkpoints Git con índice privado; worktree/rama y journal derivado con `ConversationHistoryImported`. El seed contiene sólo mensajes anteriores al corte y pares de herramientas completos, conserva el texto privado y se integra con compactación. La inicialización del journal privado es unpooled para que un rollback pueda retirar también una sesión ya persistida. Los intents recuperan preparaciones interrumpidas; un destino cambiado o no verificable se conserva con `alternative_cleanup_required`.

Aceptación final local 2026-10-20: build 0W/0E; **34/34 PASS** en WorkspacePanel, ConversationLifecycle, WorkspaceUndo y atribución de denegaciones por Lane; **1/1 PASS** de lifecycle stdio real HMAC/recibos/CAS. La prueba cruzada Electron→Host→HTTP programado aprobó **1/1 en 18.18 s**, incluida continuación con historial importado sin pares huérfanos y origen dirty/índice/HEAD intactos. El consumidor aprobó app 119/119, presentation 85/85 y scanner. Una regresión amplia inicial de 81 casos identificó la doble redacción del evento importado; se corrigió y se repitió el foco de 35 casos, no se afirma una nueva ejecución completa de esos 81. Sin inferencia live/pagada.

Límites: timeline 300 filas, directorios 512 entradas, búsqueda 200 rutas, texto 256 KiB; snapshots hasta 10 000 archivos/64 MiB y seed 384 KiB. Captura Git con HEAD, sin enlaces, conflictos, secretos por ruta ni filtros/transformaciones incompatibles; ignorados respetados. Alternativas multimodales no disponibles en v1, sin pérdida silenciosa de adjuntos. Las consultas no restauran nada; la instrucción opcional queda pendiente y la sesión derivada usa una política nueva. No se incorporan pull/merge/descarte granular ni plugins.

Referencias revisadas sin copiar código: [claude-code-filetree](https://github.com/data-goblin/claude-code-filetree), MIT, Kurt Buhler (2026); [Git Sidebar](https://github.com/davila7/claude-code-templates/tree/main/cli-tool/components/mods/ui/git-sidebar) y [Time Machine](https://github.com/davila7/claude-code-templates/tree/main/cli-tool/components/mods/productivity/session-time-machine), MIT, Daniel (San) Ávila (2025). Sus licencias y patrones se documentan en el plan de OmniCoder. No se instalaron ni ejecutaron sus hooks.

El frame TUI completo sigue planificado para después de M10; no se declara implementado por el incremento nativo de OmniCoder. Esta asignación no detiene el plan de preferencias aprobado por separado ni aplaza automáticamente la aceptación funcional de interfaces de M8–M10. El resto del trabajo futuro de TUI se inventariará por bloques explícitos, sin tratar todo el pulido visual como una condición indefinida para cerrar el núcleo.
