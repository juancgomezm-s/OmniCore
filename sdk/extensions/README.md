# SDK de extensiones de OmniCore

SDK TypeScript compilado a JavaScript, JSON-RPC 2.0 local sobre stdio, sin Electron.
Requiere un Node **LTS >=22** sólo para paquetes ejecutables `kind: node`.
Los tres pilotos de `extensions/pilots` son declarativos y no necesitan Node.

Un paquete `kind: process` puede implementar el mismo protocolo sin el SDK:
ejecutable local dentro del paquete, JSON Lines UTF-8 por stdio y negociación
de versión/identidad/capacidades. Se verificó ese recorrido con un ejecutable
Python con shebang en Linux, instalación pura, consentimiento y una vista real.
No añade Python como dependencia de OmniCore ni acredita ese formato en Windows.

```sh
npm ci --ignore-scripts
npm test
```

El ejemplo `examples/minimal.ts` registra una herramienta y una vista. Para
empaquetarlo, coloca `dist/`, `examples/schemas/` (como `schemas/`) y
`examples/omnicore-extension.yaml` (renombrado a `omnicore-extension.yaml`) en
una carpeta propia; no incluyas `node_modules`. El manifiesto declara vista,
herramienta `hello` y command, con schema y techo de proceso explícitos. No se
publica ninguna herramienta que no aparezca en el manifiesto.

```sh
omni extensions install /ruta/al/paquete
omni extensions inspect example.hello
omni extensions enable example.hello --trust-process
omni extensions view example.hello hello
omni extensions disable example.hello
omni extensions uninstall example.hello
```

`--trust-process` autoriza código con el acceso del usuario: **no es un sandbox**.
Un paquete que solicita `restricted` se rechaza en esta entrega; nunca se convierte
en confiable implícitamente. La instalación sólo copia y valida, no ejecuta npm,
scripts, inicializadores ni la entrada del paquete. Node se resuelve desde User,
excluyendo el workspace del PATH; `OMNICORE_EXTENSION_NODE` permite una ruta User.

## Contratos

- `src/contracts.ts` y `protocol-v1.schema.json` son el contrato del SDK.
- `src/OmniCore.Protocol/ExtensionProtocol.cs` es su contraparte C# portable.
- Entrada: `initialize`, después métodos declarados (`view.read`, `tools/call`,
  `hooks/execute`, `context/contribute`, `completion/validate`, `completion/gate`).
- `initialize` fija identidad, versión `1.0`, capacidades y Session; no las elige
  el plugin. La respuesta no puede cambiar identidad ni añadir capacidades.
- Un frame es una línea UTF-8 sin BOM, máximo 262144 bytes. Máximo cuatro requests
  por sentido; sin cola ilimitada. Los servicios devuelven datos acotados.
- stdout sólo lleva RPC; `extension.log(level, code)` usa stderr estructurado.
  Core guarda severidad/código, no texto arbitrario potencialmente secreto.
- `onDispose` libera recursos; `AbortSignal` cancela handlers. Core fuerza el cierre
  del árbol ante cancelación/timeout y no reintenta automáticamente efectos desconocidos.

El SDK registra handlers, no otorga capacidades. Los servicios de Workspace/Git,
sesiones/agentes, historial y configuración están ligados al proceso y a la Session.
No existe `execution.execute` genérico ni acceso a Engine, SQLite, credenciales o DI.
Las herramientas declaradas se ejecutan por el pipeline M8, incluyendo Permission,
Barrier, journal y outcome; registrar una función JS no evita esas etapas.

`observe`, `before` y `after` tienen contratos distintos. Los observadores y hooks
posteriores sólo acusan observación; los previos devuelven `continue`, `restrict`,
`requireValidation` o `fail`, nunca un grant. Core valida capacidades y decide.
Los fallos restrictivos siguen cerrados por el dispatcher M8. El tercer argumento
de un handler `tool` lleva `toolCallId`, origen y correlación; usa `toolCallId` como
clave de idempotencia si el servicio externo la admite. Un timeout no prueba que
un efecto externo no ocurrió: Core conserva outcome desconocido y reconciliación.

