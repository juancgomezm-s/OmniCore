# M9: consumidor vigente, composer y auditoría de cableado — 2026-10-26

Estado: candidata local; **M9 global abierto**. Base Core `f21ad93`, consumidor vigente `C:/Users/juanc/Documents/JayCoder` sobre `bd82c25` más sus cambios de recuperación preexistentes. No se cambió el diseño de la GUI, no se reconstruyó su `packages/app/out` original y no se publicó ni instaló esta candidata. Se trabajó sin workers. M8 conserva su aceptación manual independiente.

## Correcciones construidas y conectadas

| Hueco comprobado | Ruta efectiva y corrección |
|---|---|
| El consumidor rechazaba el catálogo entero al recibir `nativeTool`. | `native-api.ts` y `presentation/wire.ts` aceptan el tipo real del Host; el catálogo vuelve a alcanzar el composer. No se convirtió en prompt al modelo. |
| `!` no alcanzaba la salida real de shell en la copia vigente. | `PromptDock` → preload → Main → `ChatBackendPort.invokeNativeCommand` → SDK firmado → Host `core:shell.exec` → ToolRuntime/OS. `HumanShellProcessTool` y `ToolRuntime` suministran el campo `displayOutput` existente, con código de salida, gap y truncamiento explícitos; CAS conserva el resultado completo. El adapter lo proyecta en las tarjetas existentes y en replay. |
| El segundo comando usaba recuperación/catálogo obsoletos. | Refresco previo al envío y uso del mismo snapshot de catálogo/recuperación; no se reenvían estados inciertos. |
| FullAgent→ObserveOnly durante un Run eliminaba una obligación de validación. | Core conserva `RequirePostEditValidation` al revocar mutaciones, sin relajar `IsMonotonicTightening` ni limpiar deuda previa. |
| No había importación desde el consumidor actual; replay leía campos incorrectos. | `!core:conversation.import "archivo.json"` (también `/import`) → Main, archivo regular explícito dentro del workspace, tamaño y lectura estable → comando firmado `conversation.import` → Session Imported/ObserveOnly/ImportReview → replay con `Role`/`Text` exactos → reapertura. No crea input/Run/inferencia/tools. Un ACK aplicado o incierto seguido de error no se vuelve a tratar como importación nueva repetible. |
| El composer exigía modelo para comandos humanos. | El estado de envío permite `!` y `/` con Host disponible; la inferencia ordinaria sigue requiriendo modelo. Core conserva la decisión de política por comando. |
| El adapter descartaba Permission de hijos admitidos. | Se proyectan Question y Permission de hijo con Task/Lane/Turn/ToolCall canónicos, y sólo se cierran por la interacción coincidente. Se reutiliza respuesta firmada/revisionada; Host verifica la delegación aceptada. PlanApproval de hijos y scopes ambiguos no se habilitan. |
| Las consultas de worktree estaban fuera del allowlist del SDK. | `!core:worktree.preview <laneId>` y `!core:worktree.retention.preview <ownershipId>` → IPC tipado → VM/adapter → SDK → queries pasivas exactas. El resultado real se presenta como **Consulta**, sin input, inferencia, command ni efecto Git; la tarjeta es efímera y no se inventa historia durable de herramientas. |
| Los controles de delegación existían sólo en el clon anterior. | Se trasladaron únicamente los contratos/handlers de cancelación, disposición y mailbox a SDK/Main/preload/wire actuales. `!core:delegation.cancel <id>`, `!core:delegation.mailbox.send <id> "mensaje"` y `!core:delegation.disposition <id> <resultId> Accepted|ReworkRequested "motivo"` recorren Main/Host. Main toma política, Run, target y revisión del Host, rechaza campos extra y bloquea reenvío de resultados inciertos. No se copiaron paneles antiguos. |
| El bloqueo busy impedía supervisar al hijo desde el composer. | Sólo las acciones humanas tipadas de delegación y queries pasivas admiten Enter durante un turno activo; shell/import/mutaciones/workflow conservan la barrera idle. No se cambió la apariencia ni se habilitó ejecución paralela genérica. |
| Comandos nativos existentes no se podían invocar con `!`. | `!core:<id>` usa identidad/fingerprint/revisiones del catálogo Host vigente. Los workflows siguen exigiendo disponibilidad/modo/política del Host; escribir el comando no eleva permisos ni cambia a Orchestrate. Worktree integrate/cleanup/discard/retention aceptan su objeto JSON único o los argumentos explícitos existentes. `!<shell>` conserva su texto literal; `!core:` reserva el espacio nativo. |
| WebP se rechazaba entre Core y el renderer. | DTOs/guardas de SDK, adapter y Main aceptan WebP, validan la referencia de la Session y exportan `.webp`; el renderer conserva su componente de imágenes existente. |
| Dos imports de Core infringían el límite del renderer. | Sólo se cambiaron imports de `CoreProcessActivity` a una exportación pura de formato en presentation. No se amplió el bypass ni se cambió JSX/CSS. Inventario IPC regenerado desde registros reales. |

