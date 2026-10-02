# M5.5 Phase A — Bug 5: preservar el input al reanudar un Turn

**Base:** `281dcf1` (`codex/nemotron-m55-resume-input`)  
**Alcance:** `ExplorerTurn.Ask` y pruebas de integración locales; sin llamadas a proveedores reales.

## Hallazgo y corrección

Al reanudar un Turn abierto, `Ask` sustituía el texto recibido por una cadena vacía. Ahora aplica redacción al input en ambos caminos, lo añade una vez a la conversación del modelo y lo registra como `UserInputReceived` en el journal también cuando el Turn ya está abierto. Se conserva el mismo `TurnId`; no se crea un segundo `TurnStarted`. Si el caller no proporciona `origin`, el input reanudado se identifica como `ResumeInput`. La comprobación de cuestionarios pendientes sigue ocurriendo antes de aceptar texto libre.

## Cobertura

- El test de continuidad comprueba que el texto de seguimiento llega al modelo y al journal, junto con el historial previo, sin multiplicar eventos.
- El test SQLite crea un Turn abierto, cierra y reabre el journal, y luego lo reanuda. Comprueba el mismo `TurnId`, un solo `UserInputReceived`, el texto en el request del modelo una sola vez y el evento terminal del mismo Turn.
- Los proveedores son fakes; esto no acredita el flujo con una TUI interactiva ni con un provider remoto.

## Verificación independiente

- `dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --filter "FullyQualifiedName~Explorer_resume_open_turn_preserves_user_input_and_journals_it|FullyQualifiedName~Explorer_resume_after_sqlite_reopen_persists_input_once_on_same_turn" --no-restore`: **2/2**.
- `dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --filter FullyQualifiedName~M2IntegrationTests --no-restore`: **34/34**.
- `dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore`: **1101 correctas, 0 errores, 4 omitidas** porque este entorno no permite crear symlinks.
- `dotnet test tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore`: **52/52**.
- `dotnet build OmniCore.slnx --no-restore`: correcto, **0 advertencias y 0 errores**.
- `git diff --check`: correcto.

## Límite de cierre

Esta evidencia valida el comportamiento del runtime con stores reales/locales y un modelo fake. No cierra por sí sola el gate de interacción TUI en vivo ni otros bugs de M5.5.
