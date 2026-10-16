# Navegación, selección de modelo y superficie azul — 2026-10-16

La aceptación manual posterior a `e2d4a30` detectó tres defectos adicionales. Esta
revisión conserva el menú completo y atiende las capturas del usuario.

## Causas y correcciones

1. El selector descartaba Enter si `_catalogFresh` era falso para un modelo de
   suscripción. Una consulta fallida dejaba todos esos modelos registrados sin
   poder seleccionarse. El menú de Preferencias abría además el selector sin
   refrescar, de modo que tampoco satisfacía esa condición. Se retira ese bloqueo
   de presentación: seleccionar conserva la ruta Host y sus políticas, sin hacer
   inferencia ni conceder permisos. Un catálogo recibido correctamente sigue
   retirando modelos no disponibles y el reemplazo continúa en ObserveOnly.
2. El catálogo no ofrecía reintento en el selector. Ahora tiene Actualizar modelos,
   evita consultas solapadas, recupera el botón tras un error y descarta respuestas
   pertenecientes a un panel cerrado. Conserva búsqueda, fila señalada y procedencia
   de Preferencias al reconstruir la vista; una actualización no equivale a aceptar
   la fila que el usuario está señalando.
3. Las flechas horizontales caían en la navegación predeterminada del framework.
   La reconstrucción del contenido podía transferir el foco al primer control o a
   Buscar. Se conserva el foco en categorías al explorar; Enter, derecha o Tab
   entran al contenido, e izquierda vuelve desde listas/botones. Buscar queda fuera
   del recorrido automático de Tab y se entra con Ctrl+F o clic. La lista de idiomas
   mantiene su valor, pero sólo muestra resaltado de edición cuando tiene foco.
4. El violeta anterior se sustituye por azul `#13222E`, entre conversación `#061822`
   y panel lateral `#202C3B`; encabezados y categorías usan `#182A38`. Se conservan
   contraste, indicadores de foco y soporte sin color.
5. La ventana de revisión deja de forzar `--locale es` y limpia OMNI_LOCALE sólo en
   su proceso hijo. Así la preferencia guardada y el cambio interactivo de idioma
   funcionan normalmente, sin alterar el entorno del usuario ni desobedecer los
   overrides que un usuario configure expresamente al arrancar la aplicación.

## Validación y límites

Pruebas de reproducción iniciales: 11/11 aprobadas, incluidos Enter tras error,
selección desde Preferencias sin discovery, reintento, tres formas de entrar al
contenido, cambio de idioma con teclado y contraste de la superficie. Se amplían
las comprobaciones a celdas dibujadas para distinguir valor guardado y foco real,
conservación de la fila al actualizar y retiro autorizado de modelos.

Resultados:

- Regresión amplia: 395/395 aprobadas, sin omisiones, en
  `tui-navigation-model-regression.log`. Incluye TUI, comandos, preferencias,
  localización, catálogos y OpenRouter. Esta ejecución precede al ajuste final del
  resaltado de idioma y la conservación exacta de la fila del selector.
- Sobre el código final: 30/30 pruebas focales aprobadas, sin omisiones, en
  `tui-navigation-model-final-focal.log`. Incluyen teclas reales inyectadas al
  driver, celdas dibujadas de foco/contraste, las ocho categorías en cuatro
  idiomas y dos tamaños, Enter después de error, reintento, conservación de
  búsqueda/fila y reemplazo seguro de modelos retirados.
- Arquitectura final: 62/62 aprobadas, sin omisiones, en
  `tui-navigation-model-final-architecture.log`.
- Compilación final: cero errores y advertencias en
  `tui-navigation-model-final-build.log`. Publicación Release completada con
  código de salida 0; registro `tui-navigation-model-publish.log`.
- Revisión de la captura del driver `settings-surface-distinct-120x35`: fondo
  azul diferenciado y lista de idiomas sin resaltado de edición al explorar el
  menú. La captura usa datos de prueba, no una cuenta del usuario.

Los logs y capturas se conservan fuera del repositorio en
`C:/Users/juanc/.codex/omnicore-linux-20261014`, con prefijo
`tui-navigation-model`. Las cuentas y HTTP de las pruebas son fixtures:
no acreditan autenticación real de ChatGPT/OpenRouter ni una ejecución remota de CI.
WSL no se inició. La aplicación se publica por separado de la instalación global.

## Corrección posterior: extremos de categorías y acciones del submenú

La revisión manual detectó que en el primer/último elemento Terminal.Gui dejaba
propagar arriba/abajo hacia la navegación de foco entre controles. Se consumen
ambas teclas en la lista de categorías y se calcula el siguiente índice en ciclo,
tanto en la vista integrada como en la compacta. Entrar al contenido continúa
siendo una acción explícita con Enter/derecha/Tab o clic.

Recargar, Cancelar y Guardar se trasladan del pie global al contenido de General
y apariencia. En la vista integrada el grupo sólo aparece mientras se edita esa
categoría y queda fuera del recorrido de foco al volver al menú. En la compacta
pertenece exclusivamente a la página General. Cancelar revierte el borrador de
idioma y vuelve a categorías, conservando Preferencias abierta. Se comprueba la
ausencia de solapamientos y, en 80×25, que el foco desplaza el contenido para
mostrar las acciones. Guardar y Cancelar se prueban con clic por coordenadas.

Las nuevas pruebas inyectan flechas para recorrer dos vueltas completas por
sentido, verifican la permanencia del foco en cada paso y comprueban entrada
explícita, visibilidad del grupo y cancelación sin escritura de la preferencia.

Validación de esta corrección: 29/29 pruebas aprobadas, cero fallos y omisiones
(`tui-category-cycle-final.log`, 57,487 s). Incluye navegación, clics de Guardar y
Cancelar, conflicto de revisión y recarga, redimensionado, contraste/sin color y
recorrido de las ocho categorías en los cuatro idiomas y dos tamaños. La
comprobación vertical de General usa su extensión desplazable, mantiene la
ausencia de solapamientos y se complementa con la prueba de que las acciones
quedan visibles al recibir foco en 80×25. La compilación terminó sin errores ni
advertencias (`tui-category-cycle-build.log`) y la publicación Release con código
0 (`tui-category-cycle-publish.log`). Las capturas del driver muestran el grupo
ausente al explorar categorías y dentro del contenido al editar. Los registros
y capturas usan el mismo directorio externo indicado arriba. Esta validación es
local en Windows; no acredita un nuevo resultado remoto de CI ni una prueba Linux.

## Subtítulos sin indicador de selección

Se sustituye `▸` por `│` en el encabezado compartido de secciones y en los dos
encabezados específicos de Interfaz y Cuentas. Se conserva la sangría, el color
y el carácter informativo de las etiquetas; el marcador deja de parecer una
opción enfocada. No se modifica el comportamiento de navegación ni selección.

Compilación sin errores ni advertencias y publicación Release con salida 0.
El recorrido existente de todas las categorías pasó en sus 8/8 casos, sin
omisiones, para cuatro idiomas y dos tamaños. Se revisó además una captura del
driver del submenú Modelos. Evidencia externa: `tui-heading-bar-build.log`,
`tui-heading-bar-test.log`, `tui-heading-bar-publish.log` y
`tui-heading-bar-captures`, en el directorio de validación indicado arriba.
