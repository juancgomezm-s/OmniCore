# M5: cota de intentos desconocida y wrappers

## Contrato y alcance

`IModelRequestAttemptBound.MaximumGenerationRequestAttempts` es ahora `long?`.
`null` significa que no hay una cota finita conocida; valores conocidos deben
ser positivos. Los consumidores que implementaban el getter `long` deben adaptarlo
y recompilar. El contrato opcional cuenta envíos de generación por invocación,
incluidos retries y reenvío tras refresh; no cuenta el refresh de autenticación
ni redirects del transporte y no es factura ni límite de cuenta.

Host ya no sustituye la ausencia de capacidad por un intento. Una capacidad con
null, cero, negativo o getter fallido se considera no disponible. Para rutas no
declaradas Local, se rechaza antes de invocar o guardar perfil, traits y evidencia
mediante `ModelQualificationCostEvidenceUnavailableException`. Los detalles de
la excepción del getter no se exponen. Local explícito puede continuar con cota
desconocida, que se registra como null; esto no demuestra la gratuidad real del
endpoint. Las rutas normales conservan la cota de fábrica default 3 / Codex 6.

`TelemetryObservingModelProvider` transmite la capacidad del proveedor interno;
si no la tiene, transmite null, sin fabricar uno. La cota se captura una vez antes
del trabajo y pasa a la estimación y a la evidencia CAS. El JSON v1 incluye campos
aditivos `maximumGenerationRequestAttempts` (number/null) y
`generationAttemptBoundSource` (`configured-factory-default`,
`injected-provider-capability`, `unavailable`). No se relee después del stream.

Los once providers de tests anteriores son respuestas/eventos en memoria y ahora
declaran explícitamente un único intento. No se modificaron sus assertions,
configuración ni comportamiento de streaming. La identidad y billing de una
inyección siguen siendo responsabilidad del consumidor: una cota positiva no
prueba que corresponda al endpoint/precio declarado.

## Evidencia reproducible

Root integró el contrato, Host, wrapper y controles adicionales; Luna HIGH aportó
la propuesta de regresión y la declaración explícita en los once fixtures.
Regresión inicial: 11 casos, 5 PASS / 6 FAIL / 0 SKIP (0.514 s). Tras la corrección:
11 PASS (0.463 s). Controles adicionales cubren ausencia directa sin wrapper,
IncludedQuota desconocido, Local con null persistido y getter que falla si se relee
después del stream. Focal final: 253 PASS / 0 FAIL / 0 SKIP (1.654 s).
Arquitectura: 56 PASS (0.652 s); build sin errores ni advertencias.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -class '*Qualification*' -class '*AttemptBound*' -class '*Telemetry*'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll
```

Logs fuera del repositorio: `C:/Users/juanc/.codex/omni-m55-workers-20261006-2017/`,
prefijos `unknown-attempt-red-*`, `unknown-attempt-fixed-*`,
`unknown-attempt-final-*`, `unknown-attempt-architecture-*`.
Full final conjunta con el control endpoint: 2126 casos, 2122 PASS / 0 FAIL /
4 SKIP por permisos symlink (104.637 s), proceso 87653 terminado con exit0.
Recompilación fresca 0 errores/0 advertencias. Las cifras focal/full se solapan.
Estos son fixtures de consumo y almacenamiento real SQLite/CAS, no
consultas autenticadas, débito de cuenta ni garantía monetaria de retries fallidos.

M5/M5.5 siguen abiertos: uso reportado incoherente, validación conectada, ledger/
reserva y demás criterios de ADR0046 no quedan acreditados por este bloque.
