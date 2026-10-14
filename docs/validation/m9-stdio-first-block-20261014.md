# M9 — primer bloque stdio (2026-10-14)

## Resultado

Este checkpoint entrega una ruta de proceso completa y consumible para CLI↔Host. `omni host --stdio`
ejecuta el `OmniCliRuntime` existente contra el journal persistente del workspace. `StdioOmniClient`
lanza ese hijo y negocia el protocolo; `omni stdio input/query/sim` consume el proceso real. Se
conservan `OmniCliRuntime.Connect()`, `IOmniClient` in-process, `ITrustedUserActionClient` y TUI.

El contrato es Protocol 1.0 con JSON Lines UTF-8 estricto, máximo de 1 MiB por frame antes de
decodificar y una petición en vuelo. Negocia `hello`, correlaciona respuestas y publica capabilities
para `sim`, `sim.resume`, `session.input`, ocho read models y replay
por lotes desde secuencia N. `CommandAck.Outcome/FirstSeq/LastSeq` cruzan el wire. Los fixtures
consumibles están en [`protocol/stdio/1.0`](../protocol/stdio/1.0/README.md).

El CLI público envió una entrada con caracteres en inglés, español, francés e italiano (más CJK),
recibió su replay, y un segundo CLI/Host consultó el journal persistido (`state` y `sessionIdentity`).
La prueba aisló `OMNICORE_DATA_DIR` y `OMNICORE_CONFIG_DIR` en temporales y no configuró provider,
por lo que no ejecutó ni facturó inferencia.

## Límites abiertos

`subscribe` es replay request/response por lotes, no entrega push. No hay lease de escritor entre
procesos, heartbeat, reconexión, deduplicación ni resolución de resultado ambiguo; si se pierde un
ack y el caller repite un command, sus efectos pueden repetirse. No se afirma exactly-once. El wire
rechaza completos los replay o queries que superan 1 MiB, sin respuesta parcial; v1.0 aún no tiene
cursor para paginar ese caso. Tampoco hay límites de memoria en el read de eventos de `IOmniClient`.
El wire
no puede implementar `ITrustedUserActionClient`, no toma un booleano de origen humano y no publica
autoridad humana externa. Los commands restringidos permanecen rechazados. Este bloque tampoco
implementa el consumer TypeScript de OmniCoder, inferencia remota como capacidad garantizada,
`media.read`, procesos persistentes ni ConPTY. La TUI no se conecta aún por stdio. OR5 autenticado
de M8 sigue pendiente por separado.

## Verificación ejecutada

- `dotnet build src/OmniCore.Cli/OmniCore.Cli.csproj --no-restore` — correcto, net10, 0 warnings y 0 errors.
- `dotnet build src/OmniCore.Client/OmniCore.Client.csproj -f net8.0 --no-restore` — correcto, Client y Protocol net8, 0 warnings y 0 errors.
- `dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --filter FullyQualifiedName~StdioTransportTests` — 9 pruebas focales PASS: CLI/Host hijo, persistencia/replay, Unicode, goldens consumidos por el test, versión incompatible, framing inválido y sobredimensionado, respuesta de replay acotada, EOF/cancelación, límite in-process y uso/diagnóstico es/en/fr/it.

No se ejecutó la suite integral.
