# Source del envelope durable

Contrato aditivo conforme a ADR-0046 §4. `DomainEvent.Source` es `string?`:
identidad explícita del escritor que produjo el envelope, no del actor upstream,
cuenta, provider, modelo ni nivel de confianza. El escritor actual `EventStream`
emite el valor fijo `OmniCore.Engine.EventStream`; nunca lo deriva del payload.
Scope y causa siguen en sus campos independientes.

`Create` y `Stored` aceptan el último argumento opcional `source = null`.
El store conserva el valor exacto. Los callers antiguos y eventos históricos
siguen sin atribución: no se rellena un origen retrospectivo ni se cambia el payload.
La migración SQLite añade `source TEXT NULL`, serializando la comprobación y ALTER
en la misma transacción inmediata que la migración de ExecutionId. Reabrir es
idempotente. Las lecturas por sesión y por tipo entre sesiones preservan el campo.

ProtocolMapper publica `source` dentro del payload JSON del evento ya expuesto
por su allowlist, con la redacción habitual. Se omite cuando es null; no cambia
WireEnvelope, versión del protocolo ni versión de los payloads de dominio.
Un tipo no expuesto sigue sin exponerse. Source no forma parte del estado canónico.

## Evidencia y reproducción

Pruebas de contrato con fixtures de eventos/artifact metadata y SQLite real;
no consultas autenticadas, consumo real ni integración visual de OmniCoder.
El build inicial del bloque reprodujo 6 casos: 4 PASS y 2 FAIL, por Source
ausente en el escritor y en el wire. Logs `source-foundation-red-build.log` y
`source-writer-red-test.log`, bajo
`C:\Users\juanc\.codex\omni-m55-three-20261006`.

La batería ampliada comprueba identidad fija, legacy null, migración idempotente,
persistencia de scope/UTC/ArtifactRefs, batch y aislamiento entre sesiones.
Focal final: 32 PASS / 0 FAIL / 0 SKIP, 1.503 s (`source-final-focal.log`).
Suite completa: 1802 casos / 1798 PASS / 0 FAIL / 4 SKIP por permisos symlink,
211.288 s (`source-final-full.log`). Build: 0 warnings / 0 errores
(`source-final-build.log`). Focal y full se solapan, no se suman.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*EventSourceEnvelopeTests*' -class '*CanonicalWriterArchitectureTests*' -class '*Sqlite*' -class '*Protocol*' -class '*EventEnvelope*'
```

Esto cierra Source para el escritor actual, no la totalidad de M5.5 ni la auditoría
de todos los CommandOutcome. No introduce scheduler/joins ni nuevos productores.
