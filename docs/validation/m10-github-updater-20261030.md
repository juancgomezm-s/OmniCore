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
| Node: launcher, instalación, sincronización, helper, terminal | 51 | 0 | 1 |
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

## Límite pendiente

No existe todavía una release pública de OmniCore que permita comprobar la cadena
completa de Actions/OIDC, descarga por `omni update` y verificación positiva de su
attestation. Los tests del JSON del certificado sólo validan la política posterior
a GitHub CLI; no prueban una firma real. Faltan la publicación autorizada de la
preview y esa aceptación, y una CI nueva de la combinación local. El canal público
no se declara cerrado. No se actualizaron shims, perfil, preferencias ni instalación
del usuario; mascota, Consolas 14/ClearType y panel compacto permanecen intactos.

Fuentes primarias del verificador: [GitHub CLI verify](https://cli.github.com/manual/gh_attestation_verify)
y [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations).
