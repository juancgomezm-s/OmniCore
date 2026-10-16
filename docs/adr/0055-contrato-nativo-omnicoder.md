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

## Consecuencias y trabajo pendiente

M9-J hace operativas las consultas actualmente negociadas y acciones ya construidas, incluido el
catálogo real en una vista útil para tools/skills/context y un SDK de producción que reutiliza el
Host. No cierra el acceso funcional completo. La siguiente cobertura debe coordinar y construir
contratos mutadores tipados para crear/abrir/renombrar/compactar Sessions; modelos y rutas reales de
Core; `InteractionRequest`/Questionnaire; `command.invoke`, activación de skills y acciones de
memoria; y controladores de plan/lanes/worktrees. ConPTY, capacidad prevista en M9, y otras partes
de M10 siguen inexistentes y no se anuncian como disponibles.
