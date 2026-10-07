# Objetivo activo: cerrar M5 y M5.5

## Registros pre-M6 implementados y probados — 2026-10-07 13:45 UTC

Root implementó los tipos/23 eventos schema1, codecs, extracción ArtifactRefs y
proyección factual con guard single/batch; Luna HIGH entregó cobertura SQLite/CAS
y auditó el conjunto. [Contrato efectivo y reproducción](../architecture/pre-m6-record-contracts.md).
No se implementó scheduler, ejecución de joins/wake/mailbox ni aceptación automática.
Los receipts respaldan procedencia, no demuestran por sí mismos checks/wiring.

Se reprodujeron fallos sin debilitar assertions: idempotencia por EventType y,
tras auditoría Luna, rebinding ExecutionId más cuatro hashes inválidos (5 FAIL).
Build final 0 errores/advertencias; focal 16 PASS. FULL final terminal exit0:
2887 casos, 2883 PASS/0 FAIL/4 SKIP por permisos symlink, 126.976 s.
Arquitectura tras build actualizado: 56 PASS. Conteos solapados. Fixtures no
acreditan workers reales, consultas autenticadas ni consumo real.

Esto no cierra íntegramente M5/M5.5: cualificación Quick real y cableado de
política UltraCode siguen pendientes; la auditoría final debe cubrir todos los
criterios, no sólo estos registros. Se retiró el gate artificial de aprobación
por campo técnico; las políticas nuevas no definidas siguen fuera de alcance.
La segunda revisión de Luna señaló enlaces de AgentExecutionStarted aún sin
validar (padre, envelope) y la necesidad de validar el journal externo de un
receipt cross-session. Son pendientes comprobables, no cubiertos por la FULL
actual; no se declara completo el freeze hasta reproducirlos y resolverlos.

## Cuota de suscripción en status line — 2026-10-07 13:07 UTC

Root conectó UsageSnapshot.AccountQuota, caché por sesión/proveedor y
formatter/CLI; Luna HIGH aportó integración normal loopback/SQLite.
Cuota de cuenta y límites de respuesta permanecen separados; fuente/AsOf,
reset, stale y scopes saldo/clave conservados, desconocidos no son cero.
RED retirando sólo cableado CLI: 13 casos, 11 PASS/2 FAIL. Restaurado,
build 0 errores/advertencias y focal 71 PASS. FULL terminal exit0:
2874 casos, 2870 PASS/0 FAIL/4 SKIP symlink, 123.724 s.
[Contrato, reproducción y límites](m5-subscription-statusline-20261007.md).
Fixtures sintéticos no acreditan consulta autenticada ni consumo real.

Auditoría de facturación: ADR0011/0037 describen Codex como suscripción,
pero ADR0046 §3 exige Unknown sin declaración de BillingMode. Root descartó
la propuesta de inferir IncludedQuota del perfil; ConfigLoader y su test
se conservan. La aceptación Quick real sigue pendiente de declaración
correcta y consentimiento, sin inventar precio cero ni alterar credenciales.
M5/M5.5 siguen abiertos; no se implementa scheduler/joins M6.

## Namespace User: aceptación ausente — 2026-10-07 12:48 UTC

Lectura SQLite real, `mode=ro`/`query_only`, sin migraciones ni claves:
`C:/Users/juanc/AppData/Local/OmniCore/user.db` contiene **0 model_profiles,
0 model_traits** y no tiene tablas adicionales de evidencia/recibos de
cualificación. No había overrides de datos/config en el proceso auditor.
Esto acredita ausencia de aceptación persistida en el namespace activo,
no ausencia de login ni ausencia histórica de inferencia en otros namespaces.
[Inventario reproducible y límites](m5-user-acceptance-inventory-20261007.md).

El recorrido de Claude se contrastó otra vez con
`OmniCoder.Core/Providers/SubscriptionUsage.cs`: ambos usan `--ax-screen-reader`
con `/usage` por stdin. El Unknown observado no se sustituye por una ventana
inventada ni acredita que la cuenta esté desconectada. No se cambió su CLI,
cuenta o configuración para obtener un dato artificialmente.

## Checkpoint de recuperación y cuota real — 2026-10-07 12:44 UTC

Luna completó controles de route drift con listener separado y cero dispatch,
cancelación predispatch y error temprano, seguidos de retry válido en la misma
instancia. Root auditó fixture y compiló: exit0, cero errores/advertencias,
23.83 s; focal de suspensión/reasoning/boost/CLI **36 PASS**, 9.514 s.
La FULL `resume-final-controls-full-1245.log` terminó exit0: **2861 casos,
2857 PASS, 0 FAIL, 4 SKIP** por permisos de symlink, 121.779 s.
Conteos solapados; los providers de estos controles son fixtures, no consumo real.

Root recompiló el grafo real del probe OmniCoder en outputs privados (exit0,
cero errores/advertencias, 23.78 s) y consultó cuentas sin inferencia ni renovación
explícita. Codex reportó semanal 20% usado/80% restante, reset 2026-10-14T03:29:28Z;
Claude permaneció Unknown por falta de ventanas utilizables en este recorrido.
Los resultados llegan al ViewModel real, pero esto no acredita consumo de modelo
ni un éxito autenticado de Claude. [Evidencia y reproducción](m5-live-quota-20261007.md).

Los contratos pre-M6 pendientes tienen ahora una [propuesta conjunta de shapes,
eventos, compatibilidad y pruebas](../architecture/pre-m6-contract-schema-proposal.md),
contrastada con el C# real de OmniCoder. Es propuesta para revisión, no schema
aceptado ni implementado; no inicia scheduler/joins M6. La decisión del conjunto
sigue pendiente y no se marca el criterio de contratos congelados como cumplido.

## Alcance M5 y recuperación legacy — 2026-10-07 12:37 UTC

ADR0007 §Clasificación y spec §90/95 asignan a M5 el runner `quick`;
la suite `full` de código en sandbox y la calibración corresponden a M10+.
No se añaden como condiciones artificiales de cierre de M5 ni se atribuyen a
Quick capacidades que sus diez probes no miden. La aceptación autenticada
de Quick y la evidencia de uso/cuota reales siguen pendientes.
La configuración User inspeccionada contiene ChatGPT `OpenAIResponses` con
perfil `codex` y local Qwen Chat-compatible; no se alteraron YAML ni credenciales.
Se solicitó consentimiento específico para diez probes por Sol/Luna con cuota
incluida, sin API de pago ni fallback. No se ejecutó generación autenticada.
El YAML User de ChatGPT omite `billingMode`: `ConfigLoader` conserva por contrato
`Unknown`, por lo que el preflight rechaza la cualificación sin estimación monetaria.
Un login existente no convierte ese dato en `IncludedQuota`. Para la aceptación
habrá que declarar el modo real con autorización, sin cambiar credenciales ni
suprimir el gate de coste. Esta observación es lectura de configuración y código,
no un intento autenticado fallido ni evidencia de credenciales ausentes.

Luna implementó recuperación usando Lane/Turn del payload bajo Run atribuido,
incluidos envelopes legacy nulos y exclusión de eventos de otros Runs/Lanes.
Root reprodujo una regresión con el control de drift del límite del modelo:
focal inicial **34 casos, 33 PASS, 1 FAIL**, 8.814 s, porque Ask devolvía
exit0 aunque Explorer rechazaba el fingerprint. Root corrigió los resultados
Error/Cancelled de Ask para devolver exit1, conservando la aserción original.
Build final exit0, cero errores/advertencias, 15.78 s; focal **34 PASS**, 9.138 s.
Los logs `resume-matrix-*` están en el directorio de evidencia existente.
Son pruebas offline con HTTP loopback y SQLite/CAS; no consumo autenticado.
La FULL `resume-matrix-full-1238.log` terminó exit0: **2859 casos,
2855 PASS, 0 FAIL, 4 SKIP** por permisos de symlink, 121.520 s.
Los conteos de focal y FULL se solapan y no se suman.
Route drift separado y liberación tras cancelación/error siguen pendientes;
la propuesta externa de Luna no se cuenta como pruebas ejecutadas.

## Redacción de fragmentos y reanudación concurrente — 2026-10-07 12:22 UTC

Root añadió un sexto control de respuesta completa: un secreto ficticio dividido
entre dos `TextBlock` adyacentes. El resumen era seguro, pero `visibleContent`
persistía fragmentos que, unidos, reconstruían el secreto. RED reproducido con
build correcto de 18.44 s: **8 casos, 7 PASS, 1 FAIL**, 1.751 s, en
`visible-redaction-red-tests-1221.log`. Las assertions anteriores se conservan.

`EncodeVisibleContent` reúne sólo fragmentos de texto adyacentes antes de
redactarlos. No inventa separadores; herramientas y razonamiento conservan su
orden y sus fronteras. No transforma `ProviderState` ni concede autorización.
La proyección visible no garantiza un bloque por fragmento físico del proveedor.
El test exige igualdad entre texto visible reconstruido, resumen seguro y
respuesta conservada en artifacts y contexto del Turn posterior.

Luna amplió el flujo normal de suspensión: tras SQLite reopen, el endpoint retiene
la respuesta de resume; un segundo resume debe terminar rechazado mientras la
primera petición sigue retenida. Sólo llegan dos peticiones totales (pregunta y
resume), con el mismo Turn y pasos `[0, 1]`, sin reactivar UltraCode.

Build final `visible-redaction-fixed-build-1221.log`: exit0, cero errores y
advertencias, 15.40 s. Focal `visible-redaction-fixed-focal-1222.log`: **46 PASS,
0 FAIL, 0 SKIP**, 9.207 s, incluyendo replay/opaque, marcadores de tool y CLI.
FULL `visible-redaction-full-1222.log` terminó con exit0: **2857 casos,
2853 PASS, 0 FAIL, 4 SKIP** por permisos de symlink, 119.455 s. Los conteos
se solapan; no se suman. Son fixtures scripted y SQLite/CAS reales, no consultas autenticadas,
facturación, cualificación real ni cierre íntegro de M5/M5.5.

## Aceptación en curso: respuesta completa y continuación normal — 2026-10-07 12:03 UTC

El chat no debe perder bloques de texto de una misma respuesta: `ExplorerTurn`
conservaba sólo el último en `FinalText` y añadía separadores al resumen por paso.
El cambio concatena el texto superior en orden, sin añadir separadores ni incluir
razonamiento. Cinco controles verifican respuesta, Markdown de tabla/código,
redacción, artifacts terminales, reapertura SQLite y contexto del Turn siguiente.
Verifican conservación del contenido; **no son pruebas del renderer visual**.

La integración normal de `TuiTurnHost` ahora prueba HTTP loopback → pregunta →
revocación → reapertura SQLite → respuesta al cuestionario → mismo Turn → nuevo
Turn. El suspendido conserva el reasoning original y el nuevo no revive UltraCode.
La comparación de ruta usa igualdad de identidad, no referencia de objeto; el
rechazo de una ruta distinta permanece obligatorio.

Build `turn-continuation-route-build-1202.log`: exit0, cero errores/advertencias,
15.57 s. Focal `turn-continuation-route-focal-1203.log`: **37 casos, 36 PASS,
1 FAIL, 0 SKIP**, 8.996 s. El fallo de concurrencia revela que un envío durante
generación viva puede confundirse con continuación del Turn y reutilizar su boost.
Se mantiene abierto hasta corregir admisión y verificar el conjunto; la FULL
anterior no acredita este WIP. Los logs están en el directorio de evidencia de abajo.
Todo provider es scripted; SQLite/CAS y el camino Host son reales, pero no hay
consulta autenticada, gasto real ni cualificación real acreditados por estos fixtures.

Actualización 12:10 UTC: los cinco controles de texto fallan al restaurar sólo el
comportamiento anterior (`visible-response-red-tests-1208.log`, 0.697 s). El
control normal de continuación falla al desconectar sólo la selección del Turn
abierto (`normal-continuation-red-tests-1209.log`, 2 casos, 1 PASS/1 FAIL,
1.183 s). No se modificaron las assertions. Tras restaurar ambos arreglos y
añadir admisión exclusiva no bloqueante por instancia de runtime, build completo
exit0, cero errores/advertencias, 15.39 s. Focal ampliada: **135 PASS, 0 FAIL,
0 SKIP**, 11.469 s; arquitectura **56 PASS**, 0.492 s. La escalación interna
autorizada conserva su reentrada bajo esa admisión; los entrypoints externos no
pueden duplicarla. FULL `turn-continuation-full-1210.log` terminó con exit0:
**2856 casos, 2852 PASS, 0 FAIL, 4 SKIP** por permisos de symlink, 121.660 s.
Los conteos se solapan con la focal y no se suman. El guard es local a una
instancia de runtime, no un scheduler ni un lease entre procesos. Continúan
controles adicionales de resumes concurrentes, liberación por error/cancelación
y compatibilidad legacy; el objetivo íntegro y las demás filas siguen abiertos.

