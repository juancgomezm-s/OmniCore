# Instalación y actualización manual — 2026-10-15

Se añade un recorrido común para Windows y Linux sobre el checkout elegido:
`scripts/install.ps1` / `scripts/install.sh` → `npm run install:cli`.
Linux instala en `~/.local` por defecto; Windows conserva el prefijo global de
npm. Ambos admiten un prefijo explícito, incluidos espacios en la ruta.

El instalador compila explícitamente mediante el `prepare` existente, comprueba
`omni.dll --help`, prepara un `.tgz` y sólo entonces pide a npm que instale o
reemplace el paquete. `--ignore-scripts` se aplica al empaquetado/instalación del
payload ya compilado, sin omitir su compilación. No descarga cambios del checkout,
eleva privilegios ni modifica configuración o datos de OmniCore. El archivo se
conserva para reinstalación manual. No implementa el actualizador de releases M10.

## Evidencia local

| Comprobación | Windows x64 | Debian 13.5 / WSL2 x64 |
|---|---|---|
| Entorno | Node 24.19.0, npm 11.17.0, SDK 10.0.401 | Node 22.23.3, npm 10.9.9, SDK 10.0.401 |
| Pruebas Node: instalador y lanzador | 16/16 PASS, sin omisiones | 16/16 PASS, sin omisiones |
| Publicación Release y paquete npm real | PASS | PASS |
| Comando instalado y ayuda | PASS (`omni.cmd`) | PASS (`bin/omni`) |
| Simulación offline, persistencia y verificación del journal | PASS | PASS |
| Reinstalación sobre el mismo prefijo | PASS | PASS |
| SHA256 de configuración de prueba, archivo de workspace y journal conservados | PASS | PASS |

`scripts/smoke-npm-install.py` comprueba la DLL instalada contra la recién
compilada, ejecuta la CLI instalada con Node real, completa `multi-item-plan`,
verifica el journal y conserva hashes antes/después de actualizar. Los procesos
usan carpetas de evidencia propias, sin cuentas ni inferencias de proveedores.
Las pruebas unitarias ejercitan además fallos de publicación, arranque,
empaquetado e instalación: se propaga el error y no se ejecuta el siguiente paso.

Windows PowerShell 5.1 inicialmente abortaba al redirigir los avisos de npm a un
log con `ErrorActionPreference=Stop`. Se corrige la invocación nativa para
conservar stderr y comprobar el código de salida real; la instalación y la
actualización se repitieron correctamente con salida redirigida.

Logs locales: `C:/Users/juanc/.codex/omnicore-linux-20261014/npm-*.log`;
evidencia Debian: `/home/omnicode-ci/omnicore-linux-evidence/npm-install-update-20261015`.
La ruta con espacios se ejercitó realmente en ambos sistemas.

## CI y alcance

El nuevo job `npm-install` ejecuta las pruebas Node, instalación real, simulación
y actualización con conservación de estado en Windows y Ubuntu. No se elimina
ninguna comprobación ni test existente. En el commit `303fe7b`, ejecución real
[38769064544](https://github.com/juancgomezm-s/OmniCore/actions/runs/38769064544):

| Job | Resultado remoto |
|---|---|
| [npm-install (windows-latest)](https://github.com/juancgomezm-s/OmniCore/actions/runs/38769064544/job/116539812859) | PASS; 16 pruebas Node, instalación, simulación/journal, actualización y estado conservado |
| [npm-install (ubuntu-latest)](https://github.com/juancgomezm-s/OmniCore/actions/runs/38769064544/job/116539812868) | PASS; mismas comprobaciones reales |
| [linux-package](https://github.com/juancgomezm-s/OmniCore/actions/runs/38769064544/job/116539812710) | PASS; gate existente self-contained y Native AOT conservado |

También se actualizó la instalación global existente de Windows y se instaló
la de Debian en `~/.local`. Ambas DLL coinciden con su build actual y la ayuda
funciona por el comando instalado. Se añadió el SDK ya existente de `~/.dotnet`
al PATH del usuario de Debian y se comprobó desde una nueva shell de login.
Una nueva ventana real de Windows Terminal ejecuta la instalación Linux
(`~/.local/lib/node_modules/omnicore-cli/dist/npm/omni.dll`), con su propia
configuración de vista previa; la ventana anterior se conserva.

Los fallos de Core ya identificados en `preferences-integration-20261014.md`
siguen fuera de esta corrección. El
[CI previo de Main, Ubuntu](https://github.com/juancgomezm-s/OmniCore/actions/runs/38767223307/job/116533681326)
confirma exactamente esos 16 fallos y salida 2; ninguna de las 14 regresiones
de UI anteriores reaparece. **No se declara el CI global verde.** Tampoco se
acreditan ARM64, macOS, proveedores autenticados ni el servicio M10 de releases.
