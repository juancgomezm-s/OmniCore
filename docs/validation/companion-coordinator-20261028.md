# Validación de la mascota coordinadora — 2026-10-28

Implementación y uso: [mascota-coordinadora.md](../architecture/mascota-coordinadora.md).

## Entorno y aislamiento

Windows, .NET SDK 10.0.401, destino net10.0 y Terminal.Gui 2.6.0-develop.61. Se construyó un archivo limpio de `main` más los archivos de esta entrega en `TestResults/companion-coordinator-source`, sin incorporar modificaciones ajenas del árbol de trabajo. No se actualizó el framework ni se añadieron paquetes de ejecución.

La validación del mini chat usa un proveedor falso y comprueba que no hay llamadas hasta enviar, que la solicitud no contiene herramientas y que la memoria no cambia automáticamente. No equivale a comprobar la respuesta de cada proveedor remoto con credenciales reales. El perfil Codex no recibe un límite de tokens no soportado. Las rutas por consumo quedan deshabilitadas en el mini chat hasta integrar el control monetario; el resto del coordinador funciona sin modelo.

## Pruebas

- Regresión `FullyQualifiedName~Companion`: 121 casos aprobados, sin fallos. Dos benchmarks explícitos no se ejecutaron en esa pasada.
- Prueba adicional de tristeza con `ImageView`: dos codificaciones Sixel distintas dentro del panel del mini chat, sin cambiar especie ni XP.
- Medición aislada adicional de cien sondeos del coordinador.
- Pruebas finales de resize, borradores, memoria y entrega al principal: siete aprobadas en `TestResults/companion-coordinator-final.trx`.

Los casos incluyen memoria compartida y conflictos de versión, límites de tamaño, preferencias persistentes, estados desconocidos tras desconexión, acuses, deduplicación, cola durante trabajo, rechazo de destino antiguo, decisiones caducadas y opciones no válidas. La entrega se valida también lanzando un segundo proceso .NET que reclama el mensaje desde el mismo perfil; no interviene ninguna ventana de terminal.

La prueba de decisión remota crea una interacción real de permiso en el journal y espera `InteractionResolved` del Core de origen. La instrucción remota entra por `StartModelTurn` y conserva borrador y foco. Las pruebas del panel abarcan 120×40 y 80×25; el cambio de tamaño mueve la tarjeta y conserva ambos borradores.

## Frames del renderizador

- `TestResults/companion-coordinator-frames/settings-companion-mini-chat-120x40.svg`
- `TestResults/companion-coordinator-frames/settings-companion-mini-chat-80x25.svg`
- `TestResults/companion-coordinator-frames/settings-companion-memory-120x40.svg`
- `TestResults/companion-coordinator-graphics/companion-sad-first.sixel`
- `TestResults/companion-coordinator-graphics/companion-sad-second.sixel`
- `TestResults/companion-coordinator-graphics/companion-graphic-chat.png`

Los SVG proceden de las celdas del driver de Terminal.Gui. El PNG gráfico combina esas mismas celdas con los píxeles del `ImageView` exportados por el driver, usando la posición y las dimensiones que reporta el buffer. Es evidencia del renderizador real en la prueba, con respuesta Sixel emulada y proveedor falso; no es una captura de una conversación enviada a un proveedor real ni una nueva homologación de Windows Terminal. No se modificó la mascota personal para preparar las capturas.

## Medición del canal local

`TestResults/companion-coordinator-measurements.json`: proceso aislado, cien ciclos completos de heartbeat, lectura del perfil y preferencia de avisos, sin modelo ni renderizador.

| Medida | Resultado |
| --- | ---: |
| Tiempo medio por ciclo | 1.59 ms |
| Percentil 95 | 2.19 ms |
| CPU acumulada de los cien ciclos | 156.25 ms |
| Asignaciones administradas acumuladas | 2,656,800 bytes |
| Memoria privada del proceso de prueba | 41,730,048 bytes |
| Almacenamiento del perfil de prueba | 106,936 bytes |
| Salida al terminal del canal | 0 bytes |

La memoria privada es el total del proceso de prueba, no un incremento atribuible a la mascota. Tampoco se presenta el tiempo de estos sondeos como latencia de teclado o coste de dibujar Sixel. En producción se ejecuta un sondeo cada dos segundos fuera del hilo de entrada/dibujo. Los sprites mantienen su cadencia anterior y no generan eventos por frame. La geometría del panel solo se modifica al cambiar pestaña, tamaño o modalidad.

Los nueve atlas pasan de 136,336 a 142,904 bytes en conjunto: **6,568 bytes adicionales** para los dos frames de tristeza por apariencia.

## Límites conocidos

El canal comunica instancias TUI actualizadas del mismo perfil/equipo. Los comandos CLI breves permiten inspección y mini chat textual, pero no anuncian un agente receptor persistente. Los cuestionarios con esquema se responden en su sesión original. La presencia tiene una tolerancia de quince segundos; un proceso cerrado puede aparecer brevemente. No hay garantía de enfocar ventanas, ni se necesita para entregar instrucciones o decisiones.

No se hicieron nuevas pruebas visuales en PowerShell clásico, SSH o tmux para este cambio. Se conservan las capacidades y respaldos gráficos previamente implementados. La comparación completa de CPU/salida del renderizado con y sin mascota está documentada en las validaciones anteriores; esta medición separa únicamente el canal añadido.
