# M10 — cambiar apertura de paleta a Ctrl+P

Cambio acotado solicitado por Juan, exclusivamente en Zephyrus (`ZEPHYRUSG14`)
y el worktree `openrouter-catalog-grid/OmniCore`, rama `codex/m10-keybindings`.

## Cambio y colisiones

Una única definición de producción cambia Ctrl+Shift+P por Ctrl+P para
`palette.open`; se conserva `!overlayOpen && !awaitingPermission`. La ayuda de
bindings obtiene su etiqueta del motor. ADR, ejemplo YAML y acta de la paleta
reflejan el atajo actual; `/paleta` y F3 conservan sus rutas.

No existía otra asignación propia de OmniCore para Ctrl+P ni un keybindings.yaml
en la ruta User efectiva de esta máquina. Tampoco se detectaron overrides User o
Machine OMNICORE_CONFIG_DIR/OMNICORE_DATA_DIR. No se crea ni reescribe configuración
personal. El editor conserva la navegación por flechas. La precedencia existente
Priority/Source permanece intacta; se prueba con un archivo User temporal que una
asignación Ctrl+P a sidebar.toggle gana al default sin cambiar sus bytes, y que
/paleta sigue abriendo la paleta. La reasignación de palette.open a F9 sigue válida.

## Archivos cambiados

- `src/OmniCore.Client/Input/KeyBindingModel.cs`: definición predeterminada.
- `tests/OmniCore.Tests/TuiWiringTests.CommandPalette.cs`: inyección real Ctrl+P,
  retirada del default anterior, respeto a User y conservación de adjunto/borrador.
- `tests/OmniCore.Tests/KeyBindingServiceTests.cs`: las pruebas genéricas de
  predicados/timeouts usan F8 para mantenerse independientes del nuevo default.
- `docs/adr/0025-client-actions-keybindings.md`, `docs/examples/keybindings.yaml`
  y `docs/validation/m10-global-command-palette-20261026.md`: documentación vigente.
- Esta acta.

## Validación terminada

- Solución Release: cero errores/advertencias, incluidos Client/Protocol net8/net10.
- **72 PASS, 0 FAIL, 0 SKIP** de motor, configuración, proyección de paleta y aliases.
- **27 PASS, 0 FAIL, 0 SKIP** de TUI: Ctrl+P abre la paleta real, Ctrl+Shift+P ya no
  abre por default, Enter/Escape mantienen rutas, cuatro idiomas, remap User y su
  prioridad sobre Ctrl+P, texto/adjunto intactos sin nuevo input ni command, permisos,
  scope, cancel y regresiones M10-B y F3/Ctrl+M.
- **62 PASS, 0 FAIL, 0 SKIP** de arquitectura.
- Logs `%TEMP%/omni-palette-ctrl-p-{build,client,tui,architecture}.log`.

No se tocan M9 (`f21ad93`, archivo no seguido x conservado), OmniCoder, permisos
persistentes ni tareas de soporte; no se publica, hace push o merge. La instalación
conserva el procedimiento de prefijo de versión y backups de launchers que dejó
M10-C, sin cerrar ventanas/procesos y verificando publicación antes de cambiar
los accesos. Las pruebas de driver no sustituyen aceptación humana de terminal.

## Instalación verificada

Código `8868313`; ProductVersion
`0.1.0+886831317186e07cda14890b553944d557eea9cc`.
Prefijo `%LOCALAPPDATA%/OmniCore/cli/releases/8868313/`. Antes de cambiar accesos
se verificaron los **138 archivos** de la publicación por SHA-256 (cero diferencias)
y `--help` del binario de versión. Se preservaron los accesos anteriores en
`previous-launchers/` y se regeneraron omni/omni.cmd/omni.ps1 con cmd-shim de npm.
Los accesos públicos CMD y PowerShell pasan `--help`, código 0. El paquete activo
anterior y sus copias no se eliminaron; no se cerró ninguna ventana/proceso.
Manifest `%TEMP%/omni-palette-ctrl-p-install-verification.json`; paquete local y
SHA-256 registrados en `%TEMP%/omni-palette-ctrl-p-package.json`. Sin publicación
remota. Se requiere reabrir omni para cargar el atajo nuevo.
