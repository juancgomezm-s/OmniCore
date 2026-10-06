# Objetivo activo: llegar a M6

Actualizado 2026-10-06 08:43 UTC / 02:43 America/Mexico_City.
Objetivo autorizado: cerrar M5.5 conforme a ADR-0046 y dejar M6 listo para empezar.
No se implementan scheduler/joins M6 ni restore físico M7 antes de cerrar sus fronteras.

Rama: `codex/omnicore-consolidation-20261004`, worktree `m55-artifactrefs-roundtrip`.
Main, cambios ajenos, credenciales y procesos no propios se preservan; no push.
M4 Windows ya tiene evidencia de cierre; esta ronda no la reemplaza.

## Avance verificado

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
El build global explicable del fingerprint y los demás componentes reales aún están pendientes.

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
3. Disponibilidad desde breaker y selección por RouteId implementadas (58ee5bc); full verde.
   Afinidad del arnés TUI Init/Run reproducida (11 vs6), corregida en cd727e5;
   59 pruebas TUI verdes, con login20ciclos y resize en vivo.
   No equiparar fixes de fixtures con cierre de defectos del framework o producción.
4. Steering explícito en fronteras de ModelStep y outcome de descarte.
5. Source y causation real, eliminación del fallback al último evento y guards de escritor.
   Reconciliación terminal multi-Run corregida: ids/cause de origen e idempotencia
   sobre outcomes de toda la sesión, no slice cronológico incompleto. RED reproducido,
   focal52PASS/combined110PASS; [evidencia](m55-terminal-reconciliation-attribution.md).
   Fallback global EventStream y scopes de interacción por conflicto siguen pendientes.
6. ToolCallStarted v3: Reversibility/TargetRef/BeforeStateRef, codecs/upcasters y evidencia.
7. Registros durables congelados de delegación/wake, JoinPolicy, SupervisionBinding y
   ResultDisposition; sin scheduler ni joins ejecutables.
8. Poblar los componentes reales del fingerprint y build real; cerrar CommandOutcome
   correlacionado en todos los commands de frontera y guards de arquitectura.
9. Reproducir los siete criterios de salida de ADR-0046, actualizar docs y registrar
   exactamente qué evidencia real de providers pertenece a M5, sin convertir fixtures en éxito real.

## Ownership / pruebas

Luna implementó contratos de rutas y qualification key; el integrador audita, cablea,
migra y reproduce tests. Nemotron/OpenRouter y GLM5.3/NVIDIA no entregaron código en
la ronda cerrada; fallos conservados, sin reintentos ni reemplazos pagados.
Evidencia: `C:\Users\juanc\.codex\omni-m55-three-20261006`.

- Última suite completa binding/reconciliación: 1706 casos, 1702 PASS, 0 FAIL,
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

Próxima implementación tras verificación RouteId: steering y Source/causation en fronteras,
además de resolver el contrato de almacenamiento/replay opaco señalado arriba.
Reanudación ask verificada mediante solicitud+consent User+revisión exacta Session/Run,
Turn/Lane de origen e identidad física vigente; no usa un mensaje nuevo como sustituto.
Focal routing/resume/SQLite/protocol/CLI/FollowUp: 61 PASS, 0 FAIL, 0 SKIP;
routing-resume-single-input-tests.log. TUI callback/ack/carrera con driver real: 4 PASS;
routing-resume-tui-race-tests.log. Fixtures/SSE controlado, sin gasto real.
Ledger diario entre workspaces, reservas concurrentes y reanudación automática
allow_plus también pendientes. No asumir cierre por contratos o credenciales.
