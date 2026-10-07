# Resolución de los 42 archivos locales recuperados

Fecha: 2026-10-07. Base de integración: `main` en `84ee618`.
El usuario autorizó integrar o terminar estos archivos después de consolidar los worktrees.
No se sustituyen módulos actuales por copias antiguas ni se reanuda el monitor M5/M5.5.

## Fuente y conservación

Copias originales verificadas por SHA256:
`C:\Users\juanc\.codex\worktree-backups\omnicore-20261007-1600\pending-unintegrated`.
`INVENTARIO.md` en el directorio padre identifica checkout, rama y HEAD original.
Las ramas, el WIP `5a843f1` y el stash de cambios previos del checkout principal siguen conservados.
Esta revisión trata **los 42 archivos locales**, no afirma integrar todos los commits divergentes.
La eliminación física anterior fue rechazada por la herramienta: no se elude ese bloqueo.

## Resultado por archivo

`src/` y `tests/` en esta tabla son rutas relativas al repositorio. «Superado» significa que
la intención útil está presente en la implementación actual; no que el borrador sea idéntico.

### 030-OmniCore-luna-audit — 1 archivo

- `AUDIT.md`: preservado como evidencia histórica; resultados revalidados abajo. No se publica
  como auditoría vigente ni se atribuyen sus once hallazgos al estado actual sin comprobarlos.

### 060-OmniCore-m4-nvidia-closure — 15 archivos

- `docs/validation/m1-m4.md`: superado. Su afirmación de que el composer no ejecuta turnos contradice el ITuiTurnHost actual; no se incorpora.
- `src/OmniCore.Cli/Tui/MultilineComposer.cs`: superado por ComposerView; se mantienen Enter para enviar, Shift+Enter para salto y cursor bloque.
- `src/OmniCore.Cli/Tui/TuiTheme.cs`: se recupera el criterio de contraste en TuiWiringTests, no la antigua paleta naranja.
- `src/OmniCore.Cli/Tui/TuiApp.cs`: superado por sidebar de altura completa, overlays sin marco, selector circular y mantenimiento separado; no se restaura el antiguo layout.
- `src/OmniCore.Client/TuiPresentation.cs`: superado por las políticas responsive y autocomplete actuales.
- `tests/OmniCore.Tests/CheckpointContinuationRegressionTests.cs`: integrado, con referencia XML de TurnResult corregida. Dos pruebas de continuidad y rechazo conservador de un checkpoint incompleto.
- `tests/OmniCore.Tests/ClientTests.cs`: superado por las pruebas Client/autocomplete actuales.
- `tests/OmniCore.Tests/TuiInteractionRegressionTests.cs`: superado por TuiWiringTests de cuestionarios, cancelación, login y aceptación de rutas.
- `tests/OmniCore.Tests/TuiLayoutBoundaryMatrixTests.cs`: superado por la matriz actual de frames reales y layout; no impone los umbrales antiguos.
- `tests/OmniCore.Tests/TuiOverlayNavigationTests.cs`: superado por selector circular y mantenimiento vertical actuales, sin botones de paginación.
- `tests/OmniCore.Tests/TuiOverlayRegressionTests.cs`: superado por escenas de overlays reales y bordes actuales; las aserciones antiguas sobre FrameView no describen el diseño solicitado.
- `tests/OmniCore.Tests/TuiPresentationTests.cs`: superado por las pruebas actuales de presentación.
- `tests/OmniCore.Tests/TuiThemeContrastTests.cs`: criterio adaptado a la paleta de producción en `Current_theme_keeps_readable_contrast_and_uniform_composer_surface`.
- `tests/OmniCore.Tests/TuiWiringTests.cs`: intención recuperada sobre esquema uniforme del composer y contraste; resto cubierto por navegación, render y scroll actuales.
- `xdev/pkg/pulse-audio-sdk_1.3-kite_auto__nobugs-goaptunaip`: excluido. Sólo contiene un comentario sobre binarios de Ubuntu; no es código funcional ni dependencia de OmniCore. No se ejecuta.

### 061-OmniCore-m4-verify-journal — 12 archivos

