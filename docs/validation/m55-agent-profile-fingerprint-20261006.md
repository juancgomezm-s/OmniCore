# M5.5 — perfil de lane en el fingerprint

## Integración efectiva parcial verificada — 2026-10-07 07:12 UTC

`AgentProfilePermissionPolicy` intersecta el perfil resuelto con la política
existente: preferencias y aprobaciones no elevan el techo. Comprueba rutas
físicas, globs relativos al workspace, argv completo, red, secretos y shell.
Reglas de proceso/red coincidentes se intersectan; la categoría `build` requiere
un proceso estructurado con efecto compatible. Un `Ask` del techo del perfil no
se convierte en `Allow` mediante un grant de tool.

Los factories Host aceptan el perfil explícito y lo aplican al executor real.
ExplorerTurn obtiene la definición del techo efectivamente aplicado, comprueba
su identidad contra LaneCreated antes de llamar al provider y registra su
configuración completa. Las preferencias solo ordenan las herramientas visibles;
no crean un allowlist. Los callsites sin perfil mantienen el comportamiento previo.

Evidencia root sobre checkpoint coordinado con Luna, con respuestas de modelo
fixture offline y CAS real privado; **no son consultas autenticadas**:

- Build de tests: 0 errores / 0 advertencias, 33.68 s.
- Focal conjunta perfil/fingerprint/autoridad: 34 casos, 33 PASS / 1 FAIL,
  1.061 s. Fallo detectado en ProductEffort de cliente (`Standard` frente a
  `standard`); queda en corrección de Luna, no se debilita la assertion.
- Perfil/fingerprint + CanonicalWriterArchitectureTests: 32 PASS / 0 FAIL /
  0 SKIP, 0.984 s. Esta ejecución no certifica el bloque de autoridad.
- Segundo checkpoint tras corrección de Luna: build 0 errores / 0 advertencias,
  14.51 s; los mismos grupos más ModeAuthorityContractTests: **39 PASS / 0 FAIL /
  0 SKIP**, 1.126 s. El wire conserva `standard` y se rechaza origen automático
  no implementado; estas focales no acreditan los once criterios completos.

Reproducción de la segunda ejecución:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*Architecture*' -class '*EffectiveAgentProfileTests' -class '*ReusableAgentProfileTests' -class '*RuntimeTurnFingerprintFactoryTests' -class '*RuntimeFingerprintContentTests'
```

**El objetivo sigue completo y abierto:** falta resolver la configuración User
en los callsites normales de Lane/AgentExecution y verificar reopen/resume del
perfil efectivo, integrar autoridad en fingerprint y aceptar todos los criterios
de ADR0046/0047. También falta suite integral posterior y evidencia real M5.
No se declara cerrado el hito por estas pruebas. Los techos del scheduler están
asignados a M6 por ADR0047 §4; M5.5 debe admitir transiciones solo con autoridad
válida y rechazar/diferir capacidades no implementadas, sin simular enforcement.

## Configuración reutilizable implementada — 2026-10-07 06:55 UTC

Root implementó `AgentProfile`/`AgentProfileRegistry` en Abstractions, donde vive
el `ToolId` canónico, sin invertir las dependencias del Domain. El perfil tiene
identidad de configuración explícita, nombre, revisión positiva, PermissionScope
techo y preferencias de tools ordenadas. Copia defensivamente todas las listas,
incluidos los patrones argv de cada regla; no contiene identidad de ejecución.
El registro no sustituye silenciosamente revisiones duplicadas ni fabrica un
perfil para una identidad desconocida.

`AgentProfileConfiguration.Load` carga un documento YAML explícito de scope User:
`agentProfiles` contiene entradas por nombre con `id`, `revision`, `permissions`
y `preferredTools`. Todos los campos de permissions (`reads`, `writes`, `process`,
`network`, `secrets`, `allowShell`) son obligatorios. Vacío explícito es distinto
de ausente; campos desconocidos, tipos incorrectos y valores incompletos fallan
cerrado. Este helper no prueba por sí solo el origen de un archivo: el Host debe
invocarlo únicamente con la configuración User confiable, nunca con contenido
del modelo o del workspace presentado como User.

El fingerprint acepta opcionalmente el perfil resuelto. `agent.profile` v2 guarda
la configuración efectiva completa en JSON canónico (incluyendo orden de reglas
y preferencias), usa la misma preparación/redacción/publicación CAS y rechaza
un ProfileId de Lane discrepante. Sin definición resuelta conserva exactamente
el componente v1 existente; no hereda una definición de un fingerprint previo
ni reescribe fingerprints históricos. Es un techo, no un grant, y la lista de
preferencias no es un allowlist.

Evidencia root, fixtures offline sin llamadas a proveedores:

- Abstractions/Domain: build 0 errores/0 advertencias, 3.64 s.
- Checkpoint coordinado con Luna: build de tests 0 errores/0 advertencias, 34.85 s.
- ReusableAgentProfileTests + RuntimeTurnFingerprintFactoryTests +
  RuntimeFingerprintContentTests: **21 PASS / 0 FAIL / 0 SKIP**, 0.381 s.
- Logs: `C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/profile-*`.

Reproducción:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*ReusableAgentProfileTests' -class '*RuntimeTurnFingerprintFactoryTests' -class '*RuntimeFingerprintContentTests'
```

**Criterio integral todavía abierto:** falta resolución/selección normal al crear
Lane y AgentExecution, aplicar el PermissionScope techo en el Permission Engine,
aplicar preferencias al ToolPlan y conservar configuración efectiva durante
resume con pruebas Host/CLI/journal. El helper y las focales no acreditan ese
wiring, la aceptación de ADR0047, la suite completa posterior ni consultas
autenticadas/consumo real de cualificación. La última full (b2ac3fe) precede este
bloque: 2637 casos = 2633 PASS / 0 FAIL / 4 SKIP por permisos symlink.

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
