# Preferencias e idiomas de OmniCore

Plan independiente de producto e implementación, acordado a partir de la petición del 11 de octubre de 2026. Responsable de integración y revisión: Codex. Implementación por paquetes: un worker Luna. El objetivo es que una persona encuentre y configure las funciones construidas sin conocer comandos, archivos YAML ni la arquitectura interna, con toda la interfaz propia disponible en español, inglés, francés e italiano.

El rediseño no se limita a cambiar etiquetas. Separa cuentas, conexiones y motores locales; une las preferencias dispersas; da representación a las capacidades recientes de M8; y sustituye la localización parcial por un contrato verificable de extremo a extremo. No declara M8 terminado ni adelanta MCP, hooks o memoria global por el hecho de añadir una entrada al menú.

## Problemas que se deben resolver

1. El subtítulo «Modelos, políticas y cuenta» ocupa una fila similar a una opción. El título, la explicación, el foco y la selección no tienen una jerarquía inequívoca.
2. «Modelos y permisos» mezcla selección de modelo y autoridad operativa. «Conexiones API de providers» mezcla cuentas por suscripción, servidores locales y proveedores con clave. No permite entender dónde configurar cada uno.
3. La cuenta conectada sigue enfocando «Iniciar sesión». Los estados desconocidos no explican si falta configurar, medir o implementar una acción.
4. El menú principal no representa ajustes ya disponibles en otras rutas: panel lateral, confianza, límites, perfiles, permisos y las nuevas extensiones, skills y memoria de sesión.
5. Hay recursos es/en, pero también `Ui(es, en)`, condiciones `locale == "en"`, mensajes ingleses directos, estados técnicos y JSON mostrado al usuario. Añadir dos diccionarios no cubre estas rutas.
6. El idioma `ui.locale` está previsto en ADR0040, pero `UserSettingsYaml` y la lista de claves admitidas de `ConfigLoader` todavía no lo implementan. CLI y TUI tienen resoluciones binarias diferentes.

## Base y límites de integración

La base inicial es `main` y `origin/main` en `e3cae2f`, la TUI en `3589b59` y M8 en `7c69645`. La consolidación se realiza primero en `codex/tui-omega-animations`, sin sustituir archivos enteros con versiones antiguas. Los checkpoints OAuth `ff0430d` y M7 `7293748`/`2fd5403` tienen parches equivalentes en main según `git cherry`; no deben volver a importarse como implementaciones nuevas. Los cambios no confirmados se conservan por separado y se auditan antes de integrar. La evidencia final queda en [el registro de integración](../validation/preferences-integration-20261011.md).

No se modifica ninguna cuenta, credencial, configuración personal, servidor o modelo real al preparar el plan o probarlo. Las pruebas usan almacenes y procesos fixture. La UI futura sí ofrecerá operaciones explícitas al usuario, respetando la autoridad existente. No se hace push ni se eliminan ramas como consecuencia de este plan.

## Arquitectura de información

F4 y el comando de preferencias abrirán el mismo centro de configuración. La navegación tendrá estas categorías, con búsqueda transversal por nombre, descripción y sinónimos en los cuatro idiomas:

| Categoría | Contenido y resultado esperado |
|---|---|
| General | Idioma de interfaz y comportamiento general ya soportado. Español predeterminado; English, Français e Italiano disponibles por su nombre nativo. |
| Cuentas y conexiones | Dos grupos claros: cuentas por suscripción y proveedores por API. ChatGPT y Claude conservan sus flujos distintos. Cada fila explica cómo se autentica y qué se puede hacer. |
| Motores locales | Conectar a un servidor existente o configurar un motor administrado por OmniCore. Endpoint, ejecutable y argumentos donde correspondan; estado, propiedad y límites visibles. |
| Modelos y ejecución | Catálogo, modelo preferido, aliases, rutas, razonamiento y políticas existentes. Diferenciar preferencia para próximos Runs de una operación sobre el Run actual. |
| Herramientas y seguridad | Qué hace cada herramienta, de dónde viene, por qué está disponible o bloqueada, reglas de permisos y confianza del workspace. Consultar no equivale a activar. |
| Interfaz | Panel lateral y widgets, indicadores, animaciones y accesibilidad. Mantener los estilos de conversación y editor ya construidos. |
| Contexto y memoria | Presupuesto y consumo de contexto, memoria de esta sesión y su procedencia. Separar ajustes persistentes de inspección y acciones sobre la sesión. |
| Extensiones y skills | Catálogos, origen, estado, capacidades y consentimiento; activación de skills para la sesión. Comandos asociados con ayuda legible. |
| Diagnóstico y datos | Diagnóstico, fuentes de configuración y mantenimiento disponible. Las acciones destructivas quedan separadas, explicadas y confirmadas. |

