# Plataforma de extensiones — validación Windows y Debian, 2026-10-21

Repositorio único: OmniCore. Rama `codex/extensions-platform`, base
`b81b3c212ad51a4e085b8f2dce8aed79ec80b454`. Cambios locales, sin merge ni push.
No se modificó OmniCoder ni se cambiaron credenciales/configuraciones reales.

## Alcance y componentes

Se ampliaron el manifiesto, catálogo, consentimiento por hash y consumidores M8.
La instalación y administración viven en `ExtensionPackageManager`; el host v2
en `SupervisedExtensionProcess` y `OmniServer.Extensions`. Los v1 conservan su
invocador Strong. No se alteró el grafo de referencias ni se introdujo un segundo
runtime de herramientas/hooks. `ExtensionViewProjection` reutiliza el modelo de
widgets Client existente. Permisos, journal, artifacts y snapshots siguen en Core.

Contratos públicos: `OmniCore.Protocol/ExtensionProtocol.cs`, SDK TypeScript en
`sdk/extensions`, schema JSON y ejemplo compilable con herramienta/command/vista.
API de extensiones `1.0`, manifiesto nuevo `2`, conservando manifiestos `1`.
Los clientes futuros deben negociar `query.extensionPlatform`; guía completa en
[integración de clientes](../extensions/client-integration.md).

## Evidencia ejecutada

- Compilación completa de `OmniCore.slnx`: correcta, **0 advertencias, 0 errores**;
  incluye los destinos net8/net10 de las bibliotecas compartidas.
- Regresión focal Core, seguridad de extensiones/M8, hooks, MCP, Workspace Panel,
  stdio y alias humanos: **147 correctas, 0 fallos, 0 omisiones**. Resultado generado
  en `TestResults/extensions-platform.xml`. No es una pasada de toda la suite del repo.
- Tras los últimos ajustes del árbol compartido, DTOs y presentación: **91/91**
  focales repetidas sobre el árbol final (`TestResults/extensions-final.xml`). Son
  un subconjunto de la regresión anterior, no 91 casos adicionales distintos.
- Arquitectura: **62/62**.
- SDK TypeScript: compilación + **3/3** sobre un Node real. Negociación, vista,
  herramienta, hooks previos/posteriores/observadores y cancelación cooperativa.
- TUI con driver DOTNET real y servidor/journal reales: **1/1**. Inspección de
  paquete, confirmación, habilitación persistida, catálogo y apertura de los tres pilotos.
  Archivo generado: `TestResults/extensions-tui.xml`. Prueba de cableado, no aceptación
  visual humana de contraste o disposición en todas las terminales.

Las primeras pasadas detectaron y corrigieron una selección inicial ausente en
el menú, datos `null` confundidos con arrays y una aserción que buscaba contenido
en un evento que sólo conserva el summary cuando no hay CAS configurado. No se
dan esas pasadas fallidas por aprobadas; las cifras anteriores son posteriores.

Casos adicionales construidos y comprobados en las focales:

- Instalar no ejecuta scripts; declarativos sin proceso permanente ni dependencia Node.
- Paquetes incompatibles/desconocidos, ZIP traversal, dependencias ausentes,
  alteración posterior de hashes y restricted sin aislamiento se rechazan.
- El wire genérico no puede instalar/habilitar. El proceso no puede invocar un
  ejecutor arbitrario ni cambiar identidad/capacidades/Session mediante parámetros RPC.
- Caída, bloqueo y stdout inválido/excesivo fallan sin derribar Core; cancelación
  cierra el árbol y no reintenta automáticamente resultados externos desconocidos.
- Revocar mediante el consentimiento M8 existente también revoca el acceso v2.
  El vigilante de concesión cierra procesos inactivos sin esperar otro RPC.
- Configuración separada por extensión y workspace; revisiones incrementales,
  detección de gaps y snapshots; forma declarativa tipada C#/TS y cuatro idiomas.
