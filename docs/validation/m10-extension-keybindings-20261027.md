# M10-G — atajos aportados por extensiones (2026-10-27)

## Alcance y delegación

El usuario autorizó continuar M10 y delegar el trabajo principal a Luna.
Luna implementa el descubrimiento de bindings de extensiones, sus contratos y el
cableado TUI; la sesión raíz revisa el diff, valida y gestiona la instalación.
La revisión no equivale a aceptar automáticamente una entrega del obrero.

Este corte completa una conexión pendiente del motor M10-A/D. Las extensiones
aportan defaults para sus propios comandos calificados; no configuran acciones
Core, otras fuentes, prioridades arbitrarias o eliminaciones de defaults. Los
argumentos siguen siendo argv literales y el despacho usa el consumidor Host ya
compartido por slash, paleta y keybindings.

El descubrimiento es de sólo lectura, sin activar procesos ni conceder permisos.
Sólo se proyectan paquetes actuales, habilitados y con el consentimiento/trust ya
exigido por el catálogo de extensiones existente. La ejecución revalida la identidad
actual del paquete, contribución y comando; una revocación o actualización exige
resolver de nuevo el binding antes de ejecutar.

Los defaults de extensiones quedan acotados al foco de entrada del chat, fuera de
overlays e interacciones. No deben consumir navegación de formularios o aprobaciones.
La primera pulsación requiere Ctrl, Alt o una tecla F1–F24; las pulsaciones posteriores
de una secuencia pueden ser letras ordinarias después de ese prefijo explícito.
La configuración User conserva su precedencia documentada y su reserva de capacidad;
un exceso de contribuciones de extensiones no puede retirar todos los bindings User.

Cada paquete puede declarar hasta 16 bindings. Hay un máximo separado de 256
contribuciones de extensiones; si se excede, se rechaza ese conjunto con diagnóstico
y se mantienen User/defaults. User conserva su límite previo de 256 entradas. La
identidad Source admite hasta 512 caracteres para coincidir con el catálogo, sin
reescribir ni inferir ownership. La condición final incluye el guard de foco y pasa
por los límites/diagnósticos existentes del resolver.

Ejemplo de fragmento para un comando `acme.tools:review` ya declarado por el paquete:

```yaml
provides:
  # conservar aquí tools y commands existentes
  keyBindings:
    - keys: Ctrl+Alt+R
      command: acme.tools:review
      arguments: ['{"path":"src/Example.cs"}']
      when: input
requests:
  capabilities: [tool.register, command.register, keybinding.register]
  # conservar aquí el techo de permisos existente
```

Este fragmento no es un paquete instalable autónomo. Añadirlo cambia el hash del
manifiesto y requiere el consentimiento/enable del lifecycle existente. No se
modifican los paquetes o archivos User reales como parte de esta entrega.

## Coordinación

Worktree M10: `openrouter-catalog-grid/OmniCore`, rama `codex/m10-keybindings`.
M9 mantiene su checkout `m9-completion/OmniCore`; al comenzar este corte se observaron
cambios suyos en `OmniServer.Conversations.cs`, `StdioProtocolHost.cs`, el nuevo
`WorkspaceConversationImportSource.cs` y sus pruebas. Esos archivos, el consumidor
OmniCoder y sus artefactos/instalaciones quedan fuera del encargo a Luna.

La consulta de contribuciones necesaria para la TUI es in-process; no se amplía el
allowlist stdio, la admisión humana, permisos, recibos o seguridad por Session de M9.
Builds, logs y capturas usan este worktree y temporales propios. Los launchers
globales se gestionan sólo desde la sesión raíz después de revisar la entrega.

## Validación y entrega

Luna entregó parser/manifiesto, contrato Protocol, consulta Host, cableado TUI y
pruebas focales. La revisión raíz corrigió/admitió estos puntos antes de instalar:
el primer descubrimiento refresca el registro declarativo; el ownership viene sólo
del catálogo Host; se rechazan prefijos ordinarios; metadatos JSON inválidos no
rompen la UI; una actualización/deshabilitación/desinstalación invalida el binding;
y el segundo chequeo de `provides` en manifiestos v2 admite `keyBindings` igual que
el primero. La revisión añadió roundtrip/bounds/JSON incompleto y aceptación v1/v2.

Build final raíz Release de `OmniCore.slnx`, incluidos net8/net10: 0 advertencias,
0 errores. Pasada focal final sobre los assemblies producidos:

| Grupo | Correctas | Fallos | Omisiones |
|---|---:|---:|---:|
| Protocol/manifiestos v1-v2, resolver/configuración/dispatcher, paleta, búsqueda e inspector | 143 | 0 | 0 |
| TUI extensiones, teclado, paleta, búsqueda e inspector | 59 | 0 | 0 |
| Arquitectura | 62 | 0 | 0 |
| Total focal final | 264 | 0 | 0 |

Logs finales `%TEMP%/omni-m10g-root-final-build.log` y
`%TEMP%/omni-m10g-final-{client,tui,architecture}.log`. Los logs de la entrega de
Luna son `%TEMP%/omni-m10-extbindings-{build8,model-tests-final2,tui-tests-handover,
regression-model,regression-tui,architecture}.log`: build limpio, 63 modelos,
4 recorridos nuevos, 24 regresiones de modelo, 35 TUI y 62 arquitectura correctos.
Esos grupos solapan la pasada final; no se suman para inflar el total.

La primera pasada raíz detectó el rechazo de `keyBindings` en el segundo whitelist
de `provides` de v2; se corrigió antes de la pasada final y se añadió despacho TUI
v2 además del parsing. La prueba de Enter descubrió la necesidad del guard de
prefijos ordinarios; Luna lo corrigió antes de entregar. No se declara una suite
full ni ejecución real de un proveedor/proceso de extensión: el recorrido positivo
usa Host real y un turn host controlado para verificar el consumidor común sin
activar procesos o gastar tokens.

`git apply --check` del diff acumulado desde `ffa96af` en archivos compartidos,
incluido el único hunk nuevo de OmniServer.Query, pasa en M9 `0f90367`. No se aplicó
el patch ni se alteraron sus archivos. Esto comprueba compatibilidad de hunks,
no integración ni aceptación de los pendientes de M9.

La instalación versionada se registra tras verificar el paquete.

## Límites del hito

Este corte no cierra M10. Continúan memoria multiscoped, búsquedas más amplias,
undo, evaluación full, LSP/parches múltiples y el actualizador GitHub según el plan.
La verificación de fixtures no acredita ejecución de proveedores ni aceptación
manual del usuario; las pendientes de M9 conservan su propia revisión.
