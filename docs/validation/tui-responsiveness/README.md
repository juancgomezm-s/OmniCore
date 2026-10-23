# TUI: respuesta al desplazamiento y coste de la mascota

Validación del 23 de octubre de 2026. Base: `de73da7b1d73f4af8a0376641e790e128f01418d`.
Cliente publicado e instalado después de los cambios: SHA-256 de `omni.dll`
`CFD674F00887FE41F2B0B539CAF35B8EEAAD80060C606CC89F8974EEF272DB53`.

## Cambios

- El historial mueve tres filas por paso de rueda, conservando borrador, cursor,
  selección y foco. Un desplazamiento que sólo cambia la posición ya no compara
  todos los mensajes para decidir si necesita reconstruir el historial.
- Los colores del texto se resuelven una vez por combinación de atributos y se
  reutilizan al dibujar las celdas. Caché acotada a 256 entradas, invalidada al
  cambiar tema, atenuación o `NO_COLOR`. La variable de entorno se consulta una
  vez por dibujo, en lugar de una vez por celda.
- Se permiten 50 iteraciones por segundo durante un gesto de entrada. Después
  de 500 ms sin entrada se recupera el límite original. Esto no solicita dibujos
  ni acelera el sondeo del journal. Una configuración explícita del framework
  se conserva, y el límite compartido se restaura al salir del cliente.
- La consulta de la mascota pasa de cada 500 ms a cada segundo cuando está
  visible, y cada tres segundos cuando está oculta. Una sesión o secuencia nueva
  invalida la espera; las acciones locales siguen siendo inmediatas.
- Se elimina la presentación duplicada de la mascota durante un mismo sondeo y
  se reutilizan sus modelos mientras no cambien estado, expresión, frame o tamaño.
  Se conservan como máximo los tamaños de presentación del estado actual.
- Los sprites del panel pasan de un paso cada cuatro ticks a uno cada seis
  (aproximadamente 400 a 600 ms). El reposo conserva su imagen y tiene parpadeos
  ocasionales. Las travesuras conservan su cadencia de 400 ms. Al leer con rueda
  o teclas de navegación se pausa el movimiento durante 300 ms y se cancela la
  persecución de letras para evitar que sobreviva a un cambio rápido de foco.
- Las travesuras reutilizan sus píxeles sin cambio y sólo solicitan layout cuando
  cambia la posición. Oculta o con movimiento reducido, la mascota deja de entrar
  en el trabajo periódico de animación.

No se cambian especies, evolución, XP, persistencia ni reglas de invitados.

## Comparación controlada

Mismo equipo Windows, .NET 10.0.12 y Terminal.Gui **2.6.0-develop.61**, driver
DOTNET real, 120×40 celdas, conversación persistente de 160 líneas y mascota buey
en el panel. Salida contada en UTF-8; capacidad Sixel controlada por el harness.
**No es una medida de Windows Terminal físico ni del visor del navegador.**

Cada modo tiene tres repeticiones: tres segundos en reposo y 30 cambios de
viewport con espera al evento real `LayoutAndDrawComplete`. La tabla promedia
las dos repeticiones posteriores al calentamiento. Los datos completos,
incluido el calentamiento, están en [before.json](before.json) y
[after.json](after.json). Las latencias incluyen la entrega al hilo UI y la
notificación de dibujo completado; no incluyen el transporte al visor.

| Mascota | Mediana por desplazamiento antes → después | P95 antes → después | CPU de 30 desplazamientos antes → después |
|---|---:|---:|---:|
| Oculta | 93,6 → 65,9 ms | 100,9 → 79,3 ms | 820 → 531 ms |
| Unicode | 128,5 → 76,6 ms | 209,5 → 87,6 ms | 1195 → 797 ms |
| Gráfica Sixel | 93,8 → 77,1 ms | 108,7 → 97,5 ms | 875 → 898 ms |

| Mascota | CPU en 3 s de reposo antes → después | Bytes en 3 s de reposo antes → después | Bytes administrados asignados en reposo antes → después |
|---|---:|---:|---:|
| Oculta | 31 → 16 ms | 381 → 390 | 1 231 024 → 1 158 856 |
| Unicode | 211 → 133 ms | 366 → 387 | 1 559 532 → 1 004 968 |
| Gráfica Sixel | 133 → 148 ms | 18 074 → 9 201 | 46 477 580 → 23 599 004 |

Los modos oculto y Unicode registraron cero notificaciones de dibujo completo
en reposo; las ventanas gráficas con un parpadeo registraron cuatro antes y dos
después. El driver sigue emitiendo pequeñas actualizaciones del cursor. El parpadeo
gráfico puede caer dentro de una ventana corta y provocar escalado/codificación
asíncronos, salida y asignaciones muy superiores. Las cifras de asignación son
asignaciones acumuladas del proceso, **no memoria retenida**. No permiten concluir
que el consumo gráfico sostenido de CPU haya bajado: aquí mejora la latencia,
pero la CPU de Sixel queda prácticamente igual o algo por encima. Tampoco se
deduce que un protocolo gráfico consuma siempre menos que otro.

## Comprobaciones y sesión real

La regresión focal de conversación, temas, sprites, limpieza gráfica,
travesuras y escritura pasó 161 casos (más un benchmark explícito no ejecutado
como regresión). Otra ejecución pasó 33 casos adicionales, con solapamiento de
algunos casos unitarios de enlaces: **169 casos únicos pasados, cero fallos**.
Hay pruebas específicas de rueda/foco/borrador/selección,
observación de cambios externos de la mascota, aceleración temporal del lector
y conservación de un límite explícito del framework. Se comprueban además los
enlaces y el autocompletado a través de las vistas reales.

Se actualizó la instalación desde el checkout con `scripts/install.ps1`, se
verificó que el DLL instalado coincide con el publicado y se reinició la sesión
real con su historial. Se abrió Windows Terminal **1.24.12741.0** para una prueba
independiente de escritura; el usuario confirmó que responde mejor. Esta prueba
cualitativa no reemplaza las medidas de un terminal físico bajo carga prolongada.

El visor local de desarrollo transmitía capturas completas cada 400 ms mediante
un flujo de imágenes, y no reenviaba la rueda. Se sustituyó por peticiones
secuenciales de la última imagen disponible, sin cola de imágenes atrasadas,
con captura limitada a aproximadamente 10/s y suspensión sin espectadores.
La rueda ahora se reenvía a la terminal. El visor sigue añadiendo captura,
transporte y presentación de imágenes: su latencia no representa por sí sola
la respuesta del cliente nativo. Estos cambios del visor son auxiliares de
desarrollo, en `TestResults`, y no forman parte del runtime de OmniCore.

## Repetir la medición

```powershell
dotnet build tests/OmniCore.Tests -c Release
$env:OMNICORE_TUI_BENCHMARK_FILE = "$PWD/TestResults/tui-responsiveness.json"
dotnet tests/OmniCore.Tests/bin/Release/net10.0/OmniCore.Tests.dll -method '*Tui_measures_idle_output_and_completed_scroll_frames' -explicit on
```

El benchmark utiliza directorios y un servidor de prueba aislados, sin modelos
remotos, cuenta real ni modificaciones del historial o la mascota del usuario.
