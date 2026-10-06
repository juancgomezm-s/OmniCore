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

Los fixtures no acreditan consumo real o consultas autenticadas. Siguen pendientes
las cotas monetarias pre-call derivadas de límites realmente enviados, el tratamiento
de reintentos/errores sin usage, evidencia CAS completa y BenchmarkIdentity.
