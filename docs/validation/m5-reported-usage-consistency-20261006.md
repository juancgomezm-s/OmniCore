# M5/M5.5: uso reportado coherente

## Contrato reutilizado

`TokenUsage` ya define Input incluyendo los detalles de caché y Output incluyendo
razonamiento. `TokenUsageFields` identifica qué cantidades fueron reportadas.
`TokenUsageValidation.IsInvalid` comparte el control entre cualificación, runtime,
lecturas de gasto/replay y consumo de conversación. Rechaza negativos conforme al
contrato previo y, solo si ambos bits están reportados, CacheRead > Input,
CacheWrite > Input o Reasoning > Output. Igualdad válida. No compara un detalle con
un placeholder de un agregado no reportado, ni impone CacheRead + CacheWrite <=
Input: los contratos no especifican que ambos detalles sean disjuntos.

ProbeRunner devuelve Error y coste null antes de scorer/quote ante datos inválidos;
Host conserva el error tipado SuiteIncomplete y no escribe perfil, traits o CAS.
ModelPricing añade overload con máscara para cotizar solo agregados reportados y
coherentes. El overload anterior sin máscara opera con Input/Output, no convierte
detalle desconocido en medición. Tarifas explícitas cero no autorizan contradicción.

Explorer conserva el ModelStepCompleted y su artifact con uso/máscara originales
y coste null antes de bloquear herramientas/pasos. Con caps usa el bloqueo de
presupuesto existente y sin caps abandona el Turn. Lecturas de gasto y replay
rechazan también contradicción histórica. Ningún journal del usuario se reescribe.
ReadConversation descarta la invocación inválida de la medición, marca incompleta y
devuelve Unknown/null en total, coste, contadores y bundle Tokens; no duplica
completion/resumen ni incorpora otra sesión. El detalle parcial conocido sigue
presentado mediante Breakdown; no se inventa contador Reasoning en ese contrato.

MetaModelService valida antes de cotizar o capturar el resumen del proveedor. Ante
contradicción publica MetaModelInvocationFailed con uso/máscara crudos y coste null,
sin llamar al cotizador ni devolver el resumen inválido. Conserva el fallback
determinista de ExplorerTurn, que no se acredita como resumen del proveedor.
Agregados no reportados permiten el resumen directo de esta API con coste unknown;
no se interpreta ese resultado como autorización para continuar bajo un cap.

## Construcción y evidencia

Luna HIGH aportó propuestas Host/runtime/meta y doce controles de observabilidad.
Root leyó las propuestas completas, integró/corrigió fixtures y produjo el código
compartido. La propuesta runtime asumía ausencia de TurnCompleted tras un bloqueo
de presupuesto y ausencia de ModelCompleted para uso incompleto; esas expectativas
no corresponden al runtime existente. Los controles se ajustaron a su lifecycle
real: terminal BudgetExceeded válido, coste incompleto null, cero tools/calls extra.
También se evitó un bucle artificial de tools retornando EndTurn en segunda llamada.
No se rebajaron las assertions de bloqueo de uso contradictorio.

Primer filtro de runner inválido (`*Inconsistent*Usage*`) no ejecutó pruebas: se
conserva el log y se corrigió a dos clases exactas. Primer intento 20 casos/15 FAIL,
incluía tres controles de terminal incorrectos. Regresión válida: 20 casos/8 PASS/
12 FAIL (1.306 s): tres Host y nueve runtime realmente permitían la contradicción.
Tras fix: 20 PASS (1.068 s). Reporter: 12 casos/6 PASS/6 FAIL (0.417 s), luego verde.

Broad inicial: 391 casos/8 FAIL (2.283 s). Cinco tests antiguos esperaban Passed
con uso negativo; ahora exigen Error/score0/outputnull/error constante/máscara/raw/
quoteCalls0/costnull/duración acotada. Tres fixtures de overflow tenían auxiliares
long.MaxValue con agregado cero; ahora incluyen cada detalle en su agregado para
alcanzar la segunda invocación y overflow. Assertions de dos steps, calls/tools,
costes exactos, abandono y ausencia de resumen permanecen. No demuestra overflow
auxiliar aislado si el agregado también desborda.

Broad inicial corregida: 391 PASS / 0 FAIL / 0 SKIP (2.366 s). La regresión del
productor meta agregó 12 casos: RED 9 PASS/3 FAIL (0.556 s), que demostraba devolución
de resumen inválido y cotización. Después del fix, build fresco sin warnings/errores
y focal final 403 PASS/0 FAIL/0 SKIP (2.275 s), incluyendo la duración acotada del test
previo restaurada. Matriz de 32 máscaras por relación y controles de cero precios,
igualdad y caché no disjunta; son cuatro Facts, no 96 casos adicionales.
Full final: 2174 casos = 2170 PASS/0 FAIL/4 SKIP por permisos symlink (105.726 s),
exit0. Arquitectura fresca: 56 PASS/0 FAIL/0 SKIP (0.642 s), build0/0.
La full intermedia de 2162 casos no incluía los doce controles meta; no es la entrega
final. Los conteos focales se solapan con la full y no se suman.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -class '*Usage*' -class '*Spend*' -class '*Qualification*' -class '*Observability*'
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll
dotnet build tests/OmniCore.ArchitectureTests/OmniCore.ArchitectureTests.csproj --no-restore
dotnet tests/OmniCore.ArchitectureTests/bin/Debug/net10.0/OmniCore.ArchitectureTests.dll
```

Logs: `C:/Users/juanc/.codex/omni-m55-workers-20261006-2017/`, prefijos
`usage-consistency-*`, `usage-reporter-*`, `meta-usage-consistency-*`. Resultado final
en `usage-consistency-verified-full.log`; arquitectura en
`usage-consistency-verified-architecture-test.log`. Son fixtures deterministas,
SQLite/CAS reales para runtime/Host y proyección de SessionObservationHub. Los tests
meta usan journal en memoria y CAS privado. No autenticación, factura ni validación
visual de OmniCoder. No se declara M5/M5.5 cerrado. Wiring OAuth de cualificación Codex,
ledger User, replay opaco, ToolCallv3 y contratos M6 siguen en el plan.
