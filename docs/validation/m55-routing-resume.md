# M5.5 — Reanudar el destino consentido de una escalación

Implementación sobre los eventos existentes de escalación, interacción y política.
No introduce scheduler/joins ni un nuevo contrato de historial.

## Frontera durable

`ModelEscalationConsentReplay.FindGrantedPending` es una lectura pura de la Session/Run.
La última solicitud ContextLimit gobierna el intento. Sólo acepta una oferta posterior
con identidad física y BillingMode exactos, resolución User/allow_route y revisión
persistida de política. No reutiliza un grant anterior para otra solicitud. Rechaza
Approved/Completed, expiración, otro Run/Session, endpoint/protocolo/facturación cambiados
y revisiones sin consentimiento; consulta la política final validada por replay.

El caller de ModelEscalationRequested ahora aporta TurnId/LaneId de origen. Los eventos
legacy siguen legibles, pero una escalación sin origen atribuible/input durable no
se reanuda automáticamente. No se toma otro mensaje del composer como sustituto.

`TuiTurnHost.ResumeEscalationAsync(interactionId)` revalida sesión seleccionada, Run activo,
intento pendiente, origen y ruta actual. El destino aprobado prevalece sobre default/router;
se reutiliza el mismo Run, sin session.input/act nuevos. El historial del journal conserva
el input original. Los FollowUp encolados después quedan reservados para otro Turn.
Un rechazo, cancelación o cambio de configuración no invoca el destino.

Los primeros ContextOverflow ahora persisten el input/promoción junto al TurnStarted,
antes de Abandoned. Esto evita perder una intención si el overflow ocurre antes del
primer ModelStep. Una reanudación no inserta un input vacío ni re-promueve FollowUps.
También el envío normal de chat consume el input ya persistido por session.input:
no entrega otra vez el mismo texto a Explorer ni añade un input vacío. Las pruebas
exigen un único UserInputReceived para la intención original.

La TUI comprueba el CommandAck de interaction.respond. Sólo para un consentimiento
ligado a escalación inicia el callback; deny nunca lo inicia y una respuesta rechazada
muestra error. Esta lectura de elegibilidad no concede permisos: el Host revalida todo
antes de la invocación. La selección inicial sin escalación no usa este callback.
Si el overlay se acepta antes de que la invocación originaria termine de devolver 3,
el callback queda pendiente hasta ese checkpoint. No se pierde por el flag de turno
ocupado; cancelar la invocación no dispara esa reanudación pendiente.

`ModelEscalationApproved` queda una sola vez; la reanudación identifica la InteractionId
que la autorizó. Completed sólo se escribe tras observar ModelStepCompleted en el mismo
Run después del inicio del intento. Accepted/Approved no se presentan como éxito del Run.
También la escalación auto reutiliza el Run y condiciona Completed a respuesta real.

## Evidencia reproducible

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*CliEndToEndTests' -class '*ModelEscalationConsentReplayTests' -class '*FollowUp*' -class '*SessionRouting*'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -method '*Model_route_consent*' -method '*Rejected_route_response*'
```

- Focal Engine/Host/CLI/FollowUp/SQLite: 61 PASS, 0 FAIL, 0 SKIP.
- TUI real con driver Terminal.Gui, botones/teclado, OmniServer SQLite y turn-host
  explícitamente fake: 4 PASS, 0 FAIL, 0 SKIP, incluida aceptación antes del return 3.
  Acredita callback/ack/journal, no proveedor.
- Adapter real HTTP loopback con SSE controlado: reanudación normal y nueva instancia
  del runtime conservan intención, sólo invocan target-model y no consumen el FollowUp.
  Un callback repetido no reinvoca; deny/endpoint/billing cambiados producen cero HTTP.
  Auto autorizada conserva un único Run y ordena Completed después de ModelStepCompleted.
- Estos SSE, claves y usage son fixtures: no consultas autenticadas ni consumo real.
- Suite completa final: 1682 casos, 1678 PASS, 0 FAIL, 4 SKIP por permisos symlink,
  140.553 s (`routing-resume-single-input-full-suite.log`). Build 0 warnings/0 errores.
- Logs `routing-resume-single-input-tests.log` y `routing-resume-tui-race-tests.log` en
  `C:\Users\juanc\.codex\omni-m55-three-20261006`. Las cifras se solapan con la suite global.
- Se preservan errores intermedios: routing-resume-initial-tests.log falló por fixture
  que escribía YAML en data en vez de config, corregido sin debilitar assertions.
  routing-resume-boundary-tests.log ejecutó un DLL previo tras build fallido y NO acredita
  el estado final; routing-resume-current-tests.log seleccionó cero tests y tampoco cuenta.

Pendientes: selección inicial y resume budget, cuota/ledger/reservas, circuito breaker,
RouteId del router y los demás criterios de ADR-0046. M5.5 no se declara cerrado.