No se llenará el menú con interruptores ficticios. Una capacidad no implementada tendrá una explicación y una referencia a su dependencia en la vista avanzada, no una opción que parezca operativa. Los controles del Run, agentes, lanes, join, undo e integración de worktrees siguen siendo acciones operativas; preferencias puede mostrar el estado o un acceso a su vista, sin fingir que son ajustes globales.

### Jerarquía visual

En una terminal ancha habrá navegación de categorías y contenido de la categoría seleccionada. En una terminal estrecha se mostrará una sola columna, con volver y navegación claros. El diseño se medirá en celdas, no reduciendo una fuente por control: la tipografía de una TUI depende del terminal.

- Un solo título y ruta breve, por ejemplo «Preferencias › Motores locales». Quitar el subtítulo redundante actual. Una explicación necesaria será texto secundario, no enfocable y separado de las opciones.
- Paneles por tonos de fondo y acento izquierdo, sin marcos pesados. El foco debe distinguirse de selección y de encabezado, incluso con `NO_COLOR` y modo ASCII.
- Estados junto al objeto: «Conectada», «Falta configurar», «Sin comprobar», «Caducada», «No accesible». «Sin comprobar» no significa conectado ni desconectado.
- Ayuda compacta en el pie. Campos con etiqueta y descripción; botones con verbos concretos. Atajos únicos, Tab y Shift+Tab predecibles, Escape vuelve sin guardar.
- Alturas según contenido, desplazamiento cuando haga falta y anchuras revisadas en francés e italiano. Sin espacios vacíos enormes ni botones perdidos en los extremos.
- Los detalles técnicos extensos se despliegan bajo demanda. JSON no es la vista normal de herramientas, extensiones, skills, memoria ni comandos.

### Guardado y alcance

Cada ajuste mostrará valor efectivo, alcance y origen: Usuario, Proyecto, Workspace o Sesión. Estos nombres se traducen; los identificadores internos no cambian. `locked` y las restricciones de confianza deben explicar por qué una edición no es posible.

Los formularios conservan un borrador y ofrecen Guardar o Cancelar. Guardar valida primero y escribe mediante Host, de forma atómica y con revisión esperada para detectar cambios concurrentes. Debe preservar claves ajenas y referencias a secretos. No se convierte todo YAML a un DTO limitado perdiendo campos. Al cancelar no quedan cambios parciales. Las acciones externas inmediatas —iniciar sesión, probar conexión, arrancar motor— se presentan separadas del guardado y explican sus efectos antes de ejecutarse.

## Inventario funcional y conexiones pendientes

Esta matriz identifica contratos existentes y trabajo nuevo. Cada paquete debe ampliar la matriz con todas sus acciones concretas; una fila general no demuestra cobertura de todos sus estados.

