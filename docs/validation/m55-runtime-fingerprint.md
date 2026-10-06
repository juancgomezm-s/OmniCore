# Fingerprint de hechos efectivos del runtime (bloque parcial M5.5)

La CLI de producción construye el fingerprint con `RuntimeFingerprintFactory` después
de resolver la ruta, el perfil y el harness. La identidad del build es versión/MVID del
assembly Host; la cualificación conserva exactamente su formato previo. El componente
`runtime.build` incluye además el nombre y la versión informativa reportados por el assembly.
No interpreta una versión informativa como un commit confirmado.

Componentes versionados, JSON escrito explícitamente y SHA-256 (v1 salvo donde se indica):

- `model.descriptor`: descriptor real y selección, incluidos modelo lógico, RouteId,
  presupuesto, modo de herramientas y solicitud de razonamiento.
- `model.profile` v2: perfil efectivo, formatos/modalidades y traits ordenados por nombre,
  más qualificationKeyHash, qualificationRevision y qualificationState del MISMO
  snapshot que aportó los traits. Sin evidencia utilizable, los tres campos son null;
  no se interpreta como una medición de cero ni como prueba de que no existe otro perfil.
  La revisión del estado es ProfileRevision (el contador que incrementa también MarkStale),
  no un segundo contador inventado. La clave completa/CAS explicable sigue pendiente.
- `model.harness`: valores efectivos de la política resuelta.
- `context.policy`: política de materialización, presupuesto y tokenizer utilizado.
- `provider.adapter` v2: digest de la ruta física canónica y del tipo concreto/versión-MVID
  del assembly de la instancia IModelProvider ya conectada. La CLI pasa esa instancia;
  no deduce el adapter a partir de la familia ni crea una segunda conexión.
  Sin instancia, providerType/providerBuild son null explícitos, no una identidad inventada.
  Un wrapper futuro identificaría su tipo exterior; no se inspeccionan campos privados
  ni se afirma identificar un adapter interior. No se serializa el objeto del provider.
- `runtime.build`: metadatos reales del assembly.
- `tools.plan` v2: las herramientas realmente visibles tras el filtro del harness/boundary,
  en su orden efectivo; nombres → ToolId, hash del schema, descripción, Source completo,
  versión declarada de Source y flags/riesgo/protección/efecto/tags del descriptor.
  Incluye ahora InputSchemaJson completo además de su hash.
- `prompt.template` v2: ID `ExplorerTurn.SystemPrompt`, texto y hash del prompt efectivo renderizado
  y redactado, no una etiqueta M2/M3 ni el texto del usuario.
- `plan.revision`: PlanId y revisión inicial del Turn; ambos null cuando no hay plan.

La CLI persiste los cinco componentes resueltos (build, descriptor, perfil, harness,
contexto) y ExplorerTurn los tres componentes por Turn (tools, prompt, revisión) en
el CAS de texto existente ANTES del evento que los referencia. El blob se verifica
contra hash/size y `Content.Hash == Hash`; múltiples escrituras deduplican el blob,
aunque cada receipt tenga un ArtifactId distinto. El agregado no incluye ArtifactId.
`provider.adapter` conserva `Content=null`: no se copia su endpoint privado a CAS.
No se serializa el objeto IModelProvider ni se desactiva el redactor ADR0018.
Si el store marca una representación como Redacted, su ref NO se presenta como la
configuración exacta: se conserva el hash de la configuración y Content=null.
El texto redactado puede quedar como blob huérfano elegible para GC; no se referencia
desde el fingerprint. Una ref declarada exacta con hash distinto o Verify=false
produce InvalidDataException antes del Turn/provider. Los fallos I/O se propagan.
Esto no acredita contenido explicable para un componente privado/redactado.
GC distingue estos digests de las referencias CAS;
si un componente sí contiene `Content`, sigue transitivamente esa referencia y falla
sin barrer cuando el blob falta. La retención conservadora de hashes del payload permanece.

Los fingerprints legacy sin componentes conservan su hash; las simulaciones no se
presentan como configuración real del runtime. Este bloque **no cierra** el criterio
completo: faltan AgentProfile/skills efectivos y la evidencia de cualificación
explicable en CAS, así como otros componentes de ADR-0017 donde estén configurados. La CLI
ya reemplaza `core-tools-1` por el digest del plan de herramientas visible. La versión
de Source es la declarada por el descriptor, no una versión de tool inventada.
Los digests tampoco acreditan contenido CAS explicable cuando `Content` es null.
La clave completa de cualificación, contributor/version, AgentProfile y demás
componentes aplicables de ADR0017 siguen pendientes. No se cambia el contrato de
opaque ProviderState ni se simula soporte declarado de reasoning.

