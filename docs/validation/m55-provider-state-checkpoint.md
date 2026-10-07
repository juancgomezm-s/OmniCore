# Checkpoint de ProviderState por ModelStep

## Otras familias: solicitud explícita sin omisión — 2026-10-07 03:34 UTC

Anthropic representa la forma existente `ReasoningRequest("budget", n)` con
presupuesto manual explícito >=1024; no deduce presupuestos a partir de etiquetas
de esfuerzo ni ignora una etiqueta mezclada con un presupuesto. La ausencia de
razonamiento mantiene el body y los límites existentes. El adaptador compatible
no tiene un dialecto de solicitud declarado: rechaza razonamiento explícito sin
inventar `reasoning_effort`, pero conserva la lectura de `reasoning_content` en
las respuestas normales. Ambos validan contradicciones declaradas por ruta antes
de consultar secretos o enviar HTTP, también con fallback desde ModelSelection.
Los errores tienen texto constante, sin incluir secretos ni solicitudes.

Luna: auditoría y propuesta offline. Root: lectura completa, corrección del fixture
de header Anthropic, integración, expansión de fallback, presupuestos inválidos y
contradicciones declaradas; implementación y ejecución. RED compilado: 7 casos,
4 FAIL por omisión silenciosa / 3 PASS, 0.244s. Final: 162 PASS / 0 FAIL / 0 SKIP,
4.983s; arquitectura 56 PASS. Logs `other-adapters-reasoning-red.log`,
`other-adapters-reasoning-final.log`, `other-adapters-reasoning-architecture.log`
en `C:\Users\juanc\.codex\omni-m55-workers-20261006-2103`.
La primera expansión del fixture no compiló por usar `with` en ModelRequest;
se corrigió con su constructor antes del RED válido. No cuenta como fallo del
producto. Los fixtures usan handlers HTTP y secretos sintéticos; no acreditan
autenticación, disponibilidad de modelos ni gasto real.

Reproducción:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*OtherAdaptersReasoningBoundaryTests' -class '*Anthropic*' -class '*ModelProviderResilienceTests' -class '*Reasoning*' -class '*ProviderState*'
```

FULL sobre 0e83092 terminó con 2545 casos / 2541 PASS / 0 FAIL / 4 SKIP por
symlink, 268.165s (`responses-reasoning-native-full.log`); antecede este bloque.
La suite completa posterior todavía debe verificarse.
La auditoría de replay distingue cobertura existente de None/PreserveAcrossSteps
y la frontera universal misma ruta/modelo de las diferencias operativas no
especificadas de ProviderManaged/RequiredWithTools y del valor null. No se
congelan expectativas inventadas ni se acredita cierre M5.5 con esta suite focal.

## Wire nativo Responses — 2026-10-07 03:26 UTC

OpenAIResponsesProvider ya no omite `none`, `xhigh` y `max`. El adapter representa
exactamente los siete valores publicados de `reasoning.effort`: none/minimal/low/
medium/high/xhigh/max. Esto es vocabulario de Responses, no un enum/ranking global
de Domain ni una afirmación de soporte de todos los valores por todos los modelos.
El subconjunto declarado por ruta se valida también para invocaciones directas
al adapter; la disponibilidad real sigue siendo responsabilidad del proveedor.
Fuente contrastada y abierta el 2026-10-07:
[guía oficial de razonamiento](https://developers.openai.com/api/docs/guides/reasoning).

Una solicitud explícita sin representación en este adapter falla con mensaje
constante antes de consultar credenciales o enviar HTTP, en lugar de omitirla.
BudgetTokens numérico (positivo, cero o negativo) no se convierte en un esfuerzo
ni en max_output_tokens. Se mantiene el fallback existente desde ModelSelection
si ModelRequest.Reasoning es null; ausencia de ambos no inventa un esfuerzo.
`none` como esfuerzo explícito no significa ReasoningReplayPolicy.None: no se
modifica la política de continuidad, la selección, las cotas ni la autorización.

Luna aportó propuesta cerrada offline; root leyó completa, contrastó documentación,
integró y amplió controles de contradicción declarada/ausencia/case/budgets.
RED: 20 casos / 8 PASS / 12 FAIL, 0.211s, responses-reasoning-native-red.log.
Final ampliado: 159 PASS / 0 FAIL / 0 SKIP, 10.302s, build 0/0,
responses-reasoning-native-final.log; arquitectura56PASS/0FAIL.
Los perfiles API y Codex usan HttpMessageHandler/SSE y credenciales sintéticas;
no autenticación ni consumo real. Full2513=2509PASS/4SKIP265.703s sobre2781b71
antecede este cambio; la suite completa posterior sigue pendiente.

Pendientes distintos: dialectos Anthropic/ChatCompatible, storage opaco seguro y
otras políticas de replay no se declaran resueltos por estos controles de esfuerzo.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*OpenAIResponses*' -class '*Reasoning*' -class '*ProviderState*' -class '*NativeOutputTokenLimitTests' -class '*HostOutputTokenLimitTests' -class '*M5QualificationCodexSubscriptionIntegrationTests'
```

