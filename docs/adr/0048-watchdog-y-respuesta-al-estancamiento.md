# ADR-0048 — Watchdog en Runs reales y respuesta al estancamiento (`StallPolicy`)

- **Estado:** Aceptada (2026-10-09), por instrucción del propietario del proyecto.
- **Precisa:** ADR-0016 §9 y ADR-0036 §7.
- **Relacionado:** ADR-0007 (`HarnessPolicy`), 0033 (`NoticeBlock`), 0034 (interacciones), 0035 (Run y conversación), 0040 (idioma de los prompts) y 0046 (routing autorizado y escalación).

## Contexto

ADR-0016 §9 define el watchdog de progreso y una respuesta por `StallPolicy` (`Replan`, `Diagnose`, `EscalateModel`, `SplitTask`, `AskUser`, "en ese orden por defecto y con límites"). La auditoría de cableado del 2026-10-09 encontró que:

- `ProgressStalled` solo se emitía en `omni sim`. En los Runs reales nadie evaluaba el watchdog.
- `HarnessPolicy.StallThresholdTurns` solo entraba en el fingerprint: el valor configurable no cambiaba el comportamiento.
- `StallPolicy` era un enum sin uso, y ADR-0016 no fijaba qué hace cada respuesta.

## Decisión

### 1. Detección

- El watchdog se evalúa **al cerrar cada Turn** (`TurnCompleted`, `TurnInterrupted` o `TurnAbandoned`) de cualquier Lane: raíz, ACT, delegada o etapa de workflow. Un Turn suspendido (`user.ask`, cuota, presupuesto) no se evalúa hasta que termina.
- Se vigila el **item actual** del Plan (R7) cuando está `InProgress`. Se cuentan los Turns de sus Lanes vinculadas o, sin vínculos, los de la Lane raíz (ADR-0036 §7).
- **Plan sin descomponer.** Un Run real crea su Plan con un único item raíz `Pending` que solo avanza por mutaciones explícitas. Si el Plan sigue así (solo el item raíz, sin vínculos, `Pending` o `Ready`), se vigila ese item: representa el objetivo del Run en curso. Sin esta regla, el watchdog no actuaría en un `omni act` cuyo modelo no use `plan.propose`.
- **Input humano.** Un `UserInputReceived` humano reinicia el conteo, y el Turn que lo responde no cuenta: es dirección nueva, no un agente girando en falso. Solo cuentan los Turns que el agente encadena sin dirección nueva. No es progreso del Plan, así que no reinicia la cadena de respuestas ni `lastProgressAt`. Los prompts que genera el runtime (feedback del bucle ACT, continuación tras escalar) llevan un origen `Runtime(...)` y no reinician. En una conversación dirigida por el usuario el watchdog no se dispara: cada Turn llega con input humano.
- **Validaciones.** Precisa ADR-0036 §7. `RunValidationStarted` no es señal: es el inicio, no un resultado. `RunValidationRejected` es señal salvo que repita los mismos faltantes que el rechazo anterior. Un rechazo idéntico es el bucle típico: el modelo declara que terminó sin cambiar nada.
- El umbral `N` es `HarnessPolicy.StallThresholdTurns` del modelo del Turn. Sin `HarnessPolicy` vale 6.
- **Un episodio, un evento.** El conteo empieza en la última señal de progreso **o** en el último `ProgressStalled` del mismo item. Cada `N` Turns sin señal se emite un único `ProgressStalled`. `ProgressStalled` no es señal de progreso.
- `lastProgressAt` es la marca de tiempo (UTC) de la última señal de progreso de esas Lanes. Si no hubo ninguna, es la del primer evento del Run.
- `omni sim` usa la misma detección.

### 2. Selección de la respuesta

- `StallPolicyEvaluator` es una función pura del Engine. Decide la política determinista; el modelo nunca la elige (INV-032).
- **Cadena por defecto:** `Replan → Diagnose → EscalateModel → SplitTask → AskUser`.
- El paso de la cadena es el número de respuestas ya seleccionadas para ese item desde su última señal de progreso. Una señal de progreso reinicia la cadena.
- **Disponibilidad.** El Host la decide y la registra:
  - `EscalateModel` exige la Lane raíz dentro del bucle ACT, donde el runtime dirige el siguiente Turn. En la conversación (`ask`, TUI) el siguiente Turn lo envía el usuario y la escalación no persistiría entre mensajes, así que queda no disponible (`UserDrivenTurn`). Exige además que no haya un modelo fijado (`OMNI_MODEL`) ni una escalación en curso, y que la política de routing ofrezca una ruta siguiente.
  - `AskUser` exige la Lane raíz: una Lane delegada no tiene un canal humano propio.
  - Las respuestas no disponibles se saltan y quedan anotadas en el evento.
