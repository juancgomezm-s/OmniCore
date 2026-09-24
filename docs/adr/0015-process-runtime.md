# ADR-0015 — Process Runtime unificado: `process.exec` como primitive, shell como superficie de riesgo

- **Estado:** Aceptada — rev. 2 (2026-09-24). **Rev. 2** (revisión integral):
  - `SandboxProfile` se reemplaza por el de ADR-0038 §3: sin `None`, con red y filesystem declarados, multiplataforma;
  - la resolución de ejecutables y la allowlist de entorno se precisan en ADR-0037 §6;
  - los procesos de build/test/restore tienen **red sin restricción** por decisión del usuario (ADR-0037 §4).
- **Reemplaza:** `shell.exec` como tool principal (spec §38) y el `IProcessRuntime` genérico de la spec §39
- **Relacionado:** ADR-0004 (efectos), ADR-0008 (Sandbox), ADR-0014 (pipeline), ADR-0018 (secretos)
- **Diagrama:** [arquitectura §8](../architecture/arquitectura.md#8-process-runtime)

## Contexto

Si el primitive es `shell.exec("texto arbitrario")`, Security termina interpretando strings de shell para estimar riesgo, lo que es frágil y evadible. Un primitive estructurado permite aplicar permisos, auditar, hacer replay, escapar correctamente, aislar con sandbox y clasificar el riesgo.

## Decisión

### 1. Primitive: `process.exec`

```csharp
public sealed record ProcessClaim(
    string Executable,                          // nombre o ruta; se resuelve en la etapa de permisos, no en Prepare
    IReadOnlyList<string> Arguments,            // argv literal, sin interpretación de shell
    NormalizedPath WorkingDirectory,
    IReadOnlyDictionary<string, EnvValue> Environment,   // solo el delta explícito; EnvValue = Literal | SecretRef
    ProcessEffect Effect,                       // Observational | Rerunnable | WorkspaceEffect | External
    StdinPolicy Stdin,                          // None | Provided(ArtifactRef) | Interactive (futuro, PTY)
    TimeSpan Timeout,
    OutputPolicy Output);
```

- **Resolución del ejecutable:** la hace el Permission Engine vía `IExecutableResolver` (PATH, ruta absoluta, hash opcional). La autorización se decide sobre el ejecutable **resuelto**, no sobre el nombre.
- **Políticas estructuradas:** se evalúan sobre `(ejecutable resuelto, argv[0..n])`, por ejemplo `dotnet test *` o `git status`, sin parsear shell.
- **Escape correcto:** se lanza con `ProcessStartInfo.ArgumentList`, nunca concatenando strings.
- **Superficie shell en Windows:** un `Executable` que resuelve a `.bat` o `.cmd` pasa por el parser de `cmd.exe` (clase de vulnerabilidad *BatBadBut*). Por eso se reclasifica automáticamente como superficie shell (§2).
- **Clase de efecto:** `ProcessEffect` lo declara la tool y, cuando no se conoce, vale `External`. Alimenta la reconciliación (ADR-0004 §4).

### 2. Superficies shell: `shell.exec(shell, script)`

Existen (`bash`, `cmd`, `powershell`), pero como superficie de **mayor riesgo**:

| Aspecto | `process.exec` | `shell.exec` |
|---|---|---|
| Autorización | Política estructurada sobre ejecutable + argv | `Ask` por defecto; `Allow` solo con una regla explícita del usuario |
| Sandbox | Perfil por defecto según `ProcessEffect` (ADR-0038 §3) | Sandbox `Strong` y red denegada por defecto; con sandbox `Basic` solo tras `WeakSandboxConsent` (ADR-0038 §3) |
| `EffectClass` | La declarada | Siempre `NonIdempotent`; nunca se reintenta solo |
| Perfil de modelo | Cualquiera permitido | Requiere `HarnessPolicy` que lo habilite (traits mínimos, ADR-0007) |
| Análisis del texto | No aplica | Solo **advisory** para UX (reglas portadas de OmniCoder); **nunca** es la autoridad |

### 3. Unified Process Runtime

```csharp
public interface IProcessRuntime
{
    ValueTask<IProcessHandle> StartAsync(ProcessLaunch launch, SandboxProfile sandbox, CancellationToken cancellationToken);
}

public interface IProcessHandle : IAsyncDisposable
{
    ChannelReader<ProcessOutputChunk> Output { get; }   // stdout/stderr intercalados con número de secuencia; canal acotado
    ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    ValueTask<ProcessExit> WaitAsync(CancellationToken cancellationToken);
    ValueTask CancelAsync(CancellationMode mode);        // Graceful (Ctrl+C/Ctrl+Break) → timeout → KillTree (Job Object)
}
```

`process.exec` y `shell.exec` comparten este runtime, que cubre:

- **Sandbox:** `SandboxProfile` según ADR-0038 §3 (`Strong | Basic`, filesystem, red y límites). Todo proceso tiene al menos control del árbol de procesos (Job Object, cgroup o grupo de procesos) y límites de recursos.
- **Entorno:** se construye desde una allowlist más el delta del claim. Los `SecretRef` se resuelven al lanzar y sus valores se registran en el redactor (ADR-0018). Solo se persisten los **nombres** de las variables.
- **Salida y backpressure:**
  - canal acotado;
  - la salida completa se escribe en streaming a un artifact content-addressed (ADR-0001);
  - un ring buffer entrega el preview;
  - si el consumidor va lento, se aplica backpressure al lector de pipes, nunca pérdida silenciosa.
- **Timeout y cancelación:** el `CancellationToken` se propaga. La cancelación sigue la secuencia graceful → kill del árbol.
- **Approvals:** la autorización ocurre antes de `StartAsync` (ADR-0014). El runtime no decide permisos.
- **PTY (futuro):** ConPTY detrás del mismo `IProcessHandle` (`StdinPolicy.Interactive`).

## Clasificación

| Elemento | Categoría |
|---|---|
| Tipos `ProcessClaim`, `ProcessEffect`, `SandboxProfile` (se referencian desde `ResourceClaims` en M1) | **Necesario desde M1** (solo tipos) |
| `IProcessRuntime` mínimo con control del árbol de procesos (M2, lo necesita el servidor local managed), confinamiento `Strong` por plataforma, `process.exec` y `shell.exec` (M3) | **Contract now / implementation later** |
| PTY/ConPTY, procesos interactivos | **Deferable** |
