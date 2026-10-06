# Objetivo activo: llegar a M6

Actualizado 2026-10-06 19:54 UTC / 13:54 America/Mexico_City.
Objetivo autorizado: cerrar M5 y M5.5 con implementación y evidencia reproducible,
conforme a ADR-0007/0044/0046/0047, y dejar M6 listo para empezar.
No se implementan scheduler/joins M6 ni restore físico M7 antes de cerrar sus fronteras.

## Instrucciones vigentes del objetivo

El usuario pidió actualizar el objetivo y reanudarlo el 2026-10-06. `get_goal`
confirmó `active` a las 18:29 UTC: la pausa de producto ya fue revocada. El texto
del objetivo conserva M5.5/M6; estas instrucciones vigentes incluyen también el
cierre de M5 y el reparto de agentes solicitado. La automatización histórica sigue
pausada; no se crea otro objetivo ni se declara terminado el actual por un subconjunto.

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
6. ToolCallStarted v3: Reversibility/TargetRef/BeforeStateRef, codecs/upcasters y evidencia.
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