Diseño alternativo superado por ContextManagementPolicy, ContextCompaction y ContextCheckpointRecorded.
Se conserva un solo contrato y un solo pipeline; no se introducen eventos context.compacted/externalized paralelos.

- `src/OmniCore.Cli/CliApp.cs`: se conserva el cableado actual del contexto.
- `src/OmniCore.Context/ContextMaterializer.cs`: se conserva materialización actual con procedencia y checkpoint.
- `src/OmniCore.Context/ContextPipeline.cs`: no se crea un segundo pipeline.
- `src/OmniCore.Context/MetaModelService.cs`: se conserva el servicio actual.
- `src/OmniCore.Domain/Checkpoint.cs`: no se crea una segunda identidad de checkpoint.
- `src/OmniCore.Domain/Context.cs`: se conservan los contratos actuales.
- `src/OmniCore.Domain/Events.Context.cs`: no se introduce una segunda familia de eventos.
- `src/OmniCore.Engine/EventStream.cs`: se conserva la atribución y validación actuales.
- `src/OmniCore.Host/ConversationHistory.cs`: se conserva reconstrucción actual, sin lector paralelo.
- `src/OmniCore.Host/ExplorerTurn.cs`: se conserva la integración actual; continuidad reforzada por la prueba recuperada.
- `src/OmniCore.Infrastructure/EventCodecs.cs`: se conservan codecs canónicos actuales.
- `tests/OmniCore.Tests/ContextPipelineTests.cs`: capacidades contrastadas con ContextManagementTests (externalización, poda, compactación, reapertura y fingerprint) y continuidad recuperada; no se prueban tipos obsoletos.

### 062-OmniCore-m5-nvidia-closure — 2 archivos

- `tests/OmniCore.Tests/ModelQualificationHostTests.cs`: se conserva la adaptación vigente a QuickProbeSuite; no se devuelve a expectativas antiguas de probes.
- `tests/OmniCore.Tests/QualificationEmptySuiteRegressionTests.cs`: intención ya cubierta por M5QualificationQuickCoverageTests: suite vacía/ids duplicados rechazados antes de llamadas o persistencia.

### 085-OmniCore-qwen-bench-27b-t2 — 6 archivos

Intención: localización de Doctor/Act. El borrador contiene ternarios y callbacks rotos.
Se conserva la implementación actual y sus pruebas CLI reales con proveedor scripted.

- `src/OmniCore.Cli/CliApp.cs`: DoctorLocale y Localization ya conectados.
- `src/OmniCore.Client/Localization.cs`: se conservan recursos es/en actuales.
- `src/OmniCore.Host/OmniCliRuntime.cs`: se conserva resolución actual de textos de Doctor/Act e interacción.
- `src/OmniCore.Host/OmniHost.cs`: sin cambio respecto a su base; no hay trabajo local que recuperar.
- `src/OmniCore.Host/OmniServer.cs`: se excluye inserción corrupta del callback dentro de la decisión de herramientas.
- `tests/OmniCore.Tests/M2WiringTests.cs`: se conserva el cableado vigente; CliEndToEndTests verifica Doctor en inglés explícito y ausencia de claves crudas.

### 086-OmniCore-qwen-bench-27b-t3 — 1 archivo

- `tests/OmniCore.Tests/TuiPresentationTests.cs`: ocho casos de autocomplete ya cubiertos por las pruebas Client/TuiPresentation actuales.

### 087-OmniCore-qwen-bench-t1 — 1 archivo

- `src/OmniCore.Host/OmniCliRuntime.cs`: no se introduce un segundo mensaje de error ni la clave inexistente act.missingInstruction. La implementación actual emite usage localizado una vez.

### 091-OmniCore-qwen-m55-artifactstore — 1 archivo

- `run-tests.ps1`: intención integrada en `tools/test-sqlite-durability.ps1`, utilizando el ejecutable real xUnit v3, ruta independiente del cwd, compilación comprobada y propagación de exit code. No se conserva el filtro dotnet test incompatible.

### 095-OmniCore-qwen-m5usage — 3 archivos

