# Aceptación real de comandos y herramientas con Luna y Qwen

Estado posterior: [continuación con timeout ampliado, terminal y regresión](closure-live-and-terminal-20261012.md).
Los cortes de las secciones siguientes son históricos; no representan la última campaña Qwen.

## Resultado actualizado y reparaciones

Corte de evidencia: 12 de octubre de 2026, 05:24 UTC; 11 de octubre, 23:24 en Ciudad de México.
Rama `codex/tui-omega-animations`. Reparaciones: `89e4f7d` (cuestionarios y guard de sesión)
y `ca39560` (ayuda CLI). Main permanece en `fc6f8e0`, limpio y sin push.

**Luna produjo llamadas reales y resultados verificados para los 14 contratos BuiltIn de
herramientas**, dentro de escenarios acotados. No equivale a certificar todos los flujos del
Host: las generaciones de mailbox ocurren fuera de la generación gestionada del hijo.
**Qwen sigue sin un caso de herramienta certificado**: catálogo disponible, generación sin respuesta.

Los nombres y esquemas provienen de OmniCore.Tools y HostTools. No se añadieron herramientas
de producto para aprobar pruebas. Se conserva llamada, argumentos, identificador, resultado
y continuación del proveedor. El protocolo de llamadas no prescribe nombres universales
para filesystem, planes o agentes: estos son contratos existentes de OmniCore, no nombres
inventados por el ejecutor. Los comandos slash/CLI no son herramientas nativas por aparecer
en la ayuda.

### Herramientas ofrecidas y último resultado

El catálogo compuesto de la prueba general ofreció 13 descriptores: todas las filas siguientes
excepto worktree.integrate. La integración se ofreció aparte con su coordinador Host real.
Esto no implica que cada modo del producto exponga simultáneamente ese catálogo.

| Herramienta | Resultado comprobado con Luna |
|---|---|
| filesystem.read | Auto; contenido real y retorno al modelo |
| filesystem.list | Auto; entradas esperadas y retorno |
| search.text | Auto; marcador encontrado y retorno |
| reference.resolve | Auto; referencia y contenido reales |
| filesystem.write | Auto; bytes exactos y retorno |
| filesystem.patch | Auto; postimagen y línea ajena preservada; token suministrado por la fixture |
| artifact.read | Auto; lectura autorizada del CAS y retorno |
| process.exec | Auto; proceso real, salida y retorno; consentimiento once legítimo y auditado |
| shell.exec | Auto; comando fijo inocuo, salida y retorno; consentimiento once legítimo y auditado |
| user.ask | Auto entre dos tools; SingleChoice, MultipleChoice con Other y FreeText; publicación, reapertura, respuesta Host y retorno |
| plan.propose | Auto; propuesta correcta; no acredita aplicación durable del plan |
| mode.propose | Auto; propuesta correcta; no cambia autoridad por sí sola |
| worktree.integrate | Auto; bytes, manifest CAS y Completed en repo temporal; HEAD/índice preservados y retorno |
| core.agents.mailbox.receive | Selección exacta; binding/identidad reales, entrega Host, un ACK y marcador devuelto por Luna |

Mailbox acredita contrato e integración, **no elección autónoma ni generación gestionada del
hijo**. Las herramientas generales usaron selección automática sobre el catálogo y objetivos
dirigidos. La política de fixture sólo admite el efecto previsto. No son pruebas de todos
los permisos, recovery o usos libres posibles.

### Comandos presentados al modelo

Los 45 nombres se contrastan con HumanCommandAliases.Entries. La última pasada reconoció
**45/45 nombres**, pero generó argumentos inválidos para `skills list`: 13/14 acciones
Cliente ejecutadas por el driver. La anterior reconoció 44/45 nombres y ejecutó 14/14 acciones
Cliente. No se deben combinar como una pasada ficticia de 45 ejecuciones aprobadas.

