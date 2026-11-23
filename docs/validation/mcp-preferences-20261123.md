# Configuración MCP desde la TUI · validación local

Fecha: 2026-11-23. Entorno: Windows x64, SDK .NET 10.0.403.
Checkout: `C:/Users/juanc/.codex/worktrees/m10-completion/OmniCore`.

## Resultado funcional

La entrada de Ajustes → Extensiones y habilidades → Servidores MCP y `/mcp`
abren gestión de configuración, en lugar del informe de sólo lectura.
Permite añadir/editar/quitar conexiones de usuario stdio y HTTP, guardar tokens
Bearer en el almacén de credenciales y conceder/revocar autorización explícita.
Las acciones forman una sola columna; Enter avanza campos y después confirma el
botón, Tab/Shift+Tab recorren campos visibles y Shift+Enter inserta líneas en
Argumentos. Las confirmaciones empiezan en Cancelar. Textos es/en/fr/it.

La configuración se escribe desde el Host, con revisión y lease compartida con
los escritores CLI. Conserva propiedades avanzadas, configuración de proyecto y
la identidad exacta de otros servidores, incluyendo sus autorizaciones. Las
operaciones de mantenimiento no lanzan procesos, conectan servidores ni realizan
inferencia. El catálogo del siguiente turno consume el archivo guardado; la
autorización MCP no sustituye los permisos de herramientas.

## Comprobaciones

- `python scripts/validate-local.py --scope quick`: aprobado. El primer intento
  dentro del sandbox falló en npm; la repetición con los procesos locales
  permitidos terminó correctamente. Evidencia:
  `C:/Users/juanc/AppData/Local/Temp/omnicore-local-validation-x2gbj3sr/summary.json`.
- `dotnet restore OmniCore.slnx --verbosity quiet`: aprobado; los assets habían
  sido retirados en la limpieza previa de temporales.
- `dotnet build OmniCore.slnx --no-restore --verbosity quiet`: aprobado,
  cero errores y advertencias, sobre la revisión final.
- Tests Microsoft Testing Platform, `--parallel none`, filtro
  `FullyQualifiedName~McpPreferencesTests|FullyQualifiedName~TuiWiringTests.Mcp_|FullyQualifiedName~McpBridgeTests|FullyQualifiedName~McpCompatibilityTests|FullyQualifiedName~ActiveMcp`:
  **61 aprobados, cero fallos y omisiones**.
- Regresión de formularios, filtro
  `FullyQualifiedName~TuiWiringTests.Enter_|FullyQualifiedName~TuiWiringTests.Local_engine_editor|FullyQualifiedName~TuiWiringTests.Inspection_commands|FullyQualifiedName~TuiWiringTests.F7_collapses`:
  **19 aprobados, cero fallos y omisiones**.
- `git diff --check`: aprobado. Se conservan los cambios previos de visibilidad
  de conversación y sus controles F7 presentes al iniciar este encargo.

Las pruebas se ejecutaron con `dotnet test --project
tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore --parallel none --output
Normal --no-progress`; la regresión de formularios añadió `--no-build`.
`OMNICORE_DATA_DIR` y `OMNICORE_CONFIG_DIR` apuntaron a temporales de pruebas;
los fixtures usan configuraciones, credenciales y servidores sintéticos.
Los recorridos del driver TUI cubren 80×25 y 110×35, alta/edición/eliminación,
foco inicial, flechas, Enter/Tab, cancelación y los cuatro idiomas. Incluyen el
descubrimiento del catálogo tras guardar y autorizar, sin servidores reales.

Logs finales en `C:/Users/juanc/AppData/Local/Temp/`:
`omni-mcp-final-build.log`, `omni-mcp-final-regression.log` y
`omni-mcp-navigation-regression.log`. Capturas del driver real de pruebas en
`omni-mcp-screens-final/`, en texto y SVG.

## Límites

No se ejecutó la suite completa, CI remota, inferencia, pruebas GPU ni servidores
MCP del usuario. Esta evidencia no acredita Linux/macOS ni una reconstrucción
nativa Rust. No se hizo commit, merge, publicación ni modificación de la
instalación del usuario o de M9 en este encargo.
