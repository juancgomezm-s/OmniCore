# M10-D — atajos User para comandos del Host

## Alcance

Siguiente corte del bloque teclado/paleta de M10 en OmniCore, sobre `e969dd6`,
en Zephyrus y la rama `codex/m10-keybindings`. No se declara M10 cerrado.

El archivo User puede apuntar a un comando Host por ID calificado:

```yaml
version: 1
bindings:
  - keys: Ctrl+Alt+E
    command: core:explain
    arguments: ["src/Example.cs"]
    when: input && !overlayOpen && !awaitingPermission
```

Usar la ruta User mostrada por `/keybindings` y recargar explícitamente después
de editarla o cambiar la definición/fuente del comando. El ejemplo no se instala
automáticamente ni se escribe configuración personal.

## Construcción y límites

- Carga condicional del catálogo Host al compilar bindings que contienen IDs
  calificados. Los defaults y bindings sólo cliente no añaden esa consulta.
- Targets conocidos con ownership obtenido del catálogo; IDs desconocidos o
  cortos no publicados se diagnostican sin ejecución. Se preservan las reglas
  que impiden a una extensión reclamar targets de Core u otro propietario.
- IClientCommandHandler adapta el dispatcher portable a comandos Host. Los
  comandos cliente conservan la ruta original. Sin adaptador explícito no existe
  fallback que trate un comando desconocido como acción o como texto del modelo.
- Antes de enviar: PollEvents, mismo scope de UI, When todavía verdadero y misma
  resolución, sin trabajo de modelo activo ni overlay/aprobación pendiente. Se
  revalida fuente, fingerprint, disponibilidad y contrato de argumentos.
- Origen `KeyBinding:User` y argv literal; el mismo RunHostCommand consume los
  outcomes Prompt/Workflow/Extension usados por slash/paleta. CancellationToken
  se propaga al Send/Query. Los gates y permisos siguen decidiéndose en el Host.
- Cambiar fingerprint o disponibilidad impide despachar; recargar permite admitir
  la versión nueva. Un error leyendo el catálogo conserva los defaults. Un fallo
  posterior al envío informa resultado no confirmado y no reintenta el comando.
- Se conservan borrador, adjuntos, idioma y configuración User. Los hints de paleta
  corresponden a la misma invocación/argv; los presets con argumentos distintos
  se inspeccionan en /keybindings sin anunciar una equivalencia falsa.

No se cambia la persistencia ni el transporte/autoridad nativos de M9. No se
construye todavía discovery automático de contribuciones de bindings de extensión,
ni editor gráfico de presets. Los demás bloques de M10 conservan sus pendientes.

## Validación terminada

- Solución Release, incluido Client/Protocol net8/net10: cero errores/advertencias.
- **83 PASS, 0 FAIL, 0 SKIP**: dispatcher Host explícito, argv/origen, cancelación,
  propiedad de targets, motor/configuración, catálogo, registro y recursos es/en/fr/it.
- **35 PASS, 0 FAIL, 0 SKIP** de TUI: cuatro recorridos del comando real core:explain
  hasta el consumidor Host, borrador/adjunto intactos, cambio de huella, recarga,
  disponibilidad, ID calificado ante nombre ambiguo, cambio de Session, aprobación,
  catálogo inaccesible y target desconocido; regresiones de Ctrl+P/paleta/M10-B.
- **62 PASS, 0 FAIL, 0 SKIP** de arquitectura. Sin ampliar el grafo permitido.
- Logs `%TEMP%/omni-m10d-{solution,final-client,final-tui,final-architecture}.log`.

No se usan proveedores, credenciales ni conversaciones reales para las pruebas.
Las fixtures acreditan wiring; no equivalen a aceptación humana de todos los OS.
M9 conserva `f21ad93` y su archivo no seguido `x`; la sesión informa CI aprobada y
pendientes propios. No se toca su worktree, PR, publicación ni documentos sucios de
main. El parche de archivos compartidos acumulado con M10-C se comprueba mediante
`git apply --check` sobre M9 sin aplicarlo. Este corte nuevo sólo modifica el
dispatcher Client y parciales TUI propios; no cambia Host/Protocol ni el shell M9.

La instalación usa el flujo de prefijo de versión separado, verifica publicación
antes de cambiar accesos y preserva sus copias previas; no termina procesos del
usuario ni modifica datos/permisos persistentes. Sin push, merge o publicación.

## Instalación observada

Código `78acf07`; ProductVersion
`0.1.0+78acf07efc3512e024920566aedfc0cb27d22bbe`, instalado en
`%LOCALAPPDATA%/OmniCore/cli/releases/78acf07/` en `ZEPHYRUSG14`.
Los **138 archivos** coinciden por SHA-256 con la publicación; se comprueba versión
y `--help` antes de cambiar accesos. Backups de los tres launchers en
`previous-launchers/`; los accesos CMD y PowerShell terminan `--help` con código 0.
Manifiesto `%TEMP%/omni-m10d-install-verification.json`, paquete/hash en
`%TEMP%/omni-m10d-package.json`. No se cerraron procesos. Reabrir omni para cargar
esta versión; el archivo User permanece a cargo del usuario, sin escrituras nuevas.
