# ADR-0060: autorización Orchestrate y recuperación nativa del workflow

Estado: recorrido integrado del workflow aceptado localmente el 2026-10-18,
Core `582dc9c` y Coder bundle `1fb3639`/test `b919802`. La atribución de
mutaciones de procesos al ledger tiene un primer vertical v1 Strong construido
y validado localmente el 2026-10-22. La paridad de procesos generales sigue
pendiente; publicación y CI de cada incremento se comprueban por separado.
M9 sigue abierto; M8 espera pruebas manuales.

**Corte M9-L local del 2026-10-23:** la integración nativa de worktrees reutiliza
`worktree.integrate`; el cliente obtiene ownership y propuesta desde un Lane
público de su Session. Su Permission tool-only se reanuda mediante
`RunInteractionResumed`, con admisión/Started/llamada/permiso/resolución de usuario
correlacionados, sin nueva entrada ni inferencia. Los validadores canónicos y de
replay conservan los rechazos de scope y procedencia. El recibo deferred original
sólo pasa a resultado terminal con hash/Session/Run y evidencia durable del efecto;
el ACK se conserva. [Aceptación local y límites](../validation/m9-worktree-lifecycle-20261023.md):
12 casos de Luna bajo sin recompilar; no hay CI/publicación de este incremento.

## Problema

El handoff de ADR-0059 ya ejecuta prompts y recupera admisiones sin inicio, pero
OmniCoder sólo ofrecía Plan/Act. El executor compilado requiere autorización
Orchestrate vigente y contexto raíz real. Además, una etapa que devuelve 3 deja
la invocación Started: repetir el comando no es una recuperación segura.

## Decisión

Reutilizar `run.mode.select` con esfuerzo UltraCode/adaptive, modos admitidos y
límites finitos explícitos; Core conserva comparación revisionada, techo de
Session/modelo, duración y presupuesto. La interfaz presenta el alcance antes
de que la acción humana lo autorice. El renderer no fabrica grants ni dispone
de un RPC de delegación arbitraria. La elección de modo no ejecuta el workflow.

La forma HMAC nativa de Orchestrate es específica: trece campos, modo wire `orq`,
esfuerzo UltraCode adaptativo, allowedModes reconocidos/únicos y límites positivos
de agentes, profundidad, turnos, herramientas y tiempo; maxSpendUsd es un decimal
finito obligatorio. Plan/Act conservan su payload simple de cuatro campos. Los
scopes, autoridad y eventos usan `orq`; la preferencia legacy conserva su nombre.

Cada etapa divulga directamente sólo su catálogo compilado: no contiene el
descubridor general tool.search. Esa divulgación no amplía capacidades ni cupos;
ToolRuntime vuelve a comprobar las políticas Session/modelo para cada solicitud.

La resolución automática de razonamiento también considera el adaptador de la
ruta. Responses conserva un esfuerzo declarado; Anthropic conserva su presupuesto
explícito y reserva de salida. Chat-compatible no tiene representación de esa
solicitud: UltraCode registra Requested=high, Applied=null y reducción Capability,
con la advertencia existente. No se inventa un campo de protocolo ni se anuncia
razonamiento efectivo. Preferencias y boosts explícitos incompatibles se rechazan
antes de HTTP; las continuaciones del mismo Turn conservan su selección durable.

El catálogo nativo y la admisión comprueban el mismo requisito de disponibilidad:
Run autorizado en ejecución, root Task/Lane/AgentExecution vivos, perfil válido,
sin paso/modelo/tool raíz abierto, snapshot real persistido con modelo/ruta que
permite orquestación y Plan raíz canónico. No se siembran eventos ni se simula
un turno para habilitarlo. Un turno ordinario del usuario puede proporcionar
ese contexto; si no existe, el workflow debe permanecer deshabilitado.

`workflow.recovery` es una lectura acotada de invocaciones y etapas canónicas,
sin argumentos protegidos, objetivo ni contenido del verificador. Expone
ready/pending/blocked/completed. Consultar, reconectar o hacer replay no inicia
ni supervisa un proceso. Un hijo Accepted o una ejecución incierta no se vuelve
a lanzar por inferencia desde PID o un ACK.

