# Cliente MCP y prototipo de escritorio — 2026-10-30

Se construyó e instaló localmente `omnicore-cli@0.1.0-mcp.20261030` a partir
de `04d226b9f386ed94e898d5f0ff8f325402977bba` y los cambios MCP de esta tarea.
El checkout principal conserva sus demás cambios; no se incorporaron a la
compilación instalada. No se publicaron commits, paquetes remotos ni cambios
de OmniCoder.

## Comportamiento

- Stdio conserva un proceso Strong dúplex durante el recorrido del Host, con
  correlación de IDs, lectura de notificaciones y drenaje independiente de stderr.
  Cierre por EOF, plazo y terminación del árbol. Un fallo no repite herramientas.
- Discovery moderno y handshake anterior con revisión comprobada. Un error moderno
  reconocido conserva su código y no se confunde con un servidor anterior.
  Un sondeo stdio silencioso puede reiniciar exclusivamente ese sondeo antes del
  handshake; no es un reintento de `tools/call`.
- HTTP conserva sus límites, rechaza redirecciones, procesa errores JSON-RPC
  antes de descartarlos por el estado HTTP y administra sesiones anteriores.
  Valida anotaciones `x-mcp-header` y refleja parámetros primitivos codificados.
- El catálogo recorre hasta 16 páginas/256 herramientas, rechaza ciclos de cursor
  e informa exclusiones. Un servidor averiado no impide cargar los demás.
- CLI y parser comparten la validación de endpoint: HTTPS remoto y HTTP únicamente
  con una dirección IP loopback explícita. Configuración y confianza no equivalen
  a conectividad: la inspección agrega `connectivity: NotProbed`.
- Timeout de arranque y de petición configurables entre 1 y 300 segundos; los
  valores por defecto son 10 y 30 segundos, respectivamente.
- Las imágenes inline se decodifican y validan, se guardan como artefactos
  sensibles en CAS y llegan al siguiente paso del modelo mediante `ImageBlock`.
  El base64 se retira de su representación textual.

## Autoridad y aislamiento

Las herramientas conservan Permission Engine, `AuthorizedToolIntent`, revisión
de confianza, consentimiento de primer efecto y aislamiento de Session/Run/Lane.
Las anotaciones remotas no conceden permisos. Stdio continúa sin red ni rutas
adicionales. El nuevo `SandboxLaunchSpec.IsolationKey` permite perfiles Strong
independientes para servidores persistentes con el mismo cwd; no concede recursos
y conserva la identidad pública anterior cuando no se especifica.

`input_required` y otros tipos de resultado no implementados producen un error
explícito; una operación con posibles efectos permanece incierta. No se anuncian
sampling, elicitation, roots ni continuaciones interactivas. El contrato actual
admite una imagen por resultado; varias se rechazan expresamente.

## Evidencia

- Compilación de solución net10/net8 sin errores ni advertencias.
- Foco MCP y materialización: 49 casos, 48 aprobados y una omisión por restricción
  de creación de symlinks en este entorno. Incluye procesos AppContainer reales,
  continuidad, compatibilidad anterior, sondeo silencioso, headers, paginación,
  errores, imágenes y Deny antes de enviar una herramienta.
- Arquitectura: 62/62. SDK de extensiones: 44/44.
- Aceptación de escritorio con los mismos bytes de Host de la instalación:
  las 15 comprobaciones de la ventana WinForms propia pasaron. Incluye DPI,
  accesibilidad, password, Unicode, atajos, clic, arrastre, rueda y observaciones
  invalidadas, movidas, sin foco, vencidas y timeout de captura.
- El ensayo pasó por el runtime de permisos de OmniCore. Siete observaciones
  visuales llegaron al siguiente paso de un proveedor determinista como imágenes,
  sin solicitudes a proveedores reales. No acredita comprensión de un modelo live.
- Discovery de producción con la credencial protegida: diez herramientas.
  PowerShell y CMD ejecutan los lanzadores instalados correctamente.

La regresión amplia se conserva en XML junto con la evidencia de la tarea.
La copia aislada necesitó `npm ci --ignore-scripts` y `npm run build` en
`sdk/extensions` para las fixtures TUI; estos artefactos de desarrollo no están
en Git. Las corridas paralelas se sustituyeron por comprobación secuencial.

La suite global **no se declara aprobada**. El primer tramo secuencial registró
2.779 aprobaciones, 24 omisiones, un caso no ejecutado y el fallo por el SDK de
desarrollo ausente. Tras compilarlo, la continuación se acotó después de más de
cinco minutos sin salida: el ejecutor esperaba lecturas de pipes y tenía un
proceso `git.exe` de fixture como hijo. Se conservaron las pilas administradas;
esa espera no demuestra por sí sola un defecto de Git ni del módulo MCP. No hubo
XML final de la continuación. Las aceptaciones MCP y de escritorio enumeradas
arriba sí terminaron correctamente; no se sustituyen por un supuesto éxito global.

## Instalación y uso

La instalación reside en
`C:/Users/juanc/Documents/Codex/cua-mcp/omnicore-releases/20261030-mcp/`.
Los lanzadores anteriores están en `previous-launchers/`; el manifiesto de
instalación conserva sus hashes. Se verificó el paquete y la identidad del
ensamblado utilizado en la aceptación de escritorio.

El servidor `cua-prototype` 0.3.1 escucha en `127.0.0.1:18743/mcp`, con credencial
DPAPI y registro protegido de OmniCore. Está activo en la sesión actual. Tras
reiniciar Windows se inicia con `C:/Users/juanc/Documents/Codex/cua-mcp/Start-CuaMcp.ps1`.
No se agregó un arranque automático ni se alteró el modo o modelo del usuario.

Una instancia nueva de `omni` utiliza esta versión. Para capturas se necesita un
modelo/ruta con entrada de imagen; para actuar, un modo/perfil que admita esos
efectos y las aprobaciones normales. El prototipo permite UIA sin imagen para
rutas de texto. Captura con PrintWindow y sus límites de contenido acelerado o
protegido; no se afirma compatibilidad con todas las aplicaciones.
