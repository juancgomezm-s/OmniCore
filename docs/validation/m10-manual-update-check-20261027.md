# M10-H — consulta manual de releases (2026-10-27)

## Alcance y reparto

El usuario autorizó continuar un corte independiente de M10, delegando el trabajo
principal a Luna y revisando la entrega sin interferir con M9. Este corte inicia
la consulta manual del canal descrito en `docs/plans/github-updates.md`: CLI, vista
TUI y servicio de lectura común. Aplicación, descarga de paquetes ejecutables,
rollback, recuperación del instalador, publicación y verificación del publicador
conservan su alcance pendiente; no se presentan como operativos por consultar.

La consulta usa el repositorio oficial `juancgomezm-s/OmniCore`, canal stable por
defecto y preview por elección explícita. Abrir preferencias, paleta o la vista no
contacta GitHub; sólo una comprobación humana explícita inicia la lectura acotada
de metadatos. No se usan credenciales de proveedores, tokens GitHub o cookies.

La presentación separa versión instalada, build de desarrollo, versión candidata,
compatibilidad conocida/desconocida y verificación pendiente de paquete/publicador.
No se deduce un commit fijado de `target_commitish: main`, ni integridad o identidad
del publicador de un digest declarado. Una búsqueda limitada tampoco acredita
haber encontrado la versión más reciente de todo el catálogo.

Luna tiene a cargo implementación principal y pruebas focales; la sesión raíz
revisa el diff y añade verificaciones cuando las evidencias dejan un hueco. La
entrega del obrero no se acepta automáticamente. La instalación local, si procede,
se gestiona desde la raíz con una release independiente y backups de launchers.

## Coordinación

M10 trabaja en `openrouter-catalog-grid/OmniCore`, rama `codex/m10-keybindings`.
M9 sigue en `m9-completion/OmniCore` con el recorrido real TUI→OmniCoder de hijo,
disposición e integración de worktree. Al inicio de este corte mantiene cambios
propios en `OmniServer.Conversations.cs`, `StdioProtocolHost.cs`, el nuevo
`WorkspaceConversationImportSource.cs` y pruebas. Esos archivos, sus datos,
procesos, artefactos, instalaciones y el consumidor OmniCoder quedan fuera del corte.

El servicio de consulta no amplía stdio, admisión humana, permisos, receipts,
herramientas del modelo o seguridad de Session. Conserva la experiencia de los
menús existentes y añade un acceso concreto. Las pruebas usan temporales y HTTP
controlado, sin publicar releases o iniciar proveedores.

## Fuentes primarias consultadas

Se verificaron los contratos de lectura y campos en la documentación de
[GitHub Releases](https://docs.github.com/en/rest/releases/releases?apiVersion=2022-11-28),
la descarga de metadatos de [release assets](https://docs.github.com/en/rest/releases/assets?apiVersion=2022-11-28),
y los estados/límites en las [prácticas de REST](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).
La separación entre metadatos y pruebas de procedencia sigue el alcance de
[releases inmutables](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).
La precedencia de versiones se contrasta con [SemVer 2.0](https://semver.org/).
Estas fuentes describen los servicios; no acreditan por sí mismas la implementación.

El [ejemplo v1](../examples/release-manifest-v1.json) contiene versión, hash, tamaño
y commit ficticios; no corresponde a una release publicada, no acredita un paquete
y no se debe distribuir como manifiesto de producción. Sus campos permiten revisar
el contrato de metadatos junto con las pruebas. Los rangos son declaraciones de la
release: no establecen por sí mismos el esquema/configuración observados del usuario.

## Entrega y validación

Integrado en `main` por el merge `754559b` (rama `codex/m10-keybindings`, commit
`7ca860f`). `dotnet build OmniCore.slnx` terminó con 0 advertencias y 0 errores.
Pasaron 37/37 focales de la consulta (26 de checker/CLI y 11 de wiring TUI) y
62/62 de arquitectura. Corrección de evidencia del 2026-10-27: `omni update check`
sí consulta la red por petición humana explícita. Sólo las opciones inválidas
responden con el uso antes de consultar. La lectura real inicial respondió
`github_rate_limited`; la revisión posterior encontró y corrigió que una cabecera
normal `X-RateLimit-Reset` en HTTP 200 se confundía con rechazo. La evidencia del
corte posterior figura en [controles de ejecución](m10-execution-controls-20261027.md). El paquete npm local se reinstaló y `omni --help` lista el comando
(`c9ea25d`). No se publicó release, no se descargó ni aplicó paquete y no se
verificó firma de publicador.

## Pendientes del hito

M10 permanece abierto por memoria multiscoped, búsqueda más amplia, undo,
evaluación full, LSP/parches múltiples y las fases restantes del actualizador.
El cierre de M9 y las aceptaciones manuales conservan su propia evidencia.