| Función | Código o contrato existente | Trabajo necesario |
|---|---|---|
| Estructura de preferencias | `TuiApp.ShowPreferences`, `OverlayFrame`, `ApplyTheme` | Sustituir lista de cuatro botones por categorías y páginas; modelo de navegación testeable sin Terminal.Gui. |
| Idioma | `Client/Localization.cs`, ADR0040 | Resolver es/en/fr/it, recursos completos, persistencia User `ui.locale`, precedencia común y cambio de interfaz. |
| Cuenta ChatGPT | `TuiAccountHost`, `ChatGptSubscriptionAuthProvider`, catálogo | Reutilizar login, logout y estado; mostrar acciones acordes al estado, no pedir API key. |
| Cuenta Claude | `OmniCliRuntime.LoginClaudeAsync`/`LogoutClaude`, configuración OAuth y almacenes | Añadir fachada TUI tipada y selector de provider exacto. Respetar identidad OAuth configurada, sin inventar cliente ni usar credenciales ajenas. |
| API remota | `ProviderConnectionService`, `ITuiProviderConnectionHost` | Actualmente conectar/descubrir está especializado en Anthropic. Generalizar sólo a familias ya implementadas mediante adaptadores explícitos y capabilities; no llamar al flujo Anthropic para cualquier fila. |
| Motor local | `LocalServerSupervisor`, `ProviderFileYaml.Host`, `ManagedHostYaml` | Editor y fachada Host de lectura/validación/guardado pendientes. Servidor existente y administrado son rutas distintas. Puerto efímero y API key de managed son del runtime, no campos inventados de usuario. |
| Modelos y políticas | `ModelPolicyHost`, picker, `ModelPolicyCommands`, routing en YAML | Unificar acceso sin mezclar selección, cualificación y permiso. Conservar revisión, historial y selección exacta provider/model. |
| Perfil y reglas | `UserPermissionsConfiguration`, `PermissionGrantCommands`, `OmniCliRuntime.Permissions` | Vista explicativa y editor validado donde el contrato permita; separar visibilidad, política allow/ask/deny y grants revocables. No existe un toggle universal que conceda autoridad. |
| Confianza | `WorkspaceTrustStore`, `SetWorkspaceTrusted`, ADR0039 | Entrada visible con alcance y confirmación. Confiar en un workspace no concede consentimiento automático a paquetes. |
| Sidebar | `SidebarConfiguration.Read/Set`, `SidebarPreferencesSnapshot`, `ShowSidebarSettings` | Incorporar página y navegación; reutilizar revisión, scopes, locked y reglas actuales. |
| Animaciones | `AnimationSettingsStore`, `TuiApp.Animations`, `ActivityPresentation` | Integrar sin perder preferencias guardadas. Si se migra `tui-animations.json` a YAML, hacerlo en paquete explícito, con lectura compatible y prueba de migración. |
| Contexto y gasto | consultas context/usage, políticas de contexto, `BudgetSettingsYaml`, cuotas | Separar estimación de tokens, consumo medido y límites. No inventar gasto 0 si falta medición; no refrescar cuotas por abrir la pantalla. |
| Memoria Session | query `memory.session`, acciones add/forget, `SessionMemoryStore` | Vista legible, procedencia, expiración y olvido explícito por trusted user action. Memoria Project/Workspace/Global, promoción y LastUsedAt incompletos no se presentan como disponibles. |
| Extensiones | query extensions, `ExtensionConsentStore`, manifest y tool bridge M8 | Catálogo y consentimiento por capabilities, ligado a id/version/hash. Hash cambiado invalida consentimiento; denegación parent/model/user sigue vigente. |
| Skills | query skills, acciones activate/release, `SessionSkillActivationStore` | Selección de sesión, explicación del contenido añadido al contexto y coste acotado. Discovery actual sólo Project confiable; no simular otros scopes. |
| Comandos | `ClientCommandRegistry`, `CommandService`, catálogo Host | Ayuda, búsqueda e invocación traducidas desde descriptores comunes; precedencia/trust sin cambios. |
| Diagnóstico | doctor, diagnósticos de configuración, mantenimiento/journal/worktree CLI | Presentación humana localizada, detalles técnicos opcionales y enlaces de acción seguros; no reconstruir el motor en la UI. |
| Funciones futuras | ADR de keybindings, hooks, MCP y otras capacidades aún no cerradas | Inventariar como dependencias; no declarar su backend hecho. No añadir un editor que guarde opciones que el runtime ignora. |

