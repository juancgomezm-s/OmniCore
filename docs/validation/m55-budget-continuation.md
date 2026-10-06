# Presupuesto de usuario y consentimiento durable de ampliación

2026-10-06 06:44 UTC / 00:44 America/Mexico_City.

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

- SessionRoutingPolicy y consentimiento de rutas de pago/desconocidas:
  el routing automático actual aún no aplica esta autorización.
  BillingMode explícito ya implementado y preservado en adapters: [contrato](m55-provider-billing.md).
- El ledger diario actual consulta todas las sesiones del journal del workspace;
  no agrega otros workspaces del usuario. No acredita aún el tope diario User-wide.
- Reserva atómica antes de la llamada y liquidación/liberación posterior: dos sesiones
  concurrentes pueden pasar un precheck con el mismo saldo. El postcheck no es una reserva.
- NoClient/Deny ya finalizan Run Failed/BudgetExceeded con aislamiento y command
  correlacionado: [integración y pruebas](m55-budget-lifecycle.md).
  Continúa pendiente la reanudación automática desde el cliente; el efecto durable
  de allow_plus no acredita por sí solo la experiencia completa de la TUI/CLI.
- Reproducción focal de esos escenarios y cuota de IncludedQuota.

No se desactivaron assertions, permisos ni redacción; no hay push ni modificación de main.