Integración OmniCoder repetida con todo el grafo recompilado en outputs privados:
build `turn-continuation-omnicoder-build-1212.log`, exit0, cero errores/advertencias,
16.78 s. Probe `turn-continuation-omnicoder-probe-1213.log`, exit0: Host/SQLite,
Explorer, Protocol JSON, ViewModel net8 y binding WPF reales; razonamiento separado
del primer texto, terminales limpian indicadores, contexto/consumo separados,
polling idempotente y cambio de sesión aislado, sin modificar Git. Evidencia en
`C:/Users/juanc/AppData/Local/Temp/omni-observability-probe-cd29012f66334c76b90ffabc7d0e9ecd`.
Provider scripted; no autenticación ni consumo real. No acredita el cableado de
toda la aplicación OmniCoder ni una revisión visual interactiva de la TUI.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor -class '*ExplorerVisibleResponseIntegrationTests' -class '*ModeAuthoritySuspensionIntegrationTests' -class '*TurnBoostTuiAdmissionTests' -class '*ReasoningReplayResumeTests' -class '*CliEndToEndTests'
```

## Aceptación de cualificación y suspensión — 2026-10-07 11:44 UTC

Root corrigió el oracle de M5: `ProbeScorer.ExtractText` concatena todo el texto
de respuesta, sin separadores inventados; no puntúa reasoning ni tools anidadas.
Quick `1.1.1` identifica la corrección sin cambiar prompts, umbrales ni TaskSetHash.
Siete RED reproducidos incluían falsos `Qualified` persistidos por Host/SQLite/CAS.
Luna construyó `ModeAuthoritySuspensionIntegrationTests`; root auditó y reprodujo
el flujo de cuestionario, revocación, cierre/reapertura y mismo Turn en Explorer.
La resolución y límites originales se conservan y la autoridad revocada no revive.

Build final 0 errores/advertencias, 14.96 s. Barrido `*Qualification*` más suspensión:
**378 PASS, 0 FAIL, 0 SKIP**, 8.096 s. FULL `qualification-blocks-full-1144.log`
terminó con exit0: **2850 casos, 2846 PASS, 0 FAIL, 4 SKIP** por permisos de
symlink, 120.258 s. Conteos solapados no se suman a ejecuciones anteriores. Logs
`qualification-blocks-*` en el directorio de evidencia de la matriz de abajo.

Brecha real encontrada durante esta aceptación: CLI recalcula la resolución del
Run tras revocar UltraCode, mientras el Turn suspendido exige su resolución durable
original. El fixture directo conserva la selección y no acredita ese camino normal.
Luna tiene ownership de `OmniCliRuntime.cs` y su integración para corregirlo;
permanecen abiertos el criterio 7 integral y los demás pendientes de la matriz.
Toda esta ejecución usa providers scripted; no acredita consultas autenticadas,
consumo real, nuevos workers de runtime ni aceptación real de Quick en Sol/Luna.

## Matriz de aceptación íntegra — 2026-10-07 11:28 UTC

Checkpoint conjunto: build final 0 errores/advertencias, 14.60 s; focal de CLI,
authority y policy 58 PASS, 0 FAIL, 8.845 s; arquitectura 56 PASS, 0 FAIL,
0.632 s. FULL `mode-acceptance-full-1127.log`: **2841 casos, 2837 PASS,
0 FAIL, 4 SKIP**, 121.096 s. Conteos solapados, no sumables. Logs en
`C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/`.

Luna implementó cierre exacto de `ModelStepNotDispatched` en el gate de autoridad
y la matriz SQLite de rechazo/reopen. Root auditó y reprodujo RED retirando sólo
las tres líneas del fix: 24 casos, 23 PASS, 1 FAIL (`Accepted` esperado,
`Deferred` real), 2.413 s; restauró el fix sin modificar assertions. Root añadió
los tres controles de explicación directa en los modos y validó el árbol conjunto.
Las propuestas/fixtures no acreditan activación automática ni cualificación real.

```powershell
dotnet build OmniCore.slnx --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor -class '*CliEndToEndTests' -class '*UltraCodePolicyTransitionIntegrationTests' -class '*ModeAuthorityContractTests'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor
```

Esta matriz prevalece como índice operativo sobre las colas históricas de abajo.
No elimina requisitos ni convierte cobertura offline en aceptación autenticada.
Una fila con evidencia parcial permanece abierta; los siete criterios de ADR0046
y los once de ADR0047 se conservan expresamente.

| Requisito | Evidencia inspeccionada / pendiente que impide afirmar cierre |
|---|---|
| ADR0046 salida 1: tres ModelSteps y una suspensión, mismo Turn y gasto completo | `M55ThreeStepSuspensionTests` ejecuta Explorer, SQLite/CAS, cuestionario y reopen; contadores de intentos y readers adicionales. Es proveedor scripted, no consumo autenticado. |
| ADR0046 salida 2: ProviderState exacto, misma ruta/modelo | `ReasoningReplayResumeTests` y contrato de binding físico. La evidencia de una ruta compatible no acredita almacenamiento exacto de todo opaque que la redacción pueda alterar; no eludir el redactor. |
| ADR0046 salida 3: Local no amplía gasto sin consentimiento | Routing/consentimiento, suspensión y presupuesto tienen integración offline. Credenciales o el éxito de un worker no son consentimiento ni aceptación real de M5. |
| ADR0046 salida 4: Task/Lane/Turn, UTC y ArtifactRefs en ToolCalls | Preimagen y envelopes con SQLite/reopen; la ronda previa reparó aislamiento de la prueba que mutaba el entorno. No implementa restore M7. |
| ADR0046 salida 5: escritor autoritativo y CommandOutcome | `CanonicalWriterArchitectureTests` verifica call sites y controles positivos; tests de commands verifican ranges/faults. Conservar revisión de todos los caminos excepcionales, no sólo un command exitoso. |
| ADR0046 salida 6: contratos futuros serializan y se reproducen | `AgentExecutionContractTests` prueba identidad, parent y lifecycle independiente. Aún faltan contratos completos Delegation/Wake, JoinPolicy, SupervisionBinding, ResultDisposition y registros de validación; el nombre en el ADR no es implementación. |
| ADR0046 salida 7: deltas fuera del journal | `TelemetryBoundaryTests` ejecuta wrapper y Explorer real con fixture; guard de arquitectura. No confundir telemetría con recibos canónicos. |
| ADR0047 criterio 1: explicación PLAN directa | Nuevo `CliEndToEndTests.Direct_explanation_preserves_selected_mode_without_plan_tools_or_children`: TuiTurnHost→adapter HTTP loopback→SQLite, sin tools, hijos, archivos ni PlanApproval. |
| ADR0047 criterio 2: corrección ACT en Lane raíz | `CliEndToEndTests.Act_executes_read_and_approved_patch_before_completing_gates`; no sustituir esta prueba por la explicación sin tools. |
| ADR0047 criterio 3: consulta ORQ directa / delegación acotada | Nuevo control ORQ verifica consulta directa y modo conservado. Delegación útil no queda acreditada; scheduling/delegación M6 no se implementan aquí. |
| ADR0047 criterio 4: modelo/reasoning no conceden autoridad | `ModeAuthorityContractTests` comprueba origen User confiable, rejects y authority/fingerprint; no atribuir a User un command del modelo. |
| ADR0047 criterio 5: permisos, gasto y preguntas no cambian modo | Tests de PlanApproval distinguen approve_execute/approve_only/reject; permisos y consentimiento siguen separados. Falta aceptación integral de todos los caminos, no extrapolar del gate interno. |
| ADR0047 criterio 6: transición UltraCode autorizada | Matriz SQLite de pinned/revoked/expiry/consentimiento/steps/tools/grants falsificados. Método interno sin caller de producción ni trigger determinista aceptado: activación real abierta. |
| ADR0047 criterio 7: revocación durante espera y reinicio | `ModeAuthoritySuspensionIntegrationTests` incluye flujo normal TuiTurnHost→HTTP scripted→pregunta→revocación→SQLite reopen→respuesta→mismo Turn y nuevo Turn sin reactivar UltraCode; focal 12:10 y FULL2856 verdes. Herencia a hijos no se acredita con un gate de raíz ni se implementa scheduling M6. |
| ADR0047 criterio 8: gates proporcionales / Plan interno | Los tres controles directos verifican un Task/Lane raíz y ausencia de PlanApproval/tools/hijos; conservan el seguimiento técnico. |
| ADR0047 criterio 9: thinking efectivo y boost de un Turn | Resolución durable, precedencia, capacidad, wire y admisión TUI cuentan con fixtures dedicados. No afirmar thinking real de un modelo por estas pruebas ni usar reasoning para activar UltraCode. |
| ADR0047 criterio 10: aislamiento Run/restart/retry | Tests de autoridad multi-Run, razonamiento/replay, snapshot y boosts. NotDispatched debe cerrar exclusivamente su `(TurnId, StepIndex)`, no pasos vecinos. |
| ADR0047 criterio 11: delegación selectiva y aceptación de evidencia | No hay aceptación integral de delegación útil; los contratos congelados pendientes y el scheduler futuro no se sustituyen por Tasks creadas en un fixture. Mantener el criterio explícito y su dependencia de M6. |
| AgentProfile reusable/efectivo | `ReusableAgentProfileTests`, `EffectiveAgentProfileTests`, `DurableAgentProfileBindingTests`, `AgentProfileSuspensionIntegrationTests` y CLI verifican configuración User, ceiling, fingerprint/CAS y drift antes de dispatch. |
| M5 / ADR0007: Quick, perfiles, receipts y aceptación real | Runner/store/evidencia/coste y cuotas tienen controles offline; la lectura autenticada de cuotas del 06/10 es histórica y no cualifica Sol/Luna. La ejecución reproducible de Quick sobre proveedores reales y su acta siguen pendientes. No afirmar credencial ausente sin comprobarla ni ejecutar gasto sin presupuesto autorizado. |

Prioridad siguiente: terminar el bloque de autoridad/espera/replay y su integración;
cerrar los esquemas aceptados faltantes sin lógica M6; verificar la aceptación real
de M5 con límites explícitos. La distribución de archivos entre root y Luna es
ownership, no una división del objetivo ni un cambio de sus criterios de salida.

## Checkpoint conjunto — 2026-10-07 11:11 UTC

FULL `authority-report-full-1110.log`: **2819 casos, 2815 PASS, 0 FAIL,
4 SKIP**, 120.235 s. Arquitectura 56 PASS, 0 FAIL. Integración con componentes
reales WPF de OmniCoder verificada con proveedor scripted y outputs aislados;
no equivale a consulta autenticada ni consumo real. Evidencia reproducible en
`m55-generation-attempt-evidence.md`.

Se mantienen completos los criterios de ADR0007/0046/0047. El reparto de
archivos no constituye objetivos parciales ni permite cerrar un hito por una
suite focal. Continúan pendientes la aceptación integral, la activación real
de la política determinista UltraCode, los contratos pre-M6 aceptados restantes
y la cualificación real de M5; no se inventan reglas ni se implementa M6.

## Checkpoint de evidencia de intentos — 2026-10-07 10:09 UTC

Build 0 errores/advertencias. Suite completa `retry-evidence-full-1007.log`:
**2780 casos, 2776 PASS, 0 FAIL, 4 SKIP**, 118.617 s. Focal relacionada 114 PASS,
1.472 s; conteos solapados no sumables. La ejecución anterior encontró siete
fixtures single-call sin cota declarada; se corrigió su configuración sin
debilitar assertions. Los terminales primary/meta v3 ahora conservan contador
y cota de intentos, y los lectores mantienen incertidumbre tras retries/reopen.
Contrato y reproducción en `m55-generation-attempt-evidence.md`.

Este checkpoint resuelve la brecha de persistencia descrita abajo, no el cierre
íntegro. No demuestra consumo autenticado ni integración real OmniCoder.
Continúan los demás criterios completos de ADR0007/0046/0047; la distribución
de ownership entre agentes no divide ni reduce los objetivos M5 y M5.5.

## Checkpoint de inicio durable y presupuesto — 2026-10-07 09:48 UTC

Build sin errores ni advertencias. Suite completa `boost-budget-full-0943.log`:
**2743 casos, 2739 PASS, 0 FAIL, 4 SKIP** por permisos de symlink, 238.950 s.
La focal de lifecycle/presupuestos pasó 97/97; otra de regresión M2 pasó 56/56.
Los conteos se solapan y no se suman. Logs en el mismo directorio indicado abajo.

El refuerzo TUI se reserva de forma exclusiva y se consume al publicar un nuevo
`TurnStarted` durable, antes del proveedor; un rechazo anterior libera la reserva,
resume no consume otro refuerzo y un turno posterior vuelve a su configuración.
Los controles verifican concurrencia con HTTP privado y ambos journals de sesión,
duración a través del loop y conservación tras reapertura SQLite.

La admisión de tokens reconstruye input+output del Run, incluyendo llamadas de
compactación, sin sumar otra vez cache/razonamiento ni contar duplicados EventId.
Uso ausente, inválido, overflow o invocación sin resolver no se vuelve cero.
La cota de la siguiente invocación usa capacidad declarada, salida e intentos
físicos; `ContextBudget` estimado no demuestra una capacidad nativa.

**Limitación abierta detectada en esta auditoría:** los terminales actuales no
persisten el número de envíos HTTP. El uso de una respuesta final tras retries no
demuestra consumo completo de la invocación. Falta evidencia durable aditiva de
intentos en primary/meta y lectura incierta tras replay; el ledger monetario ya
mantiene su reserva incierta. Este checkpoint no acredita todavía ese criterio.

Todo lo anterior usa fixtures offline, SQLite/CAS y HTTP controlado; no demuestra
consumo autenticado, cualificación real ni integración real con OmniCoder. Los
objetivos completos ADR0007/0046/0047 siguen abiertos: retries, resolución de
límites, transición UltraCodePolicy autorizada, contratos pre-M6 aceptados y
cualificación real. Un checkpoint verde no sustituye el cierre íntegro.

## Checkpoint verificado — 2026-10-07 09:04 UTC

La suite completa `snapshot-full-0859.log` terminó con exit 0: **2704 casos,
2700 PASS, 0 FAIL, 4 SKIP** por permisos de symlink, 239.129 s. La focal previa
pasó 235/235, 18.202 s; no se suman sus conteos a la suite completa. Build final:
0 errores y 0 advertencias, 15.67 s. Logs en
`C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/`.

Esto verifica el checkpoint de perfiles, autoridad, consentimiento y snapshots;
NO cierra M5/M5.5 ni acredita consultas autenticadas, consumo real o integración
real con OmniCoder. Las pruebas emplean proveedores fixture, HTTP loopback,
SQLite y CAS. Siguen pendientes los criterios completos de duración/consumo
single-use de boost, límites de contexto/TaskBudget, transición UltraCode
autorizada, contratos pre-M6 sin payloads aceptados completos y cualificación
real de M5. El anexo ausente se solicitó al usuario sin detener los otros frentes.

### Contratos incluidos en este checkpoint

- `lane.created` v2 añade `AgentProfileRevision` y `AgentProfileHash` opcionales.
  La configuración reusable sólo se carga de `agent-profiles.yaml` User; una
  Lane vinculada no puede reanudarse con revisión/contenido diferentes. v1
  conserva marcadores ausentes y no adquiere el default actual.
- `turn.started` v3 añade `InstructionSnapshot` (`ConversationOnly`,
  `ResolvedInstruction`) y `ReasoningResolution` opcionales. La instrucción
  publicada está redactada; resume conserva la instrucción original. v1/v2
  mantienen nulls, sin reconstruir intención a partir del modo actual.
- `model_step.started` v4 añade la misma `ReasoningResolution`. El tracker
  exige equivalencia completa con el Turn y coincidencia de `AppliedRequest`
  con los campos existentes `ReasoningKind`/`ReasoningBudgetTokens`; no admite
  añadir, omitir o alterar procedencia entre pasos de un Turn con snapshot.
- `ReasoningResolution` es inmutable: `RequestedRequest`, `AppliedRequest`,
  `Source` (`None`, `UserDefault`, `RunOverride`, `TurnBoost`, `UltraCode`),
  revisiones User/Run/ModeAuthority, `TurnBoostId`, `OutputReserveTokens` y
  `Reductions` canónicas. `None` no equivale a un off explícito. Registrar
  reducciones no demuestra todavía que todos los límites estén conectados.
- `model.reasoning.resolved` y `turn.instruction` entran en el fingerprint
  efectivo del Turn con contenido canonical referenciado por CAS. Ausencia
  significa ausencia: no heredan componentes de otro baseline ni actualizan
  retroactivamente fingerprints legacy. Se rechaza drift antes de append o
  dispatch, conservando los receipts originales en resume.
- `run.interaction_resumed` conserva identidad de interacción y causalidad del
  command al salir de una espera resuelta. El consentimiento de ruta publica
  petición y espera correlacionadas, sin fingir un nuevo `UserInputReceived`.
- La consulta de autoridad separa `autoModeSwitch` almacenado de
  `effectiveAutoModeSwitch` calculado con el reloj UTC de consulta. Un control
  exige una sola clave JSON; el reloj no modifica el fingerprint durable.

Reproducción:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none
```

## Configuración original del Turn — 2026-10-07 08:38 UTC

WIP integrado, aún sin compilar/verificar: `TurnStarted` v3 conserva una
`TurnInstructionSnapshot` opcional y una `ReasoningResolution` opcional;
`ModelStepStarted` v4 conserva la misma resolución. Las versiones anteriores
se leen con snapshots ausentes, sin atribuirles preferencias actuales.

