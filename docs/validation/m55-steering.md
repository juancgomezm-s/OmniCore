# Steering explícito — contrato incremental M5.5

ADR-0046 §1 distingue tres entradas: respuesta a una interacción, FollowUp para un
Turn posterior y Steering para el Turn activo. Sin declaración explícita sigue
siendo FollowUp. Steering no crea una intención nueva ni otro Turn.

## Eventos v1

Los tres payloads conservan `SteeringId`, `RunId`, `LaneId` y `TurnId`.
El envelope conserva además Session, Sequence y las atribuciones existentes.

- `turn.steering_received`: `InputPartsJson` y `Origin?`; identifica la entrada
  explícita destinada a ese Turn, no texto generado por el modelo.
- `turn.steering_applied`: `StepIndex`; identifica la frontera del ModelStep
  que incorpora la entrada, no una aplicación en medio de una invocación.
- `turn.steering_dropped`: `Reason` no vacío; deja constancia del descarte.

`SteeringId` es un identificador durable independiente de InteractionId y
FollowUpId. No existe supersesión automática ni conversión a FollowUp.
`InputPartsJson` se conserva exactamente en la serialización del contrato;
la admisión de texto debe aplicar la política de redacción existente.

Los codecs y el source generator registran los tres eventos como schema v1.
No se cambia ningún schema previo ni se necesita un upcaster de v0.
Las restricciones de lifecycle y consumo pertenecen a la cola/consumidor,
no quedan acreditadas por un roundtrip de los records.

## Evidencia reproducible y alcance

Logs bajo `C:\Users\juanc\.codex\omni-m55-three-20261006`:
`steering-contract-build.log` y `steering-contract-focal.log`.
Build sin warnings/errores; focal 48 PASS/0 FAIL/0 SKIP, incluyendo los tres
roundtrips, ModelStep, FollowUp y envelope/scopes. Son pruebas deterministas
de contratos, no consultas autenticadas ni consumo real de proveedores.

Este bloque todavía **no** acredita admisión desde IOmniClient, cola durable,
aplicación entre ModelSteps, replay tras suspensión ni descarte terminal.
Tampoco implementa concurrencia de lanes, scheduler o joins de M6.
