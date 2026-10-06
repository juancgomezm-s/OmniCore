# M5: suite quick y preflight de coste

Trabajo manual autorizado el 2026-10-06. Luna `gpt-6-luna`, esfuerzo alto, implementa
el bloque; root audita los diffs, reproduce las pruebas y guarda commits. No se
ejecutan benchmarks autenticados ni se modifican credenciales o políticas del usuario.

## Contratos conservados y cambios

- `QuickProbeSuite`: diez probes deterministas, versión `1.1.0`, en vez de tres.
  Siete Reading/Reasoning alimentan InstructionFollowing y tres miden StructuredOutput.
  No acreditan tool calling, coding, recuperación, planificación ni mutaciones.
- `TaskSetHash`: SHA-256 de JSON estructurado UTF-8, orden ordinal por ProbeId y campos
  id/kind/prompt/expected/maxCostUsd. El coste es un string `G29` invariant: 1m y
  1.00m tienen la misma identidad. Los controles U+0001/newlines no separan campos.
  Cambiar el prompt sí cambia la identidad; el formato anterior lo omitía.
- El Host rechaza el coste máximo declarado superior al consentimiento antes de
  construir ModelSelection, conectar el provider o construir ProbeRunner. El runner
  conserva su propia comprobación defensiva. Se reutiliza ModelQualificationCostCapException.

## Evidencia reproducible

Logs en `C:\Users\juanc\.codex\omni-m55-workers-20261006-1558`.

`preflight-red-test.log`: dos casos, 1 PASS/1 FAIL/0 SKIP, 0.240s. Un probe de
2 USD con cap 1 USD y timeout inválido llegaba al constructor del runner y lanzaba
ArgumentOutOfRangeException en lugar del rechazo de coste. El caso positivo sí ejecuta
un stream offline. El repro no acredita una llamada HTTP anterior al guard.

Los fallos de integración previos se preservan: una llave extra del archivo del
worker, un uso incorrecto de CodecRegistry.Encode en el fixture root y el pool
SQLite del fixture. Se corrigieron sin debilitar assertions, limpiar pools globales
ni sustituir el defecto de producción por un cambio de expectativa.

`final-focal.log`: 131 casos, 130 PASS/1 FAIL; el fixture CLI todavía contestaba
solo los tres prompts originales. Se adaptó para parsear el JSON real de cada
request y responder exclusivamente un prompt conocido de quick; se conserva la
aserción Qualified y se exige diez requests y todos los IDs de probes en pantalla.
`final-cli-build.log`: 0 warnings/0 errores.
`final-cli-focal.log`: 131 PASS/0 FAIL/0 SKIP, 2.077s. Incluye los tres controles
SQLite meta entre sesiones. No sumar focales solapados.

