# ADR-0047 — Modos explícitos, ejecución directa y esfuerzo UltraCode

- **Estado:** Aceptada (2026-10-01), por instrucción del propietario del proyecto.
- **Modifica:** ADR-0035 §3–§5, ADR-0016 §11, ADR-0046 §5/§9 y spec FR-RUN-004/007.
- **Relacionado:** ADR-0005, 0007, 0014, 0017, 0034, 0037, 0044 y 0046.
- **Implementación pendiente:** contratos y autoridad en M5.5; scheduling/delegación real en M6.

## Contexto

OmniCore ofrece tres modos elegidos por el usuario: PLAN, ACT y ORQ. Si una clasificación del modelo
puede cambiarlos automáticamente, esa elección deja de ser vinculante. Tampoco toda intención
requiere un plan visible, descomposición o workers: una explicación puede resolverse en un Turn
directo, con cero herramientas y cero agentes hijos.

El plan previo contiene un Plan interno de un item por Run. Se conserva ese registro de lifecycle,
pero se distingue de la fase de planificación y de la delegación. No existía una definición de
UltraCode en la documentación ni en los contratos del runtime al aceptar esta decisión.

## Decisión

### 1. Modo, estrategia y esfuerzo son ejes separados

| Eje | Qué decide | Autoridad |
|---|---|---|
| `RunMode` (PLAN / ACT / ORQ) | Qué clase de operación está autorizada | Usuario; excepción acotada de UltraCode (§4) |
| `ExecutionStrategy` (Direct / Workflow / Delegated) | Cómo organizar trabajo dentro del modo | Política determinista del producto, dentro del techo autorizado |
| Esfuerzo del producto | Profundidad, iteraciones y coordinación permitida | Selección explícita del usuario; UltraCode es un preset de este eje |
| Esfuerzo de razonamiento del provider | Opciones nativas de una llamada al modelo | Adapter + capacidades efectivas + presupuesto |

UltraCode no es un cuarto `RunMode`, un provider ni un valor de `reasoning_effort` enviado literalmente
a una API. Su traducción al razonamiento nativo se resuelve por capacidades, sin depender del nombre
del modelo. `high`, `xhigh`, `max` o un mayor presupuesto de tokens no activan UltraCode ni otorgan
autoridad para cambiar modo o crear agentes.

### 2. Frontera entre Core y política de producto

- Core ofrece lifecycle, journal, permisos, budgets, validación de commands, gates y mecanismos de
  scheduling/delegación. No impone a toda solicitud una metodología de planificación o multiagente.
- La política de producto decide si un workflow o una delegación aporta valor, dentro del modo y de
  la autoridad vigentes. El modelo puede recomendar; no selecciona ni modifica su propia autoridad.
- Una Task raíz y su Lane identifican la ejecución principal. No son un worker delegado y no obligan
  a crear Tasks hijas. Una tool call tampoco es un worker.
- El Plan interno de un item por Run (ADR-0016) es seguimiento técnico. Su existencia no fuerza
  `plan.propose`, una pantalla de plan, descomposición, un cambio de modo ni una aprobación humana.
  La UI lo muestra bajo demanda; no antepone un plan a una respuesta directa.
- Los gates se seleccionan por efectos y criterios reales de la Task. Una explicación no requiere
  build/test, evidencia de cambios inexistentes ni aceptación humana obligatoria para terminar.

### 3. Semántica vinculante de los tres modos

| Modo | Ejecución ordinaria | Planificación | Workers |
|---|---|---|---|
| PLAN | Responder, explicar, investigar y preparar propuestas con herramientas observacionales | Fase explícita cuando la intención pide un plan; una consulta puede terminar directamente | No crea agentes hijos; puede proponer una futura descomposición |
| ACT | Resolver directamente y aplicar cambios autorizados, con validación proporcional | Puede ordenar pasos o usar un workflow local; no exige planificación previa ni cambio a PLAN | No crea agentes hijos; un workflow en la Lane raíz no equivale a ORQ |
| ORQ | Coordinar trabajo dentro del objetivo autorizado | Plan y Tasks diferenciadas cuando la coordinación resulta útil | Permite delegación, sin exigirla ni imponer un número mínimo de agentes |

