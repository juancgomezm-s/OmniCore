# M10: evaluación y memoria entre ámbitos

## Cualificación

`omni model qualify <modelo> --suite full --yes --max-cost <USD>` añade diez fixtures
C# a los diez probes de quick. Cada fixture tiene archivo inicial, versión SHA-256,
edición exacta y aserción fijados por la suite `omnicore-full` 1.0.0. La identidad de
tareas incluye prompts, oráculos y pruebas concretas. No se ejecuta al descubrir un
modelo, ni se cambian automáticamente su política de mutación o routing.

Host usa el proveedor configurado y los mismos guards de consentimiento, precio,
cuotas, reserva, uso reportado, cancelación y costo de M5. Un código que sólo coincide
con JSON no basta: se aplica con `filesystem.patch` a un worktree Git propio,
se restaura/compila con el SDK y se ejecuta su aserción. Sólo se compila la postimagen
idéntica al oráculo conocido; la salida arbitraria de un modelo nunca llega al compilador.
Los procesos no heredan credenciales y usan home/cache/config propios. No se instala
un SDK, workload, dependencia del proyecto ni sistema operativo.

El trait resultante `CodeFixtureEditReliability` describe estas tareas acotadas, no
coding general, seguridad de mutaciones operativas o native tool calling. Probes
erróneos dejan un perfil provisional; una batería incompleta no publica cualificación.
El costo/uso de una llamada ya realizada se conserva aun si falla la validación local.

## Calibración observada

`omni model calibration <modelo>` consulta evidencia sin llamar al proveedor. La captura
del runtime fija clave completa, ruta, Session, Run, Lane y eventos nuevos de la
invocación. Recibos por EventId/contador evitan sumar otra vez al reabrir/repetir.
El agregado SQLite y los traits se confirman juntos y conservan los traits previos.

Los contadores separan pasos completados, requests de herramientas, argumentos
aceptados/rechazados, éxito, estancamientos y rechazos de gates atribuibles a esa ruta.
Una recuperación de argumentos significa éxito del mismo nombre de herramienta en un
paso de modelo posterior al rechazo tipado dentro del mismo Turn. No demuestra una
reparación semántica general. No se atribuyen eventos de otra ruta ni eventos sin scope
suficiente. Su ausencia se presenta como falta de evidencia.

`ToolArgumentValidity` y `ArgumentRepairSuccess` tienen muestras/confianza/fuente
explícitas. Tras 20 pasos completados y 20 observaciones de validación de argumentos,
un perfil previamente Qualified puede pasar a Calibrated. Esto no cambia permisos,
categoría, ruta, MutationPolicy ni los traits de FileMutationReliability. Los recibos
se limitan a 100.000 por clave; saturar telemetría nunca repite una invocación ni falla
el Run. No se guardan prompts, respuestas o rutas en esas tablas.

## Evidencia de memoria

Project y Workspace pueden promover tres propuestas humanas coincidentes de tres
Sessions distintas del journal del workspace actual. Primero se hace durable la
propuesta privacy-screened; después se registra un índice de hashes. Ese índice no es
autoridad: cada referencia se resuelve de nuevo contra una propuesta User real, mismo
scope/kind/contenido, sin rechazo/olvido ni resolución humana reject. Una Session de
otro clone inaccesible, un voto huérfano o refs del modelo no cuentan. Global siempre Ask;
un conflicto con memoria activa también pide confirmación.

La decisión por evidencia y Prepared quedan en el journal antes del cambio SQLite.
Al recuperar, un recibo ya aplicado se reconoce antes de revalidar evidencia, evitando
resucitar un recuerdo olvidado. Sin Prepared, una decisión automática interrumpida
vuelve a Ask con revisión/conflictos actuales. Una precondición rechazada termina esa
generación; un nuevo encargo humano puede crear otra.

Olvidar conserva un tombstone de hash y retira votos para impedir una promoción
automática posterior del mismo contenido; la confirmación humana explícita sigue
disponible. El ledger reserva capacidad para borrar los registros vivos. La migración
de un ledger antiguo lleno permite como máximo 500 recibos adicionales sólo de olvido.
No se podan recibos que podrían volver a autorizar un Prepared antiguo. La consolidación
semántica/vectorial permanece diferible según ADR-0028.