CLI significa ejecución real solicitada por Luna mediante process.exec; Cliente significa
ClientActionHandler y OmniServer invocados por el driver con los argumentos del modelo;
Interpretado significa sólo reconocimiento, no ejecución del flujo.

| Comando | Resultado de ejecución disponible |
|---|---|
| help | CLI y Cliente |
| exit | Cliente; salida solicitada |
| preferences | Interpretado; no navegación TUI |
| tools | CLI y Cliente |
| models | Interpretado; no selector TUI |
| model | CLI list, policy set y policy show sobre modelo sintético |
| tasks | Cliente |
| events | Cliente |
| context | Cliente |
| commands | CLI y Cliente |
| permissions | CLI list y Cliente |
| skills | CLI aprobada; última clasificación skills list inválida; Cliente anterior aprobado sin argumentos |
| memory | CLI y Cliente: session, sólo lectura |
| extensions | CLI y Cliente: consulta |
| cancel | Cliente; RunCancelled real de fixture |
| interrupt | Cliente; RunAwaitingInput real de fixture |
| agents | Interpretado |
| delegate | Interpretado; no iniciado por el modelo |
| agent | Interpretado |
| join | Interpretado |
| mode | Interpretado |
| runmode | Interpretado |
| reasoning | Interpretado |
| animation | Interpretado |
| sidebar | Interpretado |
| fuente | Interpretado |
| login | Interpretado; cuenta intacta |
| logout | Interpretado; cuenta intacta |
| doctor | Interpretado; comprobación offline separada, no ejecución por Luna |
| trust | CLI; sólo confianza de fixture |
| resolve | Interpretado |
| session | Interpretado; no purga real |
| worktree | Interpretado; no confundir con la tool worktree.integrate |
| ask | Interpretado; ayuda reparada |
| act | Interpretado; no turno Act desde este comando |
| plan | Cliente |
| explain | CLI sin barra; reporte de contexto, no lectura de README ni llamada de modelo |
| orq-auth | Interpretado; no autorización concedida por el modelo |
| explore | Interpretado; alias sin barra |
| resume | Interpretado |
| fanout | Interpretado |
| verify-journal | CLI; journal real generado por sim |
| audit | CLI purge --dry-run; auditoría preservada |
| gc | CLI --dry-run; blob candidato preservado |
| tui | Interpretado; no control TUI real |

También se ofreció y ejecutó `sim`, fuera del inventario de 45 alias. Las **16 invocaciones
CLI distintas** son help, /tools, /commands, /skills, /memory session, permissions list,
trust, extensions o /extensions, model list, model policy set, model policy show, sim,
verify-journal, explain, gc --dry-run y audit purge --dry-run. Todos tienen efecto y retorno
comprobados entre dos pasadas complementarias: 14 en la principal y dos en mantenimiento.
Los JSON conservan failed/incomplete por los rechazos intermedios; no fue una pasada perfecta.

### Reparaciones realizadas y pendientes reales

- Producto: user.ask alineó esquema y descripción con restricciones semánticas, incluidos
  opcionales nullable y valores no vacíos. QuestionnaireValidator no se relajó.
- Producto: RequireSession ahora devuelve un código canónico localizado en es/en/fr/it.
- Producto: ayuda distingue ask (modelo), explain/explore sin barra (reporte local) y
  /explain (solicitud al modelo), en cuatro idiomas.
- Fixture: PowerShell dependía de PATHEXT, eliminado por el entorno saneado. El wrapper usa
  dotnet.exe absoluto y PATHEXT=.EXE sólo en el hijo; no se cambió la allowlist de producción.
- Fixture: se corrigieron equivalencias válidas de argumentos slash y gc; audit compara
  ahora el contenido completo inmediatamente anterior, incluidos registros de pasos previos.
- Modelo: conserva errores corregidos tras rechazos y skills list inválido en la última
  clasificación. No se modifica el producto para fingir que un argumento inválido era válido.
- Pendiente: flujos TUI, autenticación, mantenimiento destructivo y demás comandos sólo
  interpretados no están certificados como ejecuciones por el modelo.

