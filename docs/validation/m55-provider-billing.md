# Declaración explícita de facturación del provider

Checkpoint 2026-10-06 06:54 UTC / 00:54 America/Mexico_City.

ADR-0046 §3: `ProviderDescriptor.BillingMode` es `Unknown` si el usuario no declara
la modalidad. No se infiere de credenciales, perfil `codex`, hostname privado,
precio ni nombre del provider. El provider local incorporado declara `Local`.

`providers.yaml` admite `billingMode` con estos nombres exactos, case-sensitive:
`Unknown`, `Local`, `IncludedQuota`, `CreditBalance`, `MeteredCurrency`.
El schema JSON declara el mismo enum. Valores inválidos producen diagnósticos con
archivo, ruta YAML, línea y columna. Un número no se interpreta como ordinal del enum.

La factory del Host conserva la modalidad y el perfil al construir los tres
adapters nativos, incluso al reemplazar el endpoint efectivo. No cambia autenticación
ni TLS. Declarar la modalidad **no autoriza gasto** ni demuestra disponibilidad de cuota.

Reproducción:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*ProviderBillingConfigurationTests' -class '*ConfigValidationTests' -class '*M2WiringTests'
```

Build: 0 warnings/errores. Focal: 32 PASS, 0 FAIL, 0 SKIP.
Log: `C:\Users\juanc\.codex\omni-m55-three-20261006\provider-billing-tests.log`.
Pruebas de configuración/factory en proceso, sin llamadas autenticadas ni consumo real.

Pendiente: política durable por Session, consentimiento de routing/escalación y regla
de cuota. Este contrato por sí solo no acredita ese criterio de salida de M5.5.