## Verificación integral y límites

Evidencia primaria local: `artifacts/m9-final-wiring-20261026/`. Los intentos rojos se conservan separados; no se cuentan como aprobados ni se sobreescribieron.

- Core: build de solución y build final de tests/dependencias, cero errores/advertencias; foco OS de shell **1/1**, con stdout/stderr, cancelación y prueba de no herencia al contexto posterior; regresiones SessionSecurity **35/35**. Logs `core-shell-display-wired-result.json`, `core-session-security-final-result.json`.
- Consumidor: build de los seis workspaces y builds incrementales de los componentes cambiados. Regresión final **75/75** en seis archivos: SDK nativo, adapter, supervisor, referencias de imagen, wire binario y parser de comandos. `coder-final-all-native-regression.log`.
- Frontera arquitectónica final: **165 registros Main / 165 métodos preload con canal / 163 métodos declarados en Bridge**, 49 archivos inspeccionados. Las APIs adicionales de Main son las que registra el inventario, no un recuento de funcionalidades aprobadas. `coder-final-all-native-boundaries.log`.
- Electron→Host→OS: shell en perfiles Core **es/en/fr/it**, salida y cwd reales, exit7, límite de salida, reapertura sin reejecución, ObserveOnly niega escritura y cero requests al proveedor. Último smoke es sobre el consumidor final: `coder-shell-es-real-final-wiring-result.json`. Configurar cuatro perfiles no acredita traducción completa de toda la GUI.
- Electron actual: importación válida e inválida, `!core:conversation.import`, consulta de Lane inexistente devuelve `null` sin fabricar ownership, rechazo de scope ajeno, journal sin delta por query y reapertura pasiva. `coder-final-all-native-integral.log`.
- Electron actual: defaults sólo para Sessions nuevas, dos techos independientes, cambio revisionado de categoría, rechazo de categoría/autoridad falsificada y workspace ajeno, reapertura A/B, desconexión/reintento mediante el control existente, borrador conservado, cero fallback y cero requests. Controles de delegación sin Run y campos extra se rechazan desde composer/Main. `coder-security-delegation-reconnect-final-e2e.log`.
- Supervisión durante inferencia: Electron mantiene un request de modelo real contra fixture SSE en espera. Enter con cancelación de hijo ajeno alcanza Main/Host, se rechaza sin otro input/request y conserva borrador; el turno original termina tras liberar la respuesta. `coder-human-control-active-turn-sse-final-e2e.log`.
- WebP: proveedor HTTP **fixture** local → Host/CAS → componente de imagen → exportación byte a byte → reapertura con un único request. No se presenta como generación live. `coder-media-authenticated-loopback-e2e.log`, resultado `C:/Users/juanc/AppData/Local/Temp/omnicoder-m9-media-zcSQhU/result.json`.
- Portapapeles del sistema Windows: imagen PNG y más de 40.000 caracteres pegados mediante Ctrl+V en Electron real. Snapshot de formatos sólo en memoria, escritura/restauración atómica y guardia frente a cambio concurrente del usuario. La confirmación nativa fue aceptada por el harness; no equivale a aceptación humana manual. Misma prueba de medios; ejecución opt-in, no modifica clipboard de CI rutinaria.
- **Visión live**: producción SDK→Host→ChatGPT OAuth existente→`gpt-6-luna`, un request con imagen propia cuyo código aleatorio no está en el prompt. Respuesta exacta figura/color/código y Run completado. `live-vision/result.json`; perfil temporal con `inputModalities: [text,image]`, workspace propio y techo ObserveOnly; no cambia configuración global ni crea claves.
- Preservación GUI: estructura de render de `PromptDock` y `CoreProcessActivity` idéntica antes/después al normalizar sólo las expresiones de los handlers `on*`; el handler Enter de PromptDock sí cambia para admitir supervisión durante inferencia. 424/439 archivos preexistentes sin cambios y cero cambios en assets/CSS/bundles originales. `gui-final-render-structure.json`, `gui-preservation-files.json`. Los 15 archivos preexistentes modificados y seis nuevos están individualizados en `coder-delta-index.json`; patch respecto a la GUI inicial de este turno, no respecto al HEAD antiguo: `coder-delta-review.patch`.

