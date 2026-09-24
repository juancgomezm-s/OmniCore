# ADR-0008 — Sandbox como librería compartida `OmniCore.Sandbox`

- **Estado:** Aceptada — rev. 3 (2026-09-24). Rev. 2: §7 resuelve la frontera Security ↔ Sandbox con `IPathBoundaryValidator`. **Rev. 3:** OmniCore es **multiplataforma** (Windows + Linux completos, macOS best-effort) y Sandbox compila para **`net10.0;net8.0`** (ADR-0038). Las referencias a "solo Windows" y "solo net10" de este ADR quedan reemplazadas por ADR-0038 §1–§3 y §6.
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
   - **M2:** validación de rutas y reparse points (+ sus tests), como `OmniCore.Sandbox.PathBoundary`. `OmniCore.Security` la usa **solo a través de `IPathBoundaryValidator`** (Abstractions), cuya implementación es un adapter en `OmniCore.Execution` (rev. 2; ver §7 y ADR-0009).
   - **M3:** AppContainer, Job Objects, leases de ACL, toolchain → usados por `OmniCore.Execution` detrás de `IProcessRuntime`.
   - Las reglas JS (comandos seguros/destructivos, rutas secretas) se reescriben en C# como políticas del Permission Engine, usando sus 33 casos de test como especificación.
5. El worker Node de operaciones de archivo **no** se porta: en OmniCore las tools de archivo son C# nativo validadas en proceso; el AppContainer se reserva para ejecución de procesos.
6. APIs Windows marcadas con `[SupportedOSPlatform("windows")]`; el resto del Core no queda atado a Windows.
7. **Frontera de rutas sin ciclos (rev. 2, 2026-09-24).** Resuelve la contradicción con ADR-0009, que prohíbe `Security → Sandbox`:

   ```text
   Abstractions:  IPathBoundaryValidator  (contrato)
   Sandbox:       PathBoundary            (implementación real; sin dependencias OmniCore)
   Execution:     SandboxPathBoundaryValidator : IPathBoundaryValidator   (adapter; Execution ya referencia Sandbox)
   Security:      usa IPathBoundaryValidator (solo Abstractions)
   Host:          registra el adapter
   ```

   **Contrato:**

   ```csharp
   public interface IPathBoundaryValidator
   {
       // Pura: normalización léxica (.., separadores, absolutas, UNC, ADS, nombres reservados de Windows).
       LexicalPathResult NormalizeLexically(string workspaceRoot, string path);

       // I/O de solo lectura: resuelve reparse points (symlink, junction, mount) y verifica la frontera física.
       ValueTask<PhysicalPathResult> ResolveAsync(WorkspaceBoundary boundary, NormalizedPath path, CancellationToken ct);
   }
   ```

   **Uso por etapa:**
   - `Prepare` (ADR-0014) usa solo la parte léxica, que es pura.
   - Permission Engine usa `ResolveAsync`, una observación de solo lectura y sin efectos.
   - En la ejecución, Sandbox vuelve a comprobar sobre el handle abierto (ruta final del handle, sin seguir reparse points) para cerrar la ventana TOCTOU.

   **Alternativas descartadas:**
   - `Security → Sandbox`: arrastra P/Invoke de Windows a Security.
   - Duplicar la lógica.
   - Mover la validación a Domain: requiere I/O.

## Riesgos abiertos

- ~~AppContainer sin red rompe `dotnet restore`~~ **Resuelto (revisión integral):** los procesos de build/test/restore tienen red sin restricción por decisión del usuario (ADR-0037 §4, ADR-0038 §3). El resto de los procesos sigue sin red por defecto.
- La validación léxica de rutas es TOCTOU; el respaldo real es la ACL del AppContainer.
