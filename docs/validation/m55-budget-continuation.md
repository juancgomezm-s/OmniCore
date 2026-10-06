# Presupuesto de usuario y consentimiento durable de ampliación

Actualizado 2026-10-06 11:46 UTC / 05:46 America/Mexico_City.

Implementación parcial de ADR-0037 §7 / ADR-0039, no cierre de la frontera de gasto.

`<config>/settings.yaml` carga `budget.session` y `budget.daily` como decimales
no negativos. Ausencia de campos conserva 5/20 USD; cero es un límite, no ilimitado.
DTO/YAML estático y `Schemas/user-settings.schema.json` describen la misma forma.
YAML inválido da diagnóstico de archivo/path/línea/columna. El runtime entrega ambos
valores a ExplorerTurn en PLAN y ACT; no lee esos valores desde el repo.

Un exceso monetario conocido genera una oferta en `InteractionRequested.SubjectJson`:
`budgetContinuation:1`, `scope:run|session|daily`, `baselineUsd`, `currentUsd`,
`newLimitUsd`, `runId` o `day` según alcance. Se conserva la opción existente
`allow_plus` con incremento 10 USD. El usuario debe responder por el comando de
interacción; el journal existente es la fuente durable. No se añade un contador de
ampliaciones en memoria ni un grant de gasto inferido de una credencial.

RunControlService rechaza una oferta legacy sin alcance, una oferta malformada, una
de día pasado, Run no activo o límite actual obsoleto. El replay exige la pareja
request/resolved con causa User, respeta la primera resolución/expiración y no suma
consultas duplicadas. Un cambio de baseline configurado invalida las ampliaciones
anteriores. Precio/uso desconocido y límites no monetarios ofrecen solo Detener.
La guardia de tokens/turns/tools se conserva; la comprobación monetaria de Host suma
consumo durable y actual, en lugar de un segundo guard que omita gasto anterior.

## Evidencia

### Uso desconocido dentro del mismo Ask

Un `ModelStepCompleted` nuevo cuyo `ReportedUsageFields` no incluye ambos Input/Output
se conserva con `CostUsd=null`, junto con el artifact y el uso que sí reportó el provider.
Si hay un tope monetario activo, Host emite BudgetExceeded inmediatamente después de
persistirlo, antes de ejecutar sus tools o iniciar otro ModelStep. No recalcula ese paso
como cero, ni ofrece `allow_plus`: ampliar un límite no convierte uso desconocido en medido.
Sin tope monetario, el control mantiene la continuación anterior. Un cero explícitamente
reportado en Input/Output sí tiene coste calculable y puede continuar.

El postcheck de sesión/día usa la suma de costes conocidos del guard de este Ask, no
una nueva valoración de tokens acumulados que ignoraría la disponibilidad de sus campos.
Tras reabrir SQLite/CAS, el coste desconocido sigue desconocido y bloquea nuevo gasto
de otra sesión del mismo workspace sin alterar eventos de la sesión original.
Esto no demuestra agregación User-wide entre workspaces ni reserva atómica.

- `unknown-step-usage-red-test.log`: 5 casos, 1 PASS/4 FAIL, 0 SKIP; el control uncapped
  pasaba y los casos con tope continuaban hasta EndTurn en lugar de bloquear.
- `unknown-step-usage-final-build.log`: 0 warnings/0 errores.
- `unknown-step-usage-final-focal.log`: 45 PASS/0 FAIL/0 SKIP, 3.096s; incluye Input-only,
  Output-only y None, Run/defaultcaps, cero medido, uncapped y reopen real de SQLite/CAS.
  Proveedor/usage/tool scripted: fixtures offline, no consumo autenticado.
- `unknown-step-usage-full.log`: 1853 casos = 1849 PASS/0 FAIL/4 SKIP por permisos
  symlink, 212.612s, exit0. Incluye los diez casos nuevos de este bloque; focales solapados.