Verificación final conjunta: `final-full.log`, 1991 casos = 1987 PASS/0 FAIL/
4 SKIP por permisos Windows de symlink, 101.556s, proceso exit0.
`architecture-test.log`: 56 PASS/0 FAIL/0 SKIP, 0.587s; build 0 warnings/errores.
Esta suite incluye ambos bloques M5 y M5.5; los conteos no se suman a focales.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v minimal
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*Qualification*' -class '*MetaModelSpendRegressionTests' -class '*MetaModelDailySpendIntegrationTests'
```

## Lo que este bloque NO cierra

El MaxCostUsd=0 de los probes es una declaración de fixture, no prueba que una ruta
sea gratuita. El Host todavía no deriva una cota pagada de pricing y límites de
tokens efectivamente enviados. El bloque de evidencia de coste de abajo corrige la
representación cero ficticio; la persistencia completa de resultados sigue pendiente.
BillingMode Local/IncludedQuota/Unknown no se deduce del nombre del modelo ni del login.

El helper TaskSetHash no acredita que BenchmarkIdentity y las respuestas completas
se persistan como artifacts: el wiring de esa evidencia debe verificarse por separado.
La cualificación autenticada de las rutas Sol/Luna ya conectadas sigue requiriendo
una ejecución reproducible del circuito del proyecto; las fixtures y las llamadas
a workers externos no la sustituyen. No se declara M5 completo.

## Persistencia atómica de cualificación

El circuito anterior llamaba Upsert y SaveTraits por separado, con dos commits:
un fallo al insertar traits dejaba una revisión Qualified sin traits. Ahora Host
usa `IModelQualificationStore.UpsertWithTraits`: perfil y traits se confirman en
una transacción de escritura SQLite. La lectura y comprobación de revisión ocurren
dentro de esa transacción. Todos los traits deben identificar la clave exacta y
expectedRevision+1; validación antes de writes, suma de revisión checked, control
de cancelación antes de confirmar. Error, conflicto o cancelación revierten
perfil/traits nuevos y conservan íntegros el perfil y el historial anteriores.

Upsert y SaveTraits independientes se conservan para los consumidores existentes;
no se ofrece un fallback no atómico para la operación conjunta.

Luna aportó `M5QualificationAtomicPersistenceTests`, con trigger SQLite privado
BEFORE INSERT ON model_traits / RAISE(ABORT) en StructuredOutputReliability.
Provider offline exacto, diez requests, store y SQLite reabiertos realmente.
`atomic-red-revalidated-test.log`: tres casos = 1 PASS/2 FAIL, 0.331s. Creación
dejaba perfil y recalificación cambiaba Calibrated→Qualified pese al error de traits.
No se cambiaron esas aserciones al corregir producción. Primer handle de build
16047 desapareció sin resultado: antes de relanzar se confirmó ausencia de procesos
dotnet y de log de test, no se trató un timeout de observación como fallo del producto.

`atomic-fixed-build.log`: 0 warnings/errores. `atomic-fixed-focal.log`:
121 PASS/0 FAIL/0 SKIP, 1.537s. Esta verificación aún no incluye las pruebas nuevas
de contrato del store ni sustituye la suite completa posterior.

`M5QualificationAtomicStoreContractTests`: dos conexiones independientes con
expectedRevision=0, revisión obsoleta tras rev2, identidad de trait inválida con
control de reintento válido, cancelación inyectada durante UpsertCore antes de
insertar traits. Se reabre SQLite y se comparan metadata y campos completos de
traits anteriores. No es una prueba de estrés ni un scheduler concurrente M6.
La auditoría corrigió el fixture del reloj: inicialización del store también usa
el reloj para su marcador de migración, así que la cancelación se arma explícitamente
después de la primera escritura, no por un número global de llamadas supuesto.

`atomic-contract-build.log`: 0 warnings/errores.
`atomic-contract-focal.log`: 125 PASS/0 FAIL/0 SKIP, 1.479s.

`atomic-architecture-test.log`: 56 PASS/0 FAIL/0 SKIP, 0.574s;
build 0 warnings/errores.
Primera suite completa `atomic-full.log`: 1998 casos, 1993 PASS/1 FAIL/4 SKIP
symlink, 109.656s. La prueba existente Pinned_tls_handler_works_end_to_end_against_a_real_tls_server
falló por TaskCanceledException al leer cabeceras del servidor de fixture con plazo
30s; su código y la política TLS no se modificaron. Ejecución aislada del mismo
test: `atomic-tls-isolated.log`, 1 PASS, 2.354s. Esto demuestra diferencia entre
ejecuciones, no identifica ni resuelve su causa. Se preserva el fallo y se ejecuta
una repetición completa sin cambios de fuente, sin aumentar timeout ni retirar assertions.

Repetición completa `atomic-full-repeat.log`: 1998 casos = 1994 PASS/0 FAIL/
4 SKIP por permisos symlink, 112.034s, exit0. No incluye los nuevos tests de coste
desconocido. El fallo TLS inicial queda documentado: la repetición verde no demuestra
que se haya corregido su causa intermitente. No se declara M5 cerrado.

## Evidencia de uso y coste de probes

`ProbeResult.CostUsd` y `QualificationProbeOutcome.CostUsd` ahora son `decimal?`:
null significa desconocido, no cero. Es un cambio de tipo público que requiere
recompilar consumidores; no se afirma compatibilidad binaria del getter anterior.
Se conservan estado, puntuación, salida y duración. Los topes declarados y el
consentimiento previo no se sustituyen por el coste observado después de la llamada.

El runner conserva `TokenUsage` junto con `ReportedUsageFields` de ModelResponse.
Sin respuesta completa, sin tarifas o sin Input y Output reportados, el coste queda
null. Una respuesta puntuable incorrecta conserva uso y coste igual que una correcta.
Uso negativo, cotización negativa o desbordamiento no se convierten en cero.
NotRun tampoco acredita una medición de coste. No se infiere tarifa de la cuenta,
nombre de modelo, login ni BillingMode IncludedQuota/Local.

En el Host normal se reutiliza LoadedUserConfiguration.Pricing(modelId): precios
de modelo por encima de los del provider. Un registryOverride de fixture no aporta
tarifas. El resultado es uso reportado multiplicado por tarifas configuradas en USD,
no un débito autenticado, factura ni saldo de cuenta. Input ya incluye caché y Output
razonamiento: no se suman otra vez. Cero sólo es calculable con datos reportados y
tarifas explícitas que produzcan cero. Tarifas incompletas mantienen null.

La fachada añade `QualificationProbeUsage` con contadores primitivos nullable;
solo los campos reportados tienen valor, los ausentes quedan null. La CLI muestra
«coste desconocido» o «cost unavailable» cuando no hay cotización, sin añadir USD
a ese marcador. Importes disponibles conservan el formato monetario con USD.

Luna entregó las pruebas RED offline y la integración CLI bilingüe sobre HTTP
loopback del adapter real; root corrigió fixtures de compilación, reprodujo RED y
añadió controles de máscaras parciales, precio cero explícito, tarifa parcial,
desbordamiento, cotización inválida y puntuación fallida. Logs previos de compilación
preservados (Task ambiguo y analizador xUnit2002); no acreditan defectos productivos.
RED real `usage-evidence-red-test.log`: 4 FAIL/0 PASS, 0.143s por coste0 informado.
Primera verificación `usage-fixed-focal.log`: 138 PASS/0 FAIL/0 SKIP, 1.574s,
build sin warnings/errores. Aún no incluye la integración de tarifas configuradas
posterior ni reemplaza la suite completa de esta revisión.

Verificación final focal `usage-final-focal.log`: 156 PASS/0 FAIL/0 SKIP, 2.055s,
con configuración privada de tarifas completas/parciales, Host normal y SQLite real.
Incluye SpendPricingTests y no se suma a las focales anteriores. Arquitectura:
`usage-architecture-test.log`, 56 PASS/0 FAIL/0 SKIP, 0.585s; ambos builds sin
warnings/errores. La suite completa `usage-final-full.log` se verifica por separado.

Suite completa final `usage-final-full.log`: 2018 casos = 2014 PASS/0 FAIL/
4 SKIP por permisos Windows de symlink, 106.623s, proceso exit0. Incluye este bloque
de uso/coste y todas sus pruebas. El fallo TLS intermitente de la ronda atómica
anterior permanece documentado: esta ejecución verde no prueba su causa resuelta.

Auditoría siguiente de Luna: store actual no conserva una referencia por revisión
a resultados completos; ArtifactGc marca journal/refs, no raíces de user.db.
Guardar una ref sin integrar retención/GC no cumpliría evidencia durable. ModelRequest
no transmite seed ni temperatura y BenchmarkIdentity exige ambos; representarlos
como cero sería una identidad falsa. Próximo bloque debe preservar esos valores
como no proporcionados y verificar CAS/reapertura/retención y atomicidad de la ref.

## Preflight configurado y límite Chat

Defectos reproducidos sin red ni secretos:

- `configured-cap-red-test.log`: 2 casos = 1 PASS/1 FAIL, 0.372s. Ruta explícita
  MeteredCurrency, precios 2/8 USD por millón, probe.MaxCostUsd0 y cap0 ejecutaban
  la respuesta del fixture en vez de rechazar antes de la invocación.
- `chat-limit-red-test.log`: 5 casos = 1 PASS/4 FAIL, 0.202s. Body capturado del
  adapter omitía max_tokens para límites16/4096/longMax, también en el caso400.
- `configured-cap-overflow-red-test.log`: 5 casos = 4 PASS/1 FAIL, 0.461s.
  Dos topes declarados decimalMax daban OverflowException genérica.
- `runner-cap-overflow-red-test.log`: 1 FAIL, 0.133s; el runner directo tenía la
  misma suma desbordada. Ambos rechazan ahora con excepciones tipadas antes del stream.

Host conserva el guard de topes declarados y agrega el mayor de esa suma y la
estimación de configuración: precio de Input por ContextWindow declarado y precio
de Output por límite realmente serializado, multiplicados por cantidad de probes.
Ejemplo fixture de una invocación: 8192*2/1e6 +2048*8/1e6 =0.032768 USD.
El tope0 y un tope positivo0.01 se rechazan antes de conectar/llamar o guardar perfil.
El control cap1 produce quote0.000066 USD con usage17/4 y conserva estimate0.032768.
`QualificationRunResult.EstimatedCostSource` distingue esa procedencia del gasto medido.

MeteredCurrency o Unknown configurado sin tarifas completas o límite aplicable no puede fabricar una
estimación: lanza ModelQualificationCostEvidenceUnavailableException antes de conectar.
La suma declarada no representable también falla con ese error tipado; el runner usa
QualificationCostEstimateUnavailableException y Host lo traduce para mantener IL del CLI.
No se amplió el máximo de tarifas del loader: decimalMax no es tarifa YAML válida.
Ese caso no se presenta como un repro de overflow de tarifas configuradas.

`PreviewSuiteCost` usa la misma selección/pricing para el consentimiento, sin crear
provider ni consultar cuenta. Sin precios, Unknown/IncludedQuota/CreditBalance/Metered
exponen importe null; Local explícito puede mostrar el máximo DECLARADO de probes,
no un débito medido. CLI muestra estimación de configuración y tope aceptado, o
coste desconocido y tope, nunca la declaración quick0 como precio conocido remoto.

El adapter Chat compatible ahora serializa el long exacto en `max_tokens`; null
omite el campo. Host transmite selección en las rutas Chat, Responses API y Anthropic;
Codex de suscripción sigue sin límite aplicado. Un400 no se reintenta sin límite.
Es el contrato legacy: [OpenAI documenta su deprecación y que no es compatible con
o-series](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create).
La prueba de body no cualifica modelos/servicios ni confirma que un endpoint remoto
honre el límite. No se cambian perfiles ni se migra a otro proveedor o modelo.

Focal final `configured-cap-final-focal.log`: 207 PASS/0 FAIL/0 SKIP, 2.160s;
build0warnings/errores. Full `configured-cap-final-full.log`: 2035 casos =
2031 PASS/0 FAIL/4 SKIP por permisos symlink, 107.362s, exit0.
Arquitectura `configured-cap-architecture-test.log`: 56 PASS/0 FAIL/0 SKIP,
0.662s; build0warnings/errores. Cifras solapadas, no se suman.
Fixtures in-memory y configuración/SQLite privados, no consumo autenticado.

Gates todavía abiertos: ContextWindow es DECLARADO, no tokenización/medición remota;
la estimación cubre una invocación por probe, no una reserva contra gasto concurrente
ni todos los reintentos, tarifas de caché específicas, cargos sin usage o gasto de
rutas sin descriptor y otros modos de facturación. No se afirma garantía monetaria end-to-end ni cierre M5.

Los fixtures no acreditan consumo real o consultas autenticadas. Siguen pendientes
las cotas monetarias pre-call derivadas de límites realmente enviados, el tratamiento
de reintentos/errores sin usage, evidencia CAS completa y BenchmarkIdentity.

## Guard de facturación desconocida — 2026-10-06

ADR0007 §7 exige consentimiento y presupuesto máximo para proveedores de pago;
ADR0046 §3 trata Unknown como potencialmente pagado, no como Local implícito.
La cualificación explícita consentida no equivale a routing automático; aun así,
sin una estimación configurada completa no hay evidencia para aceptar su tope.
Host rechaza ahora también el descriptor Unknown antes de construir/invocar provider.
El consentimiento no convierte desconocido en cero. CLI es/en explica ambos modos.

Luna HIGH aportó cinco regresiones offline; root reprodujo RED
`unknown-billing-red-test.log`: 5 = 2 PASS/3 FAIL, 0.449s, build0warnings/errores.
Fallaban Unknown sin precios con cap0/cap1 y Unknown con precio parcial.
Controles mantienen Unknown con precios completos y Local explícito sin precios.
Los bloqueos verifican calls0, perfil ausente, traits vacíos y store vacío;
los controles verifican score1, uso reportado y traits persistidos, sin precio ficticio.
El fixture previo de precio parcial declara Local explícito para mantener su propósito
de coste medido ausente; sus assertions originales no se debilitan y el comportamiento
Unknown se cubre en la suite independiente.

Focal `unknown-billing-fixed-focal.log`: 212 PASS/0 FAIL/0 SKIP, 2.170s.
Arquitectura: 56 PASS/0 FAIL/0 SKIP, 0.798s, build0warnings/errores.
Full `unknown-billing-final-full.log`: 2040 casos = 2036 PASS/0 FAIL/4 SKIP
por permisos symlink, 116.037s, exit0; build final0warnings/errores.
Fixtures privados no acreditan
consultas autenticadas ni garantía de gasto. registryOverride sin descriptor conserva
su camino legacy; no se declara gratuito por URL/auth/nombre y requiere revisión aparte.
Reservas, reintentos y evidencia CAS siguen siendo gates abiertos de M5.

## Stale: conservar medidas utilizables — 2026-10-06

Luna detectó y escribió regresiones de una pérdida real: MarkStale avanzaba la revisión
sin copiar traits; UsableSnapshot consultaba solo la revisión vigente y devolvía null.
Root reprodujo `stale-evidence-red-test.log`: 5 = 1 PASS/4 FAIL, 5.274s,
build0warnings/errores. Fallaban uso tras reopen, rollback por trigger, cancelación
y overflow de revisión. Root implementa lectura y validación de revisión/estado,
checked-next, cambio y copia de traits dentro de una transacción; cancellation antes
del commit. Se retienen todas las medidas históricas y sus campos originales.

La corrección también contempla datos existentes: migración one-shot
`m5-suite-stale-trait-copy-v1`, solo Stale por versión de suite, sin causa de migración
de ruta, revisión>1, sin traits actuales y con medidas en la revisión inmediatamente
anterior. Copia esas filas exactas: no fabrica medidas, no busca revisiones arbitrarias,
no mezcla ni pisa un conjunto actual, no cambia revisión/fechas/suite del perfil.
Un vaciado explícito posterior no se vuelve a restaurar al abrir el store.
El antiguo API SaveTraits permite un vaciado deliberado anterior al upgrade y no hay
provenance para distinguirlo del fallo antiguo; no existen callers productivos actuales
de ese método, pero esta limitación del contrato público queda documentada.

Root agregó el repro de upgrade: `stale-repair-red-test.log` 6 = 5 PASS/1 FAIL,
0.592s. Una primera implementación expuso otro fallo: el marker se confirmaba antes
de detectar identidad corrupta en la migración de ruta. Se conservó la assertion
original de cero markers; ambas migraciones comparten ahora una transacción.
El mismo intento tuvo un fallo del fixture por sumar DELETE+UPDATE en ExecuteNonQuery;
se separaron operaciones con assertions exactas, sin debilitar condiciones productivas.
Focal corregido `stale-repair-atomic-focal.log`: 218 PASS, 2.120s; controles ampliados
de exclusión/rollback: `stale-evidence-final-focal.log` 222 PASS, 2.275s.
Diez regresiones nuevas: cinco iniciales de Luna, upgrade/idempotencia de root y
cuatro controles de migración de Luna. Root corrigió los namespaces de enums en
el fixture y una lectura preparatoria que abría el store y ejecutaba el upgrade antes
del trigger: ahora el snapshot pre-trigger es raw SQLite, sin causar la reparación.
El build fallido de fixture (seis errores de namespace) queda conservado y no cuenta
como defecto de producción. Full `stale-evidence-final-full.log`: 2050 casos =
2046 PASS/0 FAIL/4 SKIP por permisos symlink, 106.160s, exit0.
Arquitectura `stale-evidence-architecture-test.log`: 56 PASS/0 FAIL/0 SKIP,
0.653s. Builds finales0warnings/errores. Cifras solapadas, no sumables.

Esta reparación conserva evidencia empírica; no define un factor nuevo de reducción
de confianza Stale ni acredita CAS de resultados completos. Quedan por implementar:
artifact User con outputs/score/usage/máscaras/coste nullable y BenchmarkIdentity real,
ref por revisión en la misma tx perfil/traits, Verify bajo exclusión de GC hasta commit,
raíces GC user.db para todo el historial, y parámetros seed/temperatura no enviados
expresados como ausentes (no cero). Una ref sin estas raíces no cerraría M5.

## Raíces CAS User y sampling ausente — 2026-10-06

Prerrequisito de evidencia durable: ArtifactGc lee todas las filas/revisiones de
`model_qualification_evidence` en el `user.db` del mismo directorio del CAS, además
del journal opcional. Campos mínimos del lector: artifact_algorithm exacto sha256,
artifact_hash hexadecimal minúsculo de64 y artifact_size entero no negativo.
Cada blob debe verificar hash y tamaño; se recorre también su grafo de referencias CAS.
Referencia ausente, corrupta, malformada, algoritmo/size incorrectos o DB ilegible
abortan el mark antes de todo sweep. DB antigua sin tabla se admite, sin inventar raíces.
Las consultas son read-only y sin pooling: no retienen handles de DB del usuario
ni limpian pools ajenos. La exclusión ArtifactStoreLease cubre mark y sweep.

`omni gc --scope user [--artifacts <directorio-CAS>] [--grace-hours N] [--dry-run]`
usa el CAS de User por defecto (paths.DataDirectory) y no depende de journal de workspace.
Un --journal explícito con User se rechaza con exit2 para no mezclar namespaces.
Workspace sigue siendo el scope predeterminado y conserva --journal/--artifacts.
Error de validación/IO devuelve exit1; solo un mark completo permite borrar huérfanos.
Un error durante el sweep no promete rollback de borrados anteriores.
No se ejecutó GC contra los datos reales del usuario: todos los borrados fueron fixtures GUID.

Luna escribió nueve casos de raíces fuera del repo; root auditó e integró el paquete,
reprodujo `user-cas-gc-red-test.log`: 9 = 1 PASS/8 FAIL, 0.368s, build0/0.
Primer focal tras implementación tuvo9 fallos de cleanup por pools read-only retenidos,
no se rebajaron assertions: las conexiones productivas son ahora no pooled.
Luna añadió seis casos del comando Host real: dry/live e historia, missing/corrupt root,
combinación de scopes inválida y control workspace. Root corrigió alias Task en el fixture;
el build fallido de cinco errores ambiguos está conservado y no es defecto productivo.
Focal `user-cas-gc-cli-focal.log`: 291 casos, 290 PASS/0 FAIL/1 SKIP,
9.128s; la omisión es de permisos symlink. Build0warnings/errores.
Full `user-cas-gc-final-full.log`: 2067 casos, 2063 PASS/0 FAIL/4 SKIP symlink,
104.999s, exit0. Arquitectura56PASS0.651s, build0/0. Cifras solapadas, no sumables.

BenchmarkIdentity conserva constructor int/double para valores explícitos y agrega
overload nullable: Seed int?, Temperature double?. Ausencia no se representa como0;
la presencia tampoco acredita que el proveedor lo recibiera. Luna aportó dos controles
de cero/ausencia/temperatura conocida. Los getters cambian tipo CLR: consumidores
deben revisar/recompilar; no se afirma compatibilidad binaria. No se cambió ModelRequest
ni se envió sampling nuevo. El próximo artifact registrará seed/temperature null y
samplingParametersSent=false porque el request actual no los envía.

La tabla usada en estas pruebas es un fixture de columnas mínimas, no la implementación
del store de evidencia. Falta crear su schema productivo y writer atómico perfil/traits/ref,
Verify bajo lease hasta commit, ref histórica de Stale y JSON completo/identidad real.
GC roots y nullable sampling por sí solos no cierran M5 ni acreditan gasto/autenticación.

Auditoría Luna readonly: el orden lease/mark/verify/sweep y los rechazos de scope
son correctos. La ausencia de tabla se tolera como legado; el schema productivo
debe añadir un marcador durable para rechazar una tabla borrada tras instalar evidencia.
Un override User de CAS compartido con workspace no incluye raíces de sus journals:
no usar este comando para un namespace compartido; no se acredita soporte de ese caso.

## Contrato de evidencia durable por revisión — 2026-10-06

Capability aditiva `IModelQualificationEvidenceStore`, separada del store legacy:
`UpsertWithTraitsAndEvidence` publica la siguiente revisión junto a sus traits y
una ArtifactRef verificada; `Evidence(key, revision)` devuelve la referencia exacta
y SourceRunRevision, o null para revisiones antiguas sin evidencia. No backfill inventado.
El JSON se publica primero en el CAS User; después el writer adquiere el lease de GC,
verifica hash/tamaño y confirma perfil/traits/ref en una transacción. Si GC recogió
el blob antes del lease, el writer falla sin dejar perfil parcial. Nunca llama PutText
dentro del lease no reentrante. Conflicto, fallo SQL o cancelación anterior al commit
revierten la operación completa. Un blob publicado sin commit puede quedar huérfano.

Schema productivo `model_qualification_evidence`: PK(key_hash,profile_revision),
source_run_revision y metadata íntegra ArtifactRef (id, algorithm/hash/size, media_type,
kind, sensitivity, redacted). El marcador `m5-qualification-evidence-v1` vive en
model_profile_migrations y se instala en la transacción de upgrade. Store y GC
rechazan una tabla ausente después del marcador, sin recrearla ni barrer blobs.
MarkStale copia la referencia conservando SourceRunRevision y los registros históricos;
la migración de ruta también conserva refs si existen. Legacy sin resultados sigue null.

Host almacena `application/vnd.omnicore.model-qualification+json`, schema
`omnicore.model-qualification.v1`, Sensitive/Other. Incluye clave canónica/hash,
sourceRunRevision, UTC recordedAt, origen configured-provider/injected-provider,
probeSetOverride, BenchmarkIdentity (suite/version, hash del conjunto real ejecutado,
seed/temperature null, samplingParametersSent=false, assemblyVersion/MVID real),
estimación y fuente/cap/moneda, cada definición prompt/expected/maxCost, cada resultado
output/error/status/score/durationTicks/cost nullable, máscara y usage nullable, y traits.
Cache y reasoning son subconjuntos, no sumas adicionales; coste computado no es débito.
FileArtifactStore redacta secretos antes de hash/publicación: ArtifactRef.Redacted
identifica esta transformación, no se promete conservar secretos crudos para auditoría.
QualificationRunResult agrega EvidenceHash/EvidenceRedacted como datos primitivos.

Integración Host real con provider scripteado: dos ejecuciones completas, reopen,
Stale y GC sin gracia conservan ambas evidencias; subset inyectado conserva su hash
real y provenance, no se confunde con el conjunto quick completo. Estas son fixtures,
no consultas autenticadas ni evidencia de gasto real.

Luna entregó12 casos de store; root leyó y corrigió el using Abstractions y el
snapshot esperado de traits en la revisión alias (rev3, no rev2), sin cambiar los
valores. Build de seis errores de fixture conservado en evidence-subset-build.log;
su ejecución posterior usó el binario anterior y NO acredita esas pruebas nuevas.
Root añadió dos pruebas de Host y tres controles de recalificación Calibrated y
lease OS retenido en tx. Focal final226PASS0FAIL0SKIP5.053s, build0/0,
arquitectura56PASS0.508s. Full evidence-final-full.log:2084=2080PASS0FAIL4SKIP
symlink106.245s, exit0. Cifras solapadas/no sumables. No cierre M5 por este bloque.

Auditoría Luna readonly confirmó orden lease/verify/transaction y alias histórico;
el control adicional root ya prueba rollback sobre Calibrated. Hallazgo siguiente:
un override de dos probes que pasan puede marcar el perfil Qualified bajo el id de
Quick aunque no haya ejecutado toda Quick. El JSON del bloque hace visible su hash
y probeSetOverride, pero no corrige aún esa clasificación preexistente. Debe probarse
RED y corregirse la frontera de cobertura sin rebajar la evidencia de los controles
de costes/rutas. Otro límite de API a comprobar: constructor legacy del store permite
nombres DB arbitrarios, mientras GC User solo consulta user.db; el writer de evidencia
no debe confirmar una ref fuera del namespace de raíces que GC sabe marcar.

Reproducción local (fixtures, sin consultas autenticadas):

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -class '*Qualification*' -class '*Gc*'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll
```

