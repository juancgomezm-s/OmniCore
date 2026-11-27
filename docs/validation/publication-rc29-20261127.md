# Publicación rc.29: editor del mensaje de sistema

## Alcance autorizado

El 2026-11-27 Juan solicitó integrar el campo de mensaje de sistema en Main y
publicar una actualización para probarlo con `omni update`. Esta publicación
entrega el editor; no anuncia resuelto el rechazo HTTP 429 de Anthropic.

Se creó un corte limpio sobre `origin/main` (`f635472b`) y se integró únicamente
el commit del editor. Main remoto avanzó por fast-forward a
`659fb85fc69ddae01495ee6c4b5ebcb018bdfc63`, origen exacto del tag
`v0.1.1-rc.29`. No se incluyeron las correcciones pendientes del arnés OAuth,
MCP o esfuerzo de la otra rama. El checkout local de Main, con trabajo ajeno
en curso, no se modificó ni se limpió. M9 y otros productos se conservaron.

## Validación del corte exacto

Windows x64; SDK .NET 10.0.403, Node 24.22.1, Python 3.12.10. Configuración y
datos de pruebas aislados. Sin cuentas reales, inferencia pública ni GPU.

- `dotnet build OmniCore.slnx --no-restore --verbosity quiet`: correcto,
  cero advertencias/errores.
- `dotnet test --project tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build
  --no-restore ... --parallel none --output Normal --no-progress`: **98/98**,
  cero fallos/omisiones. Foco: configuración y editor de mensajes, cuatro idiomas,
  tamaños 80×25/120×40, guardado/cancelación/restauración, envío nativo, contexto,
  protocolos de razonamiento, descubrimiento de herramientas y Anthropic.
- `python scripts/validate-local.py --scope quick`: correcto. Resumen en
  `%TEMP%/omnicore-local-validation-_6kyhjk8/summary.json`.
- `git diff --check`: correcto; árbol limpio antes de publicar.

El recuento de este corte es independiente de los 109 casos del corte original,
que incluía cambios de otras correcciones no incorporadas a esta release. No se
acredita la suite completa, reconstrucción Rust local ni otras plataformas.

## Paquete publicado

[OmniCore 0.1.1-rc.29](https://github.com/juancgomezm-s/OmniCore/releases/tag/v0.1.1-rc.29)
se publicó como candidata el 27 de noviembre de 2026 a las 16:18, Ciudad de México.

Sólo se despachó el [flujo manual de empaquetado y firma](https://github.com/juancgomezm-s/OmniCore/actions/runs/46074845760).
Sus dos jobs terminaron correctamente. `ci.yml` conserva exclusivamente
`workflow_dispatch`; no se solicitaron pruebas remotas ni reruns.

- Archivo: `omnicore-cli-0.1.1-rc.29-win-x64.tgz`, **58 042 786 bytes**.
- SHA-256: `cc7ad0551dd657be51deb36e82757978c58b4a30b5a26710d9a36ce0e4a6f5a9`.
- Manifest cotejado contra repositorio, versión, tag, commit, plataforma, nombre,
  tamaño y hash del archivo descargado.
- Firmas de paquete y manifest verificadas con `gh attestation verify`, bundles
  descargados y confianza del verificador, exigiendo repositorio y workflow
  oficiales, source-ref del tag y source-digest/signer-digest del commit anterior;
  se rechazaron firmantes en runners self-hosted.
- Paquete extraído en una carpeta nueva: ayuda de CLI, simulación determinista
  `docs/sim/multi-item-plan.yaml` y `verify-journal --json` aprobados. Resultado:
  una sesión, 58 eventos, cero incidencias. Sin invocar modelos ni herramientas
  de escritorio reales.
- Borrador publicado después de verificarlo, con cinco assets: paquete, manifest,
  sus dos bundles Sigstore y raíz de confianza. Notas en UTF-8 legible.

Evidencia, capturas TXT/SVG, notas y comprobaciones conservadas en
`%TEMP%/omnicore-publication-20261127-rc29/`.

## Detección desde la instalación

La CLI instalada rc.26, con datos/configuración aislados y sin aplicar cambios,
ejecutó `update check --channel preview` y detectó rc.29 con el commit, tamaño y
hash anteriores. Runtime .NET 10.0.14 y Node.js 24.22.1 compatibles.

El comando habitual para instalarla sigue siendo **`omni update`**, sin parámetros
adicionales. La instalación personal queda para la prueba del usuario. No se
reemplazaron mascota, configuración de terminal, fuente/ClearType o panel compacto.

La ruta del editor es **F4 → Cuentas y conexiones → Administrar cuentas Claude →
Configurar cuenta… → Mostrar opciones de conexión**. El texto se añade a las
instrucciones del Host; no modifica la identidad OAuth ni sus permisos.

Esta acta se incorpora después del tag y sólo cambia documentación. El origen
exacto del paquete y sus firmas sigue siendo `659fb85f`.
