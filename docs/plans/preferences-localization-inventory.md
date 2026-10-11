# Inventario de preferencias y localización

Línea base del plan del 11 de octubre de 2026, sobre la consolidación de TUI, M8 y rescate OAuth. Este inventario controla cobertura: cada grupo se desglosa en entradas concretas al implementarlo. Un grupo pendiente no cuenta como traducido por tener un título en los cuatro idiomas.

## Medición inicial

Auditoría de Luna revisada contra los puntos de entrada por Codex:

| Medición | Resultado | Interpretación |
|---|---:|---|
| Claves del diccionario español | 538 | Catálogo existente, no todas las cadenas visibles |
| Claves del diccionario inglés | 538 | Paridad de nombres con español |
| Claves exclusivas de uno de esos diccionarios | 0 | No demuestra paridad de placeholders o corrección de traducción |
| Catálogos francés e italiano | 0 | Pendientes |
| Líneas candidatas con selección binaria de idioma fuera del resolver | 51 en 13 archivos | CLI 29 en 7; Client 12 en 5; Host 10 en 1 |
| Líneas candidatas con `Ui(` en CLI | 212 en 4 archivos | Pueden incluir declaración del helper; no equivalen a 212 claves definitivas |

Reproducción: en `src/OmniCore.Client/Localization.cs`, delimitar `_es` y `_en` y extraer las asignaciones que coinciden con `^\s*\[".*"\] = `; comparar sus conjuntos de claves. Para selección binaria usar `rg -n 'locale\s*==\s*"en"|_locale\s*==\s*"en"|OMNI_LOCALE.*==\s*"en"' src -g '*.cs'` y para helpers `rg -n 'Ui\(' src/OmniCore.Cli -g '*.cs'`. Los números se recalculan sobre cada entrega; no se convierten en un porcentaje de producto.

Un scan complementario de líneas con salidas directas y literales encontró candidatos en CLI, Client y Host. No se usa como total: incluye datos técnicos, solapamientos y contenido ajeno. El cierre exige clasificar los puntos de salida y seguir su flujo, no sólo reducir una expresión regular.

### Incremento P1A (11 de octubre de 2026)

El catálogo inicial de preferencias se implementó en `src/OmniCore.Client/PreferenceCategoryCatalog.cs`:
9 categorías estables y 27 recursos (título, descripción y ayuda por categoría), cada uno con texto
propio en `es`, `en`, `fr` e `it`. Esto es una cobertura acotada, no una traducción completa de la UI.
Los IDs exactos son `general`, `accounts_connections`, `local_engines`, `models_execution`,
`tools_security`, `interface`, `context_memory`, `extensions_skills` y `diagnostics_data`; cada uno
genera las claves `preferences.category.<id>.title`, `.description` y `.help`.
Las 538 entradas heredadas `es`/`en` siguen en `Localization.cs`; fr/it heredan inglés en esas claves.
`Localization.ResolveDetailed` reporta `RequestedLocale`, `ResourceLocale` y `UsedFallback` para
hacer visible esa transición.

Persistencia del locale ya disponible: `ui.locale` sólo se admite en `settings.yaml` del scope User,
se valida contra es/en/fr/it y se escribe atómicamente con revisión esperada, conservando otras
preferencias. Protocol define DTOs y normalización; Host expone lectura y comando confiable de cambio.
El resolver puro fija precedencia `--locale` > `OMNI_LOCALE` > `ui.locale` > `es`, con fallback a es
ante un valor explícito inválido. En P1A quedaba pendiente conectar esa resolución al arranque;
el incremento P1B descrito abajo ya realiza ese cableado.

Pruebas P1A: `UiLocalePreferencesTests` cubre normalización, precedencia y valores inválidos, 27
recursos en cuatro idiomas, fallback heredado, persistencia User con revisión/atomicidad lógica,
preservación de settings y rechazo de otros scopes; además valida los comandos/query Host confiables.
La suite de arquitectura valida límites Client/Protocol/Host. Inventario de grupos fuera de P1A:
permanecen con el estado inicial indicado abajo; no se marca ninguno como traducido por esta entrega.

