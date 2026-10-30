# Canal de actualizaciones por GitHub

Solicitud del usuario del 2026-10-12, incorporada al plan el 2026-10-13.
**Estado 2026-10-30:** consulta, aplicación y rollback Windows x64 implementados
para instalación npm global, con verificación de publicador mediante GitHub CLI
fijada por SHA256 y attestations de paquete/manifest. Empaquetado real, aplicación,
arranque y rollback probados en un prefijo npm temporal. La primera release y su
aceptación completa desde GitHub siguen pendientes; el canal público aún no está cerrado.
Complementa
[arquitectura §24](../architecture/arquitectura.md) y el
[plan M8–M10](m8-m10-user-localization.md). La instalación npm desde Git ya
existe; los contratos del actualizador ya están cableados y falta activar el canal
publicado. [Evidencia del incremento](../validation/m10-github-updater-20261030.md).

**Instalación manual actualizada (2026-10-15):** `scripts/install.ps1` y
`scripts/install.sh` compilan el checkout actual, verifican el arranque y entregan
a npm un paquete preparado. Repetirlos actualiza la instalación; Linux usa
`~/.local` por defecto. Uso y requisitos en el README;
[aceptación Windows/Debian y CI Windows/Ubuntu](../validation/npm-install-update-20261015.md).
Este recorrido manual
no implementa el servicio de releases, comprobación ni rollback de M10.

**Revisión del 2026-10-22:** el recorrido manual añade descarga explícita desde
Main mediante `scripts/install.ps1 -FromGitHub` o
`bash scripts/install.sh --from-github`. Requiere un clon limpio del repositorio
oficial en Main; rechaza cambios sin commit, archivos sin seguimiento, commits
locales sin publicar y divergencias. Fija el SHA descargado, sólo avanza por
fast-forward y corta antes de instalar si la consulta falla. El instalador sin
esta opción conserva la instalación del checkout actual. No equivale a un
actualizador de Releases ni habilita `omni update`; ese alcance sigue en M10.

## Distribución y canales

Usar GitHub Releases del repositorio `juancgomezm-s/OmniCore` como origen de
versiones publicadas. `stable` es el canal predeterminado, con versiones SemVer
y releases publicadas sin marca prerelease; `preview` requiere selección
explícita y admite prereleases. No tomar `main` ni un draft como actualización
estable. Conservar versión, tag, commit, plataforma y procedencia instalados.

El flujo actual `npm install ...#main` continúa como instalación de desarrollo;
una instalación reproducible fija el SHA. El canal de release entregará un tgz
preparado, con CLI y dependencias publicadas, para evitar compilar en el equipo
destino. Windows x64 es la primera plataforma de aceptación. La distribución
framework-dependent requiere Node y runtime .NET 10; los requisitos exactos
quedan en el manifest. ARM64, Linux y distribución self-contained requieren
su propio alcance y evidencia antes de ofrecerse.

Cada release incorpora notas, manifest versionado, tamaños, SHA256 por asset,
tag/commit y requisitos de runtime/configuración/protocolo. El pipeline de
GitHub Actions construye y prueba el árbol etiquetado, genera los assets y su
procedencia, prepara un draft completo y publica la release verificada.
Contemplar releases inmutables y verificación de attestations del repositorio
y workflow autorizado. Un checksum comprueba integridad, por sí solo no
acredita identidad del publicador. El consumidor debe verificar esa identidad
además del digest y rechazar assets que no correspondan a la release/plataforma.

## Recorrido de usuario

Contratos implementados: `omni update check`, `omni update apply [version]`,
`omni update rollback` y `omni update status`. La vista de preferencias sólo
ofrece consulta de metadatos; aplicar y revertir se hacen desde la CLI. No son
herramientas invocables por el modelo.

La consulta es manual por defecto. Mostrar versión instalada/disponible,
canal, notas, tamaño, compatibilidad y procedencia antes de descargar/aplicar.
Abrir preferencias no consulta GitHub ni inicia instalación. Las comprobaciones
automáticas, si se incorporan, requieren preferencia explícita, intervalo y
cancelación; ninguna consulta aplica o reinicia por sí sola.

La descarga usa HTTPS con verificación de certificados, timeouts, límites y
destinos/redirects restringidos a los endpoints de assets autorizados. Diferenciar
sin actualización, offline, rate limit, release incompatible y fallo de
verificación. No utilizar credenciales de modelos para consultar releases.

## Aplicación y recuperación

Reconocer el canal y propietario de instalación: npm global, checkout de
desarrollo o paquete de release. La actualización npm usa el gestor npm sobre
un asset/version fijados; no reemplaza manualmente archivos administrados por
npm. Un checkout de desarrollo conserva instrucciones explícitas y no hace
pull/merge automático sobre cambios del usuario.

Separar binarios de cuentas, preferencias, journals, CAS y workspaces. Preparar
una nueva versión en staging y validar hash, identidad y compatibilidad antes
de activar. Detener/reanudar de forma coordinada el Host cuando sea necesario:
no sustituir DLLs en uso ni perder Runs/Ask activos. Aplicar mediante helper o
gestor externo, con estado durable de operación y versión previa recuperable.
No elevar privilegios silenciosamente ni repetir una aplicación cuyo efecto
quedó incierto después de un crash.

Rollback explícito conserva datos del usuario. Si existe migración de esquema,
comprobar compatibilidad hacia atrás y backup antes de aplicar; una migración
irreversible bloquea rollback con explicación, no restaura datos antiguos sobre
actividad posterior. En M9 preparar identificación/versiones del Host y clientes;
la construcción del updater permanece en M10.

## Aceptación M10

- Consulta/selección stable-preview y comparación de versiones mediante fixtures,
  incluidos prerelease, draft, offline, rate limit y plataformas incompatibles.
- Descarga alterada, digest/identidad incorrectos o redirect fuera del destino
  permitido se rechazan antes de tocar la instalación activa.
- Aplicación y arranque real en Windows sobre instalación npm/release; cancelación,
  DLL ocupada, crash antes/después de activación y recuperación idempotente.
- Rollback y preservación de cuentas, settings, sesión/journal/CAS y workspace;
  no se contabilizan fixtures como campañas autenticadas de proveedores.
- Estados, confirmaciones, errores, ayuda y accesibilidad es/en/fr/it mediante
  los contratos existentes. IDs, versiones, URLs y hashes permanecen invariantes.

**Estado de aceptación (2026-10-30):** los fixtures cubren consulta stable/preview,
digest/tamaño/identidad, restricciones de redirección, backup verificado, estado
durable, confirmación, rechazo con DLL ocupada, identidad del verificador y
exclusión con PID npm activo. Se aplicó y revirtió la CLI .NET real mediante npm
en un prefijo temporal y se conservó la instalación activa. La verificación de
una attestation real del pipeline de OmniCore y la aceptación de descarga desde
una release publicada siguen pendientes. Los fixtures de certificado no sustituyen
esa prueba criptográfica. Ver el acta para los cortes de proceso y sus límites.

Fuente primaria del mecanismo de publicación:
[GitHub Releases API](https://docs.github.com/en/rest/releases/releases) y
[releases inmutables](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).
Estos servicios soportan la propuesta; no acreditan que el updater esté construido.