- `src/OmniCore.Client/UsagePresentation.cs`: no se reemplaza el código funcional por dos using y un carácter suelto.
- `src/OmniCore.Protocol/UsageContracts.cs`: se mantienen contratos actuales de disponibilidad, fecha, fuente y cuotas; no se restauran enums incompletos o campos mal escritos.
- `src/OmniCore.Protocol/UsageContracs.s`: excluido; borrador sintácticamente inválido y duplicado, no fuente C# compilable.

UsagePresentationTests y SubscriptionStatusLineIntegrationTests cubren la implementación actual.
Fixtures no acreditan consulta autenticada, cuotas actuales ni consumo real.

## Revalidación de la auditoría histórica

1. Publicación de archivos: hoy existe FilePublishLock, relectura y comprobación final del hash y cancelación antes de publicar. **Persiste la limitación expresamente documentada de un escritor externo no cooperante entre hash y rename**; no se afirma CAS portable.
2. Secretos cortos: hoy se rechazan explícitamente credenciales demasiado cortas; RegisterSensitiveText y SecretLeakTests cubren redacción de valores sensibles cortos. No hay descarte silencioso equivalente al descrito.
3. Schema: ToolRuntime llama ToolSchemaValidator antes de Prepare; ToolSchemaValidationTests cubre su ejecución.
4. Familia nativa: el runtime actual despacha adapters Responses/Anthropic/Chat compatible; no se restaura el adaptador único antiguo.
5. Exclusión global de escritores por sesión: **pendiente arquitectónico, no resuelto por esta recuperación**. EventStream valida y después hace append; el lock/transacción del store no convierte todo read-check-append de múltiples instancias en una sección crítica de sesión. No confundir las pruebas de escritor canónico con esa garantía.
6. Cancelación de mutaciones: comprobaciones presentes antes y durante publicación; se conserva el código vigente.
7. WorkspaceId: Domain deriva de una ruta ya canónica; no decide IsMacOS ni plegado de mayúsculas.
8. Catálogo: FakeCatalog.Add rechaza ids duplicados con ToolRegistrationRejected, incluidos Protected.
9. Errores de herramientas: existe ToolErrorCode, incluido StaleWrite; no depende sólo de parsear una frase.
10. Cuestionarios: el parser produce QuestionnaireValidationErrorCode; presentación localizada fuera del parser.
11. Descripciones al modelo: las definiciones filesystem.read/write actuales están en inglés.

## Evidencia reproducible

- Compilar: `dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet`.
- Continuidad: ejecutar OmniCore.Tests.dll con `-class '*CheckpointContinuationRegressionTests' -noLogo`.
- Contraste/cableado: `-method '*Current_theme_keeps*' -noLogo`. Se fija NO_COLOR sólo dentro del fixture y se restaura; se espera a que el tema real termine de aplicarse.
- Durabilidad: `pwsh -File tools/test-sqlite-durability.ps1`.
- Suite completa: ejecutar OmniCore.Tests.dll con `-noLogo`.
- Arquitectura: ejecutar OmniCore.ArchitectureTests.dll con `-noLogo`.

Logs preservados junto al inventario: recovered-checkpoint-focal.log, recovered-work-focal.log,
recovered-theme.log, recovered-main-full.log y recovered-architecture.log.
Una selección inicial combinó filtros class/method y ejecutó cero casos: **no se contabiliza**.
El primer intento del nuevo test detectó que StartTui puede devolver antes de aplicar el tema;
se corrigió la sincronización del fixture sin cambiar ni debilitar las aserciones de color.

Resultados finales sobre el árbol estable: build 0 advertencias/0 errores; focales 87 PASS
más contraste 1 PASS; helper de durabilidad 7 PASS (solapados con focales); suite integral
2933 casos / 2929 PASS / 0 FAIL / 4 SKIP por permisos symlink, 125.845 s;
arquitectura 56 PASS, 0.700 s. Los recuentos focales no se suman a la suite integral.
Las pruebas recuperadas utilizan proveedores scripted y driver Terminal.Gui real;
no se realizaron llamadas de generación a proveedores ni se modificaron credenciales.
