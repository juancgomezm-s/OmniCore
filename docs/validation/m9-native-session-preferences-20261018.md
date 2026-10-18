# M9-L: skills y memoria de Session desde OmniCoder

Estado: construido, cableado y validado localmente en Host y consumidor.
M9 sigue abierto. M8 espera la aceptación manual del usuario.

## Construcción

Core `716bb37` + `6ba1e93` + `619353e` reutilizan los servicios de skills y
memoria existentes con frontera nativa HMAC/CAS. El contrato está en
[ADR-0058](../adr/0058-native-session-skills-memory.md). Queries puras de
skills y memoria llevan identidad/revisión; la activación fija manifest y
contenido de paquete, y la liberación usa la activación durable exacta aun
con fuente ausente, ambigua o sin trust actual. La revisión incluye
supresiones automáticas. Activar no concede permisos de tools.

Memoria add/propose, forget, promote y reject usan sólo la Session actual.
Core deriva la interacción de conflicto; el cliente no elige la memoria que
reemplazar como autoridad. Add Accepted indica política procesada: puede
producir registro, candidato pendiente o rechazo por privacidad. No equivale
a guardar un registro. Se conservan redacción y límites existentes.

Mutaciones se serializan bajo la barrera de autoridad y bloquean con Deferred
si hay Turn abierto o efectos/hijos inciertos. El contrato trusted in-process
legacy se conserva; stdio exige payload completo y proof del scope exacto.
Promote/reject no reanudan un Turn abierto: falta el consumidor de continuación
MemoryPromotion. No se simula reanudación con otro prompt.

OmniCoder `cc057ba` conecta SDK/Main/preload/Bridge/panel existente, con
comandos tipados y revalidación de workspace, Session, VM y generación antes
de firmar y aplicar readback. El readback distingue registros/candidatos y
permite liberar activaciones huérfanas. Queries legacy siguen legibles; sin
revisión/hashes válidos no se ofrece la mutación nueva.

## Validación disponible

Tests Core `1abfe35`: **13 PASS, 0 FAIL, 0 SKIP** (8,475 s). Incluyen dos
casos stdio reales: migración de selección de modelo sobre journal legacy
sin routing/Run; y skills/memoria con HMAC, CAS, receipts/repetición,
aislamiento, hash de paquete cambiado bajo manifest igual, fuente ambigua y
ausente, reapertura, add/forget y privacidad sin texto sensible persistido.
Los otros casos son Host/preferencias/presentación, incluidos los payloads
legacy y dos nuevos casos de decoder. No son trece casos nuevos de stdio.

Desde la raíz Core:

```powershell
dotnet test --project tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --parallel none --output Detailed --no-progress --filter 'FullyQualifiedName~Native_model_selection_initializes_a_legacy_missing_routing_snapshot_with_revision_zero|FullyQualifiedName~Native_session_skills_and_memory_use_hmac_cas_isolation_and_durable_source_identity|FullyQualifiedName~SessionPreferencesHostTests|FullyQualifiedName~SessionPreferencesPresentationTests'
```

Log `%TEMP%/m9-session-preferences-core-619353e-v2.log`. La primera invocación
con sintaxis posicional anterior compiló pero ejecutó **0 tests, exit 5**;
no se cuenta como aprobación ni hay resumen independiente de advertencias.
La compilación posterior con el runner correcto para reparar EOL de modelos
pasó su foco 1/1; tests `eb03d4a`, log
`%TEMP%/m9-native-model-routing-windows-eol-fix.log`. Esa corrección está
separada del bloque nuevo y del diagnóstico de CI en
[su informe](../ci-failure-analysis.md).

El primer build Coder compiló los cinco workspaces iniciales y falló en el
typecheck de app. Se corrigieron discriminantes, scope y valores nullable;
el build app sobre `7950a7c` aprobó (9,48 s). No se presenta el primer build
completo como aprobado. IPC root aprobó **151 main / 151 preload / 149 Bridge**
en 37 archivos; el catálogo documentado coincide. El scanner falló antes
porque faltaban dos declaraciones de Bridge y se corrigió la fuente.

## Auditoría y aceptación local

Se corrigieron el estado ficticio de candidato `Pending` frente al canónico
`ask`, el límite de ID de skill, decoder legacy y bloqueo incondicional de
estado Released aun con paquete actual verificable. Core conserva el rechazo
de fuentes no elegibles y la UI no convierte un ACK en guardado o inferencia.

Quiescencia aprobó **1/1** adicional: después de cerrar Host, la fixture
siembra un Run/Turn abierto válido en el journal, reabre el proceso y envía
memory.session.add con HMAC. Exige busy/Deferred, receipt canónico y cero
cambios de eventos, revisión o registros. No es inferencia interrumpida en
vivo; es aceptación conservadora de un Turn abierto tras reinicio. Log
`%TEMP%/m9-session-memory-open-turn-core-test-r4.log`. Dos intentos anteriores
fallaron por fixtures: RunCreated ausente y seeding con Host/lease vivo;
se corrigieron sin modificar producción. Este caso no se suma varias veces.

Coder tests `13ed154`: SDK **1 PASS / 94 filtrados** y Electron **1 PASS /
76 filtrados** (10,25 s), ambos sobre el consumidor final `cc057ba`.
La UI activa skill, agrega memoria, conserva un conflicto y descarta el
siguiente; el request HTTP local contiene instrucciones y memoria que no
están en el prompt. Tras liberar/olvidar, otro Turn ya no contiene esos
marcadores. No hubo errores renderer. El SDK acepta el diagnóstico real de
skill inválida con hash vacío como null y no permite activar esa fuente.
El build app-only final aprobó; HTML normalizado antes de probar el bundle.
`Assert-NoOrphanWorkers.ps1` y la comprobación adicional de procesos Host/Electron
por rutas de las fixtures aprobaron. La evidencia y comandos Coder se conservan
en su `PLAN_REALISTA.md`, según sus instrucciones de repositorio.

Logs `%TEMP%/m9-session-skills-memory-app-cc057ba.log`,
`m9-session-skills-memory-sdk-cc057ba-r2.log`,
`m9-session-skill-memory-e2e-cc057ba-r4.log` y
`m9-session-skills-memory-no-orphans.log`. Los intentos previos E2E corrigieron
únicamente fixtures: el helper cambiaba el workspace después de crear el skill,
y un selector de memoria coincidía también con el input. No se contaron como
aprobados ni motivaron regresiones amplias. La quiescencia está en `ebc7d8f`.
La publicación y CI remota se registran por separado; los focos locales no
acreditan la matriz remota.

## Límites del hito

No incluye command.invoke, controles plan/lanes/worktrees, procesos
persistentes/ConPTY, continuación MemoryPromotion ni de Lanes hijas, memoria
Project/Global M10 o aceptación live/manual de medios/idiomas/plataformas.
Los fixtures locales no acreditan inferencia de la cuenta del usuario.
