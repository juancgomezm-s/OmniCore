# Propuestas de modo — ADR-0047 §5/§6

`mode.propose` acepta exclusivamente `mode` (`plan`, `act`, `orq`) y `reason`
(texto no vacío, máximo 512 caracteres). Es una herramienta observacional de
categoría `PlanProposal`, disponible en la composición normal Host según la
política efectiva de capacidades. No recibe origen, autorizaciones, límites,
objetivo, revisiones ni identidad de sesión del modelo.

ExplorerTurn registra `run.mode_proposed` v1 junto con el resultado exitoso de
la ToolCall, en un mismo append atómico. El runtime captura el modo vigente y
las revisiones de autoridad, objetivo y política desde el journal, no desde los
argumentos. El evento identifica Run, Turn y ToolCall; el envelope identifica
sesión, secuencia y fecha UTC. La razón usa la redacción habitual del journal.

`ModeProposalProjection.Replay` conserva el historial observacional por sesión
y Run. Valida lifecycle, envelope, snapshot vigente y una ToolCall exitosa
`mode.propose` del mismo Run/Turn con argumentos coincidentes. Rechaza recibos
cuya atribución Task/Lane/Execution no coincida exactamente con la propuesta,
ausentes, duplicados, revisiones obsoletas y referencias a otra sesión. El writer
valida el batch antes de persistir; un rechazo no consume secuencia. El evento
no existía en schema0 y no se interpreta como un evento legacy.

El protocolo publica `from`, `to`, `reason`, `turnId`, `toolCallId`,
`authorityRevision`, `objectiveRevision`, `objectiveDigest`, `policyRevision`,
`origin=Model` y `advisory=true`, además de los campos normales del envelope.
El cliente puede presentar la sugerencia, sin tratarla como selección efectiva.
No se modifican RunMode, permisos, rutas, presupuesto ni fingerprint de autoridad
por recibir una recomendación. No se crean Tasks hijas ni workers.

Aceptar un modo sigue siendo una acción explícita del usuario mediante el
command existente `run.mode.select` y su frontera confiable. No se interpreta
una respuesta a `user.ask`, un permiso o consentimiento de gasto como aceptación.
Esta herramienta tampoco llama a `ApplyUltraCodePolicyTransition`: recomendar
no es decidir una política. El disparador productivo de UltraCode sigue pendiente;
estos contratos no acreditan ese criterio ni implementan scheduling M6.

## Evidencia reproducible

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*ModeProposalIntegrationTests' -class '*CliEndToEndTests' -class '*ModeAuthority*' -class '*UltraCode*'
```

`ModeProposalIntegrationTests` usa un modelo scripted con el pipeline productivo,
SQLite/CAS, codecs, redacción y reopen. Comprueba consejo sin transición,
identidades, redacción, falsificaciones, duplicados y schema0. El caso
`Normal_PLAN_chat_can_recommend_ORQ_without_granting_mode_or_creating_workers`
recorre TuiTurnHost → runtime CLI → adapter HTTP loopback → pipeline/capability
boundary → journal SQLite → replay. Son fixtures de proveedor, no consultas
autenticadas ni evidencia de consumo real o de la UI de OmniCoder.

`ModeProposalUserAcceptanceTests` añade el recorrido por `ExecuteExplorerTurn`:
la propuesta conserva la autoridad, `Send` genérico rechaza una selección sin
avanzar secuencia y `SendUserAction` acepta la selección explícita del usuario
en el mismo Run. El replay conserva el historial advisory y la transición
efectiva como registros separados.
