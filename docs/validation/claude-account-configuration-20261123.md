# Alta de cuenta Claude desde la TUI · validación local

Fecha: 2026-11-23. Windows x64, SDK .NET 10.0.403.
Checkout: `C:/Users/juanc/.codex/worktrees/m10-completion/OmniCore`.

## Defecto y corrección

La pantalla de cuenta sólo listaba identidades OAuth previamente escritas en
`providers.yaml`. Sin ninguna, deshabilitaba Iniciar sesión y no ofrecía alta.

El Host ahora ofrece lectura y guardado explícito de la configuración OAuth.
La TUI permite configurar e iniciar sesión desde una instalación vacía, o guardar
la configuración sin autenticar. El formulario presenta la plantilla incluida,
con opciones avanzadas editables, y llama al login nativo existente con el
identificador exacto guardado. El estado local distingue una conexión configurada
sin sesión de una no configurada. Se conserva la lectura local de estado y el
almacenamiento protegido del flujo OAuth existente.

Las acciones están en una columna y el foco inicial permite iniciar el alta.
Enter recorre campos visibles antes de confirmar Guardar; Espacio despliega
opciones y Tab/Shift+Tab recorren controles. Esc descarta sin escritura ni login.
La configuración valida campos, conserva otros proveedores, modelos, referencias
de credenciales y ajustes adicionales, y rechaza revisiones obsoletas con la
lease compartida de proveedores. Los textos nuevos incluyen es/en/fr/it.

## Resultados

- `python scripts/validate-local.py --scope quick`: aprobado. Evidencia:
  `C:/Users/juanc/AppData/Local/Temp/omnicore-local-validation-3w0pbanz/summary.json`.
- `dotnet build OmniCore.slnx --no-restore --verbosity quiet`: aprobado sobre
  la revisión final, cero errores y advertencias. Log:
  `C:/Users/juanc/AppData/Local/Temp/omni-claude-final-build.log`.
- `dotnet test --project tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
  --filter 'FullyQualifiedName~ClaudeAccountConfigurationTests|FullyQualifiedName~TuiWiringTests.Claude_|FullyQualifiedName~TuiWiringTests.Preferences_accounts_open|FullyQualifiedName~TuiClaudeAccountHostTests|FullyQualifiedName~ClaudeOAuth'
  --parallel none --output Normal --no-progress`: **247 aprobados, cero fallos
  y omisiones**. Log:
  `C:/Users/juanc/AppData/Local/Temp/omni-claude-final-regression.log`.
- `git diff --check`: aprobado. Se conservaron los cambios previos de MCP y
  visibilidad de conversación presentes al iniciar este encargo.

Las pruebas aíslan `OMNICORE_DATA_DIR` y `OMNICORE_CONFIG_DIR`. El recorrido de
alta usa la fachada real de configuración con login simulado; la suite OAuth
existente usa sus fixtures de protocolo, transporte, credenciales y renovación.
Se cubren alta, edición, colisiones con proveedores API, referencias explícitas,
rechazo de cambios concurrentes, validación, foco, navegación, cancelación,
completados tardíos y resultados. La TUI se recorre en 80×25 y 110×35 con los
cuatro idiomas. Capturas del driver y las pantallas existentes en
`C:/Users/juanc/AppData/Local/Temp/omni-claude-setup-screens/`.

No se autenticó una cuenta real, llamó a modelos ni usaron GPUs. No se ejecutó
la suite completa, CI remota ni pruebas de otras plataformas. No se publicó ni
actualizó la instalación. M9 y los otros checkouts permanecen sin modificaciones.
