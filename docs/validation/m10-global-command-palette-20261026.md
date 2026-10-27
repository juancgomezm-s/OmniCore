# M10-C — paleta global conectada al motor de teclado

## Alcance construido

OmniCore TUI, sobre `9e2ca9d` en `codex/m10-keybindings`.

- Ctrl+Shift+P desde el chat abre `palette.open`. Se admite reasignación User y
  apertura por `/palette`, `/paleta` o `/tavolozza`, según aliases existentes.
  El ejemplo YAML documenta una reasignación F9; no se escribe configuración User.
- Proyección portable en Client, sin Terminal.Gui: acciones/comandos cliente,
  navegación local y comandos calificados publicados por el Host.
- Búsqueda fuzzy sin distinguir mayúsculas/diacríticos, en título, descripción,
  identidad y fuente. Orden determinista; filtrar/abrir/navegar no ejecuta nada.
- Lista con columnas Acción/comando, Atajo y Fuente. El detalle conserva identidad
  exacta, fuente completa, descripción, disponibilidad y procedencia del binding.
  Sólo se anuncia el atajo que resuelve realmente en el contexto de origen.
- Es/en/fr/it para recursos propios; las descripciones externas conservan su
  traducción declarada o la marca de texto original del paquete.
- Enter ejecuta una acción mediante el handler TUI ya usado por keybindings, o
  un CommandInvocation Host con ID calificado y origen Palette. El consumidor de
  outcomes Prompt/Workflow/Extension se comparte con el slash escrito.
- Campo de argumentos únicamente para comandos Host, analizado como argv por el
  parser existente. Comillas incompletas se diagnostican sin enviar el comando.
- Revalidación al aceptar: misma Session/Run, entrada disponible, misma fuente,
  fingerprint, descripción y contrato de argumentos. Un cambio exige reabrir.
  La comprobación cliente no sustituye la autorización ni los gates del Host.
- Esc vuelve al borrador; la paleta y sus acciones conservan texto/adjuntos. No se
  abre encima de formularios, overlays o aprobaciones; interrupt/cancel y default
  deny continúan por sus rutas existentes. Los comandos Host se bloquean mientras
  exista trabajo de modelo activo, con razón visible.

## Disponibilidad e IDs ambiguos

`CommandCatalogEntry.CanonicalEnabled` es una propiedad nullable/aditiva del
catálogo. El Host la obtiene del mismo resultado de `handler.IsEnabled` que ya
consultaba; luego sigue aplicando al campo Enabled la precedencia del nombre corto.
Una opción ambigua/shadowed puede elegirse por ID calificado sólo si su handler
estaba disponible. No se infiere disponibilidad por texto ni por nombre de fuente.
Los DTOs antiguos sin esta propiedad conservan fallback conservador a Enabled.
Un ID calificado duplicado en un catálogo malformado se excluye, sin elegir uno.

No se cambian CommandContext, fingerprints, cálculo de revisiones, native command
admission ni workflow continuation. No se introduce una segunda ruta de ejecución
ni se convierte búsqueda/selección de fila en input del modelo.

## Coordinación con M9

Se inspeccionó el chat «Analiza y reconoce el avance del plan hasta el momento»:
continúa su validación de `f21ad93`. Su rama, worktree, SDK, scripts y publicación
no reciben cambios desde este corte. Se conserva su archivo no seguido `x`.
Los documentos previamente sucios de main tampoco se modifican.

Los renderers nuevos viven en TuiApp.CommandPalette.cs y TuiApp.HostCommands.cs.
El segundo mueve el consumidor existente de outcomes Host para compartirlo con
la paleta; no modifica StartHumanShellAction ni la cancelación de procesos M9.
Se comprueba con `git apply --check` el parche compartido acumulado desde ffa96af
(M10-B, selector y paleta) sobre M9, sin aplicarlo. Esto acredita compatibilidad de
los hunks; no se atribuye a M9 una integración ni aceptación de estos cambios.

## Límites

No se añade una paleta a OmniCoder ni al CLI plain, ni un stack nuevo de overlays.
La paleta se usa desde el chat; un formulario o interacción activa conserva foco
y estado. No hay watcher del catálogo mientras se filtra: Enter lo revalida y se
puede cerrar/reabrir para actualizar. Los atajos directos a comandos Host y el
descubrimiento de contribuciones de extensión siguen pendientes de su consumidor;
palette.open y las acciones/comandos cliente existentes sí están conectados.
El resto de M10 y la aceptación de M9/OR5 permanecen en sus propios planes.

La validación usa driver Terminal.Gui real y fixtures locales. No acredita una
aceptación humana de todos los terminales/OS ni una suite global de producto.
No se emplean credenciales ni conversaciones reales o proveedores de pago.

## Validación final

- Solución Release sin errores ni advertencias, incluidos Protocol/Client net8 y
  net10. No se modifica el grafo permitido.
- **106 PASS, 0 FAIL, 0 SKIP**: proyección/fuzzy, precedencia y hints efectivos,
  catálogo y disponibilidad calificada, registro, aliases humanos, recursos de
  cuatro idiomas, acciones cliente, configuración/teclado y comandos Explorer.
- **24 PASS, 0 FAIL, 0 SKIP** de TUI: diez recorridos de paleta (cuatro idiomas,
  dos tamaños, sólo lectura al buscar, Escape/borrador, Enter hacia F3, remap User,
  argumentos e ID Host reales, catálogo modificado, aprobación pendiente, cancel,
  cambio de conversación, vacío/deshabilitado y comillas incompletas), más los
  catorce recorridos de regresión del motor de teclado M10-B.
- **62 PASS, 0 FAIL, 0 SKIP** de arquitectura.
- Capturas RGB de driver 80×25 y 130×38 en
  `%TEMP%/omni-global-palette-frames-final/`, rasterizadas con Sharp e inspeccionadas.
  El campo de argumentos se oculta para acciones locales y aparece para comandos.
- Logs: `%TEMP%/omni-global-palette-{solution,final-client,final-tui,final-architecture}.log`.

El primer grupo unitario encontró una aserción que esperaba sólo dos resultados
de una búsqueda fuzzy: se corrigió para comprobar que las dos identidades exactas
ocupan las primeras posiciones sin excluir coincidencias parciales posteriores.
No se presenta esa pasada fallida como aceptación del árbol final.

Actualización local mediante `npm run install:cli`, con smoke `--help` y comparación
SHA-256 de publicación frente a instalación. Una TUI ya abierta conserva su binario
hasta que el usuario vuelva a abrirla; no se termina su proceso desde esta tarea.