### Reglas de cuentas y motores

Una cuenta conectada muestra primero su estado, proveedor y pista enmascarada. «Volver a iniciar sesión», «Cambiar cuenta» o «Desconectar» son acciones secundarias con consecuencias claras. Una cuenta caducada prioriza reconectar. El mensaje «No necesitas introducir una API key» sólo pertenece a la ruta por suscripción.

API y local no se clasifican por el texto del nombre del provider. La clasificación debe proceder de configuración/capacidades del Host. Añadir un proveedor requiere tipo, endpoint permitido y método de autenticación compatible, no sólo pegar una clave. La UI nunca recibe el secreto almacenado de vuelta; editar presenta un campo vacío de sustitución y una pista enmascarada.

Abrir una página no hace login, discovery, prueba de red ni inferencia. Cada prueba indica destino y si puede generar consumo. El motor existente no se detiene desde OmniCore. Para managed, sólo se puede detener un proceso cuya propiedad mantiene el supervisor; una nueva instancia de UI no puede adjudicarse un PID. Reinicios automáticos conservan los límites actuales.

## Contrato de localización completa

### Qué se traduce

Todo texto propio que una persona vea o invoque: navegación, categorías, títulos, etiquetas, ayudas, placeholders, botones, confirmaciones, validaciones, errores, estados vacíos, accesibilidad, indicadores, tooltips, descripciones de herramientas, permisos, diagnósticos humanos, CLI plain, cuestionarios, onboarding, autocompletado y ejemplos de comandos. También progreso de OAuth, estados de motores, mensajes de fallos iniciales de configuración, y copy generado por M8.

Se mantienen originales los mensajes escritos por el usuario o el modelo, archivos, código, rutas, nombres de modelo/provider, identificadores de herramienta, URLs, nombres de productos y datos del protocolo. No son textos de UI a traducir ni se sustituyen dentro de los argumentos. `--json`, hashes, journal y nombres canónicos de API conservan su contrato. El usuario verá una etiqueta traducida además del identificador cuando sea necesario.

Los prompts internos siguen el contrato de ADR0040. No se traducen automáticamente instrucciones de skills ni respuestas históricas. Sí debe auditarse la orden fija «Respond in Spanish» de `CommandService`: el idioma solicitado explícitamente por el usuario prevalece, y la preferencia de interfaz no reescribe contenido ni cambia silenciosamente un Turn en curso.

### Recursos y resolución

- Un catálogo de claves estable, dividido por dominio, con las mismas claves en es/en/fr/it. No añadir nuevas ramas `Ui(es,en)` ni concatenar fragmentos que impidan cambiar el orden gramatical.
- API común de normalización y resolución. Aceptar variantes soportadas como es-MX/fr-FR y normalizarlas al idioma disponible; entradas inválidas generan diagnóstico localizado y usan español de manera explícita.
- Precedencia propuesta y testeada: `--locale` explícito, después `OMNI_LOCALE`, después `ui.locale` User, después español. Mostrar cuando un override de proceso domina lo guardado; no mentir diciendo que el cambio ya se aplicó.
- La configuración temprana debe permitir explicar un error de idioma aunque el resto de settings no cargue. Project/Workspace no pueden imponer el idioma de la persona.
- Placeholders tipados y paridad de argumentos; variantes singular/plural y formato de números/fechas por cultura. Valores de máquina y rutas siguen formato invariante.
- Fallback defensivo compatible con ADR0040, pero una prueba que entra en fallback para una clave propia en una ruta cubierta falla. Paridad de claves no basta: copiar español en el catálogo francés no es traducir.
- Cambio de idioma reconstruye texto/presentación sin perder borrador, conversación, foco, selección, scroll ni operaciones en marcha. Si una página no puede cambiar en vivo con seguridad, debe diferir explícitamente ese cambio; no mostrar una mezcla silenciosa.
- La biblioteca Client mantiene independencia de Host/Engine y de frameworks gráficos. Host emite claves y argumentos; no se agrega una dependencia circular para traducir errores. Actualizar ADR0040 con cuatro idiomas y alias humanos, manteniendo contratos técnicos.
- Consultar y guardar idioma usa DTOs en Protocol y una operación confiable de Host con revisión, no escritura de YAML desde Client. El esquema `src/OmniCore.Host/Schemas/user-settings.schema.json`, las allowlists y los DTOs deben cambiar juntos. `ui.locale` es exclusivamente User, no una nueva hoja sobreescribible desde el proyecto.
- La resolución se hace al entrar al CLI y se propaga a TUI, plain, cuestionarios y diagnósticos. Hoy `--locale` sólo lo interpreta doctor; migrarlo requiere pruebas del resto de comandos, incluyendo argumentos libres y el separador `--`, sin consumir opciones que pertenezcan a procesos hijos.
- Durante P1 los recursos nuevos de preferencias deben estar completos en los cuatro idiomas. El catálogo legado seguirá marcado como parcial hasta P6; ni una entrega interna ni la UI deben anunciar cobertura completa prematuramente. La selección pública de fr/it deberá advertir la cobertura parcial mientras dure esa transición.

