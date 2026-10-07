# Reserva monetaria compartida: fundamento e integración

## Recibos de cualificación User (fundamento, todavía sin wiring diario)

`QualificationProbeReceipt` identifica una ejecución local de cualificación y un
probe; **no** representa AgentExecution/Session/Run/Turn. Se almacena append-only
en `user.db.qualification_probe_receipts`, independiente del perfil y sus copias
Stale. Clave `(ExecutionId, ProbeId)`, reserva única y ordinal único por ejecución.
Un duplicado exacto es idempotente; un conflicto no sobrescribe el original.

Conserva key hash, suite/versión/task-set, inicio y fin UTC por probe, scoring y
terminación independientes, BillingMode observado, máximo reservado, cota de
intentos, envíos observados, usage/máscara y coste USD nullable. El coste de este
contrato es el cálculo del Host a partir de usage y precios explícitos, **no** un
débito de cuenta. Un importe mayor que la reserva se conserva para auditar el
sobreconsumo. Decimal se guarda como texto invariante y se recupera exactamente.
No se suma cache/razonamiento otra vez; los slots no reportados son SQL NULL, no
mediciones cero. Un contador reportado ausente, máscara/bool/enum desconocido o
coste sin usage válido aborta la lectura. El cero observado de sends no prueba
que una operación fuera gratis ni autoriza liberar una reserva.

Los textos/resultados/errores pertenecen al CAS redactado, no a columnas de texto
del recibo. `RecordProbeReceipt` exige namespace User y referencia verificable,
toma lease CAS y transacción SQLite inmediata con synchronous FULL; el recibo se
confirma antes de cualquier liquidación. Un fallo deja la referencia sin commit
para GC, no inventa un recibo exitoso. Este store no invoca providers ni liquida.
El schema tiene marcador propio; si se instaló y falta la tabla, store y GC fallan
cerrado. Una base legacy sin capacidad instalada recibe una tabla vacía, sin
fabricar recibos a partir de perfiles históricos.

GC incluye cada recibo como raíz, incluso sin perfil ni suite completa, y sigue
sus refs transitivas. Un blob ausente/corrupto o metadata inválida aborta el mark
antes de borrar cualquier huérfano. Los recibos no dependen de ProfileRevision ni
`source_run_revision`: copiar un perfil no representa una ejecución nueva.

Verificación offline: focal 76 PASS / 0 FAIL / 0 SKIP, 4.086s,
`qualification-receipt-store-expanded.log`, build 0 warnings/errores. SQLite/CAS
reales privados; no consultas autenticadas ni gasto real. Barrido anterior a los
últimos cuatro controles: 319 = 318 PASS / 1 FAIL, 12.336s,
`qualification-receipt-all-with-daily-red.log`; el FAIL sigue siendo la integración
diaria de cualificación no conectada. No acredita cierre ni full verde.

Endurecimiento final (01:13 UTC): cabecera CAS canónica obligatoria
`schema=omnicore.qualification-probe-receipt.v1` y propiedad única `receipt`,
generada con `CanonicalObservationJson()` sin la propia Evidence (no hay hash
autorreferencial). Identidad/UTC/máscara/importe/procedencia se contrastan exactamente
al escribir y leer. El writer de texto redacta output/error antes del hash; si la
redacción cambia la cabecera, el recibo no se acepta. Un coste relacional alterado
con blob todavía válido se rechaza. El JSON usa decimal invariante canónico.
Uso inconsistente se rechaza incluso con coste nulo; un view no puede instalar el
marcador ni hacerse pasar por la tabla. Auditoría Luna: RED33=31PASS/2FAIL1.988s,
`qualification-receipt-audit-red.log`; fallo view inicialmente enmascarado por
cleanup del fixture al dejar abierto el store inesperadamente admitido. El fixture
ahora siempre lo cierra. No atribuir ese IOException a GC o proveedor.

Final: 81 PASS / 0 FAIL / 0 SKIP, 4.624s,
`qualification-receipt-header-final.log`, build0warnings/0errores. Barrido actual
328 = 327 PASS / 1 FAIL, 13.029s,
`qualification-receipt-header-all-with-daily-red.log`; solo el diario Host pendiente.
Cifras anteriores solapadas, no sumar. No consultas autenticadas ni gasto real.

