# Propuestas visuales: cangrejo y robot

Diseños originales inspirados en Claude y Codex, integrados como invitados
ocasionales de sesión. No modifican las seis especies ni el sorteo del perfil.

Las dos pruebas `Companion_design_preview_renders_six_native_sixel_states` pasan.
Controlan el selector de visitas en el servicio de pruebas y usan las consultas
reales del Host y el `ImageView` de producción. La especie habitual sigue siendo
el buey. Los `.sixel` contienen la salida real de ImageView. También comprueban el
respaldo Unicode/textual, ocultar y conservar el foco. El sorteo y el aumento por
uso real de modelos se prueban por separado en `CompanionGuestTests.cs`.

`xterm-*.png` son capturas del emulador xterm.js 5.5.0 con addon-image 0.8.0,
reproduciendo esa salida; no representan una sesión completa conectada de OmniCore.
Los GIF alternan seis capturas: reposo, trabajo, espera, celebración, fallo y caricia.
Cada estado dura un segundo para facilitar la revisión del diseño; no reproduce
la cadencia de animación de producción. Versiones y limpieza comprobada en
`design-preview-validation.json`.

Validación de integración: 61 casos de contratos/servicio/presentación/headless
en `TestResults/pet-guests-contracts.trx` y 16 casos de TUI nativa en
`TestResults/pet-guests-render.trx`, todos correctos. Las capturas de invitados
se repitieron en `TestResults/pet-guests-captures.trx` (dos casos adicionales de
ejecución, no contados como pruebas únicas). Compilación de tests y de Client
para net8.0 sin errores ni advertencias. `settings-companion-*-unicode-120x40.png`
son frames del buffer real del driver DOTNET, exportados a SVG y rasterizados;
no son fotografías de una sesión física de terminal.
