# Reserva monetaria compartida: fundamento, todavía sin cableado

ADR-0011 §6 exige reservar el máximo antes de una llamada pagada y liquidar con el
uso real; ADR-0037 §7/0046 §3 establecen los límites de Run, Session y User diario.

`SqliteSpendReservationStore` es un ledger separado del journal de uso. Cada
admisión toma una transacción SQLite inmediata y FULL, lee mediante callback
readonly el gasto canónico y valida todos los ámbitos más sus reservas pendientes.
No suma importes en floating point: almacena decimal invariante y suma con checked.
Un fallo leyendo evidencia aborta, no se interpreta como gasto cero.

Estados: reserved antes de envío; dispatched durable antes de enviar; settled
después de persistir el recibo canónico; released sólo si todavía no se despachó.
Una reserva dispatched no caduca ni se libera por cancelación/timeout. Un estado
desconocido aborta la admisión. Las repeticiones de ID nunca autorizan otro envío,
incluidas las ya liquidadas. El caller sólo puede invocar tras `Admission.Reserved`.
La liquidación y liberación son idempotentes, pero un recibo/importes contradictorios
se rechazan. Liquidar un importe superior al máximo conserva el sobreconsumo real:
el Host debe tratarlo como tal, no descartarlo por exceder la reserva.

El callback y la liquidación son fronteras internas, no endpoints de renderer.
El ledger por sí solo no verifica que el recibo exista: el Host debe persistir y
verificar la evidencia canónica antes de liquidar. Los importes settled son audit
data y no se suman otra vez al consumo canónico. Para el límite diario, la identidad
del ámbito se mantiene estable entre fechas; así una llamada incierta no desaparece
del total pendiente a medianoche. El gasto liquidado del callback sí usa la fecha
del journal correspondiente.

## Evidencia reproducible

Build `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet`:
0 warnings / 0 errores.

```text
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*SpendReservationStoreTests' -class '*CanonicalWriterArchitectureTests'
```

Resultado terminal: 14 PASS / 0 FAIL / 0 SKIP, 2.620s. Incluye 10 casos del ledger
y 4 del guard de escritores canónicos. El caso multiproceso inicia dos hijos
propios independientes y sincroniza ambos antes de reservar: exactamente uno
Reserved y uno Insufficient. No consulta proveedores, cuotas ni credenciales.

Auditoría readonly de Luna detectó que un estado desconocido podía omitirse de
pendientes. RED reproducido: 1 FAIL, 0.489s; fix conserva esa assertion.
Logs `spend-reservation-state-red.log` y `spend-reservation-multiprocess-final.log`
en `C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/`.
Root implementó el ledger y sus tests; Luna sólo auditó, sin producir este código.

## Pendiente antes de acreditar la corrección del presupuesto

Actualización 2026-10-07 00:07 UTC: `ModelInvocationCostBound` calcula una cota
conservadora desde capacidad declarada de entrada, techo explícito de salida
aplicado por el request y cantidad máxima de envíos. No usa ContextBudget ni
token estimates como techo físico. Precio/capacidad/salida/intentos desconocidos,
negativos o cálculo no representable devuelven null, nunca cero. Precio declarado
cero es distinto de ausencia de tarifa. La cota no se escribe como consumo real.
Focal nuevo: 23 PASS / 0 FAIL / 0 SKIP, 2.655s,
9 casos de cota + 10 ledger + 4 guard; `spend-reservation-bound-final2.log`.
El primer intento de las teorías nullable falló por Int32 vs Nullable Int64 en
InlineData: fixture corregido con literales long; no RED del cálculo productivo.
La cota todavía no tiene callers productivos, igual que el ledger.
La regresión de Explorer se reforzó además para exigir una llamada y 0.30 USD:
no puede pasar vacíamente bloqueando las dos llamadas.

Este fundamento todavía NO está conectado a ExplorerTurn ni a compactación.
`ConcurrentSpendAdmissionTests` sigue RED: dos workspaces gastan 0.60 USD ante
un máximo de 0.50 USD. La suite completa verde de 291842f precede esa regresión.

El siguiente bloque debe calcular un máximo real conocido de entrada/salida e
intentos de generación, reservar en todos los caminos pagados antes de enviar,
persistir y liquidar idempotentemente, y conservar reservas cuando el uso de un
intento sea incierto. Un bound de intentos no acredita facturación ni uso real de
los reintentos. No sustituirlo por estimaciones presentadas como mediciones ni
liberar automáticamente reservas inciertas. No se implementó scheduler/joins M6.