### Persistencia CAS y política efectiva — 2026-10-06

- effective-policy-build.log: fallo de import PathBoundaryValidator; añadido
  OmniCore.Execution. effective-policy-fixed-build.log sin warnings/errores.
- effective-policy-focal.log: 10 PASS/0 FAIL/0 SKIP, 0.763s antes del bloque CAS.
  ExplorerTurn real, HostTools/boundary y respuestas scripted: ObserveOnly vs PatchOnly
  cambia tools realmente enviadas y fingerprint durable; repetición conserva hash
  pese a Session/Run/Turn distintos. No AgentProfile/overrides inventados.
- fingerprint-content-red-build.log: fixture intentaba using SqliteEventStore,
  que expone Close, no IDisposable; corregido con Close y pool privado antes cleanup.
- fingerprint-content-red-fixed-build.log sin warnings/errores; red-test.log:
  1 caso/1 FAIL/0 SKIP0.608s, TurnStarted carecía de Content en sus tres componentes.
- fingerprint-content-focal.log: 84 casos/79 PASS/5 FAIL0SKIP7.518s. Cuatro CLI
  esperaban el contrato anterior sin Content; la integración policy comparaba
  ArtifactId entre receipts distintos del mismo hash. Se actualizan assertions al
  contrato CAS: igualdad EXACTA de nombre/versión/hash más ref/hash/size/Verify,
  no se excluye ni tolera diferencia de configuración.
- fingerprint-content-final-build.log sin warnings/errores.
- fingerprint-content-final-focal.log: 84 PASS/0 FAIL/0 SKIP7.565s. Reopen real
  SQLite/CAS con refs del envelope; schemas/texto recuperables, deduplicación física,
  hash estable con/sin ref, ruta privada ausente, redactor fixture sin fuga, rechazo
  de refs exactas inválidas, CLI HTTP loopback y controles existentes de resume/GC.
- fingerprint-content-full.log: 1970 casos = 1966 PASS/0 FAIL/4 SKIP por permisos
  symlink, 267.460s, runner exit0. Incluye los seis casos nuevos CAS/policy; no incluye
  FingerprintContentAppendFailureTests entregado después del build, todavía sin ejecutar.
- fingerprint-content-architecture-build.log: build sin warnings/errores;
  fingerprint-content-architecture-test.log: 56 PASS/0 FAIL/0 SKIP0.586s.
  Suite separada, no se suma a los 1970 casos funcionales.

Los tests de pricing/provider/secret son fixtures offline o HTTP loopback controlado;
no son consultas autenticadas ni cualificación. tools.plan/prompt.template v2 cambian
el agregado: el guard de drift existente sigue rechazando Turns abiertos que tengan
fingerprint anterior no-null. No se reescribe journal ni se promete migración transparente.
La mera adición de Content no cambia el agregado. Legacy sin fingerprint mantiene
su comportamiento previo, no se presenta como validación de configuración.

## Reanudación y estabilidad por Turn

La CLI activa la composición por Turn en Explorer y Act, incluido el paso de Plan a Act.
Se construye una sola configuración para TurnStarted y los snapshots de todos sus pasos.
Al reanudar un Turn abierto, la revisión del plan se calcula hasta la secuencia de su
TurnStarted: mutaciones durables del propio Turn no alteran su configuración inicial.
El estado actual del plan sigue entrando en el WorkingState; no se revierte el plan.

Antes de encolar un FollowUp, emitir ModelStep o llamar al proveedor, se compara el
fingerprint efectivo contra el original cuando éste existe. Un cambio incompatible
devuelve Error explícito y no escribe eventos ni terminaliza el Turn. Restaurar su
configuración original permite continuar con la respuesta ya persistida. Un Turn legacy
sin fingerprint conserva el comportamiento anterior; no acredita validación de configuración.

La escalación consentida por overflow no necesita bypass: el Turn original fue Abandoned
y el target comienza otro Turn, relacionado por los eventos de escalación existentes.
Un resume que desborda contexto abandona el Turn una vez, sin duplicar TurnStarted.
No se sobrescriben eventos y no se introduce scheduler ni revisión de config por ModelStep.

## Evidencia reproducible

### Snapshot de cualificación y ruta efectiva