### Incremento P1B/P2 y configuración local (11 de octubre de 2026)

`LocaleArgumentParser`, `CliApp` y `OmniCliRuntime` conectan la precedencia al arranque, plain,
cuestionarios, doctor y TUI. Un `--locale` sin valor termina antes de despachar el comando; `--`
protege los argumentos posteriores. El origen predeterminado se diferencia de una preferencia
User realmente configurada. La previsualización y cancelación de General sincronizan el idioma
efectivo sin modificar variables de entorno; un override de proceso permanece vigente.

`LocaleRuntimeResources` contiene 35 claves compartidas con traducción propia en los cuatro
idiomas. `PreferencesUiResources` contiene 119 claves del centro y sus formularios, también
en los cuatro idiomas. Se suman al catálogo de 27 claves de categorías de P1A; no significan
que las 538 claves heredadas ni los helpers binarios estén totalmente migrados.

| Superficie | Entregado en este bloque | Pendiente específico |
|---|---|---|
| F4 y General | Nueve categorías, búsqueda con estado vacío, navegación ancha/compacta al abrir, preview/guardar/cancelar/recargar, origen y override visibles | Cambio de layout al cruzar el umbral de anchura con el menú abierto; recorrido completo NO_COLOR/ASCII y accesibilidad |
| Cuentas/API | Accesos separados y cuenta ChatGPT conectada sin priorizar login | Fachada/página Claude, capabilities por familia, todos los estados y traducción de páginas heredadas |
| Motores locales | Listar, añadir y editar attach/managed; revisión de providers/models, preservación, lease compartido y argumentos literales | Supervisión/ownership desde UI, eliminar configuración, catálogo/modelos locales y recorridos adicionales de error/resize |
| Modelos/seguridad/interfaz | Rutas a políticas, herramientas, permisos, sidebar y animaciones; actualizar modelos requiere acción explícita en preferencias | Formularios completos de routing, límites, reglas, grants y trust; migración del copy heredado |
| Contexto/M8 | Consultas de contexto, extensiones, skills y memoria en visor desplazable; uso medido diferenciado de ausencia de datos | Vistas tipadas completas, acciones de sesión/consentimiento y localización de todos los campos/estados |
| Diagnóstico | Categoría y límite de disponibilidad explicado | Acciones de diagnóstico/datos tipadas; no se simula mantenimiento operativo |

La evidencia agrupada y los fallos corregidos se conservan en
[preferences-batch-20261011.md](../validation/preferences-batch-20261011.md). El registro inicial
siguiente se mantiene para comparar la migración; esta tabla de incremento tiene precedencia
para el estado actual. P3–P7 no están cerrados.

## Registro por superficie

Estado inicial de todos los grupos: pendiente de migración completa a cuatro idiomas. «Parcial» se refiere a comportamiento existente, no a aceptación del rediseño.

