# M9: TUI operativa, imagen real y cierre técnico Linux — 2026-10-29

Estado: candidata funcional completa; inspección inicial sobre `e4d6669e` y base
de integración `2f39002d` (avances npm ajenos al código M9), con las dos
correcciones del consumidor ya validadas. **El cierre de entrega de M9 espera
revisión de Dot y CI de la candidata.** No se trasladan requisitos a M10 ni a otra
fase. Sin workers, cambios visuales, nueva generación, GPU ni instalación.

## Lo que ya puede hacer la TUI

La aceptación real del 28 de octubre recorrió TUI → Explore/Implement/Verify →
disposición Accepted → misma Session en OmniCoder → aplicación exacta del worktree
y reapertura. Sustituye los bloqueos anteriores de paquete/verificación/aplicación,
según [el acta del consumidor](m9-consumer-final-wiring-20261026.md#resolución-de-los-bloqueos-funcionales-2026-10-28).
El hijo de lectura real y su disposición también tienen aceptación independiente.

Para solicitar una modificación normal: abrir `omni` dentro del repositorio,
seleccionar un modelo disponible, modo Act y una categoría de seguridad que permita
el cambio; describir el cambio, los archivos y el criterio de prueba. Plan no
autoriza mutaciones. `!comando` es shell humano sin inferencia; `/orq-auth` es el
workflow explícito con verificador, no un sustituto obligatorio de cada edición.
La instalación inspeccionada apunta a `9b702820`: no se actualizó en esta campaña.

## Imagen real: recepción y recuperación completadas

Se revisó exclusivamente la llamada ya hecha: Session
`01a18153-1c2d-7ad5-8fbe-cf2c337e735a`, modelo
`black-forest-labs/flux.2-klein-4b`, JPEG 1024×1024, 85.916 bytes, costo reportado
y contabilizado USD 0,014 (techo configurado USD 0,05). SHA-256:
`3fde9287a5405f4f4e73a7c65170f1370f65c8643802f233e2a5000202a01829`.

La TUI ya había listado/exportado esos mismos bytes. En esta campaña OmniCoder
vigente `C:/Users/juanc/Documents/JayCoder` abrió la misma Session en **dos procesos
Electron secuenciales**, decodificó a 1024×1024, abrió la vista ampliada y exportó
dos JPEG con el hash y tamaño originales. No se modificaron fuentes, tests ni
bundles de OmniCoder. Perfil gráfico aislado, datos originales del workspace;
se compararon los conteos de eventos por tipo antes/después: se conservaron. El
harness no compara byte a byte el journal ni el contenido de todos sus eventos.
Un endpoint local que
rechaza cualquier solicitud sustituyó la configuración de inferencia sólo en el
perfil de prueba: **cero solicitudes**, sin una segunda generación.

Evidencia nueva: `%TEMP%/omnicore-m9-image-replay-v1Z6Sk/result.json`,
`reopened-1.png`, `reopened-2.png` y exportaciones. Harness de aceptación:
`artifacts/m9-closure-20261029/replay-live-image.cjs` (ignorado).
Los intentos de preparación anteriores fallaron por pedir una acción antes de
Ready y por intentar releer un blob desde el renderer; no se cuentan como pases.
La versión final verifica decodificación visible y exportación canónica desde Host.

El contador histórico `ObservedGenerationSends = 0` no se reescribe. Su causa y
corrección tienen [39 pruebas offline](m9-image-send-counter-20261029.md); ese
resultado no se presenta como una llamada live nueva del proveedor corregido.

## Defecto Linux encontrado y corregido

El recorrido de `process.start/read/send_input/stop` tenía una omisión de Windows
en su prueba integral. Al habilitar Strong Linux con shell real, el inicio,
lectura y entrada pasaron; `process.stop` falló. La reproducción aislada mostró
`InvalidOperationException: No process is associated with this object`:
el monitor llamaba `DisposeAsync` tras observar la salida mientras `TerminateAsync`
todavía intentaba hacer `Process.WaitForExitAsync` sobre el handle cerrado.

El control Linux conserva una única observación de salida y su exit code,
serializa el acceso a Process con su disposición y hace idempotentes parada y
liberación. No se oculta un fallo del SO ni se convierte Unknown en éxito. Una
regresión determinista exige conservar exit 35 y admitir stop después de Dispose;
la prueba integral conserva todos sus receipts, cinco controles, tres solicitudes
al proveedor fixture, salida final Unicode, RunCompleted y consultas pasivas.

Validación final Linux sobre los mismos ensamblados:

- **18 PASS, 0 FAIL, 0 SKIP:** 12 casos Linux batch/lifecycle y seis recorridos
  M9 (proceso nativo, lease entre procesos/muerte del dueño, ACK perdido y lookup,
  stream/replay/backpressure, cuatro idiomas y snapshots de seguridad/reapertura).
- **4 PASS, 0 FAIL, 0 SKIP:** shell humano sin inferencia para ejecutarlo ni
  incorporación automática al contexto; importación mediante dos Hosts OS;
  selección de modelo aislada por Session; grants Run/Workspace y techo efectivo.
- El probe que reprodujo la carrera pasó después 12 iteraciones; es diagnóstico,
  no doce pruebas adicionales. Windows Strong/Weak nativo **2/2** antes del ajuste
  exclusivo del control Linux; Sandbox net8/net10 recompilado después: 0W/0E.

Reproducción conjunta: `sh scripts/validate-linux-m9.sh`, wrapper del build único
y keyring aislado de `validate-linux-m1-m8.sh`. Agrupa exactamente los 22 casos
validados en las dos tandas anteriores, sin repetir el build entre ellas.
XML de esta campaña `focused-tests.xml` y
`m9-native-contracts.xml` en
`/home/omnicode-ci/OmniCore-linux-20261014/artifacts/m1-m8-current-main-20261029`.
Windows: `artifacts/m9-closure-20261029/windows-process-controls.xml`.
Los proveedores de estas pruebas son fixtures HTTP, no modelos locales/remotos.
PTY Linux/macOS y Weak persistente sin ownership equivalente mantienen los límites
ya fijados en ADR-0038; no se anuncian como capacidades construidas por estos pases.

## Resolución de los dos pendientes del consumidor

Juan autorizó expresamente corregir ambos pendientes el 29 de octubre, sin
cambios visuales ni workers, y publicar tras aprobación de Dot y CI verde. En
JayCoder `ba5dc22` se cambió sólo la lógica del estado/composer, helpers pasivos y
sus pruebas; el JSX/CSS/layout permanece igual. `session-security-query` reintenta
sólo errores transitorios de lectura con cota y guardas de identidad; no reenvía
commands, no elige permisos y no reutiliza una policy de otra Session/generación.
El importador conserva el código preciso del Host y lo traduce en las cuatro
lenguas de usuario. No se sustituyó por un error genérico ni se omitió el positivo.

Build de app EXIT 0; **205 unitarias PASS**, incluidas 12 nuevas. Los E2E existentes
de seguridad A/B/reconexión, importación Imported/ObserveOnly/reapertura y control
durante inferencia pasaron **3/3** contra Core real. Imagen live anterior reabierta
dos veces más sobre ese bundle: hash exacto, cero requests y conteos de eventos
por tipo inalterados (sin afirmar integridad byte a byte del journal).
Fronteras IPC/arquitectura y ausencia de workers huérfanos: EXIT 0.
Estado único del consumidor y comandos en `JayCoder/PLAN_REALISTA.md`,
`OC2-SEGUIMIENTO → Cierre funcional M9: seguridad e importación`.

Evidencia final: `%TEMP%/omnicoder-m9-security-cmk13e/`,
`omnicoder-m9-import-f1rIud/`, `omnicoder-m9-security-D7GtOR/` y
`omnicore-m9-image-replay-Gqw8f0/result.json`. No se suman estas repeticiones a las
pruebas históricas para inflar casos únicos.

Los `result.json` de los tres E2E y las capturas/exportación visual de la imagen
también se conservaron en
`artifacts/m9-closure-20261029/consumer-e2e/{security,import,busy,live-image}/`.
El campo histórico `journalUnchanged` del reporte de imagen significa únicamente
que coinciden los conteos por tipo; no se reescribió esa evidencia original.

## Gates de entrega, sin reabrir los bloques aceptados

| Pendiente | Evidencia y acción necesaria |
|---|---|
| Recuperación de seguridad de OmniCoder al alternar Sessions | **Corregida y aceptada localmente**, según la sección anterior; el repro rojo original se conserva en `%TEMP%/omnicoder-m9-security-3jtkQA/`. |
| Importación y mensaje localizado | **Corregidos y aceptados localmente** con positivo y reapertura, sin inferencia/efectos. El repro rojo original se conserva en `%TEMP%/omnicoder-m9-import-nBxejD/`. |
| Revisión y entrega de la candidata | Dot recibió el informe mediante `hostId: durable`. El main remoto `e4d6669e` tiene [CI 5/5 aprobada](https://github.com/juancgomezm-s/OmniCore/actions/runs/40955153944), incluidos Windows, Ubuntu, npm ambos OS y paquete Linux. No acredita el diff local de credenciales, aislamiento, lifecycle o contador de imágenes. Falta dictamen de Dot y CI de la candidata autorizada antes de publicar/instalar. |

La solicitud final de aprobación después de corregir el consumidor no pudo
iniciar el chat de Dot: `send_message_to_thread` devolvió
`invalid turn/start params: AbsolutePathBuf deserialized without a base path`.
Se conserva el encargo completo en
`artifacts/m9-closure-20261029/dot-review-request.txt`; no se sustituyó el dictamen
por una aprobación propia ni se publicó para saltar ese requisito.

Dot emitió después un **dictamen focal favorable, sin P0/P1 identificado**, con
condiciones previas al cierre: candidatas completas por SHA, CI nueva en ambos
repositorios, paquete Linux/AOT posterior al último ajuste de lifecycle,
conservación de los tres E2E locales y precisión sobre la medición del journal.
Ese dictamen no es aprobación final de publicación. Se conserva la autorización
humana vigente y el bloqueo de main hasta cumplir los gates y recibir el dictamen final.

El paquete Linux se volvió a construir **después** de la corrección de lifecycle,
en `package-lifecycle-final/` dentro de la evidencia Linux persistente. Self-contained
y Native AOT terminaron sin warnings y cada uno pasó seis escenarios de CLI,
recovery/journal, decoder WebP y dos PTY reales. SHA-256 de `omni`:

- Self-contained: `2cee859b647f2b398db2c5e5316c90b4af8e80f0815bd201770cabe725c0c799`.
- AOT: `28d9eb10091180abe09328b82c5c15652b1cf080f02910c51b8976f9a1d4766d`.

Esto acredita la pasada local posterior al ajuste; la CI del commit inmutable
final debe acreditar exactamente la candidata completa, no los otros cambios en
curso del workspace compartido. Esos cambios ajenos no se incorporan a esta entrega.
El consumidor completo quedó fijado localmente en
`05d540a3e535093bb628ffd5ccfac5027881f98e`, incluidos nuevos archivos y bundles;
el CSS es un rename R100, mismo blob Git. No se modificó main al fijar la candidata.

El tercer E2E existente, `native-control-busy-composer`, pasó: supervisión humana
desde el composer durante inferencia fixture sin una llamada adicional. Los rojos
originales se conservan por separado; los tres pases finales no los borran.
M8 Windows está aceptado por Juan. M2/M3 reales siguen pospuestos por ocupación de
la GPU y no se usan como un bloqueo nuevo de M9.
