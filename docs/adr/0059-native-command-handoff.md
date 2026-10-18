# ADR-0059: ejecución nativa durable de comandos humanos

Estado: prompt y recuperación previa al inicio validados localmente; extensión
y acceso/continuación workflow conservan aceptación pendiente.
[Evidencia reproducible y límites](../validation/m9-native-command-handoff-20261018.md).
M8 espera pruebas manuales del usuario y M9 permanece abierto.

## Problema

El comando legacy `command.invoke` resuelve un handler y deja un resultado en
campos volátiles del Host. CLI/TUI consultan `commandOutcome` y llaman al runtime
para ejecutar el prompt, la extensión forzada o el workflow. Exponer ese ACK en
OmniCoder no ejecuta el comando y no permite recuperar el handoff tras reiniciar.

## Decisión

Conservar el camino legacy y añadir el wire trusted `command.invoke.user`.
El cliente elige un ID canónico de un catálogo efectivo del Host y presenta
argumentos acotados. Session, Run y revisiones de modo, seguridad, selección y
routing, catálogo y fuente son condiciones de comparación; nunca autoridad
aportada por el renderer. Main firma sólo después de comprobar sender,
workspace, VM, Session y generación, incluidos los awaits finales.

El Host resuelve de nuevo el handler y su elegibilidad, trust y contenido de
fuente, y persiste una admisión ligada al ID de envelope/receipt. Argumentos y
resultado expandido van a un artefacto protegido con propósito ligado al scope;
el evento de admisión sólo contiene identidades, revisiones y referencia/fingerprints. La
referencia debe entrar al índice de artefactos del envelope para retención/GC.
Publicación del blob y evento respetan la lease de publicación existente.
El Turn conserva su contrato normal de input/contexto redactados; proteger el
handoff no significa ocultar todo texto público del prompt en el historial.

Después del ACK, un consumidor Core ejecuta el pipeline existente. Un prompt
alcanza el modelo; una extensión forzada alcanza ToolRuntime y sus permisos;
un workflow alcanza el catálogo compilado y las delegaciones reales. El evento
de inicio se persiste antes del primer efecto. No se inventan autorización
Orchestrate, contexto raíz, snapshot, grants ni cierre de deuda Build/Test.
Un handler sin consumidor real no se anuncia como ejecución nativa soportada.
La rama de workflows aún no acredita acceso desde OmniCoder: el cliente ofrece
Plan/Act y falta autorización Orchestrate; una espera posterior al inicio
tampoco dispone todavía de continuación de workflow. La presencia del executor
no cierra esas dos dependencias.

Un primer PromptCommand en una Session vacía debe crear su Run/Task/Lane/Plan
canónicos dentro de esa misma Session, con seguridad/defaults/perfil vigentes,
sin chat preparatorio ni input ficticio. Se distinguen las condiciones CAS
previas sin Run del binding efectivo creado para ejecutar. Extensiones y
workflows conservan el requisito de Run y modo autorizados. La continuación de
un permiso/cuestionario conserva el Turn existente y no readmite el comando.

`command.catalog` presenta el catálogo y snapshot efectivos; `command.recovery`
es una lectura pura y acotada de estado sin argumentos sensibles. Reiniciar,
leer, hacer replay o consultar un receipt no inicia trabajo. `command.continue`
es una intención humana nueva y revisionada para una admisión sin inicio; deriva
su contenido del journal/artefacto canónicos. Una ejecución iniciada de resultado
incierto queda bloqueada y nunca se relanza automáticamente. Reintentar un mismo
envelope devuelve su receipt; otro payload con el mismo ID entra en conflicto.
No se promete exactly-once general.

ClientActions locales conservan su camino. El nuevo borde no es shell/RPC
arbitrario ni permite al renderer ejecutar una tool por su nombre. Las acciones
se presentan en la interfaz existente con los textos propios es/en/fr/it.

## Aceptación

Stdio con runtime real y Electron→Host→HTTP local ya demuestran primer Explain
sin chat previo en la misma Session, rechazo de scope/revisiones/fingerprint
incorrectos antes de efectos y recuperación explícita de una admisión sin inicio
tras reabrir runtime/Host sobre SQLite. El corte ACK no es muerte de proceso OS.
Faltan extensión con efecto/Ask y acceso/continuación workflow. Comprobar indexación del artefacto y
ausencia de argumentos sensibles en query/replay/journal. La prueba CLI de
workflow existente no acredita el nuevo handoff nativo. Registrar compilación,
focos específicos, scanner IPC y procesos huérfanos antes de publicar el bloque.
