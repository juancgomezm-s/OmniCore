# Mascotas por capacidades: implementación y validación

OmniCore conserva Core, las seis especies, incubación/evolución, identidad,
persistencia, `companion.pet` y `/pet`. El cliente selecciona la presentación.
No se ha actualizado Terminal.Gui ni añadido un proceso de gráficos al runtime.

## Uso

```text
/pet mode auto
/pet mode gráfico
/pet mode unicode
/pet mode texto
/pet mode oculto
/pet mode
/pet detect
```

`/pet mode` abre los controles **Vista de la mascota**, con modo solicitado y
efectivo; **Detectar de nuevo** permite reintentar explícitamente. El detalle de
`/pet` también contiene **Vista**. La preferencia se guarda por cliente.
`/pet hide` conserva la preferencia visual; `/pet show` recupera la mascota.
Si se había seleccionado `oculto`, `show` vuelve a `auto`.

| Modo | Resultado |
| --- | --- |
| auto | Sixel comprobado; en su defecto Unicode; si no, texto |
| gráfico / graphics | Solicita gráficos; explica brevemente el respaldo si no están disponibles |
| unicode | Bloques y colores compatibles; texto si la codificación/anchura no lo permite |
| texto / text | Nombre, especie y estado en una línea, sin sprites ni animación |
| oculto / hidden | Sin representación de la mascota |

Las escapadas y persecución de letras conservan el dibujo Unicode, incluso
cuando el panel usa Sixel: así mantienen su silueta, la superposición y la entrada
del chat. Se suspenden en texto/oculto. No se modifica el borrador ni se roba foco.

En headless o con redirección no se inicializa el renderizador gráfico. El CLI
conserva JSON por defecto y acepta `--json`; `--text` solicita una línea. Su JSON
añade `visual.requested`, `visual.effective`, `visual.backend` y `visual.reason`.
La herramienta conserva el snapshot estructurado de Core, independiente del
terminal. Ejemplo: `omni pet status --text`.

## Framework comprobado y alcance

La referencia exacta es **Terminal.Gui 2.6.0-develop.61**, commit
`145585dd5831527a11c6c70a4c4cc0b8e18b700d`, obtenido del nuspec instalado.