UsableSnapshot obtiene el perfil una vez y lee traits por esa revisión exacta. Sólo
Qualified/Calibrated/Stale con traits aportan evidencia empírica, como antes; UsableTraits
público conserva su copia mutable. El snapshot lleva una copia read-only; la CLI aplica
sus traits y pasa la misma identidad al factory, sin un segundo Get. Si el store falla,
se conserva el fallback heurístico y metadata null; cancelación no se convierte en fallback.
No se modifica la semántica de QualificationKey ni se ejecuta una suite de cualificación.

El lookup recibe el endpoint efectivo elegido por el turno. RouteFor ya considera
OMNI_BASE_URL cuando falta override explícito; no se atribuye aquí un fallo previo de
ese caso. Pasar route.Endpoint evita depender del entorno al usar una ruta restaurada.
Los controles CLI siembran un perfil de fixture en User SQLite, cierran/reabren el store
y comprueban el componente persistido en TurnStarted a través del HTTP loopback real:
ruta normal, override con clave coincidente y override con clave ajena no aplicada.

- `qualification-snapshot-build.log`: fallo de compilación del test por import Models
  ausente; corregido sin cambiar la API pública ni las assertions.
- `qualification-snapshot-focal.log`: 58 casos/57 PASS/1 FAIL; fixture de clave ajena
  había usado el override del entorno al construir la clave. Se corrigió el endpoint
  explícito de la clave sembrada, no la assertion de identidad del componente.
- `qualification-fingerprint-red-test.log`: 4 casos/4 FAIL, 0.156s, con representación
  anterior de model.profile recompilada. Metadatos ausentes no distinguen key/rev/state.
- `qualification-final-focal.log`: 88 PASS/0 FAIL/0 SKIP, 6.057s.
  Los tests de factory usan metadata sintética declarada; no prueban consumo ni auth.
- `qualification-final-all-focal.log`: 89 casos/88 PASS/1 FAIL, 6.174s. El control
  adicional SQLite pasó las assertions pero falló al borrar user.db retenido por pooling.
  Cleanup corregido liberando sólo el pool de su base privada, nunca ClearAllPools.
  El binario de este resultado no incluye aún esa corrección de cleanup.
- `qualification-full.log`: 1949 casos, 1 FAIL/4 SKIP, 266.984s, exit1; corresponde
  al binario anterior al fix de cleanup, no acredita el cambio final.
- `qualification-cleanup-fixed-build.log`: 0 warnings/0 errores.
- `qualification-cleanup-fixed-focal.log`: 89 PASS/0 FAIL/0 SKIP, 6.141s; incluye
  la lectura SQLite de rev2, conservación de traits de rev1 y rechazo de otro endpoint.
- `qualification-cleanup-fixed-full.log`: 1949 casos = 1945 PASS/0 FAIL/4 SKIP
  por permisos symlink, 266.837s, exit0. Incluye este bloque completo; no incluye las
  seis regresiones monetarias entregadas después de la compilación de este binario.

La representación v2 cambia el fingerprint; el guard existente de drift se mantiene.
Legacy sin fingerprint no se presenta como configuración validada. No se cierra M5.5.

### Identidad del adapter real

- `provider-identity-red-build.log`: build sin warnings/errores con la representación
  anterior de provider.adapter y las cuatro pruebas nuevas.
- `provider-identity-red-test.log`: 9 casos = 5 PASS/4 FAIL, 0.182s; la ruta sola no
  distinguía tipos de adapter ni instancia ausente de conocida.
- `provider-identity-focal.log`: 103 casos = 102 PASS/1 FAIL; el control CLI existente
  esperaba el digest v1. Se actualizó a comprobar exactamente metadata v2 del adapter
  OpenAiChatCompatibleProvider real, incluyendo su assembly Models, sin omitir el hash.
- `provider-identity-final-build.log`: 0 warnings/0 errores.
- `provider-identity-final-focal.log`: 103 PASS/0 FAIL/0 SKIP, 7.614s.
  Incluye la CLI real con HTTP loopback, journal/codec, GC y los tres controles de replay
  legacy posteriores a la última suite completa. No acredita una consulta autenticada.
- `provider-identity-full.log`: 1933 casos = 1929 PASS/0 FAIL/4 SKIP por permisos
  symlink, 268.052s, exit0. Incluye los tres tests de replay legacy de599a22d y cuatro
  nuevos tests del adapter. No incluye el paquete posterior de snapshot de cualificación.