Reproducción: runner existente con `-class '*UnknownStepUsageBudgetRegressionTests'`
y las clases SpendPricing, SuspendedSpendAccountingRegression, BudgetContinuationIntegration,
CrossSessionDailyCapRegression, SameSessionDailyCapControl y ExplorerTurnDurableProviderState.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*BudgetContinuationTests' -class '*BudgetContinuationIntegrationTests' -class '*UserBudgetConfigurationTests' -class '*SpendPricingTests' -class '*CrossSessionDailyCapRegressionTests' -class '*SameSessionDailyCapControlTests' -class '*SuspendedSpendAccountingRegressionTests' -class '*RunControlTests' -class '*RunControlEdgeTests'
```

71 PASS/0 FAIL/0 SKIP, build 0 warnings/errores.
SQLite/CAS reabiertos y respuesta por OmniServer prueban que el nuevo límite se
conserva: sesión no hereda a otra sesión; día sí comparte entre sesiones del journal.
Son proveedores/usage de fixture, NO consultas autenticadas ni consumo real.
Suite completa: 1611 casos = 1607 PASS / 0 FAIL / 4 SKIP por permisos symlink,
135.282 s. Los focales se solapan y no se suman.
Logs `budget-continuation-integration-tests.log` y `budget-continuation-full-suite.log`
en `C:\Users\juanc\.codex\omni-m55-three-20261006`.

## Pendientes de la frontera completa

- SessionRoutingPolicy y consentimiento de rutas de pago/desconocidas ya tienen
  integración y pruebas: [contrato](m55-session-routing-consent.md), [resume](m55-routing-resume.md).
  BillingMode explícito está preservado en adapters: [contrato](m55-provider-billing.md).
- El ledger diario actual consulta todas las sesiones del journal del workspace;
  no agrega otros workspaces del usuario. No acredita aún el tope diario User-wide.
- Reserva atómica antes de la llamada y liquidación/liberación posterior: dos sesiones
  concurrentes pueden pasar un precheck con el mismo saldo. El postcheck no es una reserva.
  Los límites declarados del registry ya se transmiten a Responses API y Anthropic;
  Chat compatible y el backend de suscripción Codex siguen sin una cota aplicada.
  Un valor declarado pero no enviado no es una cota máxima de coste; retries y
  disponibilidad de usage también deben quedar contabilizados. Véase el bloque siguiente.
- NoClient/Deny ya finalizan Run Failed/BudgetExceeded con aislamiento y command
  correlacionado: [integración y pruebas](m55-budget-lifecycle.md).
  Continúa pendiente la reanudación automática desde el cliente; el efecto durable
  de allow_plus no acredita por sí solo la experiencia completa de la TUI/CLI.
- Reproducción focal de esos escenarios y cuota de IncludedQuota.

No se desactivaron assertions, permisos ni redacción; no hay push ni modificación de main.

## Límite solicitado de salida en rutas nativas

`ModelSelection.MaxOutputTokens` es un `long?` aditivo: legacy conserva `null`,
un valor explícito debe ser positivo y no altera ModelId, RouteId ni ContextBudget.
Es una solicitud de salida total, no consumo medido ni reserva monetaria.
Host traduce el límite positivo del registry sólo para Responses API y Anthropic,
tanto al seleccionar en CLI como al componer probes. El fingerprint incluye el
límite solicitado: un cambio invalida el componente `model.descriptor`.

Responses API escribe `max_output_tokens` con el menor límite presente entre selección
y opciones. Rechaza valores menores que 16 antes del HTTP; no eleva silenciosamente
el límite. Este campo cubre salida visible y razonamiento según el
[contrato oficial](https://developers.openai.com/api/reference/python/resources/responses/methods/create).
El perfil Codex conserva su body anterior sin el campo; una solicitud explícita se
rechaza porque este bloque no cualifica ese parámetro en el backend de suscripción.
Esto no afirma que el backend lo soporte o no lo soporte: falta evidencia específica.

Anthropic escribe exactamente el límite seleccionado en `max_tokens`, sin aumentarlo
para acomodar razonamiento. El modo manual actual exige presupuesto de thinking >=1024
y menor que `max_tokens`, conforme al
[contrato de extended thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking).
Sin selección explícita mantiene default/budget+margin; la suma usa `long` para evitar
overflow de `int`. No introduce adaptive thinking ni presume soporte por nombre de modelo.
La fixture anterior de budget=500 se corrige a 1024 y comprueba 1034 con margin=10;
controles negativos independientes verifican el rechazo de presupuestos inválidos.

Evidencia offline, logs en `C:\Users\juanc\.codex\omni-m55-three-20261006`:

- `native-output-limit-red-test.log`: 28 casos, 13 PASS/15 FAIL/0 SKIP, 0.166s;
  reproduce límite ignorado, validaciones ausentes y overflow.
- `native-output-limit-final-build.log`: build de tests, 0 warnings/0 errores.
- `native-output-limit-final-focal.log`: 90 PASS/0 FAIL/0 SKIP, 3.374s.
  Incluye transporte del límite, selección Host, fingerprint, adapters SSE, CLI
  y cualificación. Los dos controles Host→selección→adapter inspeccionan el body
  capturado por un HttpMessageHandler local y la respuesta completada.
- `native-output-limit-full.log`: 1891 casos = 1887 PASS/0 FAIL/4 SKIP por
  permisos symlink, 231.777s, exit0; incluye las pruebas nuevas de este bloque.
  Las cifras focales se solapan y no se suman.

Estas fixtures no acreditan llamadas autenticadas, aplicación del límite por un
servidor real, cuotas ni consumo. No cierran Chat/Codex, reserva/liquidación atómica,
tope User-wide, contabilización de retries o los siete criterios de M5.5.
Reproducción focal: runner con las clases NativeOutputTokenLimitTests,
ModelSelectionOutputLimitContractTests, HostOutputTokenLimitTests,
OpenAIResponsesProviderTests, AnthropicMessagesProviderTests,
RuntimeFingerprintFactoryTests, ModelQualificationHostTests y CliEndToEndTests.

## Reportes numéricos inválidos y acumulación sin overflow

Un contador negativo en cualquiera de Input, Output, CacheRead, CacheWrite o
Reasoning invalida la valoración del reporte, aunque el campo auxiliar no esté
marcado como disponible. `ModelPricing.CostUsd` devuelve `null`, no cero ni un
importe negativo; una estimación que exceda `decimal` también queda no disponible.
Un cero explícito con precios conocidos sigue siendo cero; precios desconocidos
no se vuelven conocidos porque los tokens sean cero.

Host conserva primero `ModelStepCompleted`, artifact, uso y flags originales.
Bajo un tope monetario, el reporte inválido produce BudgetExceeded sin `allow_plus`
antes de tools/otra llamada. Sin tope, termina con error/TurnAbandoned, sin fabricar
una interacción de cuota. El mensaje de rechazo se conserva en la conversación,
pero no se emite el resumen numérico `ModelCompleted` de ese turno inválido.
El audit `turn.spend` registra `usageStatus=invalid` y omite input/output/coste,
para no presentar un agregado parcial o cero como una medición completa.

Las sumas por contador y de Input+Output se comprueban con `checked` después de
persistir la invocación individual. Si el total no cabe en `long`, el Turn abandona:
no hay summary `ModelCompleted` ni `TurnCompleted` de éxito. Los costes individuales
válidos ya persistidos se conservan. La lectura de pasos de un Turn abierto detecta
contadores negativos/sumas no representables y no autoriza nueva invocación.
El replay tiene controles dedicados de fixtures legacy sintéticas, descritos abajo;
no confundirlas con respuestas autenticadas ni con datos válidos de facturación.

`SpendGuard` rechaza costes negativos en lugar de clamp a cero; una acumulación de
tokens que desborda deja sus últimos contadores válidos intactos. Los contadores
de turnos y tools también usan incrementos comprobados. No cambia los límites ni
autoriza gasto adicional.

Evidencia reproducible offline en `C:\Users\juanc\.codex\omni-m55-three-20261006`:

- `invalid-step-usage-red-test.log`: 14 casos, 1 PASS/13 FAIL/0 SKIP, 1.309s;
  control de cero medido pasa, negativos continuaban hasta EndTurn/Error sin guard.
- `usage-overflow-red-test.log`: después del fix de negativos y antes de aplicar
  sumas comprobadas, 25 casos, 19 PASS/6 FAIL/0 SKIP, 1.382s; los overflows terminaban
  como EndTurn. Ninguna assertion se desactiva para corregirlos.
- `invalid-step-usage-final-build.log`: 0 warnings/0 errores.
- `invalid-step-usage-final-focal.log`: 80 PASS/0 FAIL/0 SKIP, 6.090s; incluye 26 casos
  InvalidStepUsageBudgetRegression (cinco campos, campos no reportados, sin topes,
  overflow entre pasos y de un paso, cero medido y reopen real SQLite/CAS), siete
  controles adicionales de precios y dos de SpendGuard. El reopen conserva flags/uso
  originales, verifica el blob CAS y bloquea nuevo gasto de otra sesión del mismo
  workspace sin modificar la sesión original. No prueba ledger User-wide.
- `invalid-step-usage-full.log`: versión previa al ajuste final del summary/audit,
  1926 casos = 1922 PASS/0 FAIL/4 SKIP symlink, 274.723s, exit0. No acredita ese ajuste.
- `invalid-step-usage-final-full.log`: versión final con summary/audit inválido
  comprobado, 1926 casos = 1922 PASS/0 FAIL/4 SKIP symlink, 274.340s, exit0.
  No incluye el siguiente paquete PersistedUsageReplayRegressionTests.

Focales solapados; no sumarlos. Proveedores, usage y tools son fixtures scripted;
SQLite/CAS se reabren realmente. No consultas autenticadas, cuota ni consumo real.
Runner focal: InvalidStepUsageBudgetRegressionTests, UnknownStepUsageBudgetRegressionTests,
SpendPricingTests, SpendGuardAccountingValidityTests, SuspendedSpendAccountingRegressionTests,
BudgetContinuationIntegrationTests, CrossSessionDailyCapRegressionTests,
SameSessionDailyCapControlTests y ExplorerTurnDurableProviderStateTests.

### Replay legacy: atribución y rechazo antes de invocar

`PersistedUsageReplayRegressionTests` agrega tres casos black-box sobre filas legacy
sintéticas, sin fingerprint, artifact ni coste autenticado. Dos Turns abiertos tienen
un contador negativo o dos pasos cuya suma desborda; al reanudar se abandona el mismo
Turn con motivo explícito, cero llamadas, sin nuevos ModelStepStarted ni summary de
éxito. El control negativo está en un Turn ya cerrado distinto: el nuevo Turn sí
invoca y completa. La protección no utiliza el último dato de toda la sesión como
si perteneciera al Turn actual. No deshabilita el guard de CanonicalStateTracker.

`persisted-usage-replay-build.log`: 0 warnings/0 errores.
`persisted-usage-replay-focal.log`: 83 PASS/0 FAIL/0 SKIP, 6.229s, añade estos tres
casos a las clases focales anteriores. La última suite completa sigue siendo 1926
(no incluye estos tres casos); no presentar este focal como full1929 ni sumarlo.
No cambia producción: añade evidencia al guard ya implementado.

## Sumas monetarias no representables

Los costes individuales y sus artifacts se conservan aunque una suma histórica de
Run/Session/día exceda decimal.MaxValue. ReadJournalSpend devuelve null para el total
no representable y lo marca incompleto; no publica cero, saturación ni total parcial
como medición. La lectura de invocaciones y de resúmenes legacy usa el mismo guard.
Con tope, la invocación siguiente se bloquea con BudgetExceeded, sin allow_plus para
un total desconocido. Sin topes, Ask devuelve Error y TurnAbandoned, no una excepción
que escape por encima del outcome. Un uso histórico simplemente desconocido sigue
conservando el comportamiento uncapped existente: no se confunde con overflow.

La suma del historial más el segmento actual se verifica antes de invocar y después
de persistir ModelStepCompleted, antes de tools. Si desborda tras un paso, se conserva
ese paso con su coste/usage/artifact; no se ejecuta su herramienta ni otro provider.
También se convierte de manera controlada un overflow del subtotal SpendGuard.
La suma decimal fallida de SpendGuard no altera el último total válido.

Pruebas y logs en omni-m55-three-20261006:

- money-overflow-red-test.log: 6 casos, 2 PASS/4 FAIL, 0.868s. Costes de historial
  sintéticos individualmente válidos 5e28 y4e28; overflow escapaba de Ask antes del try.
- money-overflow-fixed-focal.log: 66 casos, 4 FAIL. El outcome ya era correcto;
  la assertion del fixture comparaba DomainEvent por referencia después de reopen.
  Se sustituyó por comparación de TODOS los campos durables y refs, no se omitió historial.
- money-overflow-daily-focal.log: 69 PASS/0 FAIL/0 SKIP, 4.145s. Incluye diario entre
  dos sesiones: sólo la solicitante se bloquea, secuencia/envelope/payload/refs de la
  otra permanecen iguales, sin ampliación de presupuesto para un total no representable.
- money-overflow-current-focal.log: 75 casos, 4 FAIL por expectativa de string '2'
  frente a decimal legítimo '2.000000'; la prueba final compara importe EXACTO2m y
  verifica hash/size del artifact. Sin tolerancias ni redondeo.
- money-current-red-test.log: 6 casos, 2 PASS/4 FAIL, 0.735s. Se conservó el fix de
  lectura histórica y se retiró sólo el nuevo guard posterior al paso para reproducir
  el caso current: históricoMax-1 + coste actual2 desborda, frente al controlMax-3+2.
  Capped devolvía Error en lugar del bloqueo y uncapped ejecutaba la herramienta antes
  de detectar el overflow en la frontera siguiente. No afirmar que este RED es HEAD entero.
- money-overflow-restored-final-build.log: 0 warnings/0 errores.
- money-overflow-restored-final-focal.log: 75 PASS/0 FAIL/0 SKIP, 4.203s.
- money-overflow-full.log: 1964 casos = 1960 PASS/0 FAIL/4 SKIP por permisos
  symlink, 266.765s; runner terminó exit0. Incluye los 15 casos nuevos monetarios.

Son fixtures de costes, precios y providers; SQLite/CAS se reabren realmente en los
tests históricos. El coste actual2 usa el cálculo de pricing real contra respuesta
scripted (1 token a2M/M), no consumo autenticado ni una factura de proveedor.
Esto no completa ledger User cross-workspace, reservas/liquidación ni retries facturables.

Reproducción focal: runner xunitv3 con -class '*PersistedMonetaryOverflowRegressionTests'
-class '*CurrentMonetaryOverflowRegressionTests' -class '*SpendGuardAccountingValidityTests'
y las clases de presupuesto/replay indicadas arriba. Focales solapados, no sumables.