| Grupo y símbolos iniciales | Alcance y propietario | Ruta o acción | Estado inicial y prueba exigida |
|---|---|---|---|
| Preferencias `TuiApp.ShowPreferences` | Usuario, copy propio | F4 y comando de preferencias | Cuatro botones, `Ui(es,en)`; navegación, foco y resize |
| Idioma `CliApp.Loc/DoctorLocale`, `TuiApp` ctor | Usuario/proceso, copy propio | Arranque y elección de idioma | Binario; precedencia, variantes, error de settings y propagación |
| Cuenta `ShowAccount`, `ShowLogin`, `TuiAccountHost` | Usuario, copy propio con identidad enmascarada | Login navegador/dispositivo y logout | ChatGPT; estados, cancelación, no secretos y no login al listar |
| OAuth Claude y errores Models/Host | Usuario, copy propio y respuestas externas | `login claude`, `logout claude` | CLI/backend sin página equivalente; mantener selección y defensas OAuth |
| `TuiApp.ProviderConnections`, `ProviderConnectionService` | Usuario, copy propio y metadatos del proveedor | Conectar/probar/descubrir/desconectar | UI genérica; capacidades exactas y estado desconocido explicado |
| `LocalServerSupervisor`, configuración managed | Usuario/runtime, copy propio | Conectar existente, guardar managed, iniciar/detener | Sin página dedicada; propiedad de procesos y ausencia de efectos al renderizar |
| `ModelPolicyHost`, picker y ModelPolicyCommands | Usuario/workspace/Run, copy propio | Selección, políticas, historial y routing | Disperso; edición con revisión, scopes y límites |
| `ToolsInspection`, `TuiApp.Tools`, descriptores | Run/política, copy propio o de paquete | `/tools` y futura página de seguridad | Español/inglés parcial; propósito, descripción, motivo de bloqueo, catálogo sin truncar |
| `UserPermissionsConfiguration`, grants y trust | Usuario/workspace, copy propio | Consultar/reglas/revocar/confiar | Separar política, grants y visibilidad; rechazo por autoridad superior |
| `SidebarConfiguration`, `ShowSidebarSettings` | Usuario/proyecto/workspace, copy propio | `/sidebar settings` | Fuera de F4; revisión, locked, persistencia y fuente efectiva |
| `AnimationSettingsStore`, `ActivityPresentation` | Usuario, copy propio | Preferencias de animaciones | Es/en parcial; preservar configuración y reduced motion |
| `ComposerView`, header/help/status | Sesión, copy propio | Envío, abortar, búsqueda y atajos | Literales; no perder borrador y ayuda compacta en cuatro idiomas |
| Projections y presentación de interacción | Sesión/Run, copy propio con datos de usuario | Preguntas, permisos, planes, tareas, agentes, archivos | Recursos y ramas binarias; todos los estados y errores |
| CLI y renderers humanos | Usuario/proceso, copy propio | Help, comandos, cuestionarios y diagnósticos | Fuera del alcance de cambiar sólo F4; mismos idiomas e invocaciones |
| `CommandService`, registro Client y catálogo Host | Todos los scopes admitidos; copy propio o externo | Comandos, subcomandos, ejemplos y aliases | Compatibilidad canónica, colisiones, trust y argumentos intactos |
| Extensions loaders, `ExtensionConsentStore` | Workspace/proyecto, copy propio y paquete | Listar, consentir/revocar capacidades | Estados ingleses y JSON; consentimiento por hash y confirmación |
| Skills loaders, `SessionSkillActivationStore` | Sesión/proyecto, copy propio y paquete | Listar/activar/liberar | Instrucciones son contenido, no copy; respetar scope y límite de contexto |
| `SessionMemoryStore`, query y trusted actions | Sesión, copy propio y recuerdos del usuario | Listar/añadir/olvidar | Vista pendiente; redacción, procedencia y Supersedes; no traducir recuerdos |
| Contexto, usage y presupuesto | Sesión/usuario, copy propio y datos | Consultar y editar límites soportados | Medido/estimado/desconocido; no confundir cero con ausencia |
| Doctor, config y mantenimiento | Usuario/workspace, copy propio con diagnóstico externo | Validar, inspeccionar, limpiar cuando proceda | Casos tempranos y fallos; acciones destructivas separadas |

## Formato de las entradas definitivas

Cada grupo se expande con: archivo y símbolo, clave estable o literal de origen, pantalla/comando, estado del flujo, alcance, propietario, recursos es/en/fr/it, acción canónica/alias, prueba y estado de revisión. Un texto de tercero se marca como tal y no se cuenta como traducción propia terminada.

Antes del cierre deben quedar cero entradas propias pendientes y cero fallbacks propios en los recorridos. Las exclusiones se justifican individualmente como datos, IDs, código o contenido ajeno. No se permiten exclusiones masivas por carpeta ni copias automáticas de español usadas como supuesto francés/italiano.