- Herramienta y hook TypeScript ejecutados por los consumidores M8 reales, con
  política controlada de pruebas. No se atribuye una inferencia LLM a estos casos.
- Time Machine usa checkpoints/Git/SQLite reales: corte sin resultados futuros ni
  tool pairs huérfanos, alternativa con archivos/conversación, checkout original
  sucio intacto y rollback ante fallos antes/después de crear la Session derivada.

## Recorrido manual de proceso, sin UI

CLI real, directorios de datos/configuración aislados bajo:
`%TEMP%/omni-extension-final-3f895715ae6a4323837c1864d1a6a8f6`.

Se instalaron/habilitaron los tres pilotos y un paquete autocontenido con el
ejemplo compilado (dist + schemas + manifiesto). Filetree devolvió `tree/ready`
con 20 entradas del repositorio real. Agent Flow informó Session no disponible
sin inventar agentes; Historial sin Session no ofrece restauraciones. El ejemplo
Node devolvió su vista `details/ready` con dos filas y una consulta Git real.
La comprobación posterior corrigió en el servicio compartido el uso de slash
final al consultar ignore de directorios (Git 2.54.0.windows.1). El árbol muestra
ahora 24 entradas, incluidos los nuevos directorios `sdk` y `extensions`; un
caso con carpeta nueva, carpeta ignorada y línea vacía en `.gitignore` lo cubre.
El cliente `headless.mjs` negoció con `omni host --stdio` y obtuvo Filetree. Se
deshabilitó y desinstaló el ejemplo conservando su archivo recuperable y datos.
No se hizo login, inferencia, llamada a un proveedor de pago ni gasto de cuota.

## Reproducir

```powershell
npm ci --prefix sdk/extensions --ignore-scripts
npm test --prefix sdk/extensions
dotnet build OmniCore.slnx --no-restore
dotnet run --project tests/OmniCore.Tests --no-build -- -class '*ExtensionPlatformTests' -class '*ExtensionManifestTests' -class '*WorkspacePanelTests' -class '*LifecycleHookDispatcherTests' -class '*McpBridgeTests' -class '*StdioTransportTests' -class '*HumanCommandAliasTests' -parallelMode none -noLogo
dotnet run --project tests/OmniCore.ArchitectureTests --no-build -- -noLogo
dotnet run --project tests/OmniCore.Tests --no-build -- -method '*Extension_packages_preferences*' -noLogo
```

Uso humano e instalación de pilotos: [README del SDK](../../sdk/extensions/README.md).
TUI: F4 → Extensiones y habilidades → Paquetes y vistas. No se instalan pilotos
en el perfil real del usuario automáticamente.

## Límites explícitos

- Validación local Windows/.NET 10 y Node LTS 24.19.0; continuación también en
  Debian/WSL2 x64, .NET 10.0.401 y Node LTS 22.23.3. macOS no ejecutado.
  Se añadió la preparación SDK/Node al CI Windows/Ubuntu;
  eso no acredita que ese CI remoto ya se haya ejecutado.
- V2 ejecutable inicialmente User-scoped y **confiable, sin sandbox**. Restricted
  se rechaza; no se degrada a User/Basic. Las guardas Strong de v1/MCP no se relajan.
- El código confiable puede usar las APIs del sistema operativo con los accesos
  del usuario; el SDK no impone confinamiento de archivos/red. Las fachadas y los
  comandos Core sí aplican sus guardas. No confundir ambas garantías.
- La TUI ofrece lista/detalles, árbol diferido, diff y, tras el cierre de esta
  entrega, formularios e indicadores nativos. Un cliente que no anuncie un tipo
  recibe disponibilidad explícita y conserva el contrato headless. No hay HTML
  ni gráficos arbitrarios aportados por la extensión.
