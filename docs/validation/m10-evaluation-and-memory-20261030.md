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
