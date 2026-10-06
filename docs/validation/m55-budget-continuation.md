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
  Antes hace falta aplicar límites de salida reales: MaxOutputTokens del registry no
  se transmite hoy a Chat/Responses. Un valor declarado pero no enviado no es una cota
  máxima de coste; retries y disponibilidad de usage también deben quedar contabilizados.
- NoClient/Deny ya finalizan Run Failed/BudgetExceeded con aislamiento y command
  correlacionado: [integración y pruebas](m55-budget-lifecycle.md).
  Continúa pendiente la reanudación automática desde el cliente; el efecto durable
  de allow_plus no acredita por sí solo la experiencia completa de la TUI/CLI.
- Reproducción focal de esos escenarios y cuota de IncludedQuota.

No se desactivaron assertions, permisos ni redacción; no hay push ni modificación de main.