## Cobertura Quick y namespace User — 2026-10-06

Qualified exige todos los resultados Passed y el hash canónico del conjunto real
igual al de Quick soportada. Un subset o una definición modificada puede guardar
medidas, pero queda ProvisionallyClassified, no se consume como evidencia Qualified.
La lista de probes se copia antes de validar, estimar o construir requests; esa misma
copia se usa después del await para traits, resultados e artifact. La reordenación
del conjunto completo mantiene el mismo hash y sigue cualificando. Suites no
soportadas no pueden eludir la validación mediante un override; empty/IDs duplicados
se rechazan antes de llamar al provider. JSON/outcome añaden SuiteComplete explícito,
independiente de probeSetOverride (un override completo también puede ser válido).

El nuevo writer durable solo admite el nombre exacto user.db, la base que marca GC
User en el directorio CAS. Alternate.db se rechaza con InvalidOperationException
antes de adquirir lease/mutar perfil o traits. Las operaciones legacy perfil/traits
mantienen sus rutas anteriores. Cancelación se consulta primero. La conexión usa
SqliteConnectionStringBuilder con ruta absoluta y Pooling=false: un punto y coma
en una ruta legal no se interpreta como parámetros de conexión ni redirige la DB.

RED reales conservados en workers-1558: quick-coverage-red-test.log6=2PASS4FAIL
0.351s; evidence-namespace-red-test.log4=3PASS1FAIL0.274s;
namespace-snapshot-red-test.log7=4PASS3FAIL0.294s (dos mutaciones async retirando
temporalmente solo la copia; un path con punto y coma). Copia restaurada y ruta corregida.
Luna aporta cuatro namespace y dos snapshot async; root seis gates de cobertura,
path extra, producción y controles JSON/outcome. Un build intermedio falló por seis
using Abstractions faltantes del fixture snapshot; namespace-path-red-build.log
conservado y no se ejecutaron pruebas nuevas desde ese build.

