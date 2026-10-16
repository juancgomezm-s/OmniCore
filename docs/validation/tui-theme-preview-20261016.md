# Temas: vista previa y catálogo — 2026-10-16

## Problema y reparación

El selector anterior mostraba cuatro nombres y sólo aplicaba la paleta al
guardar. Claude y OpenCode se habían resuelto como dos variantes oscuras con
acentos cálidos, con poca separación visual en el uso práctico.

Ahora el selector aplica temporalmente el tema al recorrerlo con flechas o
seleccionarlo con clic/Enter. La misma pantalla contiene una muestra nativa de
conversación, código y herramientas. Guardar escribe la preferencia; Cancelar,
Esc, F4 y el cierre general del panel restauran el tema anterior. Un error de
escritura se muestra dentro del selector y permite seguir probando o cancelar.
La vista previa no recarga la conversación ni escribe el historial o el borrador.

El catálogo contiene 16 paletas. Claude Light tiene fondo crema y acento
terracota, OpenCode fondo negro y acento durazno; Claude Dark conserva una opción
oscura cálida. Se añadieron OmniLlama/OmniLlama Light, Nord, Dracula, Catppuccin
Mocha/Latte, Tokyo Night, Gruvbox, Solarized Dark/Light y Rosé Pine, además de
OmniCore y Codex. Cambian colores, sin rediseñar la TUI principal.

Los colores OmniLlama se adaptaron de sus tokens propios `omni-dark` y
`omni-light` en `examples/server/webui/src/omnicode.scss`, revisión
`bdb89fb2b893d946e1261ec922107553553409a7` de nuestro repositorio. Ese frontend
también ofrece temas DaisyUI; no se incorpora su framework web a OmniCore.
Las extensiones de Pi sirvieron como referencia de catálogo y previsualización,
sin introducir su runtime. Las paletas se adaptan a los roles de Terminal.Gui.

Se adaptaron también colores de sintaxis y estados para fondos claros, y el
texto de foco se calcula según el relleno de selección. El texto normal y
secundario supera 4.5:1 en las cinco superficies principales de todas las paletas.
Se ajustaron los tonos secundarios de Solarized al detectar dos combinaciones
por debajo de ese valor. `NO_COLOR` se respeta durante la previsualización.

## Validación

- Publicación Release de `OmniCore.Cli` completada.
- **266 pruebas relevantes aprobadas**, 0 fallidas, 0 omitidas: temas,
  preferencias, navegación, presentación y panel lateral.
- **62 pruebas de arquitectura aprobadas**, 0 fallidas, 0 omitidas.
- Los 16 temas se recorrieron con entradas reales del driver en **120×40** y
  **80×25**. Se exportaron sus 32 cuadros de celdas y se inspeccionaron imágenes
  de Claude Light, Claude Dark, OpenCode y Solarized Light. Son capturas del
  driver de pruebas, no capturas de una terminal humana.
- Las pruebas verifican clic antes de guardar, persistencia al guardar, reversión
  con Cancelar/Esc/F4, conservación de conversación/distribución/borrador, fallo
  real de escritura y ausencia de escritura durante la vista previa.
- Se abrió la TUI completa publicada en Windows Terminal, ventana
  **«OmniCore - Temas con vista previa»**, proceso `dotnet` observado. La
  confirmación visual en esa terminal corresponde al usuario. No se inició WSL
  ni se atribuye esta entrega a una ejecución nueva del CI.

Comandos usados desde la raíz del worktree:

```powershell
dotnet test --project tests/OmniCore.Tests --no-restore --filter 'FullyQualifiedName~Theme_|FullyQualifiedName~Preference|FullyQualifiedName~Animation_picker|FullyQualifiedName~Styled|FullyQualifiedName~Sidebar|FullyQualifiedName~Tui_uses' --report-xunit-trx --report-xunit-trx-filename theme-preview-final.trx --no-progress
dotnet test --project tests/OmniCore.ArchitectureTests --no-restore --no-progress
dotnet publish src/OmniCore.Cli/OmniCore.Cli.csproj -c Release --no-restore -o <directorio-de-entrega>
git diff --check
```

El build Debug tuvo un reintento automático de copia del ejecutable; la copia
se completó y la ejecución de las 266 pruebas terminó con código 0. No se
ignoraron errores ni se modificaron gates para conseguir los resultados.

Los logs, TRX y capturas locales quedan bajo
`C:/Users/juanc/.codex/omnicore-linux-20261014/` y `TestResults/` del worktree.

## Referencias revisadas

- [OmniLlama: tokens propios](https://github.com/juancgomezm-s/omni_llama/blob/bdb89fb2b893d946e1261ec922107553553409a7/examples/server/webui/src/omnicode.scss).
- [OpenCode: paleta pública](https://github.com/anomalyco/opencode/blob/dev/packages/tui/src/theme/assets/opencode.json).
- [Catppuccin: paletas](https://github.com/catppuccin/palette/blob/main/palette.json).
- [Pi Theme Switcher](https://github.com/codewithkenzo/pi-theme-switcher).
- [Evaluación independiente de visual-explainer](../visual-explainer-evaluation.md).
