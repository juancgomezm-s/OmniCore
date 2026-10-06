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

Son componentes hash-only: `Content=null`. No se copia el endpoint ni configuración
privada a un artifact o al journal. GC distingue estos digests de las referencias CAS;
si un componente sí contiene `Content`, sigue transitivamente esa referencia y falla
sin barrer cuando el blob falta. La retención conservadora de hashes del payload permanece.

Los fingerprints legacy sin componentes conservan su hash; las simulaciones no se
presentan como configuración real del runtime. Este bloque **no cierra** el criterio
completo: faltan tools visibles/prompt/plan por Turn, AgentProfile/skills cuando exista
su configuración efectiva, y la revisión/evidencia de cualificación explicable. La
etiqueta legacy `core-tools-1` aún no sustituye una descripción real de herramientas.
Los digests tampoco acreditan contenido CAS explicable cuando `Content` es null.

## Evidencia reproducible

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

Los tests de factory son fixtures offline. El test de CLI recorre dispatcher, Host,
adapter HTTP loopback, journal SQLite y codec real; no es una consulta autenticada,
cualificación de proveedor real ni consumo real. El test comprueba el componente de
ruta física que quedó persistido en TurnStarted, además de ModelStepStarted.

Comandos desde el worktree autorizado:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*RuntimeBuildIdentityTests' -class '*RuntimeFingerprintFactoryTests' -class '*CliEndToEndTests' -class '*FingerprintComponentsContractTests' -class '*ContextPolicyFingerprintTests' -class '*SimFingerprintTests' -class '*ModelQualificationCliTests' -class '*MaintenanceTests' -class '*ExplorerTurnDurableProviderStateTests'
```