Una explicación sencilla sigue siendo directa incluso en ORQ. ACT puede pensar, leer y planificar
pasos antes de editar: eso no cambia su modo. PLAN conserva el techo de solo lectura de ADR-0037.
ORQ no amplía los permisos de ACT, las rutas de modelos ni los budgets. Las restricciones de escritura
paralela de M6/M7 siguen vigentes.

### 4. UltraCode: excepción explícita y limitada

**UltraCode** es el preset de esfuerzo adaptativo del producto para tareas complejas de programación.
Permite que la política determinista reorganice el trabajo y cambie entre PLAN, ACT y ORQ cuando
exista una razón observable dentro del objetivo. No obliga a usar el máximo esfuerzo, planificar o
delegar siempre: una tarea sencilla puede seguir resolviéndose directamente.

- Solo la selección explícita de UltraCode por el usuario autoriza cambios automáticos. Una
  recomendación del modelo, clasificación de complejidad, skill, extensión o subagente no lo activa.
- La UI/CLI explica al seleccionarlo que autoriza cambios de modo y coordinación dentro del objetivo.
  Registra una autorización durable vinculada al Run, su objetivo/revisión y su revisión de política,
  con `AllowedModes` y límites de coordinación/budget. El preset propone los tres modos; el usuario
  puede reducir ese conjunto o fijar un modo. Un modo fijado prevalece sobre UltraCode.
- Esta `ModeSwitchAuthorization` es distinta de un `Grant` de permisos de tools (ADR-0037): autoriza
  la selección de modo, no levanta una denegación de permisos ni acepta una ampliación de alcance.
- Fuera de UltraCode, `AutoModeSwitch` es false, independientemente del esfuerzo de razonamiento.
  Dentro de UltraCode solo es efectivo mientras la autorización del usuario siga vigente.
- UltraCode puede autorizar anticipadamente PLAN → ACT para la revisión del plan y el alcance cubiertos
  por la autorización. La política registra la decisión; no inventa una respuesta del usuario a `PlanApproval`.
  Una expansión de alcance, un plan fuera de la autorización o un modo no autorizado vuelve a requerir interacción.
- UltraCode no concede acceso adicional a archivos, shell, secretos, providers de pago ni acciones
  destructivas. Se siguen aplicando ADR-0037, ADR-0044 y la `SessionRoutingPolicy` de ADR-0046.
- Límites de agentes, profundidad, turnos, tools, tiempo y gasto son finitos y configurables en User.
  No se aceptan límites ausentes como autorización ilimitada. En M6 el scheduler hace cumplir estos techos.
- Desactivar UltraCode o fijar otro modo revoca la autorización adaptativa y bloquea nuevas
  delegaciones/transiciones automáticas. El cambio se aplica en una frontera segura de ModelStep;
  efectos ya iniciados terminan o se cancelan por su protocolo. Un downgrade a PLAN queda Deferred
  mientras no pueda garantizarse el techo de solo lectura; no se revierte trabajo implícitamente.
- El replay reconstruye selección, límites y revocación. Los hijos reciben una intersección de la
  autoridad del padre; no obtienen una autorización nueva ni pueden reactivar UltraCode.

#### 4.1 Razonamiento y duración del esfuerzo (alineación con Claude Code)

UltraCode combina **razonamiento reforzado** con la autorización adaptativa descrita arriba; no es
solo un selector de workers. La referencia de comportamiento y sus diferencias están trazadas en
[la revisión de fuente](../architecture/ultracode-alineacion-claude-code.md).

- Thinking se solicita habilitado cuando el modelo tiene una capacidad efectiva verificada. Se usa
  adaptive thinking si existe; en caso contrario, presupuesto explícito compatible con el provider.
  UltraCode propone `high`, no `max` obligatorio: nivel, presupuesto y salida se acotan por perfil,
  contexto disponible y TaskBudget. Un esfuerzo explícito fijado por el usuario prevalece; la UI
  muestra por separado UltraCode activo y el razonamiento realmente aplicado.
