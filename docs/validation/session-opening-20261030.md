# Selección explícita de conversación en TUI

Al abrir la TUI o cambiar de directorio mediante su navegador se solicita **Nueva conversación / Retomar existente**. La selección durable del Host sigue disponible para recuperación, pero no se presenta ni recibe un prompt hasta que el usuario elige una conversación. **Continuar la última** es una acción explícita. La lista permite buscar por título o identificador y muestra título y fecha local de actualización.

La misma carpeta admite varias conversaciones independientes. **Nueva conversación** crea otra Session; **Retomar existente**, F6 o `/retomar` seleccionan una existente. Cancelar la elección no crea una Session ni modifica la selección durable: se mantiene la vista vacía y el siguiente envío vuelve a solicitar una elección. El borrador no se envía mientras falte esa elección.

Si la recuperación conserva un Turn abierto, continuar la Session ya seleccionada únicamente habilita su presentación; no vuelve a despachar el Turn ni lo cancela. Crear otra conversación o cambiar de Session conserva el bloqueo de actividad del Host. Los clientes Host/stdio mantienen su contrato de recuperación y las Sessions conservan su aislamiento.

## Verificación local

`TuiWiringTests.SessionOpening.cs` cubre la elección inicial en 120×40 y 80×25, títulos/fechas, cancelación por Escape/botón, borrador no enviado, dos Sessions en una carpeta, búsqueda y reapertura, y Turn durable pendiente. `SessionOpeningWorkspaceRuntimeTests` abre dos Hosts con directorios aislados, recupera dos Sessions al volver a la primera carpeta y comprueba una segunda reapertura tras retomar la otra Session. Las pruebas de ciclo durable (`ConversationLifecycleTests`) y navegación (`WorkspaceNavigationRuntimeTests`, `TuiWiringTests.WorkspaceNavigation`) complementan la regresión. La ejecución final, sus resultados y límites están registrados en [el acta del incremento](sessions-initialize-skills-20261030.md).
