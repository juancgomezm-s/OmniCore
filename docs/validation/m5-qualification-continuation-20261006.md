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
tokens efectivamente enviados. ProbeRunner informa coste cero sin cotizar el usage
de la respuesta: corregir esa representación y su persistencia sigue pendiente.
BillingMode Local/IncludedQuota/Unknown no se deduce del nombre del modelo ni del login.

El helper TaskSetHash no acredita que BenchmarkIdentity y las respuestas completas
se persistan como artifacts: el wiring de esa evidencia debe verificarse por separado.
La cualificación autenticada de las rutas Sol/Luna ya conectadas sigue requiriendo
una ejecución reproducible del circuito del proyecto; las fixtures y las llamadas
a workers externos no la sustituyen. No se declara M5 completo.

Auditoría Luna de persistencia: Host llama Upsert y SaveTraits por separado, cada
uno confirma su propia transacción. Un fallo al insertar traits deja una revisión
Qualified sin traits; al recalificar, la revisión anterior conserva sus traits,
pero la vigente queda incompleta. Repro pendiente: trigger SQLite privado BEFORE
INSERT ON model_traits que aborte StructuredOutputReliability, con provider offline.
El siguiente bloque debe guardar perfil y traits en una transacción conjunta y
probar rollback/reopen tanto de una creación como de una recalificación. Es un
hallazgo de código, aún no un RED ejecutado ni un fix implementado.