`workflow.continue` es una intención humana nueva, firmada para la invocación,
Session/Run, revisiones de modo/política/modelo/routing y catálogo/fuente exactos.
Deriva contenido del artefacto protegido y stages del journal. Continúa el mismo
workflow; no borra Started, no crea otra admisión ni vuelve a despachar una etapa
que ya está aceptada. Sólo puede despachar Created seguro o supervisar Returned;
los completados canónicos se reutilizan con sus evidencias y dependencias. Los
gates de Build/Test/árbol Implement→Verify y la integración posterior al workspace
principal mantienen sus autorizaciones existentes. No se promete exactly-once general.

Main/SDK/adapter conservan fencing de sender, workspace, VM, Session y generación
a través de awaits y antes de firmar. La UI sólo ofrece continuar con un estado
canónico listo; Accepted del comando no equivale a workflow completado.

## Aceptación

Compilación, modo ORQ por HMAC (1/1), preservación de preset con nota (2/2) y
recuperación anterior a Started tras corte ACK (1/1) tienen evidencia local en
[el acta](../validation/m9-native-command-handoff-20261018.md). Esta última
reabre el mismo store y exige continuación humana; no demuestra kill OS ni cierre
del workflow. Coder tiene formulario, prestart/continuación y lectura tras fallo.

Electron→Host→hijos/tools/verificador sobre proyecto temporal real aprobó tras
reinicio: denegación 47.404 s y consentimiento único 84.825 s. Se conserva la
misma Session/Run, las consultas no ejecutan trabajo y no se duplica el proceso;
deny exige cero inicio y todos los terminales, consent_once exige verificador
exitoso, resultado al modelo y workflow completed. Build 0W/0E y preflight 7/7
aprobados. Fixtures HTTP prueban cableado, no inferencia live. Los unitarios de
ToolRuntime no sustituyen pruebas negativas específicas del matcher Host;
los demás límites y comandos reproducibles están en el acta.

## Checkpoint de detención

Este checkpoint describe la detención anterior. La instrucción posterior del
usuario reanudó la construcción el 2026-10-18; no convirtió los fallos en aceptación.

Core `be2948f` y Coder `270126a` compilan; el consumidor aprobó focos de contratos
SDK/adapter/transporte, pero no existe aún aceptación Electron del workflow.
Los arreglos de catálogo/disclosure Core `3a6ff35`/`23e2fc3` no habilitan por sí
solos procesos escritores: su contabilidad de mutaciones requiere otro contrato.
No se publica ni cierra este slice por pasar un build. Ver el hallazgo posterior
en [el acta de comandos](../validation/m9-native-command-handoff-20261018.md).

## Alcance de la corrección de procesos escritores

El primer vertical de procesos mutadores requiere un checkout Git y aislamiento
Strong real. La raíz original conserva los claims y la autorización; la ejecución
se realiza sobre una copia privada sin escritura al common-dir Git original.
No se copian secretos ni archivos ignorados del usuario. Los outputs se inspeccionan
incluyendo nuevos archivos ignorados: gitignore no exime la contabilidad.

La publicación debe reutilizar las garantías de M7: imágenes protegidas, límites
de tamaño y archivos, topología segura, preimages CAS y validación de todo el lote
contra MutationLedger antes del primer cambio. Cada Create/Patch/Replace/Delete/Move
se acepta sólo cuando los techos Session/modelo, lecturas previas y presupuestos lo permiten.
Delete/Move denegados o con Ask sin resolver rechazan/difieren el lote completo;
una aprobación específica vigente permite evaluarlos bajo los mismos límites.
La autorización genérica del proceso no sustituye esa aprobación. No hay
fallback silencioso en workspaces no Git ni cuando Strong no esté disponible.
Esta sección conserva el contrato general. El vertical de extensión v1 descrito
abajo ya tiene implementación y validación local; no acredita todos los canales
de procesos escritores.

### Vertical de extensión escritor: dos acciones con aplicación automática autorizada

