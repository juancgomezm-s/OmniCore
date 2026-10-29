# Mascota: conversación única y mantenimiento — 2026-10-29

## Comportamiento

La conversación de mascota recibe los avisos y permite contestar por proyecto o por un único aviso contextual. Se eliminan las pestañas de sesiones, avisos y memoria del flujo habitual. Todas las rutas de configuración abren la misma ventana **Mascota**: modelo, exclusividad local opcional, contexto efectivo, avisos, atención, visibilidad, restricción, movimiento, presentación, recuerdos por categoría y consumo informativo. La ventana se ajusta a 120×40 y 80×25. Abrirla conserva los borradores del principal y de la mascota.

Los avisos se agrupan por instancia, sesión y clase. La burbuja cuenta todos los grupos pendientes, aunque haya más de cien filas recientes. Se presentan en lotes de ocho y se reconocen después de mostrarlos. La atención es breve, respeta silencio y movimiento reducido, no consulta un modelo y no toma el foco. La burbuja deja espacio al título del retrato.

SQLite mantiene el historial completo y permite escrituras simultáneas de varias instancias sin reemplazar el diálogo entero. Cada chat heredado se importa una vez. Las rutas de avisos conservan instancia, sesión y run: un cambio de run invalida la respuesta contextual. Un proyecto explícito distinto nunca se sustituye por el aviso enfocado. Las instrucciones y sus seguimientos se guardan en la misma transacción; los textos completos de las instrucciones se conservan aunque el extracto del seguimiento sea más corto. Las desconexiones actualizan el estado sin declarar finalización.

El contexto local usa hasta 32768 tokens; el remoto usa la capacidad declarada del modelo, con respaldo de 32768 si no está declarada. Hay reserva de respuesta y compactación de intercambios antiguos completos. Los extractos no reemplazan el archivo original. El contador puede ser una estimación si el proveedor no expone tokenizador. No se agregan cuotas monetarias, consultas periódicas al modelo, servidor vectorial ni herramientas del agente principal.

## Verificación

Windows, .NET 10.0.12 y Terminal.Gui 2.6.0-develop.61. Los proveedores de conversación de las pruebas son controlados; no requieren credenciales ni consumen servicios remotos.

- `TestResults/companion-natural-second.trx`: 26 pruebas focales aprobadas.
- `TestResults/companion-natural-regression.trx`: 269 aprobadas, cero fallos; dos benchmarks explícitos omitidos.
- `TestResults/companion-natural-main-regression.trx`: revisión con main, **279 aprobadas**, cero fallos y dos benchmarks explícitos omitidos. Compilación Release sin advertencias ni errores en `TestResults/companion-natural-main-build.log`.
- `TestResults/companion-natural-final-host.trx`: **46 aprobadas**, cero fallos, tras el último ajuste de transacción y estados de seguimientos. Compilación final Release sin advertencias ni errores en `TestResults/companion-natural-final-build.log`.

La regresión cubre mascotas, gráficos y respaldo, animaciones, foco y borradores, redimensionado, herramientas exclusivas, buzón lateral y ciclo de conversaciones. Los casos nuevos cubren nombres estables de proyecto, más de cien avisos pendientes, reconocimiento selectivo por lote, rutas ambiguas o caducadas, memoria FTS con búsqueda sin acentos, edición concurrente, olvido y resolución de seguimientos, migración de chats y compactación sin perder el archivo.

## Frames y límites

`TestResults/companion-natural-final-frames/settings-companion-{bubble,conversational-notice,one-preferences}-{120x40,80x25}.svg` son exportaciones del driver real de Terminal.Gui con datos sintéticos. El frame de burbuja a 120×40 incluye los píxeles reales producidos por ImageView con soporte Sixel emulado en el driver. No es un mock HTML ni una captura de Windows Terminal en producción. Los PNG se obtienen rasterizando esos SVG.

Se conservan dos capturas con la implementación: [ventana única a 80×25](companion-natural-frames/preferences-80x25.png) y [burbuja junto al retrato detallado](companion-natural-frames/notice-bubble-120x40.png).

Esta revisión no vuelve a certificar un proveedor remoto en vivo, terminales físicos adicionales ni tiempos de entrada en producción. Conserva los backends y la cadencia de animación anteriores. La recuperación de memoria es textual con FTS5; no se presenta como búsqueda semántica vectorial. El mantenimiento conserva las cuatro categorías en una misma lista seleccionable, sin ventanas adicionales.
