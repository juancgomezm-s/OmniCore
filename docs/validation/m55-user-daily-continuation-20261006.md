# M5.5 — consentimiento diario User-wide

2026-10-06 22:35 UTC / 16:35 America/Mexico_City.

El tope diario usa todas las sesiones del usuario (ADR-0046 §3). La opción de
continuar ya se reconstruía de InteractionRequested/Resolved/Expired y se
compartía entre sesiones de un workspace, pero no entre workspaces.

## Cambio

- El snapshot de otros journals incluye las tres familias de interacción.
- UserDailyBudgetContinuation reutiliza BudgetContinuation.Limit, calculando
  cada journal por separado y tomando el máximo límite autorizado. No empareja
  la petición de un journal con una resolución de otro aunque compartan IDs.
- ExplorerTurn aplica el límite diario del mismo usuario y día UTC. El permiso
  de Session/Run sigue restringido a su ámbito original.
- El servidor persistente normal recibe el lector User-wide. RunControlService
  valida el límite vigente local y externo antes de aceptar otro allow_plus.
  Una oferta obsoleta se rechaza sin escribir una resolución.
- Se conserva la semántica existente: solo InteractionCause.User, incremento
  tipado de 10 USD, mismo baseline de configuración y día, resolución única.
  Cambiar el baseline o el día invalida el aumento previo. Sin evidencia no se
  eleva el límite; un journal ilegible también bloquea el gasto por su guard.

No se crean commands nuevos, ventanas de cuota, reservas concurrentes ni lógica
de scheduler/joins. Es un límite derivado del journal, no saldo o factura real.

## Evidencia

Fixtures declarados: HTTP loopback auth:none y costes sintéticos, SQLite/CAS
privados reales y commands del servidor normal. No acreditan consultas
autenticadas ni cargos reales.

- RED: cinco casos CrossWorkspaceDailyCapRegressionTests, cuatro PASS y un FAIL,
  3.290 s: A autorizó continuar diario pero B no llegó al proveedor.
- Final focal: 121 PASS / 0 FAIL / 0 SKIP, 10.355 s, build 0 errores/advertencias.
- Arquitectura: 56 PASS / 0 FAIL / 0 SKIP, 1.188 s, build 0 errores/advertencias.
- Controles: daily cruza workspace después de cerrar A; session no cruza;
  segundo aumento valida el límite de A y sobrevive a reopen; stale offer no
  escribe; doble respuesta no duplica; cambio de día/config no hereda aumento;
  petición y resolución en journals distintos no forman consentimiento.
- La batería incluye gasto primario/meta y controles de presupuesto anteriores.
  Conteos solapados, no sumar a los del bloque anterior.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*CrossWorkspaceDailyCapRegressionTests' -class '*UserDailyBudgetContinuationTests' -class '*UserWorkspaceSpendReaderTests' -class '*Budget*' -class '*Spend*'
```

Logs: C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/
user-daily-consent-red-*, user-daily-consent-final2-* y
user-daily-consent-architecture-*. Un filtro intermedio inválido no ejecutó
pruebas; final2 sí las ejecutó. No se acredita full verde posterior.
