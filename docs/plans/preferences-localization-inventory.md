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

## Registro por superficie

Estado inicial histórico de todos los grupos: pendiente de migración completa a cuatro idiomas. «Parcial» se refiere a comportamiento existente en esa línea base, no a aceptación del rediseño. La rama de preferencias añadió implementación y evidencias posteriores; consultar el corte de integración del [plan M8–M10](m8-m10-user-localization.md) antes de reutilizar este inventario como lista de trabajo actual.

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

## Ampliación para las funciones restantes de M8–M10

Revisión del 2026-10-11; criterios futuros, no implementación acreditada. Reusar los registros de la rama de preferencias al integrar, y añadir las siguientes entradas al mismo inventario. No reconstruir sus formularios ni contabilizar su traducción como cobertura de estos backends pendientes.

| Hito / función | Estados y acciones que deben cubrirse | Contrato y prueba de usuario |
|---|---|---|
| M8 fuentes/skills | Origen, confianza, incompatibilidad, elegible/activada/cargada/liberada, conflictos y aportes | Cuatro idiomas; contenido original y coste acotado; aporte llega al consumidor; lenguaje de programación independiente de idioma UI |
| M8 commands/toolPreferences | Alias User/localizado/canónico, colisión, selección efectiva, sustitución protegida y Ask | IDs persistidos invariantes, planner/fingerprint efectivos, argumentos intactos y misma autoridad en es/en/fr/it |
| M8 hooks/MCP | Consentir/revocar, destino, capacidad, sin conexión, denegado, error restrictivo/observación, texto externo | Recursos propios completos; redacción; lista sin efectos; fallos/traducción no amplían permisos |
| M8 candidatos/uso Memory | Proponer, promover/rechazar/Ask, conflicto/Supersedes, expiración, uso, olvido y reapertura | Contenido sin traducir; decisión durable, uso real en ModelRequest y auditoría; cambio de idioma no promueve ni duplica |
| M9 transporte/lease | Arranque, versión incompatible, esperando/ocupado, pérdida de supervisor y recuperación | UTF-8/proceso real/net8; clave/args por cliente, stdout sin texto humano; identidad y ownership estables |
| M9 reconexión/historial | Reconectando, después de N, parcial/completo, nativo/importado, error y Ask pendiente | Clientes con distintos idiomas sobre eventos idénticos; sin pérdida/duplicación ni repetición de efectos; historial literal |
| M10 memoria multiscoped | Scope/procedencia, evidencia insuficiente, candidato, Global Ask, expiración, reemplazo y olvido | Recursos/acciones por alcance, privacidad y confianza; no inferir/promover idioma UI como recuerdo Global |
| M10 bindings/palette/search | Disponible/bloqueado, conflicto, reasignar/restaurar, buscar/sin resultados, snippet y navegación | Binding→ID estable; cancelación siempre accesible; acentos/etiquetas largas; no mutar contenido ni ampliar visibilidad |
| M10 undo/evaluación | Preview, conflicto, confirmar/cancelar/recuperar, deuda de validación, consumo/métrica desconocida | Misma operación autorizada; preservar cambios ajenos; informe propio localizado, valores de máquina invariantes y capacidad lingüística del modelo evaluada aparte |
| Todos: cultura y accesibilidad | Plural, decimal/entrada, moneda/unidad, fecha/zona, override, resize, NO_COLOR/ASCII | Roundtrip sin reinterpretación, instantes UTC, ninguna conversión de moneda por idioma, frames es/en/fr/it 80×25/120×35 |

Las entradas definitivas añaden cultura efectiva/origen y estados separados de contrato, backend, cableado, presentación, validación de rama e integración. El cierre de P6/P7 cubre las nuevas superficies por hito; un test de paridad del catálogo antiguo no certifica un mensaje aún no construido.
