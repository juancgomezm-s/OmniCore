# M5: cancelación de la cualificación antes de publicar evidencia

Checkpoint: 2026-10-06 21:20 UTC / 15:20 America/Mexico_City.

## Defectos reproducidos

`ProbeRunner.RunSuiteAsync` devuelve `NotRun` para los probes restantes cuando
detecta cancelación entre invocaciones. Host trataba estos resultados como una
suite fallida, en vez de propagar la cancelación del caller. Si el último stream
completaba y después cancelaba el token, Host publicaba evidencia CAS antes de
que la transacción de perfil rechazara la cancelación: quedaba un blob huérfano.

Host comprueba el token al volver del runner, antes de interpretar los resultados,
y de nuevo justo antes de publicar evidencia. No se cambia el contrato del runner
ni se confunde timeout de probe con cancelación del caller. No se escribe perfil,
traits ni evidencia en los cuatro checkpoints deterministas verificados.

Estas comprobaciones no hacen atómicos CAS y SQLite frente a una cancelación
concurrente posterior al último check. Esa ventana y la recuperación de blobs
no referenciados siguen dependiendo del contrato CAS/GC existente; no se declara
una garantía de atomicidad entre recursos ni cierre completo M5.

## Integración offline y reproducción

Luna HIGH aportó tres casos; root auditó la propuesta, agregó el caso al terminar
el último probe y la comprobación de que el CAS privado no contiene archivos.
`M5QualificationCancellationTests` usa el Host normal, configuración Local privada,
proveedor scripted con cota1 y SQLite real. No consulta modelos ni credenciales.

- Cancelación previa: cero invocaciones.
- Entre probes: exactamente una invocación, `OperationCanceledException`.
- Durante un stream: una invocación, cancelación propagada como control.
- Después del último probe: exactamente diez invocaciones, cancelación propagada.

Todos exigen store de perfiles vacío, referencia de evidencia ausente y CAS privado
sin archivos. RED3: 2 PASS/1 FAIL (0.453s). RED4 más fuerte: 2 PASS/2 FAIL (0.569s),
build0 errores/advertencias. Tras corrección: focal cualificación/Responses/auth
272 PASS/0 FAIL/0 SKIP (6.618s), build0/0; arquitectura56 PASS (0.977s), build0/0.
Suite completa nueva en curso; resultado anterior2177 corresponde al wiring Codex,
no a esta corrección. Logs únicos en `C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/`
(`qualification-cancellation-red*`, `green-*`, `full.log`, `architecture*`).

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*M5QualificationCancellationTests'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*Qualification*' -class '*OpenAIResponsesProviderTests' -class '*ChatGptSubscriptionAuthProviderTests'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none
```