## Solicitud de razonamiento declarada — 2026-10-07 02:53 UTC

`ReasoningCapability.ValidateRequest` rechaza una solicitud no nula cuando
`Supported=false`, o cuando existe una lista declarada de esfuerzos que no
contiene su `Kind` exacto. Null/Unknown no se convierten en soporte ni en
ausencia de soporte. Sin solicitud no hay contradicción; una lista vacía no
equivale a una lista no informada. No hay ranking, cambio de mayúsculas,
heurística por nombre ni traducción universal entre esfuerzos y budgets.

ExplorerTurn comprueba antes de escribir eventos o invocar; devuelve Error
sin herramientas ni nuevos ModelSteps ante contradicción. MetaModelService
comprueba antes de reservar/persistir/despachar y ProbeRunner antes de admitir
por observer. Ambos rechazan con InvalidOperationException. Los tres conservan
la selección en `ModelRequest.Reasoning`, sin introducir otra fuente de policy.
Esto no amplía permisos ni consentimiento de gasto. La cualificación normal
de Host, que selecciona Reasoning=null, no solicita un esfuerzo por el mero
hecho de ejecutar un probe de tipo Reasoning.

Luna aportó propuestas offline y auditoría de call sites; root corrigió tipos
del fixture (IEventCodecRegistry y ExplorerTurn.TurnResult), alineó la respuesta
scripted del control del probe con su respuesta esperada, reprodujo los 12 REDs
y realizó la implementación. `declared-reasoning-request-red.log`:
12 casos / 12 FAIL / 1.128s. `declared-reasoning-request-final.log`:
67 PASS / 0 FAIL / 5.798s. Barrido ampliado con cualificación y TUI:
`declared-reasoning-and-tui-final.log`, 154 PASS / 0 FAIL / 0 SKIP,
95.095s, build 0 warnings / 0 errores. Cifras solapadas, no sumar.

Reproducción mínima de esta frontera, tras el build indicado más abajo:

```powershell
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*DeclaredReasoningRequestTests' -class '*SecondaryReasoningRequestTests'
```

Fixtures con proveedor en memoria y CAS/SQLite privados; no llamadas
autenticadas, factura real ni evidencia de protocolos del proveedor. Los
adaptadores nativos ya recurrían a ModelSelection.Reasoning cuando la request
era null: no se afirma que todo esfuerzo se perdiera en la transmisión.
La validación y representación neutral corregidas aquí NO resuelven todavía
el dialecto de esfuerzo de cada adapter, el storage opaco general seguro ni
las políticas de replay restantes.

Suite completa sobre89e990a antes de estas correcciones:
`visible-content-reopen-full.log`, 2469 casos / 2464 PASS / 1 FAIL /
4 SKIP symlink, 342.495s, terminal exit1. El FAIL fue una lectura del
SubViews del selector mientras el hilo UI lo reemplazaba en TuiWiringTests.
La prueba ahora inspecciona sus snapshots y elige en el propio hilo UI,
manteniendo las mismas assertions; los 154 casos incluyen esa regresión.
No se acredita aún suite completa verde del estado posterior.

## Contenido visible durable — 2026-10-07 02:42 UTC

Se corrigió la pérdida de razonamiento y texto intermedio al reabrir SQLite/CAS
y reanudar el mismo Turn. `ModelStepCompleted.ResponseArtifact` conserva el
envelope de consumo existente y añade opcionalmente `visibleContent`:
`version: 1`, `routeIdentityHash` y `blocks` ordenados. Cada bloque contiene
`kind: reasoning` con `text` nullable y `visibility`, `kind: text` con `text`,
o `kind: tool-call` con `callId`. El último es sólo un marcador de orden:
nombre y argumentos proceden exclusivamente del ToolCallRequested canónico
del mismo paso, Run, Lane y Turn. No crea autorización ni llamadas nuevas.

