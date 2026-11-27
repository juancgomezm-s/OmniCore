# Editar el mensaje de sistema del agente

Abre **F4 → Cuentas y conexiones → Administrar cuentas Claude → Configurar cuenta…
→ Mostrar opciones de conexión**. Debajo de User-Agent está **Mensaje de sistema
del agente**, un editor de varias líneas.

Escribe instrucciones propias de rol, tono, dominio o estilo y elige **Guardar
configuración**. Enter avanza al campo siguiente; Shift+Enter inserta una línea;
Tab y Shift+Tab recorren los controles sin insertar tabulaciones. Esc vuelve sin
guardar. La edición admite hasta 8192 caracteres. Vaciar el campo y guardar
restaura el comportamiento predeterminado.

La preferencia se guarda sólo en configuración User, como
`providers.<conexión>.agentSystemPrompt` en `providers.yaml`, fuera del bloque
`oauth`. Las conexiones existentes sin ese campo conservan su comportamiento.
El texto se añade a las instrucciones del agente principal en turnos nativos
nuevos. Las continuaciones de un turno conservan su snapshot original; editar la
preferencia no reescribe el journal ni los turnos anteriores. Los perfiles e
instrucciones de agentes hijos y el chat independiente de la mascota conservan
su configuración propia.

El campo complementa las instrucciones del Host: mantiene primero la
identificación de OmniCore, el modo, sus reglas de ejecución y los permisos. No
cambia identidad OAuth, scopes, tokens ni facturación. Guardar la personalización
de una cuenta existente no inicia login ni realiza inferencia. La edición del
mensaje no acredita ni resuelve el rechazo HTTP 429 del proveedor.
