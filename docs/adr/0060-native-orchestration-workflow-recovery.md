# ADR-0060: autorización Orchestrate y recuperación nativa del workflow

Estado: recorrido integrado del workflow aceptado localmente el 2026-10-18,
Core `582dc9c` y Coder bundle `1fb3639`/test `b919802`. La atribución de
mutaciones de procesos al ledger es otro pendiente explícito. Publicación y CI
de este incremento se comprueban por separado.
M9 sigue abierto; M8 espera pruebas manuales.

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

## Alcance de la corrección en construcción

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
Esta sección fija el alcance previsto; todavía no acredita su implementación.

### Vertical de extensión escritor: dos acciones con aplicación automática autorizada

Decisión del usuario del 2026-10-21; todavía sin aceptación de implementación.
El proceso Strong se ejecuta sobre una copia privada y sólo produce una propuesta
durable. No recibe escritura al checkout ni al common-dir originales. La respuesta
distingue la propuesta del efecto sobre el proyecto; completar la RPC no publica
los archivos. La copia excluye secretos e ignorados de entrada, mientras que la
inspección de salida incluye archivos nuevos ignorados y valida topología y límites.

Por defecto se exponen dos acciones: ejecutar para generar una propuesta y aplicar
el lote completo. No se exige una aprobación por archivo: si el lote necesita Ask,
la interacción presenta su alcance conjunto; si ya está permitido, no se inventa
una nueva pregunta. Aplicar esa propuesta pasa por el pipeline común. El Host
resuelve su procedencia, contenido y operaciones exactas antes de autorizar la
publicación; el permiso del proceso no sustituye Ask/Deny de archivos. Se conserva
la lectura efectiva del modelo en el scope original y se revalidan versión,
historia, techo y presupuesto bajo el fence. No se fabrica ownership de worktree
ni se vuelve a ejecutar un proceso incierto. Este vertical no anuncia todavía
paridad de process.exec/shell.exec, wrappers generales, procesos persistentes ni
ConPTY.

El usuario puede autorizar además la aplicación automática para un alcance
explícito. Dentro de ese alcance, ejecutar encadena internamente la aplicación y
presenta una sola operación visible; el modelo no necesita otra ronda de
inferencia para coordinarla. Los recibos de proceso y publicación se conservan
separados. Si el resultado queda fuera del alcance autorizado, permanece como
propuesta. En modo de revisión, una ampliación válida se solicita para el lote,
no archivo por archivo, y no vuelve a ejecutar el proceso que ya terminó. En
automático se aplica el comportamiento sin preguntas definido debajo.

La autorización reutilizable identifica workspace físico, extensión/paquete,
rutas o patrones normalizados, operaciones permitidas, límites y duración. La
duración inicial propuesta es Run; Workspace requiere elección explícita y permite
revocación. Se revalidan las políticas vigentes al consumirla; el usuario puede
modificar sus políticas mediante sus controles, pero el permiso del proceso o un
booleano enviado por el modelo no levanta un Deny ni eleva el techo del modelo.
Lectura efectiva, token, presupuestos y conflictos se comprueban en cada lote.

Existen grants Run/Workspace para claims exactos. No acreditan todavía esta
autorización compuesta ni la cobertura de futuros lotes con rutas distintas: hace
falta implementar su contrato explícito y matcher de alcance, auditado y ligado a
identidad/revisión. Consultas y reconexiones permanecen pasivas. Este acuerdo
sustituye la propuesta de exigir siempre dos acciones o siempre una sola.

#### Selector de aplicación y confirmaciones

La UI debe ofrecer un selector explícito **Aplicación de cambios**, disponible
en CLI/TUI y OmniCoder mediante el mismo estado canónico del Host:

- **Revisar propuesta:** ejecutar produce propuesta; aplicar es una segunda
  acción. Las aprobaciones necesarias se agrupan por lote.
- **Aplicar automáticamente:** la elección humana autoriza el encadenamiento
  dentro del alcance configurado. Ejecutar devuelve el resultado de ambas fases
  y no abre preguntas de permisos por llamada, lote o archivo. El Host consume
  la autorización de ejecución/publicación correspondiente; no deja que el modelo
  decida la aprobación ni le exige una segunda invocación para aplicar.

El alcance y su duración se muestran al configurar el selector, con Run inicial
y opción explícita de recordar para Workspace. Cambiar el selector no inicia
trabajo ni publica propuestas anteriores. La configuración tiene revisión y
revocación, se sincroniza entre clientes y sus etiquetas se traducen usando los
recursos existentes; el idioma no modifica la autorización.

En modo automático, si falta autoridad de proceso/publicación, hay un Deny, el
lote excede el alcance o aparece un conflicto/límite, el Host se detiene y reporta
la causa **sin abrir un diálogo de aprobación**. Conserva una propuesta válida
cuando exista; el usuario puede revisar o cambiar el alcance desde los controles
por iniciativa propia. No se relanza el proceso ni se pasa silenciosamente al
modo interactivo. Así, automático significa una sola acción sin preguntas, no
una autorización ilimitada.

La aceptación requiere probar cero interacciones Permission tanto en el recorrido
automático autorizado como en sus rechazos fuera de alcance, además de autoridad
humana del selector, revocación/revisión, recuperación y ausencia de ejecución
por cambiar el modo. El selector y su comportamiento todavía deben construirse.

#### Permisos elevados implican continuidad automática

Ampliación explícita del usuario del 2026-10-21: seleccionar el perfil humano de
permisos elevados activa también la aplicación automática y la política efectiva
de no preguntar. No requiere que el usuario active después otro selector ni
apruebe una nueva modalidad por cada llamada. Ejecutar, generar propuesta,
validar y publicar forman una sola operación visible continua dentro del alcance
del perfil. El selector independiente queda disponible para permisos normales y
para una preferencia explícita de revisión elegida por el usuario.

El perfil de permisos User existente incluye `Autonomous`; la implementación debe
ligar la automaticidad a esa elección humana y a su revisión/alcance efectivo en
Host, compartido por CLI/TUI/OmniCoder. La procedencia de la preferencia distingue
derivación del perfil y override explícito: al retirar elevación se recalcula la
opción heredada; un override automático conserva cero preguntas pero sólo puede
ejecutar dentro de los permisos restantes. Cambiar de perfil no despacha trabajo.

El contrato diferencia amplitud de permisos de fuerza de aislamiento. Strong/Weak
describen aislamiento; un probe, un proceso con privilegios OS, una categoría del
modelo o un booleano de la herramienta no acreditan una elección humana de
permisos elevados. Esta decisión no elimina el aislamiento Strong requerido por
el vertical escritor ni introduce un fallback Weak silencioso.

La política de no preguntar se respeta también cuando el trabajo no se puede
completar: un permiso ausente, rechazo, conflicto o límite produce estado y causa
sin diálogo. La reanudación consume evidencia durable y no relanza el proceso.
La aceptación incluye perfil elevado → automático sin selección adicional,
cero interacciones de aprobación por ambas fases, reapertura/reconexión,
revocación/cambio de revisión y conservación de overrides humanos explícitos.

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
