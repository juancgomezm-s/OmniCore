# Integración de menús y preferencias en main — 13 de octubre de 2026

Objetivo: terminar los menús del plan independiente, integrar sus cambios sin
perder la base existente y dejar preparada la continuación de M8. Este cierre
no declara terminado M8 ni certifica todos los proveedores.

## Auditoría de alcance

La base `29c2c26` ya contiene las ramas históricas de preferencias y M8:
`git merge-base --is-ancestor codex/tui-omega-animations main` y el mismo comando
para `codex/m8-extensibility-20261011` devolvieron cero. `git fetch origin main`
confirmó que main local y remoto coinciden antes de la integración. No se vuelven
a fusionar checkpoints viejos ni se sustituyen archivos con versiones anteriores.

| Requisito del plan | Código actual y evidencia que lo ejercita |
|---|---|
| P0: base consolidada y cambios preservados | Ancestry de Git comprobada; integración nueva desde `codex/preferences-harness-closure-20261013`. |
| P1: idioma User, español por defecto, precedencia, guardado y cancelación | `UiLocalePreferencesTests`, `LocaleRuntimeTests`, `PreferencesLocaleContractTests`; parser/Host y revisiones preservan campos ajenos. |
| P2: nueve categorías, jerarquía, búsqueda, foco y reflow | `TuiApp.Preferences`, `Preferences_center_reflows_live_without_losing_search_category_locale_or_focus`, recorridos de General y frames reales. |
| P3: cuentas separadas, API por capacidades, motores y propiedad | `TuiWiringTests.AccountPreferences`, `LocalEnginePreferencesTests`, `Local_engine_page_is_read_only_until_confirmed_and_targets_selected_provider_for_start_and_stop`; listar no inicia login/red/procesos. |
| P4: catálogo, rutas, seguridad, trust, sidebar y animaciones | `ModelCatalogPreferencesHostTests`, `PreferencesOperationsTests`, `TuiWiringTests.PolicySecurityDiagnostics`, pruebas de grants, sidebar y animaciones. |
| P4 ampliado: controles por modelo efectivos, no decorativos | `ModelPolicyProfileControlsTests`, `ExplorerTurnGenerationDeadlineTests`, `ModelPolicyStoreTests`; capacidades, mutaciones, límites y coordinación se verifican en Host/runtime además de la UI. |
| P5: contexto, memoria Session, skills, extensiones y diagnóstico | `TuiWiringTests.SessionPreferences`, `CommandCatalogPresentationTests`, `PreferencesOperationsTests`, recorridos de consentimiento por sesión/hash y GC preview/cancelación. |
| P6: recursos propios, ayudas, aliases e invocaciones es/en/fr/it | `LocaleCatalogCoverageTests`, `LegacyLocaleCoverageTests`, `HumanCommandAliasTests`, pruebas de CLI/localización; no se traduce JSON, IDs ni contenido externo. |
| P7: recorridos completos, evidencia visual, compilación y regresión | Suite final de tests y arquitectura; capturas del driver inspeccionadas y aceptación PTY histórica separada de una captura del escritorio. |
| Cuotas voluntarias | Catálogo y runtime omiten cuotas no declaradas; pruebas de presupuesto, delegación, salida opcional y registro/discovery preservan cuotas explícitas. |
| README y comparación de contexto | Imágenes reales de fixtures, panel descrito y nueve requests capturadas de cinco harnesses. `tools/test-initial-context-payload.cjs` y comprobación de hashes/cifras HTML/README. |

La matriz vincula el alcance a recorridos de comportamiento, no solamente al
nombre de un test o a la existencia de un diccionario. Los registros históricos
del 11/12 de octubre permanecen como historial, no como pendientes vigentes.

## Corrección final de menú

El nuevo formulario de controles de modelo mostraba y analizaba el ratio de
reescritura con cultura invariante. Se corrigió para usar `PreferenceInput.Culture`:
francés e italiano aceptan `0,5`, español mexicano e inglés `0.5`; el valor
persistido sigue siendo numérico/invariante. Los ocho recorridos de cuatro
idiomas por dos tamaños comprueban guardar 0,5/0.5, reabrirlo y cancelar otro valor
sin modificar lo persistido. No se cambian las guardas de autoridad ni los límites
del perfil para hacer pasar la prueba.

## Validación e integración

