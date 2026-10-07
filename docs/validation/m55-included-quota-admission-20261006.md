# M5.5: admisión de cuota incluida

## Recibos parciales y publicación protegida — 2026-10-07 03:17 UTC

El límite de persistencia descrito en la actualización anterior ya está corregido:
cada probe admitido IncludedQuota conserva su QualificationProbeReceipt canónico
en User/user.db, antes del siguiente gate de cuota y sin depender de publicar un
perfil. ExecutionId es sólo identidad de cualificación; no se inventa Session,
Run, Turn ni AgentExecution. ReservationId asocia la invocación, no representa
una reserva monetaria. MaximumUsd=0 significa ninguna autoridad USD; CostUsd
permanece null y no acredita gratuidad ni débito cero. Se conservan la cota
de intentos, envíos observados, máscara de campos reportados y UTC.

QualificationReceiptWriter reutiliza la misma construcción CAS/header tanto para
contabilidad monetaria como para IncludedQuota. Las cifras inválidas quedan sólo
como evidencia CAS redactada; el recibo no las transforma en agregado válido.
Los contadores no reportados son null en JSON/SQL, no ceros medidos. Cancelación
no descarta uso ya reportado. Una cota de envíos contradicha se registra antes de
rechazar otra llamada; un fallo de escritura propaga sin publicar éxito.

Luna detectó una carrera CAS-publicación/root con GC de gracia cero. Root añadió
PublishProbeReceipt al store: callback síncrono usando el artifact store suministrado,
misma AcquirePublicationLease existente desde la publicación hasta el commit FULL,
validación de CAS/header intacta, sin adquisición anidada. RecordProbeReceipt para
artifacts ya publicados conserva compatibilidad. La excepción libera la exclusión,
deja sólo un orphan y nunca un recibo falso. No se añade scheduler ni joins.

RED uso parcial: 15 casos / 14 PASS / 1 FAIL (colección vacía al reabrir).
RED exclusión: 1 caso / 1 FAIL (lease ya libre después de PutText).
Final cualificación: 369 PASS / 0 FAIL / 0 SKIP, 16.789s; build 0/0,
included-quota-receipt-verified.log. Incluye reapertura, idempotencia read/write,
rechazo del segundo probe, cancelación, counters inválidos, exceso de envíos,
máscara input-only, GC cero-gracia y trigger SQLite de fallo de commit.
ArchitectureTests final: 56 PASS / 0 FAIL / 0 SKIP; log
receipt-publication-architecture-final.log. Los focales anteriores se solapan.
Todos son fixtures offline y archivos/bases privados; no prueba autenticación,
unidades reales de cuota ni gasto. Suite completa del nuevo bloque aún pendiente.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*Qualification*'
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore -v quiet
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll -noLogo -parallelMode none
```

## Cualificación: actualización 2026-10-07 03:10 UTC

ModelQualificationHost utiliza QualificationIncludedQuotaAdmission antes de
cada probe IncludedQuota. QualificationOptions.QueryQuota consulta el ID del
provider configurado; ConfirmLowQuota autoriza sólo ese probe y no sustituye
ConsentGiven. Se reutiliza el umbral de runtime: ventana reportada no vencida
con remanente [0,10). Identidad ajena y errores de consulta abortan sin enviar.
Unknown no significa cero ni disponibilidad verificada. Sin adaptador reconocido
se conserva Unknown con limitación. Un provider inyectado sin QueryQuota nunca
consulta la cuenta real del desarrollador.

El CLI muestra proveedor, cuenta, fuente, fecha, disponibilidad (incluida Stale),
ventanas y reinicios antes del consentimiento separado. No interactivo deniega;
--yes no concede esta aprobación. La consulta por defecto reutiliza el adaptador
de suscripción existente y el login existente, sin nuevas claves, resets o cargos.
Cancelación después de admisión produce observación Cancelled sin entrar al
provider; no convierte cero envíos observados en coste cero conocido.

Auditoría Luna de sólo lectura detectó fecha antigua presentada como actual y
entrada al provider tras cancelación. Root reprodujo 3 fallos entre 25 casos,
implementó ambas correcciones y obtuvo 45 PASS / 0 FAIL / 0 SKIP, 8.174s,
build 0 warnings / 0 errores. Logs quota-audit-red.log / quota-audit-final.log
en el directorio de evidencias citado abajo. Suite completa 8f837b1 verde
2484=2480 PASS/4 SKIP antecede este bloque; no se atribuye a cambios nuevos.

Límite pendiente explícito: CompletedAsync de este observer todavía no persiste
un recibo durable de uso parcial si una llamada posterior es rechazada. No se
inventan recibos monetarios, USD cero ni Session/Run/Turn para ocultarlo.
Todos los snapshots de estas pruebas son fixtures offline; no acreditan consultas
autenticadas, consumo real ni cierre global M5.5.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*QualificationQuotaPromptTests' -class '*QualificationProbeExecutionBoundaryTests' -class '*QualificationIncludedQuotaTests' -class '*M5QualificationCodexSubscriptionIntegrationTests' -class '*QualificationCanonicalDailyIntegrationTests' -class '*QualificationCrossWorkspaceDailyTests' -class '*QualificationAttemptBoundTests' -class '*IncludedQuotaAdmissionTests'
```

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
