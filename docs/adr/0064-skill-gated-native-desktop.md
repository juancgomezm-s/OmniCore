# ADR-0064: escritorio nativo seleccionado mediante skill

Estado: implementado localmente, pendiente de publicación. Fecha: 2026-10-30.

## Decisión

El usuario solicita reescribir el prototipo independiente en Rust e integrarlo
como herramienta de OmniCore, disponible sólo mediante skills. Se añade un solo
ID Core, `desktop.control`, y una skill BuiltIn/Core sellada `desktop-control`.
La activación automática se deriva de la solicitud humana actual cuando pide
probar, capturar u operar una ventana. Se utiliza la admisión existente por
`activation.taskTypes: [desktop-interaction]`, con límites de skills/contexto y
supresión automática vigentes. No se selecciona por texto de la aplicación,
WorkingState ni instrucciones encontradas en pantalla. El clasificador local
cubre solicitudes habituales en español e inglés; no es un intérprete universal.
No hay registro MCP ni endpoint HTTP para este componente.

Al terminar un turno asentado se libera únicamente la selección automática, se
retira la herramienta del catálogo y se cierra el helper. Un turno pendiente se
conserva para reanudación con el mismo fingerprint; completar o abandonar permite
su descarga. Las cargas humanas/modelo explícitas conservan su semántica durable
por Session y no se liberan a escondidas. Un fallo al registrar la liberación
restaura la selección. El comando manual sigue siendo opcional.

El Host incorpora la herramienta al catálogo exclusivamente cuando la composición
principal ha cargado esa skill exacta desde el paquete BuiltIn. Al liberar o
recomponer, la retira del catálogo: tampoco aparece en FullCatalog, discovery ni
por un ID adivinado. Las Lanes delegadas y etapas de workflow no la reciben.
Prepare es puro; Execute revalida la selección durable, manifest y hash completo
del paquete antes de iniciar el helper o emitir entrada. Cambiar los archivos
invalida la selección. La skill no concede permisos ni modifica modos/modelos.

`desktop.control` se clasifica conservadoramente como GeneralProcess en el techo
de capacidad y como ExternalOrUnknownProcess para permisos, incluso al observar.
Entrada/foco son NonIdempotent de riesgo alto; observaciones son EffectClass.None
con exposición privada. La ejecución sólo acepta AuthorizedToolIntent por la ruta
habitual y conserva Ask/Deny, la política vigente y el journal de efectos.
Timeouts/errores de canal cierran la instancia sin reintentar entrada incierta.

## Backend de Core y sandbox

El helper es una implementación nativa fija de Core, no un proceso de extensión
o proyecto. El modelo no elige ejecutable, argumentos de arranque, directorio,
entorno ni biblioteca. La ruta se fija respecto del ensamblado CLI; Host comprueba
SHA-256 contra un recurso compilado y mantiene el archivo abierto sin compartir
escritura durante la creación del proceso. Se transmite sólo un entorno mínimo,
sin credenciales del proveedor. No descarga código ni ejecuta scripts.

Acceder al escritorio interactivo requiere las APIs de la sesión del usuario y
no puede acreditarse como AppContainer Strong. Esta ruta específica de Core se
ejecuta con esa autoridad y lo documenta; no introduce fallback Weak ni cambia
los launchers Strong/consentimientos de procesos generales de ADR-0038.
Los permisos existentes sobre `desktop.control` siguen siendo obligatorios.
Un usuario local capaz de sustituir Host o su paquete tiene la misma autoridad
que el propietario de la instalación; el hash no pretende defender contra él.

## Alcance y límites

Captura WGC o PrintWindow con DPI físico; UIA de la ventana/proceso objetivo, contraseñas
redactadas; teclado Unicode y ratón SendInput. Cada acción exige observación
reciente con identidad del proceso/ventana, geometría y foco válidos. UIA por índice
revalida runtime ID. Tras actuar se invalida la observación. Cleanup libera sólo
entradas sintéticas que sí fueron insertadas, aun si se perdió foco.

Captura/UIA son hijos aislados con timeout de ocho segundos; se cierran junto al
canal al disponer Host, y tras una caída del padre pueden durar hasta el timeout.
Imágenes se publican en CAS sensible y llegan como ImageBlock al modelo, sin
base64 en el texto. No hay hooks, elevación, clipboard ni dependencia del binario
original. No se garantiza escena atómica ni soporte para toda aplicación.

El helper mantiene un indicador nativo visible mientras está vivo: cortina morada
al 6,3 % sobre el escritorio virtual, transparente al ratón, y aviso superior sin
activación con ventana objetivo y botón de cancelación. Escape es cancelación del
usuario y no una tecla que el modelo pueda sintetizar. Un latch cancela las
operaciones y los workers; la notificación al Host se emite después de liberar
entrada propia. Host enlaza esta señal al CancellationToken del turno, incluyendo
la espera del proveedor. La cancelación explícita libera la selección automática
aunque el journal conserve TurnInterrupted para recuperación. Las selecciones
explícitas y los permisos pendientes mantienen sus reglas previas.

La captura sólo admite ventanas de aplicación descubiertas; rechaza el escritorio,
shell e indicador propio. PrintWindow captura únicamente el objetivo. El indicador
también usa WDA_EXCLUDEFROMCAPTURE como medida adicional. La cortina es un tinte
suave, no un desenfoque del contenido. Estos controles no se incorporan a OmniCoder.


## Núcleo compartido 0.4.0

`native/desktop-engine` concentra captura WGC/PrintWindow, escalado, UIA y SendInput.
`native/desktop-control` es el adaptador empaquetado con OmniCore. La aplicación
independiente Desktop Agent para Claude consume una instantánea fijada por hashes
de esa crate y mantiene MCP, indicador y registro propios. Los ejecutables enlazan
el núcleo y no requieren al otro producto en ejecución. Compartir código no comparte
permisos, sesiones ni canales. OmniCore conserva su gate de skill y cancelación.
Las acciones UIA `invoke`/`set_value` se autorizan como mutaciones NonIdempotent;
las nuevas coordenadas reducidas se ligan a screenshot_id y su observación vigente.
La política de OmniCore excluye screen_gdi. Validación: desktop-shared-engine-20261030.
