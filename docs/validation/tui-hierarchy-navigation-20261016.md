# Validación de jerarquía y navegación de preferencias

Fecha de validación: 16 de octubre de 2026.

La aceptación manual detectó que las ayudas compartían columna y fila contigua
con acciones, las flechas podían transferir el foco del contenido a categorías,
el idioma se aplicaba al recorrer una lista y el panel lateral presentaba un
selector de categoría en lugar de resúmenes desplegables.

## Corrección

- Se introduce un rol común de texto informativo: columna 8 frente a títulos en
  2 y acciones en 4, tono secundario, sin foco. Se separan ayudas y acciones en las
  ocho categorías, con desplazamiento para conservar espacio en 80×25. Los datos
  de idioma dejan de comprimirse en dos columnas. La presentación común reconoce
  además ayudas, estados, avisos y descripciones de los formularios de preferencias.
- Una navegación común mantiene arriba/abajo dentro del formulario. En listas
  recorre filas y al llegar a un extremo continúa por los controles del mismo
  contenido; el último vuelve al primero. Los editores conservan su movimiento
  de texto y Tab sigue disponible. Categorías y Buscar quedan fuera del grupo.
  Izquierda y los controles explícitos conservan el retorno al menú.
- Recorrer idiomas no modifica el borrador ni el idioma efectivo. Clic sencillo
  o Enter confirma el borrador; Guardar persiste con revisión y aplica el idioma
  sólo después de la aceptación del Host. Cancelar descarta sin cambiar el idioma.
  Los overrides de proceso conservan su prioridad. Recargar sigue siendo una
  operación explícita y puede adoptar una preferencia externa.
- El panel lateral muestra las categorías juntas con un resumen. Cada sección
  tiene su enlace Más/Menos con expansión independiente y persistencia Workspace.
  Se retiran el carrusel y el botón de expansión global. Se conservan la sesión
  fijada, alertas, datos reales, inspección de diff, selección y desplazamiento.
  Los enlaces se recorren con Tab y se activan con clic o Enter. El salto a
  categorías contempla filas envueltas: se solicita el refresco de posición
  antes de consultar el evento de coordenadas originales de Terminal.Gui.
- Las acciones de habilidades, extensiones y memoria conservan la fila elegida
  por su identidad después de actualizar los datos. Antes la pantalla volvía a
  la primera fila y la siguiente acción podía dirigirse a otro elemento.
- Los textos propios nuevos mantienen recursos es/en/fr/it. No cambian permisos,
  autorización, almacenamiento de secretos ni ejecución de modelos.

## Verificación

Las pruebas reproducen navegación por extremos en las ocho categorías, elección
de idioma por clic y Enter, guardado, descarte, revisión y redimensionado. El panel
se verifica con clic/Enter sobre enlaces, expansión individual, rutas largas,
diffs conservados y datos medidos de uso. Los tests anteriores que esperaban
preview al pasar por una fila se adaptan al nuevo contrato de aceptación y
guardado explícito, conservando sus validaciones de persistencia y aislamiento.

La verificación se realiza con el driver real de Terminal.Gui y fixtures de
Host aisladas. No acredita autenticación real, un CI remoto ni pruebas Linux.
WSL no se inició. Registros y capturas externos:
`C:/Users/juanc/.codex/omnicore-linux-20261014`, prefijo `tui-hierarchy`.

Resultados de la batería principal en Windows:

| Comprobación | Resultado | Registro |
| --- | --- | --- |
| Compilación Debug de las pruebas | 0 errores y 0 advertencias | `tui-hierarchy-build.log` |
| TUI, presentación, preferencias, panel lateral y localización | 395 aprobadas, 0 fallidas, 0 omitidas; 6 min 17 s | `tui-hierarchy-verified.log` |
| Arquitectura | 62 aprobadas, 0 fallidas, 0 omitidas | `tui-hierarchy-architecture-verified.log` |
| Publicación Release de la CLI | Código de salida 0 | `tui-hierarchy-publish.log` |

Las capturas de las ocho categorías a 80×40 y 120×40 están en
`tui-hierarchy-verified-captures/settings-hierarchy-*`. La revisión visual de
Contexto y sesiones y Herramientas y seguridad confirma la separación entre
acciones y ayudas. Las escenas compactas también comprueban desplazamiento en
80×25 y prioridad de idioma cuando existe una opción de proceso.

La última revisión visual detectó una ayuda del panel lateral que conservaba
las flechas del carrusel retirado. El texto se sustituyó en los cuatro idiomas
por la indicación del enlace Más. La comprobación posterior de localización,
enlaces, diffs y fotogramas de la TUI terminó con 62 pruebas aprobadas, 0 fallidas
y 0 omitidas en `tui-hierarchy-copy-verified.log` (1 min 28 s). La compilación de
ese ajuste también terminó con 0 errores y 0 advertencias, y se volvió a publicar
la CLI Release.

La TUI completa publicada se abrió en la ventana
«OmniCore - Jerarquía y navegación». Se verificó el proceso correspondiente al
runtime `tui-hierarchy-runtime-20261016/omni.dll`.
