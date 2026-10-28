# Panel compacto y muestra ClearType — 2026-10-28

Revisión explícita del usuario: rechaza el compacto del chat, acepta el panel
lateral y quiere ver por separado el efecto de ClearType. Sustituye el alcance
del primer mock rechazado.

## Estado vigente del prototipo

- `ConversationPresentation.cs` volvió byte por byte a su original conservado;
  no se cambia la separación entre mensajes, el borde del chat ni el código.
- El composer recuperó sus márgenes, altura, posición y anchura originales.
- Sólo se compacta el interior del panel lateral: una columna menos de margen
  izquierdo y dos filas menos de separación antes de su contenido desplazable.
  Su anchura exterior y el espacio de la mascota son los mismos.
- `/compacto` prueba ese panel, Esc vuelve, `/compacto guardar` lo guarda y
  `/compacto normal` restaura el panel normal. La preferencia usa un archivo
  `.sidebar-density`; no reutiliza el `.density` del mock rechazado.
- El override optativo es `OMNICORE_TUI_SIDEBAR_DENSITY=compact`. Se mantiene la
  entrada en F4 → General y en el autocompletado. No hay cambios de permisos,
  journal, proveedor o worktree de M9 ni instalación global.

## Muestra de ClearType

`artifacts/sidebar-cleartype-preview-20261028/cleartype-comparacion.png` compara
los mismos caracteres, Consolas a 18 píxeles, posiciones y fondo. Se genera en
Windows con `System.Drawing`/GDI+, `AntiAliasGridFit` frente a `ClearTypeGridFit`.
Es una muestra real de esos modos de renderizado; **no es una captura de Windows
Terminal ni una garantía de cómo se verá su renderizador DirectWrite**.

El detalle inferior es una ampliación 4× por vecino más cercano. La imagen puede
ser reescalada por el visor; los PNG `texto-*-100.png` conservan la muestra 1:1.
El control monocromo verificó cero píxeles cromáticos en grises y 489 con ClearType,
sin cambiar fuente ni geometría. Datos en `cleartype-rendering.json`.

Se reproduce sin abrir ni automatizar terminales interactivos:

```powershell
powershell.exe -NoProfile -File artifacts/sidebar-cleartype-preview-20261028/Render-OmniCoreClearTypeSample.ps1 -OutputDirectory artifacts/sidebar-cleartype-preview-20261028
```

El perfil opcional de Windows Terminal sigue siendo un fragmento propio que no
edita el perfil habitual. Ahora solicita sólo panel compacto y ClearType.
Sin argumentos, el script sólo muestra el plan; no se aplicó al directorio real.
La reversión acepta también el fragmento exacto del primer mock, conservando
cualquier archivo con modificaciones ajenas.

## Comprobaciones

Build Release aislado de tests y dependencias: cero advertencias y errores.
Tres recorridos TUI pasan: dos tamaños (80×25 y 120×40) verifican explícitamente
que la geometría y texto del chat, la geometría del composer, la mascota y el
journal permanecen iguales al probar/revertir el panel; otro comprueba guardar
y restaurar la preferencia. Cuatro casos del store pasan.

El script del perfil pasa en PowerShell 7 y Windows PowerShell 5.1 con fixtures
temporales: sólo actúa sobre su archivo exacto, preserva el ajeno y permite
retirar tanto el fragmento actual como el original. Las capturas nuevas del
driver están en `artifacts/sidebar-cleartype-preview-20261028/frames`.

## Ajuste posterior de fuente solicitado por el usuario

Se preparó Cascadia Mono SemiBold, ya instalada, para dar un trazo más firme que
Consolas Regular. El perfil optativo usa `font.face: Cascadia Mono` y
`font.weight: 600`; no fija otro tamaño ni cambia colores, chat o composer.
El script conserva la reversión exacta de los perfiles anteriores sin fuente.
No se aplicó a la configuración real de Windows Terminal.

La muestra nueva está en
`artifacts/sidebar-cleartype-font-preview-20261028/fuente-comparacion.png`;
ambas fuentes se renderizan con ClearType, a 18 píxeles y con el mismo texto.
`texto-cascadia-semibold-100.png` conserva la propuesta 1:1. El renderer GDI+
confirmó el nombre de familia `Cascadia Mono SemiBold`, sin fallback; no representa
una captura ni una aceptación física de Windows Terminal. La fuente no cambia
el contraste matemático de los colores: aumenta el grosor visual de los trazos.

La aceptación visual en el monitor y renderizador de Windows Terminal del usuario
sigue pendiente. No se alteró su configuración global ni se publicó el prototipo.
