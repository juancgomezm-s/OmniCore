# Mascota: conversación, recuerdos y coordinación local

La mascota tiene una conversación independiente del agente principal y compartida entre las instancias del mismo perfil. Los avisos aparecen en esa misma conversación y se le responde con **Enviar**, por ejemplo «dile a Proyecto 106 que debe validar sus cambios contra main». No recibe el historial completo del trabajo, no autoriza permisos del Core ni dirige subworkers. Tiene nueve herramientas exclusivas.

Una burbuja junto a la cabeza muestra el número de grupos de avisos pendientes. La mascota llama la atención brevemente una vez por cambio, respetando movimiento reducido y silencio. La llegada de un aviso no consulta ningún modelo, cambia el foco ni desplaza el chat principal. Abrir la conversación presenta hasta ocho avisos por lote y sólo reconoce los que se han mostrado; repetir el sondeo no duplica mensajes.

**Preferencias → Mascota** reúne en una sola ventana modelo propio, exclusividad local opcional, avisos, atención, visibilidad, restricción, movimiento, modo visual, consumo y mantenimiento de recuerdos. No hay pestañas de sesiones, avisos o memoria en el chat. Los comandos anteriores siguen siendo accesos a la misma conversación o ventana de mantenimiento.

`sessions.create(title)` crea una conversación vacía y persistida en el workspace actual. Conserva la conversación seleccionada y sus borradores, aplica los valores normales de seguridad y rutas de una sesión nueva y devuelve su ID. La creación no ejecuta un modelo ni abre otra ventana. Se puede seleccionar desde la lista de sesiones (F6); no aparece como agente activo antes de abrirla. Los reintentos de una misma llamada devuelven la misma sesión.

## Comunicación lateral en el mismo buzón

Los principales disponen de `agents.list`, `agents.send(target, text)` y `agents.read(after)`. Usan la misma tabla `deliveries` del mismo `companion/coordinator.db` que las instrucciones humanas, con los mismos IDs de presencia. Funcionan entre procesos del mismo perfil local y no dependen de PowerShell, del renderizador ni de que la mascota esté visible o descubierta.

Las órdenes humanas conservan `kind=instruction/answer`, `origin=user`, `relation=human`. Los mensajes de agentes usan `kind=message`, `origin=agent`, `relation=lateral`, y remitente y sesión de origen fijados por el Host. Son comunicaciones entre pares: no crean un supervisor ni un subworker, no amplían el objetivo autorizado ni conceden permisos o aprobaciones.

El receptor consulta sus mensajes con `agents.read`; recibe el cursor `next` para continuar con `after`. Repetir un cursor permite recuperar una respuesta perdida. Un envío repetido con el mismo ID no se duplica. Los mensajes laterales generan un aviso y nunca pasan por `StartModelTurn` como si fueran entradas humanas. Un principal en reposo los verá al consultar su buzón en un turno posterior; no se generan llamadas al modelo, ciclos de conversación o gasto automáticamente. Los principales activos pueden consultar y contestar durante su trabajo.

La presencia caduca y no se redirige un mensaje destinado a una instancia cerrada hacia otra sesión. La mascota conserva sus herramientas exclusivas; el catálogo de comunicación se registra sólo para principales, no para ejecuciones delegadas. El buzón jerárquico ya existente de los subworkers conserva su contrato y sus atribuciones de Core.

## Uso

| Acción | Entrada |
| --- | --- |
| Conversar con la mascota | Botón **Conversar** o `/pet chat` |
| Ver avisos entre instancias | Burbuja de la mascota o `/pet alerts` |
| Consultar sesiones o enviar instrucciones | Pedírselo a la mascota en su conversación |
| Mantener modelo, recuerdos y preferencias | **Preferencias → Mascota** o `/pet memory` |
| Impedir paseos y persecución de letras | **Restringir** o `/pet restrict` |
| Permitirlos otra vez | **Permitir** o `/pet unrestrict` |
| Ocultar la mascota y cerrar su panel | `/pet hide` |

En ventanas amplias el mini chat ocupa la parte superior del panel lateral y conserva el retrato debajo. En ventanas pequeñas usa una tarjeta que se cierra con **Cerrar** o Escape. El borrador del chat principal se mantiene separado. Las órdenes remotas conservan el foco y el seguimiento del desplazamiento del lector.

**Restringir** se guarda entre sesiones. Detiene el paseo inmediatamente y muestra una expresión triste de tres segundos; después la mascota sigue animada en su panel. No altera identidad, visitas, especie, XP, ánimo ni evolución, y permite seguir conversando o recibiendo avisos. El movimiento reducido presenta un solo frame de la expresión.

## Datos locales

Dentro del directorio de datos del perfil:

- `companion/pet.db`: identidad y preferencias visuales existentes, incluida la restricción.
- `companion/memory.md`: memoria heredada, todavía legible y editable, con comprobación de versión SHA-256. El mantenimiento permite editar u olvidar sus hechos individuales sin perder los demás.
- `companion/coordinator.db`: bandeja SQLite WAL, instancias, avisos, rutas exactas, preferencias, consumo, recuerdos por categorías y conversación global. El nuevo archivo de conversación conserva sus mensajes completos; ya no corta cada mensaje a 4000 caracteres ni conserva sólo 24 entradas. Importa una sola vez cada chat heredado al visitarlo. La vista carga las últimas 100 entradas, mientras el archivo y el contexto se gestionan por separado.