La nueva representación cambia el fingerprint; un Turn abierto con metadata anterior
  y fingerprint no-null se rechaza por el guard de drift existente. No se reescribe su
  TurnStarted ni se simula compatibilidad. Legacy sin fingerprint conserva su tratamiento.
La cualificación y sus claves no cambian en este bloque. CAS explicable y revisión
  de cualificación siguen pendientes; este avance no completa ADR-0017 ni cierra M5.5.

### Protección de GC ante JSON ajeno

La excepción para `FingerprintComponent.Hash` requiere la forma completa conocida
de `ExecutionFingerprint`, campos escalares con sus tipos y componentes con nombre
único, versión y SHA-256 válidos. `modelKey` por sí solo no identifica un fingerprint:
JSON de tools/modelos también puede usar `components/hash` como referencias CAS.
Formas incompletas, mal tipadas o duplicadas mantienen traversal conservador. `Content`
se sigue recorriendo; si falta un blob referenciado, el mark aborta antes del sweep.
La detección es estructural sobre el contrato conocido, no autenticación del JSON.

- `gc-shape-red-test.log`: 2 casos/2 FAIL; borrado de una referencia transitiva y
  ausencia de aborto ante un blob obligatorio que faltaba.
- `gc-shape-final-build.log`: 0 warnings/0 errores.
- `gc-shape-final-focal.log`: 44 PASS/0 FAIL/0 SKIP, 3.752s. Incluye referencias
  desde journal y desde artifacts anidados, formas malformadas/duplicadas y controles
  previos de fingerprint hash-only/Content. SQLite/CAS reales, payloads de fixture.
- `gc-shape-full.log`: 1843 casos = 1839 PASS/0 FAIL/4 SKIP por permisos symlink,
  213.513s, exit0. No incluye el paquete posterior de regresiones de presupuesto.

Reproducción focal: el mismo runner de abajo con `-class '*MaintenanceTests'`
`-class '*RuntimeFingerprintFactoryTests' -class '*CliEndToEndTests'`.

Directorio: `C:\Users\juanc\.codex\omni-m55-three-20261006`.

- `runtime-fingerprint-focal.log`: 58 casos, 57 PASS/1 FAIL; CLI+GC descubre la
  clasificación errónea de un digest como blob obligatorio.
- `fingerprint-gc-red-test.log`: 3 casos, 2 PASS/1 FAIL; repro específico hash-only,
  controles de Content existente y Content ausente.
- `runtime-fingerprint-gc-fixed-build.log`: compilación sin warnings/errores.
- `runtime-fingerprint-gc-fixed-focal.log`: 86 PASS/0 FAIL/0 SKIP, incluyendo CLI,
  fingerprint, cualificación, GC y checkpoint durable del proveedor.
- `runtime-fingerprint-full.log`: 1823 casos = 1819 PASS/0 FAIL/4 SKIP por permisos
  symlink, 210.175s, exit0. No incluye el siguiente archivo nuevo de regresiones de comandos.
- `fingerprint-resume-red-test.log`: 2 casos, 1 PASS/1 FAIL; un segundo fingerprint
  en el mismo Turn invocaba al provider y terminaba en vez de rechazar el drift.
- `fingerprint-turn-integration-build-fixed.log`: 0 warnings/0 errores.
- `fingerprint-turn-integration-focal-fixed.log`: 89 PASS/0 FAIL/0 SKIP, 5.283s.
  Incluye schema/Source.Version, tools ocultas, idempotencia, filtro de harness en
  request real, snapshot del paso, cuestionario tras reopen SQLite/CAS con revisión
  posterior del plan y escalación consentida en proceso/tras reopen.
- `fingerprint-turn-full.log`: 1834 casos = 1830 PASS/0 FAIL/4 SKIP por permisos
  symlink, 213.545s, exit0. Incluye todos los tests de este bloque.

Los tests de factory son fixtures offline. El test de CLI recorre dispatcher, Host,
adapter HTTP loopback, journal SQLite y codec real; no es una consulta autenticada,
cualificación de proveedor real ni consumo real. El test comprueba el componente de
ruta física que quedó persistido en TurnStarted, además de ModelStepStarted.

Comandos desde el worktree autorizado:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*RuntimeBuildIdentityTests' -class '*RuntimeFingerprintFactoryTests' -class '*CliEndToEndTests' -class '*FingerprintComponentsContractTests' -class '*ContextPolicyFingerprintTests' -class '*SimFingerprintTests' -class '*ModelQualificationCliTests' -class '*MaintenanceTests' -class '*ExplorerTurnDurableProviderStateTests'
```
