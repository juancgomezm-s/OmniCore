# Cierre global P0-OMNI-UNIFICACION — 2026-10-14

**Estado: CERRADO en el alcance aceptado por el usuario.** Se revisaron los tres planes,
sus matrices, la evidencia registrada por cada owner y los cierres locales, preservando las
interfaces existentes. El usuario confirmó en esta conversación: **«Sí; acepto ese alcance
y ya funciona la animación»**. La pregunta especificaba OmniCoder Electron, OmniCore CLI/TUI
y OmniLlama React; la WebUI Svelte alternativa queda fuera de esta fase.

La confirmación resuelve la aceptación pendiente y el reporte del movimiento de OmniLlama
en el otro equipo. Se registra como confirmación del usuario; este agente no inspeccionó ese
equipo ni volvió a ejecutar allí la prueba. No se atribuye a una compilación o a una fixture.

## Base y evidencia por producto

| Producto / superficie | Base aceptada y evidencia | Límite |
|---|---|---|
| OmniCore CLI/TUI | `9946a7ff10ca80a6468c6fc7d925156ae841d2a4`. U-C01–U-C12 resueltos; corrección de texto literal/borrador. Build 0 warnings/errors; 316 casos del área verificados mediante lote y pasada focal, más 62 de arquitectura. [Acta local](omnicore-homologation-closure-20261014.md). | Diferencias de terminal/capacidad explícitas. OR5 autenticado no se acredita con fixtures. |
| OmniCoder Electron/React | `95bbeeaac6c9c9e8aeef369112944f9f8a6fca38` más las correcciones locales de roles/foco/selección, acceso a Trabajo/Objetivos y E2E registradas en `PLAN_REALISTA.md` §11.0. Owner: build EXIT 0; app 49/49; ProductUI Electron 6/6; `NO_ORPHANS`. Se revisaron los cambios y sus hashes antes del cierre. | Pruebas de UI con demo/mock, no proveedor autenticado ni ejecución Core. Se conserva agrupación de proyectos/sesiones y backend actual; la migración de runtime no está cerrada. |
| OmniLlama React (`--webui auto`) | `44e9bfeda5d926531e76c5c4a1cd4e415f3615cf`, árbol limpio al revisar. `OMNI_LLAMA.md` y `BRANDING.md`: 59/59 UI, TypeScript/Vite/ESLint, build nativo y smoke de modelo/bundle/chat/SSE; navegación, Markdown/copia, contexto y RAG/MCP real registrados. Infinito observado con Qwen3.8 y barras verificadas con stream de QA sin tokens nuevos. Logs locales presentes. | Las fases guionizadas siguen identificadas como QA; inferencia real separada. El usuario confirma funcionamiento remoto de la animación. Svelte no se declara homologado. |

Los resultados anteriores se reutilizan; esta revisión de planes no volvió a ejecutar las
suites de los otros productos ni convierte sus resultados en pruebas nuevas de este agente.
Las fuentes de OmniCoder estaban sin confirmar al iniciar: la base se identifica por HEAD
y hashes, sin presentarla como código ya contenido en `95bbeea`. Su integración documental
y de cambios revisados se registra por separado en el repositorio del producto.

Hashes SHA-256 de las fuentes/pruebas locales aceptadas de OmniCoder:

| Archivo | SHA-256 |
|---|---|
| `packages/app/src/renderer/Sidecar.tsx` | `4aeeab34143759fdc2fdd927421815232a53b1921ec15c24ea68380db613debc` |
| `packages/app/src/renderer/app.css` | `2c7bf6a27b08120b03df04f560f437124b364a3a6c491d01b4e2441f7c697063` |
| `packages/app/src/renderer/index.html` | `c57a5893dc3cdee5056ae242cee3e38289838b195ddf9e2667ba555d509d0c51` |
| `packages/app/src/renderer/theme.ts` | `1d2621e93ba852ee585be3b2876f755ad7e03273fa50b326bc8d39012d94b0df` |
| `packages/app/test/e2e/helpers/electron-app.ts` | `04fc0820c109c513937e78d6bb68eadfb038b62b5c9a7315cbcf55935fc45a39` |
| `packages/app/test/e2e/product-ui.e2e.test.ts` | `0d88faf6f266acd6b1dd67362650933f8c5f13719028c2ff8e5b9065e3741b2f` |

## Resolución compartida

- Marca, roles, color/foco/selección: se conservan los tokens semánticos existentes y su
  adaptación terminal; no se exige identidad de píxeles ni se transforma el logo de OmniLlama.
- Chat/composer, envío/cancelación, código/copia, ajustes/persistencia y contexto: los
  recorridos disponibles tienen evidencia por producto. Se aceptan las diferencias de
  capacidad documentadas; no se reconstruye la navegación de proyectos de OmniCoder ni los
  menús de Core para imitar una lista de conversaciones web.
- Herramientas, fuentes y métricas: catálogo/selección no equivale a ejecución; se conserva
  autorización y procedencia reales. Datos ausentes permanecen desconocidos. Las fixtures de
  tarjetas, proveedores o streaming no se presentan como éxito autenticado.
- Idioma y accesibilidad: Core conserva es/en/fr/it, `NO_COLOR` y controles de movimiento
  existentes; los otros productos conservan su cobertura y diferencias declaradas. El cierre
  no afirma cuatro traducciones nuevas en Electron/WebUI ni traduce contenido del usuario.
- Superficies futuras: `media.read`, ConPTY, palette/bindings de Core y Electron→Core no se
  convierten en requisitos retrospectivos de homologación. Svelte queda explícitamente fuera.

## Continuación autorizada

Publicación confirmada del registro de cierre en `main`: OmniCore `527925b`, OmniCoder
`ecba22e` (incluye las fuentes/pruebas locales revisadas y su evidencia ya registrada),
OmniLlama `b384da9` (documentación de cierre sobre `a620b4e`, que fija LF para builds WebUI
reproducibles en Windows; no cambia las funcionalidades aceptadas de `44e9bfe`). Los tres
push terminaron correctamente. Esto no afirma CI remoto nuevo aprobado ni repite suites.

El bloqueo de ampliaciones por `P0-OMNI-UNIFICACION` queda levantado en los tres planes. El
usuario ordena empezar **M9 con un solo worker Luna en alto**, preservando lo existente y
evitando trabajo y regresiones duplicadas. Se comienza por Host separado, transporte stdio,
negociación/versiones/capabilities y consumo real desde el cliente; cada entrega debe estar
cableada y verificarse cruzando procesos, sin sustituir el transporte por una fixture.

**M8 no se declara cerrado:** OR5 autenticado sigue pendiente de configuración/aceptación de
la cuenta. La autorización actual permite iniciar M9 conservando ese pendiente explícito;
no autoriza pruebas pagadas ni introducir credenciales en código, renderer, journal o logs.
Lease/reconexión, origen humano externo, schemas/goldens para TS, `media.read` y procesos
persistentes/interactivos conservan los criterios de salida de M9. Iniciar su primer bloque
no acredita el cierre de todo el hito ni la integración Electron completa.
