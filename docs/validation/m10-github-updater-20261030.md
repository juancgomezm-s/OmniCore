# M10 — actualizador Windows desde GitHub

Fecha: 2026-10-30, America/Mexico_City. Alcance: OmniCore Core/CLI/TUI.
Un solo worker Luna alto construyó helper/pipeline; root revisó y corrigió la
entrega, compiló y ejecutó las aceptaciones. No se modificó OmniCoder.

## Implementación

El updater conserva `check/apply/rollback/status` y la consulta pasiva de TUI.
`apply` verifica tamaño, SHA256 e identidad npm; luego exige attestations de
paquete y manifest antes de preparar backup o lanzar un helper. El verificador
oficial GitHub CLI 2.103.3 se distribuye dentro del paquete y su ejecutable debe
coincidir con el SHA256 compilado. No se instala globalmente ni recibe tokens
GitHub o credenciales de proveedores del proceso padre. La política exige ID
permanente del repositorio `1386758159`, workflow `release.yml`, tag, SHA de
fuente y de firmante, procedencia SLSA y runner hospedado. El manifest verificado
vuelve a comprobar los requisitos que se mostraron al usuario. El ejecutable
verificado permanece abierto sin permiso de escritura/borrado mientras se usa.

Pipeline Windows por tag y SHA exactos contenidos en Main: compila, prueba,
empaqueta sin lifecycle scripts, genera attestations con GitHub OIDC y prepara
un draft. Las Actions tienen SHA completo fijado. No publica automáticamente.
Se corrigieron en revisión sintaxis PowerShell, ruta interna del ZIP oficial,
licencia tomada del ZIP verificado y un SHA incompleto de setup-node.

En Windows npm se ejecuta dentro del PID durable registrado, tras el gate; no
se usa el PID de un supervisor para representar un nieto independiente. El helper
guarda ese PID y el estado antes de autorizar el primer efecto. Una
ventana anterior al gate termina sin iniciar npm. Un PID vivo/indeterminado
impide concurrencia; una operación incierta no se reintenta automáticamente.
Después de npm se comprueban identidad/version exactas y arranque `--help` en
carpetas data/config temporales. Rollback explícito exige huella del backup,
mismo prefijo y ausencia de helper/npm activos; sólo archiva un lock abandonado
que coincide con la operación durable. Backups alterados o con enlaces se rechazan.
La generación actual de configuración es 1.0; no se implementaron migraciones
ni se acredita rollback a través de una migración irreversible futura.

## Resultados

| Comprobación | Aprobados | Fallos | Omitidos |
|---|---:|---:|---:|
| Node: launcher, instalación, sincronización, helper, terminal | 55 | 0 | 1 |
| C#: updater, downloader, manifest, CLI y política de publicador | 64 | 0 | 1 |
| Regresión de memoria scoped/Session y frontera de EventStream | 97 | 0 | 4 |
| Arquitectura | 62 | 0 | 0 |

Las omisiones requieren crear symlinks y Windows no lo permitió; no se elevaron
privilegios. Compilaciones finales: cero errores y advertencias. El escáner de
producción confundía el nuevo auxiliar `OmniCore.MemoryCrashTestProcess` con
producción: se excluyó ese ejecutable de tests, conservando la inspección IL de
todos los ensamblados productivos y sus controles positivos.

Se ejecutó `prepare-github-release.ps1` desde una copia Git limpia de la candidata
`e775b13b021ff06e895871db6502288ed4a7f424` (M9 integrado), no desde archivos ajenos
sin commit. Versión local de ensayo `0.1.1-rc.1`, asset Windows x64 de 57,085,970 bytes,
SHA256 `9c2e1084727f0f5f418ed825dce1dead57f5e06b5643e23da7efd8f8ba98ace2`.
La copia del manifest conserva tag y commit exactos. Este tag no se creó ni publicó.

La CLI .NET real, no un launcher ficticio, se instaló con npm en un prefijo temporal
con espacios y `&`; se aplicó 0.1.1-rc.1 y se revirtió a la copia de la instalación
0.1.0. Ambos estados terminaron `Succeeded` y `--help` arrancó. Cuatro archivos
de control data/config/CAS/workspace conservaron sus bytes y la huella de la
instalación activa del usuario se mantuvo. Los controles son archivos del fixture;
no constituyen una sesión con proveedores ni una migración de datos reales.
El fixture Windows de DLL ocupada usa un proceso PowerShell propio con FileShare.Read
sobre una DLL del paquete: rechazo antes de npm, sin cerrar procesos del usuario.

Evidencia local: `artifacts/github-updater-20261029/` contiene logs/XML de tests,
`candidate.json`, `prepare-final.log`, `real-cli-receipt.json` y scripts de aceptación.

## Caída real y recuperación fuera del paquete

Se interrumpieron únicamente procesos del fixture. El corte anterior al gate
conservó el paquete completo idéntico al backup aunque el estado ya decía Running.
El corte durante npm dejó la CLI sin package.json: no podía arrancar para pedir
rollback. Ese ensayo detectó una carencia de la entrega anterior.

