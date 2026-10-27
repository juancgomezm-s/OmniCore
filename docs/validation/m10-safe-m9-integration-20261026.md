# Integración local acotada de M9 antes de M10 — 2026-10-26

Se revisó el worktree M9 en `f21ad93`, conservando sus archivos y el archivo ajeno
`x` sin cambios. La sesión M9 deja pendiente confirmar el fallo original de Windows
CI y la revisión de Dot antes de publicar. No se hizo push, merge de main ni se
actualizó su rama. Los cambios de OmniCoder están fuera del alcance.

Se integra únicamente el cambio independiente de `f02d3b5` sobre
IsolatedExtensionProcess, su InternalsVisibleTo y sus pruebas. Es el único cambio
de esos archivos en la rama M9 desde nuestra base `916c392`; aplica limpiamente
sin requerir procesos persistentes, nuevos commands, shell humano o ConPTY.

El cierre de una extensión conserva la raíz privada y la excepción original si
falla la barrera de cierre del árbol. Sólo después del cierre confirmado espera
hasta dos segundos ante sharing/lock de Windows; un bloqueo persistente produce
`extension_runtime_cleanup_lock_timeout`. No elimina una raíz de un proceso cuyo
cierre no se haya confirmado, ni considera exitosa una limpieza fallida.

La compilación Release del árbol combinado tiene cero errores y advertencias.
ExtensionIsolationTests: 7/7 PASS, cero omisiones, con aislamiento Strong real,
bloqueos Windows transitorios/persistentes y conservación de evidencia tras error.
Logs: `%TEMP%/omnicore-m9-safe-integration-{build,tests}.log`.

La candidata conserva los cuatro incrementos OpenRouter y sus controles de
visibilidad, precios y tarifa cero. El resto de M9 no se incorpora a la instalación
por esta aceptación local. No se atribuye cierre de M9 ni CI remoto verde.