## Auditoría contra M9 completo

| Familia M9 | Construcción/cableado inspeccionado | Salida pendiente que no se simula |
|---|---|---|
| B/C/D/E/H: transporte, lease, dedup, origen humano, chat, stream | Contratos/SDK/adapter productivos; suites y actas históricas, CI f21 y regresión del supervisor actual. Import y `!` conservan el pipeline confiable. | No se promete exactly-once ni se sustituye un recibo incierto por éxito. |
| F/F2/I: texto, documentos, imágenes y salida | Rutas de adjuntos/media.read/CAS/replay existentes más WebP y clipboard comprobados; visión live aprobada en perfil declarado. | **Generación real de imagen OpenRouter pendiente de autorización de una imagen hasta USD0,05**; el fixture anterior no la sustituye. Aceptación humana TUI/GUI separada. Audio/vídeo siguen fuera del alcance acordado. |
| G: skills/instrucciones/memorias externas | `agent.sources`, consentimiento Ask, carga y política Host permanecen conectados según acta G; no se cambió descubrimiento en este corte. | No se atribuye autoridad a contenido externo ni se declara memoria multiscoped M10 terminada. |
| K/SEC-CONV | Políticas/procedencia/snapshots/revisión/receipts Core y consumidor real según recorridos anteriores; ningún bypass de categoría o fallback construido en este corte. | La matriz SEC-CONV-01–13 completa mantiene los límites de sus actas; el E2E nuevo no certifica todas las combinaciones de grants/Discovery/escalación, ni todas las carreras UI. |
| L/M: hijos, procesos y worktrees | Controles/ownership/ConPTY/publicación conjunta/worktree lifecycle Core aceptados en actas; consumidor vigente ahora incluye permisos de hijos, queries pasivas y controles de delegación tipados, además de procesos, alternativas, workflow y comandos existentes. | La aceptación positiva completa de hijo retornado→disposición y worktree real→aplicar desde este composer vigente no se sustituye por negativos Main ni por unitarias de SDK. Se conserva como gate de aceptación integral del consumidor. |
| Idiomas/plataformas | Shell bajo cuatro perfiles y CI Windows/Linux exacta de f21. ADR-0038 conserva diferidos. | No se certificó toda traducción de GUI ni todas las combinaciones multi-OS. Weak persistente Linux figura como trabajo futuro de plataforma, no como nueva exigencia M9 creada por este corte. |
| Publicación | PR10/f21 conserva CI exacta **5/5** (`40421687536`); `main` remoto sigue en `f02d3b5`. | Esta candidata cambia C# y consumidor: **no hereda el verde f21**. Falta revisión de Dot, CI de la nueva candidata y promoción de main. No se publicó a falta del dictamen. |

No se encontró resultado de modelo simulado en las rutas productivas modificadas. Los fixtures están confinados a tests opt-in; la visión live conserva su evidencia independiente. Los límites anteriores impiden declarar M9 global cerrado por un build o por contar handlers.

