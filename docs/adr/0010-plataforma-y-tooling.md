# ADR-0010 — .NET 10 en todo, tooling de build y tests

- **Estado:** Aceptada (2026-09-24)

## Decisión

- **SDK:** .NET 10 (`global.json` fija 10.0.401 con `rollForward: latestFeature`). Ningún proyecto apunta a .NET 8.
- **Solución:** `OmniCore.slnx`.
- **Build:** `Directory.Build.props` con `Nullable`, `TreatWarningsAsErrors`, `AnalysisLevel=latest`, `EnforceCodeStyleInBuild`, build determinista. `CS1591` (docs XML faltantes) se silencia hasta estabilizar Abstractions/Protocol.
- **Paquetes:** Central Package Management (`Directory.Packages.props`). Agregar paquetes con `dotnet add package` para que la versión quede centralizada.
- **Tests:** xUnit v3 sobre **Microsoft.Testing.Platform** (`global.json` → `test.runner`). Con el SDK 10 el modo VSTest ya no está soportado por MTP; no se usan `Microsoft.NET.Test.Sdk` ni `xunit.runner.visualstudio`.
- **Comandos:** `dotnet build OmniCore.slnx` · `dotnet test --solution OmniCore.slnx`.
