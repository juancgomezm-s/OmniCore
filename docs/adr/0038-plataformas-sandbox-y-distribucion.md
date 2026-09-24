# ADR-0038 — Plataformas, sandbox multiplataforma, distribución y multi-target

- **Estado:** Aceptada (2026-09-24). Decisiones del usuario:
  - **multiplataforma desde v1**, con soporte completo para **Windows + Linux**;
  - **sin sandbox fuerte, se permite con confirmación de sesión**;
  - distribución **self-contained por RID con AOT posible**;
  - **Protocol, Client y Sandbox compilan también para net8** (lo consume OmniCoder).
- **Resuelve:** seguridad F04 y F05; modelos F20 y F21
- **Reemplaza parcialmente:** ADR-0008 §3 y §6 (solo Windows, solo net10), ADR-0010 ("ningún proyecto apunta a .NET 8"), ADR-0015 §3 (`SandboxProfile`)
- **Diagrama:** [arquitectura §38](../architecture/arquitectura.md#38-plataformas-y-sandbox)

## Decisión

### 1. Plataformas

| Plataforma | Nivel en v1 | Sandbox | Credenciales | Datos |
|---|---|---|---|---|
| **Windows** (x64, arm64) | completo + CI | AppContainer + Job Objects | Credential Manager / DPAPI | `%APPDATA%\OmniCore` (config), `%LOCALAPPDATA%\OmniCore` (datos) |
| **Linux** (x64, arm64) | completo + CI | **bubblewrap** (namespaces) + **Landlock** (filesystem) + **seccomp** (syscalls) + cgroups/`prlimit` (recursos) | Secret Service (libsecret) o un archivo cifrado con clave del keyring | `$XDG_CONFIG_HOME/omnicore`, `$XDG_DATA_HOME/omnicore`, `$XDG_STATE_HOME/omnicore` |
| **macOS** | **best-effort** (compila y corre; sin CI obligatoria) | sin sandbox fuerte en v1: aplica §3 | Keychain | `~/Library/Application Support/OmniCore` |

Las rutas concretas por scope están en ADR-0039. Donde este documento o ADR-0022 dicen `%LOCALAPPDATA%\OmniCore`, debe leerse **el directorio de datos de la plataforma**.

### 2. Abstracciones de plataforma

Todas viven en Abstractions; la implementación por sistema operativo, en Sandbox, Execution o Infrastructure:

| Abstracción | Windows | Linux | macOS (v1) |
|---|---|---|---|
| `IProcessSandbox` | AppContainer + Job Object | bubblewrap + Landlock + seccomp + cgroup | aislamiento básico (§3) |
| `IProcessTreeControl` (matar el árbol, límites) | Job Object | cgroup v2 o grupo de procesos + `prlimit` | grupo de procesos |
| `ICredentialStore` | Credential Manager / DPAPI | Secret Service | Keychain |
| `IPlatformPaths` | known folders | XDG | Application Support |
| `IPathBoundaryValidator` (ADR-0008 §7) | reparse points, ADS, nombres reservados, UNC | symlinks, bind mounts (`/proc/self/mountinfo`) | symlinks |
| Interrupción graceful | `CTRL_BREAK_EVENT` a un grupo nuevo | `SIGINT` o `SIGTERM` al grupo | `SIGINT`/`SIGTERM` |
| PTY (futuro) | ConPTY | `forkpty` | `forkpty` |

- La regla *BatBadBut* de ADR-0015 §1 aplica solo en Windows.
- Las reglas léxicas de rutas se seleccionan por plataforma.

### 3. `SandboxProfile` (reemplaza el de ADR-0015)

```csharp
public sealed record SandboxProfile(
    SandboxStrength Strength,            // Strong | Basic
    FilesystemAccess Filesystem,         // rutas de lectura y escritura concedidas (workspace o worktree, toolchain, temp)
    NetworkAccess Network,               // None | Loopback | Build (sin restricción, ADR-0037 §4) | Allowlist(hosts)
    ResourceLimits Limits);              // CPU, memoria, número de procesos, tiempo
```

- **Siempre hay control del árbol de procesos** (Job Object, cgroup o grupo de procesos) y límites de recursos. `None` desaparece.
- **`Strong`:** confinamiento del filesystem y de la red aplicado por el sistema operativo.
- **`Basic`:** solo control del árbol y límites, sin confinamiento de filesystem.

**Sin sandbox fuerte disponible** (macOS en v1, Linux sin user namespaces o sin bubblewrap, contenedores restringidos):

1. `/doctor` y la status line muestran el nivel efectivo: `sandbox: basic`.
2. Al primer proceso con efecto de la sesión se emite un `InteractionRequest` de tipo **`WeakSandboxConsent`**: "Este sistema no ofrece sandbox fuerte; los procesos podrán leer y escribir fuera del workspace". Las opciones son `Permitir en esta sesión` y `Denegar`.
3. Si se acepta, **todo** corre con `Basic` durante la sesión, **incluida la superficie shell**, que se sigue rigiendo por su `Ask` propio de ADR-0037. La aceptación queda en el audit log (ADR-0043).
4. Sin cliente interactivo, aplica ADR-0003: `Deny`, así que no se ejecutan procesos con efecto.

**Perfiles por defecto:**

| Proceso | Filesystem | Red |
|---|---|---|
| `Observational` | lectura del workspace | None |
| build/test/restore | lectura y escritura del workspace o worktree, toolchain en lectura, caches del gestor de paquetes | **Build** (sin restricción, decisión del usuario) |
| `External` / shell | lectura y escritura del workspace | None, salvo grant |
| servidor de modelo local managed | modelo en lectura | Loopback |
| lane de Claude Code (ADR-0012) | ver ADR-0012 (riesgo aceptado) | — |

**Nunca accesibles en `Strong`:** `~/.ssh`, `~/.aws`, `~/.config/gh`, los almacenes de credenciales de la plataforma y las rutas de secretos de ADR-0018.

### 4. Identidades con rutas canónicas (precisa ADR-0022)

La "ruta canónica" que se hashea para obtener `WorkspaceId` y `ProjectId` se calcula así:

1. ruta absoluta con los symlinks resueltos (`realpath`), o sus equivalentes (reparse points en Windows);
2. separador `/`;
3. sin barra final;
4. **en Windows**, en minúsculas invariantes (el sistema de archivos no distingue mayúsculas); **en Linux**, sin cambios.

La URL `origin` se normaliza así antes del hash:

- esquema y host en minúsculas;
- sin credenciales;
- sin `.git` final;
- la forma SSH `git@host:org/repo` se convierte a `host/org/repo`.

### 5. Distribución

- **Ejecutable self-contained single-file por RID:** `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64` y, en best-effort, `osx-arm64`. No requiere .NET instalado.
- **Native AOT como opción, no como requisito.** `IsAotCompatible` está activo en `src/` desde ya (`src/Directory.Build.props`), así que los analizadores de trimming y AOT forman parte del build con warnings como errores. En consecuencia:
  - la serialización es solo con System.Text.Json con source generation;
  - YAML se lee con el generador estático de YamlDotNet (ADR-0039);
  - no hay reflexión dinámica en el Core;
  - las extensiones corren fuera de proceso (ADR-0023), que es compatible con AOT;
  - SQLite aporta su binario nativo por RID;
  - antes de publicar hay que verificar la compatibilidad AOT de Terminal.Gui v2 (OAQ-15).
- **`dotnet tool`:** queda como canal secundario opcional, no como principal.

### 6. Multi-target net8 para las librerías compartidas con OmniCoder

- **Proyectos:** `OmniCore.Protocol`, `OmniCore.Client` y `OmniCore.Sandbox` compilan para **`net10.0;net8.0`**. Es la única excepción a "todo en .NET 10".
- **Integración:** OmniCoder (net8) los referencia sin migrar y habla con OmniCore **por stdio** (M9) o in-process si en algún momento migra. El adapter `IAgentRuntime` de OmniCoder usa `IOmniClient` y `WireEvent`s (ver la corrección de la spec §98).
- **Restricción:** en esas tres librerías no se usan APIs exclusivas de net10.
- **Test:** un test de arquitectura verifica que solo ellas apuntan a net8 y que las tres lo hacen (`Only_shared_libraries_target_net8`).

## Clasificación

| Elemento | Categoría |
|---|---|
| Multi-target y analizadores AOT (ya aplicados); `IPlatformPaths`; canonicalización de rutas para las identidades (el journal se ubica con ellas); tests de M1 verdes en Windows y Linux (con CI en matriz en cuanto el repo tenga remoto; hoy no tiene) | **Necesario desde M1** |
| `IPathBoundaryValidator` por plataforma (M2); `IProcessSandbox`, `IProcessTreeControl` y `WeakSandboxConsent` (M2 control del árbol, M3 confinamiento); `ICredentialStore` por plataforma (M2) | **Contract now / implementation later** |
| Sandbox fuerte en macOS (Seatbelt), PTY | **Deferable** (v1.x) |