Reproducción:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*QualificationProbeReceiptStoreTests' -class '*M5QualificationEvidenceStoreTests' -class '*M5UserQualificationGcRootTests' -class '*QualificationProbeExecutionBoundaryTests' -class '*CanonicalWriterArchitectureTests'
```

Pendiente inmediato: productor Host conectado al observer, CAS por probe,
reconciliación recibo-antes-Settle previa al lock de admisión y lectura diaria
compartida desde ASK/ACT/meta/cualificación. No sumar artifacts de perfiles además
de los recibos, ni valores settled del ledger como segunda fuente de consumo.

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

## Historia del fundamento (00:07 UTC, anterior a la integración)

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
En ese checkpoint la cota todavía no tenía callers productivos, igual que el ledger.
La regresión de Explorer se reforzó además para exigir una llamada y 0.30 USD:
no puede pasar vacíamente bloqueando las dos llamadas.

En ese checkpoint el fundamento NO estaba conectado a ExplorerTurn ni a compactación.
`ConcurrentSpendAdmissionTests` estaba RED: dos workspaces gastaban 0.60 USD ante
un máximo de 0.50 USD. La suite completa verde de 291842f precede esa regresión.

## Integración 2026-10-07 00:34 UTC / 2026-10-06 18:34 local

ExplorerTurn reserva antes del envío primario y de compactación. CLI ASK, ACT
aprobado y escalación que vuelve a RunTurnAsync usan el mismo límite compartido;
los wrappers de telemetría conservan el bound del adapter. La lectura de gasto
canónico es fresca dentro de la transacción inmediata; no vuelve a sumar el guard
del Ask ni el importe settled del ledger. Run, Session y User diario se validan
juntos. Los IDs incluyen Session/Run/Lane/Turn/paso o invocación meta.

Antes de invocar se persiste dispatched; antes de liquidar se persiste el recibo
canónico con Barrier y se conserva su EventId. GenerationRequestAttemptScope
observa los SendAsync de generación, no login ni cuota. Un envío observado con
uso completo puede liquidar. Varios envíos con solo el recibo final pasan a
uncertain: pending conserva el máximo menos la porción acreditada. No se
reconstruye el coste de intentos fallidos a partir del último resultado.
Cero envíos observados significa desconocido, no gratuito; solo el seam declarado
de un intento sin reintentos admite su recibo completo. Uso/coste desconocido,
fallo de recibo o cancelación después de dispatch conserva la exposición.

### Contratos de no envío

Eventos aditivos v1, sin modificar schemas existentes ni inventar Usage=0:

| Evento | Identidad del payload | Hecho canónico |
| --- | --- | --- |
| model_step.not_dispatched | TurnId, StepIndex | Started durable, pero nunca se entró al delegate del provider |
| meta_model.invocation_not_dispatched | InvocationId, RunId, Operation, ModelFingerprint | Started durable, pero nunca se entró a StreamAsync |

Se escriben con Barrier y sin token cancelado antes de liberar reserved.
Un fallo del marcador deja la reserva retenida. Nunca se emiten tras entrar al
provider: ausencia de texto, excepción o timeout no demuestran ausencia de coste.
Los lectores exigen inicio anterior, misma identidad/scope, marcador único y
ausencia de completion contradictorio. Los starts legacy sin evidencia siguen
incompletos. Los filtros de lectura de otros workspaces incluyen ambos tipos.
El consumo se deriva del journal, no del ledger operativo ni de estos marcadores.

### Evidencia y límites de la afirmación

RED pre-send: segundo Turn Cancelled en vez de EndTurn, 1 FAIL / 1.083s,
`spend-before-send-red.log`. Green inicial 18 PASS / 7.916s. Focal de reservas,
cancelación y retry: 67 PASS / 21.855s, `spend-before-send-expanded.log`.
Cross-workspace: 10 PASS / 4.805s, `spend-not-dispatched-cross-workspace.log`;
valid/missing/duplicado/identidad incorrecta/marcador anterior al inicio para
primary y meta. Incluye CLI, SQLite y HTTP loopback offline, sin autenticación.
Reopen y fallo de Barrier primaria, fallo de marcador meta y migración del ledger
sin pending_usd se verifican en el bloque focal ampliado. Las cifras se solapan.

La focal ampliada inicial dio 124 casos / 123 PASS / 1 FAIL / 75.192s:
un fixture de gasto meta de ayer no declaraba capacidad/salida/intentos. Se ajustó
su descriptor a límites explícitos, sin cambiar las assertions de cuota/gasto/CAS.
Resultado posterior terminal: 116 PASS / 0 FAIL / 0 SKIP, 26.869s,
spend-reservation-wired-final2.log, build 0 warnings/errores. El filtro posterior
excluye ContextManagementTests (ocho casos pasados en la ejecución anterior) e
incluye los cinco controles de migración; resultados no acumulables entre filtros.

Esta integración NO acredita todavía todas las generaciones pagadas: la auditoría
readonly de Luna encontró que ModelQualificationHost respeta MaxTotalCostUsd de
la suite, pero no consulta el ledger User diario ni incorpora su coste en ese
acumulado. Pendiente regresión offline e integración de ese camino, sin reabrir M5.
La última full de 2284 casos sobre 291842f precede estas fuentes: no atribuirla a
este bloque. Todos los precios, uso y cuotas de estos tests son fixtures, no
facturación ni consultas autenticadas. No se implementó scheduler/joins M6.
Root implementó producción, integración y pruebas; Luna auditó las fronteras y
detectó el caso cancelpre-send y la brecha de cualificación, sin escribir código.
