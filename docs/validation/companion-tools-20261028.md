# Herramientas exclusivas de la mascota — 2026-10-28

Uso y catálogo: [mascota-coordinadora.md](../architecture/mascota-coordinadora.md).

## Implementación

Catálogo cerrado de siete herramientas: `memory.read`, `memory.remember`, `memory.forget`, `sessions.list`, `notices.list`, `agent.message` y `pet.react`. El mini chat no importa el registro de herramientas del agente principal. Las llamadas desconocidas y argumentos fuera del contrato se rechazan como resultados de herramienta; no se ejecuta texto libre como una acción.

El bucle conserva los identificadores de llamada, los resultados y la continuación opaca del mismo proveedor/modelo. Cada respuesta del modelo se registra en el contador propio, incluidas las respuestas intermedias con llamadas a herramientas. Se mantienen selección propia de modelo y ausencia de cuotas monetarias.

Un envío exige un destino indicado por la persona y una instrucción literal de su petición. Si varios receptores coinciden, no se escoge uno automáticamente. El registro y la bandeja existentes validan nuevamente instancia, sesión y Run. Se deduplican envíos repetidos dentro de la misma consulta, incluso si cambia el orden de propiedades de los argumentos.

Los recuerdos se editan dentro del archivo existente, conservando encabezados y hechos ajenos al recuerdo elegido. Se utilizan identificadores estables y la versión del archivo para detectar conflictos. No existe un parámetro para acceder a otros archivos. Las reacciones son breves, compartidas entre instancias, y no modifican el estado de evolución ni la restricción de la mascota.

## Pruebas

Entorno: Windows, net10.0, Terminal.Gui 2.6.0-develop.61. Compilación Release de un archivo limpio de `main` más únicamente los archivos de esta entrega; no se incluyen cambios ajenos del árbol de trabajo.

`TestResults/companion-tools-regression.trx`: **138 pruebas aprobadas**, cero fallos; dos benchmarks explícitos sin ejecutar. Se cubren herramientas permitidas, rechazo de herramientas del principal, argumentos extra, memoria de una línea y deduplicación, eliminación selectiva, destinos ambiguos o desconectados, destino actual, instrucciones no indicadas por la persona, conservación de estado del proveedor y contabilización de varias respuestas. Se comprueba también el límite técnico que impide un bucle sin fin.

La prueba de integración TUI conecta un proveedor falso con las herramientas reales, guarda un recuerdo, encola una instrucción y espera que el receptor la admita por `StartModelTurn`. Conserva el borrador y el foco del mini chat, refresca el editor de memoria y presenta la reacción de celebración. El turno del principal es un host falso; no se envía trabajo a un proveedor real.

Captura del driver de Terminal.Gui: `TestResults/companion-tools-frames/settings-companion-tools-message-120x40.svg` y PNG del mismo nombre. Es una captura del renderizador en la prueba, con datos sintéticos, no una conversación de producción.

## Protocolo HTTP completo

Proveedor HTTP/SSE simulado local, declarado `MeteredCurrency`, sin credenciales reales. Los comandos CLI seleccionaron `paid-pet` en un perfil aislado y conversaron con el proveedor a través del adaptador real. El servidor respondió usando los nombres de función serializados por ese adaptador.

Primera petición humana: `Recuerda: Me llamo Juan. Dile a Alpha: revisa las pruebas`. Tres respuestas del modelo ejecutaron guardar recuerdo, consultar sesiones/avisos, enviar la instrucción y celebrar. Se devolvió el identificador del envío y su estado **en cola**. Segunda petición: `Olvida el recuerdo de Juan`; tres respuestas más consultaron el recuerdo y lo eliminaron por su identificador.

El contador final registró **6 solicitudes, 6 respuestas, 720 tokens de entrada, 180 de salida y 0.01080 USD de estimación tarifaria sintética**. No fabricó un coste reportado por el proveedor. Esta prueba no genera cargos reales ni cuenta como homologación de todos los proveedores remotos.

Evidencia: `TestResults/companion-tools-http/request-{1..6}.json`, `reply.json`, `forgotten.json` y `usage-final.json`. La continuidad HTTP incluye resultados de herramientas y solo ofrece las siete definiciones autorizadas.
