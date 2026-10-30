# M10 Core publicado e instalado: rc.2

Checkpoint histórico. La [rc.3 posterior](m10-startup-installed-20261030.md)
incorpora actualización automática y Consolas 16 px/ClearType; sustituye el estado
de instalación vigente, sin atribuir a la rc.2 esas funciones.

Fuente publicada: `2c9fd13a511050dfc6c2d8a417f7a628c7a81939`.
La candidata aprobó sus cinco trabajos en
[CI 41088077351](https://github.com/juancgomezm-s/OmniCore/actions/runs/41088077351).
La repetición sobre Main también terminó con los cinco trabajos aprobados:
[CI 41096494046](https://github.com/juancgomezm-s/OmniCore/actions/runs/41096494046).
Incluyen tests Windows/Linux, instalación npm en ambos y distribución Linux.
No se instaló Linux en la máquina del usuario.

La [release v0.1.1-rc.2](https://github.com/juancgomezm-s/OmniCore/releases/tag/v0.1.1-rc.2)
es una prerelease publicada, sin cambiar el canal estable. Su
[pipeline 41096502209](https://github.com/juancgomezm-s/OmniCore/actions/runs/41096502209)
terminó correctamente. El cliente verificó paquete y manifest con Sigstore,
repositorio permanente, workflow, tag y SHA exactos, sin credenciales GitHub.

## Comprobaciones sobre el paquete publicado

- SHA-256 del tgz:
  `01df2dd23a8ca27b2452a641d1d964e67b19d37a6eaf21eded76ad48b5cefc52`.
- Check → apply → help → full → rollback → help, en prefijo npm temporal,
  usando la descarga pública y el helper productivo. Ambos cambios terminaron
  `Succeeded`; cuatro canarios de datos permanecieron intactos.
- `full` real por la suscripción ChatGPT ya configurada: 20/20 sobre el paquete.
  Evidencia SHA-256
  `2a696d9f68c6fd747a8e105488fe66d35b7488548fe062935e9e8f092e935f6b`.
  2.111 tokens de entrada, 819 de salida; coste no observado. No se convierte
  una cuota incluida en un precio de cero ni se extrapola capacidad general.
- Instalación personal activada mediante los tres shims existentes; la versión
  anterior permanece disponible para reversión. Auditoría de 119 archivos:
  118 idénticos al tgz y normalización de la primera línea del ejecutable JS
  realizada por npm. La DLL coincide byte por byte con la release.
- Arranque real del comando global en un PTY, F1, Ctrl+P, Ctrl+F1 y `/exit`:
  salida 0, misma terminal y cero pasos de modelo en el journal aislado.

SHA-256 de la DLL ejecutada, instalada y cualificada:
`969ed7a7dd7d30fed4fd078e467cee5a854ce6cf45990204918752b853d39520`.
Preferencias previas y posteriores coinciden: mascota preservada, panel compacto,
Consolas 10,5 puntos (14 px lógicos) y ClearType. No se abrió ni restauró la base
de la mascota durante el instalador o el smoke.

Recibos locales conservados en
`C:\Users\juanc\.codex\artifacts\omnicore-m10-20261030`, con índice y hashes;
los originales siguen en `artifacts/m10-install-20261030`,
`artifacts/m10-live-chatgpt/packaged` y `artifacts/github-updater-20261029`.
La comprobación final de Main está en
`artifacts/m10-final-evidence/main-ci-green.json`.

## Comparación externa y ampliación posterior

Por petición de Juan se buscaron reportes en las copias/documentación disponibles
de OmniCoder y se consultaron las sesiones relacionadas, en solo lectura.
Se encontró `C:\Users\juanc\Documents\JayCoder\docs\benchmark-2026-09-06.md`:
Qwen3.5-4B-Q8 en CPU, cinco tareas FULL/BARE. No es el brazo OmniCoder + Pi de
`omnicore-full` 1.0.0; no se encontró un recibo comparable actual. Se conserva
pendiente la comparación de spec §97, sin inventar resultados ni pedir al usuario
que localice el archivo. Esto no revoca la entrega Core publicada y verificada.

La actualización automática al iniciar se recibió después. Su implementación
pertenece a la rama local `codex/m10-startup-updates` y a su
[plan separado](../plans/m10-startup-updates-20261030.md). No está incluida en esta
rc.2 ni se atribuye a sus CI, release o instalación.
