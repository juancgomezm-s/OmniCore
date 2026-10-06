# Presupuesto: denegación y ausencia de cliente

Checkpoint 2026-10-06 07:01 UTC / 01:01 America/Mexico_City.
ADR-0037 §7, ADR-0003 y ADR-0034; ampliación de presupuesto en
[m55-budget-continuation.md](m55-budget-continuation.md).

## Implementación

- `RunControlService.Respond(..., "deny")` resuelve con `InteractionCause.User`
  y termina el Run originario con `RunFailed(Cause="BudgetExceeded")` en el mismo batch.
- `ResolveBudgetWithoutClient` sólo permite BudgetExceeded/deny, nunca aumentos ni
  respuestas de cuestionarios. Resuelve con `InteractionCause.NoClient`.
- El Run se obtiene del envelope de la petición durable, no del Run activo más reciente.
  Una petición sin origen, con origen inexistente o terminal se rechaza sin escribir.
- Se cancelan tareas, lanes, items de plan y llamadas no iniciadas del Run originario;
  se interrumpen sus Turns abiertos y expiran sus otras interacciones pendientes.
  No se cierran Run/Lane/Task/interacciones de otra atribución o Session.
- Herramientas iniciadas pasan a `EffectUnknown`; las ya desconocidas permanecen así.
  No se deshacen efectos ni se declara su éxito. `UnreconciledEffects` sigue bloqueando
  el inicio de un Run hasta reconciliación.
- OmniServer expone la denegación sin cliente como command interno con causation y
  `CommandOutcome`/FirstSeq/LastSeq. Rechaza una petición que no pertenece al Run seleccionado.
- Runtime PLAN/ACT diferencia la capacidad explícita `HasInteractionClient` del uso de
  Console.ReadLine. `TuiTurnHost` declara esa capacidad: no convierte una TUI conectada
  en NoClient por suprimir entrada de consola.
- Con cliente o consola interactiva se devuelve InputRequired/BudgetExceeded (exit 3);
  sin cliente se aplica el default deny y exit 1. Una petición legacy sin atribución
  bloquea la ejecución sin atribuirle un Run por inferencia.
- La proyección compartida muestra el detalle de presupuesto y etiquetas de Continuar/
  Detener, con default deny, y retira el overlay tras resolución. No se modificó Git.

## Evidencia reproducible

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*BudgetClientLifecycleTests' -class '*BudgetDenialLifecycleTests' -class '*BudgetContinuationTests' -class '*BudgetContinuationIntegrationTests' -class '*RunControlTests' -class '*RunControlEdgeTests' -class '*RunControlCommandOutcomeTests' -class '*InternalExplorerAskCommandTests' -class '*ClientTests'
```

80 PASS / 0 FAIL / 0 SKIP; build 0 warnings/errores.
Suite completa: 1638 casos = 1634 PASS / 0 FAIL / 4 SKIP por permisos de symlink,
134.095 s (`budget-lifecycle-full-suite.log`). Las cifras focales se solapan.
`budget-lifecycle-tests-fixed.log`, en
`C:\Users\juanc\.codex\omni-m55-three-20261006`.
La primera reproducción detectó un fixture sin PlanCreated: se añadió el plan que
la assertion exigía, sin eliminarla ni suavizarla. Log previo conservado.

Integración real en proceso de ExplorerTurn → OmniServer → RunControlService y
ProtocolMapper → ClientProjection. El precheck con tope cero produjo **cero llamadas
al provider**. No es una consulta autenticada ni evidencia de consumo real ni prueba
visual de una ventana de OmniCoder.

## Pendiente

Continuar ya aumenta el límite durable, pero no se acredita todavía la reanudación
automática del turno desde el botón de la TUI o una selección en consola. La consola
recibe la petición y puede responder mediante el command de resolución existente.
También faltan SessionRoutingPolicy/consentimiento, cuota incluida, ledger User-wide
entre workspaces y reservas/liquidación concurrentes. No se declara M5.5 cerrado.
