# Herramientas alternativas: configuración guiada — 2026-10-16

La pantalla anterior presentaba un mapa vacío y pedía escribir «Herramienta
original» y «Destino». No explicaba para qué servía, exigía conocer identificadores
internos y aceptaba preferencias que podían quedar sin efecto por incompatibilidad.

## Comportamiento

- El menú se llama **Herramientas alternativas…**. Explica que preferir otra
  herramienta es opcional y que dejar la configuración vacía conserva el
  comportamiento habitual.
- Dos listas muestran el origen y las alternativas con metadatos compatibles.
  Al recorrer cada lista aparece la descripción de la herramienta enfocada. Los
  identificadores se seleccionan del catálogo, sin introducirlos a mano.
- Enter pasa del origen a las alternativas; otro Enter o Añadir preferencia
  prepara la fila. Guardar escribe sólo los cambios preparados explícitamente.
  Seleccionar una fila no crea una preferencia por sí solo.
- Quitar y Guardar permiten retirar reglas, incluso si la extensión ya no está
  en el catálogo. Volver descarta el borrador. Los conflictos de revisión se
  muestran en el formulario y conservan los cambios externos.
- Sin alternativas se ocultan las listas vacías y las acciones de creación.
  La pantalla explica que no hace falta configurar nada. Un fallo de lectura o
  metadatos antiguos incompletos tienen mensajes distintos; las reglas existentes
  se conservan y siguen pudiendo eliminarse explícitamente.
- Todos los textos nuevos tienen recursos es/en/fr/it.

## Compatibilidad y autoridad

La consulta existente `tools` agrega únicamente `risk`, `tags` y `protection` de
los descriptores que ya inspecciona. El decodificador Client los considera
opcionales; los Hosts anteriores siguen siendo legibles. Sin tags o riesgo
reconocido no se inventa compatibilidad.

La selección guiada compara identidades distintas, mismos tags y clase de
efecto, y riesgo igual o menor, siguiendo las condiciones de metadatos existentes
del planner. No afirma equivalencia semántica completa de esquemas. No cambia
ExplorerTurn, permisos, límites ni la ruta de ejecución. La visibilidad de un
turno se vuelve a evaluar en ejecución; la preferencia puede prepararse antes
de ACT. Ambas herramientas deben estar disponibles para aplicarla. Una
sustitución protegida conserva el Ask y el fingerprint de ADR-0027.

La UI sólo propone destinos aportados, excluyendo los built-in: sus tags amplios
no acreditan intercambiabilidad (listar y leer archivos comparten tags, pero
cumplen funciones distintas). Esta restricción de presentación para nuevas
preferencias no altera el planner ni borra reglas existentes. Se conserva la
descripción del destino para que el usuario evalúe la elección explícita.

Los alias de comandos conservan su editor. Se retira la rama antigua de entrada
manual de herramientas y su parser sin consumidores.

## Evidencia

Se comprueban selección por teclado, clics reales del driver, guardado explícito,
descarte de borradores, eliminación de reglas ausentes, conflictos, catálogo
ausente/inválido/antiguo, traducciones y geometría en 80×25 y 120×40. La prueba de
la consulta real compara los tres metadatos con el descriptor del Host; los
catálogos con alternativas usadas para probar la UI son fixtures aisladas.

Los registros y capturas se guardan fuera del repositorio en
`C:/Users/juanc/.codex/omnicore-linux-20261014`, con prefijo `tui-guided-tools`.
La validación es local en Windows; no acredita un CI remoto ni una nueva prueba
Linux. WSL no se inició.

Resultados finales:

- Compilación: cero errores y advertencias (`tui-guided-tools-build.log`).
- Comportamiento, contrato de inspección, localización, mapa User, permisos/
  fingerprint y recorridos de menú: **68/68**, cero omisiones, 54,435 s
  (`tui-guided-tools-final.log`).
- Arquitectura: **62/62**, cero omisiones (`tui-guided-tools-architecture.log`).
- Publicación Release del código final: salida **0**
  (`tui-guided-tools-publish.log`).
- Capturas revisadas: configuración guiada con alternativas de fixture y estado
  sin alternativas del catálogo integrado, conservando el fondo azul. Las ocho
  combinaciones de idioma/tamaño del recorrido de menús pasan con el nuevo editor.
