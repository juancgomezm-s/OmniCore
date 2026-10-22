# Integración futura de clientes: extensiones v1

OmniCoder no se modificó en esta entrega. Debe consumir estos contratos mediante
el Host/stdio y Protocol/Client existentes; no copiar servicios Git, snapshots,
persistencia, parsers de herramientas ni reglas de autorización.

## Conectar y negociar

Arranca `omni host --stdio` en el workspace correspondiente. Conserva la negociación
`hello` de [stdio 1.0](../protocol/stdio/1.0/README.md). Comprueba la capability
`query.extensionPlatform`; para administración solicita el canal humano existente
con `command.extensions.package.user`. Un cliente genérico no puede inventar HMAC,
bootstrap ni `ITrustedUserActionClient`.

Consultas de `IOmniClient.Query` o del request stdio `query`:

| Nombre | Resultado | ¿Ejecuta código? |
|---|---|---|
| `extensions.packages` | Array `ExtensionPackageState` | No |
| `extensions.views` | `{extensionId,viewId,kind}[]` de contribuciones habilitadas | No |
| `extensions.view:<encodeURIComponent(JSON)>` | `ExtensionView` | Sólo la apertura explícita de una vista ejecutable consentida |
| `extensions.configuration:<encodeURIComponent(JSON)>` | Valor JSON de una clave declarada | No |

Request de vista: `{extensionId, viewId, path:"", search:"", afterRevision:null,
clientKinds:["tree","list","table","form","indicator","details"]}`. Envía sólo los renderizadores
disponibles. Una contribución incompatible responde `unsupported`; no desaparece
ni pierde su alternativa headless. Árboles cargan un directorio por consulta.
Filetree devuelve estado Git por fila y cabecera/ramas/worktrees en `metadata`;
el diff usa `workspace.panel` existente, no un parser en el cliente.
Columnas/celdas de tabla, campos de formulario e indicadores tienen DTOs tipados
en C# y TypeScript. Un tipo presente en el contrato no acredita un renderer en
cada cliente. Las acciones de escritura inicialmente expuestas son las de los
pilotos Core y las acciones de configuración propia descritas abajo; no traduzcas
un ID arbitrario de una vista a un command privilegiado.

## Formularios e indicadores nativos

La TUI implementa `text`, `number`, `boolean` y `choice`, con campos obligatorios,
sólo lectura y opciones cerradas. Los números visibles usan la cultura de las
preferencias; los valores del protocolo siguen siendo números JSON invariantes.
Indicadores conservan estado, valor y unidad: un valor `null` se presenta como
desconocido, no como cero. Una vista no admite HTML ni código para dibujar controles.

Una acción de formulario incluye `configurationKey`, `configurationScope`
(`user`, `workspace` o `session`), `requiresUserAuthority:true`, `enabled` y,
si está deshabilitada, `disabledReason`. Core sólo habilita claves declaradas y
capacidad `configuration.write` consentida. Un cliente no ejecuta una acción
deshabilitada ni convierte cualquier otro ID en un command.

Guardar usa el canal humano existente, no RPC del proceso de extensión:

```json
{"cmd":"extensions.package","action":"submit-view","extensionId":"example.preferences","submission":"<ExtensionFormSubmission serializado como string JSON>"}
```

`ExtensionFormSubmission` lleva `extensionId`, `viewId`, `actionId`,
`expectedRevision`, `scope`, `values`, `path` y `search`. Copia el `scope` completo
del snapshot sin modificarlo: workspace, sesión nullable, hashes y `snapshotId`.
El transporte vincula el paquete y la sesión a la prueba humana firmada; Core
revalida además la revisión y la identidad vigente. Todos los campos se validan
antes de guardar un único objeto en la clave declarada. Valores desconocidos,
duplicados, de tipo incorrecto, opciones inválidas o cambios de sólo lectura se
rechazan sin escritura parcial. `values` admite hasta 8 KiB UTF-8; el contenedor
serializado hasta 64 KiB, dentro del límite del frame.

Un snapshot consumido no puede reutilizarse con un nuevo command. Ante rechazo,
conserva el borrador y ofrece releer/descartar, no reintentes efectos automáticamente.
La TUI pausa el refresh mientras hay cambios locales; los formularios con acciones
no soportadas muestran esa limitación en lugar de ejecutarlas.

## Revisiones, eventos y recuperación

`id` y los IDs de filas son estables. `revision` es un entero seguro para JavaScript,
local al read model/conexión, no una secuencia del journal. El snapshot inicial usa
`reset:true`. Pasa la última revisión para recibir filas cambiadas y `removedIds`.
Si el servidor ya no conserva la base, devuelve un snapshot completo. Después de
reconectar empieza sin `afterRevision`. `ExtensionViewProjection` implementa la
detección de gaps; no mezcles cachés de workspaces/sesiones ni rutas del árbol.
`itemOrder`, cuando está presente, es el orden completo por IDs después de aplicar
el delta. Una modificación no debe mover la fila al final ni desplazar la selección.

