# ADR-0009 — Grafo de dependencias entre proyectos

- **Estado:** Aceptada — rev. 2 (2026-09-24)
- **Rev. 1:** el mismo grafo. La rev. 2 no cambia ninguna arista; agrega reglas de frontera **dentro** del grafo: la validación de rutas, la autoridad de `AuthorizedToolIntent`, el CLI desacoplado y proyectos futuros.
- **Spec:** §78–§81
- **Diagrama:** [arquitectura §18](../architecture/arquitectura.md#18-permission-y-sandbox-boundaries)

## Decisión

### 1. Grafo (sin cambios)

```text
Domain            ← (nada)
Abstractions      ← Domain
Protocol          ← (nada)        DTOs wire-safe; lo consumirán clientes externos (OmniCoder)
Sandbox           ← (nada)        compartida con OmniCoder (ADR-0008)
Engine            ← Abstractions, Domain
Context           ← Abstractions, Domain
Models            ← Abstractions, Domain
Tools             ← Abstractions, Domain          (NO Security)
Security          ← Abstractions, Domain          (NO Sandbox)
Execution         ← Abstractions, Domain, Sandbox
Infrastructure    ← Abstractions, Domain
Host              ← todos los anteriores salvo Sandbox directo (composition root)
Cli               ← Host, Protocol
```

`tests/OmniCore.ArchitectureTests/ProjectDependencyTests.cs` lo hace cumplir leyendo los `ProjectReference`. Un proyecto nuevo sin frontera declarada hace fallar los tests.

### 2. Reglas de frontera (rev. 2)

1. **Autorización antes de ejecutar (ADR-0014).**
   - El Engine consulta al Permission Engine **antes** de invocar al Tool Runtime.
   - `AuthorizedToolIntent` vive en Abstractions con constructor `internal`, y `InternalsVisibleTo` se concede **solo** a `OmniCore.Security`.
   - Ningún otro assembly puede fabricar una autorización.
   - *Test nuevo en M1:* el único `InternalsVisibleTo` de Abstractions (fuera de los tests) es Security.
2. **Rutas (ADR-0008 §7).**
   - `IPathBoundaryValidator` está en Abstractions, la implementación en Sandbox y el adapter en Execution.
   - Security nunca referencia Sandbox.
3. **Protocolo (ADR-0013).**
   - Protocol no depende de Domain.
   - `ProtocolMapper` (dominio → wire) vive en Host.
4. **CLI desacoplado (ADR-0019).**
   - El código del CLI usa solo tipos de `OmniCore.Protocol` (`IOmniClient`). El único punto que toca Host es la composición en `Program.cs`, que crea un cliente in-process.
   - *Test nuevo en M1:* las referencias IL de `omni.dll` son solo `OmniCore.Protocol` y `OmniCore.Host`, y la superficie pública de Host se limita a la fábrica del host y del cliente.
5. **Proyectos futuros previstos** (se agregan con su frontera cuando lleguen, no antes):
   - `OmniCore.Qualification` (M5): runner de la Model Qualification Suite. Consume el runtime como un cliente, igual que el CLI.
   - `OmniCore.Extensions` (M8): host de extensiones fuera de proceso (Extension API por JSON-RPC, ADR-0023). Ejecuta tools, commands, skills, hooks y contributors de fuentes no `Core`. Depende de Abstractions y Domain.
   - `OmniCore.Memory` (M8+): implementación de `IMemoryStore` y `MemoryContextContributor` (ADR-0028). Depende solo de Abstractions y Domain. **Ningún proyecto del Core lo referencia**: lo compone Host de forma opcional.
6. **Commands (ADR-0024):**
   - Los contratos (`CommandDescriptor`, `ICommandHandler`) viven en Abstractions.
   - El `CommandRegistry` de servidor está en Host y los DTOs del catálogo en Protocol.
   - `ClientAction`s y `KeyBindingService` viven solo en el cliente (Cli), nunca en el Core (ADR-0025).
   - **El Engine no contiene ningún parser de `/`.**

## Consecuencias

- Cambiar el grafo requiere editar este ADR y la tabla `Allowed` del test en el mismo cambio.
- Los tests nuevos de las reglas 1 y 4 llegan en M1, junto con el código que protegen.
