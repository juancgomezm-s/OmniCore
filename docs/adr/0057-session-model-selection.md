# ADR-0057: selección nativa de modelo y ruta por Session

Estado: construido y validado localmente en Host y consumidor Electron.
No cierra M9 ni la aceptación manual de M8.

## Contexto

La selección de `ModelPolicyService` es una preferencia de workspace. El chat
nativo de OmniCoder necesita elegir el modelo de su conversación sin cambiar
otras Sessions ni escribir configuración Pi. Mostrar un selector sin consumo
real en Core no satisface ese requisito.

## Decisión

Una elección explícita se guarda en el journal de la Session como
`SessionModelSelectionSet` y, después, `SessionModelSelectionRevised`. El
binding contiene revisión, modelo, proveedor, ruta, hash de identidad de ruta
y billing mode. Se revalida el historial contra la autorización canónica de
rutas. No se guardan credenciales en estos eventos.

Las Sessions sin binding conservan la resolución legacy; una query no crea
selección ni consentimiento. Un binding explícito debe resolverse exactamente:
si su modelo o identidad de ruta ya no coincide con la configuración, se
informa el bloqueo antes de invocar al proveedor. No hay fallback silencioso.
Las continuaciones de un Turn conservan su modelo y snapshot originales;
la elección se aplica al Turn nuevo. La prevalidación de adjuntos y la
inferencia tienen que usar el mismo resolver.

`conversation.new` persiste el snapshot inicial de rutas configuradas. Una
Session legacy sin ese snapshot devuelve `routingPolicyRevision: null`; una
selección válida con revisión esperada 0 lo inicializa bajo la misma barrera
que el binding o la oferta de consentimiento. Una query, selección obsoleta
o modelo desconocido no inicializa autoridad. No se inventa un Run.

La selección no modifica la política del modelo ni el techo de Session, los
grants o los presupuestos. Elegir un modelo más capaz no eleva autoridad. El
Host rechaza cambios cuando una ejecución/hijo/efecto pendiente impide cambiar
de modelo con seguridad. La comprobación de revisiones y la escritura durable
comparten la barrera de autoridad y el ownership existentes.

## Contrato stdio 1.0

Se negocian dos queries tipadas:

- `models.catalog`, capability `query.modelsCatalog`: catálogo local acotado,
  token `catalogRevision`, identidades de modelo/proveedor/ruta, billing,
  límites de contexto/output y modalidades de entrada/salida. No hace discovery,
  sondeos ni solicitudes de inferencia. No expone endpoints ni credenciales;
  estar configurado no acredita disponibilidad de un proveedor.
- `session.model.selection`, capability `query.sessionModelSelection`: Session,
  binding o null, revisión de selección (0 si no existe), revisión de routing,
  revisión del techo de Session, Run activo nullable, token del catálogo y
  consentimiento pendiente o null. La consulta es de lectura.

`session.model.select`, capability `command.session.model.select.user`, recibe
`sessionId`, `modelId`, `expectedSelectionRevision`,
`expectedRoutingPolicyRevision`, `expectedSessionPolicyRevision`,
`expectedRunId` nullable y `catalogRevision`, además de `cmd`. Core resuelve
el modelo y la identidad de ruta; no acepta un fingerprint del renderer como
autoridad. El token del catálogo impide aplicar una elección preparada contra
configuración obsoleta.

Una ruta no autorizada produce una solicitud canónica `ModelRouteConsent`
ligada a la Session. No crea un Run ficticio ni llama al proveedor.
`session.model.route.respond`, capability
`command.session.model.route.respond.user`, recibe `sessionId`, `interactionId`,
`optionId` (`allow_route` o `deny`), las tres revisiones anteriores,
`expectedRunId` nullable y `catalogRevision`. Ambos comandos comparan el Run
activo exacto; la existencia de un Run quiescente entre Turns no equivale a una
ejecución en vuelo. La aceptación sólo autoriza la ruta y billing exactos del
offer del Host; la denegación conserva selección y autorización anteriores.
La elección no equivale a consentimiento para gasto adicional por otras rutas.

Los dos comandos exigen prueba humana HMAC del payload exacto, revisión de
scope y receipts durables mediante `command.result.lookup`. Los duplicados
recuperan el resultado sin volver a mutar; un resultado incierto no se reenvía.
Main comprueba emisor, workspace, VM, generación y Session después de awaits
y antes de firmar. El renderer sólo transmite intención de selección/respuesta.

## Consumidor y aceptación

El SDK nativo decodifica DTOs acotados; Main y el adapter alimentan el composer
existente. El selector Core no aplica preferencias Pi ni presenta como efectiva
una selección aún pendiente/rechazada. El último modelo ejecutado y la elección
para el Turn siguiente son estados distintos cuando no se ha hecho inferencia.

Se exige prueba de Host stdio real y Electron: dos Sessions con selecciones
distintas; reapertura durable; CAS, firma y scope; repetición sin efectos nuevos;
allow/deny de ruta sin Run ficticio ni provider; cambio de configuración y
modelo sin ampliar seguridad; adjuntos validados con el modelo del próximo
Turn. Un fixture HTTP local acredita cableado, no aceptación de proveedor live.

[Evidencia, comandos, fallos corregidos y límites](../validation/m9-native-model-selection-20261017.md).
