# M9: selección nativa de modelo y ruta por Session

Estado: construido, cableado y validado localmente; no declara cierre de M9 ni aceptación manual de M8.

## Construcción y auditoría

Core `92f2db4` y OmniCoder `31cb234` conectan catálogo local, binding durable
por Session y consentimiento exacto de ruta al composer y al Turn siguiente.
El contrato está en [ADR-0057](../adr/0057-session-model-selection.md). Las
queries no contactan proveedores ni crean autoridad. Seleccionar no cambia
el techo de seguridad ni los grants o presupuestos de la Session.

El runtime usa el binding también para validar adjuntos antes del primer Run.
Un cambio de identidad del proveedor/ruta bloquea incluso el prompt de texto
antes de admitir input; no hace fallback. Las continuaciones conservan su
modelo original. `d2c4381` corrige una rama específica Codex que podía volver
a imponer la preferencia global al actualizar su catálogo. Este guard se
auditó en fuente; no se hizo una llamada autenticada a Codex para acreditarlo.

Main y UI fijan workspace, Session, VM y generación antes/después de awaits y
justo antes de firmar. El readback tardío no actualiza la caché de otra Session.
La selección normal es directa; autorizar una ruta nueva conserva confirmación
específica con billing y ruta. El setter genérico del adapter sólo sincroniza
un binding canónico ya coincidente; la mutación usa el control nativo firmado.

## Evidencia Core disponible

`28465df` añade **un** caso integrado de stdio real/HMAC/HTTP loopback:
catálogo sin endpoints/credenciales, CAS obsoleto y token de catálogo cambiado,
allow/deny de ruta sin inferencia, aislamiento de Sessions y seguridad
conservada, selección entre Turns de un mismo Run, payload de otra Session
rechazado, reapertura durable, adjunto PNG con selección anterior al Run y
ruta modificada rechazada antes de input/HTTP. Sus múltiples assertions no
se contabilizan como casos independientes. Resultado: **1/1 PASS**.

El foco lifecycle relacionado aprobó **1/1**. El foco de arquitectura CLI
aprobó **1/1** sobre `7029910`, sin ampliar la allowlist. Son ejecuciones
separadas; no representan una suite integral. Log final del caso nativo:
`%TEMP%/m9-core-model-routing-92f2db4-native-focused-fixturefix5-20261017.log`.

Hubo builds fallidos antes: variable local duplicada y routing nullable,
corregidos en producción; después, errores de compilación de assertions de
la fixture, corregidos en tests. La primera ejecución reveló una Session
nueva sin snapshot de rutas, corregida en `92f2db4`. Otras ejecuciones
rechazaron correctamente firmas null usadas para la segunda/tercera Session;
se corrigieron los helpers para firmar el scope activo sin debilitar el Host.
Ninguna de esas pasadas fallidas se acredita como aprobación.

Build incremental final `d2c4381` de tests/Host/CLI: **0 advertencias, 0 errores**,
50,37 s. Comando: `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore`.
Log: `%TEMP%/m9-core-codex-binding-d2c4381-testproject-build-20261017.log`.
No se repitieron los focos verdes por el guard específico Codex; la inferencia
autenticada de esa rama sigue sin validarse.

Comandos focales Core (desde su raíz):

```powershell
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --parallel none --filter-method '*Native_model_selection_is_session_cas_scoped_and_catalog_does_not_expose_route_configuration*'
dotnet test tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-build --no-restore --parallel none --filter-method '*Omni_assembly_references_only_allowed_projects*'
```

## Consumidor y recorrido integrado

Build app final sin errores; cinco workspaces previos compilados en la primera
tanda. SDK **2 PASS / 5 filtrados** y adapter **13/13** sobre `e3d3fbf`, antes
del cambio exclusivo de tres textos de UI en `31cb234`. Dos E2E únicos reales
Electron→Host→HTTP loopback aprobados en ejecuciones separadas: selección
local-b/reapertura/modelo efectivo, y ruta metered con deny, aislamiento de
Session, respuesta obsoleta rechazada, allow y modelo efectivo. No hubo una
tanda combinada 2/2 verde: la selección pasó en una tanda 1 PASS/1 FAIL; el
consentimiento pasó después 1/1. Los fallos restantes fueron de fixtures:
default global confundido con binding explícito, nombre accesible de listitem
y consulta todavía incompleta confundida con una Session diferente.
`NO_ORPHANS` aprobó; root también comprobó ausencia de procesos Electron/Host
de estos directorios/fixtures. Control IPC: 149/149/147, catálogo consistente.
`git diff --check` pasó después de normalizar EOL/espacios del HTML generado,
antes de las E2E. Tests del consumidor: `7d2445b`.

Los comandos exactos y logs del consumidor se registran sólo en
`PLAN_REALISTA.md` de OmniCoder, conforme a sus instrucciones de repositorio.

## Límites del hito

La auditoría posterior encontró que el validator HMAC aún rechazaba revisión
de routing 0 para Sessions legacy sin snapshot, aunque handler y SDK la
admitían. `6ba1e93` corrige sólo select (route.respond sigue exigiendo >=1).
Un caso stdio real sobre journal legacy sin Run aprobó dentro del foco de
skills/memoria posterior, tests `1abfe35`; no se atribuye ese caso al foco
original. La CI de `e0474a5` aprobó Ubuntu/npm/paquetes y falló sólo en Windows
por EOL de la fixture; el [seguimiento CI](../ci-failure-analysis.md) conserva
el diagnóstico y el foco reparado 1/1. OmniCoder `476ce06` tiene CI aprobada.

HTTP loopback acredita cableado y modelo del request; no interpretación visual,
generación o gasto de un proveedor live. La configuración del catálogo no
equivale a disponibilidad remota. No se acredita aceptación multiplataforma,
cambio de idioma en caliente, procesos persistentes/ConPTY, comandos/skills/
memoria nativos restantes ni controladores de plan/lanes/worktrees. M9 y la
aceptación manual M8 permanecen abiertos.
