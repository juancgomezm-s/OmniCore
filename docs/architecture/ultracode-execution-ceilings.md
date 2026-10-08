# Cotas de ejecución UltraCode — ADR0047

La autorización durable del Run es un techo, no una recomendación. El Host
comprueba su estado vigente antes de materializar contexto que pueda disparar
compactación, antes de cada invocación primaria/meta y antes de cada ToolCall.
La comprobación posterior a la respuesta conserva primero su recibo de uso;
una respuesta con consumo incompleto no autoriza herramientas ni otro paso.

`UltraCodeExecutionGuard` reconstruye del journal propio del Run:

- MaxTurns: identidades `TurnStarted` distintas. Tres ModelSteps no son tres
  Turns; suspender/reanudar tampoco inicia otro Turn.
- MaxToolCalls: identidades `ToolCallRequested` distintas. Una aprobación o
  reanudación de la misma llamada no crea un presupuesto nuevo.
- MaxSpendUsd: recibos primarios y meta mediante `CanonicalSpendReader`. Nunca
  se suma de nuevo el resumen del Turn. La próxima llamada requiere una cota
  conservadora con capacidad de entrada declarada, salida máxima aplicada,
  número finito de envíos y precios completos. Uso/coste desconocido bloquea;
  no es cero. Un coste estimado no se presenta como una factura medida.
- MaxElapsedSeconds: fecha UTC durable del grant. La expiración se verifica
  en cada frontera y se enlaza con la cancelación del proveedor en curso cuando
  el plazo cabe en el temporizador del sistema. Plazos superiores al rango de
  ese temporizador siguen verificándose en cada frontera, sin overflow.

Los contadores abarcan el Run, también tras reinicio o selección de otra
autorización dentro de él. `BudgetContinuation/allow_plus` no amplía estas
cotas: su interacción ofrece sólo detener. Una nueva selección de autoridad
requiere el command confiable del usuario y su validación ordinaria. Una
revocación se lee del estado vivo; no se restaura desde el fingerprint histórico.

Se serializan las invocaciones síncronas Explorer del mismo Session/Run y
`IEventStore` dentro del proceso. Dos instancias no pueden admitir el único
Turn restante sobre la misma lectura. La espera es cancelable y el gate se
libera incluso al fallar. Commands del usuario no quedan bloqueados por este
gate. No es un scheduler multiagente ni un lease entre servidores independientes:
se mantiene el requisito de un escritor autoritativo de la sesión.

La transición adaptativa al terminar usa `TurnResult.TurnId` exacto y los eventos
causados por el command que ejecutó ese Turn, incluidos descendientes causales.
No toma el último Turn terminado cronológicamente ni una propuesta ajena.
PLAN→ACT sigue requiriendo cobertura explícita del plan vigente. Otros
predicados no implementados informan Deferred, sin inferir permiso de reasoning.

MaxAgents/MaxDepth se conservan en el contrato y fingerprint. Su enforcement
operativo sobre hijos, joins y coordinación pertenece a M6: este bloque no
crea agentes. Tampoco sustituye la política de rutas ni los permisos de tools.

```powershell
dotnet build tests/OmniCore.Tests --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor -class '*UltraCodeExecutionCeilingTests' -class '*ModeAuthority*' -class '*ModeProposal*' -class '*UltraCodePolicy*'
```

Fixtures SQLite/CAS y HTTP loopback: cotas, unknowns, quote insuficiente, recibos,
dos instancias concurrentes, interrupción por plazo, suspensión/reopen, revocación
y command ajeno. Los costes cero se declaran exclusivamente en fixtures que
prueban autoridad; no se atribuyen a las cuentas reales Local/IncludedQuota.