Explorer conserva la instrucción durable en resume y rechaza una resolución
de razonamiento diferente antes de publicar eventos o invocar al proveedor.
El snapshot de instrucciones pasa por la redacción existente; no se guarda
una copia sin redactar. El fingerprint añade `model.reasoning.resolved` sólo
si la selección contiene la resolución, incluyendo solicitud, aplicación,
origen, revisiones, reserva y reducciones. No confunde solicitud con soporte
verificado ni afirma que el proveedor haya honrado la configuración.
También incorpora `turn.instruction` cuando existe el snapshot (flag e
instrucción). Su ausencia elimina cualquier componente heredado, y resume de
un Turn legacy no incorpora el snapshot actual. El control de CAS/hash y
no-herencia está añadido; `git diff --check` pasó, pero no sustituye al build
ni a la ejecución de las pruebas.

Se amplió la prueba SQLite/CAS de suspensión/reapertura a siete variantes
(perfil y drift de razonamiento) y se añadió control determinista del
fingerprint de procedencia. Son fixtures offline, pendientes de ejecución.
El wiring CLI, validadores y proyecciones permanece bajo ownership Luna.
La última suite completa sigue siendo la roja de 08:28; este avance no cambia
el estado de cierre ni sustituye los criterios íntegros ADR0007/0046/0047.

## Evidencia integral actual — 2026-10-07 08:28 UTC

La focal de integración de perfiles, autoridad y lifecycle pasó 120/120;
la verificación posterior del almacén de preferencias pasó 26/26 (conteos
solapados). La suite **completa** posterior terminó con exit 1: **2692 casos,
2665 PASS, 23 FAIL, 4 SKIP**, 235.954 s. Es evidencia roja, no cierre.
Log reproducible: `C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/integrated-full-0822.log`.

Se localizaron las causas a corregir antes de aceptar el checkpoint: el comando
explícito `explore.start` dejó de conservar PLAN al adoptar el default ACT;
cuatro controles de cuota rechazan resume porque el runtime cambia el flag
conversacional y con ello la plantilla/instrucción del fingerprint original;
las expectativas de tres controles de routing requieren el nuevo batch exacto
de petición y espera. No se elimina el guard de fingerprint ni se simula
respuesta humana. Los tres controles de perfil suspendido/reabierto sí pasan.

El objetivo sigue íntegro. También falta completar el snapshot durable de
razonamiento solicitado/aplicado, origen y reducciones, boost por Turn y retry;
el camino autorizado UltraCode del criterio 6 no se sustituye por rechazo
genérico; continúan pendientes contratos pre-M6 y evidencia real de M5.

## Integración y regresiones reales — 2026-10-07 07:28 UTC

El objetivo no se fracciona ni se considera cerrado por pruebas focales.
Root guardó el techo AgentProfile efectivo y orden de preferencias en `58acacb`.
El wiring User/Lane/AgentExecution normal sigue pendiente; no basta la selección
manual de perfiles en un fixture.

Root conectó (todavía WIP) `run.mode_authority` al fingerprint y al Turn. Se
prepara/publica por el CAS existente; reemplaza, no acumula ni hereda autoridad
de otro baseline. Un Turn reanudado conserva selección inicial y sus refs;
revocación posterior permanece vigente en la proyección actual. Journals legacy
no adquieren retroactivamente el componente. Pruebas SQLite close/reopen y
provider fixture verifican estos casos. No hay llamadas autenticadas en ellas.

- Build checkpoint: 0 errores/0 advertencias, 16.69 s.
- Focal autoridad/fingerprint/perfil: 35 PASS/0 FAIL/0 SKIP, 1.297 s.
- FULL sesión50312, terminal exit1: **2662 casos = 2650 PASS / 8 FAIL /
  4 SKIP**, 249.340 s. No sustituye la última full verde ni acredita cierre.
- Fallos: default ACT cambiado accidentalmente a PLAN (dos tests); binding del
  digest de objetivo crudo frente al journal redactado (un test); prefijo `/mod`
  ahora ambiguo con `/mode` (un test); metadata exacta requiere nuevo componente
  y evento de autoridad (cuatro tests). Clasificación contrastada con código;
  todavía exige rerun integral posterior a los fixes.
- Root actualizó las assertions exactas de metadata (incluyendo unicidad del
  evento añadido), no quitó verificaciones: focal CLI/input/rollback, 26 casos =
  25 PASS/1 FAIL, 5.191 s. Input aislado reproduce error de revision/objective
  mismatch: 6 casos = 5 PASS/1 FAIL, 0.521 s.

También se detectó que el guard de selección de modo observa ModelStep, pero
puede permitir downgrade PLAN mientras una ToolCall con efectos sigue en vuelo:
ModelStepCompleted se emite antes de ejecutar tools. Luna corrige este guard y
sus regresiones, preserva el default existente y vincula el digest a la misma
representación redactada durable, sin desactivar redacción ni comparación.
Después continúa ADR0047 completo: reasoning por capacidades/budget declarado,
precedencia User/Run/Turn, boost y retry, con wire/UI efectivos. Root mantiene
ownership factory/Explorer/perfiles/aceptación. No scheduler/joins M6.

