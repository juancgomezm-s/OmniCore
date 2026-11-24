# Configurar servidores MCP desde la TUI

Abre **Ajustes → Extensiones y habilidades → Servidores MCP**, o escribe `/mcp`
en el chat. La lista permite añadir, editar y quitar conexiones de usuario.
Todas las acciones están en una columna; las flechas recorren la lista y los
botones. Al no haber servidores, el foco empieza en **Añadir servidor**.

En el formulario, Enter avanza al campo siguiente y después a **Guardar**.
Otro Enter confirma el guardado. Tab y Shift+Tab recorren los controles visibles;
Esc vuelve sin guardar. En Argumentos, Shift+Enter inserta una línea y Tab sale
del editor.

El **Nombre del servidor** admite espacios, por ejemplo `RAG local`. Debajo se
muestra el identificador técnico generado, `rag-local`, usado por las herramientas.
Cambiar el nombre de una conexión existente conserva su identificador y las
referencias de sus herramientas. Las configuraciones anteriores conservan sus IDs.
Si un campo no es válido, el aviso identifica el campo y el foco vuelve a él.

## Conexión local: stdio

Elige un nombre y el transporte **Local · stdio**. Indica la ruta
absoluta del ejecutable y sus argumentos, uno por línea. Por ejemplo, para un
servidor JavaScript ya instalado, el ejecutable es la ruta de `node.exe` y el
primer argumento es la ruta del archivo del servidor. El directorio de trabajo
es opcional; si se indica debe ser absoluto.

Los argumentos se envían literalmente, sin interpretarlos como shell. Una línea
con `""` representa un argumento vacío; para comillas dobles literales o una
barra invertida inicial, antepón otra barra invertida. No se instalan paquetes
ni se inicia el programa al guardar. Los servidores locales mantienen el sandbox
Strong y el techo de recursos de MCP; configurar un comando no amplía sus permisos.

## Conexión remota: HTTP

Selecciona **Remoto · HTTP** e introduce la URL completa del endpoint MCP. Se
acepta HTTPS, o HTTP en una IP explícita de loopback, por ejemplo
`http://127.0.0.1:3000/mcp`. No se admiten credenciales, query ni fragmento en la URL.
El Host limita el destino al host y puerto configurados.

Si necesita un token Bearer, selecciona el servidor y **Configurar credencial**.
El campo está enmascarado y el secreto se guarda en el almacén de credenciales
del sistema; `servers.json` sólo contiene su referencia. Después elige
**Autorizar conexión** y revisa el destino antes de confirmar. Guardar, editar o
cambiar credenciales no autoriza conexiones remotas; editar revoca su autorización
anterior. La confirmación se abre con **Cancelar** seleccionado.

## Aplicación y alcance

Abrir estas pantallas, guardar o autorizar no realiza sondeos, llamadas a modelos
ni conexiones. La composición de herramientas del siguiente turno lee los
servidores configurados y realiza el descubrimiento de los autorizados. La
autorización del servidor no concede permisos de ejecución a sus herramientas:
siguen pasando por el motor de permisos y la política de la sesión.

Las conexiones de usuario se guardan en `<UserConfig>/mcp/servers.json`. Los
servidores de proyecto de `.omnicore/mcp.servers.json` aparecen como sólo lectura;
desde la pantalla se puede conceder o revocar su consentimiento, sujeto a la
confianza del workspace. No se sobrescriben sus archivos al gestionar servidores
de usuario. Quitar un servidor requiere confirmación y conserva sesiones y
programas instalados. Las ediciones comprueban la revisión y rechazan formularios
obsoletos para conservar cambios de otras ventanas.
