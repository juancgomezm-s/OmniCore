# Desktop Engine 0.4.1

Núcleo Rust compartido para observar y operar ventanas Windows. Fuente canónica:
`OmniCore/native/desktop-engine`. Captura WGC/PrintWindow, reducción PNG,
coordenadas retenidas, UIA con lectura/selección/Invoke/Value y entrada SendInput.
Captura y UIA se ejecutan en procesos propios con límite de ocho segundos.

Los dos productos se distribuyen por separado:

- `omnicore-desktop`: adaptador NDJSON fijo incluido en OmniCore. La herramienta
  conserva autorización de Host, selección por skill y cancelación del turno.
  Cortina morada, botón/Escape, observaciones de 30 s y capturas sólo de ventana.
- `desktop-agent`: adaptador CLI/MCP para Claude, con indicador propio de bordes.
  Su dependencia `vendor/desktop-engine` es una instantánea de esta biblioteca,
  verificada por `shared-engine.lock.json`. Se actualiza mediante
  `python scripts/sync_shared_engine.py --source <carpeta-canónica>` y se comprueba
  con el mismo comando más `--check`. No requiere una instalación de OmniCore.

Ambos ejecutables enlazan la biblioteca estáticamente: no se comunican entre sí,
no comparten estado de sesión y no requieren un daemon, DLL o carpeta del otro.
Las decisiones de permisos y el transporte pertenecen a cada adaptador.

`screen_gdi` es una opción explícita del producto MCP independiente y puede incluir
oclusiones. La política de OmniCore no la admite. `delta` conserva su sentido
Win32 anterior; `scroll_x/scroll_y` positivos significan derecha/abajo.
Escape y combinaciones de gestión de sistema siguen restringidos en el núcleo.

WGC fija la DLL del sistema GraphicsCapture.dll durante la vida de cada worker:
se evita que callbacks retrasados accedan a código descargado al cerrar WinRT.
El worker termina normalmente o se cancela desde el padre. La fijación sólo carga
la DLL del directorio System32 y no modifica registro, sistema o configuración.

Validación: `cargo test --locked`, `cargo clippy --locked --all-targets -- -D warnings`.
Los verificadores GUI operan únicamente sobre ventanas de prueba propias.

Nombre comercial de los componentes: **OmniDesktop Control**. Los adaptadores para OmniCore y Claude conservan sus protocolos e identidades de permisos por separado.
