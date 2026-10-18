# ADR-0060: autorización Orchestrate y recuperación nativa del workflow

Estado: construcción conservada en rama; integración detenida ante un impedimento
transversal de atribución de mutaciones de procesos al ledger. Todavía no acredita el recorrido
integrado ni se ha publicado. M9 sigue abierto; M8 espera pruebas manuales.

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

Pendiente: compilación, contratos y rechazos previos a admisión, cambio de modo
visible con límites reales, Electron→Host→hijos/tools/verificador sobre proyecto
temporal real y cierre del gate; continuación de una etapa durable sin duplicar
delegación ni efecto, lectura/replay sin autoejecución y rechazo de scope/revisión
obsoletos. El caso CLI anterior no acredita el nuevo consumidor. Fixtures HTTP
prueban cableado, no inferencia live. Accepted con interacción hija pendiente
requiere el consumidor de esa interacción; no se declara resuelto por mostrarlo.

## Checkpoint de detención

Core `be2948f` y Coder `270126a` compilan; el consumidor aprobó focos de contratos
SDK/adapter/transporte, pero no existe aún aceptación Electron del workflow.
Los arreglos de catálogo/disclosure Core `3a6ff35`/`23e2fc3` no habilitan por sí
solos procesos escritores: su contabilidad de mutaciones requiere otro contrato.
No se publica ni cierra este slice por pasar un build. Ver el hallazgo posterior
en [el acta de comandos](../validation/m9-native-command-handoff-20261018.md).