Qwen respondió HTTP 200 al catálogo usando TLS fijado y credencial existentes, con el modelo
correcto presente. Una generación mínima, sin tools y con máximo solicitado de 16 tokens,
agotó 30 s sin respuesta HTTP. La prueba streaming previa agotó 45 s sin cabeceras/eventos.
No es un bloqueo de sandbox demostrado: falta diagnosticar servidor/cola/inferencia. No se
reiniciaron ni reconfiguraron servidor, modelo, TLS o cuenta; tampoco se sustituyó el modelo.

### Flujo solicitado de Luna a Qwen y despertar de Luna

La auditoría corresponde a OmniCore; queda confirmar si “OmniCoder” nombra esta TUI o la
aplicación WPF independiente. **El flujo completo todavía no está implementado ni certificado.**

1. delegation.create es una acción confiable del usuario, no una tool del principal
   (ADR0046, DelegationAdmission y HostTools). No se puede afirmar que Luna la invocó.
2. El worker nativo usa el modelo/proveedor/ruta resueltos por OmniCliRuntime. AgentProfile
   permite native o claude-code, no selección explícita de Qwen para un worker nativo.
3. mailbox.receive espera un mensaje, no 60 segundos. Contar números en texto no garantiza
   tiempo real: hace falta una espera cancelable y medida por reloj monotónico.
4. ExecuteDelegation persiste AgentExecutionCompleted, AgentResultProduced y DelegationReturned;
   la TUI muestra estado, pero ese recorrido no inicia automáticamente otra generación de Luna.
5. Luna/codex no admite el límite de salida usado por el cálculo conservador de presupuesto
   del hijo. La guarda rechaza un techo desconocido antes de llamar al proveedor. No se
   debe inventar un límite ni desactivar esa guarda. Qwen además necesita responder a chat.

El siguiente diseño debe reutilizar Delegation, SupervisionBinding, AgentExecution,
AgentResultProduced, DelegationReturned, WakeRequest y políticas existentes. Exponer
delegación al modelo exige decidir contrato y autoridad, no publicar tools ficticias.
La aceptación deberá probar: petición autorizada de Luna, ruta Qwen fijada por Host,
al menos 60 s medidos, saludo real, resultado durable y exactamente una continuación de
Luna sin nuevo mensaje humano, con cancelación, duplicados y reapertura cubiertos.

### Evidencia nueva

Rutas bajo TestResults; UTC interno de cada JSON es la referencia temporal (algunos nombres
de archivo usan etiquetas manuales). Logs y fixtures se preservan localmente, sin secretos.

- live-luna-core-roundtrip-20261012T0504.json: 9/9; UTC interno 04:53.
- live-luna-process-repair-20261012T0439.json y live-luna-shell-repair-20261012T0440.json.
- live-user-ask-repaired-20261012T0456/user-ask-luna.json; log 4/4 (tres offline, uno real).
- live-worktree-roundtrip-20261012T0456/worktree-integrate-luna.json; log 1/1 real.
- live-mailbox-host-final-20261012T0508/: 1/1 real; scope limitado explícito.
- repair-focal-regression-20261012T0508.log: 50/50, incluidos ocho guards de sesión.
- cli-explain-help-root-verification.log: 1/1, cuatro locales sin proveedor.
- live-acceptance-optin-final.log: cuatro casos offline aprobados y tres reales omitidos
  correctamente sin opt-in; las omisiones no se cuentan como certificación.
- live-qwen-pinned-diagnostic-20261012T0504/qwen-pinned-minimal.json: timeout real.
- live-luna-command-final-20261012T052130.json: 45/45 nombres; skills list inválido.
- live-luna-command-documented-20261012T0502.json: 44/45 nombres; 14/14 acciones Cliente.
- live-luna-native-cli-complete-20261012T051841.json: 14 efectos/retornos y dos pendientes.
- live-luna-native-maintenance-20261012T052057.json: GC y audit aprobados; conserva rechazo previo.