Decisión consolidada del usuario del 2026-10-21, implementada y validada
localmente el 2026-10-22 según [el acta](../validation/m9-isolated-extension-publication-20261021.md).
La publicación requiere la CI del incremento. Reutilizar el control de autonomía/permisos que ya existe, sin añadir
un selector de publicación, perfiles paralelos ni formularios nuevos de alcance.

| Permisos elegidos por el usuario | Comportamiento |
|---|---|
| Normales | Ejecutar genera una propuesta; aplicar el lote completo es la segunda acción. Las aprobaciones necesarias se agrupan por lote. |
| Elevados/autónomos | Ejecutar genera, valida y publica la propuesta como una sola operación continua, sin preguntas ni otra llamada al modelo. |

El Host deriva el comportamiento del perfil User humano existente
(`Autonomous` para el recorrido automático), el workspace, las reglas y los
límites vigentes. El usuario no configura rutas, operaciones ni duración de
nuevo por cada extensión o modificación. Las excepciones se gestionan desde los
controles de permisos existentes. CLI/TUI y OmniCoder comparten la decisión
canónica del Host y su revisión. Cambiar permisos no inicia trabajo ni aplica
propuestas anteriores. Al retirarlos, la próxima autorización usa el perfil
actual; una aprobación antigua no conserva autoridad retirada.

El proceso se ejecuta Strong sobre una copia privada, sin escritura al checkout
ni al common-dir originales. La copia excluye secretos e ignorados de entrada;
la inspección de salida incluye archivos nuevos ignorados y valida topología,
alcance y límites. El Host conserva una propuesta protegida con procedencia
Extension propia, sin fabricar ownership Git.

La aplicación consume el lote inmutable mediante el aplicador común. Resuelve
las operaciones exactas antes del permiso y valida el conjunto completo antes
del primer cambio. Conserva lectura efectiva del modelo, tokens/versiones,
historia durable, presupuestos, fence, pre/postimágenes y recibos. La lectura
interna del arnés no concede lectura al modelo. Ejecutar un proceso no basta
para autorizar la publicación.

En automático no se abren diálogos ante falta de permiso, Deny, conflicto o
límite: la operación informa su estado y conserva la propuesta válida si existe.
El usuario puede intervenir por iniciativa propia desde sus controles habituales.
La fuerza de aislamiento Strong/Weak, la categoría del modelo, un probe o los
privilegios OS del proceso no sustituyen la elección humana de permisos.
Este vertical conserva Strong; no añade fallback Weak silencioso.

Los recibos de proceso y publicación son separados. Consultar o reconectar no
inicia trabajo; una continuación usa la fase durable comprobada y no relanza un
proceso incierto. Una publicación interrumpida exige reconciliación y no promete
atomicidad global del filesystem. El aplicador deriva operaciones nativas por
destino y comprueba el lote completo bajo fence. La configuración User vigente
se vuelve a leer; una política anterior al proceso no conserva permisos retirados.
El resultado al modelo incluye el estado de publicación y la causalidad durable
agrupa la subacción en la conversación y el cliente. El aplicador autoriza el
intent Reconcilable real y no amplía grants a lotes futuros.

La aceptación exige proceso Strong real, allow/deny, validación conjunta,
conflictos/límites y recuperación; además, cero preguntas en automático,
perfil elevado que lo activa sin otra selección, retirada de permisos y cambio
de perfil pasivo. Luna alto acreditó build sin errores/advertencias; Luna bajo
aprobó 86 casos focales sin fallos ni omisiones, incluidos siete caminos de lote
con AppContainer real y el siguiente request al proveedor. El acta conserva los
comandos y el alcance de esa evidencia. Este vertical publica texto completo
con reemplazo atómico y directorios padre existentes; no anuncia PatchOnly
localizado. El supervisor v2 trusted conserva su acceso OS declarado en
ADR-0061, fuera del confinamiento Strong de este canal.
No se anuncia todavía paridad de process.exec/shell.exec,
wrappers generales, procesos persistentes ni ConPTY.