Usa el stream M9 del journal y sus ACK/cursor/backpressure existentes para cambios
de herramientas, agentes y turnos. Agrupa refresh de las vistas afectadas; no
conviertas cada evento en un escaneo completo del árbol. Cambios externos en disco
pueden refrescarse al enfocar o por sondeo acotado. El servicio Git existente
comparte su caché de dos segundos. En esta versión no hay watcher nuevo ni push
privado del proceso de extensión: snapshots/deltas se obtienen por consulta.
La TUI realiza ese sondeo cada dos segundos en segundo plano sólo para la vista
abierta; agrupa consultas y descarta resultados de overlays/sesiones anteriores.

Conserva selección/expansión por ID en el cliente. `partial`, `error`, diagnóstico y
falta de Session son estados visibles, no resultados completos inventados. Los
datos originales no se traducen; textos propios usan recursos es/en/fr/it. Trata
todo label/dato externo como literal, nunca HTML ni instrucciones ejecutables.

## Acciones humanas

Administración: command `extensions.package` con `action` `install`, `enable`,
`disable`, `uninstall`, `update`, `rollback`, `configure` o `submit-view`.
`install` recibe `source` absoluto local.
Las otras reciben `extensionId`; habilitar/desinstalar/actualizar/revertir requieren
`expectedManifestSha256` y `expectedPackageSha256` del paquete inspeccionado.
`enable` ejecutable exige `trustProcess:"true"` después de explicar el acceso
efectivo del usuario sin sandbox. `configure` recibe `key` y `value` como texto JSON;
jamás configura permisos del runtime. No conceder automáticamente al abrir Preferencias.

`update` recibe `extensionId`, `source` local y los dos hashes de la versión actual;
`rollback` recibe identidad y hashes actuales. Ambas detienen el proceso supervisado,
validan antes de reemplazar, preservan datos y dejan la versión resultante deshabilitada.
El usuario revisa y consiente los nuevos hashes por `enable`; no se heredan grants.
Un intent durable recupera la versión anterior si se interrumpe la publicación.
Rollback comprueba los hashes del archivo recuperable y no revierte efectos externos
ni migraciones de datos. Si existen dependientes exactos incompatibles, se rechaza.

Time Machine publica acciones `alternative` con `itemId`. Resuelve el checkpoint de
la fila, pide instrucción opcional y envía el command humano existente
`conversation.alternative` con `checkpointId` e `instruction`. Core revalida Session,
snapshot, corte de tool pairs y turno quiescente. Usa el mismo `messageId` para
consultar `workspace.panel` kind `alternative`, `commandId:<messageId>` después del ACK.
Sólo abre el workspace/Session derivados cuando Core confirme su publicación.
La original y sus cambios sin commit quedan intactos. Recobrar un ACK no autoriza
otra creación; ante resultado ambiguo consulta antes de reenviar.
La TUI ofrece abrir la alternativa confirmada: después de cerrar el renderer crea
otro runtime para el destino, revalida recibo/origen/Session y vuelve al compositor.
No reutiliza el Host original con otra raíz ni copia permisos de la sesión fuente.

No presentes esta acción como rollback de bases de datos, servicios o efectos externos.

## Frontera pública y límites iniciales

Los paquetes v1 y MCP conservan Strong y su semántica M8. El host v2 confiable es
User-scoped; instalar un paquete Project en User exige una acción humana. Un repo
no habilita el host v2 ni concede capacidades. `restricted` se rechaza hasta disponer
de aislamiento verificable para el proceso bidireccional. No hay sandbox implícito.

Las fachadas RPC del proceso exponen lectura de Workspace/Git/agentes/historial y
configuración propia. Las mutaciones generales, cancelación de otro agente o
creación de alternativas desde RPC de la extensión **no están expuestas**: usan
los commands y permisos humanos/Core existentes. Los hooks conservan el dispatcher
M8 con fallo cerrado para restricción/validación, orden estable y resultados durables;
la fachada nueva no admite ejecución recursiva, por lo que no crea ciclos de hooks.

`sessions.current` usa `sessions.read` y consulta sólo la sesión vinculada.
`activity.read` usa `history.read`: `{afterSequence:0,limit:64}` devuelve una página
`ExtensionActivityPage` (máximo 128 eventos y 128 KiB). `nextSequence` es un cursor
de journal, no una cuenta de filas; `gap` avisa si el corte de los últimos 300
eventos de cronología impide completar el intervalo solicitado. Nunca solicites
un ID de sesión ajeno. El iterador SDK `activity.watch` aplica pull con espera y
cancelación; no anuncia una suscripción push ni sustituye el stream M9 del cliente.

Límites: 512 entradas/64 MiB por paquete, 256 filas por vista, 256 KiB por frame,
cuatro requests concurrentes por sentido, timeout de inicialización 10 s, vista/servicio
15 s y herramienta 30 s; tres arranques por extensión/workspace/Session por Host,
con reinicio del contador tras intervención humana de lifecycle. Un fallo
de código con efectos queda desconocido y no se reintenta automáticamente. Otras
plataformas y aislamiento restringido deben validarse por separado. La TUI ofrece
lista/detalles, navegación, diff y controles nativos de formulario/indicador;
no gráficos HTML ni callbacks privilegiados definidos por una extensión.
