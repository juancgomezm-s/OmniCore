# Punto de control de implementación M6 — 2026-10-08

Este documento registra el estado de la implementación M6 antes de la validación
integral. **No declara cerrado el hito.** Quedan pendientes el build final y la
suite completa con pruebas de arquitectura, además de la regresión M4 serial, que
ejecutará la revisión raíz sobre el estado congelado.

**Cierre posterior:** la revisión raíz completó y reconoció el cierre de M6
sobre `0279677` después de corregir regresiones integrales. Este documento
conserva el checkpoint anterior; el resultado vigente, hashes, logs y límites
están en [el acta integral](m6-integration-20261008.md#cierre-de-m6-en-windows).

La integración cubre scheduling paralelo de lectores y lease exclusivo de
escritoras dentro de un Host y un store compartido; admisión durable de presupuesto
Run/Task con reservas y accounting de recibos; FanOut/FanIn con evaluación
determinista y recuperación; supervisión en proceso por `IOmniClient`, bindings,
política Wait, mailbox, ACK y Wake ligados a receipts; y comandos de workflow
tipados que recorren Explore → Implement → Verify por el dispatcher CLI/TUI, Plan,
dispositions y completion gate. El workflow verifica wiring mediante checks
declarados ejecutados como procesos reales en un workspace temporal. Cuando la
evidencia falla, la etapa solicita Rework; cuando un receipt aceptado queda obsoleto
antes del cierre, se conserva como hecho histórico y se registra deuda durable
observable sin completar el Plan.

La UI admite workers de delegación en background, conserva el composer disponible,
permite cancelarlos por separado y presenta capacidad, esperas y heartbeats
transientes. `/agents` y el inspector ofrecen transcript por Lane con atribución de
Run/Task/Lane/Execution/Turn/ToolCall y verificación de la referencia CAS. Al
reabrir, eventos replayados no acreditan liveness: una ejecución sin receipt se
conserva como incierta, no se reenvía al provider ni se muestra como aceptación
actual. El test de runtime cancela un lector sólo después de liquidar el receipt
del lector hermano; la regresión de fallo aislada verifica que un hijo que no tiene
su propio ModelStep abierto puede cerrarse aunque otro hijo mantenga un paso abierto.

La prueba de workflow usa provider HTTP scripted detrás del adapter de producción y
ejecuta herramientas de filesystem y un proceso `dotnet` real en directorios
temporales; no realiza llamadas externas autenticadas. El fixture no acredita el
sandbox fuerte de producción. La cobertura de leases corresponde a un solo Host y
store, no a varios procesos/Hosts. La integración mantiene los límites y permisos
existentes y no adelanta TaskPacket/worktrees, detección semántica por lenguaje,
políticas de riesgo, Memory o coordinación multiproceso.

## Evidencia focal reproducible

Los logs están fuera del repositorio, bajo `C:\Users\juanc\.codex\`.

| Foco | Resultado |
| --- | --- |
| Workflow real: positivo, módulo sin conectar y mutación tras Verify | 3/3 PASS, 18.362 s — `m6-workflow-final-20261008.log` |
| Transcript del inspector, CAS corrupto y ejecución ambigua | 3/3 PASS, 0.761 s — `m6-transcript-final-20261008.log` |
| Observabilidad, liveness al reabrir y polling | 28/28 PASS, 0.679 s — `m6-observability-final-20261008.log` |
| Envelope del protocolo | 8/8 PASS, 0.716 s — `m6-protocol-mapper-final-20261008.log` |
| Workers TUI en background y cancelación individual | 1/1 PASS, 10.794 s — `m6-tui-background-final-20261008.log` |
| Sidebar TUI con plan, contexto y uso | 2/2 PASS, 4.923 s — `m6-tui-sidebar-final-20261008.log` |
| Dos delegaciones `OmniCliRuntime` simultáneas, mismo Host/store y root writer deferred | 1/1 PASS, 1.782 s — `m6-runtime-parallel-final2-20261008.log` |
| Aislamiento de fallo/cancelación frente a ModelStep abierto de sibling | 1/1 PASS, 1.007 s — `m6-owned-failure-focal-20261008.log` |
| Ciclo completo de delegaciones y regresiones de lifecycle | 23/23 PASS, 9.730 s — `m6-delegation-lifecycle-final-20261008.log` |

También pasó `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore`
con 0 warnings y 0 errors antes de los últimos focos. La verificación completa
posterior al checkpoint sigue siendo necesaria para reconocer M6 como cerrado.
