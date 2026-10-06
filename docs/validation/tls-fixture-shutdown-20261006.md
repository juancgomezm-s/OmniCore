# Cierre controlado del fixture TLS — 2026-10-06

La repetición completa `single-active-run-final-full.log` falló en
SecurityP0Tests.Pinned_tls_handler_works_end_to_end_against_a_real_tls_server:
TaskCanceledException en SslStream.ReadAsync (línea478), propagada al await server
del finally. Resultado2111=2106PASS/1FAIL/4SKIP,106.468s,exit1.
La full anterior del mismo bloque pasó2111=2107PASS/0FAIL/4SKIP107.550s; no
demostraba que la intermitencia histórica estuviera resuelta.

El finally cancela el token del servidor privado antes de esperarlo. Un read/write
pendiente podía lanzar OperationCanceledException, que no estaba entre IOException
y AuthenticationException esperadas del fixture. Ahora el servidor termina únicamente
al recibir esa cancelación con su CTS efectivamente cancelado. Ninguna excepción
inesperada se captura. Los errores del cliente y assertions siguen propagándose.

No cambian las tres assertions (certificado fijado correcto acepta; otra raíz y
hostname fuera de SAN rechazan), timeout30s, validación TLS productiva, certificados
del usuario ni servidores reales. El TLS del fixture usa loopback privado/puerto
efímero; no acredita autenticación de proveedores ni facturación real.

Root reprodujo la excepción en suite completa, corrigió solo el fixture y conserva
el log rojo. Focal posterior282=279PASS/0FAIL/3SKIPsymlink3.563s/build0/0. Incluye
controles de cualificación y atribución multirun. El primer aislado del método
también pasó; resultados finales y repeticiones quedan en workers-20261006-2017.

Resultado final: tls-shutdown-final-full.log2111=2107PASS0FAIL4SKIPsymlink106.764s,
exit0; aislados del método1PASS2.382s y1PASS2.343s, con las tres assertions intactas.
Arquitectura56PASS0.623s/build0/0. Los counts son solapados, no sumables.

Reproducción:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method '*Pinned_tls_handler_works_end_to_end_against_a_real_tls_server*'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
```

Este cambio explica la cancelación de cleanup observada. No transforma un resultado
verde aislado en prueba de ausencia absoluta de otros fallos TLS.