Estado: **integrado en main** mediante el merge local `63fcb5e`, sin conflictos.
Implementación: `2fa931c`; evidencia, imágenes y comparación: `3dfc357`.
Correcciones finales de borrador/discovery: `08ef1ac`, integradas por `cb67591`.
La comparación de árboles posterior a la fusión confirma que main conserva
exactamente el código/tests verificados de la rama de cierre, además de su base.
Build de tests: **cero warnings/errores**, 14,72 s. Arquitectura: **62/62 PASS**
(`TestResults/menu-closure-architecture-20261013.trx`). Regresión completa:
**4276 casos / 4265 PASS / 0 FAIL / 11 SKIP**, del 13 de octubre a las
19:18:20–19:26:13 UTC, incluyendo **185 casos TUI** y los ocho de controles
por modelo con el formato decimal corregido
(`TestResults/menu-closure-final-20261013.trx`).

Se comprobaron en el TRX los resultados aprobados de cada clase de la matriz,
no sólo su existencia en el repositorio. Las once omisiones son cuatro casos de
symlink sin permisos y siete aceptaciones live opt-in; no son herramientas ni
menús declarados aprobados sin ejecutarse. Los grupos se solapan y no se suman.
Los logs de intentos anteriores se conservan.

### Adenda de validación del borrador

La auditoría posterior al primer merge reprodujo una pérdida de borrador: guardar
un valor inválido reemplazaba el editor de perfil por un aviso. Los ocho casos
ampliados fallaron con ese comportamiento (`menu-draft-repro-20261013.trx`).
Ahora las validaciones y conflictos de revisión se muestran dentro del formulario,
mantienen sus campos y no escriben sobre la revisión nueva de otro editor.
Cancelar conserva esa revisión concurrente. Guardar y Cancelar regresan al listado
sin iniciar discovery ni refresco remoto implícito.

Después del cambio de presentación, el grupo TUI/catálogos/perfil pasó **192/192**
(`menu-draft-final-20261013.trx`). La comprobación final de los ocho recorridos,
con un Host que sí admite discovery y una cuenta sintética conectada, pasó **8/8**
(`menu-draft-discovery-final-20261013.trx`): se comprueban tanto el contador de
llamadas como la ausencia del handle de refresco, no sólo un cero antes de que una
tarea arranque. El build final dio cero warnings/errores. Estos grupos se solapan;
complementan la regresión general anterior y no se suman como casos únicos.
Arquitectura después de esa corrección: **62/62 PASS**
(`TestResults/menu-draft-architecture-final-20261013.trx`). El árbol de código/tests
de main coincide con la rama de esta última entrega; no quedan cambios sin commit.

## Evidencia visual y límites

Las capturas del README y las de `assets/preferences-20261012/` proceden de las
celdas de Terminal.Gui, con datos sintéticos. Los frames de controles por modelo
del 13 de octubre se inspeccionaron con colores reales; previews recoloreados de
exportaciones NO_COLOR no se publican como evidencia. La aceptación PTY del
[registro anterior](closure-live-and-terminal-20261012.md) acredita teclado,
preview, guardar/cancelar y reinicio, no una fuente o ventana específica de
Windows Terminal ni login autenticado completo.

Frames finales inspeccionados sin recolorear, con foco en el bloque inferior:
[80×25](assets/preferences-20261013/settings-model-profile-es-80x25.png) y
[120×35](assets/preferences-20261013/settings-model-profile-es-120x35.png).
Los SVG originales se conservan junto a los PNG para regeneración.

La prueba Luna → Luna se conserva en [el informe del harness](harness-controls-20261013.md):
espera real de 60 segundos y una continuación del principal. Qwen no queda
certificado por terminar una espera ni por un catálogo verde; su último fallo
de proveedor está documentado. Estas campañas son independientes del cierre de
los menús y no se incluyen automáticamente en la regresión.

## Continuación de M8

Los menús ya exponen los backends disponibles. El plan
[M8–M10 e idiomas](../plans/m8-m10-user-localization.md) conserva el trabajo de
backend pendiente: `toolPreferences` de selección/resolución de extensiones,
hooks/MCP, skills de otros scopes/aportes y candidatos/uso de memoria. No se
confunde ese futuro contrato con los controles por modelo ahora operativos.
Nuevas identidades OAuth y adaptadores de conexión sin backend tampoco se
presentan como botones funcionales. Cada capacidad nueva deberá conectarse a
estas superficies y aportar recursos/recorridos en los cuatro idiomas.

No se modificaron cuentas, credenciales, servidores, TLS ni modelos del usuario.
Esta integración es local; el objetivo no autoriza un push ni borrar ramas.
