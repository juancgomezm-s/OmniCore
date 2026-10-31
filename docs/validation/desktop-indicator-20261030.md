# Indicador y cancelación de escritorio — 30 de octubre de 2026

Componente Rust independiente integrado como herramienta de Core detrás de la
skill desktop-control. Cortina morada al 6,3 %, bordes graduados y aviso superior
con ventana objetivo, botón Cancelar y Escape. No toma foco ni intercepta el ratón
fuera del aviso. Se mantiene durante la espera del modelo y desaparece al cerrar
el helper o cancelar. No modifica la interfaz ni los bundles de OmniCoder.

Captura limitada a una ventana, sin cortina ni aviso en la imagen. Se rechazan
escritorio, shell e indicador propio. Cancelar interrumpe el turno completo, libera
las entradas sintéticas propias antes de avisar al Host y descarga la skill
automática. No altera selecciones manuales, permisos, modo o modelo.

## Verificación

- Rust: seis tests unitarios, build release y clippy con warnings denegados.
- Integración nativa existente: 15/15 comprobaciones con indicador activo,
  incluyendo captura, UIA con contraseñas redactadas, Unicode, clic, arrastre,
  rueda, foco, geometría, observaciones caducadas y timeout aislado.
- Indicador: dos ventanas propias, alfa 16/255, exclusión de captura, sin robo de
  foco, objetivos prohibidos rechazados y limpieza al cerrar stdin. Botón de
  cancelación: 15 ms; manejador de Escape: 31 ms; captura bloqueada: 16 ms.
  Se ejercitó WM_HOTKEY sin enviar Escape físico global a aplicaciones ajenas.
- Host: 60 pruebas focalizadas, una conversación automática y 62 pruebas de
  arquitectura: 123/123 pasaron. Compilación completa sin advertencias ni errores.
- Runtime real de OmniCore con proveedor HTTP local programado: solicitud humana
  activa la skill, dos llamadas nativas capturan sólo la ventana propia de 875×575
  y leen UIA. Cancelar durante la siguiente espera HTTP interrumpe en 141 ms,
  sin reintentos, retira el indicador y deja cero skills automáticas activas.
  No se consumieron llamadas a un proveedor remoto.

La cortina es tinte, sin desenfoque real del contenido. PrintWindow puede devolver
negro en aplicaciones que no lo admiten; no se garantiza captura/UIA atómica ni
control de ventanas elevadas o protegidas. No se descompiló ni ejecutó el original.

Referencias de implementación: [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey),
[ventanas por capas y transparencia al ratón](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/winmsg/window-features.md),
[exclusión de captura de Windows](https://blogs.windows.com/windowsdeveloper/2019/09/16/new-ways-to-do-screen-capture/).
