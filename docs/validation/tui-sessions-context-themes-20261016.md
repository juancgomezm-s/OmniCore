# Validación de conversaciones contexto y temas

Fecha: 16 de octubre de 2026.

La TUI incorpora creación y selección de conversaciones, limpieza de la vista,
compactación manual y temas de color. El contexto automático añade un disparador
al 95 % del presupuesto efectivo de entrada. Se retira de General el texto que
explicaba cómo usar las flechas del selector de idioma.

## Conversaciones

`/nuevo`, `/retomar`, `/retomar <id>`, `/limpiar` y `/compactar` son acciones
operativas. El selector también se abre con F6 y admite búsqueda, Enter y botones.
La limpieza afecta sólo a la vista. El cambio de conversación reconstruye las
proyecciones desde el journal correspondiente y conserva por conversación los
borradores y adjuntos aún no enviados durante esa ejecución de la TUI.

El Host exige acción de usuario para crear, seleccionar o compactar. La lista
pertenece al workspace y la selección adquiere la propiedad de escritura de la
sesión elegida. Un turno abierto impide cambiar o compactar la conversación.
El cursor durable acepta sesiones aún sin Run y se sustituye mediante un archivo
temporal y renombrado. Crear una conversación vacía no inventa un input ni hereda
grants de rutas; el primer intento real resuelve su política inicial.

## Contexto

La prueba de presión cuenta tokens antes de recortar y dispara compactación al
95 %, aun sin alcanzar la cadencia por cantidad de mensajes. La entrada efectiva
reserva la salida declarada dentro de la ventana del modelo. Se conserva la
barrera de overflow de contenido protegido y la precisión del contador usado.

La compactación manual produce un checkpoint extractivo acotado, sin una llamada
de modelo adicional. Conserva el primer input y los dos últimos intercambios;
el resto se representa por extractos redactados. El cuerpo original sigue en
el journal y en sus artifacts. El checkpoint se consume en próximos turnos y
Runs de la conversación principal; no se hereda implícitamente por Lanes hijas.
Sus límites y propietario se contrastan con el evento canónico. Un checkpoint
manual invalida índices de compactación anteriores al cambio de base.

La extracción manual incluye las llamadas/resultados y los fallos de herramientas,
así como steering aplicado. Reserva espacio para instrucciones y recibos breves
antes de repartir el espacio entre respuestas extensas. No confunde una llamada
solicitada con un efecto aplicado: los outcomes se incluyen con su tipo canónico.

La cadencia de compactación sigue en el perfil: por defecto 24 elementos antiguos
y 12 recientes. Incluye el checkpoint anterior en el siguiente resumen. Las salidas
largas de herramientas se externalizan sin modificar su original redactado; la
vista de hasta 1200 caracteres reparte espacio entre inicio, diagnósticos y final,
eliminando líneas repetidas en esa vista. Los resultados cortos no cambian. Esa
vista ya acotada no vuelve a pasar por la compresión de conversación antigua.
El resumen automático recibe esos extractos de herramientas, evitando reenviar
logs íntegros; conserva la instrucción de priorizar errores, cambios, pruebas,
decisiones y pendientes. El indicador `IsError` se refleja en su prosa.

Se elimina la duplicación entre el texto del checkpoint y las secciones que lo
indexan. Los contributors se consultan una vez por materialización y se reutilizan
en la comprobación de presión y en la proyección final; no hay caché entre turnos.
Esto conserva el contrato de contributors con efectos y las publicaciones CAS.

Ningún resumen acotado garantiza conservar todos los detalles en la ventana activa.
El journal y los artifacts originales permanecen; la selección extractiva de líneas
es una heurística, no un veredicto de éxito. La política de presupuesto conserva
sus diagnósticos de omisión/truncamiento y su barrera de contenido protegido.

Las trazas visibles de razonamiento se excluyen de la prosa destinada a resumir.
El replay nativo requerido por una ruta mantiene sus validaciones y no se
convierte en texto normal. La batería incluye las pruebas existentes de replay,
razonamiento opaco, aislamiento y fingerprints.

## Colores

