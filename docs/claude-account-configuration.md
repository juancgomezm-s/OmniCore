# Configurar la cuenta Claude en la TUI

Abre **F4 → Cuentas y conexiones → Cuenta Claude**. Si aún no hay una conexión
configurada, el foco comienza en **Configurar e iniciar sesión**.

1. Pulsa Enter para abrir el alta. Revisa el identificador de la conexión.
2. **Mostrar opciones de conexión** despliega la URL de inferencia, ID público
   del cliente OAuth, User-Agent, scopes y URLs de autenticación. Se rellenan con
   la plantilla que incluye OmniCore; puedes modificarlas antes de guardar.
3. **Guardar e iniciar sesión** valida y guarda la conexión en la configuración
   del usuario y comienza el login OAuth existente. Completa la autenticación en
   el navegador; la pantalla muestra el progreso y después el resultado.

Para preparar o editar la conexión sin autenticarte, usa **Configurar cuenta** y
**Guardar configuración**. Si existen conexiones OAuth, selecciona la que quieras
usar antes de iniciar o cerrar sesión. El identificador de una conexión existente
se conserva al editar sus opciones.

Enter avanza al siguiente campo y después al botón de guardar; otro Enter lo
ejecuta. Espacio despliega u oculta las opciones avanzadas. Tab y Shift+Tab recorren
los controles visibles y las flechas recorren lista y botones. Esc o Cancelar
descartan el formulario sin escribir ni iniciar sesión. Durante el login, Cancelar
abandona la espera y descarta completados tardíos de esa operación.

Las URLs OAuth se validan como HTTPS y el User-Agent debe incluir `{version}`.
Los scopes no pueden quedar vacíos ni duplicarse. Una edición concurrente se
rechaza con un mensaje; vuelve a abrir el formulario para actualizar la revisión.
No se sustituyen conexiones existentes con otro método de autenticación: al dar
de alta otra conexión se propone un identificador libre. Las definiciones de
modelos y sus asociaciones con proveedores se conservan.

La configuración se guarda en `<UserConfig>/providers.yaml`. La autenticación
usa el flujo OAuth nativo con PKCE y el almacén protegido de credenciales del Host;
el formulario contiene únicamente la configuración pública de la conexión. Abrir
o guardar el formulario no hace peticiones a modelos. El estado de la cuenta se
lee localmente; una pantalla de progreso no se presenta como sesión conectada
antes de que termine la autenticación.