- Eventos usan stream/replay M9 existente; refresh de vistas es por consulta y
  deltas asíncronos cada dos segundos en la TUI abierta. No se implementó un
  watcher Git/FS ni un bus de eventos paralelo. El SDK ofrece pull explícito de
  actividad con cursor/gap; no se anuncia como push.
- Alternativas recuperan conversación y archivos capturados, no bases de datos,
  servicios ni efectos externos. Las mutaciones generales no tienen un atajo RPC.
- Actualizar/revertir conserva datos y archiva el paquete anterior; valida antes
  de reemplazar y recupera publicaciones interrumpidas sin habilitar código.
  La versión resultante requiere nuevo consentimiento. No hay migración automática/reversión de
  efectos externos. Marketplace, remoto y HTML arbitrario permanecen fuera.

Licencias y atribuciones inspeccionadas: [avisos de terceros](../../THIRD-PARTY-NOTICES.md).
Esta entrega no cierra por sí misma las aceptaciones históricas pendientes M8/M9.

## Continuación: recorridos funcionales y recuperación

- `sessions.current` y `activity.read` están cableados a read models existentes,
  con autoridad y Session del proceso. Actividad paginada (128 filas/128 KiB),
  cursor/high-water, rechazo de cursor futuro y gap cuando el corte de los últimos
  300 eventos no permite completar el intervalo. SDK `activity.watch` es pull,
  con consumo secuencial, cancelación y sin cola de eventos acumulada.
- Agent Flow usa `laneState`, objetivo, modelo, última herramienta y tokens reales
  disponibles; celdas/columnas tipadas y detalles legibles. Metadatos de capacidad
  y heartbeat no duplican las lanes en el payload. Datos ausentes no se inventan.
- Refresh TUI asíncrono de la vista abierta: agrupación, conservación de selección
  y orden, descarte de resultados de otra sesión/overlay y cancelación al cerrar.
  Los deltas incluyen `itemOrder`; un orden inválido no corrompe la caché Client.
- Time Machine: clics reales de seleccionar checkpoint → instrucción → crear →
  abrir. El runtime de destino revalida el recibo publicado, Session y origen antes
  de volver al compositor. Se comprueba por separado conversación importada,
  archivos recuperados y checkout original sucio intacto.
- Lifecycle: `update` y `rollback` por CLI y Preferencias; identidad/hashes,
  compatibilidad, dependencias exactas y archivo recuperable verificados. Intent
  durable y recuperación tras interrupción de publicación; grants revocados y
  versión resultante deshabilitada. No se migran datos ni se revierten efectos.

Fallos detectados y reparados durante esta continuación: referencia IL indirecta
CLI→Domain introducida por un serializador demasiado amplio (se redujo el DTO,
sin relajar el test de arquitectura), limpieza de pools/objetos Git de sólo lectura
en la fixture Windows y contador de arranques que confundía cambios normales de
Session con reinicios por caída. No se ocultaron como pruebas omitidas.

### Resultados de la continuación

| Entorno | Comprobación | Resultado |
|---|---|---|
| Windows, .NET SDK 10.0.401 / Node 24.19.0 LTS | Build solución | 0 warnings / 0 errores |
| Windows | Platform + WorkspacePanel + Manifest + aliases + hooks M8 + MCP | 100 PASS / 0 FAIL / 0 SKIP |
| Windows | Arquitectura | 62 PASS |
| Windows | TUI real con driver controlado: administración, refresh, alternativa | 3 PASS |
| Windows | SDK compilado / procesos Node | 4 PASS |
| Debian WSL2 x64, .NET SDK 10.0.401 / Node 22.23.3 LTS | Build solución | 0 warnings / 0 errores |
| Debian | Platform + WorkspacePanel + Manifest + aliases | 73 PASS / 0 FAIL / 0 SKIP |
| Debian | Arquitectura | 62 PASS |
| Debian | Los mismos tres recorridos TUI con driver controlado | 3 PASS |
| Debian | SDK compilado / procesos Node | 4 PASS |

