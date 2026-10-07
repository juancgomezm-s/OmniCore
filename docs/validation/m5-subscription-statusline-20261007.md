# M5: cuota de suscripción en la barra de estado

## Contrato y recorrido

`UsageSnapshot.AccountQuota` es aditivo y opcional. Conserva el
`ProviderQuotaSnapshot` existente: proveedor, identidad de cuenta, fuente,
fecha de medición, disponibilidad, ventanas/reinicios y créditos con scope.
No sustituye `Remaining`, que sigue representando los límites de respuesta
del adaptador. Los slots numéricos históricos tampoco se convierten en contexto.

`OmniCliRuntime.CurrentUsage` lee la medición cacheada del proveedor del turno
en la misma sesión. Rechaza un contexto perteneciente a otra sesión. El renderer
no consulta cuentas ni recibe credenciales. Una consulta Unknown conserva la
última lectura válida como Stale, su AsOf original y LastQueryAttemptAt.

`UsagePresentation.AccountQuota` muestra sólo porcentajes reportados/stale
finitos en [0,100]. Los desconocidos permanecen como `—`. La duración procede
de DurationMinutes; si falta, se muestra el ID de ventana, sin inventar 5 horas.
Saldo de cuenta y límite de clave tienen etiquetas distintas; cero reportado
es válido, cero inventado no. Unidad y moneda permanecen independientes.
Un cliente con JSON histórico sin AccountQuota conserva el formato anterior.

## Evidencia reproducible

Root implementó contrato/Host/formatter/CLI y pruebas unitarias; Luna HIGH
aportó SubscriptionStatusLineIntegrationTests. El test ejecuta la ruta normal
Responses mediante HTTP loopback y SQLite, con 60% de límite de tokens y 80%
de suscripción sintética, refresh Unknown y consulta de otro proveedor.
También comprueba un bucket SessionId nuevo vacío. No demuestra un cambio de
sesión dentro del mismo runtime: no existe una API pública para hacerlo.

Al retirar únicamente el cableado CLI, build exit0 y 13 casos dieron
11 PASS/2 FAIL: tanto el formatter vía CLI como el turno normal perdían la
cuota. Log subscription-statusline-red-1304.log. Restaurado el cableado,
build exit0, cero advertencias/errores, 17.04 s; focal ampliada 71 PASS/0 FAIL,
1.037 s, subscription-statusline-fixed-1305.log.

FULL subscription-statusline-full-1305.log terminó exit0: 2874 casos,
2870 PASS/0 FAIL/4 SKIP por permisos de symlink, 123.724 s. Los conteos
focales se solapan con la FULL y no se suman.
Arquitectura: fresh build exit0, cero advertencias/errores, 1.51 s;
56 PASS/0 FAIL, 0.553 s, subscription-statusline-architecture-1307.log.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor -class '*SubscriptionQuotaPresentationTests' -class '*SubscriptionStatusLineIntegrationTests' -class '*UsagePresentationTests' -class '*SessionUsageReporterTests' -class '*SubscriptionQuotaTests' -class '*SessionObservabilityTests'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor
```

Logs: C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/.
Estas pruebas usan fixtures, no acreditan consultas autenticadas ni consumo
real. La consulta Codex real anterior está documentada por separado en
[m5-live-quota-20261007.md](m5-live-quota-20261007.md). No se modificó Git en
OmniCoder ni se acredita aquí revisión visual de toda la aplicación.