### Invocaciones y compatibilidad

Los nombres canónicos continúan válidos en cualquier idioma para scripts y compatibilidad. Se añaden alias humanos localizados para comandos, subcomandos y valores enumerados, incluyendo la ayuda de CLI y slash. Ejemplos candidatos: `/preferencias`, `/preferences`, `/préférences`, `/preferenze`; `/herramientas`, `/tools`, `/outils`, `/strumenti`. Las grafías definitivas y sus variantes sin acentos se fijan en un registro único con pruebas de colisiones.

El parser convierte alias a la misma acción canónica antes de aplicar la lógica existente. No se reemplazan palabras por búsqueda textual dentro del prompt, rutas, JSON, URLs o argumentos libres. Autocompletado y ayuda muestran la invocación localizada y el nombre canónico como referencia. Los flags humanos documentados reciben aliases cuando el parser lo permita, sin retirar los actuales. La localización no altera `toolId`, nombres enviados al modelo, procedencia, precedencia de commands ni restricciones de confianza.

Cada paquete debe incluir pruebas de comandos locales contra built-ins y comandos de extensión, incluyendo colisión, acentos, mayúsculas y parámetros entre comillas. Un alias no puede ocultar un comando más confiable ni permitir una operación antes bloqueada.

### Extensiones y texto externo

Las extensiones distribuidas con OmniCore deben proporcionar los cuatro idiomas para su texto de interfaz. Para paquetes externos se necesita contrato de recursos y diagnóstico de cobertura: mostrar envoltura localizada, identificar el texto original y ofrecer detalles. No se afirmará cobertura completa de textos controlados por terceros sin traducción disponible. Esta carencia se registra como pendiente concreto, nunca se oculta detrás de fallback.

No se enviarán mensajes, contenido de paquetes o secretos a un traductor remoto para completar idiomas. Las traducciones de recursos propios se versionan y revisan. La especificación del manifest debe ser aditiva y preservar compatibilidad con los paquetes M8 actuales; no exige reescribir instrucciones del paquete.

## Registro de superficies para auditar

Luna mantendrá el [inventario versionado](preferences-localization-inventory.md) con columnas: archivo/símbolo, clave o literal, superficie, estado del flujo, alcance, propietario del texto, idiomas, acción canónica, prueba y estado. La línea base cuantitativa identifica candidatos, no certifica cobertura. Debe recorrer al menos:

| Superficie | Entradas iniciales |
|---|---|
| Preferencias y diálogos | `TuiApp.cs`, parciales ProviderConnections/Animations/Tools, OverlayFrame, formularios y mensajes de excepción |
| Conversación y navegación | ComposerView, ConversationView/Presentation, SidebarView, header, status, barras y ayudas |
| Presentación compartida | Localization, ClientActions, ToolsInspection, AgentsProjection, ChangedFilesProjection, SessionSidebarProjection, QuestionnairePresentationFactory, ActivityPresentation, UsagePresentation |
| CLI humana | CliApp, PlainRenderer, PlainQuestionnaireForm, ModelPolicyCommands, WorktreeCommands, MaintenanceCommands, JournalCommands y sus usos/errores |
| Host | OmniCliRuntime, CommandService, TuiAccountHost, TuiProviderConnectionHost, ModelPolicyHost, diagnósticos ConfigLoader, seguridad, OAuth y supervisión local |
| Herramientas | Descripciones builtin, presentación de ejecución/resultados, errores tipados, confirmaciones y catálogo de herramientas externo |
| M8 | ClientActions Skills/Memory, loaders de manifests, catálogos extensions/commands/skills, rechazos de trust/consent y memoria |
| Arranque y compatibilidad | LocaleArgument, OMNI_LOCALE, primer inicio, settings corrupto/ausente, no TTY, NO_COLOR, ASCII y --json |

El inventario no se limita a `src/OmniCore.Cli`. La auditoría seguirá los mensajes desde donde se originan hasta donde se muestran, incluidos fallos raros y fallbacks. Un literal técnico permitido lleva motivo verificable, no una exclusión por archivo completo.

## Paquetes de ejecución para Luna

No comenzar un paquete de UI sobre una base cuyo merge o pruebas estén pendientes. Cada paquete tiene un commit coherente, pruebas focales, registro de cambios y revisión independiente del diff. No se cierran paquetes parciales como si fueran el producto completo.

| Paquete | Entrega | Dependencias y criterio de aceptación |
|---|---|---|
| P0 Base consolidada | Merge TUI/M8 y rescate incremental de pendientes, inventario y línea base de pruebas | Root resuelve conflictos preservando ambas funciones. Luna corrige el defecto de memoria detectado en la auditoría. Main sólo avanza tras verificaciones. |
| P1 Idioma y catálogo de ajustes | Resolver cuatro locales, soporte validado de `ui.locale`, esquema/DTO necesarios, precedencia, registro de categorías y recursos nuevos completos | Pruebas de guardar/cancelar/reabrir, campos ajenos preservados, override visible, scopes rechazados. Conservar APIs es/en existentes durante migración. No declarar catálogo antiguo traducido todavía. |
| P2 Centro de preferencias | Navegación, búsqueda, jerarquía visual, layout adaptable, página General y accesos funcionales a páginas actuales | Capturas de frames reales 80×25 y 120×35 como mínimo; título no enfocable, foco y teclado correctos; idioma no pierde borrador. Ningún botón decorativo sin acción. |
| P3 Cuentas conexiones y motores | Fachadas y formularios de ChatGPT/Claude/API/local, estado y capacidades, guardado seguro | Matriz desconectado/conectado/caducado/error; provider exacto; no secretos; abrir no llama red; managed no detiene procesos ajenos. No quitar las defensas OAuth integradas. |
| P4 Modelos seguridad e interfaz | Modelos/routing/razonamiento y límites soportados, herramientas/reglas/grants, trust, sidebar y animaciones | Reusar servicios existentes; no ampliar permisos ni mezclar scopes; compatibilidad de preferencias; validación y fuente del valor visibles. |
| P5 M8 contexto y diagnóstico | Vistas legibles de extensiones/skills/comandos/memoria, inspección de contexto y mantenimiento disponible | Sin JSON truncado; preserva SessionId, trusted user actions, hash/consent y redacción. No implementar MCP/hooks/memoria global para rellenar el menú. |
| P6 Cobertura de idiomas e invocaciones | Migración de todos los literales propios, aliases y ayudas en es/en/fr/it, contrato de recursos de extensiones | Inventario sin huecos propios, cero fallback en recorridos y paridad de placeholders; comandos existentes y --json compatibles. P6 se avanza dentro de cada paquete, no se aplaza toda traducción al final. |
| P7 Cierre funcional y visual | Recorridos completos, capturas, regresión y documentación de uso | Las cuatro versiones son utilizables; fuentes de configuración, errores y operaciones reales de UI verificadas. Evidencia final enumera explícitamente límites externos. |

