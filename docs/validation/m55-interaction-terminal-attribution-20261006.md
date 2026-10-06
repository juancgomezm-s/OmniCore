# M5.5 — expiración y atribución de eventos terminales

Verificado 2026-10-06 22:05 UTC / 16:05 America/Mexico_City.

## Defectos y corrección

QuestionnaireInteractionService reconstruía pending/resolved ignorando
InteractionExpired. Una pregunta de un Run cancelado reaparecía pendiente y el
servidor aceptaba su respuesta después de empezar otro Run en la misma sesión.
Ahora la expiración es terminal: Pending la excluye y Resolve rechaza respuestas
tardías sin publicar respuesta ni transición. No se reescribe historia.

Una respuesta válida usa el scope del InteractionRequested durable, no el ambient
del llamador ni `_lastRunId`. El servidor deriva el Run que recibe UserInputReceived
del request; resolución y transición permanecen en un batch atómico. Conserva
Run/Task/Lane/Turn/ToolCall/Execution, CAS y UTC; la causación sigue siendo el comando
real de respuesta, no un evento anterior inventado. El scope ambiental se restaura.
Para envelopes históricos sin Run explícito se usa el RunCreated precedente.

RunControlService ya producía estados terminales correctos, pero los envelopes de
ToolCallCancelled, ToolCallFailed, InteractionExpired y TurnInterrupted heredaban
scope vacío/ajeno. El batch conserva ahora atribución del Requested/Started original
según la entidad que termina, sin dividir el commit ni sustituir la causación.

## Evidencia y ownership

Luna HIGH construyó dos fixtures; root auditó y corrigió imports/constructor/
nullability de pruebas antes de reproducir, implementó producción y añadió
controles ExecutionId/ToolCallId. Los errores de compilación iniciales del fixture
no se cuentan como RED funcional.

- Cuestionarios: RED 3 FAIL, 1.207 s antes de modificar producción.
- Cancelación/interrupción: RED 4 FAIL en selección 44 casos (40 PASS), 3.183 s;
  build sin warnings/errores. Ambient ausente/foráneo, segunda ejecución misma
  sesión y SQLite reopen. Los otros controles de cuestionarios ya pasaban.
- Final: 96 PASS / 0 FAIL / 0 SKIP, 6.999 s; build 0 warnings/errores.
- Arquitectura: 56 PASS / 0 FAIL / 0 SKIP, 1.239 s; build 0 warnings/errores.
- Los siete casos nuevos cubren expiración, rechazo sin append, resolución válida,
  ambos comandos y ambas variantes ambient; final añade captura filesystem para
  comprobar compatibilidad del bloque anterior. Resultados solapados, no sumables.

SQLite, CAS y servidor reales en carpetas temporales privadas. No consultas
autenticadas ni consumo real. No full fresca verde: mantenimiento M5 conocido
sigue separado. Este bloque no acredita todos los criterios de cierre M5.5.

Logs: `C:\Users\juanc\.codex\omni-m55-workers-20261006-2103\questionnaire-expiry-attribution-red-*`,
`questionnaire-scope-green-*` (RED cancelación), `terminal-scope-final-*`,
`terminal-scope-architecture-*`.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*QuestionnaireExpiryAttributionRegressionTests' -class '*CancelledToolCallScopeRegressionTests' -class '*QuestionnaireVerticalTests' -class '*QuestionnaireTurnTests' -class '*QuestionnaireTests' -class '*SendInputQuestionnaireGuardRegressionTests' -class '*InputInteractionCommandOutcomeTests' -class '*M55ThreeStepSuspensionTests' -class '*RunControlTests' -class '*RunControlEdgeTests' -class '*RunControlCommandOutcomeTests' -class '*ToolCallMultiRunDurableAttributionTests' -class '*FilesystemPreimageIntegrationTests'
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore -v quiet
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll -noLogo -parallelMode none
```
