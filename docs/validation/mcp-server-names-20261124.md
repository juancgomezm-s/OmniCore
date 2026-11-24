# Nombre de servidor MCP y validación de campos · 2026-11-24

La captura de Juan mostraba `RAG local` en el campo de identificador y una URL
loopback válida. El guardado rechazaba el espacio del nombre y mostraba un aviso
genérico de identificador/argumentos/URL que no permitía distinguir el fallo.

La TUI ahora separa nombre visible e identificador técnico: `RAG local` se guarda
como nombre y genera `rag-local` para el namespace MCP. El identificador se muestra
sin edición y se conserva al renombrar una conexión existente. Los IDs anteriores
mantienen su valor. Se preservan nombres con acentos y otros alfabetos, con IDs
ASCII acotados y estables; los nombres largos incluyen un sufijo derivado del nombre.

Los errores de nombre, identificador, URL, argumentos, ejecutable y directorio
disponen de mensajes específicos; el formulario enfoca el campo correspondiente.
Los argumentos ocultos de stdio no bloquean el guardado de una conexión HTTP.
Textos es/en/fr/it y límites de permisos, autorización y transportes conservados.

## Comprobaciones locales

Windows x64, SDK .NET 10.0.403; datos/configuración aislados. No se usan cuentas,
servidores ni GPUs reales del usuario como fixture.

- `python scripts/validate-local.py --scope quick`: aprobado. Evidencia en
  `C:/Users/juanc/AppData/Local/Temp/omnicore-local-validation-q1dtjye5/summary.json`.
- `dotnet build OmniCore.slnx --no-restore --verbosity quiet`: aprobado,
  cero errores y advertencias. `git diff --check`: aprobado.
- `dotnet test --project tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
  --filter 'FullyQualifiedName~Mcp|FullyQualifiedName~Every_preference_category_has_distinct_actions_and_returns_from_its_pages|FullyQualifiedName~A_failed_menu_read_keeps_an_error_page_and_returns_to_its_category'
  --parallel none --output Normal --no-progress`: **92 aprobadas, cero fallos y
  omisiones**. Log y capturas del driver real de pruebas en
  `C:/Users/juanc/AppData/Local/Temp/omni-mcp-rag-eba93e8d9d0f4baeb173f42dce6ba976/`.
- Caso exacto de nombre `RAG local` y URL `http://127.0.0.1:18081/mcp` probado en
  los cuatro idiomas, incluyendo guardado, lectura posterior, edición y foco
  del error de URL. Compatibilidad de IDs anteriores y nombres largos verificada.
- El recorrido MCP completo ahora crea la configuración con nombre desde la
  fachada Host y verifica autorización, descubrimiento, permisos de primera
  ejecución y resultado real de una herramienta sobre un servidor HTTP de fixture.

Esta evidencia no acredita conexión al servidor concreto de Juan ni otras
plataformas. No se ejecutó la suite completa de M9 ni CI remota de pruebas.
