# OmniCore

Runtime de agentes local-first en C#/.NET 10 y su CLI técnico `omni`. Debe funcionar igual con modelos locales pequeños (4B) y con modelos frontera, cambiando solo política y capacidades efectivas.

- Especificación: `docs/spec/OmniCore-v1.md`
- Decisiones: `docs/adr/` — **prevalecen sobre la spec**. Léelas antes de cambiar cualquier cosa que toquen.

## Comandos

```bash
dotnet build OmniCore.slnx
dotnet test --solution OmniCore.slnx
dotnet run --project src/OmniCore.Cli -- <args>
```

Paquetes: `dotnet add <proyecto> package <id>` (Central Package Management; la versión queda en `Directory.Packages.props`). Tests: xUnit v3 sobre Microsoft.Testing.Platform.

## Invariantes (spec §4) — no negociables

1. El modelo nunca ejecuta tools: emite requests que el Tool Runtime ejecuta.
2. El modelo nunca otorga permisos. Las tools nunca deciden su propia autorización: el Engine consulta el Permission Engine antes de invocarlas.
3. Subagentes solo vía `Task → Lane → AgentExecution`. Nada de `SpawnAgent(prompt)`.
4. Los subagentes reciben `TaskPacket` + contexto materializado, nunca el transcript del padre.
5. Historial canónico append-only; los eventos son la única fuente de verdad (ADR-0001). Compactar nunca lo modifica.
6. El contexto enviado al modelo es siempre una proyección del estado.
7. Lógica por capacidades, nunca por nombre de modelo o provider (`if (model == "qwen")` está prohibido).
8. El Engine no conoce providers concretos, SQLite, archivos de log, Git, PowerShell ni MCP.
9. Hooks pueden restringir, observar o enriquecer; nunca ampliar permisos.
10. Un Run no termina porque el modelo lo diga: pasa por Completion Gates.
11. Clientes interactúan solo mediante commands y eventos.

## Reglas de trabajo

- Grafo de dependencias en ADR-0009, verificado por `tests/OmniCore.ArchitectureTests`. Cambiar el grafo = actualizar ADR y test en el mismo cambio.
- `OmniCore.Sandbox` no depende de nada de OmniCore: se comparte con OmniCoder (ADR-0008).
- Todo en .NET 10. No agregar targets net8.
- Errores tipados (spec §71), no `Exception` genérica para fallos de dominio.
- Toda operación async significativa recibe `CancellationToken` y lo propaga.
- Priorizar tests deterministas (`ScriptedModelProvider`, `FakeTool`, etc.) sobre tests con LLM.
- Todo valor configurable debe tener un test que demuestre que cambia el comportamiento (lección de OmniCoder, ADR-0007).
- Identificadores y código en inglés; documentación y mensajes al usuario en español.

## Estado

Esqueleto creado (13 proyectos + tests de arquitectura). Siguiente: **M1 — runtime sin IA** (spec §86): Session, Run, Task, TaskGraph, Lane, Turn, eventos, máquinas de estado, `SqliteEventStore` (ADR-0002). Criterio: una ejecución simulada se reconstruye desde eventos.