- El siguiente paso es la posición posterior a la **última política seleccionada**, no el número de respuestas: así, saltar una respuesta no desalinea la cadena. `step` cuenta los intentos del episodio.
- Con la cadena agotada se repite `AskUser`: el usuario acota el bucle. Si tampoco está disponible (Lane delegada), solo se registra `ProgressStalled`, que queda visible como aviso.
- **Evento nuevo:** `StallResponseSelected { runId, planItemId, policy, step, skipped[] }` (`stall.response_selected`). Se escribe en el mismo batch que su `ProgressStalled`. `skipped` usa códigos estables (`EscalateModel:UserDrivenTurn`, `AskUser:DelegatedLane`, …).

### 3. Ejecución de cada respuesta

- **`Replan`, `Diagnose`, `SplitTask`:**
  - Producen una **directiva** en el contexto de los siguientes Turns de las Lanes vigiladas. Es un `ContextItem` `Constraint`, `Pinned` y `RegenerateEachTurn`, con procedencia `core.stall-policy` (INV-023).
  - Es una proyección del journal (INV-006): desaparece con la siguiente señal de progreso o cuando cambia el item actual.
  - El texto va en inglés (ADR-0040). Las mutaciones las propone el modelo con `plan.propose` y las decide `PlanService` (INV-012). Si la Lane no tiene `plan.propose`, la directiva pide informar del bloqueo.
- **`EscalateModel`:**
  - El Host usa el flujo existente `ModelEscalationRequested/Approved/Completed`, con la causa nueva `EscalationCause.ProgressStalled`, y continúa el mismo Run con un prompt de continuación generado por el runtime, como el feedback de los gates.
  - La escalación solo elige rutas de la `SessionRoutingPolicy` y respeta el modo `auto|ask|deny` y el consentimiento (INV-031, ADR-0046).
  - Si no se completa (se deniega el consentimiento o falta la credencial), el Run sigue con el modelo actual. El siguiente estancamiento avanza la cadena.
- **`AskUser`:**
  - El Host publica `InteractionRequested` de tipo `StallResolution` junto con `RunAwaitingInput`, en el mismo batch (patrón de `PlanApproval`).
  - Opciones decididas por el servidor (INV-025): `continue` (`allow`) y `stop` (`deny`). Por defecto, `stop`.
  - `continue` reanuda el Run con `UserInputReceived`. `stop` cancela el Run como `CancelRun` (ADR-0035 §6).
  - En consola interactiva se pregunta en línea. Con un cliente de interacción (TUI) responde su overlay. Sin cliente, el comando termina con `InputRequired` y la interacción queda pendiente para `omni resolve`.
- Ninguna respuesta cambia el modo, los permisos, las rutas ni los presupuestos (INV-028, 031 y 033).

### 4. Visibilidad

- `ProgressStalled` y `StallResponseSelected` se exponen por el protocolo.
- El cliente los presenta como `NoticeBlock` (ADR-0033): `! Sin progreso en el item actual del plan tras N turns` y `! Respuesta al estancamiento: <respuesta>`. La salida plain del CLI nombra el item.

## Fuera de alcance

- La detección de loops por repetición (último punto de ADR-0016 §9) queda pendiente.
- La actividad derivada `Stalled` de la Lane (`LaneActivityProjection`) sigue sin uso en producción. Es otro hallazgo de la auditoría (A7).

## Consecuencias

- El valor configurable `StallThresholdTurns` cambia el comportamiento y tiene un test que lo demuestra.
- Un agente que gira en falso recibe primero una corrección barata (directiva), después un modelo más capaz si la política lo permite, y al final la decisión del usuario.
- El Run nunca termina por el watchdog sin intervención humana: `stop` es una cancelación explícita del usuario.
