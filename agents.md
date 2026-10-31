# Instrucciones del proyecto OmniCore

Lee primero `CLAUDE.md` y las directivas vigentes que enlaza. Sus checkpoints
históricos no amplían el alcance de una tarea actual. Las decisiones de
`docs/adr/` prevalecen sobre la especificación `docs/spec/OmniCore-v1.md`.
La arquitectura y el estado de los hitos están en `docs/architecture/arquitectura.md`.

## Trabajo y validación

- Trabaja sobre el checkout autorizado; consulta `git status` antes de editar y
  conserva el trabajo ajeno. No publiques, fusiones ni cambies otros productos
  sin una solicitud explícita.
- Código e identificadores en inglés; documentación y mensajes en español.
  Los textos de interfaz deben tener recursos es/en/fr/it; los prompts al modelo
  permanecen en inglés.
- Usa .NET 10. Protocol, Client y Sandbox también compilan para net8: conserva
  sus límites de APIs y el grafo de dependencias de ADR-0009/0038.
- Compila con `dotnet build OmniCore.slnx`. Los tests usan xUnit v3 y Microsoft
  Testing Platform: `dotnet test --solution OmniCore.slnx`.
  Prefiere fixtures deterministas y ejecuta primero el foco de la modificación.
- Aísla `OMNICORE_DATA_DIR` y `OMNICORE_CONFIG_DIR` en pruebas; nunca uses los
  diarios, credenciales, servidores o cuentas reales del usuario como fixture.

## Autoridad y sesiones

- El Host escribe el journal; la UI utiliza commands/eventos de IOmniClient.
  No ejecutes herramientas sin el Permission Engine y su AuthorizedToolIntent.
- Las skills, memorias, hooks y modelos nunca conceden permisos, cambian el modo
  elegido por el usuario ni autorizan gasto. Conserva los límites por Session,
  el aislamiento de Lane y la recuperación durable; no reintentes efectos inciertos.
- Varias conversaciones pueden pertenecer a una carpeta. La TUI pide elegir
  Nueva conversación, Retomar existente o Continuar la última; cancelar no
  selecciona ni envía mensajes. El Host conserva la selección para recuperación.
- `omni inicializar` y `/inicializar` descubren fuentes locales y actualizan sólo
  el bloque gestionado de este archivo, preservando estas instrucciones manuales.
  Consulta `docs/project-initialization.md` para exclusiones y límites.
- Las rutas de skills y sus comandos/menciones están documentados en
  `docs/skills-human-invocation.md`. Usa el catálogo vigente; las menciones no
  conceden permisos para ejecutar herramientas, scripts ni hooks.
