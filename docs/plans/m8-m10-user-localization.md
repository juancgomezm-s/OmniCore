# Pendientes M8–M10 alineados con idiomas y convenciones de usuario

Revisión inicial del 2026-10-11, ampliada e integrada el 2026-10-12 por petición del usuario. Complementa la fuente de verdad [arquitectura §24](../architecture/arquitectura.md#24-roadmap-m1m10-fuente-de-verdad) y el [plan de preferencias e idiomas](preferences-and-localization.md). Define trabajo restante y aceptación; no declara construido lo que sólo tiene contrato o pantalla. La petición comprende traducciones y convenciones regionales de presentación.

## Estado vigente (2026-10-14)

**`P0-OMNI-UNIFICACION` cerrado globalmente (2026-10-14):** [acta consolidada](../validation/omni-family-homologation-closure-20261014.md), evidencias de los tres productos y aceptación del usuario de OmniCoder Electron, OmniCore CLI/TUI y OmniLlama React, con Svelte fuera de esta fase. El usuario confirma animación remota funcionando. Se levanta el bloqueo de homologación y se autoriza iniciar M9 con un solo worker Luna en alto; los criterios técnicos y de aceptación propios de M8–M10 continúan vigentes. OR5 autenticado sigue pendiente y no se declara M8 cerrado.

**Alcance aclarado por el usuario (2026-10-14): conservar la experiencia y los menús actuales de OmniCore, considerados una buena referencia.** La [matriz U-C01–U-C12](../architecture/arquitectura.md#matriz-ejecutable-de-omnicore--u-c01-a-u-c12-2026-10-14) se usa para contraste puntual, no como doce trabajos ni como una campaña de rediseño. Sólo corregir discrepancias concretas de uso; reutilizar la evidencia existente y completar lo necesario para el cierre cruzado. Los cuatro idiomas de Core se conservan. U-C12 mantiene su alcance técnico M9 y no queda construido por homologar pantallas.

Registro de base de los donantes (2026-10-14): OmniCoder mantiene su referencia de producto Electron/WebUI; OmniLlama tiene entregas de composer, fuentes/resultados/comandos/trazas y animaciones/Markdown, con revisiones posteriores registradas en su propio plan. Sus perfiles por conversación fueron retirados. Ese registro no obliga a trasladar funciones a Core ni a sustituir su experiencia CLI/TUI. Si una corrección concreta reutiliza una pieza, consultar la revisión y el alcance vigentes de esa pieza.

**Contribución de OmniCore a P0 cerrada (2026-10-14, Windows):** revisadas U-C01–U-C12,
conservando menús/navegación y explicitando diferencias de capacidad. Corregidos el recorte
del prompt ordinario y la pérdida del borrador ante rechazos/referencias no disponibles;
evidencia y resolución de cada fila en [el acta local](../validation/omnicore-homologation-closure-20261014.md).
No queda una campaña UC-A–UC-D de Core por ejecutar. El cierre global de las tres aplicaciones
está registrado por separado en el acta consolidada. El pendiente propio de M8 es OR5 autenticado;
el usuario autoriza iniciar el alcance M9 previsto conservando ese pendiente explícito. No construir
otra interfaz ni anticipar M9/M10 para aparentar paridad.

Las cinco brechas posteriores a `084d5a8` están corregidas en `75a5a67`: discovery MCP, latch de validación, hook terminal al aceptar, restauración de memoria olvidada y techo de permisos de skills. `09ad2ba` conserva la autoridad del workspace cuando su ruta durable queda redactada; prueba reapertura y rechazo de otro checkout sin exponer secretos. Build de solución sin advertencias ni errores; suite integral: 4,505 correctas, 0 fallos y 11 omisiones explícitas (incluye arquitectura). OR5 autenticado sigue pendiente de configurar la cuenta del usuario; M8 no se declara cerrado. La evidencia vigente tiene precedencia sobre los checkpoints históricos; [acta M8](../validation/m8-extensibility-20261011.md).

## Cortes históricos de integración y dependencias

Los checkpoints siguientes describen su fecha de captura; el estado vigente anterior y la matriz M8 posterior tienen precedencia.

**Continuación 2026-10-13:** main `358f481` incorpora el cierre de preferencias,
controles de harness y safeguards de borradores/discovery. Esa base se reutiliza
al cerrar M8; sus controles por modelo no sustituyen la selección/resolución
de tools de extensiones aún pendiente. El canal de actualizaciones GitHub,
solicitado por el usuario, se añade como requisito nuevo de M10 en
[su plan](github-updates.md), sin ampliar M8 ni declararlo construido.

**Estado actualizado 2026-10-12:** traducción/preferencias `a865446` integrada por `70cd180`; Session Memory `c7d96bd` integrada por `a029012`. `4091936` incorpora como antecedentes las ramas históricas restantes manteniendo sin cambios los árboles src/tests de esa integración. Las filas siguientes conservan el corte anterior para trazabilidad; el mecanismo de idiomas ya está disponible en main, y los nuevos contratos de la sección de herramientas siguen pendientes de construcción. Instalación npm desde Git se entrega como canal framework-dependent Windows sobre el producto integrado; no cierra M8–M10.

| Base revisada | Estado y tratamiento |
|---|---|
| `main` en `fc6f8e0` | Integra extensiones/comandos/skills/Session Memory hasta `7c69645` y reemplazos `Supersedes` de `709f17b`, además de TUI/OAuth. Revisar historia antes de importar otro checkpoint: esas entregas no se reconstruyen. |
| Rama M8 en `c7d96bd` | Añade Memory visible en la solicitud al modelo, actualización de `LastUsedAt` y rechazo de reparse points. El worker reporta 9/9 focales, 62/62 arquitectura y build sin advertencias/errores; evidencia del árbol de rama, todavía no del árbol combinado con preferencias. |
| Rama `codex/tui-omega-animations` en `89e4f7d` | Contiene resolver/persistencia de idioma, recursos es/en/fr/it, centro de preferencias, `HumanCommandAliases`, recursos externos y actas de validación. Implementación disponible en rama; integración a main y aceptación interactiva real pendientes al corte. Las actas de rama distinguen pruebas automatizadas y escritorio real. |
| M9/M10 | Reusan Protocol/Client multi-target, proyecciones de agentes y bases de aislamiento/undo. Sus transportes, memoria multiscoped, bindings/palette y evaluación final no quedan construidos por el rediseño de preferencias. |

Tras integrar las bases de preferencias y los deltas independientes de M8, conectar cada backend restante a los contratos existentes de idioma, recursos y acciones. No reiniciar P1–P6 por cada hito ni repetir íntegramente regresiones ya válidas sin un cambio que lo justifique. La coordinación mantiene un worker a la vez y bloques amplios; incorporar las herramientas al roadmap no inicia su implementación.

Se conserva el alcance Windows y los diferidos de plataforma de las actas existentes. Cuatro idiomas no equivalen a soporte Linux ni a cuatro proveedores cualificados. La integración y distribución autorizadas se validan sin pruebas pagadas ni cambios a preferencias reales.

## Contrato compartido para toda función nueva

1. **Idioma efectivo:** español por defecto; es/en/fr/it; precedencia `--locale > OMNI_LOCALE > ui.locale User > es`. Reusar el resolver y la fachada Host existentes en la rama de preferencias. Project/Workspace no imponen idioma al usuario. Mostrar origen y override; guardar usa validación, revisión y escritura atómica.
2. **Recursos propios:** claves estables por dominio y argumentos tipados, paridad de placeholders, plurales y traducción real. Host produce significado/código/clave y datos; Client presenta, sin dependencia inversa desde Engine/Domain. Cada función incluye ayuda, progreso, éxito, vacío, bloqueo, cancelación, error y recuperación. Un recurso nuevo no usa ramas binarias es/en.
3. **Invocaciones:** extender `HumanCommandAliases` y el catálogo compartido, conservando comandos canónicos en cualquier idioma. Reservar alias Core, detectar ambigüedad con extensiones y alias User, y mantener scope/trust. Normalizar sólo posiciones de gramática declarada; rutas, JSON, texto de usuario, valores libres y argumentos tras `--` permanecen literales. Menú, palette, CLI y atajo llegan al mismo ActionId/CommandId autorizado.
4. **Herramientas y paquetes:** traducir nombre visible, descripción, capacidades y estados; toolId, esquema, nombres enviados al modelo y preferencias persistidas siguen canónicos. Reusar `uiResources` opcional y su validación/límites de la rama de preferencias. Un paquete externo sin recursos muestra envoltura localizada y texto original identificado. No ejecutar discovery remoto ni enviar contenido a traducción por abrir una pantalla.
5. **Contenido e integridad:** no traducir recuerdos, instrucciones de skills, prompts, respuestas, archivos ni historial importado. La preferencia de interfaz no cambia el idioma solicitado por el usuario al modelo ni la elegibilidad de una skill por lenguaje de programación. Localización no altera fingerprint de autoridad, hashes, journal, reglas de confianza ni decisiones de seguridad; un cambio efectivo de recursos de un paquete conserva la invalidación de consentimiento por hash.
6. **Cambio de idioma:** reconstruir presentación preservando borrador, foco, selección, scroll, sesión e interacción pendiente. M9 conserva independencia por cliente. No reiniciar una ejecución, volver a enviar una orden ni responder automáticamente a Ask por cambiar idioma. Un formulario que deba diferir el cambio lo explica.

## Herramientas previstas y ampliaciones autorizadas (2026-10-12)

**Checkpoint 2026-10-13:** discovery `tool.search` y lectura paginada están
integrados en main `e23f722` y tienen evidencia focal en
[el acta M8](../validation/m8-extensibility-20261011.md).
Los estados de las filas distinguen estas entregas de lo aún pendiente.

**Checkpoint 2026-10-13 `cd6f12f`:** aliases User y `toolPreferences` integrados
desde settings y CLI/TUI hasta el catálogo/planner/fingerprint. Las sustituciones
protegidas usan consentimiento durable por origen, destino y descriptor/paquete;
un grant ordinario no lo reemplaza y otra Session vuelve a pedir Ask. Evidencia:
35/35 focales y 1/1 adicional de aislamiento. Quedan hooks, MCP/web, skills completas,
candidatos/promoción/uso Memory y OpenRouter. El usuario configurará su conexión
OpenRouter cuando esté lista la interfaz; su aceptación autenticada permanece pendiente.

**Checkpoint `00fc8d4`:** hooks Project consentidos están conectados a siete puntos
de Turn (`BeforeModel`, `AfterModel`, `BeforePermission`, `BeforeTool`, `AfterTool`,
`ToolFailed`, `BeforeCompletion`), con redacción, `HookApplied`, fingerprint y rutas
CLI/TUI. Evidencia: filtro de 18/18 y dos casos adicionales de revisión (2/2), sin
repetir la suite amplia. La denegación bloquea también la compresión por metamodelo;
una denegación posterior a una tool conserva su outcome y detiene llamadas siguientes.
Los puntos Session/Run/Task/Lane/Compact/Validation y el validador real permanecen
pendientes de M8 y se muestran deshabilitados como `UnsupportedPoint`.

**Checkpoint `3273026`:** OpenRouter OR1–OR4 integrados: conexiones, catálogo y
registro elegido, créditos con scopes y antigüedad, CLI/TUI y status line.
Evidencia focal offline 21/21 y revisión del root; no se repitió la suite amplia.
OR5 con la cuenta del usuario permanece pendiente de configuración. M8 sigue
abierto por MCP/web, memoria Candidate/Policy/Ask y
cableado restante de hooks, además de la validación final integrada.

**Checkpoint `00c7618`:** ciclo de skills BuiltIn/User/Project/Workspace/Session,
selector con presupuesto, índice y `skill.load` integrados en consumidores reales.
Cada Turn recompone contexto, tools, fingerprint y validators; las automáticas
tienen lifecycle y supresión al liberar. `provides.skills` requiere consentimiento
de la extensión padre y su hash entra en la identidad activa. Extensiones User y
Project comparten Extension API Strong con consentimiento separado. WorkflowCommand
enlaza al flujo compilado Core; recursos declarativos y Knowledge conservan
procedencia. Contributors/validators externos reciben metadata, sin API de archivos.
Lanes delegados usan su packet canónico y no alteran la selección del principal.
Evidencia: 48/48 focales del bloque, 15/15 de correcciones y 1/1 de aislamiento;
root revisó sin repetir campañas. `completion.gate` permanece pendiente para el
cableado final H. La validación real del launcher, arquitectura/build y suite
integral no se acreditan por estos fixtures. M8 sigue abierto por E/F/H y OR5 real.

El usuario incorpora las recomendaciones de la comparación y una lectura para modelos con visión al alcance M8–M10. Esta tabla distingue antecedentes de requisitos nuevos. Los nombres nuevos son contratos propuestos, pendientes de implementación y revisión; incluirlos en el roadmap no acredita un backend ni una herramienta visible al modelo.

| Hito | Herramienta / mejora | Antecedente | Alcance y aceptación añadidos |
|---|---|---|---|
| **M8** | `tool.search` y catálogo bajo demanda | **Ya previsto explícitamente:** spec §36–37, ADR0014/0037; integrado en main `e23f722` con evidencia focal de discovery/schema/ejecución/reanudación. Se conserva ese ID en lugar de inventar `tools.discover`. | Recuperar descriptores por intención, incorporar sólo tools elegibles al siguiente ToolPlan y registrar el conjunto efectivo en fingerprint. No descubrir secretos ni conceder permisos al cargar. Modelo pequeño puede usar Direct; Discovered respeta visibilidad, precedencia y máximos. Probar descubrimiento→schema→llamada real y rechazo de fuente no confiable. |
| **M8** | Rango de líneas/paginación de `filesystem.read` | **Ampliación nueva** de read existente; integrada en main `e23f722`, con páginas, hash completo, redacción y terminadores originales. `artifact.read` ya tiene paginación y se reutiliza donde proceda. | `offset`/`limit` acotados, numeración y continuación explícitas, redacción y token de versión del archivo completo conservados. Una página nunca se presenta como archivo completo ni habilita escritura sobre una versión distinta. Probar lectura consecutiva, Unicode, límites y cambio del archivo entre páginas. |
| **M8** | Búsqueda/lectura web como tools MCP | **MCP ya previsto; integraciones web concretas nuevas.** | Incorporar un conector configurable para búsqueda y fetch con URL/procedencia, límites de resultados/tamaño/tiempo, redacción y permisos de red por destino. Reusar el adaptador MCP; recursos externos son datos. Abrir preferencias/listar no consulta red. Probar destino denegado, redirect fuera del techo, error/timeout y resultados acotados sin llamar servicios reales por defecto. |
| **M8** | `memory.propose`, `toolPreferences`, hooks y MCP base | **Ya previstos**, no se contabilizan como herramientas nuevas descubiertas por el reporte. | Conservan el backend y consumidores/criterios M8 anteriores; candidatos y Ask durables, preferencias consumidas por planner y hooks sólo restrictivos. |
| **M9** | Control de procesos persistentes/interactivos | **Runtime/PTY ya contemplados** en ADR0015; esta revisión los asigna a M9, antes diferidos. La superficie de herramientas de control es nueva. | Contratos `process.start`, `process.read`, `process.send_input`, `process.stop` sobre IProcessRuntime/IProcessHandle; handles ligados a Session/Run/ownership, argv literal y permisos/sandbox del proceso original. Salida con secuencia/budget, cierre de stdin y cancelación del árbol. ConPTY Windows cuando se solicite terminal. Reinicio/lease perdido muestra proceso incierto; no relanza efectos automáticamente. Probar desde un cliente separado, incluyendo cancelación y acceso a handle ajeno rechazado. |
| **M9** | **`media.read`: lectura visual de archivos** | **Nueva herramienta solicitada por el usuario**, adicional a la lectura textual. | Leer PNG/JPEG/WebP de una ruta autorizada y entregar contenido visual tipado al modelo, con MIME, dimensiones, hash/CAS y límites de bytes/píxeles/imagenes. Hacer visible sólo ante capacidad efectiva de visión y rutas admitidas. Cablear ToolResult→ModelRequest→serializadores OpenAI/Anthropic/compatible cuando soporten imágenes, y DTOs Protocol/Client con referencias acotadas; no simular visión devolviendo base64 como texto ni agregar OCR silencioso. Probar bytes/corrupto/MIME, path/secretos/reparse, modelo sin visión, rechazo de proveedor incompatible, replay y presupuesto multimodal. El costo del esquema se mide separado de los tokens de imagen y ambos se muestran como medidos/estimados/desconocidos. |
| **M10** | `code.navigate` / `code.diagnostics` mediante LSP | **Símbolos ya conceptuales** en perfil Explorer de la spec §15; **mecanismo LSP y contratos concretos nuevos**. | Definiciones/referencias/símbolos y diagnóstico por documento/versión mediante servicio LSP de extensión/OmniCoder. No acoplar Core a un lenguaje. Procesos LSP requieren propiedad/capacidades, salida acotada y revisiones; resultados obsoletos identificados. Probar un servidor fixture y consumidor real, error/timeout y liberación, sin atribuir grep a navegación semántica. |
| **M10** | `filesystem.apply_patch` con varias operaciones y preview | **Ampliación nueva**; write/patch, preimágenes, locks y recovery ya construidos se reutilizan. | Propuesta con conjunto acotado de archivos/hunks, preview y versiones de cada archivo, autorización de todas las rutas antes de iniciar y journal/preimágenes para recuperación. Serializar escrituras; conflicto detiene antes de efectos; fallo parcial queda reconciliable sin borrar cambios posteriores del usuario. Conservar `filesystem.patch` exacto para compatibilidad. Probar conflicto, crash intermedio, cancelación, límites de mutación y preview→aprobación→efectos reales. |

Dependencias: cerrar discovery/read/MCP de M8 antes de extender catálogos; M9 conecta transporte/leases y payloads multimodales antes de acreditar `media.read` o controles de proceso; M10 incorpora semántica LSP y parche múltiple sobre esas bases. El lector visual inicial cubre imágenes; renderizado de PDF por páginas o más formatos requiere un bloque M10 explícito de límites/renderizado y no se declara incluido por admitir una ruta. No se amplía el soporte Linux diferido al añadir herramientas Windows.

Cada entrega añade recursos es/en/fr/it, ayuda/aliases humanos del registro compartido, errores/confirmaciones y evidencia Host→Protocol→Client→consumidor. IDs, argv, rutas, esquemas, imágenes y contenido del usuario permanecen originales. Los esquemas nuevos se miden al implementarse; se evita inyectar todos los contratos a todos los modelos. MCP/web, imagen, LSP y texto externo conservan procedencia y autoridad sin convertir su contenido en instrucciones.

## Convenciones lingüísticas y regionales

La selección del catálogo y la cultura de formato son conceptos distintos: `es-MX` puede resolver textos `es` sin obligar a todas las personas hispanohablantes a usar un formato regional único. Al implementar, conservar o resolver explícitamente la cultura efectiva y su origen; no inventar silenciosamente país o zona horaria por el idioma. Este punto es requisito de presentación pendiente, no una nueva clave YAML ya soportada.

| Superficie | Convención y aceptación |
|---|---|
| Vocabulario | Glosario compartido es/en/fr/it para sesión, proyecto, espacio de trabajo, extensión, habilidad, herramienta, permiso, consentimiento y memoria. Explicar estado y consecuencia; etiquetas humanas claras, ID técnico disponible en detalles. El nombre de un producto/paquete se conserva. |
| Cantidades y entrada | Decimal/separadores y plurales según cultura en campos humanos; el valor guardado y enviado es invariante. Pruebas de entrada→guardar→reabrir, valores negativos/fuera de rango y formatos ambiguos sin reinterpretación silenciosa. |
| Dinero y uso | Moneda/unidad explícita; cambio de idioma no convierte importes ni límites. Mantener distinción medido/estimado/desconocido y precisión de límites; no deducir moneda del país ni presentar falta de dato como cero. |
| Fechas y expiración | Instantes durables/wire en UTC; presentación con fecha comprensible y zona efectiva identificable. Expiración, leases y orden se comparan por datos, nunca por texto localizado. Probar cambio de fecha y representación sin modificar el instante. |
| Búsqueda e invocación | Búsqueda de etiquetas tolera mayúsculas y variantes de acentos declaradas. Alias vienen del registro; buscar una etiqueta no la ejecuta. IDs/rutas/hashes no se comparan con reglas culturales ni se deduplican por traducción. |
| Terminal y accesibilidad | UTF-8 sin BOM donde el transporte lo requiere; ancho por celdas, tildes, signos españoles, acentos franceses y apóstrofos italianos. Foco, ayudas y atajos legibles a 80×25 y 120×35, con NO_COLOR/ASCII y sin cortar acciones por etiquetas largas. |

## M8: completar extensibilidad, skills, Session Memory y OpenRouter

**Ampliación 2026-10-13:** cierre completo de OpenRouter asignado a M8: conexión,
interfaz CLI/TUI, catálogo y créditos, con aceptación según [OR1–OR5](openrouter-closure.md).
También son obligatorios en M8 todos los pendientes de skills de ADR-0026; el soporte
Project de instrucciones ya integrado es la base, no el cierre de skills completas.

| Bloque restante | Backend y consumidor real | Entrega de cara al usuario y evidencia |
|---|---|---|
| Fuentes de extensiones — integrado `00c7618` | User/Trusted y Project comparten discovery, identidad de paquete y transporte Strong, con consentimiento separado por origen. Tools, contributors y validators tienen consumidores; `provides.skills` conserva procedencia y hash del padre. | Inspección y estados propios es/en/fr/it; revocar o cambiar el paquete invalida aportes. Contributors conservan metadata; H añade a validators/gates contexto JSON válido, redactado y acotado a 8 KiB, con evidencia reciente y resolución por ID exacto. `completion.gate` tiene consumidor real y exige consentimiento propio. No implica lectura libre del workspace por paquetes. |
| Commands y preferencias de tools — integrado `cd6f12f` | ExtensionCommand→Turn/ToolRuntime conservado. Aliases explícitos User y diagnósticos, persistencia/resolución de `toolPreferences` por ID, planner y fingerprint conectados. Sustitución protegida requiere preferencia explícita y Ask ligado a la identidad; PermissionPolicy/Deny se evalúan en cada llamada. | CLI/TUI y completado reutilizan recursos es/en/fr/it; etiqueta de tool separada de ID. Alias conserva argumentos y un destino desconocido no llama al modelo. Focales verifican consumo del planner, alternativa oculta, consentimiento durable, invalidación por paquete e aislamiento entre sesiones. Validación integral compartida: 4,485 correctas, 0 fallos y 11 omisiones explícitas. |
| Skills completas — integrado `00c7618`, integración verificada | BuiltIn/User/Project/Workspace/Session, selector limitado por HarnessPolicy/presupuesto, índice y `skill.load`, ciclo durable y composición efectiva por Turn. Tools, contexto/knowledge, validators y WorkflowCommand llegan a sus consumidores. | Recursos propios es/en/fr/it; focales acreditan activación/release automática y explícita, presupuesto, integridad/consentimiento y aislamiento del worker respecto del store principal. El workflow inicial enlaza al flujo compilado `core:explore-implement-verify`; no interpreta grafos arbitrarios. Validación integral compartida sin fallos; OR5 permanece separado. |
| OpenRouter — OR1–OR4 integrado `3273026`; OR5 pendiente | Conexión User y credencial protegida, prueba/desconexión; catálogo oficial y registro de modelos; saldo de cuenta y límite/uso de clave cuando el API los informe. Reusa adapter compatible, routing/gasto y DTOs existentes. [Plan OR1–OR5](openrouter-closure.md). | CLI/TUI implementados y 21/21 focales offline. La interfaz ya está instalada; el usuario configurará su clave y avisará para aceptar conexión/catálogo/créditos con su cuenta, sin inferencia pagada. La aceptación autenticada permanece pendiente; abrir una pantalla no consulta red ni autoriza gasto. |
| Hooks — integrado `d158de0` | Los 18 puntos tienen consumidores Session/Run/Task/Lane/Compact/Validation/Turn y terminales. RequireValidation resuelve IDs exactos de validadores/gates consentidos; evidencia durable. Completion gates reales impiden RunCompleted al fallar. | Recursos propios es/en/fr/it y consulta compartida. Capacidades se validan por punto: inicio/terminal Observe; restricción y validación fallan cerrado antes del siguiente efecto. Los terminales observan tras persistir su evento. Child no hereda hooks/autoridad del principal. Verificación integral sin fallos; OR5 autenticado permanece pendiente. |
| MCP y web — integrado `5481ca3`, integración verificada | Wire MCP 2026-07-28; stdio Strong sin red/paths y HTTP acotado al endpoint, confianza User/Project y receipt de primer efecto por Session ligado a identidad efectiva/schema/credencial. Web search/fetch reutilizan el bridge y validan límites, orígenes y cadena de redirects declarada. | Inspección/configuración CLI y vista TUI, permiso con riesgo/destino reales y recursos es/en/fr/it. 9/9 focales y 3/3 focales de corrección, sin omisiones; proceso controlado bajo Strong real. Listar configuración no llama al servidor; descubrir tools al componer Turn sí usa transporte. El Host valida la declaración de redirects pero no impone el egress interno remoto. No confundir este stdio con CLI↔Host de M9. |
| Memoria Session — integrado `d53f37c`, integración verificada | Observation→Candidate→Policy→Promote/Reject/Ask y `memory.propose` conectados. `MemoryPromotion` usa la interacción canónica y decisiones Host; eventos CandidateProposed/Promoted/Rejected/Used durables. Reusa store, dedupe, Supersedes, expiración y contexto existentes, separado de WorkingState. | Recorridos y enums propios es/en/fr/it; contenido/IDs literales. Focales iniciales 25/25 y de corrección 19/19, sin omisiones: reapertura, reemplazos/vencimiento, decisión fuera del top-50, receipt limitado a propuesta y privacidad de proyecciones propias. `MemoryUsed` identifica lo renderizado en una petición con respuesta del proveedor; un desbordamiento antes de llamar no cuenta uso. La heurística inicial sólo observa tres éxitos canónicos .NET build/test en Session. |

Orden: integrar bases → completar commands/preferences/skills → hooks/MCP → cerrar candidatos/uso Session → cerrar OpenRouter OR1–OR5 → integración M8. OpenRouter reutiliza las superficies de preferencias y puede avanzar sin depender del conector web/MCP. Los bloques pueden compartir contratos ya construidos; cada entrega contiene backend, ruta Host/Protocol, presentación y pruebas. Los límites Strong de red/creación de archivos quedan explícitos hasta que el sandbox imponga el techo exacto; ninguna traducción habilita esas capacidades.

La aceptación de memoria incluye el `InteractionRequested` de tipo `MemoryPromotion`
y su resolución canónica (ADR-0034); un estado textual `ask` por sí solo no acredita
esa ruta. Las decisiones se recuperan sobre todo el journal aunque la vista limite
la cantidad de candidatos. Las propuestas sensibles no conservan argumentos brutos
en las proyecciones durables propias; la excepción de receipt en memoria para
`memory.propose` no permite desacoplar argumentos canónicos de otras herramientas.

**Compatibilidad MCP verificada el 2026-10-13:** fijar y registrar la revisión wire
soportada. La [revisión 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports)
usa metadata por solicitud; las revisiones anteriores usan `initialize`. El
[binding HTTP actual](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)
requiere cabeceras de versión/método/nombre y admite JSON o SSE por solicitud.
Una implementación de una revisión anterior declara esa compatibilidad y no mezcla
sus mensajes con los actuales. Fixtures comprueban el dialecto y el rechazo de
revisiones no soportadas; no se acredita compatibilidad universal por hablar JSON-RPC.

**Salida M8:** todos los pendientes anteriores, skills completas y OpenRouter OR1–OR5, además de `tool.search`, read paginado e integración web/MCP cableados; aislamiento/autoridad verificados y recorridos propios es/en/fr/it sin fallback, incluyendo error y cancelación. Skills completas y OpenRouter no quedan diferidos a M9/M10 por disponer sólo de una base parcial. Memoria Project/Workspace/Global continúa en M10. Las pantallas de preferencias ya construidas se amplían al existir el backend; no se duplican.

## M9: Host separado y experiencia entre procesos

**Bloque M9-B revisado e integrado en `main` (2026-10-14; acta**
**[`m9-block-b-20261014.md`](../validation/m9-block-b-20261014.md)**).** `omni host --stdio`,
`StdioOmniClient` y `omni stdio` siguen sobre el runtime persistente real y el contrato v1.0.
Se añade lease OS no expirable por Session, reclamado al morir el proceso; recibos durables hash-bound
con `Completed` / `Unknown`, `command.result` sin reenvío y protección por ID después de purga; y
`events.replay.page` con Session explícita, consulta SQL limitada, high-water fijo, cursor y detección
de gaps. `subscribe` 1.0 conserva íntegramente su semántica anterior. Las páginas ajustan el prefijo
para caber dentro del frame UTF-8 de 1 MiB; un único evento mayor devuelve `frame_too_large`.
El Client conserva las capabilities negociadas y usa legacy replay cuando no se anuncia el paginado.
La prueba de ACK no consumido mata un Host real tras observar el recibo durable, reconecta y verifica
lookup/duplicado sin nuevos eventos. Reservas inconclusas permanecen Unknown y nunca se reejecutan.
Se conservan las rutas in-process y la TUI; el hilo no afirma que M9 entero esté cerrado.

La exclusión se apoya en handles de lock de SO sobre archivos permanentes dentro de almacenamiento
local; verificada en Windows únicamente. La semántica de procesos concurrentes no ha sido validada
en Linux/macOS o filesystems de red. Replay por páginas no es push ni exactly-once. El contrato no
autoriza origen humano externo ni acepta un booleano de humano. OR5 autenticado M8 sigue pendiente en
su milestone separado.

**Bloque M9-C revisado e integrado en ambos main (2026-10-14; rama de origen
`codex/m9-omnicore-consumer`):** supervisor Electron-main + consumidor TypeScript para el wire stdio
1.0 publicado, lifecycle de proceso hijo, reserva Core por workspace, queries/read models, receipts,
replay y Sidecar read-only. Evidencia enlazada desde [acta M9-C](../validation/m9-block-c-20261014.md);
los cambios/validación de producto residen en el repo hermano OmniCoder, no en este árbol Core.
Esto no amplía el contrato wire ni acredita chat/inferencia. Sigue pendiente autoridad humana externa
segura; `media.read` llegando a un proveedor con visión; controles de procesos persistentes y ConPTY;
recorridos de aceptación en cuatro idiomas y verificación Linux. Las respuestas ambiguas se exponen
como `Unknown`; no hay reenvío ciego, lease por tiempo, reconexión push ni garantía exactly-once
indiscriminada. El contrato y los límites exactos están en el README de stdio 1.0 y el ADR-0049.

| Resto de M9 tras M9-B | Construcción | Presentación y aceptación |
|---|---|---|
| Transporte y negociación | **Construido en M9-A/B:** Host real por stdio, framing UTF-8 y cap del frame final, `hello`/versiones/capabilities, queries/read models y replay legacy/paginado. Sin backpressure adicional. | Focales cross-process cubren caracteres es/en/fr/it, incompatible, malformed y oversized. La verificación de OS en este bloque es Windows. |
| Lease y supervisión | **Construido en M9-B:** lock OS no expirable por Session, adquisición antes de mutación y reclaim tras muerte; sin heartbeat por reloj. M9-C agrega supervisor de proceso y reserva de workspace antes de spawn; esa reserva de producto no es el lease Core. | Pruebas de dos procesos/reclaim y lifecycle de child verificadas en Windows local. La UI muestra reserva y desconexión/fallo; falta aceptación de otros OS/filesystems y ownership coordinado con otros productos externos. |
| Reconexión e historial | **Construido en M9-B:** receipt hash-bound, `command.result` sin resend, `Unknown` no ejecutable, cursor/high-water SQL acotados, detección de gaps y tombstone de purge. No es exactly-once general ni reconexión push. Procedencia Native/Imported pendiente. | Prueba mata Host tras persistir receipt con ACK sin consumir, reconecta y confirma misma Session/ACK sin nuevos eventos. Unknown y cursor purgado muestran resultado/gap sin fingir finalización. |
| Clientes y preferencias | **Construido en Core:** CLI in-process y stdio con Client multi-target net8/net10, capacidades consultables, fallback al `subscribe` legacy y goldens wire. **Consumidor M9-C revisado e integrado en OmniCoder main:** TypeScript sin CLR, Host hijo real y Sidecar de lectura. `ui.locale` sigue por Host con su esquema durable existente. | Consumer probado contra Host real en Windows con replay/receipt; revisión e integración completadas; queda aceptación multi-OS/cuatro idiomas y paridad Core completa. Goldens validan wire canónico, no traducción. No almacenar copy traducido en read models. |
| Origen humano y resultado de acciones (U-C12) | **Construido en M9-B:** lookup/dedup con `CommandAck.Outcome/FirstSeq/LastSeq`. Falta definir equivalente externo seguro de `ITrustedUserActionClient` ligado a conexión/ownership/permiso; el marcador in-process no es serializable. | El wire genérico rechaza acciones que requieran autoridad humana; JSON y capabilities no crean grants. Falta acción real autorizada Electron→Host y denegación sin origen válido. |

**Salida M9:** escenarios reales de transporte/lease/reconexión/net8, lectura `media.read` que alcanza un proveedor con visión compatible y controles de procesos persistentes/interactivos, recorridos en cuatro idiomas y JSON/wire invariantes. Reusar la presentación Client y el mecanismo de idioma; no mandar frases ya traducidas por el Host para sustituir claves/argumentos. Negociación de recursos/capabilities y payload visual, si necesitan DTOs nuevos, respetan ADR0013 sin migrar journals por un cambio de copy.

## M10: v1, memoria completa y evaluación

| Bloque restante | Construcción y base reutilizable | Presentación y aceptación |
|---|---|---|
| Memoria multiscoped | Stores Project/Workspace/Global, afinidad/confianza, evidencia entre sesiones, promoción, expiración/reemplazo/olvido y auditoría. Reusar pipeline Session M8; Global siempre Ask. | Vistas y acciones por alcance en cuatro idiomas; explicar qué se conserva, dónde y por qué. Idioma UI se guarda en `ui.locale`, no se infiere/promueve a Global Memory. Recuerdos originales, privados y aislados; solicitudes de olvido y Ask equivalentes en cada idioma. |
| Keybindings y palette | KeyBindingService, `keybindings.yaml`, `When`/Priority/Source, conflictos y protección de interrupt/cancel; palette sobre ClientAction/Command existentes. | Títulos, ayuda, fuente, disponibilidad y búsqueda es/en/fr/it. Binding apunta a ID canónico, no texto traducido; cambiar idioma conserva reasignación. Probar colisiones/mnemónicos y etiquetas largas; acciones de cancelación no quedan sin acceso. |
| Búsqueda y diagnósticos | `session.search`, resultados/procedencia y completar proyecciones de Execution/supervisión/capacidad/ValidationDebt. Bases de `/agents` ya existen. | Buscar sin reescribir contenido ni ampliar visibilidad; snippet original con estados locales. Diferenciar reclamación de evidencia, deuda de validación y verificación real. Vacíos/errores y navegación accesibles en cuatro idiomas. |
| Producto undo y adapter | `/undo` completo sobre compensación/diff/preview/gates M7; terminar adapter OmniCoder sobre M9, sin duplicar transporte. | Alias/menú/palette llega a misma operación autorizada. Preview con efectos/conflictos/consecuencias, confirmación y resultado en cuatro idiomas. Conservar rutas/IDs y cambios posteriores ajenos; idioma no provoca apply ni modifica un diff. |
| Evaluación y cierre | Suite full, calibración, regresión y comparación con Pi; documentar límites de plataformas/proveedores y criterios spec §96. | Informes y ayudas propios localizados, métricas numéricas invariantes y consumo estimado/medido/desconocido explícito. Fixtures deterministas verifican UI; pruebas lingüísticas de modelos, si se realizan, se reportan aparte y no se deducen de traducir catálogos. |
| Actualizaciones GitHub — nuevo | Releases versionadas stable/preview, assets preparados, manifest/procedencia/digests, check/apply/rollback y recuperación según [plan del canal](github-updates.md). Reusar negociación/versiones M9 y gestor de instalación; no convertir main en release estable. | Consulta manual por defecto, compatibilidad y confirmación antes de aplicar, datos de usuario preservados y estados es/en/fr/it. Verificar instalación real, download alterado, crash/rollback y versión en uso; todavía no hay updater construido. |

**Salida M10:** v1 funcional con memoria completa, acciones/teclado/búsqueda/undo y evaluación, navegación/diagnósticos LSP, parche de varias operaciones con preview/recovery y canal de actualizaciones GitHub recuperable; cierre de los recorridos nuevos y existentes es/en/fr/it. P6/P7 cubren la suma del producto: una aceptación previa del centro de preferencias no cubre automáticamente estas superficies futuras. No reabrir funciones cerradas de M6/M7 para reconstruir sus bases.

## Trabajo de TUI después de M10

El frame completo de revisión y operaciones Git queda asignado después de M10 por decisión del usuario del 2026-10-13. [Post M10 Git Review](post-m10-tui-git-review.md) define diff lado a lado/unificado, selección por archivo/bloque/línea, preparar/quitar del índice/descartar, pull/merge y recuperación. No se añade a los criterios de cierre de M8–M10 ni se atribuye al visor histórico TUI-P2.

CLI v1 y su paridad de acciones siguen en M10; la conexión CLI/OmniCoder al Host separado sigue en M9; el bridge MCP y web sigue en M8. Los transportes stdio de MCP y CLI↔Host no son intercambiables. La TUI funcional, traducciones y accesibilidad de las capacidades de cada hito mantienen su aceptación antes de ese bloque; el rediseño de preferencias acordado por separado continúa sin aplazarse automáticamente.

## Evidencia y seguimiento

Ampliar el [inventario único](preferences-localization-inventory.md) por función, acción canónica, estado del flujo, fuente/alcance, claves es/en/fr/it, cultura, datos externos y test. Registrar estados separados: contrato, backend, cableado, presentación, validado en rama e integrado. Evitar porcentajes basados en contar títulos o DTOs.

Cada bloque verifica focalmente: misma acción/permiso para alias y canónico; datos libres intactos; claves/argumentos y cero fallback propio; guardar/cancelar/reabrir; ausencia de efectos al consultar; estados de error reales; recorrido hasta el consumidor. Cambios de persistencia/autoridad exigen sus pruebas de contrato; layout exige frames por idioma y tamaños. Arquitectura y regresión amplia corresponden al árbol integrado y al cierre, con resultados y fallos históricos identificados.

Para M9 añadir proceso real/net8, dos idiomas simultáneos, Unicode, reconexión y lease. Para M10 añadir scopes/Ask, bindings persistidos, búsqueda, undo con cambios ajenos e informe de evaluación. Registrar por separado capturas de driver y aceptación interactiva real. Recursos externos sin traducción son un límite explícito; recursos propios sin traducción impiden cerrar el recorrido afectado.

Referencias: ADR0013/0019 (wire/transporte), ADR0023–0028 (extensiones, comandos, acciones, skills, tools, memoria), ADR0030/0039/0040 (cliente, confianza, localización) y actas de cada integración. La revisión cambia el plan y los criterios de salida; no prueba nuevos backends ni integra ramas por sí misma.
