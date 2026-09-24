# ADR-0010 — .NET 10 en todo, tooling de build y tests

- **Estado:** Aceptada (2026-09-24)

## Decisión

- **SDK:** .NET 10 (`global.json` fija 10.0.401 con `rollForward: latestFeature`). **Excepción (revisión integral, ADR-0038 §6):** `OmniCore.Protocol`, `OmniCore.Client` y `OmniCore.Sandbox` compilan para `net10.0;net8.0`; ningún otro proyecto apunta a .NET 8, y lo verifica un test.
- **AOT:** `IsAotCompatible` está activo en `src/` (`src/Directory.Build.props`), así que los analizadores de trimming y AOT se ejecutan con warnings como errores (ADR-0038 §5).
- **Configuración:** YAML leído con YamlDotNet y su generador estático (ADR-0039).
- **Solución:** `OmniCore.slnx`.
- **Build:** `Directory.Build.props` con `Nullable`, `TreatWarningsAsErrors`, `AnalysisLevel=latest`, `EnforceCodeStyleInBuild`, build determinista. `CS1591` (docs XML faltantes) se silencia hasta estabilizar Abstractions/Protocol.
- **Paquetes:** Central Package Management (`Directory.Packages.props`). Agregar paquetes con `dotnet add package` para que la versión quede centralizada.
- **Tests:** xUnit v3 sobre **Microsoft.Testing.Platform** (`global.json` → `test.runner`). Con el SDK 10 el modo VSTest ya no está soportado por MTP; no se usan `Microsoft.NET.Test.Sdk` ni `xunit.runner.visualstudio`.
- **Comandos:** `dotnet build OmniCore.slnx` · `dotnet test --solution OmniCore.slnx`.
