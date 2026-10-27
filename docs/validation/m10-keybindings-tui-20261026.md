# M10-B — motor de teclado conectado a la TUI

## Alcance

Continuación de `ffa96af` en `codex/m10-keybindings`, dentro del worktree
`openrouter-catalog-grid/OmniCore`. Exclusivamente OmniCore. Se conservan los
menús aprobados, el catálogo OpenRouter, preferencias y selección de modelos.

- Terminal.Gui → KeyChord → KeyBindingSession → KeyBindingDispatcher → acciones
  existentes. No hay teclas ni dependencias gráficas nuevas en Core o Protocol.
- F2/F3/F4 configurables; secuencias de hasta cuatro teclas con timeout de 1 s,
  descarte de prefijo por foco/Session/Run y reconsideración de la última tecla.
- Borrador y adjuntos no se envían ni se limpian al invocar un atajo.
- Ctrl+C interrumpe primero; la segunda pulsación cancela el Run.
  Ctrl+Shift+C cancela explícitamente. Una cancelación de Turn local no impide
  alcanzar `run.cancel`. El token local sólo se cancela en la Session propietaria.
- Esc conserva default/denegar y el cierre de overlays. Navegación por atajos no
  sustituye una aprobación pendiente. Una reasignación a Esc o Meta se diagnostica
  antes de validar cobertura protegida; no deja interrupt/cancel inaccesibles.
- `/keybindings`: inspección de fuente, claves, destino, condición y prioridad,
  conflictos/errores localizados es/en/fr/it y posición de origen. Recarga mediante
  botón o `/keybindings reload`; apertura posterior lee el mismo archivo User.
  Nunca se genera/escribe configuración User al arrancar o recargar.
- Ayuda de F2/F3/F4 refleja reasignaciones; incidencias de configuración anuncian
  `/keybindings` en la ayuda del chat.

## Defectos encontrados al integrar

M10-A introducía un segundo contexto YAML estático. Las pruebas que sólo cargaban
keybindings pasaban, pero arrancar la TUI lanzaba NotImplementedException al crear
el lector de ScopedSettings: el generador fijado dejaba sin generar el contexto
previo. Los DTOs nuevos ahora pertenecen a OmniYamlStaticContext. Las regresiones
de configuración existentes se ejecutan junto con el lector nuevo.

YamlDotNet ya numera líneas/columnas desde 1: se elimina el incremento adicional
del lector nuevo y se fija con una comprobación de posición exacta. También se
normaliza el símbolo menos con su alias portable Minus.

## Coordinación con M9

Se inspeccionó la sesión «Analiza y reconoce el avance del plan hasta el momento»
y el worktree `m9-completion/OmniCore`, candidato `f21ad93`. M9 continúa su propia
validación/publicación y no recibe commits, mensajes, cambios de archivos ni
comandos de instalación desde este corte. Su archivo no seguido `x` se preserva.

El comportamiento nuevo vive en `TuiApp.KeyBindings.cs` y `TerminalKeyChord.cs`.
Los enganches del archivo compartido se limitan a inicialización/captura,
entrada local `/keybindings`, preservación de borrador, cancelación y ayuda.
No se modifica `StartHumanShellAction`, la implementación de procesos/sandbox,
el SDK de M9 ni su rama. `git apply --check` del parche de archivos compartidos
sobre el worktree M9 verifica compatibilidad sin aplicarlo. No es una integración
ni una aceptación de sus cambios pendientes. Los documentos sucios de main quedan
intactos; la actualización de ADR y esta acta pertenecen sólo a esta rama.

## Uso

Abrir `/keybindings` para conocer la ruta User efectiva. Copiar/adaptar
`docs/examples/keybindings.yaml` en esa ruta y recargar. Ejemplo mínimo:

```yaml
version: 1
bindings:
  - action: -sidebar.toggle
  - keys: F7
    action: sidebar.toggle
  - keys: Ctrl+K Ctrl+P
    command: plan
    when: input && !overlayOpen
```

Las teclas válidas se resuelven con prioridad y después User > Extension > Default.
No se instalan ejemplos automáticamente ni se cambian preferencias del usuario.

## Límites

La paleta global `palette.open` se diagnostica como no disponible; el selector de
modelos F3 es el existente. El motor acepta contribuciones verificadas de extensión,
pero la TUI todavía no descubre esas contribuciones. No hay editor gráfico de YAML
ni watcher de archivo. El resto de M10 (búsqueda, undo, memoria, evaluación, etc.)
y las aceptaciones de M9/M8 continúan en sus propios planes. Las pruebas de driver
no acreditan una aceptación humana ni todos los terminales/OS. No se hacen llamadas
a proveedores de pago ni se usan conversaciones reales para las pruebas.

## Validación reproducible

Solución Release sin errores ni advertencias, incluidos Client net8/net10.
Se ejecutan tres grupos sobre el árbol final:

- Motor, configuración User, configuración heredada, budgets y acciones cliente:
  **100 PASS, 0 FAIL, 0 SKIP**.
- Driver TUI, adaptación de teclas, recursos y regresiones de menús/interacciones:
  **44 PASS, 0 FAIL, 0 SKIP**. Incluye 14 recorridos M10 (rebind/recarga, secuencia,
  foco, otra conversación, conflicto, cuatro idiomas, YAML inválido, permiso
  pendiente, interrupción/cancelación sin Turn y con Turn local aún deteniéndose,
  y dos tamaños de pantalla), 12 casos de adaptación/recursos y regresiones de
  F2, F3/Ctrl+M, preferencias, cuestionarios y extensiones en cuatro idiomas.
- Arquitectura: **62 PASS, 0 FAIL, 0 SKIP**. No se amplía el grafo permitido.

Las capturas reales RGB 80×25 y 130×38 se exportan en
`%TEMP%/omni-m10b-frames-final/settings-m10-keybindings-*`; se rasterizan con
Sharp y se inspeccionan ambas. La variante estrecha tiene scroll y botones
visibles; la amplia muestra defaults, condiciones y diagnósticos completos.
Una primera captura heredó NO_COLOR del entorno: se conserva como accesibilidad,
no como evidencia del estilo normal; la prueba RGB elimina ese override sólo
durante su fixture y lo restaura después.

La tentativa amplia de toda TuiWiringTests se interrumpió y **no cuenta como
suite integral aprobada**: faltaba `sdk/extensions/dist`, y producir/actualizar
binarios con ese runner abierto causó sharing violations de Windows. Se detuvo
únicamente ese proceso propio, se preparó el SDK local con `npm ci --ignore-scripts`
y `npm run build`, y se volvieron a ejecutar los casos de extensiones afectados
dentro del grupo focal final. También se corrigió la aserción de un título antiguo
del mantenimiento de modelos para consultar su recurso localizado vigente.

Logs: `%TEMP%/omni-m10b-{solution,final-config,final-tui,final-architecture}.log`.
El parche compartido final se comprueba con `git apply --check` sobre `f21ad93`
sin aplicarlo; el estado de main y M9 conserva únicamente sus cambios previos.

## Actualización local

Comando estándar: `npm run install:cli`. Publica el árbol de esta rama, comprueba
`--help`, empaqueta y actualiza el comando global. La comprobación posterior
compara SHA-256 de todas las DLL publicadas/instaladas y ProductVersion de
`omni.dll`; no se usa `--version` (esa opción no pertenece al CLI).
La TUI que ya estaba abierta conserva su proceso y necesita abrirse de nuevo
para usar el binario actualizado. No se finaliza el proceso del usuario.
