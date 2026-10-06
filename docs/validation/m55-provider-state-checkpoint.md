# Checkpoint de ProviderState por ModelStep

2026-10-06 06:27 UTC / 00:27 America/Mexico_City.

Implementación de Host sobre los contratos existentes de ADR-0005/0046; no añade
scheduler, joins ni una segunda fuente de uso. `ModelStepCompleted.ResponseArtifact`
conserva el envelope de uso v1 y añade `providerState`, descriptor v1 o `null`.
El descriptor incluye modelo, RouteId, TurnId, StepIndex y StateRef completa.
Los bytes serializados de `ProviderState.Kind/PayloadJson` quedan exclusivamente en
un artifact `ProviderOpaqueState`, `Sensitive`, media type
`application/vnd.omnicore.provider-state+json`. No están en el journal, la respuesta
normal, el contexto ni la telemetría.

Se publica el checkpoint antes del barrier de ModelStepCompleted. Al reanudar se
elige el último paso completado del mismo Run/Lane/Turn y se comprueba su inicio.
Solo se recupera para el mismo modelo y RouteId. Los eventos legacy sin RouteId no
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
el replay completo para todos los providers. ReasoningCapability y la aplicación de
ReasoningReplayPolicy también siguen pendientes. Este bloque no cierra M5.5.
