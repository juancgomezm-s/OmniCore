# ADR-0055: contrato nativo versionado para OmniCoder

**Estado:** Aceptada para el bloque M9-J del 2026-10-15; la paridad funcional amplia permanece abierta.

## Contexto

OmniCoder ya consume stdio 1.0 y el Host anuncia un subconjunto de consultas. El Host conserva el
journal, los permisos, los receipts durables de comandos, la elegibilidad de herramientas y el runtime.
La prueba HMAC efímera autentica acciones humanas; no es el receipt. El cliente
necesita una superficie especializada y versionada que describa qué puede negociar y que evite
convertir nombres arbitrarios en llamadas RPC. Reescribir el transporte o dar acceso directo al
renderer duplicaría autoridad y lifecycle.

## Decisión

Se conserva stdio 1.0, el streaming/replay acotado, la lectura de artifact por chunks y la firma
existente de acciones humanas. `native.catalog` publica API/protocolo, IDs lógicos y nombres wire,
resultado esperado, capability negociada, scope, efectos y resumen. El catálogo se genera con el
mismo conjunto negociado que `hello`; una capability indica soporte técnico del Host y no equivale a
un permiso o grant de usuario. Cada consulta se registra explícitamente; alias como `ui.locale` se
normalizan al nombre wire `uiLocaleSettings` y conservan correlación con el resultado canónico.

El SDK del proceso principal ofrece el catálogo validado y DTOs tipados para las vistas de tools,
skills y contexto. Las queries genéricas conservan su payload como `unknown`; los métodos de
conversación, comandos humanos, receipts, stream/replay y artifacts exponen contratos propios. El
callback humano se ejecuta sólo después de que el flujo
existente en main verifica confirmación, grant, Session/Run, capability y firma. El renderer sólo
puede pedir las vistas allowlisted de herramientas, skills y contexto. El SDK decodifica DTOs acotados
y elimina campos internos del contexto antes de cruzar IPC.

La consulta de herramientas refleja las fábricas reales del Host y sus decisiones de elegibilidad;
no habilita una ejecución directa. Las llamadas de herramientas siguen pasando por el Run y la
política `AuthorizeToolIntent`. Consultar skills, MCP o extensiones sólo presenta metadatos aprobados;
no ejecuta probes ni cuerpos. Consultas que puedan refrescar red, como `openrouter.credits`, no se
exponen como queries. Un query con respuesta mayor que 1 MiB devuelve `frame_too_large`, sin truncar
datos y sin cerrar la conexión.

La carga de adjuntos es staging en memoria ligado a la conexión, sin recuperación durable ni
receipt `command.result.lookup`; sólo la conversación posterior persiste la operación con el flujo
durable existente. Begin/chunk/finish no garantizan ejecución exactamente una vez.

Se mantiene compatibilidad con rutas legacy, TUI e in-process. Métodos internos de Core no se vuelven
operaciones nativas sólo porque existan. La seguridad durable por conversación de OmniCoder, aprobada
en §11.M del plan de producto, continúa abierta y requiere un contrato coordinado propio. Los
permisos por tool permanecen en Core.

### Ampliación M9-L: respuestas de elección

Construcción y validación local del slice de Lane raíz registradas en
[el acta de elecciones y recuperación](../validation/m9-native-choices-recovery-20261017.md).
No cierra el resto de M9-L ni los criterios globales de M9.

`command.conversation.interaction.choice.respond.user` negocia la variante
`interaction.respond` con `responseType: "choice"`. Lleva `interactionId`,
`optionId`, `expectedRunId` y `expectedPolicyRevision`; no acepta del renderer
el tipo de interacción, una decisión de autorización ni instrucciones de ejecución.
Core deriva el tipo y las opciones del `InteractionRequested` canónico pendiente
en la Session/Run/Lane correspondiente. La revisión se compara bajo la misma
barrera de admisión que persiste la respuesta y el receipt.
Esta entrega limita la continuación nativa a la Lane raíz; responder una
interacción de otra Lane requiere su correlación y runner propios y se rechaza
hasta que estén construidos. No se presenta como paridad completa de delegación.