No sumar subconjuntos solapados de pasadas anteriores. Las pruebas de procesos
ejecutan Node/Git/Core reales; los proveedores y datos de sesión son fixtures
aisladas, sin inferencia de pago ni credenciales del usuario. El driver TUI comprueba
clics/selección/cableado, no aceptación visual humana.

Reproducción Windows (compilar primero el SDK como se indica arriba):

```powershell
dotnet build OmniCore.slnx --no-restore -v:q
dotnet run --project tests/OmniCore.Tests --no-build -- -class '*ExtensionPlatformTests' -class '*WorkspacePanelTests' -class '*ExtensionManifestTests' -class '*HumanCommandAliasTests' -class '*LifecycleHookDispatcherTests' -class '*McpBridgeTests' -parallelMode none -noLogo -result-xml TestResults/extensions-continuation.xml
dotnet run --project tests/OmniCore.Tests --no-build -- -method '*Extension_packages_preferences*' -method '*Extension_view_refreshes*' -method '*Time_machine_clicks*' -parallelMode none -noLogo -result-xml TestResults/extensions-tui-continuation.xml
dotnet run --project tests/OmniCore.ArchitectureTests --no-build -- -noLogo
```

Debian se ejecutó en copia aislada `/root/.cache/omni-extensions-GETqyh`, no sobre
los `bin/obj` de Windows, con los cuatro primeros filtros de la focal anterior.
Resultados locales: `TestResults/extensions-continuation-linux.xml` y
`TestResults/extensions-tui-linux.xml` dentro de esa copia. SDK: `npm test`.

En ese corte aún no se había ejecutado la suite integral ni el CI remoto; tampoco
aceptación visual humana, un aislamiento restringido nuevo o un ejecutable de otro lenguaje.
Las mutaciones generales continúan en el pipeline M8; no se expuso un atajo RPC
de escritura/ejecución por completar una lista de métodos. El cierre siguiente
añade los renderizadores de formulario/indicador y la regresión integral.

## Cierre de formularios, indicadores y transporte humano

- Controles nativos de texto, decimal localizado, booleano, opción y sólo lectura;
  navegación por teclado y desplazamiento mediante los controles existentes.
- El borrador sobrevive al refresh y puede descartarse explícitamente. Guardar
  valida todos los campos antes de escribir un objeto de configuración propia.
- Scope de workspace/sesión, revisión, hashes y nonce de snapshot revalidados en
  Core; el formulario consumido no admite replay con un command nuevo. Revocar o
  cambiar el paquete invalida la acción. No se exponen callbacks privilegiados.
- El transporte stdio firmado admite `submit-view`, `update` y `rollback`,
  conservando el HMAC humano. Un recorrido real instala/habilita el paquete Node,
  rechaza guardados sin prueba o con otro paquete/sesión, guarda, comprueba el
  rechazo del snapshot consumido, actualiza y revierte la versión.
- Indicadores sobre el renderer Client existente: roles semánticos, estados en
  los cuatro idiomas y valores ausentes explícitos, sin cuotas inventadas.

El test de teclado detectó que un contenedor no permitía enfocar los campos,
aunque las asignaciones programáticas y el guardado funcionaban. Se corrigió el
contenedor y se añadió escritura mediante tecla al recorrido en los cuatro idiomas.
También se reparó la lista de acciones del transporte que aún no incluía
actualización/reversión. Una primera captura heredaba `NO_COLOR`; se regeneró en
RGB y no se publicó la imagen monocroma incorrecta.

### Evidencia visual del driver

[Formulario español 80×25](../images/settings-extension-form-es-80x25.png) ·
[Indicadores inglés 120×35](../images/settings-extension-indicators-en-120x35.png).
Los SVG adyacentes son las celdas RGB reales del driver Terminal.Gui, con Core,
Node y paquete de ejemplo locales. Se rasterizaron e inspeccionaron; no son
capturas del escritorio ni aceptación humana interactiva. La habilidad de control
de aplicaciones excluye automatizar terminales; por eso no se acredita ese recorrido.