Las categorías de memoria son **Personal**, **Trabajo**, **Agentes** y **Seguimientos**, con proyecto, procedencia y estado. SQLite FTS5 recupera hechos relevantes; si no está disponible usa una búsqueda textual compatible. No se incorpora un servidor vectorial ni un proceso adicional. El contexto recupera recuerdos del proyecto pertinente y datos personales pertinentes; éstos no se copian automáticamente a las instrucciones para agentes. Los seguimientos creados al enviar una instrucción distinguen en cola, recibida y rechazada. Recibida no significa terminada; se resuelven explícitamente desde la conversación o el mantenimiento.

La memoria y la cola funcionan dentro del mismo perfil del mismo equipo. No son un servicio de red ni sincronizan máquinas o perfiles diferentes. Los recuerdos seleccionados y el mini chat se envían al proveedor configurado únicamente al pulsar **Enviar**. Los eventos del trabajo solo aportan metadatos de aviso; nunca se copia toda la conversación.

## Transporte y decisiones

Cada TUI registra un identificador aleatorio que dura lo mismo que su proceso, además del proyecto, sesión y Run exactos. Un sondeo de dos segundos ejecutado fuera del hilo de dibujo publica su estado y consulta la bandeja. La presencia caduca a los quince segundos. No se usan títulos de ventana, índices de pestañas, `wt focus-tab`, procesos por frame ni automatización de PowerShell.

La cola admite hasta 32 órdenes humanas pendientes por instancia; los datos laterales no ocupan ese cupo. Una instrucción espera mientras el principal trabaja o tiene una decisión pendiente. El receptor comprueba otra vez sesión y Run antes de usar la admisión normal del agente. Las decisiones llevan el identificador de interacción y una opción explícita; el Core vuelve a validarlas. No se interpreta texto libre como permiso. Los cuestionarios estructurados se contestan en su sesión original.

La recepción se reclama en una transacción antes de ejecutar: no hay repetición automática después de un cierre inesperado. Si se pierde el proceso antes de reclamar, se rechaza la entrega. Si se pierde después, el estado queda **unknown** y hay que revisar el resultado antes de reenviar. **accepted** significa admisión, no finalización; la terminación del turno produce otro aviso. Los avisos de decisiones se deduplican y se marcan resueltos cuando dejan de estar pendientes. Las filas antiguas se limpian a los siete días.

Las TUI anuncian un consumidor de órdenes humanas. Los principales headless activos anuncian presencia lateral durante su turno, con `acceptsInstructions=false`: sus herramientas pueden intercambiar mensajes, pero no anuncian una recepción de órdenes que no poseen. Una instancia cerrada no se abre ni se duplica al seleccionar su aviso. El comando `omni pet sessions --json` permite inspeccionar la presencia; `omni pet alerts --json` devuelve avisos y acuses. `--text` ofrece salida textual sin gráficos.

## Modelo y consumo propios

En **Preferencias → Mascota** se elige el modelo exclusivo del mini chat. También se abre desde **Preferencias** en su conversación o con `/pet model`. La selección es independiente del modelo, las rutas y los presupuestos del agente principal y se guarda por perfil en `companion/coordinator.db`. La primera vez hay que elegirla explícitamente; no cambia al cambiar el modelo principal.

**Solo local** está desactivado por defecto. Al activarlo, el selector muestra únicamente los proveedores declarados locales o administrados por el supervisor local. Si el modelo deja de estar disponible, no se utiliza uno remoto como respaldo. Al desactivarlo se pueden elegir modelos locales, de suscripción o por consumo. Se reutilizan los proveedores y la autenticación ya configurados, sin crear nuevas claves.

El mini chat tiene un **contador informativo propio, sin cuotas ni topes monetarios**, que no lee ni consume los presupuestos del agente principal. Registra consultas, respuestas, fallos, tokens de entrada/salida, detalle de caché/razonamiento, estimaciones tarifarias y costes informados por el proveedor. Caché y razonamiento ya están incluidos en los tokens agregados y no se suman otra vez. Los campos no reportados y precios desconocidos se muestran como datos incompletos, nunca como consumo gratis. El contador conserva totales por modelo/proveedor entre sesiones e instancias y evita duplicar recibos.

El contador registra consultas lógicas, no es una factura ni garantiza conocer el consumo de reintentos internos de un proveedor. Un cierre sin respuesta conserva el intento con datos incompletos. Se actualiza al conversar o abrir su configuración, sin nuevas consultas periódicas al modelo. `omni pet usage --json` conserva el detalle por modelo/proveedor.

Por CLI:

- `omni pet model <model-id> --any-model` guarda un modelo propio permitiendo cualquier tipo de proveedor.
- `omni pet model <model-id> --local-only` selecciona un modelo local y activa la exclusividad local.
- `omni pet model --json` muestra la configuración guardada.
- `omni pet usage --json` muestra sus contadores; `--text` ofrece un resumen legible.

