# Catálogo de modelos de una cuenta Claude — 2026-11-24

## Defecto y corrección

El login OAuth persistía la cuenta, pero no consultaba el catálogo. La TUI sólo
recargaba `models.yaml` tras la autorización; el descubrimiento Anthropic
existente estaba limitado a API key. Por eso una cuenta conectada podía no tener
ningún modelo en F3 o en el mantenimiento de visibilidad.

Se incorpora descubrimiento OAuth explícito en el Host y se conecta al resultado
del login TUI. **Actualizar modelos** recupera el catálogo de una cuenta conectada
sin repetir la autenticación. Abrir cuentas sigue siendo pasivo. Se conservan
las rutas por API key, los modelos previos y sus preferencias de visibilidad.

La consulta paginada usa el Bearer renovable de la cuenta, la identidad User-Agent
de esa conexión y las cabeceras OAuth existentes. El registro queda asociado al
proveedor exacto y compara la revisión de configuración y la credencial antes de
escribir. Cancelación, logout y cambio de conexión rechazan resultados tardíos.
No se reasignan identificadores que ya pertenecen a otro proveedor. Los snapshots
de esfuerzo de API key no se aplican a una ruta OAuth. Los errores del catálogo
son independientes del éxito del login; el detalle es recorrible en la TUI.

## Validación local

Windows x64, SDK .NET 10.0.403; directorios de datos y configuración aislados.

- `dotnet build OmniCore.slnx --no-restore --verbosity quiet`: aprobado,
  cero advertencias y errores; conserva compilación de los destinos net8.
- `dotnet test --project tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~TuiClaudeAccountHostTests|FullyQualifiedName~TuiWiringTests.Claude_|FullyQualifiedName~Anthropic|FullyQualifiedName~ProviderConnection' --parallel none --output Normal --no-progress`:
  **118 aprobadas, 0 fallidas, 0 omitidas** sobre los binarios finales.
- `python scripts/validate-local.py --scope quick`: aprobado.
- `git diff --check`: aprobado.

Incluye petición OAuth sin `x-api-key`, paginación, registro idempotente,
conservación de límites manuales, catálogo vacío y malformado, 401/403/429,
cancelación antes y durante la descarga, logout y configuración modificada,
conflictos sin escritura parcial. La TUI usa el Host real de catálogo con HTTP
simulado: cuenta conectada → Enter/Actualizar → F3 → Modelos visibles en el
mismo proceso, conservación de modelos locales y ChatGPT, borrador y visibilidad.
Se comprueban cuatro idiomas y geometría 80×25 / 120×40.

Evidencias locales conservadas:

- `%TEMP%/omni-claude-verified-383f631b90f345da99bde43d40fb60f8/tests.log`
- La subcarpeta `snapshots/` del mismo directorio contiene celdas reales del
  driver de la TUI en TXT y SVG para los cuatro idiomas.
- `%TEMP%/omnicore-local-validation-3ksc5bme/summary.json` y logs de cada paso.

No se usaron cuentas ni servidores reales del usuario, API de pago o GPU.
Los modelos de prueba son identificadores de fixtures, no una lista de acceso
afirmada para la suscripción del usuario. No se certifica inferencia real, Linux,
macOS ni suite completa con este foco. No se ejecutó GitHub Actions ni release.
La publicación permanece pendiente de autorización tras el rechazo automático
del intento anterior de publicar Main/tag/release; este documento no la autoriza.
