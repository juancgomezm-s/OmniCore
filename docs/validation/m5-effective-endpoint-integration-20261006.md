# M5: identidad del endpoint efectivo

Control offline de integración real Host/adaptador HTTP/SQLite/CAS, con dos
servidores loopback privados. No se inyecta IModelProvider: la configuración normal
declara A y `OMNI_BASE_URL` selecciona B. La prueba exige exactamente diez solicitudes
a B y ninguna a A, estado Qualified sobre la Quick completa, hash/perfil/evidencia
para B y ausencia de perfil al consultar la misma definición bajo A.

Luna HIGH construyó el control; root leyó su entrega completa, reprodujo y corrigió
un error CS0136 por dos variables locales `bytes`, sin cambiar assertions. Root
añadió tiempo máximo de 30 segundos al servidor privado y limitó el catch de
cancelación a su propio CTS. Se restauran las variables de entorno y se cierran
listener, conexiones y pool SQLite privados. No se modifica ningún servidor real.

El primer binario había capturado una versión intermedia del archivo mientras el
worker terminaba; su ejecución no acredita la entrega final. La compilación nueva
registró el error en `unknown-bound-endpoint-build.log`. Tras corregirlo, build
0 errores/0 advertencias y focal de endpoint+cota desconocida: 15 PASS, 0 FAIL,
0 SKIP, 0.683 s. Full de la entrega final conjunta con cota desconocida: 2126 casos,
2122 PASS / 0 FAIL / 4 SKIP por permisos symlink (104.637 s), exit0, proceso87653
terminado. Las cifras se solapan; no sumar focal y full.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -class '*EffectiveEndpointIntegration*' -class '*UnknownAttemptBound*'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
```

Logs: `C:/Users/juanc/.codex/omni-m55-workers-20261006-2017/`, prefijo
`unknown-bound-endpoint-fixed-*`. Fixture, no consulta autenticada ni gasto real.
Este control demuestra la ruta normal actual; no reproduce un defecto de identidad
ni demuestra estabilidad si un tercero cambia el entorno durante una invocación.
No acredita por sí solo todos los gates de M5 ni de M5.5.
