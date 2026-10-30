# M10: métricas reproducibles desde una copia del journal

Incremento local sobre Main `49bf57e6`, rama `codex/m10-evaluation-receipts`.
Se construye el exportador offline [measure.py](../../tools/evaluation/measure.py)
y su [guía de entrada y semántica](../../tools/evaluation/README.md). No modifica
runtime, protocolo, seguridad de Session, TUI, instalación ni archivos de M9.
OmniCoder permanece en solo lectura.

El JSON de interfaz filtra eventos y omite campos necesarios; no sirve como
journal de medición. La herramienta recibe una copia SQLite cerrada y explícita,
la lee con `mode=ro&immutable=1`, valida secuencia/identidades e integridad y
exporta tiempo, Turns, solicitudes de herramientas, tokens, crecimiento exacto
de contexto, escaladas frontier y costo cuando hay evidencia suficiente.
Éxito de tarea exige un oráculo externo declarado; `RunCompleted` no lo sustituye.
Ausencias, reintentos sin cobertura y contexto estimado quedan desconocidos.
No se vuelcan prompts, argumentos de herramientas ni contenido del contexto.

## Validación local

- `python -m unittest discover -s tests -p "test_regression_*.py"`: **35/35**.
  Incluye los cuatro casos del comparador existente y 31 del exportador:
  cobertura de uso/meta/reintentos, desconocidos frente a cero, no duplicación de
  cache/razonamiento, integridad/scope CAS, aislamiento de Runs, terminal ausente,
  secuencias discontinuas, escrituras rechazadas y políticas incompatibles.
- Simulador Core real del binario de `49bf57e6`, perfil y workspace temporales:
  copia consistente posterior a la salida del proceso, dos Turns, una solicitud
  de herramienta, 0,146929 segundos de reloj de journal, cero escaladas.
  Sin invocación de modelo; tokens/costo/contexto desconocidos, no cero.
- La fuente y el snapshot conservaron sus hashes; no se crearon sidecars en el
  snapshot. Recibo válido para el comparador y presentación es/en/fr/it.
- Se añade la suite Python al paso existente de CI Windows/Linux. **La CI verde
  de `49bf57e6` no acredita este incremento local posterior**; aún no se publica.

Recibo local: `artifacts/m10-measurement-smoke-receipt.json`.
Copia durable de recibo/script e índice:
`C:\Users\juanc\.codex\artifacts\omnicore-m10-measurement-20261030`.
SHA-256 del snapshot:
`fe0a3f219701335ed0b31a22888921e42c82d084c10f68e5d72b3b6927789740`.
SHA-256 de la medición:
`19e52aea20fec94bbf7e74e1bb3a87d1847bd6fe74352a2a58d42328fa6caa52`.
El snapshot contiene datos sintéticos y se conserva fuera del repositorio;
el recibo no autentica procedencia (`provenanceVerified:false`).

## Seguimiento del pendiente original

**Actualización posterior:** [batería agentic y comparación completadas](m10-agentic-20261030.md): Core 11/11, Pi 10/11. Esta tabla conserva el estado anterior a esa ejecución; ya no enumera trabajo pendiente de Core. La aceptación de OmniCoder sigue en su coordinación.

| Pendiente | Estado y siguiente paso |
|---|---|
| Batería agentic fija, spec §84–85/§96 | Trabajo propio de Core que puede continuar: fijar repositorios, entradas, oráculos y recuperación para las once categorías; ejecutar el brazo Core y conservar sus métricas. El full live 20/20 cualifica fixtures acotados, no sustituye esa batería. |
| Comparación Core frente a OmniCoder + Pi, spec §97 | Falta el brazo equivalente de Pi con las mismas tareas, identidad y política de medición. La búsqueda anterior no encontró un reporte comparable; el benchmark CPU FULL/BARE de cinco tareas no sirve. No fabricar resultados ni modificar el consumidor sin encargo de su coordinación. |
| Aceptación de consumidor | La coordinación OmniCoder conserva su integración/aceptación. Core entrega contratos/evidencia; este corte no reabre M9 ni impone cambios a su interfaz. |

Las once categorías son exploración de repositorio, bug simple, bug multiarchivo,
fallo de build, fallo de tests, refactor, contexto abundante, delegación,
denegación de permiso, fallo de modelo y reanudación tras interrupción. Un test
unitario relacionado no equivale a una tarea agentic medida de esa batería.

LSP tiene aceptación Strong con servidor fixture en CI; un servidor de producción
del usuario conserva su propia aceptación cuando se configure, sin instalarlo por
esta revisión. La búsqueda vectorial/consolidación diferida por ADR-0028 y el
frame Git completo posterior a M10 no se añaden como bloqueantes nuevos.