Reproducción del checkpoint rojo:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none
```

La suite posterior puede cambiar al aplicar fixes: estas cifras corresponden al
checkpoint congelado anterior, no a todas las fuentes WIP posteriores. M5 real
qualification, perfiles efectivos normales, contratos pre-M6 y los siete/once
criterios de ADR0046/0047 continúan siendo requisitos íntegros de salida.

## Alcance íntegro y equipo — 2026-10-07 06:38 UTC

El propietario reanudó exclusivamente M5/M5.5, con bloques agrupados y monitor
cada 30 minutos. No se redefine el objetivo alrededor del último fix. Contratos
de etapas posteriores necesarios para M5.5 se congelan sin lógica de scheduling,
joins o delegación M6. Main y cambios ajenos quedan intactos; no push/merge.

- P0: comandos/admisión/checkpoint/followup/escalación integrados en b2ac3fe.
  Full terminal exit0: 2637 casos = 2633 PASS/0 FAIL/4 SKIP symlink, 239.434s.
  Focal 57 PASS y arquitectura 56 PASS; cifras solapadas, no se suman.
- P0 restante: autoridad explícita de modos y UltraCode, selección/revocación
  durable, consultas directas y gates proporcionales de ADR0047. El documento
  aceptado estaba en el checkout principal y faltaba en este worktree; ahora se
  conserva fielmente aquí. Reasoning high/max no prueba esa autoridad. Sus once
  criterios deben cubrirse, además de los siete de ADR0046; no se omiten.
- P1: AgentProfile reutilizable/efectivo y fingerprint de configuración, no sólo
  UUID de Lane. GLM propone paquete independiente; root integra y audita.
- P1: conjunto completo de contratos congelados pre-M6. Propuestas con métodos
  vacíos o semánticas inventadas no son una implementación aceptable.
- P2: aceptación real M5 y acta de cierre coherente con el roadmap. Fixtures,
  llamadas a workers y tokenización local no acreditan cualificación autenticada
  del circuito Quick. No reabrir defectos históricos ya corregidos.

Luna HIGH implementa el bloque crítico completo; root conserva auditoría,
integración y commits. Nemotron gratuito/GLM/DeepSeek usan paquetes disjuntos
sin historial ni secretos, timeout de generación 20 minutos y sin fallback
pagado. Las entregas parciales o finish_reason error/length se rechazan. Qwen
local queda para pruebas cerradas; dos encargos recientes no solicitaron una
tool válida y NO ejecutaron tests. Root verificó comandos para no bloquearlo.
NVIDIA devolvió HTTP504 tras unos cinco minutos en requests no streaming; un
paquete DeepSeek streaming también devolvió504. Esos fallos se preservan, sin
reconfigurar servidores/TLS ni presentar workers vivos como progreso.

Logs y ownership vigentes: C:/Users/juanc/.codex/omni-overnight-20261005.md y
C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0623/, 0626 y 0631.
Los checkpoints históricos siguientes mantienen su fecha; no son la cola actual.

2026-10-07 05:37 UTC: provider.adapter contiene JSON v2 exacto/redactado sólo
cuando el store lo permite; Prepare no publica temprano, misma versión/hash,
resume conserva null histórico y rechaza endpoint distinto antes del proveedor.
RED fortalecido3/3; final94PASS/0FAIL7.551s/arch56PASS .545s, fixtures offline con
SQLite/CAS/CLI loopback reales. Full nueva pendiente; anterior af1918a terminó
2620=2616PASS/0FAIL/4SKIP231.835s. Pendientes AgentProfile reusable/efectivo,
contratos M6 congelados aceptados y commands Ensurepolicy/followup/escalación
(incluida admisión excepcional Explorer). No scheduler/joins ni cierre M5.5.

2026-10-07 05:29 UTC: routing y denegaciones conservan Ack/Failure; autorización
no confirmada bloquea el gate CLI. RED7/7; final52PASS/0FAIL5.588s y arquitectura
56PASS .599s. SQLite privado real; fallos/proveedores fixtures, sin consumo real.
Oracle de presupuesto corregido a cinco eventos exactos del lifecycle existente.
Full nueva pendiente; anterior sobre98224ea terminal2607=2603PASS/0FAIL/4SKIP
250.515s. Pendientes: EnsureSessionRoutingPolicy/follow-up/escalación excepcionales,
AgentProfile reusable/efectivo, contenido provider.adapter y tipos/eventos M6
congelados aceptados. No cierre M5.5 ni implementación scheduler/joins.

2026-10-07 05:15 UTC: PlanApproval errores/persistencia/confirmación conservan
Ack/Failure y sólo ID confirmada; AwaitingInput recupera pendienteNoOp sin duplicar.
CLI validaAck antes esperar respuesta; wrapper conservaexception/OCE. RED6/8;
final81PASS/0FAIL12.268s + arquitectura56PASS .776s, SQLite/stateFile reales y
providers/faults fixtures. Full nueva pendiente. FULL77248 anterior sobre565f747
terminalexit0:2599=2595PASS/0FAIL/4SKIP264.680s. Commandsrouting/followup/escalation,
AgentProfile reusable, contenido provider.adapter y payloadsM6 aceptados pendientes.

2026-10-07 05:05 UTC: completion errores/OCE/prefix/read/checkpoint ahora conserva
Failure original, Completed=null y ACK causal, sin cambiar sesión seleccionada.
RED7/12, final61PASS/0FAIL9.897s, arquitectura56PASS .727s; SQLite real/reopen,
fixtures explícitos. Full nueva pendiente. FULL30365 anterior sobre2b03571
terminalexit0:2589=2585PASS/0FAIL/4SKIP260.875s. PlanApproval, AgentProfile reusable,
contenido provider.adapter y payloadsM6 aceptados siguen pendientes; no cierreM55.

2026-10-07 04:56 UTC: ExecuteExplorerTurn conserva Failure original + ACK
correlacionable ante callback fallido/cancelado; CLI mantiene propagación sin
confundir Accepted parcial con éxito. RED3/7; final59PASS + arquitectura56PASS.
SQLite real/reopen, fallos de lectura fixtures identificados. Full pendiente.
Auditoría Luna leída: completion/gates y plan approval aún pierden outcomes
en errores; este avance no cierra todo §5. AgentProfile/payloadsM6 siguen pendientes.

2026-10-07 04:52 UTC: FULL95618 sobre185228d terminal exit0 verificada;
2584=2580PASS/0FAIL/4SKIP symlink, 263.263s. Freeze levantado tras terminal.
Contador llama.cpp y publicación cubiertos; restantes de M5.5 siguen abiertos.

2026-10-07 04:46 UTC: fingerprint baseline CLI y componentes porTurn preparados
sin CAS temprano; publicación con TurnStarted normal/overflow bajo lease corta.
Resume conserva refs originales, no nuevos receipts hash-equivalentes. RED4/4
válido; final63PASS/0FAIL8.772s + arquitectura56PASS. Full previa ad5d7c6 terminal
2578=2574PASS/0FAIL/4SKIP268.451s. Full del bloque fingerprint pendiente.
AgentProfile efectivo/configuración reutilizable, contenido provider.adapter y
payloadsM6 aceptados aún pendientes: esta protección CAS no acredita cierre §7
ni de los siete criterios. Detalle/capabilities/fixtures en el checkpoint.

2026-10-07 04:34 UTC: nueva frontera de publicación meta input/output protegida
sin lease durante await/provider. RED2=1PASS/1FAIL y final65PASS/0FAIL29.657s;
arquitectura56PASS. Ver contrato y alcance distinto Host/fixture en el checkpoint.
Full nueva pendiente; anterior97bb1972574verde. No cierra schemasM6 ni AgentProfile
efectivo. Luna READONLY siguiente paquete: diseño concreto de publicación de
componentes fingerprint CLI/Turn sin remoto bajo lease ni Verify ficticio.

Actualización 2026-10-07 04:22 UTC / 2026-10-06 22:22 local: contador llama.cpp
fuera de lease remota; preparación exacta de ToolOutput y publicación conjunta
con snapshots/eventos. RED2/2 y final39=38PASS/0FAIL/1SKIP; control adicional
13=12PASS/0FAIL/1SKIP; arquitectura56PASS. POST /tokenize autenticado real
respondió 3 tokens para «Hola OmniCore». Ver detalles y límites de evidencia en
m55-provider-state-checkpoint.md. Full dbc6d2e terminal2569=2565PASS/0FAIL/4SKIP;
full nueva terminal sobre 97bb197: 2574=2570PASS/0FAIL/4SKIP, 267.608s.
Este bloque no cierra AgentProfile efectivo, contratos M6
aceptados ni restantes fronteras CAS del metamodelo/fingerprints. No scheduler/joins.
Qwen sí ejecutó tres pruebas mediante tool cerrado sobre dbc6d2e, no sólo propuestas.
Los checkpoints históricos de abajo conservan su fecha y no son estado actual.

Estado vigente 2026-10-07 04:07 UTC / 2026-10-06 22:07 local:
FULL51789 TERMINALexit0 sobre e467066: 2566 casos, 2562 PASS, 0 FAIL,
4 SKIP symlink, 271.406s. Después: lease corta para checkpoint de contexto,
RED2/2 reproducido, prueba de diagnóstico de uso inválido propuesta por Qwen
e integrada/ampliada por root; final69PASS/0FAIL29.413s, arquitectura56PASS.
Qwen sí produjo respuestas por inferencia local autenticada; no ejecutó tests.
Luna propuso fixtures, root revisó/implementó/reprodujo. Full nueva pendiente.
Persisten snapshots/externalización CAS, AgentProfile efectivo y contratos M6
completos aceptados; M5.5 NO cerrado. No scheduler/joins ni éxito autenticado
de cuenta/cualificación/OmniCoder atribuido a fixtures. Detalles en el runbook
de ProviderState; el objetivo y sus siete criterios siguen íntegros.

Estado vigente 2026-10-07 03:55 UTC / 2026-10-06 21:55 local:
FULL88713 TERMINALexit0 sobre f2ca9cf: 2564 casos, 2560 PASS, 0 FAIL,
4 SKIP symlink, 272.183s. Freeze levantado tras su terminación.
Respuesta final CAS→batch cubierta ahora por lease común: RED2/2 reproducido,
47 PASS focales y 56 PASS arquitectura; nueva full posterior pendiente.
Detalles y reproducción en m55-provider-state-checkpoint.md. Fixtures offline,
no autenticación/consumo real. Luna aporta pruebas y auditoría; root revisa,
integra, reproduce y corrige. Continúan publicación de contexto, AgentProfile
efectivo y contratos M6 completos aceptados; no scheduler/joins ni cierre M5.5.

Estado vigente 2026-10-07 03:44 UTC / 2026-10-06 21:44 local:
FULL1781 TERMINALexit0 sobre6bbbcfa:2561=2557PASS/0FAIL/4SKIPsymlink,
268.114s, other-adapters-reasoning-full.log. Después: lease común CAS/checkpoint/
ModelStepCompleted FULL y cancelación tras respuesta antes tools/aprobación.
REDlease2/2 y REDcancel1/68 reproducidos; final68PASS/0FAIL5.762s/build0/0,
arquitectura56PASS. Detalles/reproducción en m55-provider-state-checkpoint.md.
Full posterior pendiente; fixtures no autenticación ni consumo real.
Auditoría fingerprint encontró ProfileId generado por Lane, no configuración
reutilizable resuelta. No se quita el componente ni se inventa un resolver para
declarar cierre. Los contratos M6 de Delegation/Wake/JoinPolicy/mailbox/binding/
ResultDisposition aún carecen de payloads completos aceptados; AgentExecution
no los sustituye. Continúa auditoría CAS y de siete criterios; no scheduler/joins.

Estado vigente 2026-10-07 03:34 UTC / 2026-10-06 21:34 local:
FULL94484 TERMINALexit0 sobre0e83092:2545=2541PASS/0FAIL/4SKIPsymlink,
268.165s, responses-reasoning-native-full.log. Freeze levantado tras terminal.
Después se corrigió omisión de razonamiento explícito en Anthropic/ChatCompatible:
RED7/4FAIL, final162PASS/0FAIL4.983s y arquitectura56PASS, build0/0.
Detalles/contrato/reproducción en m55-provider-state-checkpoint.md.
Fixtures offline no acreditan autenticación ni gasto. Full posterior pendiente.
M55 sigue activo: auditoría integral de criterios, opaco seguro, contratos M6
congelados y decisiones de replay no especificadas. Luna auditó, root implementó
y reprodujo. No scheduler/joins ni cierre sin evidencia.

Estado vigente 2026-10-07 03:26 UTC / 2026-10-06 21:26 local:
FULL50020 TERMINALexit0 sobre2781b71:2513=2509PASS/0FAIL/4SKIPsymlink,
265.703s, included-quota-receipt-full.log. Uso parcial IncludedQuota y lease GC
incluidos, no consultas autenticadas. Freeze levantado sólo tras terminal.
Después, Responses native effort7labels/budgetfailclosed corregido con RED12/20
y final159PASS/0FAIL10.302s/build0/0, arquitectura56PASS. Contrato y reproducción
en m55-provider-state-checkpoint.md; no amplía permisos ni cambia modelo/policy.
Full posterior pendiente. Luna propuestas/root fuente oficial e implementación.
M55ACTIVE: Anthropic/ChatCompatible wire, storage opaco/replay, frozenM6 y audit7.

Estado vigente 2026-10-07 03:17 UTC / 2026-10-06 21:17 local:
uso parcial IncludedQuota corregido mediante recibos User/SQLite/CAS por invocación,
sin perfil parcial ni reservas/cargos USD ficticios. Publicación y root FULL bajo
la misma lease de GC; REDs reproducidos de recibo ausente y lease prematuramente
libre. Final 369 PASS / 0 FAIL / 0 SKIP, 16.789s, build0/0; arquitectura56PASS.
Detalle/reproducción en m55-included-quota-admission-20261006.md. No autenticación
ni consumo real acreditados. Full nueva pendiente: 2484verde8f837b1 antecede
0d23177 y este bloque. Ownership Luna auditoría/root implementación y verificación.
GoalM55ACTIVE: wire razonamiento, políticas de replay/opaque seguro, contratos
congelados M6 y auditoría integral ADR0046 aún requieren evidencia; no cierre global.

Estado vigente 2026-10-07 03:10 UTC / 2026-10-06 21:10 local:
full terminal sobre 8f837b1: 2484 casos, 2480 PASS / 0 FAIL / 4 SKIP
por symlink, 298.957s (declared-reasoning-and-tui-full.log). No incluye
el bloque posterior de admisión de cuota durante cualificación.
Ese bloque consulta por probe, separa consentimiento de suite y de ventana
baja, conserva origen/fecha/disponibilidad y rechaza identidad de provider
ajena. CLI no interactivo no autoriza con --yes. Cancelación después de
admisión impide entrar al provider conservando la observación de interrupción.
RED auditoría: 25 casos / 3 FAIL; final focal: 45 PASS / 0 FAIL / 0 SKIP,
8.174s, build 0/0 (quota-audit-red.log / quota-audit-final.log).
ArchitectureTests anterior al último ajuste: 56 PASS / 0 FAIL, 0.743s.
Fixtures offline; no autenticación ni consumo acreditados. Falta persistir
el consumo parcial IncludedQuota si un probe posterior se deniega: no se
declara cerrado ese punto ni M5.5. Luna auditoría/propuestas; root integración,
correcciones y ejecución. Qwen no disponible: ambos endpoints rechazaron
conexión en último chequeo 01:44 UTC, sin relanzar ni cambiar configuración.

Estado vigente 2026-10-07 02:53 UTC / 2026-10-06 20:53 local:
validación declarada de razonamiento y forwarding neutral conectados en
ExplorerTurn/MetaModelService/ProbeRunner antes de admisión/reserva/provider.
12 REDs reproducidos y final154 PASS / 0 FAIL / 0 SKIP95.095s/build0/0,
incluida TUI. Full89e990a anterior2469=2464PASS1FAIL4SKIP342.495s:
único fallo era lectura concurrente de controles desde la prueba, corregida
sin suprimir assertions ni reintentar hasta verde. Full posterior pendiente.
Contrato y límites en m55-provider-state-checkpoint.md. Dialecto wire por
adapter sigue pendiente, no atribuir omisión a los adapters que ya recurrían
a selección. GoalM55ACTIVE; storage opaco seguro/otrasReplayPolicies/
IncludedQuotaqualification/frozenM6/auditoría de criterios siguen pendientes.

Actualización vigente 2026-10-07 02:42 UTC / 2026-10-06 20:42 local:
contenido visible del mismo Turn restaurado tras reapertura SQLite/CAS,
incluido razonamiento, texto intermedio y orden con herramientas canónicas.
Scope Run/Lane/Turn/paso y ruta física contrastados; corrupción y marcadores
ajenos rechazan antes del proveedor. Focal 79 PASS / 0 FAIL / 0 SKIP,
51.403s, build 0/0 (`visible-content-contract-final.log`). Fixtures offline,
no consultas autenticadas ni gasto real. Contrato y reproducción en
`m55-provider-state-checkpoint.md`. Full previa 0fbb322 no incluye este bloque.
Luna aportó fixtures y auditoría; root implementó, reprodujo REDs y verificó.
Luna entregó además propuesta externa aún no integrada de validación/forwarding
ReasoningRequest: persiste esfuerzo pero la solicitud actual no lo reenvía.
Siguen pendientes esfuerzo/wire, otras políticas de replay, storage opaco seguro,
IncludedQuota de cualificación, contratos congelados M6 y auditoría de salidas.
M5 cerrado por el usuario; no se declara cerrado M5.5. Qwen último chequeo
01:44 UTC rechazó conexión en ambos endpoints, sin generación ni fallback pagado.

Actualizado 2026-10-07 01:44 UTC / 2026-10-06 19:44 America/Mexico_City.
Objetivo vigente: cerrar M5.5 con implementación y evidencia reproducible,
conforme a ADR-0007/0044/0046/0047, y dejar M6 listo para empezar.
No se implementan scheduler/joins M6 ni restore físico M7 antes de cerrar sus fronteras.

### Estado vigente de reservas (00:34 UTC)

Actualización 02:22 UTC: suite completa sobre0fbb322 TERMINALexit0:
2458 casos = 2454 PASS / 0 FAIL / 4 SKIP symlink, 352.538s,
`reasoning-targetref-full.log`. Incluye causalidad61c46d9, Nonee3ee7ea y
TargetRef0fbb322; no suma los focales anteriores. Freeze20164 levantado al terminal.
No acredita consultas autenticadas ni cierra pendientes de ADR0046.
Auditoría root+Luna: firmas Anthropic/Responses sí restauradas mediante
ProviderState; ReasoningBlock.OpaquePayload no lo usan esos adapters.
La pérdida pendiente es razonamiento visible en ModelRequest.Messages al reabrir
(ADR0005§3). Propuesta anthropic-reopen.proposed.cs leída por root, aún sin
integrar/compilar/ejecutar; separa firma y bloque visible y exige no duplicar tools.

Actualización 02:16 UTC: TargetRef normalizado al workspace de ejecución, raíz
`.`/subdirectorios relativos; no modifica autorización/claims/cwd/reversibility.
RED3=1PASS2FAIL; final93PASS0FAIL0SKIP7.735s/build0/0, targetref-relative-expanded.log.
Luna fixture/root implementación+auditoría; filesystem privado y launcher fake.
TargetRef absoluto corregido; no acredita round-trip reasoning pendiente ni
storage opaco seguro/frozenM6/quotaqual ni full/cierre global.

Actualización 02:13 UTC: None bloquea ProviderState y referencias opacas de
ReasoningBlock en solicitudes salientes, sin borrar evidencia/uso. RED opaco
2casos/1FAIL; focal68PASS0FAIL0SKIP5.898s/build0/0, fixtures offline + SQLite/CAS
reales (`reasoning-none-expanded-final.log`). Unknown mantiene compatibilidad;
cambio de política en resume conserva rechazo por fingerprint real.
LoadConversation no reconstruye ReasoningBlock al reabrir: ensayo2FAIL
`reasoning-none-expanded.log`, pendiente explícito, no round-trip acreditado.
Storage opaco seguro/otras políticas/effort/frozenM6/TargetRef/quota qualification
y auditoría completa siguen pendientes; no full verde nueva ni cierre global.

Actualización 02:06 UTC: full sobre47cdf9c terminal:2449 casos = 2444PASS /
1FAIL / 4SKIPsymlink,353.192s (`reasoning-and-gate-scope-full.log`). Único fallo
InternalActCommandTests suponía que todo envelope conTurnId comparte el command
del modelo; gates ahora conservan eseTurn pero tienen su propio command.
Fixture corregido para exigir partición completa por secuencia: resume hasta
TurnCompleted usa segundoCommand, todos los eventos posteriores de validación
usan un tercero distinto y mismoRun/Lane/Turn; ningún evento queda excluido.
También comprueba restauración del ExecutionScope. Focal16PASS0FAIL0SKIP5.648s
(`completion-gate-causation-final.log`), build0/0. No full verde posterior aún.
Freeze96633 levantado solo al terminal. Propuestas Luna replayNone/preserve y
SQLite/CASreopen leídas por root; cambio de política usa huella real y debe
rechazar resume, no omitir el guard con un fingerprint constante.

Actualización 01:56 UTC: corregida atribución de gates process.exec.
CheckRunCompletionAndGate selecciona el último Turn persistido de la Lane raíz
única, exige que esté completado y conserva ExecutionId de origen. No crea un
Turn ni hereda scope ambiental/otra Lane; último Turn abierto no toma uno viejo.
ConfiguredCompletionGates no ejecuta procesos sin Run/Task/Lane/Turn compatibles.
Fixture M3 ahora entra por el command autoritativo del servidor, no directamente
por RunCoupon, y exige Task/Lane/Turn para todos los envelopes de la ToolCall.
RED autoritativo: 1 FAIL, 1.846s (`completion-gate-turn-scope-authoritative-red.log`)
con TurnId null. Final ampliado: 74 PASS / 0 FAIL / 0 SKIP, 8.136s
(`completion-gate-turn-scope-expanded.log`), build 0/0; incluye ambient ajeno,
Turn posterior de otra Lane y rechazo sin Turn/latest root abierto. Barrido35
anterior con fixture directo dio2FAIL; se corrigió el harness para entrar por
servidor, no se debilitaron assertions. Luna halló el defecto por auditoría;
root reprodujo/implementó/verificó. Procesos dotnet --version/git y SQLite/CAS
privados reales, modelos fixtures sin autenticación ni consumo proveedor.
TargetRef de process cwd absoluto frente a documentación relativa aún requiere
corrección/auditoría; no se declara cerrado todo ToolCall v3 o M5.5.

Actualización 01:51 UTC: `models.yaml` acepta una declaración `reasoning`
con `supported` boolean opcional, `effortLevels` lista opcional y `replayPolicy`
enum exacto opcional. DTO/cargador generado AOT y schema embebido conectados.
Omisión/objeto vacío son Unknown; strings boolean, valores desconocidos, claves
extra, duplicados/listas inválidas y esfuerzo sin soporte explícito se rechazan
con ruta y ubicación. Un YAML válido llega a route/profile/fingerprint sin
activar ReasoningRequest ni gasto y no se copia a endpoint override.
RED: 19 casos / 18 FAIL / 1 PASS, 0.380s (`reasoning-capability-yaml-red.log`).
Final configuración+contratos/fingerprint: 86 PASS / 0 FAIL / 0 SKIP, 1.329s
(`reasoning-capability-yaml-final.log`); build 0/0. Son fixtures sin catálogo
autenticado ni consumo real. Aplicación del esfuerzo/replay y almacenamiento
seguro siguen pendientes. Auditoría readonly Luna halló gates Build/Test sin
TurnId; root comprobó el scope de CheckRunCompletionAndGate y revisa regresión.

Actualización 01:48 UTC: añadido `ReasoningCapability` como hechos declarados
inmutables: `Supported` nullable, `EffortLevels` nullable (etiquetas opacas del
provider, sin ranking universal) y `ReplayPolicy` nullable. Unknown no implica
false/None; listas contradictorias o inválidas se rechazan. ModelDefinition y
ModelRoute transportan la declaración; el perfil toma la de la ruta explícita,
sin copiarla a un endpoint override. La ruta física conserva su identidad y
autorización; `model.reasoning.declared` registra los hechos en el fingerprint.
Legacy Unknown conserva los componentes anteriores. Pruebas programáticas y
JSON sintéticas: 88 PASS / 0 FAIL / 0 SKIP, 4.158s,
`reasoning-capability-contract.log`; build 0 warnings / 0 errors. Primer build
falló por import de fixture y analyzer static local, corregidos sin suprimir
analyzers; no se atribuye como RED de producto. Root implementó y verificó.
No es aún carga YAML/catálogo ni aplicación wire de esfuerzo/replay; ambas,
junto con almacenamiento opaco seguro y salida completa, siguen pendientes.

Actualización 01:44 UTC: suite completa sobre `d285983` terminada:
2422 casos = 2418 PASS / 0 FAIL / 4 SKIP por permisos symlink, 350.643s;
`qualification-user-daily-full.log`, handle 69420 terminal. Estos resultados
reemplazan el estado pendiente de la suite, no acreditan consumo real ni consultas
autenticadas. Las cifras focales siguientes se solapan y no se suman.
Qwen local: GET /v1/models en los dos endpoints configurados rechazado a
01:44 UTC; ningún catálogo ni generación disponibles, sin arrancar servidores,
reconfigurar TLS o usar fallback pagado. ADR0047 existe sin commit en el repositorio
principal y fue leída sin modificarlo; no fue incorporada a esta rama.
Auditoría readonly de Luna confirma familias M6 aún ausentes (Delegation,
WakeRequest, JoinPolicy, SupervisionBinding, ResultDisposition) y sin esquemas
completos aceptados en la rama; no se inventan payloads ni scheduler/joins.

Actualización 01:36 UTC: integración User diaria de cualificación implementada y
verificada con 406 PASS / 0 FAIL / 0 SKIP, 36.391s,
`qualification-user-daily-concurrent-final.log`; build0/0. Reserva por probe para
Metered/Credit/Unknown, recibo FULL/CAS antes de liquidar, lectura compartida de
workspace+User sin doble conteo, reconciliación del crash window y readonly User
reader para Explorer. Cancel/timeout/error/unknown/retry conservan cobertura;
uso inválido solo raw CAS, exceso de coste/envíos bloquea siguiente probe/perfil.
Dos Hosts concurrentes y chat normal (hoy/ayer/crash) cubiertos offline. Root
implementó y verificó; Luna propuso fixtures y auditó límites/extracción. No
consumo/autenticación reales; la full posterior se acredita arriba. Histórico RED diario
de debajo ya corregido; IncludedQuota de cualificación sigue pendiente. Permanecen
replay opaco seguro/ReasoningCapability, contratos M6 congelados y auditoría de
los siete criterios; no se reduce el objetivo ni se declara cierre M5.5.

Actualización 01:13 UTC: recibo User ahora contrasta cabecera canónica CAS con
cada campo financiero/identidad/UTC, no solo hash y tamaño. Auditoría Luna:
uso inválido con coste nulo y view sin marcador admitidos; corregidos y controles
añadidos. Root añadió alteración SQLcost0 con CAS original válido y redacción del
output manteniendo cabecera. Final81PASS/0FAIL/0SKIP4.624s,
`qualification-receipt-header-final.log`; build0/0. Barrido328=327PASS/1FAIL13.029s,
`qualification-receipt-header-all-with-daily-red.log`; único fallo diario Host
sigue abierto. Fundamento listo, NO conexión diaria ni cierre M5.5 acreditados.

Actualización 01:08 UTC: fundamento User de recibos por probe + raíces CAS/GC
implementado, no wiring Host/diario todavía. Focal76PASS/0FAIL/0SKIP4.086s,
`qualification-receipt-store-expanded.log`, build0/0. Reopen/idempotencia exacta,
conflictos y uniques, transacción fallida, usage/cost unknown, decimal exacto y
sobreconsumo, rollover UTC, schema legacy/missing installed y GC transitivo con
perfil ausente/corrupción cubiertos con fixtures privados. Detalles en el documento
de fundamento enlazado abajo. Primeros3FAIL eran fixture path CAS incompleto y
artifact extra publicado por el fixture al sustituir Evidence, corregidos sin
debilitar assertions; no RED producto atribuido. Barrido319=318PASS/1FAIL12.336s
conserva fallo diario conocido. Últimos4controles tienen focal propia, no barrido
nuevo completo. No afirmar consumo autenticado ni M5.5 cerrado.

Actualización 00:59 UTC: frontera por probe preparada en `ProbeRunner`:
admisión antes de entrar al provider, observación esperada antes del siguiente
probe o de relanzar cancelación/timeout, coste/usage existentes preservados,
contador de envíos por invocación y timestamp UTC individual inyectable.
Error del provider es `Termination=Failed`; respuesta completa mal puntuada sigue
`Completed` con `ProbeStatus.Failed`. Fallo del observer aborta la suite, no se
convierte en fallo puntuable del provider. El observer en memoria NO es recibo
durable; el Host aún no conecta esta frontera al diario User ni al CAS/GC.
Focal 69 PASS / 0 FAIL / 0 SKIP, 2.200s,
`qualification-probe-boundary-final.log`; build 0 warnings/errores. Incluye retries
del adaptador HTTP real con handler inyectado (3 intentos/usage del último),
cancel/timeout con y sin respuesta, rechazo segunda admisión y rollover UTC.
Todos fixtures offline. Barrido `-class '*Qualification*'`: 292 casos = 291 PASS /
1 FAIL, 11.303s, `qualification-all-with-daily-red.log`; único FAIL conocido
diario agotado permite una llamada. No quitar esa regresión ni afirmar cierre.

Actualización 00:53 UTC: corregido el gate de cualificación `CreditBalance`:
sin precios completos no se despacha aunque el usuario consienta y el probe
declare coste máximo cero. RED: 9 casos / 7 PASS / 2 FAIL (ambos CreditBalance,
una llamada en vez de cero), `qualification-credit-pricing-red.log`.
Después del fix: 56 PASS / 0 FAIL / 0 SKIP, 2.665s,
`qualification-credit-pricing-final.log`; build 0 warnings/errores.
Incluye precio completo con créditos, Missing/Partial de CreditBalance,
MeteredCurrency y Unknown, cancelación, retry, evidencia y GC. Todo offline.
No corrige todavía la brecha del diario User en cualificación descrita debajo,
ni acredita una suite completa verde o consumo autenticado.

Actualización 00:44 UTC: commit `0ec4955` guardado. Full sobre ese commit terminó:
2336 casos = 2331 PASS / 1 FAIL / 4 SKIP (symlink), 375.762s,
`spend-reservation-wired-full.log`. El único fallo fue la fecha fija Oct6 en
BudgetContinuationTests frente al día actual Oct7 UTC. Clock inyectable conservando
default UTC y prueba de rollover UTC con offset local -6: 14 PASS / 1.515s,
`budget-continuation-utc-final.log`, build 0 warnings/errores. No nueva full verde.

La propuesta offline de Luna para cualificación fue leída íntegra, integrada y
compilada por root. RED reproducible: 2 casos / 1 PASS / 1 FAIL, 0.734s,
`qualification-daily-red.log`: diario .05 USD completamente reservado, pero hubo
1 StreamAsync en vez de 0. El control disponible crea un perfil parcial válido,
1 probe/.000066 USD sintéticos y evidencia CAS verificada. No autenticación/red
ni cargos reales. El test RED permanece pendiente de corrección y sin commit.

Cableado de reserva conservadora a Explorer/compactación, CLI ASK/ACT y escalación
que regresa al mismo runtime implementado. Lectura fresca atómica, recibos Barrier,
retry parcial conserva uncertain; cancelpre-send tiene eventos canónicos v1 sin
fingir consumo. Regresión concurrente RED previa ahora GREEN: una llamada/.30 USD
ante cap .50 (no éxito vacío bloqueando ambas). Focal67PASS y crossworkspace10PASS,
cifras solapadas. [Contratos y reproducción](m55-spend-reservation-foundation-20261007.md).
La focal ampliada dio inicialmente124=123PASS/1FAIL por descriptor desconocido de
un fixture meta de ayer; límites explícitos añadidos sin debilitar assertions.
Focal previa a la full: 116 PASS / 0 FAIL / 0 SKIP, 26.869s,
spend-reservation-wired-final2.log, build 0 warnings/errores. Incluye codec,
ledger/migración, intents de retry, meta/CAS diario, reopen, suspensión y guard
de escritores. Excluye los ocho ContextManagementTests ya pasados en la ejecución
anterior; no sumar cifras solapadas ni afirmar full verde de estas fuentes.

Brecha comprobada por lectura de código de Luna: ModelQualificationHost aplica el
cap autorizado de su suite, pero no el ledger diario/User ni acumulado canónico.
Siguiente bloque de presupuesto: RED offline con diario agotado, integración de
probes/costes inciertos y verificación. M5 permanece cerrado por el usuario.
ReasoningCapability/ReplayPolicy y almacenamiento opaco seguro, contratos M6
congelados y auditoría de los siete criterios de ADR0046 siguen pendientes.
Las secciones cronológicas siguientes conservan evidencia histórica; sus RED y
notas de "sin cableado" no describen el estado vigente de Explorer.

### Checkpoint de verificación 2026-10-06 23:29 UTC

Actualización del bloque IncludedQuota 23:45 UTC: corrección de admisión y consulta
del Host, consentimiento por invocación, reanudación TUI y NoClient implementados.
Focal final78PASS13.579s/build0/0 y TUI1PASS2.587s; fixtures identificados.
[Contrato, reproducción y límites](m55-included-quota-admission-20261006.md).
La suite completa nueva sobre `291842f` terminó con exit0: 2284 casos,
2280 PASS / 0 FAIL / 4 SKIP por permisos symlink, 341.611s.
Log `included-quota-full.log` en el directorio workers indicado abajo.
Esta evidencia incluye IncludedQuota, pero no demuestra reserva atómica de gasto.
Las notas RED de abajo describen el hallazgo previo, no un fallo dejado sin corregir.

### Regresión concurrente confirmada — 2026-10-06 23:56 UTC

Fundamento posterior: ledger SQLite de reservas con transacción inmediata,
10 casos propios incluidos en focal14PASS/0FAIL/0SKIP2.620s con architectureguard.
Incluye dos procesos independientes y rechazo de estado desconocido tras RED.
[Contrato, límites y reproducción](m55-spend-reservation-foundation-20261007.md).
Todavía sin cableado a Explorer/compacción: la regresión siguiente sigue RED.

Se añadió `ConcurrentSpendAdmissionTests.Two_workspace_admissions_must_not_exceed_shared_daily_cap`.
Dos journals SQLite y CAS independientes bajo un mismo User data directory, lector
real `UserWorkspaceSpendReader` y dos `ExplorerTurn`, sincronizados al persistir
el contexto tras comprobar el presupuesto y antes de `ModelStepStarted`.
Ambas respuestas son fixtures offline de 300000 tokens de entrada a 1 USD/millón.
Resultado RED: 0.60 USD / dos invocaciones ante un límite diario de 0.50 USD;
1 caso, 1 FAIL, 1.348s, sin consultas autenticadas ni coste real.
Log `concurrent-spend-red.log` en el mismo directorio workers. Build previo válido
0 warnings / 0 errores; errores iniciales de nombre de parámetro y analyzer del
fixture se corrigieron antes de ejecutar, no son RED del producto.
La prueba queda sin commit y conserva la assertion del límite. La suite verde
de `291842f` precede esta nueva prueba: no afirmar que el estado actual está verde.
Próximo bloque: reserva máxima previa y liquidación idempotente con coordinación
entre procesos, respetando límites Run/Session/User diario, reintentos y gasto
incierto. No copiar expiración automática ni un lock exclusivamente en memoria.

Suite completa sobre `abec849`, proceso terminado con exit0:
2272 casos = 2268 PASS / 0 FAIL / 4 SKIP por permisos symlink, 319.894s.
Log `agent-result-immutable-full-test.log` en
`C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/`.
Incluye AgentResult/PlanMutation, skills y escalaciones legacy; no consultas autenticadas.

Después de esa suite se añadió una regresión de cuota IncludedQuota a
CrossWorkspaceDailyCapRegressionTests (todavía sin commit ni corrección productiva).
CLI/TuiTurnHost, journal SQLite y HTTP loopback reales de fixture: primera llamada,
medición sintética de la MISMA sesión con ventana vigente, segunda llamada.
Con 9% restante se esperaba bloqueo previo/aprobación y hubo 2 llamadas en vez de 1;
el control de frontera 10% pasa. Focal completo: 7 casos / 6 PASS / 1 FAIL, 4.169s.
Build válido final 0 warnings/errores; dos intentos iniciales tuvieron errores del
fixture (enum y extracción de SessionId), no son evidencia RED del producto.
Logs `included-quota-red-build.log` y `included-quota-red-test.log`, mismo directorio.
Reproduce ADR0037 §7: reportar cuotas al panel no implementa su control de admisión.
Pendiente cablear Ask/consentimiento, denegación sin cliente, reanudación, scope y
validez de la medición antes de cada invocación; no reutilizar allow_plus monetario
como autorización de cuota ni convertir Unknown/Stale en cero.

## Instrucciones vigentes del objetivo

El usuario confirmó «M5 está cerrado» el 2026-10-06. M5 se conserva cerrado como
hito; los checkpoints anteriores describen su historia, no su estado vigente.
Las cuatro regresiones de cualificación se corrigieron como mantenimiento,
sin requisitos nuevos que reabran M5. Final167PASS/arquitectura56PASS, builds0/0;
RED de redacción confirmado en Host/CLI después de corregir cleanup.
[Terminales, redacción y reproducción](qualification-terminal-maintenance-20261006.md).
Full previa2240=2223PASS/13FAIL/4SKIP. M4 timeout reproducido con marcadores;
eliminar replay monetario inútil sin topes dio 1PASS94.749s (200turnos/plazo sin cambios),
119PASS focales y arquitectura56PASS. [Diagnóstico](m55-uncapped-replay-m4-recovery-20261006.md).
Full posterior a 36a5260: 2258 casos, 2250PASS/4FAIL/4SKIP, 314.973s.
Cuatro controles de overflow sin topes detectaron que la optimización era demasiado
amplia. Corrección conserva replay con precio actual o evidencia monetaria histórica,
incluidos legacy y cambio a modelo sin precio; focal54PASS8.248s/build0/0.
M4 corregido1PASS94.418s/arquitectura56PASS1.381s. Full sobre5fa5cf2:
2260=2256PASS/0FAIL/4SKIPsymlink318.331s. No equivale al cierre de las fronteras pendientes.
Tope diario entre workspaces: RED2=1PASS/1FAIL corregido en la ruta normal;
final133PASS/arquitectura56PASS. Lectura solo lectura por journal/CAS de origen,
sin reservas concurrentes. Consentimiento diario entre workspaces corregido:
121PASS focales; Session/Run no se amplían, stale offers rechazadas sin escritura.
[Continuación diaria](m55-user-daily-continuation-20261006.md).
[Implementación y reproducción](m55-user-daily-spend-20261006.md).
No acreditan consultas reales ni full verde posterior.
El objetivo activo continúa siendo M5.5 y preparación de M6, sin scheduler/joins.
Escalaciones legacy: EscalationV1ReopenRegressionTests persiste Requested/Approved/
Completed v1 y sus contrastes v2, cierra/reabre SQLite y aplica codecs/upcasters.
Comprueba Turn/Lane null en v1, atribución v2, modelos/causa/aprobador, UTC,
secuencia, Source/Causation y JSON/schema persistidos sin reescritura; el replay
mantiene Run/Task/Lane Running. Propuesta Luna auditada e integrada por root.
Final47PASS/0FAIL/0SKIP12.920s/build0/0 con filtros '*Escalation*',
'*AgentExecution*', '*Lineage*'. Fixture offline SQLite real privado, no proveedor
autenticado ni consumo real. Logs escalation-v1-reopen-final-build/test.log en
C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/.
Este control no sustituye los contratos de Delegation/Wake/mailbox aún pendientes.
Fingerprint AgentProfile de LaneCreated implementado en ruta Explorer/CLI,
final83PASS/arquitectura56PASS. Solo identidad durable disponible, sin inventar
registro de perfiles; el resto de fronteras sigue abierto.
[Perfil de lane y reproducción](m55-agent-profile-fingerprint-20261006.md).
Skills fingerprint: contrato ActiveSkillFingerprint(Id, Version, ContentHash) para
identidades ya resueltas, sin descubrimiento/selector/loader M8. Explorer recibe
activeSkills opcional y copia la lista; null no informado, [] vacío confirmado.
skills.active v1 guarda source=unavailable/skills=null o source=provided/skills=[...],
ordenado por Id ordinal; IDs incompletos/duplicados rechazados. No hereda skills
de baseline. CLI normal y plan aprobado declaran [] porque no cargan skills.
Un contributor KindSkill contradice [] y falla antes del proveedor, incluso antes
de poda; callers genéricos sin declaración mantienen explícitamente unavailable.
La lista no descubre ni autentica contenido aportado por callers: esos callers
deben proporcionar la identidad real de sus skills. No inventa versiones desde
ContextItem. RED1FAIL0.323s; final120PASS14.341s/arquitectura56PASS/builds0/0.
CLI real con provider fixture, conjunto desconocido/vacío y contradicción,
orden/version/hash/duplicados, SQLiteCAS reopen y refs cubiertos. Logs skills-fingerprint-*
en workers2103; no autenticación/consumo real ni cierre de M5.5 por este bloque.
Full sobre dffa273:2267=2263PASS/0FAIL/4SKIPsymlink317.303s,
skills-fingerprint-full-test.log. No incluye el bloque AgentResult siguiente.

AgentResult inmutable (ADR0046§2): las listas de Findings/ArtifactRefs/FilesChanged/
RemainingIssues/ProposedPlanMutations se copian y exponen solo lectura, también en
asignaciones with. MutationTarget congela DependsOn/ReorderList/SplitParts para que
una propuesta anidada no permita reescribir el resultado. Null legacy permanece
null, no se convierte en una lista vacía medida. Ctor/deconstruct/propiedades y
schema de LaneCompleted/TaskCompleted permanecen compatibles.
El test de roundtrip descubrió además que PlanMutation con propuestas no vacías
no se deserializaba por tener constructor privado. Constructor JSON público sobre
los mismos campos corrige la lectura, sin aceptar/aplicar propuestas ni saltar
PlanService. SQLite reopen prueba contenido, inmutabilidad y Task/Run Running.
RED1FAIL0.246s por alias mutable; codecRED4=3PASS/1FAIL0.799s por PlanMutation.
Final58PASS2.490s/arquitectura56PASS1.185s/builds0/0. Logs agent-result-immutable-*
en workers2103; fixtures offline privados, no auth/consumo ni lógica M6.
Luna HIGH terminó la matriz normativa M5 y ahora audita exclusivamente el contrato
ToolCallStarted v3; root conserva implementación, tests, integración y commits.

Reanudación reiterada por el usuario: cerrar M5 con Luna gpt-6-luna HIGH y avanzar
M5.5 con paquetes independientes GLM5.3/NVIDIA, Nemotron/OpenRouter y DeepSeek/NVIDIA.
`get_goal` volvió a confirmar ACTIVE a las 20:56:53 UTC. No se reemplaza el objetivo
inconcluso: la API disponible no admite editar su texto. Esta sección conserva las
instrucciones nuevas; no se reactiva la automatización histórica con plazo vencido.
Luna terminó la auditoría de traits/confidence y construyó el fixture integrado
Codex y la regresión de cancelación; root conserva producción, integración y
verificación. Ahora Luna audita terminales de probes (fallos/stopreason frente a
texto puntuable), solo lectura/propuesta externa y sin consultas reales.
Externos2017 y2103 terminaron: GLM/Deep timeout sin entrega y Nem propuestas
rechazadas por APIs inexistentes/ausencia de SQLite y controles requeridos.
Logs/manifiestos preservados; no integrar propuestas sin verificación ni repetir
paquetes fallidos a ciegas. Nem2103 reportó coste0; NVIDIA sigue desconocido.

El usuario pidió actualizar el objetivo y reanudarlo el 2026-10-06. `get_goal`
confirmó `active` a las 20:37 UTC: la pausa de producto ya fue revocada. El texto
del objetivo conserva M5.5/M6; estas instrucciones vigentes incluyen también el
cierre de M5 y el reparto de agentes solicitado. La automatización histórica sigue
pausada; no se crea otro objetivo ni se declara terminado el actual por un subconjunto.
La API disponible solo cambia el estado del objetivo, no su texto ni su reanudación;
este plan conserva la ampliación operativa solicitada sin fingir una actualización
del texto de producto. Se continúa el objetivo existente, ya activo.

Checkpoint de reanudación: HEAD ba7c2ff antes del bloque actual. Corrección
nullable/failclosed de la cota de intentos y propagación por Telemetry verificadas.
Regresión reproducida: 11 casos, 5 PASS/6 FAIL; después del cambio, 11 PASS
(0.463 s), build sin errores ni advertencias. Son fixtures, no consumo autenticado.
Luna HIGH terminó la adaptación explícita de once providers de pruebas en memoria;
root conserva ownership de producción, auditoría, documentación y verificación.

- Luna `gpt-6-luna`, esfuerzo **alto**, concentrada exclusivamente en el cierre M5.
  Prioridad: persistencia atómica de perfil/traits; coste real y desconocido sin
  cero ficticio; evidencia CAS de resultados/BenchmarkIdentity; validación reproducible
  de rutas conectadas, distinguiendo fixtures de consultas autenticadas.
- GLM5.3/NVIDIA, Nemotron/OpenRouter y DeepSeek/NVIDIA: paquetes acotados en paralelo
  para M5.5, ownership disjunto y auditoría root antes de integrar. Nemotron requiere
  catálogo gratuito, max_price cero y fallback pagado prohibido. NVIDIA no se declara
  gratuito sin datos. Sin reintentos ciegos de paquetes fallidos ni sustitutos pagados.
- Root integra, reproduce RED/controles, corrige defectos comprobados sin debilitar
  assertions, verifica riesgo proporcional y guarda un commit por bloque.
- Cola M5.5: presupuesto/ledger y reservas pendientes; almacenamiento/replay opaco;
  ToolCallStarted v3; contratos M6 aceptados; fingerprint y outcomes de commands.
  No inventar semántica no acordada ni confundir nombres de contratos con implementación.
- Conservar main, cambios ajenos, procesos sin ownership probado, credenciales y
  sección Git de OmniCoder; no push/merge ni cambios a servidores/TLS/cuentas.
- Entregar commits, pruebas, ownership y pendientes reales. M5/M5.5 solo cierran
  con evidencia de sus criterios, no por conteo de tests ni disponibilidad de login.

Rama: `codex/omnicore-consolidation-20261004`, worktree `m55-artifactrefs-roundtrip`.
Main, cambios ajenos, credenciales y procesos no propios se preservan; no push.
M4 Windows ya tiene evidencia de cierre; esta ronda no la reemplaza.

## Avance verificado

- Captura automática filesystem normal anterior al Barrier/efecto, preimagen
  fiel UTF-8/UTF-16 en CAS, ausencia distinta de vacío, fallo seguro ante secretos
  o CAS inválido y lease síncrona contra GC prematuro. Nuevos ancestros/links
  permanecen Unknown; no restore M7. Focal147PASS/arquitectura56PASS, builds0/0.
  [Contrato y reproducción](m55-filesystem-preimage-20261006.md).
  M5.5 no cerrado.
- Expiración/resolución de cuestionarios y terminales cancel/interrupt conservan
  scope durable original, incluido ExecutionId, con causación del comando real.
  RED3 cuestionarios/RED4 terminales; final96PASS/arquitectura56PASS, builds0/0.
  [Corrección y reproducción](m55-interaction-terminal-attribution-20261006.md).

- M5.5 ToolCallStarted v3: contrato init aditivo, reversibilidad Unknown por defecto,
  destino único de claims filesystem, ArtifactRef de preestado indexado en envelope,
  upcasters v1→v2→v3 y SQLite/CAS reopen/GC. RED3FAIL; focal160PASS4.485s,
  arquitectura56PASS1.167s/builds0/0. Captura automática fiel y política sensible
  siguen pendientes; hashes de reconciliación no equivalen a preimagen.
  [Contrato, límites y reproducción](m55-toolcall-v3-20261006.md).
- M5.5 criterio exacto3pasos+suspensión: nuevo control root del runtime existente,
  SQLite/CAS reales, fake.read→user.ask→reopen→respuesta server→tercer paso final.
  Un Turn/3completions índices0..2; uso60entrada/9salida/cache12+6/razonamiento5,
  coste estimado0.000078USD, snapshots idempotentes antes/después de dos reopen,
  refs CAS verificadas y prefijo journal intacto. Aislado1PASS1.354s/build0/0;
  focal84PASS5.776s; no provider autenticado ni runtime modificado.
  [Criterio y reproducción](m55-three-step-suspension-20261006.md).
- M5 cancelación Host: preserva OCE del caller entre probes y antes de publicar
  evidencia del último stream cancelado; runner NotRun compatible. Luna3fixtures,
  rootcontrol final/CAS y fix. RED4=2PASS2FAIL0.569s; focal272PASS6.618s/build0/0,
  arquitectura56PASS0.977s/build0/0; full2181=2177PASS0FAIL4SKIPsymlink313.507s/exit0.
  No atomicidad CAS+SQLite
  frente a cancelación concurrente prometida ni authreal. [Contrato](m5-qualification-cancellation-20261006.md).
- Uso reportado coherente M5/M5.5: validator compartido Domain, cualificación,
  ejecución/lecturas de gasto, reporter de sesión y productor meta. Contradicciones
  reportadas no autorizan herramientas/coste/resumen de compactación; uso/máscara
  crudos preservados, Unknown/null sin datos inventados. RED reales12 runtime/Host,
  6 reporter y3 meta; focal final403PASS2.275s, full2174=2170PASS0FAIL4SKIPsymlink
  105.726s/exit0, arquitectura56PASS0.642s/build0/0. Luna propone controles/root
  audita, implementa y verifica; no auth/factura real ni cierre por conteo.
  [Contrato y evidencia](m5-reported-usage-consistency-20261006.md).
- M5 identidad de endpoint: control normal Host/HTTP loopback/SQLite/CAS exige diez
  requests a B override, cero a A configurado, key/perfil/evidencia B y Get A vacío.
  Luna HIGH fixture/root auditoría, corrección CS0136 y timeout privado; focal final
  endpoint+cota15PASS0.683s/build0/0. No autenticación ni gasto real, ni defecto de
  identidad demostrado; [reproducción](m5-effective-endpoint-integration-20261006.md).
- M5 intentos conocidos: capacidad aditiva IModelRequestAttemptBound en adaptadores,
  cota desde resiliencia efectivamente adquirida; default3/Codex6, long sin overflow.
  Preflight/preview cuentan intentos conocidos y dos tests RED reales ya pasan.
  No es factura, reserva ni garantía wire (excluye auth/redirect). Inyección legacy
  sin bound ahora permanece null y failclosed salvo Local explícito; wrapper Telemetry
  propaga cota o null, evidencia captura valor/fuente preflight. Focal253PASS1.654s,
  arquitectura56PASS0.652s/build0/0; full conjunta con endpoint2126=2122PASS0FAIL
  4SKIPsymlink104.637s/exit0. Recompilación fresca tras CS0136 corregido en fixture.
  [Contrato y reproducción](m5-unknown-attempt-bound-20261006.md).
  Auditoría también halló subset tokens reportados incoherentes; corregido en el
  bloque de uso coherente arriba, sin regla de suma de caché inventada.
- M5 sesión Codex: wiring defectuoso reproducido con ruta normal Host/Responses/
  sesión sintética privada/HTTP loopback/SQLite/CAS. RED3=1PASS2FAIL; enlace de
  subscription corregido solo para perfilcodex, green3PASS, focal268PASS/arch56PASS
  builds0/0; full fresca2177=2173PASS0FAIL4SKIPsymlink266.947s/exit0.
  No login ausente inferido ni cambio API key,
  cuota/coste desconocidos no cero; no consulta autenticada real acreditada.
  [Contrato y evidencia](m5-codex-subscription-wiring-20261006.md).
- M5.5 root: tres controles reales SQLite/CAS para éxito/deny/fallo entre dos Runs
  de la misma Session, primero cancelado por servicio normal antes del siguiente.
  Envelopes/UTC/prefijo histórico/refModelStep/uso sobreviven reopen sin fuga scope.
  [Alcance y reproducción](m55-multirun-attribution-20261006.md). No prueba todavía
  ToolCallStartedv3, suspensión/resume ni todas las Lanes/terminales.
- Full final conjunto2111=2107PASS0FAIL4SKIPsymlink106.764s/exit0, focal282=279PASS
  0FAIL3SKIP3.563s, arquitectura56PASS0.623s/build0/0. Full intermedia tuvo1FAIL por
  cancelación explícita del servidor TLS privado en cleanup; fix estrecho conserva
  las tres assertions/timeout y ninguna política TLS productiva cambia. Aislados1PASS
  2.382s y1PASS2.343s. [Evidencia](tls-fixture-shutdown-20261006.md).
  Ronda externa2017: Nemotron14,485tokens/coste0, propuesta rechazada por falsoDeny,
  metadata ausente y cleanup oculto. GLM/Deep timeout240s, sin entrega, sin reintentos
  idénticos/fallback ni código integrado. No consultas autenticadas de cualificación.

- M5 descriptor ausente: el API de cualificación sin descriptor ni provider inyectado
  rechaza con CostEvidenceUnavailable antes de conectar al fallback OMNI_BASE_URL;
  ausencia no significa Local ni gratuito. Cinco casos nuevos: RED 3PASS/2FAIL,
  controles offline y configuración Local explícita conservados. Los fixtures CLI
  ahora usan configuración normal con descriptor Local, no un override sin billing.
  Focal244PASS5.393s, full2102=2098PASS0FAIL4SKIPsymlink106.833s/exit0,
  arquitectura56PASS0.884s, builds0/0. No acreditan consumo autenticado.
  Inyectar IModelProvider sigue siendo responsabilidad del consumidor: puede ser
  remoto y no demuestra identidad/billing correspondiente al descriptor.
- Auditoría Luna HIGH: Quick mide InstructionFollowing (7 probes, confianza de
  conteo .7) y StructuredOutputReliability (3, .3), no los nueve traits mínimos.
  Qualified significa pasar Quick completa; no calibración ni confianza estadística.
  Full/calibración quedan en M10+. Siguiente auditoría M5: coste potencial de retries
  y campos omitidos; la cobertura Quick no acredita garantía monetaria.

- M5 cobertura Quick/namespace: Qualified requiere hash del conjunto canónico completo
  + todos Passed; subsets/modificaciones quedan Provisional, SuiteComplete explícito.
  Snapshot de probes estable cruza awaits; rechaza empty/IDs duplicados/suite inválida
  antes de llamadas. Writer durable admite solo User user.db; legacy otrasDB sigue.
  Rutas SQL construidas sin interpolación de opciones, conexión no pooled.
  RED cobertura4FAIL/namespace1FAIL/snapshot2FAIL/path1FAIL; focal final239PASS5.085s,
  arquitectura56PASS0.855s/build0/0. Full2097=2093PASS0FAIL4SKIPsymlink113.597s,
  exit0. Luna6 controles+audit/root7+fix.
  Cinco fixtures de un probe corrigen estado inexacto, añaden gatefalse y preservan
  costes/usage/calls/traits. DDL fixture comprueba schema real en vez de rowcount sticky.
  M5/M5.5 no cerrados: siguiente RED/fix API registryOverride sin ProviderDescriptor
  y sin provider inyectado; configuración normal valida existencia de provider.
- M5 evidencia durable implementada: capability aditiva, schema User y marcador
  failclosed, writer con lease CAS Verify→txcommit perfil/traits/ref, metadata completa,
  historia por revisión y alias Stale/route con SourceRunRevision. Host publica JSON
  completo de probes/respuestas/puntajes/usage nullable/identidad build y taskSetHash
  real; sampling no enviado se registra null/false, override de probes explícito.
  Luna aporta12 casos de store; root producción, dos Host y tres controles extra,
  auditoría e integración. Focal226PASS5.053s, arquitectura56PASS0.508s, builds0/0.
  Full2084=2080PASS0FAIL4SKIPsymlink106.245s evidence-final-full.log, exit0.
  Fixtures no acreditan consumo autenticado. Auditoría Luna detecta Qualified
  incorrecto para subsets de probes: próximo RED/fix de cobertura Quick; verificar
  también writer con nombreDB legacy distinto de user.db frente namespace de GC.
  Pendientes M5: límites reales/reservas/retries, ruta sin descriptor, validación conectada
  y auditoría de cobertura quick frente traits mínimos; no cierre por guardar artifacts.
- Reanudación confirmada: objetivo de producto ACTIVE a las19:44UTC. Se mantienen
  M5 + M5.5 y preparación M6, Luna HIGH y paquetes externos autorizados; la
  automatización nocturna histórica permanece pausada.
- Raíces GC User: lector de todas las revisiones de evidencia, validación CAS antes
  de sweep, conexiones read-only no pooled y scope User explícito en CLI. Luna aportó
  nueve regresiones de raíces, seis de CLI, dos de sampling ausente y auditoría;
  root integró, reprodujo RED8FAIL, corrigió producción y validó.
  Full2067=2063PASS0FAIL4SKIPsymlink104.999s; focal291=290PASS0FAIL1SKIP9.128s;
  arquitectura56PASS0.651s; builds0/0. Fixtures privados, no consumo autenticado.
  BenchmarkIdentity acepta sampling nullable, con cambio público de getters documentado.
  Pendiente writer/schema productivo y marcador durable: tabla ausente debe admitirse
  solo como legado, no tras declarar instalada la capacidad de evidencia. El writer
  debe retener lease entre Verify y commit de perfil/traits/ref. No cerrar M5 todavía.
- M5 Stale: corregida pérdida de traits vigentes al avanzar revisión; read/guard/update/
  copia/cancellation en una transacción, checked-overflow e historial conservado.
  Upgrade one-shot de perfiles afectados sin medidas actuales y con medidas previas;
  no mezcla sets actuales, no inventa medidas ni restaura vaciados posteriores.
  Repair y migración de ruta comparten tx/rollback/markers. Luna aporta nueve regresiones
  y auditoría; root implementa, agrega upgrade/idempotencia, corrige fixtures y reproduce.
  RED inicial4FAIL + upgrade1FAIL; focal final222PASS2.275s, arquitectura56PASS0.653s;
  full2050=2046PASS0FAIL4SKIPsymlink106.160s, builds0warnings/errores.
  CAS completo/ref histórica siguen pendientes, no cerrar M5; GC User verificado arriba.
- M5 preflight: estimación configurada y consentimiento coherentes, rechazo de
  tarifas incompletas MeteredCurrency y sumas no representables antes del provider;
  Chat compatible serializa el límite exacto sin retirarlo ante error400.
  Luna aportó regresiones de cap y wire; root implementó, auditó y agregó preview
  y overflow directo. RED reproducido antes de correcciones, sin assertions debilitadas.
  Focal207PASS; full2035=2031PASS0FAIL4SKIPsymlink107.362s; arquitectura56PASS0.662s.
  Fixtures privados, no consultas autenticadas ni garantía monetaria remota.
  Guard Unknown configurado sin precios completos implementado en el bloque siguiente;
  después CAS/evidencia y raíces de GC user.db.
  Reintentos y reserva/liquidación siguen pendientes; M5/M5.5 no están cerrados.
- M5 Unknown: Host rechaza antes del provider una estimación incompleta en rutas
  configuradas Unknown, incluso con consentimiento y cap positivo. Luna aporta cinco
  regresiones; root reproduce3FAIL y corrige guard/CLI, conserva assertions anteriores
  con fixture Local explícito. Focal212PASS2.170s, arquitectura56PASS0.798s.
  Full2040=2036PASS0FAIL4SKIPsymlink116.037s, build0warnings/errores;
  no cerrar por controles privados. Falta revisar el camino
  legacy sin descriptor (incluido registryOverride), reservas/retries y CAS durable.
- M5 uso/coste: null cuando falta uso reportado o tarifas completas, precios explícitos
  reutilizados de configuración modelo/provider, máscara conservada y contadores nullable
  en Host; CLI bilingüe sin cero ficticio. Luna aportó RED, CLI HTTP y tarifas/SQLite;
  root implementó, auditó y añadió controles de precios/uso inválidos. Focal 156 PASS,
  2.055s; build0warnings/errores. Full2018=2014PASS0FAIL4SKIPsymlink106.623s exit0;
  arquitectura56PASS0.585s. CAS/BenchmarkIdentity y cota monetaria pre-call pendientes.
  La auditoría CAS detectó GC sin raíces user.db y parámetros de sampling no enviados:
  no guardar refs desprotegidas ni inventar seed/temperatura0 para cerrar M5.
- Persistencia M5 de perfil y traits en una transacción, con validación de identidad,
  revisión y cancelación; siete regresiones aportadas por Luna y auditadas por root.
  Focal 125 PASS, arquitectura 56 PASS. Full repetida 1998 = 1994 PASS/0 FAIL/4 SKIP
  symlink, 112.034s. La primera ejecución tuvo un fallo TLS existente cuya causa
  intermitente sigue abierta; los logs de ambas se conservan. Bloque siguiente de
  coste desconocido implementado arriba; evidencia CAS de cualificación aún pendiente.
- `de76958`: checkpoint de TUI/runtime y observabilidad de sesión.
- `ad65f0f`: ModelRoute/RouteId y emisión durable en ModelStepStarted v3;
  journals v1/v2 legibles y ruta conservada tras suspensión/reanudación.
- `d2e020a`: componentes versionados del fingerprint, hash anterior compatible,
  referencias CAS indexadas y verificadas.
- `54dfd95`: cualificación por ruta y migración legacy Stale, integrada en CLI/router.
- Checkpoint ProviderState por ModelStep: replay tras reabrir SQLite/CAS, guard
  Run/Lane/Turn/modelo/ruta y retención transitiva GC; adapter Anthropic real con SSE
  de fixture. [Contrato y límites](m55-provider-state-checkpoint.md).
  Commit `5f40a81`; uso reportado conservado incluso si falla el checkpoint.
- Configuración de topes User y ampliación durable via InteractionResolved(User):
  [contrato/pruebas/límites pendientes](m55-budget-continuation.md).
- `a0c46c6`: BillingMode declarado sin inferencias de credenciales/endpoint, validación
  YAML/schema y metadata preservada en adapters; [contrato](m55-provider-billing.md).
- NoClient/Deny cierran sólo el Run originario con BudgetExceeded, conservan efectos
  desconocidos y retiran el overlay; [integración](m55-budget-lifecycle.md).
- SessionRoutingPolicy durable, bindings exactos y consentimiento previo a invocación/
  escalación; claves y precios no autorizan rutas MeteredCurrency/Unknown.
  Replay valida la revisión y SQLite conserva permisos exactos tras reinicio.
  [Contrato y límites](m55-session-routing-consent.md). Auto/ask sin cliente deniegan;
  ask con cliente publica InteractionRequest real. Reanudación durable del destino
  tras aprobación, sin nuevo input/Run ni consumo prematuro de FollowUps:
  [contrato y pruebas](m55-routing-resume.md).
- Breaker existente compartido por provider en el runtime; router inicial y escalación
  consultan snapshots sin health requests. Pruebas HTTP loopback de las tres familias;
  [contrato, evidencia y límites](m55-provider-circuit-routing.md).
- Router con preferencias/rechazos RouteId y ModelId separado, ruta retenida al invocar,
  alias YAML traducido y cadena vacía después de excluir origen sin fallback implícito.
  Core focal 102 PASS; full 1696 casos/1692 PASS/0 FAIL/4 SKIP symlink,
  201.818 s. [Contrato y verificación](m55-routeid-router.md).
- Cualificación por endpoint/protocolo/runtime build y perfil del adapter;
  asociación opcional del EffectiveModelProfile con RouteId.
- Migración SQLite una sola vez: perfiles legacy pasan a Stale sin reescribir su clave,
  suite ni evidencia. Revisión incrementada, traits históricos preservados y copiados
  a la nueva revisión. Causa `route-identity-migration`, separada de la versión de suite.
  Una identidad corrupta aborta la migración; no se modifica el estado del perfil.

La clave nueva utiliza el endpoint efectivo (`OMNI_BASE_URL` si existe) y el build del
Host identificado por versión/MVID. No se transfiere cualificación de un endpoint a otro.
El perfil, router y selección del runtime reciben la misma ruta; los overrides de endpoint
tienen identidad distinta, mientras la ruta configurada 1:1 conserva el ID anterior.
El runtime ya registra build real y digests de descriptor/selección, perfil, harness,
contexto y ruta física. [Contrato parcial y evidencia](m55-runtime-fingerprint.md).
Focal86PASS; full1823=1819PASS/0FAIL/4SKIPsymlink210.175s, exit0.
La CLI también compone herramientas visibles/prompt/revisión inicial del plan porTurn;
guard de drift en el mismo Turn abierto y snapshots consistentes, focal89PASS.
provider.adapter v2 incorpora tipo/build de la instancia conectada, no sólo la ruta;
ausencia de instancia se representa con null. RED9=5PASS4FAIL; focal103PASS/0FAIL,
7.614s, con CLI HTTP loopback/journal real y controles de replay legacy. Build0warnings/errores.
Full1933=1929PASS/0FAIL/4SKIPsymlink268.052s, exit0; snapshot posterior no incluido.
La nueva representación no se migra sobre Turns abiertos: su guard de drift se conserva.
model.profile v2 ahora incluye key hash/revisión/estado del mismo snapshot que aporta
traits a la CLI; una solaGet, Traits(revisiónexacta), lookup endpoint efectivo. Sin
evidencia utilizable, metadata null explícita. No altera keys ni cualifica proveedores.
RED4FAIL; focal final89PASS/0FAIL/0SKIP6.141s, build0warnings/errores, SQLreopen real
y CLI HTTPloopback con fixture de keyajena. Fullfinal1949=1945PASS/0FAIL/4SKIPsymlink
266.837s, exit0; no incluye las regresiones monetarias posteriores. Fallo de cleanup
Windows de fixture previo documentado, no assertion debilitada ni ClearAllPools.
Sumas monetarias históricas no representables ahora producen bloqueo controlado bajo
tope o Error/TurnAbandoned sin tope; no se fabrican totales cero ni ampliaciones.
Después de ModelStepCompleted se comprueba también histórico+actual ANTES de tools,
conservando usage/coste/artifact del paso. Diario entre sesiones y reopen SQLite/CAS
probados. RED histórico6=2PASS4FAIL; RED aislado posterior al paso6=2PASS4FAIL;
focal final75PASS/0FAIL/0SKIP4.203s, build0warnings/errores. Full1964 casos:
1960PASS/0FAIL/4SKIPsymlink266.765s, exit0 (money-overflow-full.log).
Fixtures offline, no consumo autenticado. [Evidencia](m55-budget-continuation.md).
Auditoría read-only: AgentProfile no tiene resolver activo; ProfileId de Lane no es
un perfil efectivo. toolPreferences está diferido a M8 por ADR0027. No se inventan
componentes: completar los contratos exigidos por ADR0017 sin afirmar implementaciones
futuras; política efectiva/boundary/tools visibles ya verificados en integración siguiente.
Integración política→request.Tools→TurnStarted verificada con ObserveOnly/PatchOnly
y repetición estable entre IDs distintos (10focalPASS antes de CAS). Ahora los cinco
componentes resueltos y tres por Turn tienen Content CAS exacto cuando no redactado;
tools.plan/prompt.template v2 incluyen schema/texto completos. Orden blob→evento,
refs en envelope, SQLite/CAS reopen y CLI HTTP loopback probados. RED1FAIL;
focal final84PASS/0FAIL/0SKIP7.565s, build0warnings/errores; full1970 casos:
1966PASS/0FAIL/4SKIPsymlink267.460s, exit0. No incluye test posterior de fallo append.
provider.adapter privado/redactados mantienen Content=null; no bypass de ADR0018 ni
claim CAS exacto para datos ausentes. [Contrato y límites](m55-runtime-fingerprint.md).
Faltan AgentProfile/skills efectivos y trazabilidad explicable completa; no se declara
cerrado el fingerprint de M5.5. Full1834=1830PASS/0FAIL/4SKIPsymlink213.545s, exit0.
GC ya distingue la forma completa conocida del fingerprint de JSON arbitrario
`modelKey/components/hash`; conserva refs transitivas y aborta antes del sweep si faltan.
RED2FAIL, focal44PASS; full1843=1839PASS/0FAIL/4SKIPsymlink213.513s, exit0.
No incluye el siguiente paquete de presupuesto. [Evidencia](m55-runtime-fingerprint.md).

## Cola de cierre (orden operativo)

1. Completar replay opaco: storage seguro para estados que el redactor actual alteraría,
   ReasoningCapability y aplicación de ReasoningReplayPolicy. Checkpoint/resume básico probado.
   Binding físico de checkpointv2 implementado: cambiar endpoint configurado aunque
   conserve RouteId legacy no autoriza replay. Focal62PASS/native58PASS;
   full1706=1702PASS/0FAIL/4SKIP symlink,204.762s (cifras solapadas).
   [Contrato y evidencia](m55-provider-state-physical-binding.md).
   IArtifactStore sólo tiene PutText/GetText/Verify; PutText redacta antes del hash.
   ADR0005/0046 exige opaque exacto, ADR0018 texto redactado. Mantener fail-closed;
   resolver explícitamente ese contrato antes de añadir storage, no bypass del redactor.
2. Consentimiento de rutas y resume de escalación ask en TUI implementados.
   Completar resume de selección inicial y presupuesto.
   Auto/ask no pueden ampliar gasto ni rutas autorizadas. Diario entre sesiones y continuar.
   Configuración User y efecto de allow_plus probados; completar ledger User-wide,
   reserva/liquidación atómica y UI/CLI resume. BillingMode y NoClient/Deny → RunFailed probados.
   Corregida omisión de costes de compactación: las invocaciones meta se contabilizan
   por identidad propia, sin sumar otra vez el summary del modelo principal.
   Guard previo a materializar bajo tope y posterior al meta antes primary/tools.
   RED5=1PASS4FAIL; focal final156PASS y full1982=1978PASS/0FAIL/4SKIPsymlink,
   274.147s, exit0. Arquitectura56PASS. Primera full con dos fallos conservada;
   TUI NO_COLOR y cierre sin meta corregidos sin debilitar assertions.
   Dedup Completed+Failed, incompleto nozero y suma meta+primary tienen controles
   offline dedicados; no equivalen a reserva ni a consumo autenticado.
   [Contrato y evidencia](m55-budget-continuation.md#compactación-invocación-separada-del-modelo-principal).
   Corregido el bypass intraAsk: un nuevo ModelStepCompleted con CostUsd null por
   falta de uso Input/Output detiene antes de tools/nueva llamada bajo tope monetario,
   conserva completion/flags, no asume coste cero ni ofrece allow_plus. Cero medido
   y uncapped conservan comportamiento; SQLite/CAS reopen mantiene coste desconocido
   y bloquea nuevo gasto de otra sesión del mismo workspace, sin contaminar la original.
   RED5casos1PASS4FAIL; focal45PASS; full1853=1849PASS/0FAIL/4SKIPsymlink212.612s,
   exit0. [Contrato y evidencia](m55-budget-continuation.md).
   Responses API y Anthropic ya reciben el MaxOutputTokens seleccionado del registry;
   su body se verifica en fixtures HTTP locales y el fingerprint captura la solicitud.
   RED28casos13PASS15FAIL; focal90PASS/0FAIL/0SKIP3.374s, build0warnings/errores.
   Full1891=1887PASS/0FAIL/4SKIPsymlink231.777s, exit0.
   Chat y el perfil Codex aún no tienen cota aplicada. Esta mejora no acredita reserva
   de coste, cumplimiento por el servidor, consultas autenticadas ni consumo real.
   [Contrato y evidencia](m55-budget-continuation.md#límite-solicitado-de-salida-en-rutas-nativas).
   Reportes negativos ya reproducidos (RED14=1PASS13FAIL) y corregidos: coste NULL,
   evidencia por invocación original, bloqueo antes tools/otrarequest; sin topes hay
   error/TurnAbandoned. No summary numérico falso, audit usageStatus=invalid.
   Overflow también reproducido (RED25=19PASS6FAIL) y corregido con sumas checked
   después del append durable; no se pierde la invocación que ya consumió recursos.
   Focal final80PASS0FAIL0SKIP6.090s, build0warnings/errores; SQLite/CAS reopen conserva
   el dato inválido y bloquea gasto de otra sesión del mismo journal sin contaminar
   la sesión original. Guard de replay implementado y verificado con tres fixtures
   legacy sintéticas: abierto negativo/overflow falla antes del provider y un dato
   inválido de otro Turn cerrado no bloquea al nuevo. Focal83PASS6.229s, build0/0.
   Full final1926=1922PASS0FAIL4SKIPsymlink274.340s, exit0; summary/audit verificados.
   Full no incluye PersistedUsageReplayRegressionTests; sí lo incluye el focal83.
   [Contrato y evidencia](m55-budget-continuation.md#reportes-numéricos-inválidos-y-acumulación-sin-overflow).
3. Disponibilidad desde breaker y selección por RouteId implementadas (58ee5bc); full verde.
   Afinidad del arnés TUI Init/Run reproducida (11 vs6), corregida en cd727e5;
   59 pruebas TUI verdes, con login20ciclos y resize en vivo.
   No equiparar fixes de fixtures con cierre de defectos del framework o producción.
4. Steering explícito en fronteras de ModelStep y outcome de descarte.
   Contratos v1 en 6dc1332; cola FIFO, command explícito e idempotente y consumidor
   implementados. Applied+ModelStepStarted y Dropped+terminal son batches atómicos.
   Focal102PASS, incluyendo SQLite/CAS reopen y dos suspensiones sin duplicados.
   Caso de tres steps/una suspensión conserva usage21/5 y coste de fixture0.000031USD.
   [Contrato y evidencia](m55-steering.md). Full1745=1741PASS/0FAIL/4SKIPsymlink211.540s.
   Guard previo al append implementado en CanonicalStateTracker, con estados,
   relaciones de identidad, clones, rollback y replay SQLite. RED15FAIL antes del
   fix; focal final115PASS/0FAIL/0SKIP1.914s. Suite completa1771 casos/1767PASS/
   0FAIL/4SKIPsymlink211.331s (`steering-canonical-final-full.log`), exit0.
   No declarar M5.5 completo: los demás controles de esta cola siguen pendientes.
5. Source y causation real, eliminación del fallback al último evento y guards de escritor.
   Reconciliación terminal multi-Run corregida: ids/cause de origen e idempotencia
   sobre outcomes de toda la sesión, no slice cronológico incompleto. RED reproducido,
   focal52PASS/combined110PASS; [evidencia](m55-terminal-reconciliation-attribution.md).
   Publicación/respuesta a conflictos separa Run que espera y Run del efecto;
   batch atómico scoped por item, ids explícitas prioritarias, auditoría al owner original.
   Focal74PASS; full1714=1710PASS/0FAIL/4SKIP symlink204.828s.
   [Contrato/pruebas](m55-effect-resolution-scopes.md).
   Fallback global eliminado; batch con causas explícitas por item, conflictos
   independientes y recovery activo con scope de origen verificados. Commands
   inválidos/desconocidos devuelven Rejected sin eventos/rango. Focal126PASS;
   full1787=1783PASS/0FAIL/4SKIPsymlink209.548s, exit0.
   [Contrato y reproducciones](m55-explicit-causation.md).
   Guard de escritor IL implementado y verificado con controles positivos async;
   integración SQLite demuestra que deltas no llegan al journal. Focal45PASS;
   full1795=1791PASS/0FAIL/4SKIPsymlink209.877s, exit0.
   Source nullable implementado: EventStream identifica al escritor de forma fija,
   Memory/SQLite preservan metadata y legacy null, wire aditivo sin nueva versión.
   Focal32PASS/0FAIL/0SKIP1.503s; full1802=1798PASS/0FAIL/4SKIPsymlink211.288s.
   [Contrato y reproducción](m55-event-source.md). SourceKind/ComponentSource
   describen componentes registrados, no se reutilizan como origen de eventos.
   Catches excepcionales RunSim/ResumeSim corregidos: rango de eventos realmente
   persistidos, reintento parcial sin duplicar Unknown y sesión nueva sin Run ajeno.
   [Pruebas y alcance de los guards](m55-writer-and-exception-boundaries.md).
   Caída ReadFrom al construir ack cubierta: Deferred(JournalOutcomeUnavailable)
   sin rango ni inferir cero efectos. Rango de command sigue descendientes reales;
   recovery terminal con caller conserva su causa, background conserva la del origen.
   RED inicial3casos1PASS2FAIL; RED posterior LastSeq33 vs34; focal35PASS/0FAIL/0SKIP.
   [Contrato y evidencia](m55-command-causal-ranges.md). Full1806=1802PASS/0FAIL/
   4SKIPsymlink210.503s, exit0. Reproducciones posteriores: auditoría tras interacción
   persistida e inicialización parcial act/explore (RED3FAIL). Corregidas con ack
   durable y creación atómica de once eventos en Barrier, incluyendo rollback SQLite
   real y fallo de state file después del commit. Primer session.input con identidad
   retenida y batch de Session/política (RED1FAIL adicional). Focal50PASS/0FAIL/0SKIP;
   [contrato y evidencia](m55-atomic-run-admission.md). Full1814=1810PASS/0FAIL/
   4SKIPsymlink210.307s, exit0; helper/tests nuevos de RuntimeBuild no incluidos.
   Auditoría posterior: path inválido act escapaba sin ACK y selección de nuevoRun
   en mismaSession tras fallo de input exponía cache anterior. RED3FAIL reales tras
   corregir type del fixture; fixes con rechazo pre-write e invalidación por Run.
   Focal57PASS/0FAIL/0SKIP; full1826=1822PASS/0FAIL/4SKIPsymlink209.921s, exit0.
6. ToolCallStarted v3: contrato/codecs/upcasters/index envelope verificados; falta
   captura fiel previa al efecto y política de contenido sensible, sin eludir redacción.
   TargetRef solo representa claim filesystem único; no esquema universal inventado.
7. Registros durables congelados de delegación/wake, JoinPolicy, SupervisionBinding y
   ResultDisposition; sin scheduler ni joins ejecutables.
   Auditoría documental 2026-10-06: ADR0046 §2/§8 fija nombres e identidad, pero no los
   campos ni lifecycle por familia de Delegation/WakeRequest, mailbox/claim/ack/dedup,
   binding/handshake ni el enum/IDs de ResultDisposition. Arquitectura línea747 sí fija
   ExecutionJoin All/Any/Quorum/Explicit, FanIn Direct/Aggregate y default de fallo Wait;
   no especifica cómo representarlos. Spec§16 menciona EvidenceRef/PathRef sin definir
   sus shapes. OmniCoder docs/PLAN_OMNICODER_SOBRE_OMNICORE.md líneas138-148 son política
   aspiracional del supervisor, no valores del contrato Core. Se necesita autorizar
   completar el ADR con esquemas concretos (o recuperar el anexo completo); no crear
   campos, upcasters o reglas de replay que atribuyan decisiones no aceptadas.
8. Completar los componentes reales del fingerprint (build/configuración resuelta y
   tools/prompt/plan por Turn conectados; AgentProfile/skills/CAS completos pendientes); cerrar CommandOutcome
   correlacionado en todos los commands de frontera y guards de arquitectura.
   Hallazgo de auditoría: resume de Turn abierto puede conservar fingerprint A en
   TurnStarted y persistir contexto con fingerprint B de la instancia actual.
   Repro base: QuestionnaireTurnTests con reopen SQLite/CAS y cambio de config.
   Verificado: escalación autorizada por overflow abandona Turn anterior y comienza
   otro; no requiere exención del guard sameTurn. Guard aplicado antes de FollowUp /
   ModelStep / provider y recuperación con config original, sin sobrescribir eventos
   ni inventar revisión por paso. Plan inicial congelado para no rechazar mutaciones
   legítimas del propio Turn. Fixture reopen y controles de escalación/filtro/snapshot.
9. Reproducir los siete criterios de salida de ADR-0046, actualizar docs y registrar
   exactamente qué evidencia real de providers pertenece a M5, sin convertir fixtures en éxito real.

## Ownership / pruebas

Ronda 2026-10-06 18:37 UTC: persistencia M5 ya usa `UpsertWithTraits`, perfil y
traits en una sola transacción, con validación de identidad/revisión y rollback
ante error/cancelación. RED real de trigger SQLite: 3 casos = 1 PASS/2 FAIL.
Luna aportó fixtures de Host y cuatro controles de store; root implementó contrato,
store/Host y auditó/fortaleció pruebas. Focal final 125 PASS/0 FAIL/0 SKIP, 1.479s,
build 0 warnings/errores. Sin provider autenticado. [Evidencia](m5-qualification-continuation-20261006.md).
Estado de esa ronda: coste real/desconocido por probe y evidencia CAS pendientes.
El bloque posterior de uso/coste descrito arriba ya elimina cero ficticio y calcula
cotizaciones explícitas; evidencia CAS y contención monetaria previa siguen pendientes.

Ronda manual 2026-10-06 16:14 UTC: Luna gpt-6-luna HIGH concentrada en M5,
quick10/preflight de coste/hash canónico; evidencia y pendientes en
[continuación M5](m5-qualification-continuation-20261006.md).
Root implementó tres controles SQLite meta diario/reopen después de rechazar la
propuesta Nemotron por no probar lo encargado. GLM5.3 y DeepSeek4.1/NVIDIA tuvieron
TimeoutError sin entrega en cuatro minutos, sin retry ni sustitutos. Logs de esta
ronda en `C:\Users\juanc\.codex\omni-m55-workers-20261006-1558`. Nemotron/OpenRouter
sí respondió, precio cero reportado, pero eso no acredita cualificación del proyecto.
Focal conjunto 131 PASS/0 FAIL/0 SKIP, 2.077s; build 0 warnings/errores.
Full conjunto final: 1991 casos = 1987 PASS/0 FAIL/4 SKIP symlink,
101.556s; arquitectura 56 PASS/0 FAIL, 0.587s. Focales solapados, no sumar.
No modifica los pendientes M5.5 enumerados arriba ni habilita scheduler/joins M6.

Luna implementó contratos de rutas y qualification key; el integrador audita, cablea,
migra y reproduce tests. Nemotron/OpenRouter y GLM5.3/NVIDIA no entregaron código en
la ronda cerrada; fallos conservados, sin reintentos ni reemplazos pagados.
Evidencia: `C:\Users\juanc\.codex\omni-m55-three-20261006`.

- Última suite completa respuestas/scopes: 1714 casos, 1710 PASS, 0 FAIL,
  4 SKIP symlink (`effect-resolution-scoped-full.log`, 204.828 s).
  Focal74PASS se solapa; los contratos steering nuevos aún no formaban parte del build.
- Suite previa binding/reconciliación: 1706 casos, 1702 PASS, 0 FAIL,
  4 SKIP symlink (`reconciliation-binding-full-final.log`, 204.762 s).
  Combined focal110PASS, con adapter nativo Anthropic/SSE fixture y SQLite real.
- Suite previa routing/afinidad: 1696 casos, 1692 PASS, 0 FAIL,
  4 SKIP symlink (`routeid-router-affinity-full-suite.log`, 201.818 s).
  Core focal102PASS y TUI59PASS se solapan con ella. No prueba consumo autenticado.
- Suite anterior con breaker/routing: 1690 casos, 1686 PASS, 0 FAIL,
  4 SKIP symlink (`provider-circuit-full-repeat.log`, 142.008 s). Primer intento:
  1685 PASS, 1 FAIL login TextView Lazy, 4 SKIP (`provider-circuit-full-suite.log`);
  aislado 1 PASS. Fallo intermitente abierto, sin afirmar causa raíz resuelta.
- Focal breaker/router/fábrica real Host/CLI: 66 PASS, 0 FAIL, 0 SKIP;
  `provider-circuit-focal.log`. HTTP local controlado, sin consumo autenticado.
- Lifecycle BudgetExceeded/cliente/commands/proyecciones: 80 PASS,
  0 FAIL, 0 SKIP (`budget-lifecycle-tests-fixed.log`). Billing/YAML/factory: 32 PASS.
- Focal final CAS/rutas: 121 PASS.
- Focal cualificación/routing/CLI con SQLite legacy, idempotencia y aislamiento: 87 PASS,
  build 0 warnings / 0 errores (`route-qualification-tests.log`).
- Integración final tras el wiring de rutas, incluyendo CLI end-to-end y codecs ModelStep:
  118 PASS, 0 FAIL (`route-qualification-final-tests.log`).
- Checkpoint/replay: 31 PASS, 0 FAIL (`provider-state-accounting-tests.log`), incluida
  reanudación del adapter Anthropic con SSE de fixture, sin consultas autenticadas.
- Presupuesto/RunControl/configuración e integración real SQLite/OmniServer: 71 PASS,
  0 FAIL (`budget-continuation-integration-tests.log`), usage de fixture.
- Las cifras se solapan y no se suman; fixtures no acreditan consumo autenticado.

Reproducción de este bloque:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*RouteQualificationKeyTests' -class '*RouteQualificationMigrationTests' -class '*ModelQualificationKeyTests' -class '*ModelQualificationStoreTests' -class '*ModelQualificationHostTests' -class '*ModelQualificationCliTests' -class '*ModelRoutingHostTests'
```

M5.5 **no está cerrado**. M6 permanece pendiente de estos controles.

Próximas implementaciones: ToolCallStarted v3, contratos congelados de M6, presupuesto
y fingerprint; además de resolver almacenamiento/replay opaco y la auditoría final
de commands. Steering y Source/causation ya tienen evidencia focal y full propia.
Reanudación ask verificada mediante solicitud+consent User+revisión exacta Session/Run,
Turn/Lane de origen e identidad física vigente; no usa un mensaje nuevo como sustituto.
Focal routing/resume/SQLite/protocol/CLI/FollowUp: 61 PASS, 0 FAIL, 0 SKIP;
routing-resume-single-input-tests.log. TUI callback/ack/carrera con driver real: 4 PASS;
routing-resume-tui-race-tests.log. Fixtures/SSE controlado, sin gasto real.
Ledger diario entre workspaces, reservas concurrentes y reanudación automática
allow_plus también pendientes. No asumir cierre por contratos o credenciales.