Cinco controles de coste previos declaraban Qualified para un único probe: se corrige
la expectativa exacta a Provisional y se añade Assert.False(SuiteComplete), conservando
sin cambios contadores de llamadas, precios, máscaras/usage, coste, cap y traits.
Los controles de Quick oficial/reordenada mantienen Qualified y suiteComplete=true.
Un contador DDL de DROP TRIGGER en un fixture Stale devolvió1 (último DML de la
conexión), no prueba de schema: ahora se exige COUNT(*)=0 del trigger desaparecido,
manteniendo íntegros los snapshots/rollback/idempotencia de reparación.
Focal final239PASS0FAIL5.085s, build0/0; arquitectura56PASS0.855s.
Full coverage-namespace-final-full.log:2097=2093PASS0FAIL4SKIPsymlink113.597s,
exit0. Cifras solapadas/no sumables; fixtures no acreditan gasto ni autenticación.

## Descriptor de billing ausente — 2026-10-06 20:14 UTC

La configuración normal valida providers, pero el API registryOverride creaba solo
modelos y permitía conectar el fallback OMNI_BASE_URL sin descriptor de billing.
Ahora provider==null y Provider inyectado==null lanza
ModelQualificationCostEvidenceUnavailableException después del consentimiento y
antes de construir requests/conectar. No se infiere gratuidad de nombres ni URLs.
La preview sigue mostrando coste null/source unavailable. El rechazo no guarda
perfil, traits ni evidencia. La inyección explícita conserva compatibilidad: acepta
cualquier IModelProvider y no certifica gratuidad, identidad ni billing; nuestros
controles concretos son scripteados offline, no cualificación autenticada.

