# Fingerprint de hechos efectivos del runtime (bloque parcial M5.5)

La CLI de producción construye el fingerprint con `RuntimeFingerprintFactory` después
de resolver la ruta, el perfil y el harness. La identidad del build es versión/MVID del
assembly Host; la cualificación conserva exactamente su formato previo. El componente
`runtime.build` incluye además el nombre y la versión informativa reportados por el assembly.
No interpreta una versión informativa como un commit confirmado.

Componentes v1, JSON escrito explícitamente y SHA-256:

- `model.descriptor`: descriptor real y selección, incluidos modelo lógico, RouteId,
  presupuesto, modo de herramientas y solicitud de razonamiento.
- `model.profile`: perfil efectivo, formatos/modalidades y traits ordenados por nombre.
- `model.harness`: valores efectivos de la política resuelta.
- `context.policy`: política de materialización, presupuesto y tokenizer utilizado.
- `provider.adapter`: digest de la identidad física de la ruta (endpoint/protocolo/perfil/modelo).
- `runtime.build`: metadatos reales del assembly.
- `tools.plan`: las herramientas realmente visibles tras el filtro del harness/boundary,
  en su orden efectivo; nombres → ToolId, hash del schema, descripción, Source completo,
  versión declarada de Source y flags/riesgo/protección/efecto/tags del descriptor.
- `prompt.template`: ID `ExplorerTurn.SystemPrompt` y hash del prompt efectivo renderizado
  y redactado, no una etiqueta M2/M3 ni el texto del usuario.
- `plan.revision`: PlanId y revisión inicial del Turn; ambos null cuando no hay plan.

Son componentes hash-only: `Content=null`. No se copia el endpoint ni configuración
privada a un artifact o al journal. GC distingue estos digests de las referencias CAS;
si un componente sí contiene `Content`, sigue transitivamente esa referencia y falla
sin barrer cuando el blob falta. La retención conservadora de hashes del payload permanece.

Los fingerprints legacy sin componentes conservan su hash; las simulaciones no se
presentan como configuración real del runtime. Este bloque **no cierra** el criterio
completo: faltan AgentProfile/skills efectivos y la revisión/evidencia de cualificación
explicable, así como otros componentes de ADR-0017 donde estén configurados. La CLI
ya reemplaza `core-tools-1` por el digest del plan de herramientas visible. La versión
de Source es la declarada por el descriptor, no una versión de tool inventada.
Los digests tampoco acreditan contenido CAS explicable cuando `Content` es null.

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