Reproducción en OmniCoreLiveAcceptance: --case core-tools --auto-select, --commands,
--native-cli --allow-fixture-process o --case native-cli-maintenance --allow-fixture-process;
los modos reales requieren --model luna --allow-live. --self-test y --self-test --case
native-cli son offline; el segundo también ejecuta ayuda CLI en fixture sin modelo.
No repetir Qwen bajo el mismo bloqueo sin nueva evidencia del servidor.

## Primera campaña conservada como historial

**Todo lo que sigue documenta la primera campaña de las 03:48 UTC. Sus fallos, pendientes y
restricciones no sustituyen el resultado actualizado anterior.** Se conserva para que las
reparaciones y los intentos originales sean auditables, no para sumarlos como éxitos nuevos.

## Objetivo y criterio de éxito

Comparar `gpt-6-luna` y `qwen38-27b-abl-v9-best-q4kxl` usando las herramientas reales de
OmniCore y una matriz reproducible de comandos. Una afirmación del modelo no demuestra que
haya ejecutado una acción: se requiere una llamada estructurada, validación y resultado
observable. Esta evaluación dirigida no equivale a certificar todos los trabajos posibles.

Base de producto: `286698d`, rama `codex/tui-omega-animations`. No se modifica main.
Las pruebas usan datos y repositorios temporales, no el proyecto ni la configuración personal.
El usuario autorizó reutilizar las conexiones existentes de Luna y, después, Qwen local.

## Conexiones verificadas sin inferencia

Comprobación offline del 12 de octubre de 2026, 03:00:48 UTC:

| Modelo | Conexión | Contexto configurado | Salida máxima configurada |
|---|---|---:|---:|
| gpt-6-luna | ChatGPT, perfil codex, cuota incluida; sesión vigente | 272000 | 2048 |
| qwen38-27b-abl-v9-best-q4kxl | API compatible OpenAI, servidor local existente, modo Attach | 32768 | 8192 |

Ambos descriptores declaran llamadas nativas a herramientas. La declaración es una
precondición, no un resultado de aceptación. Se conservan endpoint, TLS y límites existentes;
no se inicia, reinicia ni reconfigura el servidor local. No hay sustitución por otro modelo.

## Separación de superficies

1. **Herramientas del modelo.** El proveedor recibe sus esquemas; devuelve argumentos y el
   runtime los valida, prepara, autoriza y ejecuta. La evidencia identifica el catálogo,
   restricciones del escenario, llamada recibida y efecto.
2. **Comandos del usuario.** Slash, CLI y controles TUI se resuelven en Client/Host. No se
   convierten en herramientas del modelo porque aparezcan en la ayuda. Se verifica su ruta
   real cuando es segura, y se distingue análisis sintáctico de ejecución completa.
3. **Acciones protegidas.** Autenticación, consentimiento y operaciones destructivas no se
   aprueban por una respuesta del modelo. Las pruebas negativas deben mantener esa frontera.

## Diseño de casos de herramientas

La matriz ejecutable debe enumerar el catálogo real y señalar cualquier herramienta sin caso.
Los casos dirigidos no implican que la herramienta esté disponible bajo todos los modos o
perfiles de permisos. Las extensiones ajenas instaladas no se ejecutan automáticamente.

