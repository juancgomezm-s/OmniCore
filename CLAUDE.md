# OmniCore

Runtime de agentes local-first en C#/.NET 10 y su CLI técnico `omni`. Debe funcionar igual con modelos locales pequeños (4B) y con modelos frontera, cambiando solo política y capacidades efectivas.

- Especificación: `docs/spec/OmniCore-v1.md` (v0.5)
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
24. La UI está desacoplada: Terminal.Gui y Spectre.Console solo en `OmniCore.Cli` (lo verifica un test). `OmniCore.Client` (`ClientProjection`, modelos de presentación, acciones, widgets) no conoce frameworks visuales, y todos los renderers leen el mismo estado (ADR-0030).
25. Todo human-in-the-loop pasa por `InteractionRequest`/`RespondToInteraction`, con opciones decididas por el servidor (ADR-0034). La status line nunca inventa datos: sin cuota informada muestra `—` (ADR-0031).
26. Un Run abarca la conversación hasta pasar los gates, con 1 Run activo por Session. PLAN → ACT ocurre en el mismo Run tras `PlanApproval` (ADR-0035).
27. Cada transición de estado canónico tiene exactamente un evento. La actividad de Lane (`Waiting*`, `Stalled`) se deriva y nunca se persiste (ADR-0036).
28. Permisos: las capas se combinan por mínimo (`Deny < Ask < Allow`), y un grant solo levanta `Ask` de `UserPolicy` o del perfil, nunca un `Deny`. Los grants persistentes van por `WorkspaceId`. El perfil por defecto es autónomo (ADR-0037).
29. Un workspace no confiable ignora su `.omnicore/`. Un repo nunca configura providers, credenciales, permisos amplios ni sandbox (ADR-0039).
30. La auditoría sobrevive a la purga de sesiones, y la telemetría es solo local (ADR-0043).

## Reglas de trabajo

- Grafo de dependencias en ADR-0009, verificado por `tests/OmniCore.ArchitectureTests`. Cambiar el grafo = actualizar ADR y test en el mismo cambio.
- `OmniCore.Sandbox` no depende de nada de OmniCore: se comparte con OmniCoder (ADR-0008).
- Todo en .NET 10. **Única excepción:** `OmniCore.Protocol`, `OmniCore.Client` y `OmniCore.Sandbox` compilan también para net8, porque las consume OmniCoder. En esas tres no se usan APIs exclusivas de net10, y un test lo verifica (ADR-0038 §6).
- Multiplataforma: Windows y Linux completos, macOS best-effort. Nada de rutas ni APIs de plataforma fuera de las abstracciones de ADR-0038 §2. Los analizadores AOT están activos: sin reflexión dinámica en el Core.
- Configuración en YAML (YamlDotNet con generador estático), validada con schema (ADR-0039).
- Textos visibles al usuario: `LocalizedText` con recursos `es` (por defecto) y `en`. Los prompts al modelo, en inglés (ADR-0040).
- Errores tipados (spec §71), no `Exception` genérica para fallos de dominio.
- Toda operación async significativa recibe `CancellationToken` y lo propaga.
- Priorizar tests deterministas (`ScriptedModelProvider`, `FakeTool`, etc.) sobre tests con LLM.
- Todo valor configurable debe tener un test que demuestre que cambia el comportamiento (lección de OmniCoder, ADR-0007).
- Identificadores y código en inglés; documentación y mensajes al usuario en español.
- Datos de runtime **fuera del repo**: `<data>/workspaces/<WorkspaceId>/` (journal, blobs, worktrees), donde `<data>` es el directorio de datos de la plataforma (`%LOCALAPPDATA%\OmniCore` en Windows, `$XDG_DATA_HOME/omnicore` en Linux). `.omnicore/` del repo es solo configuración versionable (ADR-0039 §2).

## Estado

- **Hecho:** esqueleto (14 proyectos + tests de arquitectura; multi-target y analizadores AOT), revisión arquitectónica v0.2–v0.5 (ADRs 0001–0043), y **M1 implementado y auditado** (2026-09-25): runtime sin IA + Planning con journal durable (envelope `EventType`/`schema_version`, SQLite + in-memory), PlanService/ProgressReconciler/gates/watchdog/WorkingState, pipeline de tools con autorización real, crash/resume sin duplicar efectos, cliente con ClientProjection + renderers TUI/plain/JSON, y `omni sim` (YAML, `--json`, `--crash`/`--resume`). 86 tests verdes en Windows.
- **Nota toolchain:** el entorno es un runtime .NET basado en JVM; Terminal.Gui 2.5.0 no se enlaza como tipos. La TUI usa render ANSI propio sobre `ClientProjection` (contrato ADR-0030 §3 intacto).
- **Roadmap:** la fuente de verdad es `docs/architecture/arquitectura.md` §24. M2 (Explorer + modelo local) es lo siguiente.
