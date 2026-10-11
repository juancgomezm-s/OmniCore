# Encargo de Luna para preferencias e idiomas

Implementar el [plan independiente](preferences-and-localization.md) por paquetes, conservando la TUI y el núcleo consolidados. Este encargo no equivale a un cierre del rediseño: establece la secuencia, la autoridad y las pruebas de entrega.

## Base y propiedad

- Dos workers `gpt-6-luna` con razonamiento alto, coordinados por Codex conforme a la ampliación solicitada por el usuario. No crear subagentes adicionales.
- Directorio de implementación: `C:\Users\juanc\.codex\worktrees\tui-omega-animations\OmniCore`.
- Rama de trabajo: `codex/tui-omega-animations`. No cambiar de rama, reescribir historial, hacer push o merge a main desde el worker.
- Base de código consolidada: `e06aa7f`; contiene la TUI, M8, memoria `709f17b` y el rescate OAuth con las defensas posteriores de main preservadas. El coordinador proporciona el SHA final de documentación y confirma que main está integrado antes de iniciar P1.
- No tocar worktrees de otras ramas ni archivos fuera del repositorio. No leer cuentas, secretos, sesiones ni historial privado para resolver traducciones. No ejecutar login, inferencia, discovery o pruebas contra proveedores reales.
- Los fixtures deben usar directorios temporales propios. No cerrar ventanas ni matar procesos para destrabar builds; informar el PID y usar artefactos aislados si es necesario.

## Orden de lectura

Leer el plan y el [inventario](preferences-localization-inventory.md), y después ADR0030, ADR0039, ADR0040 y los contratos de los archivos que se vayan a modificar. Las instrucciones operativas del usuario prevalecen. Revisar git antes de editar; no sobrescribir cambios sobrevenidos.

## Primera entrega P1A

Construir la base de idioma y el catálogo de categorías sin rehacer todavía toda la ventana de preferencias.

1. Resolver es/en/fr/it con normalización, español predeterminado y fuente efectiva de la selección. Preservar `Spanish()`, `English()` y el contrato actual durante la migración. No traducir tool IDs, JSON, rutas ni contenido del usuario/modelo.
2. Añadir soporte de `ui.locale` exclusivamente User en DTO, parser, allowlist y esquema Host. Persistir por una fachada Host con revisión esperada, validación y escritura atómica; preservar las demás claves. Exponer DTOs de consulta/guardado por Protocol para que Client no dependa de Host. No usar el editor de sidebar como dueño semántico del idioma.
3. Definir la precedencia `--locale > OMNI_LOCALE > ui.locale > es`. La primera entrega puede separar resolución pura de su cableado completo de CLI/TUI, pero debe señalar ese pendiente. No hacer reemplazos globales de strings ni consumir argumentos tras `--`.
4. Crear un catálogo testeable de las nueve categorías y claves de título/descripción/ayuda del nuevo centro, con traducciones reales para los cuatro idiomas. Los nombres canónicos de categorías son estables e independientes del idioma.
5. Mantener explícita la cobertura parcial del catálogo legado. No rellenar fr/it copiando español o inglés para aprobar tests de paridad. Si el resolver cae a fallback, la prueba debe poder detectarlo; el catálogo nuevo no puede hacerlo.
6. Añadir pruebas de normalización y precedencia, settings ausente/inválido, idioma no soportado, escritura con revisión obsoleta, alcance no autorizado, cancelación, roundtrip y preservación de claves ajenas. Añadir paridad de claves y argumentos del catálogo nuevo.

Archivos esperados, no autorización para cambios ajenos: `src/OmniCore.Client/Localization.cs` y nuevos tipos de locale/categorías; nuevo contrato en `src/OmniCore.Protocol`; `src/OmniCore.Host/ConfigurationModels.cs`, `ConfigLoader.cs`, `Schemas/user-settings.schema.json`, nueva fachada de idioma y wiring mínimo de `OmniServer.cs`; pruebas focales; ADR0040 con el estado transitorio explícito. Leer primero los seams existentes y conservar AOT y referencias de proyectos. No modificar OAuth, permisos, motor local, Markdown ni animaciones en P1A.

## Entrega y revisión

Entregar un commit por bloque coherente, lista de archivos, pruebas y conteos reales, decisiones tomadas y pendientes concretos para P1B/P2. No deshabilitar assertions, saltar tests ni introducir mocks que eviten el contrato que se pretende validar. Actualizar el inventario con las claves/superficies migradas y la evidencia.

El coordinador revisa cada diff y agrupa la validación al cerrar bloques amplios. No se repite la regresión por cada texto o pequeño ajuste. P1B conecta arranque y propagación de idioma; P2 conecta la nueva navegación y General. P3 a P7 siguen el orden y las dependencias del plan. Una incompatibilidad de arquitectura o una operación que requiera autoridad nueva se reporta con evidencia; no se resuelve saltándose el Host o simulando una función inexistente.

Las pruebas no incluyen modificar preferencias reales del usuario ni gastar presupuesto de proveedores. Las futuras comprobaciones visuales distinguirán buffers Terminal.Gui, ventanas realmente ejecutadas y limitaciones de automatización.

## Ejecución paralela autorizada

Base de implementación `0c4ddc8`, entrega P1A. Main conserva la consolidación `fc6f8e0` mientras se construyen y revisan los bloques siguientes.

| Responsable | Bloque completo | Archivos exclusivos |
|---|---|---|
| Luna A con razonamiento alto | P1B y localización compartida de arranque, CLI, Host y catálogo existente | CLI fuera de Tui, Localization y recursos generales, contratos y configuración de locale, tests de idioma |
| Luna B con razonamiento alto | P2, General funcional, navegación y conexión de páginas ya existentes, formularios locales sobre la fachada Host | TuiApp y parciales, nuevas vistas de preferencias, PreferencesUiResources y PreferencesPresentation, tests de navegación y frames |
| Codex coordinador | Integración, configuración de motores locales, revisión y evidencia | TuiLocalEngineHost, sus pruebas, documentación compartida y correcciones acordadas |

Ambos agentes trabajan sin commits ni staging compartido; el coordinador reúne los archivos por bloque. El seam de TUI es `LocaleSelection? localeSelection = null` al final del constructor, seguido por la fachada local inyectable. Los recursos nuevos de preferencias se resuelven a través de Localization. Se escriben primero implementación y pruebas; después se compila el conjunto y se ejecuta una selección focal que cubra los contratos modificados, más arquitectura. Fallos nuevos se corrigen sin debilitar las pruebas. La regresión completa sólo se justifica por el riesgo de integración, no por la cantidad de traducciones.

## Cola tras P1B y el centro base

El [registro del bloque](../validation/preferences-batch-20261011.md) y el inventario describen el
estado implementado. Próximos encargos amplios, sin iniciar trabajo adicional automáticamente:

1. Completar navegación adaptativa en vivo y rutas de cuentas/API: cambio de ancho con borrador,
   foco y scroll conservados; fachada Claude exacta; acciones por capabilities y mensajes de estado.
2. Completar vistas/acciones tipadas de seguridad y M8, con recursos propios en los cuatro idiomas,
   sin mezclar lectura con consentimiento o ejecución. Mantener propiedad disjunta del primer bloque.

Antes de cada tanda se implementa el paquete y sus casos de prueba. Al integrar se reproduce
el grupo afectado; un fallo de UI no justifica repetir toda la suite de OAuth o del motor.
La traducción de las páginas heredadas y aliases sigue abierta y debe registrarse por superficie.
