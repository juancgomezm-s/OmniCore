# Skill de escritorio automática — 2026-10-30

La versión 0.2.0 de `desktop-control` se admite automáticamente ante solicitudes
humanas como «prueba esta ventana», «captura la pantalla» o «haz clic en ese botón».
El Host utiliza señales locales de la solicitud actual, no el texto de la
aplicación ni WorkingState. Cubre expresiones habituales en español e inglés;
no pretende reconocer cualquier formulación o idioma. El usuario ya no necesita
cargar la skill mediante un comando para esos casos.

La selección automática mantiene los presupuestos y supresiones existentes. Al
asentarse el turno se libera, se retira `desktop.control` del catálogo y se cierra
su helper. Un turno pendiente conserva el paquete para reanudación; las cargas
humanas/modelo explícitas siguen siendo persistentes. No se cambia el modo, el
modelo ni los permisos del usuario. La guía ahora describe cómo elegir la ventana
solicitada, realizar una prueba reversible pertinente y verificar su resultado;
una petición de sólo captura no implica enviar entrada.

Validación: solución completa compilada sin advertencias; 55 pruebas de skills,
permisos y ciclo de vida, un caso de conversación real con proveedor HTTP local y
62 comprobaciones de arquitectura, todos correctos. Skill validada con
`quick_validate.py`.

La aceptación GUI utilizó el runtime real `TuiTurnHost → OmniCliRuntime`, los
ensamblados de la distribución y una aplicación WinForms propia. No registró ni
invocó un MCP y no utilizó comandos de carga/liberación. Dos tareas naturales
produjeron dos activaciones y dos liberaciones automáticas. Hubo nueve llamadas
de escritorio autorizadas y exitosas: captura, UIA/contraseñas redactadas, clic
con foco verificado y escritura Unicode con lectura de vuelta. La segunda tarea
pidió sólo una captura y no envió entrada. Una pregunta posterior ajena al
escritorio no recibió la herramienta. Cero llamadas a proveedores reales.

El escritorio compartido impidió obtener foco en algunos intentos: el componente
devolvió FOCUS_DENIED sin enviar entrada. Para preparar la aceptación se observó
la ventana propia con computer-use; su intento de clic devolvió `unknown
screenshotId`, sin reintento. Tras comprobar un estado nuevo, el recorrido Rust
completo pasó. La preparación del foco queda separada de las operaciones y
capturas que acreditó OmniCore. No se modificaron las restricciones de foco de
Windows ni se tocaron ventanas ajenas. El verificador cerró su aplicación.

La prueba usa respuestas de modelo deterministas por HTTP local: acredita la
selección por solicitud y el recorrido real, no la planificación autónoma de un
modelo comercial sobre cualquier aplicación. No se ejecutó la regresión global
completa. Se mantienen los límites de PrintWindow, UIA y SendInput de ADR-0064.
