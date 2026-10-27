# M10-A — motor de teclado y configuración User

## Entrega

Construido en `codex/m10-keybindings`, partiendo de la instalación local `bedd42b`.
No modifica los archivos compartidos de captura TuiApp, CliApp, OmniServer,
ExplorerTurn ni Localization. M9 permanece en su worktree, congelado para esta
comparación en `f21ad93` con el archivo ajeno `x` intacto. No se hace push ni merge
de main ni se escribe OmniCoder.

La instalación global se actualizó **antes** de iniciar este corte: conserva las
mejoras OpenRouter y el backport independiente de cleanup de extensiones `f02d3b5`.
Versión instalada `0.1.0+bedd42b1935953097a077a8fb171722f2a6c205e`, 55 DLL idénticas
por SHA-256 y `omni --help` con exit 0. Esa instalación no incluye aún el consumidor
TUI del motor nuevo. La integración M9 se documenta en
`m10-safe-m9-integration-20261026.md`.

## Motor y fronteras

- KeyChord portable con Ctrl/Alt/Shift/Meta, normalización independiente del idioma
  y secuencias de hasta cuatro teclas.
- When booleano con `!`, `&&`, `||` y paréntesis; sin eval ni interpretación de código.
  Las variables deben pertenecer a los contextos publicados por el cliente; una
  clave ausente vale false. Texto, profundidad y número de contextos son acotados.
- Priority primero; a igualdad User > Extension > Default. Un empate de targets
  diferentes devuelve Conflict, nunca ejecución arbitraria por orden de registro.
- Cada cliente tiene su estado de secuencia, con timeout configurable y reset ante
  cambios de contexto. Una combinación inválida reconsidera la última tecla sola.
  Los bindings completos se resuelven sin esperar; prefijos que sombrean otras
  secuencias producen diagnóstico explícito.
- La cobertura de interrupción y cancelación se verifica sobre todos los estados
  de las claves de contexto publicadas. Las restauraciones se vuelven a comprobar
  entre sí para reparar reasignaciones cruzadas. Los defaults sólo se recuperan
  cuando la acción queda inaccesible, con diagnósticos de restauración/colisión.
- El registro de targets aporta ownership; la configuración YAML no declara fuente
  ni autoridad. El adaptador fija Source=Extension para contribuciones externas.
  No se pueden reasignar acciones Core desde una extensión, incluso usando un
  alias/casing de command que resuelva a esa misma acción.
- El dispatcher usa ClientActionInvocation/IClientActionHandler y el registro de
  commands existente. Los argumentos se conservan como datos literales. Un prefijo
  o conflicto no se despacha; el token de cancelación se propaga.

## Configuración

El Host lee exclusivamente `<config User>/keybindings.yaml`, con DTOs y contexto
estático YamlDotNet. Hay schema versionado y ejemplo en `docs/examples/keybindings.yaml`.
No busca `.omnicore/keybindings.yaml` ni modifica el archivo del usuario.

El archivo tiene `version: 1` y `bindings`. Cada entrada declara keys, action o
command, argumentos literales opcionales para command, when y priority. Para
deshabilitar un default se usa `action: -<ActionId>` sin teclas. Los límites son
65.536 caracteres por documento, 256 entradas, 16 argumentos y prioridad -1000..1000.
Un error estructural devuelve diagnósticos con posición y no aplica parcialmente
el documento. Los errores semánticos del motor conservan fuente y se asocian a la
línea/columna User en el adaptador. Los defaults siguen disponibles.

## Validación

- Solución completa en Release: cero errores y cero advertencias, incluido Client
  net8 y net10.
- Motor/configuración más regresión de ClientAction: **66 PASS, 0 FAIL, 0 SKIP**.
  Incluye precedencia, ownership, casing de commands Core, conflictos, booleanos,
  secuencias/timeouts, restauración protegida y cruzada, literales, cuatro idiomas,
  configuración inválida/duplicada, posiciones, límites, User-only y cancelación.
- Arquitectura: **62 PASS, 0 FAIL, 0 SKIP**. La primera revisión detectó una firma
  CLI con IPlatformPaths que introducía referencia IL a Abstractions; se sustituyó
  por el contrato primitivo del Host. No se amplió el allowlist.
- La corrección de cleanup M9 se verificó antes de instalar: **7/7** pruebas de
  ExtensionIsolationTests sin omisiones; no se suman a una supuesta suite completa.

Logs `%TEMP%/omnicore-m10-keybindings-{solution,client-build,final-tests,architecture}.log`.
El primer build sin restore detectó que el nuevo worktree no tenía assets de
ArchitectureTests; el restore y build posterior pasan. No se presenta esa pasada
fallida ni una salida anterior como aceptación del árbol final.

## Próximo corte, separado de M9

M10-B conectará eventos reales de Terminal.Gui a KeyChord, contexto/foco y despacho
existentes, con paridad de interrupt/cancel y conservación del composer. Añadirá
`/keybindings`, presentación de fuentes/conflictos es/en/fr/it y aceptación de
reasignación/reapertura en la TUI. La paleta global permanece como su propio
consumidor. No se declara M10 cerrado ni atajos configurables funcionando en vivo
por la sola existencia de este backend.
