# ADR-0009 — Grafo de dependencias entre proyectos

- **Estado:** Aceptada (2026-09-24)
- **Spec:** §78–§81

## Contexto

El diagrama de §80 hace depender Tools de Security, lo que invita a que una Tool consulte su propia autorización (contra INV-003). La spec pide verificar la dirección para evitar ciclos.

## Decisión

```text
Domain            ← (nada)
Abstractions      ← Domain
Protocol          ← (nada)        DTOs wire-safe; lo consumirán clientes externos (OmniCoder)
Sandbox           ← (nada)        compartida con OmniCoder (ADR-0008)
Engine            ← Abstractions, Domain
Context           ← Abstractions, Domain
Models            ← Abstractions, Domain
Tools             ← Abstractions, Domain          (NO Security)
Security          ← Abstractions, Domain
Execution         ← Abstractions, Domain, Sandbox
Infrastructure    ← Abstractions, Domain
Host              ← todos los anteriores salvo Sandbox directo (composition root)
Cli               ← Host, Protocol
```

1. El **Engine** consulta al Permission Engine (vía abstracción) **antes** de invocar al Tool Runtime. Las tools nunca ven la decisión de permisos (INV-003).
2. **Protocol** no depende del Domain: el mapeo dominio ↔ wire vive en Host. Así un cliente externo solo referencia Protocol.
3. Domain, Abstractions y Protocol no tienen paquetes NuGet.
4. `tests/OmniCore.ArchitectureTests/ProjectDependencyTests.cs` hace cumplir este grafo leyendo los `ProjectReference` de cada `.csproj`. Un proyecto nuevo sin frontera declarada hace fallar el build de tests.

## Consecuencias

- Cambiar el grafo requiere editar este ADR y la tabla `Allowed` del test en el mismo cambio.
