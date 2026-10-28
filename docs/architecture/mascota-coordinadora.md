# Mascota: conversación, recuerdos y coordinación local

La mascota tiene un mini chat independiente del agente principal. No recibe el historial del trabajo, no dispone de herramientas y no autoriza acciones. Los controles de **Sesiones** y **Avisos** permiten enviar una instrucción al principal o elegir una opción de una decisión real. No se dirige trabajo a los subworkers.

## Uso

| Acción | Entrada |
| --- | --- |
| Conversar con la mascota | Botón **Conversar** o `/pet chat` |
| Ver avisos entre instancias | **Avisos** o `/pet alerts` |
| Elegir agente principal | **Sesiones** o `/pet sessions` |
| Editar nombre preferido y recuerdos | **Memoria** o `/pet memory`, luego **Guardar memoria** |
| Recargar una edición externa/conflictiva | `/pet memory reload` |
| Impedir paseos y persecución de letras | **Restringir** o `/pet restrict` |
| Permitirlos otra vez | **Permitir** o `/pet unrestrict` |
| Ocultar la mascota y cerrar su panel | `/pet hide` |

En ventanas amplias el mini chat ocupa la parte superior del panel lateral y conserva el retrato debajo. En ventanas pequeñas usa una tarjeta que se cierra con **Cerrar** o Escape. El borrador del chat principal se mantiene separado. Las órdenes remotas conservan el foco y el seguimiento del desplazamiento del lector.

**Restringir** se guarda entre sesiones. Detiene el paseo inmediatamente y muestra una expresión triste de tres segundos; después la mascota sigue animada en su panel. No altera identidad, visitas, especie, XP, ánimo ni evolución, y permite seguir conversando o recibiendo avisos. El movimiento reducido presenta un solo frame de la expresión.

## Datos locales

Dentro del directorio de datos del perfil:

- `companion/pet.db`: identidad y preferencias visuales existentes, incluida la restricción.
- `companion/memory.md`: recuerdos elegidos por la persona; nombre preferido, datos y preguntas pendientes. Máximo 16 KiB. Puede editarse fuera de OmniCore. Guardar desde dos instancias comprueba una versión SHA-256 y rechaza sobrescrituras conflictivas. Borrar el contenido permite olvidar los recuerdos.
- `companion/coordinator.db`: bandeja SQLite WAL, instancias activas, avisos, acuses y mini chats. Los chats conservan las últimas 24 entradas de hasta 4000 caracteres y como máximo 128 conversaciones. Se pueden borrar sin alterar `pet.db`. La preferencia **Silenciar** también es local.

La memoria y la cola funcionan dentro del mismo perfil del mismo equipo. No son un servicio de red ni sincronizan máquinas o perfiles diferentes. Los recuerdos seleccionados y el mini chat se envían al proveedor configurado únicamente al pulsar **Enviar**. Los eventos del trabajo solo aportan metadatos de aviso; nunca se copia toda la conversación.

## Transporte y decisiones

Cada TUI registra un identificador aleatorio que dura lo mismo que su proceso, además del proyecto, sesión y Run exactos. Un sondeo de dos segundos ejecutado fuera del hilo de dibujo publica su estado y consulta la bandeja. La presencia caduca a los quince segundos. No se usan títulos de ventana, índices de pestañas, `wt focus-tab`, procesos por frame ni automatización de PowerShell.

La cola admite hasta 32 entregas pendientes por instancia. Una instrucción espera mientras el principal trabaja o tiene una decisión pendiente. El receptor comprueba otra vez sesión y Run antes de usar la admisión normal del agente. Las decisiones llevan el identificador de interacción y una opción explícita; el Core vuelve a validarlas. No se interpreta texto libre como permiso. Los cuestionarios estructurados se contestan en su sesión original.

