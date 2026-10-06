# M5.5: admisión de cuota incluida

## Regla y contratos reutilizados

ADR0037 §7 exige Ask cuando una ventana reportada deja menos del 10%.
Se reutilizan ProviderQuotaSnapshot/ProviderUsageWindow, SessionObservationHub,
InteractionRequested/Resolved/Expired y BudgetExceeded. No se crean ventanas,
precios, saldos ni una nueva autorización monetaria.

El Host consulta la cuota antes de materializar contexto y antes de cada llamada
primaria o compactación con meta-modelo. La consulta por defecto reutiliza
SubscriptionQuotaService; el perfil codex declarado resuelve el origen ChatGPT
sin depender del alias del provider. El snapshot conserva el ProviderId configurado.
Claude usa el adaptador existente para el origen `claude`; otras suscripciones sin
adaptador conservan Unknown y su limitación. No se infiere un origen desde nombres
de modelos, endpoints o credenciales. Los secretos y consultas no llegan al renderer.

Una ventana Reported con RemainingPercent en [0,10), no vencida, exige interacción.
Stale conserva la última ventana reportada, sin convertir incertidumbre en cero.
Unknown, datos de otra sesión/provider y ventanas vencidas no se presentan como
un remanente actual conocido. Cada sesión consulta su origen; no se copia una
medición ajena para atribuirla a la sesión seleccionada.

SubjectJson lleva includedQuotaConsent=1, provider/model, cuenta, fuente, fecha,
disponibilidad, ventanas/limitId/reset/remanente y TurnId/stepIndex. La aprobación
`allow_quota` requiere causa User y solo autoriza esa invocación en el mismo
Session/Run/Lane/Turn. No habilita allow_plus ni altera SessionRoutingPolicy o
topes USD. NoClient usa la denegación/lifecycle existentes. Reconsultar no duplica
solicitudes pendientes ni consumo; cambiar cuenta/fuente/ventana/reset/remanente
requiere nueva aprobación. Una fecha de consulta nueva con los mismos datos no
invalida perpetuamente el consentimiento: las fechas permanecen como evidencia,
pero no son el contenido de la autorización. Comparación JSON semántica evita
fallos por normalización/escape al persistir en SQLite.

ResumeQuotaAsync exige respuesta durable del usuario, origen del Run/Lane/Turn
activo y ausencia del ModelStepStarted ya consumido. Revalida cuota y fingerprint;
no somete un mensaje nuevo ni etiqueta la acción como ModelRouteConsent. La TUI
reanuda después de responder, incluso si el callback llega antes del checkpoint
de la tarea originaria. La compactación bajo cuota baja usa el fallback determinista
existente: el consentimiento de una llamada primaria no autoriza llamadas meta extra.

## Evidencia y límites

RED original: 7 casos / 6 PASS / 1 FAIL, 4.169s. Con 9% quedaban dos llamadas
en vez de una; la frontera 10% pasaba. Dos builds iniciales erróneos del fixture
no acreditan RED productivo. La prueba ampliada descubrió un segundo fallo de
reanudación por comparar JSON con escapes distintos; se corrigió sin quitar la
assertion de llamada efectiva.

Build final 0 warnings / 0 errores. Focal final Core: 78 PASS / 0 FAIL / 0 SKIP,
13.579s. Incluye consulta normal inyectada (sin SetQuota manual en la sesión propia),
9/10%, Unknown/Stale, expiración, otra sesión/provider, aprobación/reanudación,
reconsulta con fecha nueva, cambio 9→8%, pendiente idempotente, rechazo allow_plus,
NoClient y regresiones presupuestarias/meta/observabilidad/arquitectura. Dos controles
fuerzan compactación real: cero llamadas meta cuando el callback deniega y una
cuando permite; ambos conservan checkpoint y llamada primaria, sin desactivar fallback.
TUI real con driver de prueba: 1 PASS / 0 FAIL, 2.587s; overlay localizado y callback
de cuota, sin nuevo input ni revisión monetaria. La ronda anterior de Core+TUI
tuvo 98 PASS, 97.324s; cifras solapadas, no sumar.

Todos los remanentes/cuentas/HTTP de este bloque son fixtures offline.
SQLite/CAS, TuiTurnHost, RunControl y driver TUI son implementaciones reales.
No acredita consulta autenticada ni consumo real de ChatGPT/Claude. La funcionalidad
del adaptador autenticado existente no se recualificó en este bloque. No declara
M5.5 cerrado: almacenamiento opaco/reasoning, reservas concurrentes, contratos
congelados M6 y auditoría integral siguen pendientes.

Luna revisó solo lectura y detectó la consulta no cableada y el origen erróneo de
resume; root corrigió ambos y reprodujo los controles. No existe una declaración
de origen de cuota para cualquier alias de Claude Code: el adaptador existente
reconoce `claude`; no se declara soporte para otros aliases ni se inventa un perfil.
Cuando una nueva sesión no obtiene cuenta/medición, no se puede demostrar que sea
la cuenta de otra sesión: no se copia esa evidencia como propia. Unknown permanece
explícito. Estas limitaciones requieren consideración en la auditoría integral.

Logs en C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/:
included-quota-red-build.log, included-quota-red-test.log,
included-quota-build.log, included-quota-final-core.log,
included-quota-final-tui.log, included-quota-focal.log.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*CrossWorkspaceDailyCapRegressionTests' -class '*SessionObservabilityTests' -class '*BudgetContinuation*' -class '*BudgetDenial*' -class '*CanonicalWriterArchitectureTests' -class '*MetaModelSpendRegressionTests' -class '*MetaModelDailySpendIntegrationTests'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -method '*Quota_consent_resumes_without_new_input_or_monetary_policy_revision'
```
