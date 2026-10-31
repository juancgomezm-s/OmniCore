# Inicialización local del proyecto

`omni inicializar` y `/inicializar` generan `agents.md` en la raíz del workspace actual.
Si existe `AGENTS.md` u otra variación de mayúsculas, actualizan ese mismo archivo.
La presencia de dos variantes en sistemas sensibles a mayúsculas es un error visible.
No se asciende automáticamente a un repositorio padre: la autorización y la confianza
corresponden a la raíz actual. Un proyecto no confiable requiere primero la decisión
normal de confianza del workspace.

El inicializador conserva íntegro el texto manual fuera de
`<!-- omnicore:project:start -->` / `<!-- omnicore:project:end -->`.
Sólo reemplaza ese bloque; un bloque incompleto o duplicado detiene la actualización.
Muestra fuentes con rutas relativas y SHA-256, cambios y diagnósticos. Repetirlo sin
cambios no reescribe el archivo. Revalida confianza, fuentes y preimagen antes de
reemplazarlo atómicamente; una modificación concurrente exige ejecutar de nuevo.

Se inspeccionan README y manifiestos conocidos de la raíz, instrucciones AGENTS/CLAUDE
locales y memorias Markdown convencionales: `MEMORY.md`, `MEMORIES.md`,
`memory_summary.md`, `raw_memories.md`, así como `.md` bajo directorios `memory`,
`memories`, `.memory` y `rollout_summaries`. No se siguen imports o enlaces de las
fuentes. La salida incluye un inventario y extractos deterministas, como datos JSON
con procedencia y advertencia de baja autoridad: no ejecuta ni promueve instrucciones
contenidas en memorias. No usa un LLM ni abre bases de datos nativas, transcripciones,
memorias de home o proyectos vecinos.

Límites: 64 fuentes, 64 KiB UTF-8 por fuente, 8.192 entradas, profundidad 12 y
28.000 caracteres aproximados de contenido generado, con hasta 1.200 caracteres
por extracto. Las fuentes demasiado grandes, inválidas o con posibles credenciales
se excluyen con diagnóstico. Directorios generados, otros repositorios, secretos,
symlinks y junctions se excluyen. Se respetan exclusiones locales `.gitignore`,
`.ignore`, `.omniignore` y `.git/info/exclude` local mediante globs, clases y literales escapados. Los patrones no admitidos omiten el directorio con diagnóstico; un worktree cuyo `.git` apunta a metadata externa omite fuentes. Una negación no vuelve a
abrir rutas excluidas. No se ejecuta Git ni scripts durante el descubrimiento.
