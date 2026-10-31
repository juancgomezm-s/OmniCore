# Sesiones, inicialización del proyecto y skills — 2026-10-30

> Seguimiento: el avance local descrito abajo se recupera e integra en la
> [consolidación de Main](main-publication-20261031.md), con desktop y M10 vigentes.
> Las referencias a falta de commit/CI corresponden a este checkpoint histórico.

> Actualización de revisión: véase [la entrega verificable detallada](sessions-initialize-skills-details-20261030.md) para el ajuste de emails/URLs, 244 casos finales, capturas y discrepancia de aprobación. Las cifras inferiores registran el corte previo de 230 casos.

Cambios locales sobre Main de OmniCore, base `04d226b9`. Sin commit, push, merge,
instalaciones ni cambios de permisos persistentes. La CI del main anterior no
valida este incremento.

## Comportamiento entregado

- Al abrir la TUI o cambiar de carpeta se elige Nueva conversación, Retomar
  existente o Continuar la última. La lista muestra título y fecha. Cancelar
  conserva la selección durable y el borrador, sin presentar el historial ni
  enviar input hasta elegir. Varias conversaciones pueden pertenecer a la misma
  carpeta. La recuperación del Host y la separación entre workspaces permanecen.
  No se cambia de carpeta durante trabajo principal o delegado en curso.
- `omni inicializar` / `omni initialize` y `/inicializar` / `/initialize`
  descubren fuentes locales y generan o actualizan agents.md. Se conserva el
  texto manual y se reemplaza sólo el bloque gestionado, con fuentes, hashes,
  síntesis determinista y diagnósticos; repetir sin cambios es idempotente.
  La TUI permite desplazar todas las fuentes y revisar el contenido generado.
  Este checkout incluye un `agents.md` raíz con las instrucciones del proyecto.
- Las skills aparecen en el catálogo, ayuda y autocompletado antes de crear
  sesión. Admiten `/skill-<id>` y, si está libre, `/<id>`, así como mención
  inequívoca del ID/nombre completo en texto humano nuevo. Se controlan
  ambigüedad, nombres solapados, aliases, extensiones consentidas y workflows.
  La activación usa el almacén y límites existentes, deduplica e inyecta una
  vez, revalida paquetes modificados y no concede permisos de herramientas.

Rutas y límites: [Inicialización](../project-initialization.md),
[Skills](../skills-human-invocation.md) y
[Selección de sesiones](session-opening-20261030.md).

## Evidencia efectiva

| Verificación | Resultado |
| --- | --- |
| `dotnet build OmniCore.slnx --no-restore -m:1 -v:q` | Correcta; 0 advertencias, 0 errores |
| Funcionales y regresiones finales | 230 total: 229 aprobadas, 0 fallos, 1 omitida; 55,517 s |
| Arquitectura sobre la misma imagen | 62 aprobadas, 0 fallos, 0 omisiones; 1,458 s |
| `git diff --check` | Sin errores de whitespace |

Evidencia local del runner (carpeta ignorada por Git):
`TestResults/sessions-init-skills-final-v3.xml`,
`TestResults/sessions-init-skills-final-v3.log`,
`TestResults/sessions-init-skills-architecture-v3.xml` y
`TestResults/sessions-init-skills-architecture-v3.log`.

Se ejecutaron en serie, sin reconstruir durante las pruebas. El filtro funcional
incluye skills, inicializador, SessionOpening, CLI/TUI de inicialización,
ConversationLifecycle, WorkspaceNavigation, ExternalAgentDiscovery, registros y
presentación de comandos, palette, aliases y cobertura del catálogo de idiomas.
Incluye primera petición HTTP real al proveedor local de fixture mediante
AskAsync, ConversationAsync y comandos slash, recarga de paquete modificado,
colisiones con consentimiento actualizado, dos sesiones por carpeta y reaperturas
con SQLite. Las pruebas TUI usan el framework y el driver de prueba reales, con
teclas, foco, listado, scroll, cancelación y trabajo principal/delegado controlado.

## Límites

La prueba `ProjectInitializerTests.Symlink_memory_and_broken_target_are_rejected_without_following_outside_paths`
quedó omitida porque el entorno no permite crear symlinks. No se modificaron
privilegios para forzarla. El inicializador mantiene sus comprobaciones de enlaces,
pero ese caso concreto no cuenta como ejecutado.

Las fuentes del inicializador son convencionales y acotadas; no importa bases de
memoria, transcripciones ni memoria de home. Ante exclusiones no admitidas o
metadata Git externa de un worktree omite fuentes con diagnóstico. No se usó un
LLM para sintetizar el archivo. Se validó con proveedor HTTP local y pruebas TUI
programáticas; no acredita una sesión humana de aceptación visual ni uso de un
proveedor externo autenticado. No se ha ejecutado CI de estos cambios locales.