## Seguimiento local 2026-10-27: hijo real y bloqueo de paquete

Sin workers de construcción, cambios visuales, publicación ni instalación. En un repositorio temporal propio, la TUI productiva admitió y ejecutó un hijo con ChatGPT OAuth existente / gpt-6-luna (IncludedQuota). El hijo hizo `filesystem.read` de README.md y devolvió el código aleatorio del archivo, ausente del objetivo. Se verificó el resultado contra su hash CAS. Se cerró la TUI normalmente y OmniCoder vigente retomó exactamente la Session `01a17df6-0c0b-744a-98c0-c45eaf423d9e`. El composer `!core:delegation.disposition` produjo `result_disposition.recorded` (76), `lane.completed` (77) y `task.completed` (78), sin otro input ni model-step. Electron: **1/1**; evidencia `artifacts/m9-dot-acceptance-20261026/live-child-coder-acceptance.json` y `live-child-coder-handoff-e2e.log`. Esta aceptación positiva sustituye únicamente el pendiente de disposición del consumidor; no cierra toda la homologación ni M9.

La aplicación positiva de un worktree **sigue pendiente**. El workflow real `/orq-auth` desde la TUI se detiene antes de la inferencia del hijo con `InvalidDataException: The packet facts differ from the selected canonical parent context`. La comparación de los blobs propios identifica una discrepancia de redacción: el snapshot conserva `workflow authorization before tools`, mientras `PacketRef.Facts` guarda `workflow [REDACTED] tools`. `CreateDelegationProjection` reconstruye los hechos y los compara literalmente. Evidencia primaria: `live-workflow-packet-mismatch.json`, `live-workflow-failed-journal.json` y `live-workflow-manifest.json` en el mismo directorio de artifacts. No se desactivó la validación ni se sembraron eventos/bindings o resultados. La ruta genérica `/delegate` es de solo lectura; sus respuestas sin cambios no se aceptaron como implementación.

Se detuvo esta aceptación ante el impedimento conforme a la instrucción del usuario y se notificó a Dot. Pendiente una corrección coherente de redacción/canonicalización con regresión específica y repetir implementación verificada → preview → aplicación desde el consumidor. La candidata local y su corrección previa de importación TOCTOU no heredan el verde de f21 ni tienen aprobación de publicación.

## Corrección de permisos compartidos y actualización de pendientes (2026-10-27)

La recuperación de importación confinada y redacción canónica quedó en `3097030`,
integrada en main local. La regresión de importación/paquetes pasó 55/55 y la de
importación nativa 2/2. La implementación real Luna ya superó aquel bloqueo:
creó FeatureModule.cs y lo conectó a Program.Main en su worktree propio. El
verificador ejecutado de forma independiente terminó con código cero; no se
afirma que la etapa Verify del workflow se completara: agotó sus cuatro llamadas
antes de ejecutar el verificador. No se relajó ese presupuesto.

Por petición del usuario, Libre/Autonomous resuelve las confirmaciones por defecto
en Core, sin cambios visuales ni selectores adicionales. Ask/Deny explícitos,
ObserveOnly, AgentProfile, PLAN y límites físicos conservan su autoridad.
Regresión local: 109/109 (permisos, sandbox, importación, paquetes y Session),
dos recorridos nativos de recibos 2/2; SDK/adapter 55/55. CLI real: cuatro casos
Libre/Intermedio/Conservador/ObserveOnly con shell OS, cero inferencia; Libre
ejecuta sin petición de permiso. Electron vigente: shell es/en/fr/it 4/4 con
perfil Libre sin regla Allow adicional; importación y sustitución por junction 2/2.
Evidencia: `artifacts/m9-dot-acceptance-20261026/` y sus logs de intentos rojos
preservados; `core-cli-permission-levels.json`,
`post-recovery-permission-regression-result.json`,
`native-recovery-receipts-regression-result.json`,
`current-consumer-free-permissions-integral.log`.