### Primer encargo de implementación

Luna comienza por P1 después de recibir el SHA integrado y validado. Ownership inicial: localización compartida, soporte de idioma User, pruebas asociadas y catálogo de categorías; no reescribir simultáneamente TuiApp completo. Debe proponer una partición de recursos compatible con AOT y las referencias de proyectos existentes, implementar el núcleo del resolver y persistencia, y registrar qué cadenas siguen pendientes para P6.

Antes de P2, Root revisa diff, pruebas y la propuesta de navegación. Antes de P3 se comprueba que las fachadas tipadas no mezclan credenciales y que el estado es del Host. Las correcciones de integración se separan del rediseño para poder revisarlas y revertirlas sin perder el resto.

## Verificación proporcional y definición de terminado

No se exige repetir toda la suite por cada texto. Las pruebas de catálogo/parser se ejecutan en cada cambio de recursos; las de seguridad/persistencia se ejecutan al tocar su contrato; las visuales al tocar layout. La suite de arquitectura y la regresión amplia corresponden a la integración y al cierre, con fallos anteriores separados de los nuevos.

1. Toda acción del inventario está representada o tiene una dependencia explícita; no quedan controles que aparenten funcionar sobre contratos inexistentes.
2. Una persona encuentra cuentas y motores locales desde F4 sin memorizar comandos. Puede identificar una cuenta conectada, configurar un endpoint y distinguir guardar de probar.
3. Toda cadena propia tiene traducción real en cuatro idiomas; claves, placeholders y plurales tienen cobertura. Una inspección estática de sitios de salida y un contador de fallback en pruebas complementan la paridad de diccionarios. La revisión humana de traducciones no se sustituye por contar claves.
4. Navegación por teclado, resize, texto largo, tildes, ñ, ü, signos de apertura, acentos franceses y apóstrofos italianos se muestran correctamente. Las etiquetas largas no desaparecen ni se convierten en otras acciones.
5. Cambio de idioma y reapertura conservan preferencias y estado de conversación. Se prueban settings ausente, corrupto, sin permisos, revisión obsoleta y conflicto de edición sin sobrescritura silenciosa.
6. Tests fixture demuestran que renderizar/listar no inicia procesos, red o inferencia, y que cancelar no guarda. Login/discovery/credenciales reales no se incluyen automáticamente en una prueba visual.
7. Invocaciones localizadas y canónicas producen la misma acción autorizada. JSON, wire, tool IDs y scopes conservan sus valores técnicos.
8. Hay capturas verificadas de los frames Terminal.Gui y una comprobación de la aplicación interactiva cuando el entorno lo permita. Las capturas de fixtures no se presentan como capturas del escritorio. Se registran limitaciones reales de automatización.
9. No se cierra el trabajo con «fr/it usan fallback». Tampoco se promete traducir contenido escrito por un modelo o por terceros sin recursos; esos límites se explican dentro de su flujo.

## Referencias de diseño

- [ADR0030 arquitectura del cliente](../adr/0030-arquitectura-del-cliente.md): mantener presentación y núcleo separados.
- [ADR0039 confianza y configuración](../adr/0039-workspace-trust-y-configuracion.md): YAML, scopes, restricciones y locked.
- [ADR0040 localización](../adr/0040-localizacion.md): claves y argumentos, español predeterminado y contrato de máquina.
- [ADR0028 memoria](../adr/0028-memory.md): procedencia, reemplazo explícito y separación de WorkingState.
- [Evidencia M8](../validation/m8-extensibility-20261011.md): capacidades entregadas y pendientes; no confundir un checkpoint con cierre del hito.

Estado inicial: plan preparado; implementación del rediseño y cobertura completa de cuatro idiomas pendientes. Los avances se registran por paquete con commit y evidencia, no mediante porcentajes sin inventario.
