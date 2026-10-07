# M5: cuota real y recorrido OmniCoder — 2026-10-07

Estado: evidencia parcial real de consulta de cuenta; no aceptación de generación,
cualificación de modelos ni cierre completo M5/M5.5.

## Ejecución

Build de todo el grafo de `OmniCoderMetricsProbe`, incluyendo el proyecto real
OmniCoder.OmniCore net8/WPF, con outputs privados y Core de la rama autorizada:
exit0, cero errores/advertencias, 23.78 s. No se modificaron fuentes de OmniCoder.

`m5-quota-real-1244.log` terminó exit0. Usa `SubscriptionQuotaService` de producción:
Codex app-server `account/read` con `refreshToken:false` y
`account/rateLimits/read`; Claude Code `/usage`. Sin prompt de modelo,
sin inferencia, cambio de cuenta, login ni renovación explícita de credenciales.
El servicio administra sólo los procesos CLI que inicia para esta consulta.

El resultado pasó por Host → query de IOmniClient → protocolo JSON →
SessionMetricsViewModel real. Los controles comprobaron que ambas respuestas
llegan a la sesión del probe y siguen visibles sin una medición de contexto.
Esta ejecución no abrió/revisó visualmente toda la aplicación ni verificó
una status line durante consumo real.

## Datos reportados y limitaciones

| Fuente | Fecha de consulta UTC | Resultado |
|---|---|---|
| Codex app-server | 2026-10-07T12:43:15.2272012Z | `codex:primary`, duración 10080 minutos; 20% utilizado, 80% restante; reinicio 2026-10-14T03:29:28Z. |
| Codex créditos | misma consulta | Saldo de cuenta reportado: 0 créditos. No es límite de una clave ni precio de generación; moneda no publicada. |
| Claude Code `/usage` | 2026-10-07T12:43:22.7966527Z | Unknown; el recorrido CLI/parser no produjo ventanas utilizables ni identidad de cuenta. No acredita que el proveedor no tenga límites. |

Codex no publicó ventana de cinco horas en esta respuesta. No se inventó una
segunda ventana. La cuenta se identifica mediante fingerprint, no email ni token.
La sesión SQLite privada del probe no hizo invocaciones: sus contadores vacíos
no son mediciones de consumo del modelo, ni evidencia de coste real cero.

El PASS de transporte para Claude sólo demuestra que el estado Unknown llega
al cliente; **no acredita una consulta autenticada de cuotas exitosa de Claude**.
El porcentaje restante de Codex es derivado como `100 - usedPercent`, con fuente
explícita; utilizado, duración y reinicio proceden de la respuesta real.

## Reproducción

Desde el worktree autorizado, con sesión y CLI ya existentes:

```powershell
dotnet build tools/OmniCoderMetricsProbe/OmniCoderMetricsProbe.csproj -p:UseArtifactsOutput=true -p:ArtifactsPath=C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/metrics-artifacts -p:OmniCoreRoot=C:/Users/juanc/.codex/worktrees/m55-artifactrefs-roundtrip/OmniCore
dotnet C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/metrics-artifacts/bin/OmniCoderMetricsProbe/debug/OmniCoderMetricsProbe.dll --accounts
```

Logs preservados en `C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/`:
`m5-quota-real-build-1243.log` y `m5-quota-real-1244.log`.
SQLite/audit de esta sesión en
`C:/Users/juanc/AppData/Local/Temp/omni-observability-probe-5632a896bc81464383747c68958dddbe`.
Reconsultar produce otra fecha y puede producir otra cuota; no reutilizar este
snapshot como medición actual futura.

Quick 1.1.1 real en Sol/Luna permanece pendiente de consentimiento específico
y de declarar el modo real de facturación en la configuración User, hoy Unknown.
No se suprimió ese gate ni se modificaron YAML, credenciales, servidores o TLS.
