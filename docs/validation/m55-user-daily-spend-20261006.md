# M5.5 — gasto diario entre workspaces

Verificado 2026-10-06 22:28 UTC / 16:28 America/Mexico_City.

ADR-0046 §3 requiere calcular el diario sobre todas las sesiones del día.
La ruta normal TuiTurnHost → OmniCliRuntime → ExplorerTurn antes solo consultaba
el journal del workspace actual. La regresión reprodujo una segunda llamada
en B pese a que A ya había registrado 0.60 USD con un tope diario de 0.50 USD.

## Implementación

- UserWorkspaceSpendReader consulta los journals de los otros workspaces del
  mismo directorio User. No incluye el workspace actual ni consulta cuentas.
- Cada journal se abre en modo SQLite ReadOnly, sin pooling ni migraciones.
  Las familias de ModelStep/resumen y meta-model se materializan dentro de una
  transacción de lectura consistente por journal. Las conexiones se cierran.
- Se reutiliza la validación existente de uso/coste y receipts CAS, usando el
  CAS del workspace de origen. El coste desconocido, el journal ilegible o la
  evidencia ausente bloquean la invocación; no se convierten en cero.
- Solo se incorpora el total diario externo. Session/Run permanecen locales,
  incluso si un workspace copió sus identificadores. Los días usan UTC.
- La consulta se repite antes de invocar; no persiste contadores derivados y
  no suma de nuevo el resumen de un Turn que ya tiene ModelSteps.
- La ruta normal y la ejecución de un plan aprobado reciben este lector.

Esto es un snapshot de evidencia de uso, no una factura ni una reserva atómica
de presupuesto entre procesos concurrentes. No implementa scheduler/joins M6.
La coherencia del consentimiento de continuar diario entre workspaces todavía
requiere auditoría: este bloque no declara resuelto ese requisito distinto.

## Evidencia reproducible

Todas las respuestas/modelos/precios/créditos son fixtures declarados. Se usan
HTTP loopback sin autenticación, SQLite y CAS reales privados; no se acredita
consulta autenticada, saldo real ni consumo facturado.

- RED previo: CrossWorkspaceDailyCapRegressionTests, 2 casos, 1 PASS / 1 FAIL,
  2.428 s (workspace B llegaba al proveedor).
- Primer GREEN: mismos dos controles, 2 PASS, 2.447 s.
- Final: 133 PASS / 0 FAIL / 0 SKIP, 10.226 s; build 0 errores/advertencias.
- Arquitectura: 56 PASS / 0 FAIL / 0 SKIP, 1.163 s; build 0 errores/advertencias.
- Controles adicionales: corrupción bloquea antes del proveedor, snapshots
  repetidos conservan IDs, gasto meta de otro workspace usa su propio CAS,
  ayer no consume el diario de hoy, CAS ausente bloquea, journal antiguo sin
  execution_id/source se lee sin migrarlo y conserva sus bytes.

Desde la raíz del checkout:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*CrossWorkspaceDailyCapRegressionTests' -class '*UserWorkspaceSpendReaderTests' -class '*Spend*' -class '*Budget*' -class '*Sqlite*'
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore -v quiet
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll -noLogo
```

Logs: C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/user-daily-final-*
y user-daily-architecture-*. Las cifras focales se solapan con bloques anteriores.
No hay suite completa verde posterior acreditada; el timeout M4 sigue pendiente.