`config.get/set(key,value,scope)` acepta `workspace` (predeterminado), `user` o
`session`, sólo claves declaradas. Es almacenamiento de la extensión, no una vía
para configurar permisos o providers. Las capacidades `configuration.read/write`
deben estar solicitadas y consentidas.

`sessions.current()` consulta exclusivamente la sesión vinculada al proceso
(`sessions.read`); `sessions.agents()` conserva las métricas y herramientas reales
disponibles en Core. No acepta seleccionar otra sesión por ID.

`activity.read(afterSequence, limit)` requiere `history.read`, devuelve hasta 128
eventos atribuibles y expone `nextSequence`, `highWater` y `gap`. La proyección
actual retiene los últimos 300 eventos de cronología; un cursor anterior recibe
un aviso explícito de historial incompleto. No confundirla con el journal entero.
`activity.watch({afterSequence,signal,intervalMs})` es un iterador **pull**, no push:
lee otra página sólo cuando el consumidor continúa, sin acumular una cola; al
alcanzar el high-water espera dos segundos por defecto. Cancelar/cerrar libera
el iterador. Conserva el cursor después de procesar la página, no antes.

## Pilotos y cliente independiente

```sh
omni extensions install extensions/pilots/filetree
omni extensions enable omnicore.filetree
omni extensions install extensions/pilots/agent-flow
omni extensions enable omnicore.agent-flow
omni extensions install extensions/pilots/time-machine
omni extensions enable omnicore.time-machine
omni extensions views
node sdk/extensions/headless.mjs /ruta/absoluta/a/omni omnicore.filetree files
```

Ejecuta los comandos `omni` desde el proyecto que quieras consultar. El cliente
headless usa el Host real y no depende de TUI. En TUI: F4 → Extensiones y habilidades
→ Paquetes y vistas → Vistas. Enter abre una carpeta o consulta el diff. Historial
muestra la acción de alternativa sólo cuando el punto tiene snapshot recuperable;
permite una nueva instrucción opcional y abrir la alternativa confirmada en la
misma terminal. La CLI crea otro runtime ligado al destino; no cambia el almacén
ni los archivos del original. La vista abierta se actualiza en segundo plano
cada dos segundos y conserva selección y orden. Al cerrarla se cancela su refresh.

Las dependencias de paquetes se declaran como `publisher.name@1.2.3` (versión exacta).
No se descargan dependencias automáticamente. `omni extensions update <id>
<carpeta|zip>` valida y prepara la versión antes de sustituirla; rechaza cambios de
identidad, incompatibilidad y ruptura de dependencias exactas. La versión anterior
queda en `<data>/extension-archive`; datos/configuración se conservan y todo hash
nuevo requiere habilitación y consentimiento nuevos. `omni extensions rollback
<id>` recupera el paquete anterior, también deshabilitado y con hashes verificados.
Una publicación interrumpida se recupera sin reactivar código. No se revierten
efectos externos ni se migran automáticamente datos de una extensión.

Fachadas, integración de clientes y límites: [guía de integración](../../docs/extensions/client-integration.md).

## Ejemplo de formulario e indicadores

Después de `npm test`, prepara una carpeta de paquete que contenga el manifiesto
`examples/preferences/omnicore-extension.yaml` en su raíz y la carpeta compilada
`dist/`. No copies el árbol de ejemplos completo ni `node_modules`. Instala esa
carpeta con `omni extensions install <carpeta>` y habilita `example.preferences`
revisando los hashes y el consentimiento del proceso Node confiable.

En **F4 → Extensiones y habilidades → Paquetes y vistas → Vistas**, abre
`example.preferences / preferences`. Los campos de texto, número, booleano,
opciones y sólo lectura guardan juntos la clave propia `display`, en scope
workspace. El borrador no cambia durante el refresh; **Descartar borrador**
permite releer, y guardar requiere la acción humana y un snapshot todavía vigente.
Los recursos nativos de etiquetas, estados y mensajes están disponibles en español,
inglés, francés e italiano; los valores de opciones conservan su identificador.

La vista `indicators` muestra el estado real de esa configuración. La cuota del
ejemplo es desconocida: no hay conexión con un proveedor de facturación ni se
inventa un saldo. Para acciones de formulario declara una clave y scope propios,
`requiresUserAuthority:true` y `configuration.write`; no un callback privilegiado.
