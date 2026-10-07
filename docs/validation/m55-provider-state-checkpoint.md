# Checkpoint de ProviderState por ModelStep

## Replay explícito None — 2026-10-07 02:13 UTC

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