**Corrección acordada M9-L-MUT-01 (2026-10-18), implementación parcial:** la
normalización Host de write existente a edición por diff está publicada y la
[CI del checkpoint `e84949c`](https://github.com/juancgomezm-s/OmniCore/actions/runs/39517765699)
está aprobada. La continuidad durable de borrado/movimiento seguido de recreación
a reemplazo contra preimagen CAS está publicada en main `b81b3c2` en el
[bloque del 2026-10-20](../validation/m9-durable-file-history-20261020.md);
su acta registra validación local y CI aprobada antes y después del merge. No hay prohibición general
de write ni de borrar/recrear. El contrato exige preservar
lectura previa efectiva del modelo, identidad/historial entre Lanes y reinicios,
permisos y presupuesto del cambio completo. La lectura del arnés no concede
lectura al modelo ni se trocea el diff para evitar un límite. Las operaciones
compuestas se preparan aisladas y se valida todo antes de publicar; una
publicación parcial/incierta exige reconciliación con sus recibos y preimages.
El contrato es [ADR-0044 §5.1](0044-politica-de-modelos-y-mutaciones.md#write-edit-normalization)
y su [workflow/aceptación M9](../plans/m8-m10-user-localization.md#m9-l-mut-01).
Es condición de aceptación de procesos escritores y del control compuesto de
tools/worktrees; no reabre el consentimiento Verify ya aceptado ni amplía grants.

La recuperación también debe cubrir admisiones de workflow anteriores a Started:
la interfaz especializada conserva una acción humana `command.continue` para ese
estado. Después de Started usa `workflow.continue`. Consultar cualquiera de las
dos vistas no despacha trabajo.

## Consentimiento de sandbox en children — aceptado localmente

La E2E del 2026-10-18 acreditó Explore e Implement, pero Verify terminó con
WeakSandboxConsent resuelto por NoClient. No fue una denegación humana ni un
bloqueo del supervisor. El Host stdio y el consumidor deben presentar y continuar
esa interacción de forma explícita antes de aceptar el recorrido.

La continuación nativa prevista permite sólo `consent_once` y `deny`, ligada al
ToolCallId, argumentos/intención preparada, identidad de ejecutable/cwd,
Session/Run/Task/Lane/Turn e invocación del workflow, con HMAC/CAS. No concede
permiso Run ni reutiliza el consentimiento para otra llamada. Las consultas y
reconexiones no ejecutan procesos; un recibo duplicado no arranca otro proceso.

El corte requiere evidencia positiva de que ningún proceso fue lanzado. El
preflight tipado antes de ToolCallStarted puede informar indisponibilidad Strong;
una excepción genérica NotSupported/UnauthorizedAccess tras intentar el launcher
no demuestra ausencia de efectos y no autoriza un fallback Weak. Una ejecución
incierta queda bloqueada. El consentimiento explícito de Weak no amplía las
políticas Session/modelo, el perfil ni los argumentos autorizados del verificador.
La implementación de rama añade preflight tipado sin efectos, respuesta HMAC/CAS
y proyección específica `interaction.recovery.weakSandbox`. El consumidor sólo
usa esta última para restaurar una solicitud canónica; no interpreta el bloqueo
normal de replay de una invocación activa como fallo de ejecución. La interacción
se persiste con el ToolCallId exacto y los demás IDs del scope original. El
ejecutor de continuación pertenece al worktree hijo, con su directorio y catálogo.
Los focos locales acreditan pipeline, envelope y negociación; las dos E2E de
reapertura acreditan el recorrido UI/proceso/gate y la denegación. `deny` cierra
steering pendiente, Turn, ejecución, Task, Lane, Delegation e invocación en una
sola barrera; no vuelve a ejecutar el hijo para fallarlo. Se revalida el recibo
exacto, autoridad, causación y ausencia de efectos abiertos. `consent_once`
mantiene reprepare y validación física del binding Implement→Verify.
Un ACK de consentimiento aceptado
cuyo inicio posterior no se puede demostrar sigue bloqueado, sin reintento
automático. No existe una garantía exactly-once general.
