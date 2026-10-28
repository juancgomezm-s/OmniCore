# Animación del panel — 27 de octubre de 2026

Los nueve atlas mantienen los diseños detallados y añaden respiración, acomodo,
miradas laterales y respiración durante el sueño. Hay 18 cuadros de 128×128 por
atlas; los IDs 0–12 anteriores permanecen estables. Los PNG añaden 64 673 bytes
en total. La preparación SVG/Sharp sólo se usa durante desarrollo.

El panel conserva su reloj de 600 ms. Reposo usa seis cuadros distintos en un
ciclo de 19,2 s; 25 de sus 32 pasos mantienen la pose base. Cada renderizador
retiene como máximo seis ImageView en una LRU. Al volver a un cuadro conserva
el mismo objeto y la misma cadena Sixel codificada: no repite el encoder.
Escalado/codificación iniciales siguen en el backend asíncrono de Terminal.Gui.
Un gesto nuevo puede requerir preparación en su primera aparición.

Ocultar, cerrar panel, cambiar a texto o fallar gráficos libera las vistas y
presentaciones. Menús suspenden el reloj del panel; la ficha de la mascota sí
puede animarse. Se respetan movimiento reducido y desconexión. No cambia Core,
el sorteo de invitados, XP, evolución, borradores ni comandos.

## Validación

Compilación Release sin errores ni advertencias desde una copia aislada de main
local, con sólo estos cambios. **26 pruebas focales correctas**, más un benchmark
explícito correcto. Incluyen todos los atlas, seis estados del buey y de ambos
invitados, las otras cinco especies, respaldo, fallo y redetección, resize/fuente,
menús, foco/borrador/scroll, movimiento reducido y persecución al escribir.

La prueba de caché alterna 14 cuadros reales, exige una sola presentación y
comprueba identidad de las vistas y de las cadenas codificadas. Verifica expulsión
del séptimo cuadro, invalidación de escala y limpieza de todas las presentaciones.
Otra prueba pausa el reloj detrás de un menú y conserva el borrador al retomarlo.

Instalación global actualizada y hashes de CLI/Client comparados con el publish
aislado. Se abrió el ejecutable instalado en una sesión real del repositorio:
ConPTY, driver WINDOWS, xterm.js 5.5.0 y addon-image 0.8.0. El detector recibió
DA `?62;4;9;22c` y tamaño de celda `6;14;7t`; no se forzó la capacidad Sixel.
Se usó `/pet home`, sin enviar un mensaje al proveedor ni modificar XP/especie.
El zorro conservó L1 y 30/100 XP. No se volvió a validar Windows Terminal físico
en este recorrido; no se confunde el emulador del visor con ese terminal.

[Grabación real del panel](panel-animation-live-20261027.gif): 68 capturas,
aproximadamente 35 s, seis poses distintas; conserva las pausas observadas, sin
interpolar ni inventar cuadros. [Ventana completa](panel-animation-session-20261027.png).
El visor muestra el proceso interactivo instalado; la grabación es un recorte de
esa ventana, ampliado 1,5× para lectura. No es una reproducción del atlas.

Evidencias locales: `TestResults/panel-animation-tests-2.trx` y
`TestResults/panel-animation-benchmark.trx`.

## Medición del driver real

[Datos originales](panel-animation-benchmark-20261027.json). Windows, .NET 10.0.12,
Terminal.Gui 2.6.0-develop.61, driver DOTNET, 120×40, Host/journal temporales,
historial de 160 líneas y zorro. Soporte Sixel controlado por la prueba; destino
de salida que cuenta UTF-8, sin coste de pintar un emulador o transmitir el visor.
Preparación de 1,5 s para oculto/estático y 22 s para calentar el ciclo animado.
Una muestra por caso, proceso completo; CPU expresada respecto de un núcleo.

| Caso | Muestra | CPU % | Heap MiB | Privados MiB | Salida B/s | Asignaciones MiB/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Oculta | 12 s | 11,72 | 21,50 | 59,89 | 128 | 0,37 |
| Estática / movimiento reducido | 12 s | 2,73 | 22,73 | 65,38 | 128 | 0,39 |
| Animada / caché caliente | 24 s | 3,12 | 23,21 | 61,67 | 5 222 | 11,52 |

Heap se mide tras GC antes de cada muestra. Las asignaciones son acumulativas,
no memoria retenida: el ciclo de dibujo del framework todavía genera basura al
cambiar cuadros. El primer caso incluye estabilización/JIT; su CPU superior no
indica que ocultar sea más caro. Una muestra no permite atribuir diferencias de
CPU pequeñas ni prometer las mismas cifras en Windows Terminal. No se comparó
Kitty ni se afirma que un protocolo siempre consuma menos. La animación añade
salida frente al modo estático, y movimiento reducido sigue siendo la opción
más económica cuando se desea una imagen fija.

Reproducción (ejecutable de pruebas compilado en Release):

```powershell
$env:OMNICORE_PANEL_BENCHMARK_FILE = "$PWD/TestResults/panel-animation-benchmark.json"
dotnet tests/OmniCore.Tests/bin/Release/net10.0/OmniCore.Tests.dll -method '*Companion_panel_measures_static_and_cached_animated_idle' -explicit on
```
