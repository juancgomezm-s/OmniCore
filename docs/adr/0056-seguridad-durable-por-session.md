# ADR-0056: techo durable de seguridad por Session

**Estado:** aceptada para la construcción M9-K; implementación y validación continúan abiertas.

## Contexto

La categoría `ModelPolicyCategory` describe un preset de configuración, pero no era autoridad
durable de una conversación. Un cambio de modelo o de modo Plan/Act no puede ampliar por sí solo
los efectos permitidos por una Session. El Host debe conservar permisos, grants, sandbox y
restricciones del workspace como controles independientes, y rechazar trabajo preparado contra una
revisión anterior antes de persistir input o lanzar efectos.

## Decisión

Core guarda por Session una proyección versionada `SessionSecurityPolicySet` en el journal. El evento
incluye la categoría informativa, capacidades y límites resueltos, revisión y procedencia; las
decisiones de ejecución usan esos campos, no el orden numérico de categorías. El default de Session
nueva vive en configuración de usuario con revisión SHA-256; si falta, se usa `ObserveOnly` como
fallback conservador y se informa que no estaba configurado. El usuario puede guardar
explícitamente ese mismo valor. Una Session nueva persiste el snapshot exacto de default que
comparó, bajo la lease de configuración, junto con `SessionCreated`.

La procedencia identifica el origen de la Session (`Native`, `Imported` o `LegacyMigrated`) y no
cambia al seleccionar otra categoría. Sólo el origen Core correspondiente puede crear el primer
evento: default nativo, migración de una Session previa o revisión explícita de un importador. La
migración legacy se realiza al recuperar la lease de escritura, sólo si existe un `SessionCreated`
canónico del mismo ID/envelope; una query de lectura no crea eventos y un puntero huérfano no crea
una Session. La ruta visible del workspace es presentación y no prueba autoridad.

Antes de admitir input nativo, el Host compara `expectedPolicyRevision` para una Session existente,
o `expectedDefaultRevision` para crear una nueva. La comprobación y la persistencia comparten la
barrera de autoridad y la misma transacción de journal que admite input/Run. Un valor obsoleto se
rechaza antes de consumir el borrador o iniciar el pipeline. El Host negocia
`conversation.admission.session-security.v1`; un cliente que no reciba esa capability no debe
activar el envío nativo revisionado.

El techo se intersecta con la política de modelo, perfil de agente, workspace, permisos/grants y
sandbox en la admisión y de nuevo antes del efecto. Contadores y límites de archivos, líneas,
tools, pasos, orquestación y generación pertenecen al Run/Turn vigente y no se reinician al cambiar
el modelo. Una reducción de política no se confirma mientras exista un hijo o efecto incierto;
`Deferred` mantiene visible la revisión anterior. Una nueva decisión `Ask` requiere aprobación
exacta del intent vigente. Reconciliación `Applied`/`NotApplied` cierra la incertidumbre; los
resultados en conflicto o irresolubles permanecen bloqueados.

La política no concede permisos ni reemplaza confirmaciones de acciones que las requieren. Los
gates de build/test configurados por el usuario conservan su clasificación Host exacta, argv,
perfil de agente, grant y sandbox; la política de Session los limita sin convertirlos en tools
elegidas por el modelo.

## Consecuencias y estado

El checkpoint Core `08a4a6f` pasó el build de solución con cero advertencias y cero errores; el foco
de seguridad y lifecycle pasó 56/56. Los tests están en el commit `06af380`; comandos y logs se
registran en [el acta M9-K](../validation/m9-block-k-20261017.md). Esos resultados solo validan ese
checkpoint. El HEAD posterior incluye cambios de admisión y reanudación explícita de cuestionarios
que todavía no se han compilado ni probado.

En OmniCoder, el build de app y 9 pruebas unitarias pasaron en el ciclo K2. El E2E encontró fallos de
tipos, lectura durante transición de conexión y registro de la consulta `conversations`; se aplicaron
correcciones y el E2E integrado está en reintento. No se declara verificado el recorrido final
Core→stdio→SDK→Electron.

K permanece abierto hasta que pase el E2E real, se validen los cambios Core posteriores al checkpoint
verde y se comprueben todos los puntos de admisión. El lifecycle nativo de listar/crear/abrir/
renombrar/compactar está implementado en el checkpoint K2 de código, sujeto a esa validación. La
propiedad de Sessions importadas requiere un importador Core real; hasta entonces no se anuncia ruta
de import. L tiene lógica Core para respuestas y reanudación explícitas de cuestionario, todavía sin
consumidor SDK/UI ni validación. La paridad de los demás mutadores de runtime y M (ConPTY/procesos
persistentes) siguen abiertos. La aceptación manual de M8 corresponde al usuario y permanece
pendiente.