Texto y razonamiento visible se redactan antes de persistir. Esta proyección
no contiene firmas, ProviderState ni OpaquePayload. El estado nativo firmado
continúa por su checkpoint existente; su almacenamiento opaco general seguro
sigue pendiente. La restauración exige mismo modelo, RouteId e identidad
física; sólo incluye razonamiento del Turn que se reanuda, no del siguiente.
Artefactos legacy sin proyección siguen siendo legibles. Proyecciones corruptas,
marcadores duplicados o pertenecientes a otro paso fallan antes de invocar;
marcadores sin llamada canónica ejecutada no fabrican herramientas.

Evidencia offline: adapter Anthropic real con SSE inyectado, reapertura real
SQLite/CAS y respuesta por OmniServer. Se verifican por separado firma nativa,
razonamiento visible, orden Reasoning/Text/ToolCall y ausencia de duplicados.
No acredita consultas autenticadas ni consumo real. RED reproducibles:
`anthropic-visible-reopen-red.log` (1 FAIL), `visible-response-order-red.log`
(1 FAIL), `visible-content-interleaved-red.log` (1 FAIL) y
`visible-step-marker-red.log` (1 FAIL). Focal final:
`visible-content-contract-final.log`, 79 PASS / 0 FAIL / 0 SKIP, 51.403s;
build 0 warnings / 0 errores. Resultados anteriores solapados, no sumar.
La suite completa anterior sobre 0fbb322 no incluye este bloque.

Reproducción mínima desde la raíz del repositorio (fixtures identificados):

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*ExplorerTurnAnthropicContinuationTests' -class '*ReasoningReplayResumeTests' -class '*VisibleStepMarkerBoundaryTests'
```

El comando mínimo cubre la regresión, no reproduce por sí solo los 79 casos
del barrido ampliado. Logs en `C:\Users\juanc\.codex\omni-m55-workers-20261006-2103`.
Las notas siguientes de 02:22 y anteriores son históricas: la carencia de
contenido visible que describen queda corregida por este bloque.

## Replay explícito None — 2026-10-07 02:13 UTC

Actualización 02:22 UTC: full sobre0fbb322, 2458 casos / 2454 PASS / 0 FAIL /
4 SKIP symlink / 352.538s (`reasoning-targetref-full.log`), terminal exit0.
No incluye la propuesta nueva de reapertura Anthropic aún fuera del repo.
Anthropic y Responses restauran firmas/items cifrados mediante ProviderState,
no ReasoningBlock.OpaquePayload; la carencia confirmada es la reconstrucción
del bloque visible en el historial durable, no una pérdida demostrada de firmas.

La declaración `ReasoningCapability` ya carga desde YAML y participa en el
fingerprint efectivo. `None` retira la continuación de la solicitud saliente y
las referencias opacas de ReasoningBlock, incluso en contenidos ToolResult
anidados; no elimina los checkpoints ni modifica los contadores persistidos.
Unknown conserva compatibilidad y no se interpreta como None. Un cambio de
declaración al reanudar sigue rechazado por el fingerprint, antes de invocar.

Pruebas offline: M5ReasoningReplayPolicyTests y ReasoningReplayResumeTests,
con tool real read-only y reapertura SQLite/CAS. El proveedor es scripted;
no acreditan llamadas autenticadas. RED adicional `reasoning-opaque-none-red.log`:
2 casos, 1 FAIL por referencia opaca reenviada. Focal final ampliado:
68 PASS / 0 FAIL / 0 SKIP, 5.898s, build 0 warnings/errores,
`reasoning-none-expanded-final.log` en
`C:\Users\juanc\.codex\omni-m55-workers-20261006-2103`.
Reproducir con el comando de abajo añadiendo los dos nuevos filtros `-class` y
`-class '*InternalActCommandTests'`. Los resultados se solapan; no sumar.

Auditoría adicional: `LoadConversation` reconstruye tool calls/resultados y
texto, no ReasoningBlock al reabrir. El ensayo que exigía conservar ese bloque
produjo 2 FAIL (`reasoning-none-expanded.log`); es una carencia pendiente,
no evidencia de un round-trip implementado. El fixture final verifica el
ProviderState durable y el rechazo de replay None, sin acreditar conservación
de ReasoningBlock en resume. También siguen pendientes almacenamiento opaco
seguro, semántica/enforcement de las otras políticas y esfuerzo por adapter.
No hay nueva suite completa verde ni cierre de M5.5 por este bloque.

2026-10-06 06:27 UTC / 00:27 America/Mexico_City.

Implementación de Host sobre los contratos existentes de ADR-0005/0046; no añade
scheduler, joins ni una segunda fuente de uso. `ModelStepCompleted.ResponseArtifact`
conserva el envelope de uso v1 y añade `providerState`, descriptor v2 o `null`.
El descriptor incluye modelo, RouteId, binding físico, TurnId, StepIndex y StateRef completa.
El descriptor v1 histórico sigue legible, pero no autoriza replay sin binding físico.
[Ampliación y verificación del binding](m55-provider-state-physical-binding.md).
Los bytes serializados de `ProviderState.Kind/PayloadJson` quedan exclusivamente en
un artifact `ProviderOpaqueState`, `Sensitive`, media type
`application/vnd.omnicore.provider-state+json`. No están en el journal, la respuesta
normal, el contexto ni la telemetría.

Se publica el checkpoint antes del barrier de ModelStepCompleted. Al reanudar se
elige el último paso completado del mismo Run/Lane/Turn y se comprueba su inicio.
Solo se recupera para el mismo modelo, RouteId e identidad física. Los eventos legacy sin RouteId no
autorizan replay. Una respuesta con estado nulo elimina la continuación; no se busca
un estado anterior como fallback. Un Turn nuevo no recibe el estado de otro Turn.
Descriptor/ref/blobs corruptos fallan con mensaje constante, sin contenido opaco.
GC conserva la referencia transitiva desde el artifact de respuesta.

## Evidencia reproducible

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*ProviderStateCheckpointTests' -class '*ExplorerTurnDurableProviderStateTests' -class '*ExplorerTurnProviderStateIsolationTests' -class '*ExplorerTurnProviderStateNullClearingTests' -class '*ExplorerTurnOpaqueReasoningCasTests' -class '*ExplorerTurnSuspendedUsageRegressionTests' -class '*ExplorerTurnAnthropicContinuationTests' -class '*ModelStepEventContractsTests'
```