| Herramienta | Trabajo de prueba | Evidencia requerida |
|---|---|---|
| filesystem.read | Leer un archivo con un marcador exclusivo de la fixture | Llamada nativa y contenido exacto leído |
| filesystem.list | Enumerar una carpeta con entradas conocidas | Entradas presentes y ausencia de rutas ajenas |
| search.text | Encontrar un marcador en el directorio temporal | Coincidencia del marcador comprobada |
| reference.resolve | Resolver una referencia a un archivo de la fixture | Referencia válida y contenido observado |
| filesystem.write | Crear un archivo pequeño solicitado explícitamente | Archivo y bytes esperados, sin cambios fuera de la fixture |
| filesystem.patch | Aplicar un cambio mínimo con el token real preparado por el escenario | Contenido final correcto y línea ajena preservada; no prueba el flujo de lectura previa del modelo |
| artifact.read | Leer un artefacto preparado en un CAS temporal | Referencia autorizada y contenido exacto |
| process.exec | Ejecutar un programa y argumentos inocuos permitidos | Proceso real, salida esperada y código de salida |
| shell.exec | Emitir un marcador mediante un comando fijo permitido | Salida exacta; no ejecutar texto arbitrario generado por el modelo |
| user.ask | Formular una pregunta de texto libre | Esquema y validación semántica; la publicación/respuesta humana requiere otra prueba Host |
| plan.propose | Proponer start sobre un ítem simbólico | Declaración devuelta; sin afirmar que se aplicó a un plan durable |
| mode.propose | Solicitar un cambio de modo | Propuesta y frontera de aprobación; sin autoaprobación |
| core.agents.mailbox.receive | Leer un mensaje de un buzón de prueba | Identidad y destinatario correctos; contexto de agente explícito |
| worktree.integrate | Aplicar una propuesta revisada en un repositorio temporal | Identidad, versiones, efecto y reconciliación reales; si falta preparación, no aprobado |

## Estados del informe

- **Probado:** llamada y efecto comprobados dentro del escenario indicado.
- **Fallo del modelo:** no solicita la herramienta adecuada o emite argumentos inválidos.
- **Fallo de ejecución:** la llamada válida llega al runtime pero la operación falla.
- **Bloqueado:** falta una precondición de autenticación, disponibilidad, sandbox o autoridad.
- **No expuesto al modelo:** acción exclusiva de Client/Host; no es un fallo del modelo.
- **No ejercitado:** existe un diseño, pero falta evidencia ejecutada; no cuenta como aprobado.

Las pruebas de rechazo correcto se identifican como tales: no prueban el camino exitoso de
una operación destructiva. Se registran los límites efectivos de llamadas y tiempo del runner.
Las muestras dirigidas se ejecutan primero con Luna y luego con Qwen, conservando los mismos
objetivos y criterios. No se almacenan tokens de autenticación ni cabeceras secretas.

## Resultados de aceptación

Informe cerrado: 12 de octubre de 2026, 03:48 UTC (11 de octubre, 21:48, Ciudad de México).
La comparación con Qwen está bloqueada por disponibilidad/tiempo de respuesta, no aprobada.

**Luna: 10 herramientas probadas, una fallida y tres bloqueadas, sobre 14 herramientas.**
Además se comprobó una frontera de comandos no ofrecidos como herramientas. «Probada»
significa que cumplió su escenario dirigido; no que esté certificada en todos los flujos del Host.
**Qwen: ningún caso completado y verificado**, tres timeouts, un caso interrumpido y el resto
sin iniciar. No existe todavía una comparación completa de capacidad entre ambos modelos.

| Herramienta | Luna: último resultado evaluable | Qwen local |
|---|---|---|
| filesystem.read | Probada; contenido y devolución al modelo | Timeout de 90 s |
| filesystem.list | Probada; entradas de la fixture | Timeout de 90 s |
| search.text | Probada; marcador encontrado | Timeout de 90 s |
| reference.resolve | Probada; contenido resuelto | Interrumpida al detener el cliente propio |
| filesystem.write | Probada; bytes exactos | No iniciada |
| filesystem.patch | Probada; postimagen y línea ajena preservada | No iniciada |
| artifact.read | Probada; contenido del CAS real | No iniciada |
| plan.propose | Probada; devuelve propuesta, no aplica un plan | No iniciada |
| mode.propose | Probada; devuelve propuesta, no cambia autoridad | No iniciada |
| worktree.integrate | Probada; integración real en repositorio temporal | No iniciada |
| user.ask | Fallida; cuestionario rechazado semánticamente | No iniciada |
| process.exec | Bloqueada por consentimiento de sandbox | No iniciada |
| shell.exec | Bloqueada por consentimiento de sandbox | No iniciada |
| core.agents.mailbox.receive | Bloqueada; falta binding del buzón Host | No iniciada |

