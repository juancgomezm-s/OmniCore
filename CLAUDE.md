# OmniCore

Runtime de agentes local-first en C#/.NET 10 y su CLI técnico `omni`. Debe funcionar igual con modelos locales pequeños (4B) y con modelos frontera, cambiando solo política y capacidades efectivas.

- Especificación: `docs/spec/OmniCore-v1.md` (v0.3)
- Decisiones: `docs/adr/` — **prevalecen sobre la spec**. Léelas antes de cambiar cualquier cosa que toquen.
- Arquitectura (diagramas, clasificación de abstracciones, preguntas abiertas, roadmap): `docs/architecture/arquitectura.md`

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
5. Canonical Journal = Event Store append-only + artifacts inmutables content-addressed (ADR-0001). Estado en eventos, contenido en artifacts. Compactar nunca lo modifica, y un artifact referenciado no desaparece.
6. El contexto enviado al modelo es siempre una proyección del estado.
7. Lógica por capacidades, nunca por nombre de modelo o provider (`if (model == "qwen")` está prohibido).
8. El Engine no conoce providers concretos, SQLite, archivos de log, Git, PowerShell ni MCP.
9. Hooks pueden restringir, observar o enriquecer; nunca ampliar permisos.
10. Un Run no termina porque el modelo lo diga: pasa por Completion Gates.
11. Clientes interactúan solo mediante commands y eventos, a través de `IOmniClient` (ADR-0019).
12. El Plan es estado canónico del runtime: el modelo propone `PlanMutation`s y `PlanService`/`ProgressReconciler` deciden (ADR-0016).
13. Ninguna tool se ejecuta sin `AuthorizedToolIntent`, que solo construye Security. `Prepare` es puro (ADR-0014).
14. Ningún efecto lateral empieza antes de que su intent esté confirmado con un commit Barrier; todo efecto es reconciliable (ADR-0002, ADR-0004).
15. Procesos: `process.exec(executable, argv[])`. Nunca se autoriza interpretando texto de shell (ADR-0015).
16. Los secretos nunca llegan al journal, los artifacts, el contexto ni los logs (ADR-0018).
17. Los providers conservan su semántica nativa (`ContentBlocks` + `ProviderOpaque`); nada se fuerza a pasar por OpenAI-compatible (ADR-0005).
18. El comportamiento por modelo sale de `EffectiveModelProfile` → `HarnessPolicy`, nunca del tamaño ni del nombre (ADR-0007).
19. El Engine nunca interpreta texto que empiece por `/`: los commands llegan tipados (ADR-0024). Keybindings y palette son solo del cliente y resuelven a `ClientAction`s (ADR-0025).
20. Ninguna fuente sustituye en silencio una tool built-in `Protected`, y un nombre ambiguo nunca se resuelve en silencio (ADR-0024, ADR-0027).
21. Las extensiones y skills no `Core` corren fuera de proceso; su manifest es un techo de permisos (`ExtensionBoundary`), nunca un grant (ADR-0023).
22. Memory es un servicio externo que entra solo como `IContextContributor`. El runtime funciona sin ella, y el LLM nunca promociona memoria por sí solo (ADR-0028).
23. Todo `ContextItem` lleva procedencia (ADR-0029).

## Reglas de trabajo

- Grafo de dependencias en ADR-0009, verificado por `tests/OmniCore.ArchitectureTests`. Cambiar el grafo = actualizar ADR y test en el mismo cambio.
- `OmniCore.Sandbox` no depende de nada de OmniCore: se comparte con OmniCoder (ADR-0008).
- Todo en .NET 10. No agregar targets net8.
- Errores tipados (spec §71), no `Exception` genérica para fallos de dominio.
- Toda operación async significativa recibe `CancellationToken` y lo propaga.
- Priorizar tests deterministas (`ScriptedModelProvider`, `FakeTool`, etc.) sobre tests con LLM.
- Todo valor configurable debe tener un test que demuestre que cambia el comportamiento (lección de OmniCoder, ADR-0007).
- Identificadores y código en inglés; documentación y mensajes al usuario en español.
- Datos de runtime **fuera del repo**: `%LOCALAPPDATA%\OmniCore\workspaces\<WorkspaceId>\` (journal, blobs, worktrees). `.omnicore/` del repo es solo configuración versionable (ADR-0022 §3).

## Estado

- **Hecho:** esqueleto (13 proyectos + tests de arquitectura) y revisión arquitectónica v0.2/v0.3 (ADRs 0001–0029). OAQ-1 resuelta el 2026-09-24 (opción B).
- **Siguiente:** M1 — runtime sin IA + Planning (spec §86, arquitectura §24), cuando el usuario apruebe la revisión. No implementar nada fuera de lo que M1 usa: los contratos marcados "Necesaria desde M1" se congelan, y el resto espera a su milestone.