La corrección copia el helper a `updates/recovery/<OperationId>/`, fuera del
prefijo npm, y conserva un comando PowerShell en el estado. Se muestra antes de
aplicar/revertir y en `status`. `--mode recover --yes true` exige identidad,
versión y huella exactas del backup, prefijo coincidente y PID parent/helper/npm
ausentes o muertos. Sólo prepara una reversión explícita con npm offline.
Nunca repite el apply incierto. Funciona con el paquete activo incompleto o ausente.

La exclusión con Host y otras recuperaciones usa el mismo `state.lock` con
`FileShare.None`, mantenido por un controller PowerShell oculto. El path se pasa
por entorno sin interpolarlo en código; no instala software. Hay handshake con
plazo y cierre por EOF. Los tests comprueban el lock del Host y dos recover
simultáneos con una sola reversión y sin sobrescribir la operación activa.

Se restauró la misma instalación temporal rota: Running → Rollback/Succeeded,
versión 0.1.0 y `--help` de la CLI .NET real. Se repitió el corte durante npm
con el helper y el mutex definitivos, con el mismo resultado. Los cuatro archivos
de control conservaron sus bytes y sobrevivió el helper externo. Su SHA256 fue
`c6e5f7cb0ee49ea1673ab045085eb1ba6acca904b0bb6a7bb06afe364c3890d4`.
No se acredita pérdida de alimentación del sistema ni se cerraron instancias del usuario.

Evidencia adicional: `gate-crash-receipt.json`, `crash-recovery-receipt.json`,
`crash-recovery-final-receipt.json`, `npm-final-mutex.log` y
`recovery-focal-final.xml` (68 aprobados y una omisión: incluye los 64 updater y
cuatro controles IL que también están en la regresión de memoria de la tabla).
La compilación correspondiente terminó sin errores ni advertencias.

El paquete local de ensayo descrito arriba precede al refuerzo de recuperación.
El refuerzo tiene compilación, pruebas de Host/CLI y aceptación directa del helper
sobre la CLI .NET rota. La release se reconstruirá desde su tag/SHA exactos;
no se reutilizará el archivo anterior como si contuviera estos cambios posteriores.

## Aceptación pública posterior

La base `04d226b9f386ed94e898d5f0ff8f325402977bba` aprobó los cinco jobs de
[CI 41054565261](https://github.com/juancgomezm-s/OmniCore/actions/runs/41054565261).
[Actions 41054838618](https://github.com/juancgomezm-s/OmniCore/actions/runs/41054838618)
construyó/atestó paquete y manifest para `v0.1.1-rc.1`. La release se publicó como
preview. El gh fijado verificó firmas reales sin credenciales, contra repositorio,
workflow, tag y commit exactos; el certificado identifica el repositorio permanente
`1386758159`. No se atribuye esa prueba a un certificado simulado.

La descarga real detectó un defecto: checker/downloader rechazaban los parámetros
firmados del CDN de GitHub. Se corrigió para aceptar queries sólo en los CDN de la
allowlist, manteniendo HTTPS, puerto, ausencia de userinfo/fragmento y validación
posterior de hash/publicador. Se añadieron negativos/positivos. La preview rc.1
precede a esa corrección; el ensayo siguiente parte de un bootstrap local corregido.

Un paquete local 0.1.0 construido desde `79cee0739fc9a413f5455e74141df0226e816eed`,
nunca ofrecido como release, se instaló en un prefijo npm temporal. SHA-256:
`393d86d4f0dacf42474f2407f6e4484178b8f2a17800a06f49698ed3764144e8`.
Desde su CLI productiva se ejecutaron check preview, apply 0.1.1-rc.1 y rollback:

- `Apply/Succeeded`, `update_applied`; CLI .NET real inicia con `--help`.
- `Rollback/Succeeded`, `rollback_applied`; versión instalada de vuelta en 0.1.0.
- Paquete público verificado: 57.085.575 bytes, SHA-256
  `7559cb66308b4df2fad84cf145c4122cea34e084ca2d5aa559308f1348eda57e`.
- Journal, CAS, configuración y archivo de workspace de control conservaron bytes.
  Los datos y la instalación personal del usuario no se tocaron.

Recibo: `artifacts/github-updater-20261029/public-roundtrip-receipt.json`;
directorio temporal y logs check/apply/rollback/help quedan referenciados ahí.
Esto acredita el recorrido público desde el bootstrap corregido hacia la preview
firmada y su reversión. La release final que incluye la corrección CDN y el corte
M10 requiere su propia CI/tag; su publicación e instalación se registran aparte.
No se declara estable Main ni se acredita actualización del usuario con este ensayo.

Fuentes primarias del verificador: [GitHub CLI verify](https://cli.github.com/manual/gh_attestation_verify)
y [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations).