M5QualificationMissingProviderDescriptorTests aporta cinco casos (Luna HIGH):
caps0/1 con destino loopback cerrado, no efectos; inyección offline parcial;
descriptor Local explícito; ConfigLoader rechaza proveedor ausente. Root auditó y
reprodujo RED:5=3PASS2FAIL14.165s, missing-descriptor-red-test.log. La ejecución
corregida detectó cuatro fixtures CLI sin descriptor; se trasladaron a providers.yaml
y models.yaml privados con billingMode Local explícito y clave derivada de esa
configuración normal, conservando todas las assertions de llamadas/coste/política.

Evidencia workers-1558: missing-descriptor-cli-focal.log244PASS5.393s;
missing-descriptor-final-full.log2102=2098PASS0FAIL4SKIPsymlink106.833s/exit0;
missing-descriptor-architecture-test.log56PASS0.884s; builds0/0. Cifras solapadas.
Reproducción: los comandos locales de build/suite de este documento, con filtro
`-class '*Qualification*' -class '*Gc*'` para el focal. Fixtures no prueban gasto real.

Auditoría de cobertura ADR0007: Quick mide dos traits (7 InstructionFollowing y
3 StructuredOutputReliability); los demás permanecen heurísticos/no medidos.
Qualified expresa suite Quick completa, no nueve traits demostrados. Confianza
persistida .7/.3 deriva del conteo de probes, no estadística ni calibración real.
Full/calibración son M10+; sampling no enviado permanece declarado null/false.
Pendientes M5: retries/cota monetaria y evidencia conectada; no cierre por suite verde.
