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

El checkpoint publicado Core `5c315ba` incluye seguridad durable, lifecycle y respuesta/reanudación
de cuestionarios. El build de solución y 120 casos focales únicos se verificaron en los ciclos
descritos en [el acta M9-K](../validation/m9-block-k-20261017.md); esa cifra no representa una sola
ejecución completa. Incluye recuperación tras reiniciar el Host y restauración conservadora del
presupuesto de mutación del Turn. Los grants de lectura/versionado no se restauran como autoridad.

El checkpoint publicado OmniCoder `c85a77c` incluye consumidor SDK/Main/UI para cuestionarios y
lifecycle. Pasaron el E2E real Electron→Host→fixture HTTP de dos Sessions y el de respuesta de
cuestionario en el mismo Run, además de las pruebas focales registradas. La matriz de CI de ese
checkpoint pasó en Windows/Ubuntu y su package gate. El checkpoint posterior `2b4c0d2` retira grants
y confirmaciones redundantes con siete E2E únicos en focos separados y conservación del ACK
probada; el acta K recoge la evidencia y la CI completa aprobada. Esto no acredita inferencia de un
proveedor del usuario ni cierra todos los escenarios SEC-CONV.

La suite completa de Core sobre `5c315ba` detectó 121 fallos de 4860 casos en Ubuntu. El análisis y
la reparación se siguen en [el informe de CI](../ci-failure-analysis.md). No se extrapolan los
resultados focales a esa suite. La reparación publicada `91b07fb` ya tiene
[CI completa aprobada](https://github.com/juancgomezm-s/OmniCore/actions/runs/39082408888):
Windows 4853 PASS/9 SKIP, Ubuntu 4840 PASS/22 SKIP y cero fallos, además de instalación
npm y paquetes Linux. K permanece abierto hasta completar SEC-CONV-01–13 y los
consumidores pendientes; esa matriz no acredita cambios posteriores en construcción.

La propiedad de Sessions importadas requiere un importador Core real; hasta entonces no se anuncia
ruta de importación. El cuestionario, Permission y PlanApproval de la Lane raíz tienen consumidor
y validación integrada; [el acta de elecciones](../validation/m9-native-choices-recovery-20261017.md)
registra recuperación explícita, denegación en el contexto del modelo y sus límites. Modelos/rutas,
acciones de Lanes hijas y los demás mutadores de L siguen pendientes. M (ConPTY/procesos
persistentes) no está construido. La aceptación manual de M8 corresponde al usuario y permanece
pendiente.