## Comparación externa

`tools/evaluation/compare.py` compara receipts neutrales con la misma identidad,
sampling y cobertura de tareas. Conserva métricas ausentes como null y rechaza
duplicados, números inválidos o suites distintas. Genera reportes es/en/fr/it.
Comparar identidades declaradas no verifica la procedencia de esos receipts.

Esto no es la comparación live de spec §97. Esa aceptación exige la misma regression
suite en OmniCoder + Pi y Omni CLI + OmniCore, con success rate, tiempo, Turns,
tool calls, tokens, crecimiento de contexto, escaladas y costo. No se sustituyó por
outputs de un proveedor scripted ni se modificó OmniCoder. El ensayo live del modelo
y el baseline de su coordinación permanecen separados de las pruebas deterministas.

## Evidencia de aceptación

La compilación Release de solución y pruebas focales con fixtures usan únicamente
directorios temporales; no consumen servicios de modelos ni usan la GPU del usuario.
Los resultados definitivos se registran en `m10-closure-20261030.md` y XML de artifacts.
Los casos de caída de este incremento son fallos inyectados; las caídas OS de memoria
y updater de la base mantienen sus actas separadas. No se atribuye aquí aceptación
lingüística de modelos, servidores LSP reales del usuario ni consumidor OmniCoder.

## Aceptación live de full con ChatGPT

El usuario eligió expresamente la suscripción ChatGPT ya configurada. El 30 de
Octubre, entre 15:54:26 y 15:55:58 UTC, la CLI Release ejecutó `model qualify
gpt-6.1-sol --suite full --yes --max-cost 1.00 --probe-timeout 120` en un workspace
temporal, por `chatgpt`/Responses perfil `codex`, `IncludedQuota`. No se copió una
credencial ni se usó API key, GPU local o un proveedor scripted. El store de usuario
registró la evidencia de esta cualificación; no cambió la política del modelo.

Resultado: **20/20 Passed**, incluidos diez fixtures de parche/compilación/aserción;
`Declared → Qualified`, revisión 1, clave
`cfeae25e2803cd24e5cd810457f7994d513f56fbb2de078853ff20bfd6e625b0`.
CodeFixtureEditReliability=1.00 (10 muestras), InstructionFollowing=1.00 (7) y
StructuredOutputReliability=1.00 (3). Las duraciones registradas de los probes
suman 77.9144147 s; el tiempo de pared del proceso fue aproximadamente 91.5 s,
incluyendo inicialización y admisión. Los veinte probes conservan usage reportado;
2.111 tokens de entrada y 810 de salida reportados (cache/razonamiento son subconjuntos,
no se suman otra vez). El costo monetario quedó desconocido, no se lo convierte
en cero por ser suscripción.

Artifact canónico SHA-256
`878a5aa8b3dd3e15febd0b6a38381f9e04443a8be1f8916638d75875aea9b4d4`,
20.512 bytes, verificado contra el blob User y exportado en
`artifacts/m10-live-chatgpt/qualification-evidence.json`. Log, clave exacta y recibos
están en ese directorio. Imagen CLI SHA-256
`ae7576ba2af8868719e4790bd876d554a4680fc806b427f0c053f578195906ad`;
candidata fuente `d262b4caf7de3c95897b753fb9ea5c96b19c6003`. La build de desarrollo
no es la release final: su identidad exacta no se transfiere artificialmente al
paquete publicado. La aceptación del paquete y su instalación se registran aparte.

Esto acredita llamadas reales y estas tareas acotadas; no generaliza a capacidad
de programación ni acredita la comparación OmniCoder/Pi. La búsqueda de reportes
en la documentación de OmniCoder encontró sólo benchmark de septiembre y mapa
de capacidades, no una tanda equivalente a `omnicore-full` 1.0.0 de este corte.
El recibo neutral Core exportado (`artifacts/m10-live-chatgpt/omnicore-regression-live-receipt.json`)
conserva identidad y métricas conocidas; deja turns/toolCalls/context growth/escalations
en null porque ese arnés no las observó. No se fabricó un segundo brazo ni se
ejecutó el comparador sin el recibo de OmniCoder/Pi.