### Regresión integral y comprobación final

La regresión integral de Debian WSL2 finalizó: **5,049 PASS / 0 FAIL / 26 SKIP**,
5,075 casos totales, incluidos 62 de arquitectura, en 12 min 32 s. Las omisiones
corresponden a pruebas opt-in de proveedores reales y específicas de plataforma;
no se activaron credenciales, inferencia de pago ni servicios externos del usuario.
Log: `TestResults/extensions-full-linux.log` en la copia aislada de continuación.

Los últimos ajustes de foco y transporte se verificaron después en otra copia
aislada, `/root/.cache/omni-extension-final-6z3wcA`: build sin advertencias/errores,
66/66 Platform + Manifest (incluida interoperabilidad Python), 9/9 recorridos TUI/transporte, 62/62 arquitectura,
8/8 capturas RGB y SDK 4/4. Son subconjuntos solapados; no sumarlos a la integral
como casos distintos. XML y logs `TestResults/extensions-final-*` en esa copia.

Windows: la copia final aislada
`C:/Users/juanc/AppData/Local/Temp/omnicore-extension-final-a49591384ad041bc8402c37c362499ed`
compiló con 0 advertencias/errores y aprobó 65/65 Platform + Manifest,
9/9 recorridos TUI/transporte, 62/62 arquitectura y SDK 4/4. Los logs/XML finales
de ambas plataformas se copiaron a `TestResults/extensions-final-*` del repo para
su consulta. El log de la integral Debian también está allí.

La regresión integral de Windows finalizó: **5,059 PASS / 0 FAIL / 16 SKIP**,
5,075 casos totales, incluidos 62 de arquitectura, en 30 min 15 s.
Log: `TestResults/extensions-full-windows.log`. Las omisiones son pruebas opt-in
o específicas de entorno/plataforma. No se presenta esta integral como ejecutada
sobre el último ajuste de foco/transporte: esos cambios tienen las focales finales
posteriores en ambas plataformas.
CI remoto, macOS, aceptación humana en terminal y aislamiento restringido siguen
sin acreditarse. No se hizo commit, merge ni push.

Se añadió una prueba de interoperabilidad sin Node/SDK: Core instala y habilita
un paquete `kind: process`, verifica la negociación con Python y recibe una vista
real con texto español. Aprobada en Debian; usa shebang Linux y se omite
explícitamente en Windows. No se afirma portabilidad de ese ejecutable ni se
añade otro runtime al producto. Está incluida en la focal Linux de 66 casos,
no en las integrales compiladas antes de añadir esta prueba.

La copia de trabajo original quedó recompilada al finalizar (0 advertencias,
0 errores). Su comprobación final aprobó 65 casos Platform + Manifest con una
omisión explícita de la fixture Python Linux, y los 9 recorridos TUI/transporte
sin omisiones. Los resultados finales Windows del repo corresponden a esta
última ejecución, no a los binarios previos de la integral.

Reproducción del cierre (SDK compilado y luego solución):

```powershell
dotnet run --project tests/OmniCore.Tests --no-build -- -class '*ExtensionPlatformTests' -class '*ExtensionManifestTests' -parallelMode none -noLogo
dotnet run --project tests/OmniCore.Tests --no-build -- -method '*Extension_form_edits*' -method '*Extension_indicator_renderer*' -method '*Extension_forms_updates_and_rollback*' -parallelMode none -noLogo
dotnet run --project tests/OmniCore.ArchitectureTests --no-build -- -noLogo
dotnet test --solution OmniCore.slnx --no-build --no-restore --max-parallel-test-modules 1 --parallel none --output Detailed --no-progress
```

Los filtros de clase y método se ejecutan por separado: combinarlos intersecta
los conjuntos y puede producir cero pruebas, lo cual no se considera aceptación.