Resultado final focal: 31 PASS, 0 FAIL, 0 SKIP; build 0 warnings/errores.
Log: `C:\Users\juanc\.codex\omni-m55-three-20261006\provider-state-accounting-tests.log`.
Suite completa final sobre `5f40a81`: 1588 casos, 1584 PASS, 0 FAIL, 4 SKIP
por permisos symlink; 134.443 s. Log `provider-state-accounting-full-suite.log`
en el mismo directorio. Los focales se solapan con la suite y no se suman.
La integración reabre SQLite/CAS y resuelve el cuestionario por OmniServer. Comprueba
same destination, cambios de ruta/modelo, estado nulo, corrupción y GC sin gracia.
El adapter Anthropic real recibe SSE de fixture y reenvía la firma intacta antes de
tool_use, con tool_result asociado. Esto NO acredita consulta autenticada, firma de
proveedor real, cuotas ni consumo real.

El primer ensayo del nuevo fixture Anthropic falló porque la sustitución de argumentos
SSE no coincidía con el escape JSON original; se corrigió el fixture manteniendo las
assertions. Logs inicial/final conservados, no se simula una consulta exitosa.

## Frontera aún pendiente

ADR-0018 exige redacción en artifacts de texto. El writer actual puede alterar un
estado opaco que coincida con un secreto registrado o un patrón redactable. Persist
verifica exactitud, hash, tamaño y flag Redacted; si hay alteración, falla cerrado en
vez de enviar una firma inválida. Una prueba con secreto sintético registrado lo verifica.
Antes de abortar se persiste ModelStepCompleted con el uso reportado y estado nulo:
la llamada ya consumió tokens, y el fallo del checkpoint no puede borrar esa evidencia.
No se ejecutan sus herramientas ni se publica un cuestionario después del fallo.
No se ha añadido un bypass de redacción ni almacenamiento de credenciales en artifacts.
Hace falta resolver un storage opaco seguro que conserve esos bytes antes de declarar
el replay completo para todos los providers. ReasoningCapability y el bloqueo
None se incorporaron posteriormente como se documenta arriba; el resto de
ReasoningReplayPolicy sigue pendiente. Este bloque no cierra M5.5.
