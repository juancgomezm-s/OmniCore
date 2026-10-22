# Ampliación M8: plataforma de extensiones (2026-10-21)

## Aislamiento obligatorio autorizado (2026-10-22)

Cierre anterior conservado en `78808c1`. Un solo agente, sólo OmniCore, sin publicar
ni fusionar automáticamente. Las extensiones declarativas siguen sin proceso.
Todo ejecutable v2 debe arrancar en aislamiento fuerte: nunca se usa una concesión
"trusted" como fallback libre. Si el backend no está disponible, no arranca.

Orden: (1) ampliar el lanzador compartido de Windows con stdio bidireccional;
(2) supervisor v2 sobre un adapter de Execution, paquete/runtime en imagen de
lectura y datos privados de escritura, sin montar workspace/configuración/secretos;
(3) permisos y protección efectiva dentro de Preferencias existentes; (4) backend
Linux de namespaces con política cerrada y pruebas adversariales en ambas plataformas.
Core conserva la única autoridad sobre los servicios y el consentimiento de contexto.

La instalación no ejecuta código. La nueva política exige reactivar paquetes
ejecutables que estaban habilitados con la política libre anterior. Los permisos
de filesystem/red del manifest no son montajes ni acceso de SO: los servicios de
Core siguen comprobando capacidad, scope y política. No se modifica el sandbox de
los procesos nativos/build de ADR-0038 ni se duplica el runtime de herramientas.

Aceptación: RPC real bidireccional; intento de escritura del paquete/Core y lectura
de secretos rechazados; red/loopback denegados; hijos contenidos y terminados al
cerrar; ausencia de backend sin ejecución libre; contexto siempre sujeto al cierre
anterior. Registrar capacidades y límites reales, no sólo un arranque correcto.

Implementado y validado en `docs/validation/extensions-isolation-20261022.md`:

- [x] Transporte duplex y LPAC/Job en Sandbox, sin romper batch v1.
- [x] Adapter en Execution y supervisor v2 sin arranque libre; imagen privada y
  servicios concedidos por Core, con cancelación y limpieza.
- [x] Política versionada, reactivación de permisos antiguos y protección en
  Preferencias existentes; textos es/en/fr/it y flag histórico sin bypass.
- [x] Backend Linux Bubblewrap; RPC, negativas adversariales, interoperabilidad
  Python y stdio real con los pilotos/contexto. Arranque como UID no privilegiado.

No amplía el soporte Strong de procesos/build ni del invocador M8 v1 en Linux.
macOS ejecutable, cuotas duras de recursos y un almacén privado de blobs persistentes
no forman parte de este cierre; permanecen explícitamente fuera de sus garantías.

## Cierre autorizado: contexto protegido y cuatro pendientes

Solicitud del usuario: ninguna extensión incorpora contexto sin autorización
humana explícita ni modifica las instrucciones de Core. Esta autorización es
independiente de instalar, habilitar o confiar en un paquete.

Orden y componentes afectados, conservando la publicación M9 ya integrada:

1. Host/Protocol/Client/CLI/TUI: previsualizar y aprobar un lote exacto de contexto,
   ligado a workspace, Session/Run, contribuidor y hashes. Denegado por defecto;
   revocación y cambio de paquete invalidan la aprobación. Contributors y el
   request final nunca elevan texto externo a `Instructions`; lo aprobado viaja
   como datos externos identificados, sin alterar el mensaje original del usuario.
2. Host/SDK/TUI: notificaciones literales acotadas, con capacidad propia, origen
   visible y sin conexión con el contexto del modelo.
3. Proyección compartida/Protocol/SDK: conservar la identidad de extensión y la
   correlación disponible de herramientas en los eventos de actividad públicos.
4. Cliente independiente: aceptar los tres pilotos por stdio con Session/Run,
   crear/reabrir una alternativa y comprobar conversación, archivos y original intacto.