La prueba HMAC cubre el command exacto. Main vuelve a comprobar emisor, workspace,
VM, generación, Session/Run y revisión inmediatamente antes de firmar, después de
las consultas asíncronas. La capability expresa soporte técnico, no un grant.
Un rechazo obsoleto conserva la elección y el borrador, sin responder otra
interacción ni reenviar una operación con outcome incierto.

`Permission` debe continuar la llamada original pendiente antes de
`ToolCallStarted`, con el mismo Turn y la autorización exacta de Core. La
rehidratación conserva revalidación de argumentos/intent, política vigente,
presupuestos, sandbox y Barrier; no restaura grants de lectura/versionado desde
el journal. `Started`, incertidumbre o una correlación incompleta impiden una
nueva ejecución. `PlanApproval` conserva los efectos de las opciones de
`RunControlService`: aprobar sin ejecutar no inicia efectos de ACT; la ejecución
aprobada continúa en el mismo Run.
`approve_only` conserva el cierre `Planned` de ADR-0035 y sus gates;
`continue_planning` continúa en Plan. Un `reject` humano queda esperando nueva
dirección sin iniciar otra llamada al proveedor; no equivale al default NoClient.

Un command duplicado devuelve su receipt sin relanzar runner, provider ni efecto.
Consultar/reabrir el historial tampoco inicia ejecución. Una caída después del
ACK y antes del efecto debe permanecer visible como estado pendiente o incierto,
con recuperación explícita segura; no acredita ejecución completada ni una
garantía general de exactly-once. La publicación requiere dispatcher, receipt,
continuación durable y consumidor Main/UI reales; añadir el DTO o mostrar un
diálogo por sí solo no satisface este bloque. La variante questionnaire conserva
su contrato y capability existentes.

La recuperación se consulta mediante `interaction.recovery`, con estados tipados
`none`, `ready` y `blocked`. Una consulta o un replay no despiertan el runner.
`ready` permite una acción explícita `interaction.continue`, negociada con
`command.conversation.interaction.continue.user`; su payload contiene solamente
`interactionId`, `expectedRunId` y `expectedPolicyRevision`, además de `cmd`.
Core deriva opción, causalidad y receipts originales desde el journal; Main no
acepta estos datos de autorización del renderer. La programación del runner
ocurre después de escribir el ACK en stdio.

La ventana recuperable exige respuesta durable Accepted y ausencia de actividad
posterior de continuación. En Permission, un grant o autorización ya persistidos
también impiden reejecutar, aunque no exista `ToolCallStarted`. Un estado de
ejecución `cancelled` solo es elegible si cumple esa misma comprobación canónica.
`blocked` muestra el motivo localizado y únicamente permite cerrar el aviso:
no habilita reintento ni reconciliación. Respuestas históricas concluidas e
interacciones legacy sin receipt nativo no deben generar avisos de recuperación.
Este mecanismo cubre una ventana anterior al efecto; no constituye recuperación
general de procesos ni reconciliación de efectos inciertos.

## Consecuencias y trabajo pendiente

M9-J hace operativas las consultas actualmente negociadas y acciones ya construidas, incluido el
catálogo real en una vista útil para tools/skills/context y un SDK de producción que reutiliza el
Host. No cierra el acceso funcional completo. La siguiente cobertura debe coordinar y construir
contratos mutadores tipados para modelos y rutas reales de
Core; interacciones y continuaciones de Lanes hijas; `command.invoke`, activación de skills y acciones de
memoria; y controladores de plan/lanes/worktrees. ConPTY, capacidad prevista en M9, y otras partes
de M10 siguen inexistentes y no se anuncian como disponibles.
