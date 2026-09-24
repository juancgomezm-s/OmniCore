# ADR-0008 — Sandbox como librería compartida `OmniCore.Sandbox`

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §39, §47, §76, §77

## Contexto

Una frontera de rutas protege las tools de archivos pero no contiene a `shell.exec`: un `dotnet test` puede escribir donde quiera. OmniCoder ya tiene aislamiento real en C# (`src/OmniCoder.Core`):

| Pieza | Origen en OmniCoder |
|---|---|
| AppContainer sin capabilities (sin red, incl. loopback) | `Interop/AppContainerProcessLauncher.cs`, `AppContainerProfileLease.cs` |
| Job Objects (kill-on-close, CPU, memoria, nº procesos) | `Interop/ProcessJobObject.cs` |
| Leases de ACL por SID | `Interop/RestrictedTokenIsolation.cs` |
| Validación de rutas (`..`, absolutas, reparse points) | `Orchestration/Sandbox/SandboxToolContract.cs` |
| Toolchain dedicado y entorno por allowlist | `Orchestration/Sandbox/SandboxToolchain.cs` |
| Allowlist/denylist de comandos, rutas secretas | `Assets/guardias-escritura.js` (acoplado a Pi) |

En OmniCoder el aislamiento fuerte solo se usa en workers de orquestación (`OSRestricted`); el chat usa guardas JS.

## Decisión

1. Proyecto **`OmniCore.Sandbox`** dentro de este repo, empaquetable como NuGet (`IsPackable=true`), **net10.0**.
2. **No depende de ningún otro proyecto OmniCore** (lo verifica `ProjectDependencyTests`), para que OmniCoder pueda consumirlo sin arrastrar el Core.
3. Todo en **.NET 10**. OmniCoder (hoy `net8.0-windows`) deberá migrar a `net10.0-windows` para consumirlo; el momento de esa migración queda **pendiente de decisión**.
4. Portado por etapas:
   - **M2:** validación de rutas y reparse points (+ sus tests) → usada por `OmniCore.Security`.
   - **M3:** AppContainer, Job Objects, leases de ACL, toolchain → usados por `OmniCore.Execution` detrás de `IProcessRuntime`.
   - Las reglas JS (comandos seguros/destructivos, rutas secretas) se reescriben en C# como políticas del Permission Engine, usando sus 33 casos de test como especificación.
5. El worker Node de operaciones de archivo **no** se porta: en OmniCore las tools de archivo son C# nativo validadas en proceso; el AppContainer se reserva para ejecución de procesos.
6. APIs Windows marcadas con `[SupportedOSPlatform("windows")]`; el resto del Core no queda atado a Windows.

## Riesgos abiertos

- AppContainer sin red rompe `dotnet restore`/NuGet dentro del sandbox; el toolchain de OmniCoder cubre JDK/git/Android, no el SDK de .NET. Resolver en M3 (caché local de NuGet o `PermissionDelta` de red explícito).
- La validación léxica de rutas es TOCTOU; el respaldo real es la ACL del AppContainer.
