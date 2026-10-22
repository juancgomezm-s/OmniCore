# Extensiones: aislamiento obligatorio — 2026-10-22

Validación previa a la integración en `codex/extensions-platform`. Cierre anterior
conservado en `78808c1`; aislamiento guardado posteriormente en `9018e62`.
Un solo agente, sólo OmniCore. El usuario autorizó después integrar/publicar:
`main` conserva los merges `da2280d`, `3ebcd14` y `3a864e5`, incluyendo los tips
`61c0cc6` de publicación M9 y `74a827c` de TUI/recuperación. Todos los demás
worktrees estaban limpios y sus commits ya estaban contenidos en main; no se
eliminaron ramas ni worktrees. Las cifras siguientes son locales, no resultados
de CI remoto. La verificación del run del SHA publicado se entrega por separado.

## Resultado

Todo ejecutable v2 pasa del supervisor Host al adapter `IsolatedExtensionProcess`
en Execution y a Sandbox, sin nueva dependencia Host → Sandbox. Ya no existe el
arranque libre de extensiones mediante `Process.Start` en el supervisor. La
verificación de versión de Node sigue ejecutando sólo una expresión fija del
runtime elegido por el usuario, sin cargar código del paquete.

Windows: LPAC + Job Object, stdio retenido, sólo tres handles heredados, red sin
capacidad, hijos prohibidos y limpieza de ACL. `registryRead` permite inicializar
Winsock; no otorga autoridad general sobre HKCU ni escritura del registro. Node
evita `realpath` sobre ancestros no concedidos, sin añadir ACEs al disco raíz.

Linux: Bubblewrap de `/usr/bin/bwrap`, namespaces de usuario, montaje, PID y red,
sesión propia, capacidades eliminadas, creación de nuevos user namespaces
deshabilitada, entorno vacío, `/proc` y `/dev` privados, `/tmp` temporal. Sólo se
montan los árboles de runtime de sistema en lectura, la imagen de lectura y los
datos privados de escritura. El runtime se copia incluso cuando Node es un enlace
a una instalación bajo `/opt`. Un error nunca habilita ejecución Weak/None.

Los paquetes declarativos no arrancan procesos. `trusted` y `--trust-process`
se aceptan únicamente por compatibilidad. `IsolationPolicyVersion = 1` exige
reactivar concesiones antiguas; no se elevan permisos automáticamente.

Preferencias → Extensiones → Paquetes y vistas muestra protección declarativa,
requerida, activa o bloqueada, con traducciones es/en/fr/it. Contexto mantiene su
vista previa y aprobación humana exacta independiente de habilitar el paquete.

## Evidencia ejecutada

| Comprobación | Windows | Debian/WSL |
| --- | --- | --- |
| Extensiones + sandbox Windows + stdio (suite amplia) | 153: 152 pasan, 1 omitida | 153: 145 pasan, 8 omitidas |
| Focal final de aislamiento, cancelación y política sin fallback | 5 pasan | 5 pasan |
| Preferencias: protección visible y habilitación humana del piloto | 1 pasa | cubierta anteriormente; no repetida tras nueva aserción visual |
| SDK TypeScript/JSON Schema | 44 pasan | no repetida; no se cambió código TS |

Son ejecuciones separadas, con casos solapados: no sumar como cobertura distinta.
La suite amplia fue anterior a los dos casos focales finales y al endurecimiento
del modo `0700` del directorio privado Linux. La focal final comprueba esa versión.
También pasó la focal de TUI/contexto/vistas (69 casos, una omisión Windows) durante
la integración, incluida vista previa, aprobación, revocación y borrador intacto.

Compilación de Host/Tests en ambas plataformas sin warnings ni errores. Sandbox y
Protocol multi-target `net8.0`/`net10.0` compilados en Windows sin errores. La prueba
no-Node negocia el protocolo y sirve una vista con Python real bajo Bubblewrap en
Debian; su shebang es la única omisión de la suite amplia Windows. Las omisiones
Debian son los siete casos AppContainer y la extensión Strong v1 exclusiva Windows.

La prueba adversarial real confirma lectura de secreto fixture rechazada, escritura
de Core/paquete/workspace rechazada, escritura/lectura privada permitida, entorno
envenenado no heredado, conexión TCP al listener host bloqueada y escape por hijo
rechazado. En Windows el secreto tiene deliberadamente una ACE de lectura para
`ALL APPLICATION PACKAGES`: LPAC sigue denegándolo. No se usan secretos reales.
La suite conserva además la autorización de contexto, los hooks sin autoridad para
reescribir instrucciones, los avisos sólo de UI y los pilotos headless por stdio.

Verificación adicional Linux: la misma política de namespaces y mounts arranca
Node `v22.23.3` como `nobody` (UID 65534), no sólo como root de WSL. La suite C#
completa Debian se ejecutó como root; no se presenta esta prueba de arranque como
una regresión integral bajo otro usuario.

Reportes locales XML:

- Windows: `%TEMP%/omni-extension-isolation-windows.xml`.
- Windows final: `%TEMP%/omni-extension-isolation-refinements-windows.xml` y
  `%TEMP%/omni-extension-isolation-ui-windows.xml`.
- Debian: `/root/.cache/omni-extension-isolation-Ac4Efm/isolation-linux.xml` y
  `isolation-linux-refinements.xml` en el mismo directorio.

Comandos para repetir desde el repositorio (PowerShell; `/m:2` limita el build):

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore '/m:2' '/nr:false'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -class '*Extension*' -class '*WindowsAppContainerSandboxTests' -class '*StdioTransportTests' -result-xml isolation.xml
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -class '*ExtensionIsolationTests'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method '*Extension_packages_preferences*'
```

SDK: `npm test` desde `sdk/extensions`. En Linux hacen falta Node LTS ≥22,
Bubblewrap con `--disable-userns` y un kernel que permita los namespaces usados.
Si no están disponibles se bloquea el proceso; no hay instalación ni fallback
automáticos. Se usaron .NET SDK 10.0.401 y Node 24.19.0 Windows / 22.23.3 Debian.

## Límites deliberados

- macOS no ejecuta paquetes v2 todavía; declarativos siguen disponibles.
- Datos privados crudos efímeros por activación. Persistencia sólo mediante las
  claves de configuración declaradas y servicios ya expuestos de Core.
- Sin acceso directo al workspace ni proxy genérico para ejecutar herramientas;
  un plugin que dependía de esos accesos debe adaptarse a servicios concedidos.
- No se anuncia cobertura contra fallos del kernel, CPU/memoria/disco ilimitados
  o recursos que el administrador haya hecho públicos explícitamente al sandbox.
  Los perfiles AppContainer permanecen retenidos según el diseño compartido.
- No se habilitó globalmente Strong Linux para process.exec/build ni para M8 v1.

Referencias de implementación consultadas: [LPAC de Microsoft](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer),
[herramienta de referencia de Microsoft](https://github.com/microsoft/SandboxSecurityTools/blob/main/LaunchAppContainer/README.md),
[capability SIDs y liberación de memoria](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-derivecapabilitysidsfromname)
y [Bubblewrap](https://github.com/containers/bubblewrap/blob/main/bwrap.xml).
