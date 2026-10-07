# M5.5 — perfil de lane en el fingerprint

## Auditoría actual — 2026-10-07 03:44 UTC

El bloque descrito abajo registra fielmente ProfileId de LaneCreated, pero eso
no acredita el componente de configuración reutilizable requerido por ADR0046.
OmniServer y RunControlService crean ProfileId.New para cada Lane sin resolver
una definición de AgentProfile. Dos ejecuciones con defaults idénticos pueden
tener distintos hashes sólo por esa identidad generada; el test con IDs
manuales iguales no prueba el wiring normal ni contenido/revisión del perfil.
Luna detectó esta diferencia; root contrastó callsites y ADR0017/0037/0046.
Pendiente resolver/fingerprintear configuración efectiva con autoridad definida,
no quitar el componente, convertir desconocido en defaults inventados o cambiar
fingerprints históricos. La evidencia histórica siguiente sigue siendo válida
sólo para identidad de Lane, aislamiento y round-trip, no para cierre de §7.

2026-10-06 22:40 UTC / 16:40 America/Mexico_City.

ADR-0046 §7 requiere AgentProfile entre los componentes del fingerprint.
LaneCreated ya registra su ProfileId, pero ExplorerTurn no lo consultaba y
RuntimeFingerprintFactory no recibía ese dato. Luna detectó la brecha en una
auditoría de código; root reprodujo, implementó, integró y verificó el cambio.

## Contrato efectivo

- WithTurnConfiguration sustituye el componente versionado `agent.profile`;
  no acumula componentes de ejecuciones previas.
- Su JSON canónico v1 contiene `profileId` y `source` (`lane.created` si está
  disponible, `unavailable` con profileId null si no existe una lane registrada).
- ExplorerTurn resuelve la lane solicitada en el journal de la sesión y Run
  actuales. No infiere el perfil desde ambient scope o desde el fingerprint base.
- Solo la identidad de perfil participa, no SessionId/RunId/LaneId. Ejecuciones
  distintas del mismo perfil pueden mantener la huella; otro perfil la cambia.
- La representación sigue el CAS textual/redactor/verificación existentes; su
  referencia se indexa en TurnStarted y sobrevive a SQLite close/reopen.
- La comprobación de resume continúa exigiendo la huella original. No se cambia
  silenciosamente un Turn abierto a una nueva configuración ni se reescriben
  fingerprints históricos.

El dato disponible actualmente es ProfileId en LaneCreated, no una definición
completa cargada desde un registro de AgentProfiles. Este bloque no inventa ese
registro ni la semántica de skills/M6. El componente skills y los restantes
criterios de cierre siguen sujetos a la auditoría del objetivo completo.

## Evidencia reproducible

Todos los providers/respuestas son fixtures, sin autenticación ni gasto real.
Persistencia SQLite/CAS real privada y CLI configurado normal con HTTP loopback.

- RED: ExplorerTurnFingerprintIntegrationTests, 3 casos, 2 PASS / 1 FAIL,
  1.318 s: el nuevo control no encontraba `agent.profile`.
- Primera batería: 81 casos / 78 PASS / 3 FAIL. Dos fixtures aún suponían tres
  componentes o perfiles nuevos mientras exigían igualdad de configuración;
  el nuevo control comparaba IDs aleatorios de receipts CAS en vez de contenido.
  Se conservaron las comprobaciones de igualdad, aislamiento y roundtrip:
  mismo perfil explícito en el fixture de policy; comparación de nombre/version/
  hash/size/media/kind/sensitivity/redaction entre receipts distintos.
- Final: 83 PASS / 0 FAIL / 0 SKIP, 12.778 s, build 0 errores/advertencias.
- Arquitectura: 56 PASS / 0 FAIL / 0 SKIP, 1.242 s, build 0 errores/advertencias.
- Controles: tres ejecuciones/sesiones, mismo perfil conserva hash, perfil
  distinto cambia solo su componente; CAS exacto y refs durables; null explícito
  no hereda perfil; CLI comprueba ProfileId contra LaneCreated independiente;
  cuestionario/reopen/resume y tres ModelSteps siguen pasando.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*Fingerprint*' -class '*CliEndToEndTests' -class '*QuestionnaireTurnTests' -class '*M55ThreeStepSuspensionTests'
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore -v quiet
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll -noLogo
```

Logs en C:/Users/juanc/.codex/omni-m55-workers-20261006-2103/agent-profile-*,
resultado definitivo final2 y architecture. Conteos focales solapados.
No acredita una suite completa verde posterior ni cierra M5.5 por sí solo.
