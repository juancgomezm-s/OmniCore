# ADR-0060: autorización Orchestrate y recuperación nativa del workflow

Estado: construcción reanudada por el usuario el 2026-10-18. Se valida el
recorrido integrado del workflow; la atribución de mutaciones de procesos al
ledger es otro pendiente explícito. Este bloque todavía no se ha publicado.
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

Pendiente: Electron→Host→hijos/tools/verificador sobre proyecto temporal real y
cierre del gate; continuación de una etapa durable sin duplicar
delegación ni efecto, lectura/replay sin autoejecución y rechazo de scope/revisión
obsoletos. El caso CLI anterior no acredita el nuevo consumidor. Fixtures HTTP
prueban cableado, no inferencia live. Accepted con interacción hija pendiente
requiere el consumidor de esa interacción; no se declara resuelto por mostrarlo.

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
contra MutationLedger antes del primer cambio. Sólo se aceptan Create/Patch/Replace
cuando los techos Session/modelo, lecturas previas y presupuestos lo permiten.
Delete/Move que requieran Ask o estén denegados rechazan el lote completo; la
autorización genérica del proceso no sustituye la aprobación específica. No hay
fallback silencioso en workspaces no Git ni cuando Strong no esté disponible.
Esta sección fija el alcance previsto; todavía no acredita su implementación.

La recuperación también debe cubrir admisiones de workflow anteriores a Started:
la interfaz especializada conserva una acción humana `command.continue` para ese
estado. Después de Started usa `workflow.continue`. Consultar cualquiera de las
dos vistas no despacha trabajo.

## Consentimiento de sandbox en children — construcción pendiente

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
Esta sección fija el contrato en construcción; no declara implementado el flujo.