### Primera pasada real: Luna y después Qwen

Luna ejecutó 14 invocaciones de generación: nueve herramientas con efectos verificados,
un control de frontera de comandos, tres casos bloqueados y dos herramientas aún no
ejercitadas en ese ejecutor. El contador «10 éxitos» del JSON incluye el control de frontera;
**no son diez herramientas con efectos**.

Qwen agotó el plazo de 90 segundos en `filesystem.read`, `filesystem.list` y `search.text`.
La cuarta prueba, `reference.resolve`, había empezado cuando se detuvo el cliente de pruebas
propio (PID 43368, creación 03:25:48.987780 UTC) a las 03:30:46.280618 UTC. No se detuvo ni
modificó el servidor. Los restantes casos no se iniciaron. Esto impide evaluar su capacidad:
un timeout no demuestra que el modelo sea incapaz de usar una herramienta.

El ejecutor inicial no contaba los timeouts para su corte por tres errores consecutivos;
por eso el corte fue manual y el JSON agregado de Qwen quedó vacío. Se preservaron los JSON
individuales ya terminados. Tampoco se conserva el punto exacto alcanzado dentro de lectura
(solicitud inicial o devolución del resultado); no se atribuye ausencia de llamadas al modelo
a partir de ese registro incompleto. La corrección del corte queda en el ejecutor reutilizable,
sin repetir llamadas a Qwen bajo el mismo bloqueo.

### Diagnósticos complementarios de Luna

`user.ask` no pasó la validación semántica. En el intento instrumentado de las 03:40:31 UTC,
Luna solicitó la herramienta y el JSON pasó el esquema, pero `QuestionnaireValidator` rechazó
el cuestionario con `EmptyOptionLabel`, `EmptyOptionId`, `InvalidOtherTextLength` e
`InvalidSelectionBounds`. La preparación terminó en `Rejected`, sin publicar una interacción.
Esto demuestra un desacople en ese caso entre lo admitido por el esquema y las restricciones
del cuestionario; no demuestra que Luna nunca pueda usarlo. No se probó un ciclo de reparación.

Los intentos anteriores se conservan: el primero fue frenado por una restricción demasiado
estrecha del escenario; el de las 03:35 fue clasificado como bloqueo Host antes de comprobar
la preparación. Se corrigió el ejecutor para separar `ToolSchemaValid` de
`QuestionnairePreparationValid`; el resultado semántico posterior es el válido para el resumen.
No se modificaron ni relajaron los validadores del producto.

`core.agents.mailbox.receive` fue solicitado por Luna con argumentos válidos. El runtime
devolvió `CapabilityRefused` porque este ejecutor no tiene un buzón Host asociado. No creó
mensajes ni confirmaciones de lectura: es un bloqueo de la fixture, no un aprobado de mailbox.

`process.exec` y `shell.exec` también recibieron llamadas válidas de Luna, pero fueron
rechazadas con `WEAK_SANDBOX_CONSENT_REQUIRED`. No se concedió consentimiento ni se redujo
la protección para convertir el rechazo en un éxito.

`worktree.integrate` terminó aprobado a las 03:47:10 UTC: Luna devolvió los identificadores
y la ruta exactos de una propuesta real; `ToolRuntime` y `WorktreeIntegrationHostCoordinator`
aplicaron el cambio. Se verificaron bytes, manifest de protección en CAS y estado `Completed`;
HEAD e índice permanecieron intactos. Todo ocurrió en un repositorio temporal distinto de
OmniCore y de `main`. La prueba focal registró **1 ejecutada, 1 aprobada, 0 omitidas**.

Se conservan dos intentos preparatorios sin inferencia: un filtro de pruebas seleccionó cero
casos y otro encontró la configuración aislada de la suite, sin modelos. Se corrigió sólo el
test opt-in para recibir rutas de conexión explícitas, existentes y locales, sin desactivar el
aislamiento global de los demás tests. No se cuentan esos intentos como aprobados.

