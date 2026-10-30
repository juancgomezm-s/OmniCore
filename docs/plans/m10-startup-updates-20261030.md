# M10: actualización al iniciar (incremento local)

Recepción 2026-10-30 del encargo explícito de Juan, entregado por la coordinación
«Encargar actualización automática a M10» (`01a18dea-3324-7614-98fd-a3930f25e012`).
Comprobar/descargar en segundo plano al iniciar; tras verificar, ofrecer instalar
y reiniciar ahora o aplicar en la próxima ejecución. Canal User persistido,
ningún cambio automático stable→preview. Sin prompts bloqueantes en scripts.

La rc.2 de Main `2c9fd13a` ya fue publicada/instalada y aprobó su CI y aceptación
real. Este incremento nuevo se implementa en la misma copia, rama local
`codex/m10-startup-updates`, sin push, merge ni nueva release. No cambia credenciales,
permisos, OmniCoder ni trabajo ajeno. No se despiertan otras coordinaciones/agentes.

## Implementación

- Preparación reutiliza checker, downloader y verificador de paquete/manifest.
  Estado y preferencia propios de updates; descarga no sustituye binarios.
- Decisión humana vinculada a versión/hash/canal e instancia de origen. Ready no
  equivale a autorización. Próximo inicio exige una invocación posterior.
- Guard de procesos npm antes de abrir Session; leases por PID/prefijo y gate
  compartido. Pending/Running/Unknown bloquean el inicio de binarios en reemplazo;
  otras instancias activas aplazan la instalación sin terminarlas.
- Launcher Node conserva terminal/argv/cwd y espera el helper existente después
  de salir la CLI. Reinicio explícito sólo si la TUI no tiene turno, delegación,
  interacción, borrador ni adjuntos pendientes; conserva workspace/Session.
- Red/descarga/verificación fallida deja la versión activa intacta. Efecto Unknown
  nunca se repite: status/rollback/recovery existentes conservan su autoridad.
- Pruebas de happy path inmediato/diferido, instancias, fallos y modo no interactivo,
  con datos/config privados. Ninguna inferencia nueva ni instalación personal.

La CI de Main `41096494046` terminó aprobada 5/5 sobre `2c9fd13a`. Es evidencia
del paquete anterior, independiente de este incremento local.


## Contratos y experiencia

`tools/npm/startup-updates.js` coordina el proceso foreground en la terminal actual.
Antes de abrir Session, `update startup launch <id>` devuelve un resultado JSON
acotado; no carga `CliApp`, configuración de proveedores ni credenciales. La
preparación separada descarga y verifica en segundo plano con un entorno sin
claves ni bootstrap de acciones humanas. Help, status y recuperación no disparan
la descarga. No hay prompts en stdin.

La TUI muestra «Actualización lista» en una fila libre, sin mover mascota ni panel.
Actualizaciones conserva canal/descarga en `updates.json` User, y ofrece las dos
decisiones sólo para Ready. La decisión incluye revisión, release, hash y lanzamiento.
Reinicio inmediato conserva workspace/Session y se rechaza mientras haya trabajo,
interacciones, borradores, adjuntos o preferencias sin guardar. El helper existente
actúa después de salir la CLI; Node espera ese OperationId y relanza en la misma
terminal. Diferir no termina la sesión y exige otro lanzamiento.

Leases y barrera durable bajo el prefijo npm coordinan perfiles de datos distintos.
No se terminan otras instancias. En una instalación Windows de sólo lectura, el
arranque se permite únicamente sin operación activa ni incierta; el preflight
exclusivo de DLL del helper impide sustituirla mientras esté ejecutándose.
Pending/Running/Unknown impiden abrir una Session
sobre archivos en sustitución; un resultado incierto no se reenvía. La activación
revalida archivo, requisitos, canal, versión y procedencia. Applier, respaldo,
helper, status y rollback conservan sus rutas productivas. Fallos previos al helper
no autorizan ningún cambio. Una recuperación necesaria muestra el comando guardado,
sin ejecutarlo automáticamente.

La procedencia automática usa el verificador Windows x64 que ya distribuye el
updater; en las demás plataformas y builds de desarrollo se conserva la consulta
manual de metadatos, sin preparación/activación automática. No se acredita
activación firmada en Linux/macOS. Fixtures en otras
plataformas no sustituyen esa aceptación. No se publicó ni instaló este incremento.

## Validación local

Los logs se conservan en `artifacts/m10-startup-*`. La regresión ampliada anterior
al último ajuste de protección de borradores registró 280 casos: 277 PASS,
0 FAIL, 3 SKIP (un symlink no disponible y dos benchmarks explícitos no ejecutados).
El foco posterior de servicio, updater y localización registró 212 casos:
211 PASS, 0 FAIL y el mismo SKIP de symlink. Arquitectura: 62/62.
Build Release: cero advertencias y errores.
Node final: 66 casos, 65 PASS y un SKIP de symlink; la prueba con procesos reales de
bootstrap/reinicio pasó además en el foco de nueve casos. No hay inferencia nueva.
Foco TUI previo: 15/15, incluidos cuatro locales, cancelación, aprobación,
preferencias sin guardar, decisión diferida y reinicio explícito. Build Release y
arquitectura se repitieron: sin errores, 62/62. La aceptación final conjunta,
incluido el caso de instalación no compatible y el fallback de sólo lectura,
registró 223 casos: 222 PASS, 0 FAIL y un SKIP explícito de symlink.

`npm pack --dry-run --ignore-scripts` confirma los cinco módulos de arranque/helper
requeridos. Es inspección de la lista de fuentes, no una distribución compilada ni
una release: el script productivo de release publica la DLL y añade el módulo nuevo.
No se modificaron Main, la instalación personal, permisos, credenciales ni OmniCoder.
