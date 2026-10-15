# ADR-0053: Fuentes locales de agentes, activación portable y consentimiento Ask

**Estado:** Aceptada para la construcción local M9-G; revisión e integración pendientes.

**Fecha:** 2026-10-15

## Contexto

OmniCore necesita consumir instrucciones, skills y memorias compatibles de ubicaciones comunes de
Codex, Claude y OpenCode. Leer archivos de home o del proyecto sin límites puede cruzar proyectos,
exponer memoria privada o activar extensiones que ejecutan código. El workspace de ejecución puede
ser un subdirectorio y no debe ampliarse por la detección de Git.

## Decisión

El Host descubre únicamente raíces documentadas y configuración de usuario explícita, con límites
de cantidad, profundidad, tamaño y UTF-8. Instrucciones, skills y memorias son entradas tipadas con
proveniencia y estados distintos. Los inventarios de stdio/TUI/Electron contienen metadatos seguros,
nunca cuerpos. El contexto usa contributors separados; las skills portables entran al índice con una
descripción acotada y restricciones de invocación, y su cuerpo sólo llega al request mediante el
flujo autorizado existente `skill.load`. `allowed-tools` no concede permisos. Hooks, scripts, MCP,
shell substitutions, forks y extensiones remotas no se ejecutan ni se simulan como soportados.

La raíz del workspace permanece sin cambios. Si el cwd está anidado, el descubridor puede recorrer
desde el checkout Git verificado hasta el cwd únicamente después de consultar confianza para esa
raíz física por separado. Worktrees enlazados usan su checkout físico para leer fuentes; la identidad
Git común se usa sólo para asociar memorias cuando está comprobada. Un proyecto no confiable no se
lee. Cambios de ruta física, contenido o política invalidan activaciones antiguas.

Las memorias convencionales y de usuario conservan Ask. Memoria de proyecto sólo se materializa con
consentimiento ligado a la identidad exacta del proyecto; cambiar de workspace no transfiere ese
consentimiento. Electron solicita y revoca mediante una confirmación nativa en main, ligada al grant,
conexión y Session actuales. La memoria sigue siendo contexto de baja autoridad, nunca un permiso ni
un cambio de política.

Los imports Markdown Claude y globs locales OpenCode se expanden sólo dentro de raíces autorizadas,
antes de leer se validan límites y reparse points. Referencias externas, rutas que escapan, formatos
no admitidos y metadatos de ejecución incompatibles quedan inactivos con diagnóstico visible. Los
formatos Codex de memoria se limitan a `MEMORY.md`, `memory_summary.md`, `raw_memories.md` y
`rollout_summaries/*.md`; no se recorren bases de datos, transcripciones ni cachés de extensiones.

## Consecuencias

El adaptador es aditivo y no cambia el schema durable ni eleva autoridad del modelo. La compatibilidad
no pretende replicar todos los formatos o extensiones de cada agente. M9-G no cierra M9; OR5 M8,
validación multi-OS, aceptación manual TUI de F/F2 y aceptación live de generación de imagen continúan
pendientes. Evidencia local en `docs/validation/m9-block-g-20261015.md`.
