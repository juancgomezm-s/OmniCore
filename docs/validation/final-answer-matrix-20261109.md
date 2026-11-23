# Matriz de cierre de turnos locales

## Alcance

Se integró main hasta `6b0dbc99`, conservando los cambios de navegación de rc.25.
Esta revisión amplía rc.24: no presupone que todos los servidores soporten apagar
el razonamiento ni convierte razonamiento en texto de respuesta.

## Hallazgos corregidos

- Un texto formado sólo por espacios se aceptaba como respuesta final.
- Una salida vacía con MaxOutputTokens podía registrar TurnCompleted. Ahora
  registra TurnAbandoned y conserva el motivo de agotamiento de salida.
- La intención de cierre era sólo una variable en memoria. ModelStepStarted v5
  persiste FinalAnswerOnly; los eventos v1–v4 se leen con false por defecto.
- Al retomar antes del cierre se reconstruye su necesidad desde la última
  respuesta completada, con identidad de ruta/modelo y verificación del artefacto.
- Al retomar después del cierre se recuperan sus bytes y resultado sin otra
  generación, incluidas las respuestas vacías sin visibleContent y las fallidas.
  Una publicación ya registrada tampoco se duplica.
- Se distinguen respuesta vacía, sólo razonamiento, herramientas inesperadas en
  el cierre, agotamiento de salida y límite de pasos, con recursos es/en/fr/it.
- SSE mezclaba alternativas de choices. Ahora sólo consume la alternativa cero;
  una alternativa ajena no puede cerrar la respuesta principal ni aportar tools.
- Motivos finish_reason desconocidos, streams sin respuesta y llamadas de
  herramientas sin cierre explícito se rechazan, sin reintentar generación.
- Un encabezado válido de la respuesta de generación puede identificar el
  dialecto local aunque no esté habilitado el sondeo de metadatos.

## Casos de la matriz

| Caso | Resultado exigido |
| --- | --- |
| Sólo razonamiento, motor conocido | Una pasada final sin herramientas y sin pensamiento automático |
| Motor ajeno o capacidades incompatibles | No inventar dialectos ni cambiar preferencias |
| El servidor ignora la petición final | Diagnóstico de sólo razonamiento; ningún ciclo adicional |
| Vacío o espacios | Diagnóstico de respuesta vacía; sin TurnCompleted |
| Tokens agotados, incluso con tool parcial | Abandono con motivo de salida; no ejecutar tools |
| Tools en la pasada final | Rechazo específico; cero ToolCallRequested |
| Pausa antes del cierre | Retomar la pasada sin herramientas ni continuación opaca |
| Caída después del cierre o su publicación | Recuperar sin nueva generación ni mensaje duplicado |
| EOF sin terminación, tools incompletas, sólo usage | Sin ResponseCompleted ni llamada ejecutable |
| Varias alternatives y usage posterior | Sólo choice cero; preservar usage terminal |
| Journal anterior | Upcast compatible, sin inventar la marca de cierre |

## Validación y límites

Pruebas deterministas con configuración, SQLite, CAS y HTTP simulado aislados.
No se usa el servidor personal de Qwen como fixture. No se atribuye aprobación
de todos los modelos a estos resultados. Se ejecutan comprobaciones locales;
la release sólo usa el workflow independiente de compilación y firma.

La primera ejecución de la matriz detectó una generación extra al recuperar
un cierre sin bloques visibles. La corrección usa también el resultado y el
paso completado persistidos, sin deducir que una salida vacía no fue generada.

Resultado final: solución compilada sin errores ni advertencias; 359 pruebas
C# de cierre, replay, transporte, eventos, contexto, razonamiento, arquitectura,
auditoría e idiomas aprobadas, sin omisiones. Se ejecutaron en serie
(`-parallelMode none`) desde una carpeta temporal con datos/configuración propios.
Los primeros ensayos paralelos tenían conflictos de preparación de sesiones;
el fixture de preferencias también declara ahora su workspace temporal.
La matriz focal de 114 casos forma parte de esa regresión, no se suma dos veces.

`python scripts/validate-local.py --scope quick`: aprobado. npm: 84 aprobados,
cero fallos y una omisión por symlinks; las suites Python también aprobaron.
No se ejecutó la solución completa ni pruebas de otros sistemas operativos.

Las respuestas vacías conservan la auditoría de consumo aun al abandonar el
turno. La recuperación respeta los hooks de BeforeCompletion y verifica los
artefactos antes de reutilizar una publicación previa.