5. C#/TypeScript/JSON Schema: alinear restricciones y comprobar el mismo corpus
   válido/inválido en Core y el esquema público.

Estado de este cierre: **implementados los cinco puntos**, incluidos los cuatro
pendientes de la plataforma. Preferencias ofrece previsualizar/autorizar/revocar;
el Host comprueba otra vez el lote exacto antes del envío. Los hooks conservan su
procedencia de extensión y no pueden ocultarla al normalizar una anotación.
`toolcall.started` v4 conserva atribución con lectura compatible v1/v2/v3; avisos
UI tienen capacidad, límites y deduplicación propios. La aceptación Node firma
acciones humanas contra Core real, rechaza intentos sin prueba y crea/reabre
la alternativa. El corpus compartido tiene 40 casos válidos/inválidos.

Resultados y alcance de las pruebas de este cierre en la sección
«Cierre de seguridad y cuatro pendientes» del
[acta](../validation/extensions-platform-20261021.md). No reutilizar cifras de la
integral anterior como evidencia de este código nuevo.

Verificación proporcional: casos negativos de autorización y escalamiento, salida
final al provider fixture, recorridos reales Node/Core/TUI y headless, contratos y
arquitectura. No se añade como requisito CI remoto, aceptación humana obligatoria,
otro sandbox ni cambios en OmniCoder. No se modifica la política de publicación
existente ni se publica/integra sin una solicitud aparte.

Extensión del roadmap §24, no un hito ni framework paralelo. ADR-0061.

| Estado | Componentes | Trabajo |
|---|---|---|
| Existente | `src/OmniCore.Extensions/ExtensionManifestLoader.cs`, `src/OmniCore.Host/ExtensionConsentStore.cs` | Conservar descubrimiento, hashes y consentimiento v1. |
| Ampliar | `src/OmniCore.Host/ProjectExtensionPackageCatalog.cs`, `src/OmniCore.Cli/Tui/TuiApp.SessionPreferences.cs` | Lifecycle User, inspección y administración integrada. |
| Existente | `src/OmniCore.Abstractions/LifecycleHooks.cs`, `src/OmniCore.Host/ExtensionRpcProcessInvoker.cs` | Hooks con fallo cerrado y ejecución Strong M8; no duplicar. |
| Existente | `src/OmniCore.Host/OmniServer.WorkspacePanel.cs`, `src/OmniCore.Host/WorkspaceSnapshotStore.cs` | Archivos/Git, cronología y alternativas; único dueño de snapshots. |
| Crear | Protocol/Client: contratos de vistas; Host: supervisor v2; `sdk/extensions` | SDK TS, negociación, snapshots/revisiones y servicios acotados. |

Orden: (1) paquetes declarativos/lifecycle; (2) vistas/pilotos y CLI/TUI;
(3) proceso supervisado/SDK y fallos; (4) integración, seguridad y evidencia.
Conservar las aceptaciones pendientes de M8/M9. No atribuir a esta ampliación
validación live de proveedores ni aceptación visual humana.

Aceptación: instalación sin ejecución; permisos por hash y scope; v1 compatible;
pilotos sobre servicios reales; RPC directo sin autoridad rechazado; procesos
acotados, cancelación y cleanup; versiones C#/TS; replay sólo de lecturas.
Registrar resultados reales y pendientes en el acta de validación.

## Entrega funcional construida

- Paquetes User en el root M8 existente: instalación pura desde carpeta/ZIP,
  API/plataforma/dependencias/rutas/colisiones/enlaces/schemas validados; hashes,
  consentimiento exacto, habilitación/revocación y archivo recuperable al desinstalar.
- Host v2 Node LTS separado, negociación, identidad/Session/workspace vinculados,
  límites, cancelación y cierre; adapter a herramientas/hooks/contributors/gates M8.
