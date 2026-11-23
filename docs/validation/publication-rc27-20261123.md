# Publicación rc.27 · 2026-11-23

Publicado: [OmniCore 0.1.1-rc.27](https://github.com/juancgomezm-s/OmniCore/releases/tag/v0.1.1-rc.27).
Actualización del usuario: `omni update` o `omni -update`, sin parámetros adicionales.

## Código y paquete

- Correcciones MCP y alta de cuenta Claude: `f328a1cc`, integradas con Main rc.26
  `95a23ce1` mediante `f85de0020330c7255c62f5f41d9767bf24032868`.
- Tag inmutable `v0.1.1-rc.27`, origen exacto del paquete:
  `f85de0020330c7255c62f5f41d9767bf24032868`.
- [Construcción y firma](https://github.com/juancgomezm-s/OmniCore/actions/runs/45213871531):
  ambos jobs aprobados. Se ejecutó exclusivamente el workflow de release;
  `ci.yml` conserva su activación manual y no se despachó.
- Archivo `omnicore-cli-0.1.1-rc.27-win-x64.tgz`, 58 037 127 bytes,
  SHA-256 `b2fbeee79e67ce5739320bef68662aa2af5169182adc52656e7eb469231d4db1`.
- Paquete y manifest descargados y verificados con `gh attestation verify`,
  exigiendo repositorio, workflow, `refs/tags/v0.1.1-rc.27` y SHA de origen.
  Arranque de `package/bin/omni.js help` aprobado con datos/configuración aislados.

El ajuste posterior de `TuiWiringTests.MenuAudit.cs` actualiza la expectativa del
antiguo informe MCP al menú configurable y simula el fallo en su nueva fachada
Host. Es un cambio exclusivamente de pruebas; no modifica los archivos que
integran el paquete firmado. El tag y sus firmas conservan su origen exacto.

## Validación local de la integración

Windows x64, SDK .NET 10.0.403. Se aislaron `OMNICORE_DATA_DIR` y
`OMNICORE_CONFIG_DIR`; los escenarios usan fixtures, sin inferencia ni GPUs.

- `python scripts/validate-local.py --scope full`: npm, pruebas Python, SDK,
  solución y ejecutor de evaluación aprobados. El primer intento necesitó
  restaurar los assets eliminados de `tools/evaluation/CoreDriver`; después
  se ejecutó la suite amplia sobre la integración.
- Suite amplia: **6 423 aprobadas, 12 fallos, 40 omisiones**, de 6 475 casos.
  Su resultado original permanece fallido, sin declararlo retrospectivamente verde.
- Nueve fallos procedían de expectativas del informe MCP anterior: un caso de
  lectura fallida y ocho combinaciones de idioma/tamaño de la auditoría de menús.
  Se actualizaron conservando las comprobaciones de retorno y ausencia de efectos.
- Tres fallos ocurrieron al respaldar SQLite en fixtures de caída/recovery de M9.
  Se repitieron con un alias temporal `R:` del mismo checkout y compilación de
  pruebas con `-p:PathMap=<checkout>=R:\`, sin modificar código M9.
- Repetición dirigida de esos respaldos y la auditoría de menús actualizada:
  **15 aprobados, cero fallos y omisiones**. Incluye cuatro casos de lectura
  fallida, ocho recorridos completos de preferencias y tres escenarios M9.
- Foco independiente sobre el origen del paquete: **427 aprobados, cero fallos
  y omisiones**, cubriendo MCP, OAuth Claude, alta TUI, Enter/F7 y la integración
  con respuestas finales durables de rc.26.
- Compilación de pruebas restaurada después con `-p:PathMap=` y el alias `R:`
  retirado. No se alteraron permisos, otros checkouts ni procesos del usuario.
- `git diff --check`: aprobado. Las omisiones no acreditan otras plataformas.

Evidencias locales preservadas:

- `C:/Users/juanc/AppData/Local/Temp/omnicore-local-validation-qiz6q2r2/summary.json`.
- `C:/Users/juanc/AppData/Local/Temp/omni-release-focus-608e224fd4c3421da093beac0c494007/tests.log`.
- `C:/Users/juanc/AppData/Local/Temp/omnicore-publication-20261123/directed-retests.log`.
- `C:/Users/juanc/AppData/Local/Temp/omnicore-publication-20261123/rc27-assets/`:
  paquete, manifest, bundles y resultados de verificación.

## Comprobación desde la instalación existente

El launcher instalado rc.26 ejecutó `update check --channel preview` con resultado
correcto: disponible rc.27, manifest y SHA esperados, requisitos compatibles
(.NET Runtime 10.0.14, Node.js 24.22.1, win-x64). Notas publicadas en UTF-8 legible.
Log: `C:/Users/juanc/AppData/Local/Temp/omnicore-publication-20261123/installed-update-check.log`.

No se aplicó la actualización a la instalación personal: queda lista para que
Juan pruebe el comando sencillo. Se conservan mascota, preferencias de terminal,
panel compacto y configuración personal.
