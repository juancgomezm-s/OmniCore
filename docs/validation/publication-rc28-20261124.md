# Publicación rc.28 · 2026-11-24

## Alcance y origen

Juan autorizó revisar las correcciones y publicar la actualización si no quedaban
bloqueantes. Se revisaron las correcciones de nombres MCP (`cb436bec`) y catálogo
OAuth Claude (`1f23ad95`), sin modificar otros productos o el checkout con trabajo
M9. `origin/main` no contenía cambios adicionales al recuperar su estado.

Main se actualizó por avance directo desde `483562f9` hasta
`1f23ad95ec7f0b85e96351ceb4572fac0854bf15`. El tag inmutable
`v0.1.1-rc.28` apunta a ese origen exacto.

Se despachó únicamente el [workflow de empaquetado y firma de la release](https://github.com/juancgomezm-s/OmniCore/actions/runs/45434376017).
`ci.yml` mantiene su activación exclusivamente manual: no se solicitaron pruebas
remotas, reruns ni cambios de triggers.

## Revisión local

Windows x64, SDK .NET 10.0.403. Directorios de datos/configuración aislados;
fixtures de cuentas, catálogos y servidor MCP. Sin cuentas o servidores reales,
API de pago ni GPU.

- Compilación de la revisión exacta y validador rápido aprobados en
  [la revisión Claude](claude-account-model-catalog-20261124.md).
- Revisión conjunta previa a publicar: **210 aprobadas, cero fallos y omisiones**.
  Comando: `dotnet test --project tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~TuiClaudeAccountHostTests|FullyQualifiedName~TuiWiringTests.Claude_|FullyQualifiedName~Anthropic|FullyQualifiedName~ProviderConnection|FullyQualifiedName~Mcp|FullyQualifiedName~Every_preference_category_has_distinct_actions_and_returns_from_its_pages|FullyQualifiedName~A_failed_menu_read_keeps_an_error_page_and_returns_to_its_category' --parallel none --output Normal --no-progress`.
- Log: `%TEMP%/omni-rc28-review-c6e8177f92ce4e9a9935ee42dc0ef6cd/tests.log`;
  capturas TXT/SVG de la TUI en su subcarpeta `snapshots/`.
- `git diff --check`: aprobado; árbol limpio antes de publicar.

La cobertura incluye cuenta previamente conectada, actualización automática tras
login, F3 y mantenimiento de visibilidad en el mismo proceso, cuatro idiomas,
conservación de límites/preferencias, cancelación y conflictos, nombre `RAG local`,
formulario MCP y ejecución de herramienta de fixture pasando por permisos.
No acredita suite completa, inferencia con la suscripción concreta de Juan ni
otras plataformas.

## Entrega publicada y verificada

Publicada [OmniCore 0.1.1-rc.28](https://github.com/juancgomezm-s/OmniCore/releases/tag/v0.1.1-rc.28)
como candidata a las 10:20 del 24 de noviembre de 2026 (Ciudad de México).
Los dos jobs de empaquetado/firma y creación del borrador terminaron aprobados.

- Paquete: `omnicore-cli-0.1.1-rc.28-win-x64.tgz`, **58 039 040 bytes**.
- SHA-256: `cfa440b15707f5925da36dbc4cb5b6cf0d1bf03d1cd36b4f2a2bd077be1ed584`.
- Manifest validado contra repositorio, versión, tag, commit, plataforma, nombre,
  tamaño y hash del archivo descargado.
- Attestations del paquete y manifest verificadas mediante `gh attestation verify`
  exigiendo `juancgomezm-s/OmniCore`, workflow `.github/workflows/release.yml`,
  `refs/tags/v0.1.1-rc.28` y digest de origen
  `1f23ad95ec7f0b85e96351ceb4572fac0854bf15`.
- Paquete extraído en un directorio nuevo: `node package/bin/omni.js --help`,
  simulación determinista `multi-item-plan.yaml` y `verify-journal --json`
  aprobados con datos/configuración propios. Journal: una sesión, 58 eventos,
  cero incidencias. Sin invocar modelos ni GPU.
- Release publicada después de estas comprobaciones; contiene paquete, manifest,
  dos bundles Sigstore y raíz de confianza. Notas en UTF-8 legible.

Evidencia descargada, resultados de verificación, logs de smoke y notas:
`%TEMP%/omnicore-publication-20261124-rc28/`.

## Detección desde la instalación

El launcher instalado rc.26, con datos/configuración aislados, ejecutó
`update check --channel preview` y detectó **rc.28** con el commit, hash y tamaño
anteriores. Requisitos compatibles: .NET Runtime 10.0.14 y Node.js 24.22.1.
Log: `%TEMP%/omnicore-publication-20261124-rc28/installed-update-check.log`.

No se aplicó la actualización personal: queda disponible para que Juan ejecute
`omni update`. Tras actualizar, una cuenta Claude ya conectada puede recuperar
sus modelos con **F4 → Cuentas y conexiones → Cuenta Claude → Actualizar modelos**.
El cambio no reemplaza mascota, configuración de terminal, fuente/ClearType ni
panel compacto.

Esta acta se añade después del tag y sólo cambia documentación. El origen exacto
del paquete y sus firmas permanece inmutable.
