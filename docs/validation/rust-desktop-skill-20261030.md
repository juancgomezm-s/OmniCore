# Escritorio Rust mediante skill — 2026-10-30

Implementado e instalado localmente por solicitud del usuario. El backend completo
de capturas, UIA, teclado y ratón está en `native/desktop-control` (Rust); un
adaptador C# integra `desktop.control` en Host. No usa el ejecutable original ni
decompilación. Sustituye al prototipo Python servido por MCP/HTTP.

## Uso

En una conversación abierta de OmniCore:

```text
/skills load desktop-control BuiltIn
```

Después pedir la operación de escritorio. Para retirarla:

```text
/skills release desktop-control BuiltIn
```

El modelo puede cargarla explícitamente con `skill.load` para el siguiente turno.
El catálogo inactivo no expone `desktop.control`, ni mediante discovery ni con
el ID adivinado. Sólo la composición principal con el paquete BuiltIn sellado
activo la incorpora. Las Lanes delegadas y etapas de workflow no la reciben.
Una liberación o modificación del paquete impide ejecutar incluso un intent ya
preparado. La skill no concede permisos ni cambia modo/modelo: se conservan el
techo GeneralProcess y la evaluación de permisos ExternalOrUnknownProcess.

## Validación

- Compilación completa `dotnet build OmniCore.slnx`: correcta, cero advertencias.
- Rust: cuatro pruebas internas; `cargo clippy --all-targets -- -D warnings` limpio.
- Skills/control de escritorio: 33 casos; arquitectura: 62, todos correctos.
- Prueba GUI propia: 15 comprobaciones correctas. Captura PNG de 845 × 649 con
  DPI 125 %, alineación de píxeles, UIA/contraseñas redactadas, Unicode con emoji,
  atajos/Enter, clic por elemento, arrastre, rueda, foco, geometría, caducidad,
  coordenadas inválidas y aislamiento del timeout de captura.
- Ruta Host/skill del paquete distribuido: 29 llamadas por el runtime de permisos,
  seis capturas entregadas como ImageBlock a un proveedor determinista local;
  carga mediante SkillLoadTool y liberación comprobadas. Cero llamadas a proveedores
  reales. Hashes de los ensamblados comprobados contra la instalación.
- Skill portable validada con `quick_validate.py`; parche comprobado con
  `git apply --check` sobre la base más las correcciones MCP anteriores.

Instalación npm local: `0.1.0-desktop.20261030`, bajo
`C:/Users/juanc/Documents/Codex/cua-mcp/omnicore-releases/20261030-desktop-rust`.
Los launchers globales PowerShell y CMD apuntan a esa versión; anteriores guardados
en `previous-launchers`. Se retiró sólo `cua-prototype` de la configuración MCP,
con respaldo `servers.json.pre-rust-20261030`, y se detuvo su servidor Python.
No se alteraron modelo, modo ni permisos del usuario. No hay nuevo arranque
automático, puerto, token o servidor persistente para el componente Rust.

## Límites

Validación local Windows x64 contra una ventana controlada. No acredita todas las
aplicaciones, Linux/macOS ni una regresión global completa. PrintWindow puede
devolver negro o contenido incompleto; las observaciones secuenciales no detectan
todo cambio de contenido. No se eluden UIPI, escritorios protegidos o restricciones
de foco. El helper fijo de Core accede a la sesión del usuario; no se presenta
como proceso de proyecto en Strong. Mantiene entradas NonIdempotent y no reintenta
efectos inciertos. Véase ADR-0064.

Fuentes, binario, parche y evidencia se entregan en
`outputs/omnicore-desktop-rust` del workspace de esta conversación.
