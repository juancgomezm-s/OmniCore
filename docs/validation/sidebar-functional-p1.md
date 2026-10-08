# Panel lateral funcional — TUI-P1

## Alcance

Trabajo en `codex/tui-markdown-polish`, worktree aislado de las conexiones de proveedores que se desarrollan en el checkout principal. Base publicada: `a055bb4`. No modifica autenticación, proveedores ni el scheduler de M6.

- `sessionSidebar`: lectura canónica del Run activo, seleccionando envelopes por Session/Run, con título estable de fallback, modo/estado, revisión, jerarquía y estados derivados del Plan. PII redactada antes de entregar textos.
- `sessionObservability`: contexto de la última solicitud y consumo acumulado independientes. Tokens, capacidad declarada, presupuesto efectivo, porcentaje, costo y desglose conservan disponibilidad. `≈` es estimación; `—` es dato no disponible.
- Diagnóstico básico: bloqueo de recuperación, proyección inválida y secuencia del journal. No contiene razones crudas de recuperación ni credenciales.
- Widget de sesión fijo arriba; planes bloqueados/fallidos tienen Attention. Progreso por hojas: los contenedores no se cuentan dos veces.
- Renderer genérico de listas, colores por rol, modo `NO_COLOR`, navegación y detalle desplegable. Polls idénticos conservan scroll; ambos snapshots se limpian al cambiar de sesión.
- Se elimina la afirmación ficticia «sin archivos modificados»: Files/DiffPreview requieren TUI-P2.
- Corrección encontrada al conectar el panel: `PlanProjection` ahora aplica `plan_item.updated` y conserva el estado original de contenedores al modificar texto/metadata.

## Verificación

Registro final: 2026-10-08 05:53 UTC / 2026-10-07 23:53 America/Mexico_City.

Sin consultas autenticadas ni gasto de proveedor. Los escenarios de modelo son fixtures, con journal SQLite/CAS y la TUI de producción sobre el driver real de Terminal.Gui.

- Compilación net10/net8 de las dependencias: sin advertencias ni errores.
- Focal inicial Client/Presentation/StateProjection/Observability/Sidebar: 126 casos, 0 fallos.
- Reapertura SQLite: snapshot idéntico, sin append; Runs intercalados y actualización tardía no contaminan el Plan activo.
- Wiring nuevo: 2 casos (color/sin color), 0 fallos; comprueba modelo, plan 1/2 → 2/2, estimaciones, tokens de contexto 100 frente a consumo 120, detalle y scroll con altura reducida.
- Arquitectura: 56 casos, 0 fallos.
- PlanService/Impact/StateProjection/Sidebar finales: 45 casos, 0 fallos, incluido renombrado de un contenedor Failed cuyo hijo se reabre; no se congela el estado derivado del padre.
- Exportación visual: 22 escenarios aprobados dentro de la suite final. PNGs derivados de las celdas reales del driver; **no son capturas del escritorio**. Inspección visual a 100×30 y 120×35: modelo, Plan, contexto, consumo y diagnóstico, sin marcos; colores por rol sobre superficie contrastante.

### Suite completa, ejecución por lotes disjuntos

El primer intento paralelo tuvo 3037 casos / 3032 PASS / 1 FAIL / 4 SKIP (249.737 s): timeout de `M4ProcessRecoveryTests.Long_session_checkpoint_and_pending_questionnaire_survive_a_new_process`. Se conserva `.tmp-sidebar-full.log` y la evidencia independiente `omnicore-m4-process-e8401c93cdb64f2ea2f3a38a69db757f` en el directorio temporal. El fallo no se elimina ni se acredita ese intento como verde.

Verificación final sobre el código de entrega, sin ampliar los 120 segundos del subprocess ni relajar assertions:

1. M4 aislado, `-class '*M4ProcessRecoveryTests' -parallel none -maxThreads 1`: **1 PASS**, 111.581 s; log `.tmp-sidebar-m4-serial.log`.
2. Resto completo, `-class- '*M4ProcessRecoveryTests' -maxThreads 2`: **3037 casos / 3033 PASS / 0 FAIL / 4 SKIP**, 184.575 s; log `.tmp-sidebar-full-final.log`.

Total sin solapamiento de esos dos lotes: **3038 casos / 3034 PASS / 0 FAIL / 4 SKIP**. Los cuatro skips son los gates de symlink por permisos. Arquitectura (56 PASS) y focales son ejecuciones adicionales solapadas, no se suman a ese total. No se acredita una ejecución monolítica paralela sin el timeout.

Artefactos locales del worktree: `.tmp/sidebar-final-frames/sidebar-100x30.png` y `sidebar-120x35.png`; fuentes SVG/texto preservadas. Binarios de prueba: `.tmp/sidebar-build/`. No se incluyen logs/binarios generados en Git.

## Pendientes ubicados en el plan

TUI-P2: ChangedFiles atribuibles y DiffPreview. TUI-P3: configuración persistente por widget, pestañas reales y colapso por altura (la superficie desplazable de P1 no acredita esos requisitos). M6: agentes/inspector operativo. M7: agrupación/integración de worktrees. M8: Session Memory y diagnósticos/widgets de extensiones. M10: memorias Project/Workspace/Global y keybindings. Véase arquitectura §24.1 y ADR-0032.
