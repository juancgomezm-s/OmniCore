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

El primer checkpoint Core (`7d69feb`, corrección de ruta redactada `725a3f1`) implementa dominio,
journal, configuración revisionada, admisión de input, límites de ejecución, recuperación legacy y
controles localizados en TUI. El SDK/IPC/UI de OmniCoder consume las consultas y cambios humanos de
política; el envío de chat vuelve a comprobar el scope después de materializar adjuntos. El build
multi-target del checkpoint Core `725a3f1` compiló sin advertencias ni errores; el foco de pruebas
de seguridad sigue en curso. El contrato y sus criterios no están cerrados por ese build.

Pendiente para cerrar K: validar la corrección bajo llamadas directas, legacy, TUI y stdio; corregir
cualquier fallo focal; completar la admisión del executor del plan aprobado (`approve_execute`) en
`OmniCliRuntime`; cablear lifecycle nativo de lista/crear/abrir/renombrar/compactar; y probar Host
real más Electron→Host con revisión obsoleta y preservación de borrador. La propiedad de
Sessions importadas requiere un importador Core real; hasta entonces no se anuncia ruta de import.
L (rutas/modelos, interacciones, commands/skills/memoria y plan/lanes/worktrees) y M (ConPTY,
procesos persistentes y lifecycle) siguen abiertos. La aceptación manual de M8 corresponde al
usuario y no se atribuye a esta decisión.
