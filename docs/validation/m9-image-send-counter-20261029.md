# M9: observación de envíos del proveedor de imágenes — 2026-10-29

## Causa y corrección

`OpenRouterImageGenerationProvider` llamaba directamente a `HttpClient.SendAsync`
sin notificar a `GenerationRequestAttemptScope`. Una respuesta real podía quedar
registrada con imagen y costo correctos, pero `ObservedGenerationSends = 0`.

Se conecta el observador inmediatamente antes de `SendAsync`, después de comprobar
la cancelación. Esto también ejecuta el callback de admisión previo al envío que
ya utiliza el arnés. Un rechazo de ese callback no incrementa el contador ni llega
al handler HTTP. Los fallos HTTP, respuestas inválidas y cancelación dentro del
handler conservan el intento observado. El proveedor mantiene su límite de un
intento y no añade reintentos.

El contador observa intentos de despacho del cliente HTTP; no acredita por sí solo
facturación ni recepción de bytes por el proveedor. No se modifica el journal de
la llamada histórica: su cero sigue siendo evidencia de la versión anterior.

## Validación local

Base local antes del cambio: `main`, `9b702820`.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v:quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -class OmniCore.Tests.OpenRouterImageGenerationProviderTests -class OmniCore.Tests.GenerationRequestAttemptScopeTests -class OmniCore.Tests.GenerationRequestAttemptEvidenceTests -class OmniCore.Tests.ExplorerFinalResponsePublicationLeaseTests -noLogo -noColor -result-xml artifacts/m9-image-send-counter-20261029/tests.xml
```

Resultado final: compilación con 0 advertencias y 0 errores; 39 pruebas aprobadas,
0 errores, 0 fallos, 0 omitidas y 0 sin ejecutar. Incluye regresión de conservación
de imágenes/costo y de evidencia canónica de intentos. Los casos del proveedor
usan handlers HTTP en memoria, sin red, credenciales reales ni generación nueva.
La primera compilación detectó que el helper ampliado debía propagar el token de
cancelación de xUnit; se corrigió antes de la compilación y ejecución finales.

Sin workers, cambios de OmniCoder, instalación, commits, merges ni publicación.
La aprobación de Dot y la validación live de una llamada futura con esta versión
no están acreditadas por estas pruebas offline.