- No se promete razonamiento nativo en modelos que no lo soportan ni se simula soporte solo añadiendo
  una instrucción textual. El cliente informa la limitación y permite elegir otra ruta autorizada;
  ningún fallback compra acceso a otro modelo o cambia de provider sin la política vigente.
- El preset establece profundidad de análisis y validación proporcional: entender el código afectado,
  comparar alternativas cuando aporten valor, editar de forma acotada y comprobar el resultado. No
  obliga a presentar cadena de pensamiento, extender respuestas, reescribir archivos o producir un
  plan ceremonial. Pedir una explicación sigue sin autorizar implementación.
- Se distinguen preferencia persistente, selección de sesión/Run y refuerzo puntual de un Turn. Un
  refuerzo puntual de razonamiento vuelve al esfuerzo previo al terminar ese Turn (incluido su loop
  de tools); no habilita UltraCode ni modifica `ModeSwitchAuthorization`. Reintentar el mismo ModelStep
  conserva su configuración resuelta, sin elevarla por accidente.
- Activar UltraCode en el Run actual no lo convierte en un default global. Si el usuario guarda ese
  default, cada nuevo Run registra su propia selección y autorización acotada; no reutiliza una
  autorización de otro objetivo. Reiniciar el mismo Run conserva la selección y sus revocaciones.
- La precedencia de configuración, su origen y cualquier reducción por capacidad/budget son visibles
  y forman parte del fingerprint de ModelStep. No se anuncian `high/max` si el adapter envió otra cosa.
  Se reserva salida útil además del presupuesto de reasoning; agotar thinking no justifica loops
  infinitos ni aumentos automáticos de contexto, gasto o permisos.

#### 4.2 Planificación y delegación selectivas

- La planificación formal se propone ante ambigüedad arquitectónica, requisitos sin resolver o cambios
  de alto impacto; tocar varios archivos no basta por sí solo. Preguntas concretas pueden resolverse
  con `user.ask` sin abrir una fase de plan. Fuera de UltraCode, la propuesta espera autorización.
- La política considera delegación solo en ORQ efectivo y cuando haya una separación útil: investigación
  independiente, revisión especializada o implementación aislable. Evalúa dependencias, coste de
  coordinación y capacidad cualificada del modelo, no solo tamaño de la tarea o número de parámetros.
- Un fork puede reutilizar contexto compatible; un especialista recibe objetivo, alcance, restricciones
  y evidencia suficiente. Esfuerzo/modelo de cada hijo se resuelven por su rol y capacidad, dentro de
  los techos del padre; UltraCode no exige el mismo modelo ni `max` para todos.
- El coordinador conserva la síntesis y la aceptación: no inventa resultados mientras espera ni declara
  cierre por el resumen del worker. Recibe notificación de finalización, audita evidencia y valida
  cableado/integración. No incorpora de rutina transcripciones completas al contexto; un diagnóstico
  o una consulta de estado puede inspeccionarlas de manera acotada.
- Workers de escritura necesitan aislamiento y ownership conforme a M6/M7; investigación observacional
  no fuerza un worktree. Un worker sin UI no autoaprueba permisos: eleva la interacción al cliente
  autorizado o se bloquea/deniega. El hijo no modifica el modo efectivo ni el consentimiento del padre.

### 5. Propuestas, commands y transiciones

- Fuera de UltraCode, el modelo puede sugerir un modo alternativo y explicar la razón. La propuesta
  por sí sola no emite `RunModeChanged`, crea workers ni activa herramientas del modo propuesto.
- La aceptación llega desde una acción explícita del usuario por `IOmniClient`, o desde la política
  determinista con una autorización UltraCode vigente. Core valida origen, revisión y límites.
- Cada cambio efectivo registra modo anterior/nuevo, razón, origen de autoridad, referencia al command
  y a la autorización/revisión. El modo efectivo es visible en el cliente y se incorpora al fingerprint.
