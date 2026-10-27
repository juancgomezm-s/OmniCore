# M10-E — búsqueda de la conversación cargada (2026-10-26)

## Alcance

`session.search` es una acción cliente disponible en la paleta global y mediante
Ctrl+F, reasignable desde `keybindings.yaml` User. No interpreta slash, shell o
expresiones regulares. Usa una búsqueda literal sin distinguir mayúsculas; conserva
texto original, offsets y la identidad del bloque de presentación.

La búsqueda opera exclusivamente sobre una captura de `ClientState.Conversation`
de la Session/Run actual. No consulta otras sesiones, artifacts, tools, providers,
directorios o servicios, y no recupera contenido excluido por `ClientProjection`.
La pantalla indica expresamente «Sólo texto cargado». No equivale a un índice de
todo el journal ni a una búsqueda entre sesiones.

Los resultados muestran rol, número de mensaje y fragmento. El detalle conserva
el fragmento original y la procedencia Session/bloque; no afirma que cada bloque
sea un evento nativo nuevo, ya que la proyección también puede contener historial
importado y resúmenes locales. Las reclamaciones assistant se identifican como
texto que no acredita verificación. Los estados de herramientas siguen siendo los
resúmenes existentes, no una nueva evaluación de sus efectos.

Límites por búsqueda: 256 caracteres de consulta, 200 coincidencias, 2 millones
de caracteres examinados y 10 000 bloques. Al alcanzar un techo se informa búsqueda
limitada; no se presenta el recuento parcial como total. Los fragmentos respetan
pares UTF-16 y conservan el contenido sin normalizarlo.

↑/↓ eligen resultados. Enter revalida Session/Run, interacción pendiente y la
identidad/texto del bloque contra la proyección actual, y navega al inicio del
mensaje en el lector existente. Una transcripción que crece no invalida mensajes
anteriores sin cambios; para buscar también en el texto nuevo se reabre la búsqueda.
Esc vuelve al composer. Abrir, filtrar y navegar no envían comandos ni alteran el
journal, el borrador, los adjuntos o las preferencias. Una aprobación o formulario
pendiente mantiene su foco y sus opciones del servidor.

## Coordinación con M9

Trabajo en `codex/m10-keybindings`, worktree `openrouter-catalog-grid/OmniCore`.
Implementación en `OmniCore.Client/Input/ConversationSearch*` y
`OmniCore.Cli/Tui/TuiApp.ConversationSearch.cs`; conexión pequeña en catálogo,
keybindings y localización. No se cambian Host, Protocol, permisos, admisión humana,
recibos, shell, transporte ni el consumidor OmniCoder.

M9 continúa en `m9-completion/OmniCore`, con nuevas modificaciones en
`HumanShellProcessTool.cs` y su archivo preexistente `x`. No se escriben ese árbol,
Documents/JayCoder, sus artefactos o instalaciones. Build y capturas usan rutas de
este worktree y temporales propios. La instalación CLI se realiza desde este corte
con versión independiente y backups de launchers; no se cierran procesos del usuario.

## Validación

Build Release de `OmniCore.slnx`, Client/Protocol/Sandbox net8 y net10 incluidos:
0 advertencias, 0 errores. Pruebas seriales desde los assemblies producidos:

| Grupo | Correctas | Fallos | Omisiones |
|---|---:|---:|---:|
| Búsqueda portable, recursos, paleta, configuración/dispatcher/motor de teclado | 81 | 0 | 0 |
| TUI búsqueda/paleta/keybindings y comandos Host | 42 | 0 | 0 |
| Arquitectura | 62 | 0 | 0 |
| Total focal | 185 | 0 | 0 |

Se verifican texto/offsets originales, pares UTF-16, consultas literales, límites,
contenido child/ambiguous excluido por la proyección, recursos es/en/fr/it, atajo
efectivo y reasignación User. En la TUI real: entrada desde Ctrl+F/paleta/atajo User,
vacíos/consulta larga, flechas, navegación, crecimiento de transcripción, cambio de
Session, aprobación pendiente, borrador y adjuntos, y journal sin cambios por buscar.
Se revalidan también los recorridos existentes de paleta e interrupt/cancel.

Logs: `%TEMP%/omni-m10e-final-{build,client,tui,architecture}.log`.
`git apply --check` del diff acumulado desde `ffa96af` en archivos compartidos
con M9 pasa sobre su checkout actual. Es una comprobación de compatibilidad de
hunks; no aplica cambios, no acepta la integración y no sustituye sus pruebas.
No se afirma una pasada full ni cierre del hito.

## Instalación local verificada

En ZEPHYRUSG14, `2026-10-26T21:37:58-06:00`, se instaló
`0.1.0+d4713d3eb1bb04ec458505d0c444e6a74f1d5e76` desde este worktree en
`%LOCALAPPDATA%/OmniCore/cli/releases/d4713d3/node_modules/omnicore-cli`.
Los 138 archivos de `dist/npm` coinciden por SHA-256, sin faltantes ni diferencias.
ProductVersion coincide con el commit fuente; `--help` del publish, del assembly
instalado y de los launchers públicos CMD y PowerShell termina con exit 0.

Paquete `%TEMP%/omnicore-m10e-90bf30fd458d4e8d9fdeb5a6bad39cf3/omnicore-cli-0.1.0.tgz`,
SHA-256 `6F9A2EAF6DB1F19E16B104CA9F2D21884F4DF1CCDB3C8AEF1683E5E900D6DAD7`.
La raíz de release conserva `installation-verification.json`, `package-manifest.json`
y `publish-hashes.json`. Los launchers públicos `omni`, `omni.cmd`, `omni.ps1`
apuntan a esta versión mediante el `cmd-shim` oficial de npm. Antes del cambio se
verificó que seguían apuntando a `78acf07`; sus copias están en
`%LOCALAPPDATA%/OmniCore/cli/releases/d4713d3/previous-launchers`.

No se sobrescriben DLL cargadas ni se cierran procesos del usuario. La nueva
versión se usa al abrir otra ejecución de `omni`. La instalación termina y libera
el turno sobre los launchers compartidos; no modifica instalaciones ni artefactos
de M9. La documentación posterior no cambia el commit fuente instalado.

Capturas de driver real RGB en `%TEMP%/omni-m10e-frames`, escenas
`conversation-search-{es,en,fr,it}`, tamaños 80×25 y 130×38. SVG rasterizado con
`tools/render-tui-frames.cjs`, revisado visualmente. Esto es evidencia del driver,
no aceptación interactiva manual del usuario ni validación de fuentes de emoji.

## Pendientes del hito

Este corte no cierra M10. Permanecen búsqueda más amplia con contratos explícitos
de lectura, diagnósticos restantes, descubrimiento automático de contribuciones de
bindings, memoria multiscoped, undo, LSP/parches múltiples, evaluación y actualizador
GitHub según el plan. M9 conserva sus pendientes y aceptación independientes.
