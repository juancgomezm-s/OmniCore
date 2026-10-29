# Presupuesto de entrega de contexto web — 2026-10-28

Corrección de las dos excepciones identificadas en la [validación inicial](web-research-phase1-20261028.md). Implementación local en el workspace compartido, sin commit, push ni cambios de UI. Guía actualizada: [contrato de investigación web](../plans/web-research-phase1.md#entrega-al-modelo).

## Política implementada

1. La operación de red conserva su idempotencia. El mismo ID no vuelve a buscar ni descargar. Su replay devuelve control con `ContextSuppressed=true`, sin preguntas, candidatos, pasajes o errores de lectura. La respuesta tiene un techo global de 1024 bytes UTF-8.
2. El arnés transforma ese control en un resultado de tool con el mismo ID y texto vacío. Hace lo mismo con llamadas nuevas tras cualquier estado terminal. No hay reinserción gratuita de resúmenes terminales al modelo.
3. Un cuerpo original puede entregarse una sola vez por ID. La primera exposición está cubierta por la reserva original; una reclamación atómica en SQLite, después de seleccionar contexto y antes de llamar al proveedor, impide duplicados y carreras. El claim permanece consumido ante fallo, cancelación o despacho incierto. Recuperar evidencia requiere una nueva llamada de lectura, que cobra contexto de caché aunque no descargue otra vez.
4. La decisión persiste entre controladores, agentes y compactaciones. El guard se reconstruye desde la base existente aunque desaparezca la configuración. El historial se proyecta antes de compactarlo; las llamadas web conservan su ID y nombre, con argumentos `{}` para suprimir también `assess.quote`.
5. No se incorporan checkpoints anteriores sin versión de proyección, checkpoints manuales de sesión, resúmenes de Run ni paquetes heredados sin procedencia segura. Los checkpoints producidos en este camino llevan versión de proyección. Los bloques web no entran al material enviado al resumidor. No se restaura continuación remota ni payload opaco del proveedor.
6. Cada envío físico instrumentado verifica estado activo y plazo. El primer envío usa el cuerpo prepagado. Los reenvíos HTTP o tras refresh deben cobrar sus bytes antes del handler; si no hay cuota, se bloquean antes del envío, sin reabrir el terminal. Puede terminar el paso con error. Los adaptadores HTTP productivos usan `ProviderResilience` y el contador común; los proveedores inyectados sin transporte instrumentado reciben una admisión exterior por invocación.

El journal conserva los resultados y artifacts originales. Su resumen no permite recuperar automáticamente un cuerpo perdido antes de entregarlo; se requiere una nueva lectura pagada. Los contadores son conservadores, monotónicos y no admiten reembolsos.

## Pruebas específicas

`WebResearchControllerTests` cubre replay repetido de búsqueda y lectura, firma incompatible, reentrada con pregunta cerrada y llamadas nuevas de las cuatro operaciones tras agotar el presupuesto; exige respuesta suprimida sin evidencia y de hasta 1024 bytes.

`WebResearchContextDeliveryTests` ejecuta el camino real de `ExplorerTurn`, ejecutor y pipeline de permisos, capturando los `ModelRequest` entregados:

- Cuerpo y `assess.quote` presentes una sola vez; historial posterior vacío, con IDs preservados.
- Nueva lectura de caché con ID nuevo y cargo adicional.
- Llamadas terminales repetidas que entregan cero texto web sin crear intentos ni recursos.
- Dos instancias de guard compitiendo por el mismo cuerpo, y duplicados dentro de un request: una sola aparición admitida.
- Reentrada con configuración ausente, compactación y checkpoints de contexto/sesión anteriores: sin recuperar el marcador de evidencia en el proveedor primario o meta.
- Dos Lanes hijas reales en un mismo Run: una consulta y descarga compartidas, recuperación explícita pagada. El fixture crea las Lanes; no es una prueba completa del scheduler de delegaciones.
- Retry HTTP 429 con cuota: segundo envío cobrado. Sin cuota: handler recibe sólo el primero.
- Primer envío retenido tras vencer el plazo o cerrarse concurrentemente el Run: handler recibe cero envíos.
- Recibo pendiente suprimido ante los cuatro terminales.

`WebResearchGenerationRetryTests` utiliza el adaptador Responses con handler y refresh ficticios: 401 y reenvío admitido/cobrado con cuota, o bloqueo antes del segundo handler sin ella. Compara el texto decodificado enviado con el cuerpo exacto y el cargo por bytes; no consulta ningún servicio real ni resuelve secretos.

## Resultado y revisión

La compilación final de `tests/OmniCore.Tests/OmniCore.Tests.csproj` pasó con **0 advertencias y 0 errores** (46,14 s), con `--no-restore --disable-build-servers -m:1 -v:quiet`. La tanda web completa pasó **65/65** en 4,951 s. La regresión ampliada pasó **329/329** en 48,043 s, con cero fallos, errores u omisiones. `git diff --check` pasó. No se ejecutó la suite completa indiscriminadamente.

La ejecución ampliada reúne las seis clases web (`WebResearchControllerTests`, `WebResearchIntegrationTests`, `WebSearchProviderTests`, `WebReadTransportTests`, `WebResearchContextDeliveryTests`, `WebResearchGenerationRetryTests`), la regresión seleccionada de 155 casos registrada en la validación inicial y las clases `ExplorerContextCheckpointPublicationLeaseTests`, `ContextManagementTests`, `LaneContextIsolationTests`, `DelegationContextPacketTests`, `ExplorerTurnAssistantGroupingTests`, `VisibleReasoningProjectionTests`, `GenerationRequestAttemptScopeTests`, `GenerationRequestAttemptEvidenceTests` y `ContextPolicyFingerprintTests`. Los cambios en el arnés común justificaron esta única repetición ampliada posterior a las pruebas puntuales.

Se reutilizaron los workers existentes: Luna implementó las correcciones del controlador y los fixtures 401 en archivos disjuntos; Sol revisó el flujo y los deltas sin modificar ni ejecutar código; el agente principal integró el guard, persistencia y arnés. Los nombres de modelo son los solicitados; la interfaz no entrega verificación independiente del modelo efectivo. Sol no identificó P0/P1 residuales en la revisión final del código.

## Alcance de la garantía

La supresión y contabilidad cubren resultados y argumentos de `search.web` y `web.read` identificados por eventos canónicos, y las rutas de compactación y contexto reconocidas. No se afirma detección semántica de copias o paráfrasis en texto libre escrito por humanos o por el asistente; ese texto usa el presupuesto normal del Run. No hay un pool que agrupe objetivos equivalentes entre Runs o workspaces distintos. No se demuestra aislamiento universal de red de procesos externos.

No se iniciaron APIs reales, configuraron credenciales, hicieron copias grandes ni añadieron OCR/renderizado.

M9 recibió el alcance de los archivos compartidos y los avances. Los intentos de reporte a la sesión de origen durante esta corrección devolvieron `invalid turn/start params: AbsolutePathBuf deserialized without a base path`; no se modificó esa sesión ni su ruta para sortear el error.