La recepción se reclama en una transacción antes de ejecutar: no hay repetición automática después de un cierre inesperado. Si se pierde el proceso antes de reclamar, se rechaza la entrega. Si se pierde después, el estado queda **unknown** y hay que revisar el resultado antes de reenviar. **accepted** significa admisión, no finalización; la terminación del turno produce otro aviso. Los avisos de decisiones se deduplican y se marcan resueltos cuando dejan de estar pendientes. Las filas antiguas se limpian a los siete días.

Solo las TUI de esta versión anuncian un consumidor disponible. Una TUI anterior o un comando CLI breve no recibe órdenes. Una instancia cerrada no se abre ni se duplica al seleccionar su aviso. El comando `omni pet sessions --json` permite inspeccionar la presencia; `omni pet alerts --json` devuelve avisos y acuses. `--text` ofrece salida textual sin gráficos.

## Modelo y consumo propios

En **Configuración > Modelos y ejecución > Mascota: modelo y consumo** se elige el modelo exclusivo del mini chat. También se abre desde el botón **Modelo** de la mascota o con `/pet model`. La selección es independiente del modelo, las rutas y los presupuestos del agente principal y se guarda por perfil en `companion/coordinator.db`. La primera vez hay que elegirla explícitamente; no cambia al cambiar el modelo principal.

**Solo local** está desactivado por defecto. Al activarlo, el selector muestra únicamente los proveedores declarados locales o administrados por el supervisor local. Si el modelo deja de estar disponible, no se utiliza uno remoto como respaldo. Al desactivarlo se pueden elegir modelos locales, de suscripción o por consumo. Se reutilizan los proveedores y la autenticación ya configurados, sin crear nuevas claves.

El mini chat tiene un **contador informativo propio, sin cuotas ni topes monetarios**, que no lee ni consume los presupuestos del agente principal. Registra consultas, respuestas, fallos, tokens de entrada/salida, detalle de caché/razonamiento, estimaciones tarifarias y costes informados por el proveedor. Caché y razonamiento ya están incluidos en los tokens agregados y no se suman otra vez. Los campos no reportados y precios desconocidos se muestran como datos incompletos, nunca como consumo gratis. El contador conserva totales por modelo/proveedor entre sesiones e instancias y evita duplicar recibos.

El contador registra consultas lógicas, no es una factura ni garantiza conocer el consumo de reintentos internos de un proveedor. Un cierre sin respuesta conserva el intento con datos incompletos. Se actualiza al conversar o abrir su configuración, sin nuevas consultas periódicas al modelo. **Actualizar consumo** vuelve a leer los totales locales.

Por CLI:

- `omni pet model <model-id> --any-model` guarda un modelo propio permitiendo cualquier tipo de proveedor.
- `omni pet model <model-id> --local-only` selecciona un modelo local y activa la exclusividad local.
- `omni pet model --json` muestra la configuración guardada.
- `omni pet usage --json` muestra sus contadores; `--text` ofrece un resumen legible.

La mascota no recibe las herramientas del agente principal. El contrato de conversación queda separado para poder añadir herramientas exclusivas de su alcance más adelante. Por ahora los recuerdos y las órdenes entre instancias siguen usando sus controles explícitos. No llama al modelo por animaciones, avisos, paso del tiempo o apertura del panel.

La consulta conserva un timeout técnico de 60 segundos, contexto acotado y una respuesta breve; no son cuotas acumuladas. Se solicita un máximo de 512 tokens donde el adaptador lo soporta. El perfil Codex de suscripción no tiene un límite de salida comprobado y no recibe ese parámetro; el timeout y el recorte local no deben presentarse como límites de consumo del proveedor.

## Renderizado

Se conserva Terminal.Gui 2.6.0-develop.61 y sus backends existentes. Los atlas originales de las nueve apariencias incorporan dos frames nuevos de tristeza (18/19), preparados antes de ejecutar; Unicode tiene una expresión equivalente. No hay una dependencia de conversión ni un proceso nuevo de animación. La cadencia y la caché de los sprites conservan las optimizaciones anteriores.

La evidencia y las pruebas de esta entrega están en `docs/validation/companion-coordinator-20261028.md`.
