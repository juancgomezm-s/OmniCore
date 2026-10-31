# M10: actualización automática publicada e instalada, rc.3

Fuente publicada en Main: `49bf57e6d63f13ce5959c9d1337da8d45274edad`.
La [CI 41111893295](https://github.com/juancgomezm-s/OmniCore/actions/runs/41111893295)
terminó con cinco trabajos aprobados. Windows: 6.151 casos, 6.123 aprobados,
28 omitidos, cero fallos; Linux: 6.151 casos, 6.084 aprobados, 67 omitidos,
cero fallos. Las omisiones conservan su condición de plataforma y no cuentan
como recorridos ejecutados. Linux se ejecutó en GitHub, sin instalarlo localmente.

El [pipeline 41113235817](https://github.com/juancgomezm-s/OmniCore/actions/runs/41113235817)
produjo y atestó el paquete y manifest. La
[v0.1.1-rc.3](https://github.com/juancgomezm-s/OmniCore/releases/tag/v0.1.1-rc.3)
se publicó como preview, sin promoverla a estable ni cambiar el canal del usuario.
GitHub CLI fijada verificó firmas de ambos assets contra el repositorio permanente,
workflow, tag y SHA exactos, sin transmitir credenciales al verificador.

| Artefacto | SHA-256 |
|---|---|
| tgz Windows x64, 57.316.378 bytes | `6fcb85a3c2c6c5e4b9956529fa9650c9814689daa6b0ab40099d6f2d47f01418` |
| manifest | `ec20bd55db2010e1664318b0d9b2f7ecffe11b7ecb0a1f23390c8221cf1bab67` |
| DLL CLI instalada | `9ef77db829f37829b85200ecf8decf39c7cb175fed888be258d6babcea15158f` |

## Aceptación pública y arranque

Con la rc.3 firmada en un prefijo desechable, se comprobó descarga automática
en Preview, elección real en la TUI de «En el próximo inicio», persistencia de la
decisión y aplicación antes de abrir la siguiente TUI. La operación
`1983da3e5c7345d786b9a6f0b527dff9` terminó `Succeeded`, conservando cinco canarios
de workspace/configuración/datos y preferencias visuales. Cero pasos de modelo.

El bootstrap del ensayo usó el runtime firmado rc.3 y cambió **sólo su versión
de paquete de prueba a 0.1.0**, para ofrecer un destino público posterior. Esto
acredita el mecanismo nuevo de inicio; no atribuye esa función a una rc.2 antigua
ni publica un paquete con metadatos alterados. El apply/rollback público anterior
conserva su [aceptación separada](m10-installed-preview-20261030.md).

## Instalación personal

Los tres shims existentes apuntan al prefijo versionado `cli/releases/49bf57e6`.
Auditoría: 120 archivos comprobados; 119 idénticos al tgz, salvo la normalización
de la primera línea JS por npm. La DLL coincide con la release. Se conserva el
prefijo anterior para reversión. También se corrigió el fragmento de Windows
Terminal que aún apuntaba a una instalación antigua.

El comando global real arrancó en el mismo PTY: F1, Ctrl+F1, Ctrl+P →
actualizaciones, control de descarga automática visible y `/exit` con código 0.
La prueba usó datos privados de ensayo y registró cero pasos de modelo.

Mascota y preferencias conservadas; su base de datos no se abrió, copió ni
restauró. Panel lateral compacto comprobado. Consolas a **16 px lógicos**
(12 puntos en Windows Terminal), peso 400 y ClearType, configurados en los
perfiles PowerShell predeterminado y OmniCore. `omni` mantiene la terminal actual;
no abre otra ventana para imponer una fuente. No se modificó el checkout Main
con trabajo ajeno ni el worktree de M9.

Recibos, verificaciones, scripts e índice con hashes:
`C:\Users\juanc\.codex\artifacts\omnicore-m10-startup-20261030`.
Originales en `artifacts/m10-startup-publication-20261030/`, especialmente
`ci-green.json`, `installation-audit.json`, `public-startup-receipt.json`,
`global-smoke-receipt.json`, `font-receipt.json` y
`terminal-fragment-receipt.json`. Se conserva evidencia, no credenciales.

Esta acta sustituye las notas anteriores que dejaban la actualización automática,
su CI o instalación pendientes. No extiende el full live 20/20 anterior a la
batería agentic de once categorías, ni acredita el consumidor OmniCoder o Pi.
