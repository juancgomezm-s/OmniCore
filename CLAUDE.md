# OmniCore

**Directiva vigente M9/M10 (2026-10-23):** leer primero
[M9-M10-UI-OWNERSHIP](docs/plans/m8-m10-user-localization.md#directiva-m9-m10-ui-ownership--un-producto-interfaz-preservada-2026-10-23).
OmniCore es el núcleo del mismo producto, no una integración administrada por el usuario.
El agente de Core sólo escribe Core/CLI/TUI; cualquier copia de OmniCoder es sólo
lectura salvo encargo explícito y acotado de su coordinación/usuario. Terminar M9/M10
no autoriza cambios de composer, paneles, tema, navegación, controles, pruebas visuales,
bundles ni un nuevo dashboard. Tampoco autoriza editar su adapter/IPC por cuenta propia.
Conservar aportes en curso para revisión; no publicarlos reemplazando el workbench
de OmniCoder. Los checkpoints históricos de trabajo en ambos repositorios no amplían
este permiso. No reabrir la homologación cerrada ni debilitar la seguridad de Session.

**Corte Core vigente (2026-10-23):** procesos persistentes, controles nativos, lectura terminal
pasiva y escritores privados tienen recorridos reales locales. ConPTY Strong pasó con
resize/ACK exacto después de corregir handles estándar redirigidos; no requiere registro.
WebP estático se decodifica íntegramente. El [contrato y evidencia](docs/validation/m9-core-process-contract-20261023.md)
prevalece sobre los checkpoints históricos inferiores. El bloque se publicó en `main` como
`f02d3b5`, tras aprobar los cinco jobs de la CI 40014136165. M9 global y M8 manual siguen
abiertos. Se retoma M9-L, ciclo durable de worktrees, exclusivamente en ZephyrusG14 y
sin nueva publicación automática. Construcción Luna alto y validación Luna bajo se hacen **secuencialmente, un worker
activo a la vez**; Core no modifica ni publica OmniCoder.

**M9-L integración nativa de worktrees aceptada localmente (2026-10-23):**
preview por Lane público, aplicación, permisos, recibos y reapertura están
cableados; Luna bajo aprobó 12 casos pertinentes sin recompilar, cero fallos y
omisiones. [Evidencia y límites](docs/validation/m9-worktree-lifecycle-20261023.md).
Sin CI/publicación nueva. El incremento siguiente de controles de procesos hijos
se registra abajo; la integración Electron permanece en su coordinación.

**Controles de procesos hijos genéricos aceptados localmente (2026-10-23):**
delegación, TaskPacket y worktree reales, manager por Execution y controles
autenticados en el Turn original del hijo. Ask, efecto, recibos, replay y salida
UTF-8 están conectados sin inferencia adicional para la intervención. Luna bajo
validó seis casos únicos y, tras corregir la correlación de InteractionId,
revalidó el foco afectado sin recompilar. [Acta y límites](docs/validation/m9-child-process-controls-20261023.md).
Verify conserva su comando exacto; no se controla el stdin del agente supervisor.
Es aceptación local, sin nueva CI/publicación ni cierre global de M9/M8.

**M9-L limpieza/descarte nativos aceptados localmente (2026-10-24):**
`core:worktree.cleanup` y `core:worktree.discard` usan Lane pública, revisión
exacta del árbol, política vigente, Ask, archivado y recibos durables. Luna bajo
aprobó 13 casos sobre los binarios corregidos sin recompilar, después de detectar
y corregir una regresión de composición del catálogo de procesos. [Acta y límites](docs/validation/m9-worktree-disposition-20261024.md).
La reapertura comprobada es posterior al éxito; no acredita caída intermedia.
Barrido nativo de retención, undo, persistencia Weak y demás pendientes conservan
su alcance. Sin nueva CI/publicación; OmniCoder sigue en solo lectura y M9/M8 abiertos.

**M9-L retención nativa aceptada localmente (2026-10-24):** con autorización
expresa del usuario, el Run actual puede podar refs privadas vencidas de un Run
histórico terminado de la misma Session/workspace, usando permisos actuales.
Consulta pasiva, snapshot/OIDs exactos, Ask/Deny, lease, transacción Git sin
desreferenciar, recibos y reapertura están cableados. Luna bajo aprobó 13 casos
sin recompilar; [acta y límites](docs/validation/m9-worktree-retention-20261024.md).
No borra metadata/directorios ni amplía cleanup/discard entre Runs. M9 global y
M8 manual permanecen abiertos; sin nueva CI/publicación ni cambios de OmniCoder.

**M9-M persistencia Weak Windows aceptada localmente (2026-10-24):** backend
propio con Job Object asignado antes de ResumeThread, pipes/ConPTY, stdin/EOF,
salida secuenciada y stop por el manager existente. La limpieza libera el Job y
los handles incluso ante errores; fallos posteriores al arranque quedan unknown,
sin relanzar. Entorno heredado por allowlist exacta, Strong y WeakConsent intactos.
Luna bajo aprobó 28 casos sin recompilar tras corregir el defecto de disposición
detectado en su primera revisión. [Acta y límites](docs/validation/m9-weak-persistence-20261024.md).
Linux/macOS no tienen backend Weak persistente equivalente en este corte; PTY de
otras plataformas conserva ADR-0038. Checkpoint local, sin CI ni publicación nueva.
Native/Imported, aceptación transversal y consumidor Electron por su coordinación
siguen pendientes; M9 global/M8 manual abiertos y OmniCoder en solo lectura.

Runtime de agentes local-first en C#/.NET 10 y su CLI técnico `omni`. Debe funcionar igual con modelos locales pequeños (4B) y con modelos frontera, cambiando solo política y capacidades efectivas.

**Checkpoint M9-K/L (2026-10-17):** seguridad durable por Session, lifecycle nativo y respuesta de
cuestionarios están construidos y probados en Core y OmniCoder. Se repararon pérdida de presupuesto
al reanudar/reiniciar, formulario perdido y respuesta no cableada por guard legacy. Ver
`docs/validation/m9-block-k-20261017.md` para evidencia y límites. M9 sigue abierto por SEC-CONV,
otros mutadores/interacciones, procesos persistentes/ConPTY y aceptaciones pendientes; M8 queda
abierto para pruebas manuales del usuario. Coordinación actual autorizada: un worker Luna alto
construye y otro Luna bajo prueba; root revisa e integra, sin duplicar regresiones verdes.

**M9-J integrado en ambos main (2026-10-15):** contrato nativo versionado y SDK especializado para
OmniCoder sobre el Host/stdio existente, con consultas reales de herramientas, skills y contexto,
acciones humanas y flujos de conversación/eventos/artefactos conectados. Core conserva la autoridad.
ADR-0055 y `docs/validation/m9-block-j-20261015.md` registran evidencia y límites. El bloque no cierra
el acceso funcional completo: sesiones, modelos/rutas, interacciones y otros mutadores requieren
sus contratos y consumidores. Seguridad durable por conversación y ConPTY siguen pendientes de M9;
M8 OR5 y aceptación live/manual permanecen abiertos. Un solo worker Luna en alto; no reabrir UI sana.

**M9-I integrado y cerrado en su alcance local (2026-10-15):** `media.read` queda clasificado como
WorkspaceRead y conectado al CAS del executor Explorer, al siguiente request visual nativo y a los
clientes existentes. Focales 38 PASS/1 SKIP explícito, arquitectura 62/62 y E2E Electron→Host→HTTP
fixture 1 PASS; no acreditan comprensión visual de un modelo real. PNG/JPEG se decodifican; WebP sólo
comprueba firma/cabeceras/dimensiones. M9 y M8 OR5 siguen abiertos. Acta: `docs/validation/m9-block-i-20261015.md`.

- Especificación: `docs/spec/OmniCore-v1.md` (v0.5)
- Decisiones: `docs/adr/` — **prevalecen sobre la spec**. Léelas antes de cambiar cualquier cosa que toquen.
- Arquitectura (diagramas, clasificación de abstracciones, preguntas abiertas, roadmap): `docs/architecture/arquitectura.md`

**M9-G integrado y cerrado en su alcance local (2026-10-15):** descubrimiento, inspección y lectura segura de skills/instrucciones/memorias externos están construidos, cableados y revisados en main de OmniCore y OmniCoder. Un solo worker Luna en alto; 77/77 focales Core, build sin advertencias, arquitectura 62/62 y E2E Electron→Host de inspección/consentimiento/revocación aprobada. Decisión: ADR-0053. La query stdio devuelve metadatos y el cuerpo portable sólo se activa mediante el flujo autorizado `skill.load`. TUI y panel OmniCoder incluyen inspección; OmniCoder main solicita confirmación nativa para autorizar/revocar Ask. Memoria global y de proyecto tienen permisos independientes; proyecto queda ligado a identidad exacta. Evidencia y límites: `docs/plans/external-agent-discovery.md` y `docs/validation/m9-block-g-20261015.md`. M9-F/F2 siguen integrados en main; la aceptación live de generación de imagen y aceptación manual TUI de F/F2 siguen pendientes. Audio/vídeo fuera del alcance y OR5 M8 pendiente. No se declara M9/M8 cerrado.

**P0-OMNI-UNIFICACION cerrado globalmente (2026-10-14):** evidencia de los tres productos y aceptación explícita del usuario registradas en arquitectura §24 y `docs/validation/omni-family-homologation-closure-20261014.md`. Alcance: OmniCoder Electron, OmniCore CLI/TUI y OmniLlama React; Svelte queda fuera de esta fase. El usuario confirma que la animación funciona en el otro equipo y autoriza empezar M9 con **un solo worker Luna en alto**. Conservar interfaces y fronteras; no reiniciar homologación por capacidades diferidas. OR5 autenticado de M8 sigue pendiente y no se declara cerrado por iniciar M9.

**Alcance de Core aclarado por el usuario (2026-10-14): conservar la experiencia y los menús actuales, que ya considera una buena referencia.** La matriz U-C es una lista de contraste, no un programa obligatorio de rediseño. Leer primero el plan vigente de Core; abrir sólo correcciones de discrepancias concretas de uso, con verificación proporcional. Consultar un donante sólo para la pieza implicada y respetar sus cambios/retiradas recientes. No reconstruir superficies sanas ni adelantar funciones de M9/M10 por buscar paridad visual.

**Referencia de preferencias confirmada por el usuario (2026-10-14): «Sí, esta versión integrada».** Es el checkpoint `bef4607` de `codex/preferences-experience-20261013`: categorías laterales persistentes y controles directos en terminal amplia, con la paleta existente. El 2026-10-15 el usuario autoriza integrarla en main y publicarla en remoto. La combinación conserva los accesos y contratos de M8, memoria y M9-F/F2. No volver a presentar la pantalla descriptiva anterior como referencia aceptada ni confundir una captura sin color con un cambio de diseño. Evidencia de integración en `docs/validation/preferences-integration-20261014.md`.

**Contribución de OmniCore a la homologación cerrada en Windows (2026-10-14):** U-C01–U-C12
resueltos para el alcance actual, con diferencias explícitas; menús conservados y corregidos
recorte del prompt/pérdida de borrador ante rechazo o referencia no disponible. Evidencia en
`docs/validation/omnicore-homologation-closure-20261014.md`. No reiniciar los lotes UC por
capacidades diferidas; reabrir sólo por un defecto concreto. El cierre global y OR5 autenticado
de M8 permanecen separados. M9-B añade lease por OS, recibos/dedup, resultado ambiguo y replay
paginado integrados en `main` (2026-10-14); evidencia en `docs/validation/m9-block-b-20261014.md`. M9-C integra el consumidor TypeScript de lectura y supervisor Electron; evidencia en `docs/validation/m9-block-c-20261014.md`. El bloque M9-D está revisado e integrado en ambos main y añade acciones humanas explícitas input-Plan/mode/interrupt/cancel con proof efímero firmado desde Electron main y revalidación de scope en Core; evidencia en `docs/validation/m9-block-d-20261014.md`, decisión ADR-0050. M9-E revisado e integrado en ambos main añade `conversation.start.user` enlazado a pipeline Core real desde el composer OmniCoder, con ACK de admisión separado del resultado y replay durable; ADR-0051 y `docs/validation/m9-block-e-20261014.md`. M9-H construye stream durable opcional con ventana acotada/ACK tras consumo, cursor de Session y cliente OmniCoder cableado; ADR-0054 y `docs/validation/m9-block-h-20261015.md`. La fixture GUI→Host verifica wiring con provider local determinista, no sustituye aceptación del proveedor local. M9 permanece abierto: `media.read`, procesos persistentes/ConPTY, multi-OS/cuatro idiomas, Native/Imported y aceptación manual; OR5 M8 sigue pendiente.

**CI de Core reparado (2026-10-15):** las 16 regresiones de M9-F/F2 se corrigen en el PR #2 (`fa16299`), conservando el ajuste de panel lateral `594f2e3` y la integración M9-G `f42d16e`. Los cinco jobs Windows/Ubuntu, instaladores y paquetes Linux pasan antes de integrar. Se mantienen las facturas reales de imágenes, los hashes históricos de `model.profile` v2 y la interfaz integrada confirmada por el usuario. Evidencia y resultados: `docs/ci-failure-analysis.md`. Este cierre de CI no sustituye la aceptación funcional pendiente de M9/OR5.

## Comandos

```bash
dotnet build OmniCore.slnx
dotnet test --solution OmniCore.slnx
dotnet run --project src/OmniCore.Cli -- <args>
```

Paquetes: `dotnet add <proyecto> package <id>` (Central Package Management; la versión queda en `Directory.Packages.props`). Tests: xUnit v3 sobre Microsoft.Testing.Platform.

## Invariantes (spec §4) — no negociables

1. El modelo nunca ejecuta tools: emite requests que el Tool Runtime ejecuta.
2. El modelo nunca otorga permisos. Las tools nunca deciden su propia autorización: el Engine consulta el Permission Engine antes de invocarlas.
3. Subagentes solo vía `Task → Lane → AgentExecution`. Nada de `SpawnAgent(prompt)`.
4. Los subagentes reciben `TaskPacket` + contexto materializado, nunca el transcript del padre.
5. Canonical Journal = Event Store append-only + artifacts inmutables content-addressed (ADR-0001). Estado en eventos, contenido en artifacts. Compactar nunca lo modifica, y un artifact referenciado no desaparece.
6. El contexto enviado al modelo es siempre una proyección del estado.
7. Lógica por capacidades, nunca por nombre de modelo o provider (`if (model == "qwen")` está prohibido).
8. El Engine no conoce providers concretos, SQLite, archivos de log, Git, PowerShell ni MCP.
9. Hooks pueden restringir, observar o enriquecer; nunca ampliar permisos.
10. Un Run no termina porque el modelo lo diga: pasa por Completion Gates.
11. Clientes interactúan solo mediante commands y eventos, a través de `IOmniClient` (ADR-0019).
12. El Plan es estado canónico del runtime: el modelo propone `PlanMutation`s y `PlanService`/`ProgressReconciler` deciden (ADR-0016).
13. Ninguna tool se ejecuta sin `AuthorizedToolIntent`, que solo construye Security. `Prepare` es puro (ADR-0014).
14. Ningún efecto lateral empieza antes de que su intent esté confirmado con un commit Barrier; todo efecto es reconciliable (ADR-0002, ADR-0004).
15. Procesos: `process.exec(executable, argv[])`. Nunca se autoriza interpretando texto de shell (ADR-0015).
16. Los secretos nunca llegan al journal, los artifacts, el contexto ni los logs (ADR-0018).
17. Los providers conservan su semántica nativa (`ContentBlocks` + `ProviderOpaque`); nada se fuerza a pasar por OpenAI-compatible (ADR-0005).
18. El comportamiento por modelo sale de `EffectiveModelProfile` → `HarnessPolicy`, nunca del tamaño ni del nombre (ADR-0007).
19. El Engine nunca interpreta texto que empiece por `/`: los commands llegan tipados (ADR-0024). Keybindings y palette son solo del cliente y resuelven a `ClientAction`s (ADR-0025).
20. Ninguna fuente sustituye en silencio una tool built-in `Protected`, y un nombre ambiguo nunca se resuelve en silencio (ADR-0024, ADR-0027).
21. Las extensiones y skills no `Core` corren fuera de proceso; su manifest es un techo de permisos (`ExtensionBoundary`), nunca un grant (ADR-0023).
22. Memory es un servicio externo que entra solo como `IContextContributor`. El runtime funciona sin ella, y el LLM nunca promociona memoria por sí solo (ADR-0028).
23. Todo `ContextItem` lleva procedencia (ADR-0029).
24. La UI está desacoplada: Terminal.Gui y Spectre.Console solo en `OmniCore.Cli` (lo verifica un test). `OmniCore.Client` (`ClientProjection`, modelos de presentación, acciones, widgets) no conoce frameworks visuales, y todos los renderers leen el mismo estado (ADR-0030).
25. Todo human-in-the-loop pasa por `InteractionRequest`/`RespondToInteraction`, con opciones decididas por el servidor (ADR-0034). La status line nunca inventa datos: sin cuota informada muestra `—` (ADR-0031).
26. Un Run abarca la conversación hasta pasar los gates, con 1 Run activo por Session. PLAN → ACT ocurre en el mismo Run tras `PlanApproval` (ADR-0035).
27. Cada transición de estado canónico tiene exactamente un evento. La actividad de Lane (`Waiting*`, `Stalled`) se deriva y nunca se persiste (ADR-0036).
28. Permisos: las capas se combinan por mínimo (`Deny < Ask < Allow`), y un grant solo levanta `Ask` de `UserPolicy` o del perfil, nunca un `Deny`. Los grants persistentes van por `WorkspaceId`. El perfil por defecto es autónomo (ADR-0037).
29. Un workspace no confiable ignora su `.omnicore/`. Un repo nunca configura providers, credenciales, permisos amplios ni sandbox (ADR-0039).
30. La auditoría sobrevive a la purga de sesiones, y la telemetría es solo local (ADR-0043).
31. Tener una credencial no autoriza a gastar: el routing y la escalación solo eligen rutas de la `SessionRoutingPolicy`, y `MeteredCurrency`/`Unknown` exigen consentimiento explícito (ADR-0046).
32. `Event ≠ Decision ≠ RuntimeCommand`: solo el servidor escribe el journal, y todo cambio de estado desde fuera de Core llega como command con `CommandOutcome`. Un Turn contiene 1..N `ModelStep`; un modelo recomienda, la política determinista decide (ADR-0046).
33. El usuario elige el modo (PLAN / ACT / ORQ); ni el modelo, ni el esfuerzo de razonamiento, ni una skill o subagente lo cambian. Solo UltraCode, seleccionado explícitamente por el usuario y con autorización durable vigente, permite cambios automáticos de modo, y nunca amplía permisos, rutas ni budgets (ADR-0047).
34. El stream stdio entrega exclusivamente eventos canónicos durables del journal. ACK confirma consumo de una ventana acotada; sin crédito se pausa la lectura del journal, sin bloquear commands/cancel ni acumular una cola RAM sin límite. Gap/purga permanece visible como historial incompleto. No derivar tokens, TTFT, usage o coste sintéticos; clientes sin capability usan replay paginado declarado (ADR-0054).

## Reglas de trabajo

- Grafo de dependencias en ADR-0009, verificado por `tests/OmniCore.ArchitectureTests`. Cambiar el grafo = actualizar ADR y test en el mismo cambio.
- `OmniCore.Sandbox` no depende de nada de OmniCore: se comparte con OmniCoder (ADR-0008).
- Todo en .NET 10. **Única excepción:** `OmniCore.Protocol`, `OmniCore.Client` y `OmniCore.Sandbox` compilan también para net8, porque las consume OmniCoder. En esas tres no se usan APIs exclusivas de net10, y un test lo verifica (ADR-0038 §6).
- Multiplataforma: Windows y Linux completos, macOS best-effort. Nada de rutas ni APIs de plataforma fuera de las abstracciones de ADR-0038 §2. Los analizadores AOT están activos: sin reflexión dinámica en el Core.
- Configuración en YAML (YamlDotNet con generador estático), validada con schema (ADR-0039).
- Textos visibles al usuario: `LocalizedText` con recursos `es` (por defecto), `en`, `fr` e `it`; conservar claves/IDs canónicos y contenido original. Los prompts al modelo, en inglés (ADR-0040).
- Errores tipados (spec §71), no `Exception` genérica para fallos de dominio.
- Toda operación async significativa recibe `CancellationToken` y lo propaga.
- Priorizar tests deterministas (`ScriptedModelProvider`, `FakeTool`, etc.) sobre tests con LLM.
- Todo valor configurable debe tener un test que demuestre que cambia el comportamiento (lección de OmniCoder, ADR-0007).
- Identificadores y código en inglés; documentación y mensajes al usuario en español.
- Datos de runtime **fuera del repo**: `<data>/workspaces/<WorkspaceId>/` (journal, blobs, worktrees), donde `<data>` es el directorio de datos de la plataforma (`%LOCALAPPDATA%\OmniCore` en Windows, `$XDG_DATA_HOME/omnicore` en Linux). `.omnicore/` del repo es solo configuración versionable (ADR-0039 §2).

## Estado

- **Cierre del diferido Linux (2026-10-14):** M1, M4/TUI v0, M5.5, M6 y M7 verificados en Debian/WSL2 x64; implementación M5/OAuth y distribución self-contained/Native AOT verificadas, sin atribuir inferencias autenticadas nuevas. Suite Debian antes de integrar M9-B: 4541 casos, 4526 PASS, 0 FAIL, 15 SKIP explícitos. La integración de M9-B y los resultados finales del CI están registrados por separado en `docs/validation/linux-milestones-closure-20261014.md`. No repetir el bloqueo histórico por falta de distro. M2/M3 manuales, backend Strong Linux, OR5 autenticado y consumidores pendientes de skills continúan abiertos por sus motivos reales. Conservar los hitos ya aceptados y el estilo/menús actuales.
- **Evidencia visual:** las revisiones del diseño deben conservar los atributos RGB del terminal. Un probe `-noColor`/`NO_COLOR` se etiqueta como accesibilidad y no se presenta como el estilo normal del producto. Las imágenes del acta Linux se regeneraron con la paleta aprobada; esta reparación no modifica los estilos de la TUI.
- **Referencia de preferencias confirmada por el usuario (2026-10-14), integración/publicación autorizadas el 2026-10-15:** la versión integrada con categorías laterales y controles visibles de `bef4607`, conservada en `codex/preferences-experience-20261013`, es la esperada en las capturas. Se preservan los campos añadidos por M8 y M9-F/F2. No confundir el índice anterior con esa referencia ni reemplazar archivos nuevos desde la base antigua. La rama y su historial tienen respaldo local verificable.

- **OAuth nativo de Claude: implementación cerrada en Windows (2026-10-10).** Trabajo de Pi preservado y conectado: `omni login claude [provider] [--manual]`, logout, refresh coordinado, inferencia por Bearer, perfil/roles opcionales y estado de cuenta en doctor/status line plain/TUI. Identidad declarada en configuración User, sin defaults de cliente; routing y consentimiento de gasto conservan su política. Build multi-target sin errores ni advertencias y 241 pruebas OAuth PASS. Suite integral sobre `f59e766`: **3696 casos, 3692 PASS, 0 FAIL, 4 SKIP** por symlinks, incluye arquitectura y recuperación M4. Revisión y límites en `docs/validation/claude-oauth-closure-20261010.md`; no se acredita login/inferencia real con una cuenta Anthropic. Linux offline verificado en el acta del 2026-10-14; macOS y aceptación autenticada manual siguen pendientes.
- **Hecho:** esqueleto (14 proyectos + tests de arquitectura; multi-target y analizadores AOT) y revisión arquitectónica v0.2–v0.7 (ADRs 0001–0051; M9-E integrado en ambos main).
- **M1, M4 y M5: cerrados en Windows** (2026-09-30, 2026-10-05 y 2026-10-07; evidencias en `docs/validation/m4-closure-20261005.md` y `docs/validation/m5-closure-20261007.md`: cualificación `quick` real de ChatGPT por suscripción y de Qwen local); la verificación Linux de M1/M4 está cerrada en el acta del 2026-10-14; la implementación M5 también se verificó allí, conservando el alcance autenticado original. **M2 y M3: código completo.** Falta la validación del usuario de M2 y M3 con su modelo local (`docs/validation/m1-m4.md`). Nota de entorno: la shell aislada del agente ve una copia de `%APPDATA%`; los cambios en la configuración real del usuario se hacen desde su terminal. El servidor Qwen local debe arrancarse con `C:\temp\qwen38-start.ps1` (DRY + MTP); lanzado a mano sin DRY degenera en bucles. Diferido: sandbox Strong en Linux. El lease de escritura entre procesos de M9-B está integrado y verificado en Windows y Linux. El estado por epic está en `docs/architecture/arquitectura.md` §24.
- **Seguridad P0 corregida** (2026-09-29): Explorer de solo lectura + capa de modo PLAN, combinación de permisos por mínimo, `AuthorizedToolIntent` infalsificable, SHA-256 estándar en el CAS, cadenas de enlaces y secretos sobre el destino físico, TLS relajado solo para literales IP privados y fijable por provider con `caCertificate`, configuración de providers solo desde el directorio del usuario, `WorkspaceId` hasheado con datos en `<data>/workspaces/<id>/`, y credenciales cifradas (DPAPI en Windows, AES-GCM provisional en Linux).
- **Tests tras la revisión del cableado M6 (2026-10-09):** 3296 casos, 3292 PASS, 0 FAIL, 4 SKIP por restricciones de symlinks; arquitectura 56 PASS. La suite integral serial incluye recuperación M4. Revisión de código `db756dc`, hashes y logs en `docs/validation/m6-wiring-audit-20261009.md`; el cierre original del 08-10 conserva su evidencia en `docs/validation/m6-integration-20261008.md`. Los tests fijan `OMNICORE_DATA_DIR`/`OMNICORE_CONFIG_DIR` a un temporal: nunca tocan los datos del usuario.
- **Cableado de la auditoría A1–A12 (2026-10-09/10):** lo generado según el plan pero sin cablear a producción quedó conectado y probado.
  - **Watchdog:** `StallPolicy` completa en Runs reales, con la cadena Replan → Diagnose → EscalateModel → SplitTask → AskUser (ADR-0048).
  - **Configuración y permisos:** scopes con claves `locked` (ADR-0039 §5) y `permissions.yaml` con perfil y reglas de usuario como capa `UserPolicy` (ADR-0037 §4). Los perfiles `balanced` y `conservative` son decisión de implementación documentada en el ADR, no de la spec.
  - **Modelos locales:** `host: attach|managed` por provider, supervisado por `LocalServerSupervisor` (ADR-0011 §4).
  - **Evidencia y política de modelos:** `FileMutationReliability` medida en Runs ACT y usada por el onboarding (ADR-0044 §4); `omni act` sin TTY sobre un modelo sin clasificar termina con `ModelPolicyRequired` (§6).
  - **Multi-agente:** `/agent send` es el emisor del mailbox (el acta de cableado M6 lo daba por operativo solo por su recepción); `TaskSkipped` al saltar un item del Plan; `LaneHeartbeatRecorded` retirado (superado por ADR-0046).
  - **Cliente:** actividad de la Lane raíz en la sidebar, `ToolPresentation` según ADR-0033, `ClientCommandRegistry`/`ClientAction` (`/help`, `/exit`, `/tasks`, `/events`, `/permissions`…) y `omni explain` con contexto y fingerprint.
  - **Corrección:** los diagnósticos de configuración daban línea y columna con una unidad de más (YamlDotNet ya numera desde 1).
  - **Pendiente, anotado en los ADRs:** eventos `ProviderUnavailable`/`ProviderRestarted`; mover los commands propios de la TUI al registro; regla de red de build de un workspace no confiable (ADR-0037 §4); flujo de rework de etapas, que desbloquearía `ValidationDebtResolved` (M7); las delegaciones escritoras no pasan por `ModelPolicyRequired`.
- **Tests tras el cableado A1–A12 y la recuperación M4 (`ae0cf11`, 2026-10-09 local / 2026-10-10 UTC):** suite completa serial sin M4: 3454 casos, 3450 PASS, 0 FAIL y 4 SKIP por symlinks; arquitectura incluida y correcta. **Recuperación M4 verificada por separado en serie, 3 de 3 PASS**, con 200 Turns y el límite original de 2 min por hijo: seed 60.838 / 56.962 / 59.467 s; restore 2.370 / 2.194 / 2.382 s. `EventStream` recupera solo el sufijo nuevo en lecturas repetidas, reduciendo la mediana de seed un 42 %. Comparación con `1f16c63`, carga del equipo, hashes, logs y límites en `docs/validation/m4-recovery-closure-20261009.md`. Las suites se lanzan desde una shell sin TTY y con stdin redirigido: con consola interactiva, siete tests que arrancan el CLI como proceso hijo se cuelgan esperando stdin. La evidencia anterior de `23a9eba`, incluida su intermitencia de delegaciones, queda registrada en el acta.
- **Toolchain:** SDK oficial de .NET 10 (10.0.401); Terminal.Gui 2.6.0-develop.61 para la TUI v0, sobre `ClientProjection` (ADR-0030 §3).
- **Roadmap:** la fuente de verdad es `docs/architecture/arquitectura.md` §24. **M5.5 cerrado en Windows y Linux** (2026-10-07 local / 2026-10-08 UTC; ADR-0046 + ADR-0047), acta íntegra en `docs/validation/m55-closure-20261007.md`. Fases A/B, checkpoint opaco protegido v3, preimagen previa al efecto, fingerprint explicable, outcomes/atribución, cotas UltraCode y 23 eventos pre-M6 verificados. FULL final 2962 casos/2958 PASS/0 FAIL/4 SKIP symlink; arquitectura 56 PASS; probe de componentes reales WPF de OmniCoder exit0. **M6 cerrado en Windows (2026-10-08) y Linux (2026-10-14):** worktrees integrados e implementación del worker Luna xhigh, verificada por la revisión raíz. Workflow compilado Explore/Implement/Verify con Plan reconciliado, scheduler en un Host, pool jerárquico, lectores paralelos/escritoras serializadas, FanOut/FanIn, supervisión Wait, mailbox/wake, background TUI, heartbeat e inspector atribuible y deuda de validación. Inventario, checkpoints, pasada inicial fallida y cierre integral en `docs/validation/m6-integration-20261008.md`. **M7 cerrado en Windows y Linux** (2026-10-11), evidencia en `docs/validation/m7-isolation-20261010.md`. **M8: A–H y correcciones retrospectivas integradas/verificadas**; build limpio y suite sobre `09ad2ba`: 4,505 correctas, 0 fallos, 11 omisiones explícitas; OR5 autenticado pendiente de configurar la cuenta, por lo que el hito no se declara cerrado (`docs/validation/m8-extensibility-20261011.md`). **M9/M10: avance parcial, con pendientes explícitos**. M9-A/B integran stdio CLI↔Host, lease por Session, receipts y replay recuperable; M9-C conecta consumidor TypeScript y supervisor Electron de lectura. Evidencias y límites en `docs/validation/m9-stdio-first-block-20261014.md`, `docs/validation/m9-block-b-20261014.md` y `docs/validation/m9-block-c-20261014.md`; M9-D conecta acciones humanas explícitas; M9-E revisado e integrado en ambos main cablea el composer de OmniCoder al pipeline Core con inferencia real y replay/receipt; evidencia y límites en `docs/validation/m9-block-e-20261014.md`, ADR-0051. El smoke local qwen08 y reapertura pasan en perfil aislado, con confirmación nativa reemplazada por harness; no equivale a aceptación humana manual ni cierra M9. **P0-OMNI-UNIFICACION cerrado globalmente; M9 autorizado con un solo worker Luna alto**; la matriz U-C01–U-C12 vive en arquitectura §24 como lista de contraste. Por aclaración del usuario, conservar la UI/menús actuales y corregir sólo discrepancias concretas, sin iniciar una campaña UC-A–UC-D de rediseño. El cierre restante de M9 y la aceptación manual M2/M3 siguen pendientes. El acta Linux conserva la verificación A/B/D y los hitos anteriores; la combinación con M9-E se revalida en la rama de reparación CI antes de fusionar. Fixtures no acreditan consumo ni cualificación reales; M5 conserva su acta autenticada independiente.


**M9-F2 integrado (2026-10-15):** salida nativa de imagen PNG/JPEG construida, cableada y revisada en ambos main (Core `22128b1`, OmniCoder `db4c2a0`). La E2E local fixture valida Electron→Host→CAS/journal, exportación y reapertura sin segundo request; no hay ruta live de generación configurada/verificada. TUI visual interactiva expiró y queda pendiente (`%TEMP%\omni-tui-visual-one.log`). Audio/vídeo fuera de alcance; G no iniciado; OR5 M8 pendiente. Acta: `docs/validation/m9-block-f2-20261015.md`.