La mascota no recibe las herramientas del agente principal. Su catálogo exclusivo se describe abajo. No llama al modelo por animaciones, avisos, paso del tiempo o apertura del panel.

El contexto local es de unos **32768 tokens**, o menos si la capacidad declarada del modelo es menor. Para modelos remotos se usa la ventana declarada del modelo; si no se conoce se mantiene 32768 como respaldo. Se reserva espacio para la respuesta conforme a esa ventana y al máximo de salida declarado. Ya no se aplica el antiguo máximo fijo de 512 tokens. El perfil Codex de suscripción no recibe un parámetro de salida no comprobado.

Antes de cada consulta se cuentan instrucciones, recuerdos, extractos, conversación y herramientas con el contador disponible del proveedor (estimación cuando no hay tokenizador). Al acercarse al límite se compactan intercambios antiguos completos en extractos acotados, conservando el último mensaje y los pares de llamadas/resultados de herramientas. El historial original permanece en SQLite y el punto de compactación sólo avanza. No hay cuotas monetarias. Permanecen el timeout técnico de 60 segundos y límites de pasos para evitar bucles de herramientas.

## Herramientas exclusivas del mini chat

| Herramienta | Alcance |
| --- | --- |
| `memory.read` | Recupera recuerdos por texto, categoría y proyecto, con identificadores estables. |
| `memory.remember` | Guarda un hecho solicitado por la persona en su categoría y proyecto. No duplica hechos iguales. |
| `memory.forget` | Elimina el recuerdo identificado sin borrar la conversación. |
| `memory.resolve` | Marca un seguimiento como resuelto. |
| `sessions.list` | Consulta instancias activas, proyecto, sesión e identificador de instancia. |
| `sessions.create` | Crea una conversación vacía persistida, sin abrir otra ventana ni iniciar trabajo. |
| `notices.list` | Consulta avisos recientes reales, incluidos finalizaciones y decisiones pendientes. No aprueba ninguna decisión. |
| `agent.message` | Encola la instrucción literal para un proyecto explícito o para un aviso actual inequívoco. |
| `pet.react` | Expresa una reacción breve sin modificar XP, especie, visibilidad, restricción ni preferencias. |

Ejemplos en `/pet chat`:

- «Recuerda que mi gato se llama Miso». Después: «¿Qué recuerdas de mi gato?» o «Olvida lo de Miso».
- «¿Qué sesiones están trabajando?» y «¿Hay avisos nuevos?».
- «Dile a Alpha: revisa las pruebas». **Alpha** debe identificar un proyecto activo de forma única. También se puede indicar la sesión o instancia por ID; desde una TUI, «esta sesión» usa su sesión actual cuando hay un único receptor.
- «Dile a Proyecto 106 que valide contra main». Si acaba de presentarse un único aviso, «dile que revise las pruebas» usa la ruta exacta de ese aviso; si hay varios, pregunta por el destino. Un proyecto explícito nunca se sustituye por el aviso actual.

Si coinciden varias instancias, la mascota pide un destino más preciso. El destino debe figurar en la petición humana y el texto reenviado debe ser un fragmento literal de ella; no se deducen órdenes de avisos ni recuerdos. Las opciones de permisos, cuestionarios y aprobaciones siguen en los controles validados por el Core. No existe una herramienta para responder permisos ni para ejecutar shell, editar el proyecto o dirigir subworkers.

El modelo recibe resultados de herramientas y puede continuar hasta una respuesta final. Las llamadas de esta continuación se contabilizan individualmente en el consumo propio, sin cuotas monetarias. El diálogo técnico tiene timeout y un número finito de pasos para evitar bucles; las respuestas y listados son breves. El modelo elegido debe admitir llamadas de herramientas nativas. No se interpreta texto libre del modelo como código ejecutable.

Los reintentos de una misma acción dentro de la consulta no repiten el envío. Los envíos siguen vinculados al identificador de instancia, sesión y Run que valida la bandeja local. La respuesta distingue **en cola** de **admitida** y de **finalizada**. Los efectos completados tienen un recibo textual: si falla la última respuesta del modelo, la persona puede ver qué se guardó o encoló antes de repetir la petición.

Los recuerdos categorizados se actualizan en transacciones SQLite; el archivo heredado conserva su comprobación de versión. Las reacciones se comparten como una preferencia temporal con fecha de caducidad; no generan eventos por frame, no alteran evolución y respetan ocultar, restringir y movimiento reducido. La caricia del Core mantiene prioridad sobre una reacción de la tool.

## Renderizado

Se conserva Terminal.Gui 2.6.0-develop.61 y sus backends existentes. Los atlas originales de las nueve apariencias incorporan dos frames nuevos de tristeza (18/19), preparados antes de ejecutar; Unicode tiene una expresión equivalente. No hay una dependencia de conversión ni un proceso nuevo de animación. La cadencia y la caché de los sprites conservan las optimizaciones anteriores.

La evidencia anterior está en `docs/validation/companion-coordinator-20261028.md`; la conversación unificada se valida en `docs/validation/companion-natural-20261029.md`.