- [`ImageView`](https://github.com/tui-cs/Terminal.Gui/blob/145585dd5831527a11c6c70a4c4cc0b8e18b700d/Terminal.Gui/Views/ImageView/ImageView.cs)
  recibe `Color[,]`, admite Sixel/Kitty y mantiene cachés de escala/codificación.
  `UseBackgroundRendering` prepara el raster fuera del dibujo; el buffer del
  framework emite y retira imágenes en su propio ciclo.
- El [detector Sixel](https://github.com/tui-cs/Terminal.Gui/blob/145585dd5831527a11c6c70a4c4cc0b8e18b700d/Terminal.Gui/Drawing/Sixel/SixelSupportDetector.cs)
  usa respuesta DA y consultas de tamaño a través de `QueueAnsiRequest`.
  Se reutilizan sus resultados; no se infiere soporte gráfico por SO o nombre.
- El [detector Kitty](https://github.com/tui-cs/Terminal.Gui/blob/145585dd5831527a11c6c70a4c4cc0b8e18b700d/Terminal.Gui/Drawing/Kitty/KittyGraphicsSupportDetector.cs)
  sólo confirma soporte a partir de variables de entorno. El
  [lector de respuestas](https://github.com/tui-cs/Terminal.Gui/blob/145585dd5831527a11c6c70a4c4cc0b8e18b700d/Terminal.Gui/Drivers/AnsiHandling/AnsiResponseParserBase.cs)
  de esta versión no acumula APC con ST como respuesta de consulta. **Kitty no
  se activa en esta implementación**, aunque existan `KITTY_WINDOW_ID` o
  `TERM_PROGRAM=kitty`. Añadir una consulta insegura podría insertar su respuesta
  en el editor. Los contratos portables permiten incorporarlo cuando exista un
  mecanismo de verificación seguro. No se justifica una migración amplia.

La detección es asíncrona: el scheduler nativo abandona solicitudes inactivas
aproximadamente al segundo; el cliente limita su aceptación inicial a tres
segundos e ignora generaciones antiguas. No hay lector paralelo de stdin ni
consultas APC propias. Los cambios de tamaño solicitan métricas de celda con
debounce; una respuesta distinta invalida la escala. Un fallo gráfico queda
retenido durante la sesión hasta una acción explícita de detección.

Unicode exige codificación compatible, anchura de bloques comprobada por el
framework y ausencia de modo ASCII/`TERM=dumb`. No puede comprobarse la forma
real de cada glifo de una fuente: **texto** sigue disponible. Se respeta
`NO_COLOR`, la detección sin color y la paleta de 16 colores del driver. Para
endpoints de 256 colores se selecciona el subconjunto ANSI16: este writer del
framework ofrece ANSI16 o RGB, no ANSI256 indexado. Se mantiene RGB donde procede.

SSH, tmux y terminales integrados se evalúan en el flujo de terminal actual;
no heredan una autorización gráfica por su nombre. Un relay que anuncie Sixel y
luego descarte silenciosamente los datos no permite detectar ese fallo por ACK:
Sixel no proporciona esa confirmación. El usuario puede seleccionar Unicode/texto.

## Peso y ejecución

- Nueve atlas originales (seis especies, huevo y dos invitados), cada uno de
  **2304×128**: 18 frames de **128×128**, con expresiones, respiración y miradas.
  Total PNG: **136 336 bytes (133,1 KiB)**. Son recursos embebidos, sin accesos
  por frame al sistema de archivos. Las mediciones de abajo corresponden al buey;
  no se han repetido como comparación de CPU entre especies o invitados.
- `StbImageSharp 2.30.16` ya formaba parte del cierre de dependencias de Host;
  la referencia explícita de CLI reutiliza ese mismo paquete. No se añade otra
  biblioteca de imagen ni Chafa, ImageMagick o un ejecutable de conversión al runtime.
- Caché acotada a dos atlas decodificados. La carga/decodificación se ejecuta
  en tareas; la TUI no espera sincrónicamente. Las variantes cálidas/frías,
  manchas y corona legendaria se preparan una vez en esa tarea.
- El mismo array se reutiliza mientras el frame no cambia. Cada vista conserva
  hasta seis ImageView, reutilizando sus cuadros ya escalados y codificados.
  La escala/codificación se realiza mediante su backend de preparación asíncrona.
- El panel avanza cada **600 ms** como máximo. En reposo alterna respiración,
  un pequeño acomodo, parpadeo y miradas dentro de un ciclo nominal de 19,2 s, con pausas
  estáticas durante el 78 % de los pasos. Trabajo/espera usan ráfagas breves;
  al dormir respira suavemente. Las escapadas conservan su cadencia de 400 ms.
  Éxito/fallo/caricia respetan las reacciones efímeras existentes de Core.
- Se invalida la región de la mascota. Ocultar/cambiar modo/cerrar panel/abrir
  menús retira el ImageView y sus comandos de raster. El framework elimina
  presentaciones y repinta sus celdas. Al salir se invalida la generación del
  detector y se libera la caché. Movimiento reducido, desconexión y falta de vista
  detienen el avance. Una preparación ya iniciada puede terminar; no se encadena
  otra para una vista retirada.
- No se persiste ni transmite ningún frame. La preferencia no cambia especie,
  interacciones, XP o evolución. Protocol/Client no referencian Terminal.Gui.

Los scripts de preparación/validación usan herramientas de desarrollo disponibles
por separado. No son targets de compilación, no se ejecutan al usar la mascota y
no se publican como dependencias de la aplicación.

## Pruebas y evidencia real

Entorno: Windows **10.0.26300.0**, .NET **10.0.12**, driver DOTNET de Terminal.Gui
**2.6.0-develop.61**. La TUI corre con Host/journal reales y perfiles sintéticos.
Los resultados de capacidades se controlan en la fixture, porque el runner
redirigido no es por sí mismo un terminal Sixel.

El recorrido del buey cubre reposo, trabajo, espera, celebración, fallo y caricia;
cambio de tamaño y métricas de fuente; cerrar/abrir panel; menús; limpieza; foco,
cursor, selección y desplazamiento; movimiento reducido; fallo inyectado del
backend; ausencia de reintentos y recuperación mediante `/pet detect`.
Las otras cinco especies reutilizan ese renderer y comprueban cambio a Unicode.
Un proceso CLI independiente comprueba texto/JSON sin ESC con salida redirigida.

Resultado focal: **141 casos correctos**: 51 de contratos/reglas/assets/lector y
headless, 27 de TUI y caché visual, 62 de arquitectura y el benchmark explícito.
Las comprobaciones posteriores del detalle textual compacto y del control de
ocultación también pasaron. Build de tests/CLI y Client net8 sin advertencias.

Vistas de respaldo y controles, procedentes del driver real:
[Unicode](settings-companion-ox-sidebar-120x40.png),
[texto](settings-companion-ox-text-100x36.png) y
[modo solicitado/efectivo y redetección](settings-companion-mode-settings-120x40.png).
El detalle completo tiene 22 filas para incluir el selector de vista; la tarjeta
compacta y el modo textual usan 14, evitando reservar espacio para un dibujo ausente.

Los **frames Sixel producidos por ImageView** se conservan como `.sixel`, con
coordenadas/métricas en `.raster.json`; el decodificador de preparación produce
sus PNG. [Seis estados del buey](ox-six-states.png). Los PNG `*-terminal.png`
son composiciones de esos datos con celdas reales del driver: reconstrucciones
de su salida, no fotografías de un emulador.

También se reprodujeron esos datos en un **emulador Sixel real**:
**xterm.js 5.5.0 + @xterm/addon-image 0.8.0**, en **Edge 155.0.4283.62** headless.
Respondió `ESC[?62;4;9;22c` a DA y `ESC[6;17;8t` al tamaño de celda.
La prueba del detector real del framework consume esas respuestas grabadas.

- [Trabajo en xterm.js](xterm-ox-working.png), [caricia](xterm-ox-pet.png).
- [Fuente de tamaño 20 y terminal redimensionado](xterm-ox-font20.png).
- [Pantalla borrada](xterm-cleared.png): el almacenamiento de imágenes volvió a cero.
- [Registro de versiones, respuestas y resultados](xterm-validation.json).

Las capturas xterm son capturas del emulador reproduciendo frames de ImageView;
no representan un proceso OmniCore conectado de extremo a extremo a ese navegador.
Quedan pendientes sesiones interactivas completas en Windows Terminal, Kitty/
Ghostty, terminales físicos adicionales y combinaciones concretas de SSH/tmux.
No se afirma compatibilidad comprobada para esos productos o versiones.

Resultados reproducibles: `TestResults/pet-render-regression.trx`,
`pet-graphics-final.trx`, `pet-visual-final-contracts.trx`,
`pet-render-architecture.trx`, `pet-text-layout-final.trx` y
`pet-graphics-benchmark.trx`. El benchmark es explícito y no prolonga las pruebas
habituales. Los TRX/logs pertenecen a la salida local de pruebas, no al runtime.

## Mediciones

La ampliación de animaciones del panel tiene [mediciones y validación propias
del 27 de octubre](panel-animation-20261027.md). Los resultados siguientes
corresponden a la implementación inicial y conservan su contexto histórico.

[Datos completos](benchmark.json): tres muestras de **8 segundos** por caso,
precedidas de preparación y un segundo de estabilización, a 120×40. Misma TUI,
Host/journal, buey, panel abierto y sin escapadas; se cambia únicamente el modo
y la expresión. El writer contabiliza los bytes UTF-8 que emite el driver; su
destino controlado no añade el coste de pintar píxeles de un emulador.

CPU es porcentaje de **un núcleo**, para el proceso completo. Memoria es el heap
administrado retenido tras GC y los bytes privados del proceso, no sólo el dibujo.
La tabla presenta medianas, sin descontar el coste normal del chat.

| Caso | CPU % | Heap MiB | Privados MiB | Salida B/s |
| --- | ---: | ---: | ---: | ---: |
| Oculta, reposo | 1,76 | 19,77 | 59,31 | 127 |
| Texto, reposo | 0,98 | 19,76 | 58,18 | 127 |
| Unicode, reposo | 0,78 | 19,76 | 55,95 | 127 |
| Sixel, reposo | 1,75 | 20,67 | 57,04 | 127 |
| Unicode, trabajo | 2,15 | 19,79 | 57,28 | 4 691 |
| Sixel, trabajo | 2,73 | 20,67 | 56,96 | 6 702 |

En esta ejecución el atlas/raster añade aproximadamente **0,9 MiB** de heap;
el modo Sixel durante trabajo produjo más salida que Unicode. La CPU presenta
variación apreciable: por ejemplo, el caso oculto osciló entre 1,17 y 8,98 %,
incluyendo el arranque del primer ensayo. Los bytes privados dependen además
del JIT/GC y del orden de los casos. No deben interpretarse como una reducción
garantizada de memoria al activar gráficos. Las ventanas de reposo de ocho
segundos pueden no incluir el parpadeo, cuyo período es de veinte segundos.
No son una comparación universal entre Sixel y Kitty; Kitty no se midió.

## Reproducción

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj
dotnet build src/OmniCore.Client/OmniCore.Client.csproj -f net8.0
dotnet run --project tests/OmniCore.Tests --no-build -- -class '*CompanionVisualTests' -class '*CompanionHeadlessTests' -class '*CompanionPetTests'
$env:OMNICORE_PET_GRAPHICS_CAPTURE_DIR = "$PWD/docs/validation/companion-graphics"
$env:OMNICORE_TUI_SNAPSHOT_DIR = $env:OMNICORE_PET_GRAPHICS_CAPTURE_DIR
dotnet run --project tests/OmniCore.Tests --no-build -- -method '*Companion_graphics*'
node tools/render-companion-evidence.cjs docs/validation/companion-graphics <ruta-a-sharp>
$env:OMNICORE_PET_BENCHMARK_FILE = "$PWD/docs/validation/companion-graphics/benchmark.json"
dotnet run --project tests/OmniCore.Tests --no-build -- -explicit only -method '*Companion_rendering_measures*'
```

Para regenerar el arte: `node tools/prepare-companion-assets.cjs <ruta-a-sharp>`.
Para las capturas de emulador, instalar los dos paquetes xterm con las versiones
indicadas en una carpeta temporal y ejecutar
`node tools/verify-companion-xterm.cjs <node_modules-de-xterm> <ruta-a-playwright> <carpeta-de-evidencia>`.
La prueba usa Edge headless; no requiere esos paquetes al ejecutar OmniCore.