General y apariencia ofrece OmniCore, OpenCode, Claude y Codex. El cambio se
guarda al pulsar Guardar. El tema modifica esquemas y colores de las celdas
renderizadas, incluidos bloques de código y barras de desplazamiento, sin
transformar texto, destinos de enlaces, acciones ni geometría de la conversación.
Las pruebas comparan el texto y la posición del compositor antes y después del
cambio y comprueban la elección persistida. Las capturas proceden del driver
de pruebas de Terminal.Gui y contienen datos ficticios.

## Ayuda

La última acción de Ayuda es «Acerca de…». Muestra sólo el correo confirmado por
el usuario (`juancgomezm@gmail.com`) y el repositorio del proyecto. El correo es
seleccionable; GitHub usa la apertura explícita de enlaces existente. Volver/Esc
regresa a Ayuda y de allí al lugar desde el que se abrió, sin lanzar consultas
de red ni acciones de modelo al entrar.

## Correcciones verificadas durante la revisión

La batería completa descubrió un único defecto del panel lateral: un refresh
podía sustituir su texto entre la selección de un enlace y Enter. Conservar las
coordenadas envueltas situaba el cursor en otra fila. El renderer conserva ahora
la identidad de la acción enfocada y resuelve su nueva posición; una prueba
inserta explícitamente una fila larga antes del enlace y lo activa con Enter.
También se inicializa la posición de origen al buscar una fila, evitando que una
asignación de cursor sin cambio reutilice un evento anterior.

Las pruebas de interacción esperan la entrega observable de la tecla, sin invocar
directamente el callback del enlace ni alternar dos veces una acción ya entregada.
La prueba de skills identifica su skill Project concreta para no asumir que el
usuario carece de skills instaladas. Las capturas de preferencias desplazan el
formulario a la región que verifican, conservando sus comprobaciones de foco.

## Evidencia

Los registros y las capturas están fuera del repositorio en
`C:/Users/juanc/.codex/omnicore-linux-20261014`, con prefijos `session-theme`
y `context-about`.
La validación usa Windows, un journal SQLite real para reapertura y fixtures
aisladas para proveedores y eventos. No acredita autenticación real con un
proveedor ni una ejecución de CI remoto. WSL no se inició.

Resultados previos a la última corrección del panel:

| Registro | Resultado | Interpretación |
| --- | --- | --- |
| `session-theme-final.log` | 990 correctas, 1 fallo, 0 omitidas | El fallo fue la activación lateral descrita arriba. |
| `context-about-core.log` | 724 correctas, 0 fallos, 0 omitidas | Contexto, sesiones, razonamiento, contratos, fingerprints y continuidad. |
| `context-about-tui.log` | 278 correctas, 1 fallo, 0 omitidas | El mismo fallo lateral; incluye preferencias, idiomas y capturas. |
| `context-about-architecture.log` | 62 correctas, 0 fallos, 0 omitidas | Restricciones de dependencias y contratos arquitectónicos. |

Con la corrección aplicada, `context-about-repaired2.log` registra 23 correctas,
0 fallos y 0 omitidas: ciclo de sesiones, compactación manual con herramientas y
steering, presión al 95 %, temas, Ayuda y enlaces laterales, incluida la inserción
de líneas envueltas. La compilación `context-about-build5.log` terminó sin errores
ni advertencias. Las capturas de Acerca de y de las tres paletas se renderizaron
y se inspeccionaron como imágenes obtenidas del driver.

`context-about-regressions.log` añade una pasada de 23 pruebas correctas, sin
fallos ni omisiones, sobre gestión de contexto (incluida la conversación de 200
turnos), aperturas de diff inmutable, preferencias/redimensionamiento del panel,
expansión por enlace, temas y Ayuda. No se deshabilitó ninguna prueba.

La aceptación final local `context-about-accepted.log` termina con **24 correctas,
0 fallos y 0 omitidas**, incluida la navegación de conversaciones y la presencia
de sus comandos/F6 en Ayuda. `context-about-build-final.log` registra **0 errores
y 0 advertencias**. Son pasadas focales tras la reparación; las dos baterías
ampliadas anteriores con el fallo lateral se conservan como evidencia, sin
presentarlas como ejecuciones completas en verde.