**La aplicación real del worktree continúa pendiente.** La Session
`01a180ad-43b7-78c5-9b53-fd1680e89a7b` se retomó en OmniCoder vigente, preview
devolvió exactamente los dos archivos y el composer admitió la integración
`f5ee5c19-aaa3-4c98-837f-e1b000f22c3c`. La herramienta fue autorizada sin
preguntas, pero falló antes de Started con `PRIOR_READ_REQUIRED` (secuencias
217–225): el destino del padre no tiene lectura completa a su versión en ese
Run. No se eliminó esa protección ni se sembró evidencia de lectura. El padre
conserva sus archivos originales. Las denegaciones y fallos determinados se
distinguen de los resultados inciertos para no bloquear otras órdenes.

Sin workers de construcción, cambios visuales, instalación o publicación.
La candidata conserva revisión de Dot y CI propia pendientes; M9 global no cerrado.

Comprobación final de recuperación: build Core 0 errores/advertencias;
`live-native-failure-recovery.e2e.test.ts` 1/1 sobre la misma Session real,
reabierta desde el consumidor actual, reconoce tanto la denegación antigua como
el fallo `PRIOR_READ_REQUIRED` como `failed`, sin aplicar ni repetir el efecto.
`current-consumer-known-failure-recovery.log` y
`permission-gui-preservation.json` acreditan recuperación y preservación visual.
Los dos recorridos de recibos nativos volvieron a pasar sobre ese build.

## Resolución de los bloqueos funcionales (2026-10-28)

Este apartado sustituye los pendientes anteriores de Verify y aplicación del
worktree. La Session real `01a18115-5eb1-7d8c-9632-2d6b75880e73`, Run
`01a18115-d6f7-7448-bde4-e8d63e3c7d94`, completó Explore, Implement y Verify con
disposiciones Accepted y cerró el gate compilado en la secuencia 343. Verify
ejecutó primero `dyn.core.verify_integration`, sobre el árbol Implement ligado,
con salida cero en Strong; no se amplió el presupuesto de cuatro herramientas.

OmniCoder vigente retomó esa misma Session. El composer consultó la propuesta y
admitió la integración `c7a821d4-fa24-411b-8b13-8a42fce7279f`: aplicó exactamente
FeatureModule.cs y Program.cs del ownership `4b774106190940deb5370a031c83aade`.
Journal: Started 367, dos cargos de mutación 368–369, integración Completed 370 y
ToolCallSucceeded 371. Los bytes coinciden con el worktree, HEAD permanece intacto
y la reapertura conserva la Session, sin inferencia adicional ni nuevos permisos.
Electron real **1/1**, 71 segundos: `current-consumer-reconciled-workflow-apply.log`;
resultado y captura: `final-worktree-application.json` / `final-worktree-applied.png`
en el fixture propio indicado por `live-workflow-final-manifest.json`.

El intento anterior fue interrumpido por el plazo de la prueba. La reapertura
canónica comprobó NotApplied para ambos archivos, secuencias 356–357, antes de
permitir la nueva orden; se conserva el intento rojo y no se fingió ausencia de
efecto. La primera ejecución Strong también reveló que el SDK intentaba escribir
en C:\\Users: el home temporal privado corrige esa causa sin rebajar confinamiento.

Regresión integral worktrees/workflows **88/88** y foco final de diagnósticos,
aislamiento del proveedor, SDK privado y exclusión de rutas externas **4/4**,
sin omisiones. Logs: `close-workflow-worktree-regression-result.json` y
`final-diagnostics-sandbox-selected-regression-result.json`. Builds focales finales
de Host, CLI y tests: cero advertencias/errores. No se repitió la suite amplia tras
el verde. Los resultados corresponden a esta candidata, no certifican toda la
combinación posterior de main ni CI remota. Se conservaron fuentes, GUI y evidencias.

M9 mantiene la validación de generación de imagen real y los gates de entrega:
consolidación del consumidor, revisión de Dot y CI propia. OpenRouter ya figura
Conectado; el estado anterior de conexión pendiente quedó superado. M8 manual es
independiente. No hubo publicación ni instalación de esta candidata.