- Protocol/Client portables: árboles, listas, tablas, formularios, indicadores,
  detalles y acciones; columnas/celdas/campos tipados, revisiones/deltas y reparación
  por snapshot. Adaptación al `ListWidgetModel` existente, no otro registro visual.
- Filetree, Agent Flow y Time Machine declarativos sobre servicios Core reales;
  CLI, Preferencias y cliente headless. No se tocó OmniCoder.

Evidencia, comandos reproducibles y límites de aceptación:
[validación de la plataforma](../validation/extensions-platform-20261021.md).
La modalidad restringida no disponible se informa explícitamente; no se presenta
separación de procesos como sandbox. Los clientes sin renderer para un tipo
concreto reciben una degradación declarada.

## Continuación: recorridos y recuperación

Orden de cierre (sin ampliar la autoridad de las extensiones):

1. Host/Protocol/SDK: consultas acotadas de sesión y actividad atribuible, con
   cursor y aviso de historial incompleto; Agent Flow muestra herramientas y
   métricas existentes, nunca estimaciones de vida o consumo inventadas.
2. Client/TUI: refresh asíncrono agrupado de la vista abierta, conservación de
   selección/orden y cancelación al salir; abrir la alternativa confirmada desde
   el compositor normal con un runtime ligado al nuevo workspace.
3. Lifecycle: preparar actualización compatible antes de sustituir el paquete,
   conservar versión anterior y datos, exigir consentimiento nuevo y recuperar
   publicaciones interrumpidas sin reactivar código automáticamente.
4. Verificar contratos, aislamiento y recorridos TUI/SDK reales. Registrar las
   plataformas ejecutadas por separado; no equiparar build o CI configurado con
   validación multi-OS. Mutaciones generales continúan en el pipeline autorizado
   M8; no se añaden atajos RPC para satisfacer una lista de capacidades.

Construido y verificado en esta continuación: sesión/actividad paginada y SDK
pull; Agent Flow con métricas reales; refresh asíncrono con orden/selección estable;
crear/abrir alternativa con runtime independiente; actualización/reversión de
paquetes e intent de recuperación, sin heredar consentimiento. Builds y focales
Windows/Debian, arquitectura, SDK y tres recorridos TUI aprobados en el acta.
Este corte precede al cierre de formularios/indicadores y la regresión integral
registrados a continuación. CI remoto, macOS y aceptación visual humana se
mantienen como verificaciones independientes; no se confunden con el driver TUI.

## Cierre de presentación y regresión integral

1. Protocol/Client: validar campos, opciones, valores e IDs y ligar los formularios
   a una revisión, identidad de paquete y scope confirmados por Core.
2. TUI: campos editables tipados e indicadores con estado/valor/unidad, navegación
   y desplazamiento; conservar borradores frente al refresh. Guardar únicamente
   configuración propia declarada mediante la acción humana existente.
3. SDK/ejemplo y tests: recorrido real de leer formulario, editar, validar y guardar;
   rechazo de cambios de sesión/revisión/hash, campos inválidos y RPC sin autoridad.
4. Capturar el búfer RGB real para revisión visual y ejecutar la regresión integral.
   CI remoto requiere publicar los cambios; macOS requiere un host de esa plataforma.
   No convertir esas limitaciones en validaciones aprobadas.

Implementados los controles tipados, indicadores nativos y guardado de configuración
propia mediante el canal humano firmado. Se conservan borradores, se permite
descartarlos y se rechazan valores inválidos, readonly alterado y snapshots caducos.
La prueba física de teclado encontró y corrigió un contenedor sin foco. SDK y guía
de cliente documentan el contrato; las capturas RGB reales están en `docs/images`.
Las regresiones integrales de Windows y Debian concluyeron sin fallos; los últimos
ajustes de foco/transporte tienen comprobación focal posterior en ambas plataformas.
También se verificó interoperabilidad con Python en Linux, sin añadirlo como
dependencia del producto. El acta conserva las omisiones y las validaciones que
requieren publicación, otro host o aceptación humana.