En total se iniciaron **18 invocaciones lógicas de generación con Luna**: 14 en la pasada
principal y cuatro complementarias (dos cuestionarios, mailbox e integración). No son
necesariamente 18 solicitudes HTTP: el adaptador admite reintentos y no se capturó un contador
de transporte. El máximo de 16 del runner rige por invocación del ejecutor, no para el conjunto
de la campaña. Luna no recibió `max_output_tokens` explícito porque el perfil codex no lo
admite; 2048 en la tabla inicial es configuración, no un límite efectivamente impuesto aquí.
No se infiere el número exacto de generaciones Qwen del agregado interrumpido.

### Comandos y límites de la cobertura

La matriz offline tiene 397 filas, **no 397 comandos ejecutados contra un modelo**:

- 293 comprobaciones de parser aprobadas: 45 alias en cuatro idiomas (180), 110 casos de
  gramática y tres de argumentos slash.
- 41 comprobaciones de cliente/CLI/inventario aprobadas; incluyen rechazos esperados,
  simulación y operaciones de mantenimiento en modo de vista previa.
- 14 bloqueadas o de uso informativo, por falta de sesión, modelo o repositorio temporal.
- Un fallo de localización reproducido: `/cancel --locale en` devuelve
  «no hay una sesión activa». La operación rechaza correctamente la ausencia de sesión.
- 34 filas de inventario no ejercitadas y 14 acciones de cliente no ofrecidas como tools.

Se verificaron escritura/borrado de memoria de sesión con lectura posterior, selección de
un modelo sintético y `model policy set/show/history/delete` sobre datos temporales.
`model policy list` no es un subcomando implementado; el listado válido es `model list`.
`/tools` mostró 13 herramientas del catálogo raíz; mailbox es una superficie de agente
delegado y se comprueba por separado.

No se ejecutaron login/logout, refresh, cualificación de proveedores, purgas reales,
aplicación de GC, limpieza/descarte/undo de worktrees ni navegación TUI interactiva. La
resolución de sus alias no certifica esas operaciones. No se aprobó ninguna interacción
pendiente ni se alteraron cuentas personales.

El defecto de traducción procede del texto fijo en `OmniServer.RequireSession`
(`src/OmniCore.Host/OmniServer.cs`, línea 3432 en la base). Se registra, no se corrige producto
en este encargo de aceptación.

### Evidencia local preservada

Rutas relativas a este worktree:

- `TestResults/live-luna-20261012T0323.json` y `live-luna-20261012T0323-evidence/`.
- `TestResults/live-luna-user-ask-20261012T0335.json`: intento anterior, cuya clasificación
  de bloqueo queda corregida por el diagnóstico siguiente.
- `TestResults/live-luna-user-ask-diagnostic-20261012T0340.json`: rechazo semántico y códigos
  de validación; sólo forma de argumentos y hashes, sin contenido secreto.
- `TestResults/live-luna-mailbox-20261012T0335.json`: solicitud válida, sin binding Host.
- `TestResults/live-worktree-luna-20261012T0347/worktree-integrate-luna.json` y
  `TestResults/live-worktree-luna-20261012T0347.log`: integración real y resultado xUnit.
- `TestResults/LiveWorktreeAcceptanceTests-offline-test-final.log`: 1/1 aprobado sin modelo.
- `TestResults/live-worktree-optin-skip-20261012T0349.log`: sin opt-in, 1 omitida y ninguna
  solicitud al proveedor; la omisión no se cuenta como herramienta aprobada.
- `TestResults/live-worktree-final-build-20261012T0347.log`: compilación final sin errores
  ni advertencias; `OmniCoreLiveAcceptance-final-self-test.log`: comprobación offline del runner.
- `TestResults/live-qwen-20261012T0326-partial-evidence/`: tres timeouts completos y registros
  de casos inicialmente no ejercitados; el agregado vacío no cuenta como resultado.
