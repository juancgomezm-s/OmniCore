# Validación de la mascota — 2026-10-22

Implementación local en OmniCore, sin modificar OmniCoder, sin dependencias nuevas
ni llamadas adicionales a proveedores. Compilación de tests/CLI y de Client/Protocol
para net8 correcta, sin advertencias.

Resultados:

- `CompanionPetTests`: 29 casos correctos. Incluyen servicio, persistencia SQLite,
  perfil/cliente, reglas y datos excluidos, piso probabilístico, semilla, eclosión
  estable, concurrencia entre sesiones, replay, identidad de mensajes/commands,
  followups, cooldown/caps, outcomes verificables, informe nuevo/antiguo/erróneo,
  Git real con objetos empaquetados/worktree enlazado y edición efectiva real.
- `TuiWiringTests.CompanionPet`: 8 casos correctos. Seis especies y huevo, teclado,
  ratón nativo, redimensionado, detalle textual mínimo, reducción de movimiento,
  ocultación, descubrimiento y preferencias, chat intacto y ausencia de inferencia.
  El botón **Ocultar** restaura la altura original del panel, devuelve el foco al
  compositor, conserva el desplazamiento del chat y guarda la preferencia. El
  botón **Ocultar mascota** del detalle también cierra su ventana.
- Regresión focal: proceso, codecs y upcasters, catálogo/discovery y plataforma de
  extensiones. Una fixture de ejecutable Python con shebang se omite en Windows.
- Arquitectura: 62 casos correctos; se conserva el grafo de referencias y los
  frameworks visuales permanecen en CLI.
- CLI real, con perfil aislado en `TestResults/pet-cli-profile`: `pet discover
  --json`, `/pet rename Luna`, `pet hide`, `pet status`. El estado JSON conserva el
  huevo, nombre y visibilidad al abrir procesos separados.

Los resultados TRX de las ejecuciones están en `TestResults/companion-*.trx`
(artefactos locales ignorados por Git). Los [frames entregados](companion-frames/README.md)
son evidencia del renderer y driver reales, con perfiles sintéticos; no son maquetas.

Recorrido probado: tool ordinaria → servicio compartido → transacción/recibos →
proyección del journal/outcomes → Query/cambios por revisión → cliente → región
nativa de la TUI. `/pet` y headless llegan al mismo servicio sin inferencia.

Los premios de tests requieren informes TRX/JUnit compatibles; lo desconocido no
se premia. Las capturas no certifican todos los terminales humanos.
Véase la [guía de uso e integración futura](../extensions/companion-pet.md).

Corrección visual del huevo: se retiraron el pie marrón y las manchas oscuras,
con una punta más estrecha, base redondeada y sombreado suave. La caricia muestra
un corazón con dos lóbulos y punta que asciende sin desplazarse lateralmente.
Compilación correcta y ocho pruebas focalizadas correctas (seis especies, huevo
con ratón y renderer portable); resultados en `TestResults/companion-egg-shape.trx`.
Se revisaron las capturas reales del [huevo acariciado](companion-frames/settings-companion-egg-pet-100x30.png)
y del zorro para comprobar el adorno compartido.

El detalle de la mascota ahora ajusta el ancho al resumen y los controles, en vez
de ocupar el 80 % de la terminal. La tarjeta completa tiene 20 filas; con menos de
24 filas de terminal usa 11 filas de controles compactos. La prueba del huevo
comprueba los límites y la ausencia de solapamientos, reduce la terminal con la
tarjeta abierta a 80×20 y la amplía de nuevo a 100×30. Compilación sin advertencias;
siete casos restantes correctos en `TestResults/companion-card-layout.trx` y el caso
de redimensionado corregido y correcto en `TestResults/companion-card-resize.trx`.

Las escapadas usan ahora una mascota opaca superpuesta a las letras, sin franja
reservada ni cambios en la geometría de la conversación. Puede tapar texto
temporalmente, que reaparece al moverse u ocultarse. Los clics, enlaces, selección
y rueda siguen llegando al chat subyacente; el editor y las barras quedan fuera
del recorrido. El control de ocultación permanece en el encabezado.

Validación de esta superposición: 50 casos correctos, cuatro de ruta/sprites,
seis de la TUI con mascota y 40 de regresión de panel, conversación y enlaces.
Resultados en `TestResults/companion-overlay-{clock,tui,regression}.trx`.
Las capturas comparadas celda a celda comprueban que la mascota opaca tapa letras,
que el texto ajeno a su silueta permanece igual y que al moverse u ocultarse se
restauran tanto los glifos como sus atributos. Un clic sobre un píxel del animal
activa el enlace subyacente; la rueda desplaza la conversación. También se conserva
el rectángulo original del chat, borrador, cursor, foco y copia/selección.
Compilación de tests/CLI y Client net8 correcta, sin advertencias.

Repertorio ampliado a nueve poses: carrera, curiosidad, zigzag entre renglones,
saltos, volteretas, agacharse/lanzarse, baile, sprint y escondite. El recorrido
varía de altura y sentido entre salidas y permanece cerca del contenido en chats
cortos. Se revisaron los frames reales de zigzag, voltereta, baile y escondite.
La validación focal de esta ampliación suma 21 casos correctos: cinco de ruta y
sprites y 16 de TUI (incluye las ocho pruebas previas de mascota). El recorrido de
las nueve poses compara las celdas antes, durante y después, y otra prueba acota
las travesuras a los renglones de una conversación corta. Resultados en
`TestResults/companion-mischief-{clock,tui}.trx`. Build de tests/CLI y Client net8
correcto, sin advertencias.

Persecución durante la escritura: una copia del último grafema salta sobre el
borde del editor y el animal corre hacia ella o se agacha para atraparla. El
objetivo caduca a los 12 pasos del reloj local (aproximadamente cinco segundos),
sin persistir ni transmitir borradores. Las celdas del editor y cursor quedan
fuera del dibujo. Ocultar también retira la letra y restaura el borde.

Validación focal de esta incorporación: 27 casos correctos, ocho de ruta/sprites
y persecución (`TestResults/companion-typing-clock.trx`) y 19 de TUI
(`TestResults/companion-typing-tui.trx`). Los nuevos casos usan teclas nativas sin
reparar el foco, comprueban borrador/cursor/journal intactos, caducidad, ocultación
y restauración de glifos/atributos, con y sin color. Otro caso cubre pegado con
acentos, caracteres anchos y emoji, líneas envueltas, reducción a 80×25, inserción
dentro del borrador, borrado, navegación y vuelta al panel. El portapapeles de
prueba permanece intacto. Se revisó la captura real
`settings-companion-fox-typing-120x40.png`. Build de tests/CLI y Client net8
correcto, sin advertencias.