- Una respuesta genérica a `user.ask`, un permiso de tool o una aprobación de gasto no se interpreta
  como aprobación de cambio de modo. La interacción correspondiente debe declarar esa intención.
- Se conserva `/mode` de ADR-0035: modifica el default del próximo Run. Cambiar el Run activo requiere
  un command explícito distinto y aplicación segura; no se reinterpreta silenciosamente `/mode`.
- PLAN → ACT mediante `Aprobar y ejecutar` sigue siendo una autorización válida del usuario y no requiere
  UltraCode. `Aprobar sin ejecutar` no autoriza ACT ni ORQ. Sin respuesta no existe transición.
- Una consulta directa en PLAN termina con su respuesta observacional, sin `PlanApproval`. Un plan
  solicitado como entregable puede terminar `Planned` sin ejecutar. La aprobación habilita ejecución
  o acepta formalmente una revisión cuando el usuario lo solicite; no es requisito de toda respuesta.

### 6. Contratos, entrega y criterios de salida

M5.5 especifica y conecta la selección explícita, la autoridad para cambiar modo, la propuesta de modo,
los commands de selección/revocación, sus eventos y proyecciones. Se añaden al fingerprint del Turn
y se migran los journals anteriores con `AutoModeSwitch = false`; no se infiere UltraCode de un nivel
de razonamiento antiguo. M6 conecta scheduling y delegación a esa misma autoridad, sin otro camino.
Mientras un modo/capacidad no esté implementado, se informa `Rejected/Deferred`; no se sustituye por ACT.

Criterios deterministas obligatorios, con replay tras reinicio:

1. «¿Qué hace esta función?» en PLAN responde con cero Tasks hijas, cero escrituras y sin `PlanApproval`.
2. Una corrección acotada en ACT puede leer, editar y validar en la Lane raíz sin pasar a PLAN/ORQ.
3. ORQ resuelve una consulta trivial directamente; una delegación útil solo se admite bajo sus techos.
4. Una propuesta del modelo de pasar de PLAN/ACT a ORQ sin UltraCode conserva modo y no crea workers;
   `high/xhigh/max` del provider no cambia ese resultado. Una llamada directa a un command tampoco
   puede atribuirse falsamente al usuario.
5. Aprobar un permiso de tool, gasto o cuestionario no autoriza un cambio de modo. `Aprobar y ejecutar`
   sí autoriza PLAN → ACT; `Aprobar sin ejecutar`, silencio y rechazo no lo hacen.
6. UltraCode explícito admite un cambio autorizado y journalizado; un modo fijado, una autorización vencida,
   un objetivo ampliado o una ruta de pago no consentida impiden la ampliación automática.
7. Desactivar UltraCode durante una espera, reiniciar y reanudar mantiene la revocación. Los hijos no
   amplían la autoridad y no se programan nuevos workers fuera del modo efectivo.
8. Gates de una explicación no exigen build/test ni plan visible. El Plan interno de un item se
   reconstruye sin obligar al modelo a usar `plan.propose`.
9. UltraCode solicita thinking y resuelve esfuerzo/budget por capacidad; el wire request y la UI
   coinciden. Un modelo sin soporte informa la limitación; `max` incompatible no se envía. Un refuerzo
   puntual termina con su Turn sin persistir un default ni conceder cambio de modo.
10. Dos Runs con distintos objetivos no comparten autorización adaptativa. Un reinicio del mismo Run
    conserva la selección y límites; un retry no aumenta esfuerzo ni presupuesto implícitamente.
11. Delegación dependiente, trivial o fuera del techo se rechaza; una útil recibe alcance/capacidades
    acotadas. El coordinador espera evidencia real y verifica el entregable antes de aceptar el cierre.

## Consecuencias

Se conserva el runtime común y el seguimiento canónico existentes. Los modos son una elección
vinculante y la coordinación es una capacidad optativa. UltraCode aporta autonomía expresamente
seleccionada por el usuario, con límites observables; el esfuerzo del provider sigue siendo independiente.
Esta decisión amplía el plan, no declara implementado el comportamiento nuevo.


