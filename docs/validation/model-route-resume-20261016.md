# Continuación después del cambio de modelo · 2026-10-16

## Causa y evidencia

La TUI respondía `allow_route`, pero sólo iniciaba la continuación si la interacción
pertenecía a una escalación por falta de contexto. Un cambio normal de modelo
quedaba con el permiso guardado y sin ejecutar la petición pendiente.

La consulta de sólo lectura al journal del incidente confirmó esta secuencia:

| Secuencia | Evento |
| --- | --- |
| 167 | Entrada del usuario persistida. |
| 168 | `ModelRouteConsent` para `gpt-6.1-sol`, `IncludedQuota`. |
| 169 | `RunAwaitingInput`. |
| 170 | `InteractionResolved`, respuesta del usuario `allow_route`. |
| 171 | `SessionRoutingPolicyRevised`. |
| 172 | `RunInteractionResumed`. |

No hubo `TurnStarted` ni llamada al modelo después del consentimiento. No fue
una denegación por Enter, pérdida del historial ni un error HTTP de Sol.
Se inspeccionaron metadatos de eventos, sin publicar prompts ni credenciales.

## Corrección

- La TUI continúa también el consentimiento de ruta normal y consume el evento
  Enter, para evitar que alcance un control que aparezca después de cerrar el diálogo.
- Si el permiso se acepta antes de que termine la invocación que solicitó la
  autorización, el callback espera a ese cierre y continúa una sola vez.
- El consentimiento nuevo conserva la secuencia de la entrada admitida, su lane,
  las instrucciones conversacionales y la resolución de razonamiento del intento.
  Se usa JSON generado en compilación, compatible con las comprobaciones AOT.
- La continuación exige resolución explícita del usuario, revisión de política,
  mismo Run activo, misma ruta y facturación, y entrada pendiente sin consumir.
  Se vuelve a comprobar la entrada dentro de la exclusión de invocaciones.
- No crea otra conversación, no vuelve a registrar el mensaje ni toma instrucciones
  de un turno anterior. La escalación por contexto conserva su ruta de reanudación.
- Una petición conversacional continúa como tal aunque el Run conservado esté en
  modo Act. El refuerzo temporal de razonamiento se consume al empezar el turno
  autorizado y no vuelve a aplicarse al siguiente mensaje.

Los consentimientos de versiones antiguas carecen de esa instantánea previa al
turno: no se reconstruyen automáticamente con instrucciones antiguas. Los mensajes
guardados siguen disponibles. Esta reparación no reenvía por su cuenta peticiones
antiguas al abrir la aplicación.

## Validación

La primera serie aprobó 13 pruebas sin omisiones: cambio normal, reapertura del
runtime, rechazo, cambio de endpoint/facturación, intención sustituida, Enter en
la TUI y aceptación mientras finaliza la invocación inicial. El adaptador HTTP,
journal, política y runtime son reales; el servidor de respuestas de las pruebas
es local y controlado. No acredita una llamada a ChatGPT remoto.

| Serie final | Resultado |
| --- | --- |
| Conversación, Markdown, routing y CLI | 223 aprobadas, 0 fallidas, 0 omitidas. |
| Continuación normal, Act/Plan, Local/IncludedQuota, reapertura, Enter, carreras, cuotas, escalación y ciclo del refuerzo | 23 aprobadas en Release, 0 fallidas, 0 omitidas. |
| Arquitectura | 62 aprobadas, 0 fallidas, 0 omitidas. |
| Publicación de la CLI completa en Release | Aprobada. |

Las series se solapan; no representan una suma de casos únicos. La serie final
de 23 casos valida los últimos ajustes de Act y del refuerzo. Los casos Act
comprueban también OpenAI Responses con el servidor local controlado.

Evidencia en `C:/Users/juanc/.codex/omnicore-linux-20261014/`:

- `route-resume-tests.log`: primera serie.
- `route-resume-regression.log`: 223 casos.
- `route-resume-final-tests-v3.log`: 23 casos finales.
- `route-resume-architecture.log`: arquitectura.
- `route-resume-publish-final.log`: publicación.
- `route-resume-captures/`: celdas reales del diálogo Terminal.Gui a 80×25,
  con captura SVG/texto y vista PNG inspeccionada.

Se abrió una sola instancia de la TUI completa con la versión publicada después
de cerrar la anterior. No se inició WSL. No se acredita CI remoto ni una petición
a ChatGPT remoto en esta entrega.
