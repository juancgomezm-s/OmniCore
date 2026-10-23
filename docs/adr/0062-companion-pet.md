# ADR-0062 — Mascota local como contribución integrada de extensiones

Estado: implementada en OmniCore (2026-10-22). OmniCoder no se modifica.

La contribución integrada `core.companion` usa el catálogo/pipeline de herramientas,
las vistas declarativas de extensiones y el panel lateral existentes. No inicia un
proceso de extensión, no introduce otro dispatcher de hooks ni incorpora contexto.
`companion.pet` está registrado para `tool.search` y sólo divulga su definición al
encontrarlo o ante una petición explícita de mascota/pet/companion. `/pet` se
resuelve en el cliente y usa el mismo servicio, sin inferencia. Antes de descubrirla
no se añade a preferencias ni al catálogo habitual de vistas.

La mascota pertenece al perfil de `IPlatformPaths.DataDirectory`, compartido por
sus workspaces/sesiones. `companion/pet.db` contiene el agregado, recibos SHA-256 de
identidades, cursores de proyección y límites de premios. Una transacción SQLite
IMMEDIATE confirma estado, deduplicación y cursores juntos; WAL permite lectores.
No es un segundo journal de conversación: no guarda mensajes, frames ni reacciones.
Se proyecta el journal canónico con recuperación idempotente. Los eventos previos
al descubrimiento no contribuyen; los followups cuentan al recibirse, no al
promoverse. No se repite un sorteo ya confirmado.

La incubación usa reglas locales explícitas y versionadas en Extensions. Se
excluyen código, citas, logs, respuestas de herramientas, orígenes sintéticos y
órdenes breves. Una coincidencia ambigua no añade señales. Sólo se almacenan
contadores agregados. Pesos: `1 + 4 * señales / mensajes`, con al menos tres
señales de una categoría. Al haber una sola categoría por mensaje, la suma de
pesos no supera 10 y cada especie conserva al menos 10 %. Un predominio rudo
puede llevar al buey al 50 %, sin determinarlo. El sorteo y la variante se
confirman en la transacción que alcanza el umbral (20 por defecto).

La evolución empieza después de eclosionar. Durante la incubación los efectos no
otorgan XP: el huevo no adelanta etapas. Después se usa
`max(1, floor(sqrt(xp / 10)))`: el mínimo visual 1 permite nacer como bebé con
0 XP, sin inventar una recompensa. Desde nivel 3 es joven, 6 adulto y 10 legendario.
Ánimo inicial 70; pierde un punto por diez minutos sin actividad hasta 30. Un ánimo
inferior por fallos no aumenta por inactividad. No hay muerte ni pérdida de XP.

Los premios proceden de resultados estructurados de ejecutores Core, nunca del
texto del modelo, de una cadena de shell que contenga “test” o de un exit code
aislado. Los eventos de outcomes añaden evidencia opcional versionada y compatible
con journals anteriores. Se verifica una edición por pre/post hashes distintos y
postimagen del runtime; los tests requieren un informe TRX/JUnit nuevo; un commit
requiere HEAD y árbol nuevos con parentesco verificado por el lector Git existente.
Cancelación, timeout, error de arranque, resultados sin evidencia, extensiones,
simulación, dry-run y operaciones sin cambios no producen XP. Se aplican límites
diarios y cooldown por tipo, además de deduplicación por ToolCallId/commit.

Protocol/Client exponen snapshots, cambios por revisión, acciones, preferencias
por cliente e IDs de apariencia/animación. Los cambios son una proyección pull,
no eventos nuevos del journal de ejecución. Las reacciones tienen prioridad y
caducidad; el cliente deriva el estado de ejecución real. El reloj local de 2.5 Hz
no avanza oculto, desconectado o con movimiento reducido. `PixelWidgetView` es un
renderer genérico declarativo; actualiza únicamente las celdas de su región nativa
y no recarga la conversación. A poca altura se presenta el resumen y `/pet` abre
el detalle. La alternativa ASCII es textual y las animaciones pueden ser estáticas.

Las escapadas son presentación local: `CompanionPetPlayground` calcula una ruta
acotada con carreras, miradas, zigzags entre renglones, saltos, volteretas,
agacharse/lanzarse, baile y esconderse. Alterna bandas y sentido entre salidas;
`CompanionRunnerView` dibuja
sprites pequeños originales de 12×3 celdas sin foco ni entrada. El sprite es opaco
y se superpone al texto sin reservar filas ni alterar el área de conversación.
El compositor nativo repinta las letras al moverse u ocultarse; sólo las celdas
dibujadas por la silueta o estela cubren texto. Los clics, enlaces y rueda llegan
al chat subyacente. El recorrido habitual se acota al viewport, excluyendo editor y barras.
`CompanionTypingChase` permite perseguir una copia efímera del último grafema
introducido: la TUI observa los cambios nativos sin consumir teclas ni escribir
en el editor. El objetivo salta sobre su borde superior y el animal lo persigue
con desplazamiento gradual, sin pintar las celdas del borrador o cursor. Sólo
se retiene ese grafema, durante un máximo de cinco segundos desde la última
adición; se descarta al suspender el juego. No se persiste ni transmite texto.
Se pausa detrás de menús y autocompletado; espacio insuficiente,
ASCII, desconexión o movimiento reducido lo detienen. Un botón fijo permite ocultar
la mascota con el sidebar cerrado, desde el encabezado. Salir/volver al panel sólo afecta al reloj local, sin eventos,
XP, procesos, proveedores ni cambios en el protocolo persistente.

Referencias: [ADR-0030](0030-arquitectura-del-cliente.md),
[ADR-0032](0032-sidebar-y-widgets.md), [ADR-0061](0061-plataforma-de-extensiones.md),
[integración y validación](../extensions/companion-pet.md).