- `TestResults/command-acceptance-20261012-032946736-840ed432.jsonl` y `.log`:
  matriz final, con prompts/condiciones/expectativas/evidencia por fila.
- Las pasadas anteriores de comandos y las comprobaciones offline del ejecutor se
  conservan para distinguir correcciones del escenario de defectos de producto.

Son pruebas dirigidas (`tool_choice` exacto), con permisos explícitos sólo para el caso y
operaciones acotadas a directorios temporales. No certifican selección autónoma de herramientas,
permisos del Run habitual, recovery/reanudación ni publicación durable de todas las interacciones.
Sólo lectura tuvo devolución del resultado al modelo en esta pasada. No hubo fallback de
modelo, renovación de credenciales, cambio de TLS ni modificaciones de `main`.

Los JSON y logs quedan locales en `TestResults` (ignorado por Git); los repositorios temporales
también se preservan. El código de los ejecutores y este informe están en la rama de trabajo,
sin commit ni push. No se afirma que las pruebas se hayan repetido contra el HEAD de main.

## Reproducción

Desde la raíz del worktree, compilar los tres proyectos en `tools/OmniCore*Acceptance*` y
`tests/OmniCore.Tests/OmniCore.Tests.csproj` con `dotnet build --no-restore`.

Comprobación offline del ejecutor:

```powershell
dotnet tools/OmniCoreLiveAcceptance/bin/Debug/net10.0/OmniCoreLiveAcceptance.dll --self-test
```

Una llamada real, sólo con autorización para reutilizar la conexión existente:

```powershell
dotnet tools/OmniCoreLiveAcceptance/bin/Debug/net10.0/OmniCoreLiveAcceptance.dll --case filesystem.read --model luna --allow-live
```

Para integración usar el runner xUnit directo (el filtro MTP empleado inicialmente seleccionó
cero casos). El proceso debe recibir `OMNICORE_LIVE_ACCEPTANCE_WORKTREE_MODEL=luna`,
`OMNICORE_LIVE_ACCEPTANCE_DATA_DIR` y `OMNICORE_LIVE_ACCEPTANCE_CONFIG_DIR` apuntando a
los directorios de conexión existentes, y opcionalmente `OMNICORE_LIVE_ACCEPTANCE_EVIDENCE_DIR`.
No son variables persistentes del sistema. Sin la primera variable el caso se omite sin inferencia.

```powershell
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -method OmniCore.Tests.LiveWorktreeAcceptanceTests.Configured_model_requests_and_executes_real_worktree_integration_in_isolated_repo -noColor -noLogo
```

No volver a ejecutar `--model qwen --allow-live` bajo el mismo bloqueo de disponibilidad.
Estos comandos no cambian las autorizaciones ni completan los casos excluidos del alcance.

## Pendientes que estos resultados dejan abiertos

1. Alinear el esquema/descripción de `user.ask` con las reglas semánticas y probar el retorno
   de errores al modelo para corregir sus argumentos. Después, verificar publicación y
   respuesta mediante el Host real, no sólo preparación aislada.
2. Corregir la localización de `RequireSession` y extender la comprobación al resto de guards
   de sesión; el caso inglés fallido no queda excusado por las pruebas de alias aprobadas.
3. Repetir la matriz Qwen cuando su disponibilidad permita completar una llamada. No cambiar
   modelo, TLS, permisos ni servidor para ocultar el bloqueo observado.
4. Verificar mailbox con identidad de agente y buzón reales; ejecutar process/shell bajo un
   sandbox admitido o consentimiento explícito para el entorno. Esta prueba no lo concedió.
5. Probar por separado los flujos interactivos, autenticación y mantenimiento destructivo
   que se excluyeron. El parser, los dry-run y los rechazos esperados no sustituyen esos E2E.

Estos pendientes son una evaluación del alcance no cubierto, no una autorización para cambiar
cuentas, ejecutar purgas o reparar producción automáticamente.
