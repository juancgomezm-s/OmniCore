# Integración conjunta de OmniCore — 2026-10-28

Juan autorizó recuperar el avance efectivo de las sesiones, integrarlo en Main
y publicarlo al remoto. Esta autorización sustituye la pausa de publicación de
los checkpoints históricos; no declara cerrados globalmente M9, M10 ni M8.

## Avance recuperado

- Main local partía de `d823e2d4`, con 76 commits por delante de
  `origin/main` (`f02d3b5`). No había commits remotos pendientes de integrar.
- Los worktrees `codex/m9-completion-20261022` y
  `codex/companion-session-channel` estaban limpios. Sus commits ya eran
  ancestros de Main; ninguna rama local tenía commits fuera de Main.
- Se conservan M9 y sus límites de aceptación, las correcciones visuales,
  el motor de teclado y comandos de M10, el catálogo OpenRouter, los controles
  de ejecución, las actualizaciones de CLI y el trabajo de la mascota. La
  entrega de sesiones y mensajes laterales de la mascota está en `d823e2d4`.
- Consolas Regular con ClearType y el tamaño equivalente al mock de 16 px
  están en `0710aca1`. La preferencia lateral compacta sigue guardada y la
  mascota conserva sus archivos y comportamiento.
- Se incorporan los cambios pendientes de investigación web opt-in,
  persistencia y presupuesto compartido por Run, supresión de evidencia
  repetida en contexto, compatibilidad de permisos de búsqueda local y nombres
  de herramientas de los tres adaptadores de proveedores.
- Los respaldos Git se conservan. Se verificó que los archivos antes no
  rastreados del respaldo OpenCode ya están versionados en Main y que los
  cambios de importación y acta del respaldo M9 ya están incluidos. No se
  reaplicaron versiones históricas sobre las implementaciones posteriores.

La propuesta más reciente de interacción y memoria de la mascota seguía en
fase de análisis solicitado por Juan; no se presenta como una implementación.
No se modifica ni publica OmniCoder/JayCoder desde esta integración.

## Correcciones detectadas al combinar las entregas

1. Los tests web y CLI aún esperaban `filesystem_search`, `search_web` y
   `web_read`. Se alinean con el vocabulario vigente: `grep_files`,
   `web_search` y `web_fetch`. Los IDs canónicos y schemas no cambian.
2. La importación de un documento inválido ya se rechazaba, pero el Host
   devolvía `conversation.failed` y la CLI salía con código 1. Antes de crear
   una Session, `InvalidDataException` ahora conserva
   `conversation.import.invalid` y el código 2. Los fallos posteriores a un
   intento durable mantienen su tratamiento anterior.
3. La limpieza del fixture nativo no liberaba el pool SQLite nuevo del buzón
   de la mascota. Ahora libera sólo ese pool propio, con su cadena exacta y
   después de detener su Host. No se desactiva el pooling productivo ni se
   cierran conexiones o procesos ajenos.

## Validación local

Se reutilizaron el checkout, las dependencias restauradas y los directorios
de compilación existentes; no se copiaron repositorios, modelos o distribuciones.
La compilación Release de tests, arquitectura y arnés de aceptación pasó sin
advertencias ni errores. El arnés de aceptación sólo se compiló, no se ejecutó
contra servicios reales.

| Tanda | Resultado inicial | Cierre |
| --- | --- | --- |
| Web, contexto, permisos, proveedores, workflows y mascota | 408/409 | La expectativa de alias pendiente pasó en la tanda CLI/web |
| Arquitectura | 62/62 | Aprobada |
| CLI real y web | 80/81 | La clasificación del documento inválido pasó tras corregir el Host |
| Panel, mascota, densidad y stdio | 11/12 | La limpieza del pool propio pasó tras corregir el fixture |
| Importación, replay, parser y extensión nativa afectados | 16/16 | Aprobada en el binario final |
| npm | 37 aprobadas, una omitida | La omisión es creación de enlaces simbólicos no disponible |

La unión de los resultados por nombre de caso, conservando el resultado más
reciente de cada uno, contiene **564 casos .NET únicos aprobados y cero fallos
pendientes**. No es una ejecución indiscriminada de la suite completa ni
aceptación de modelos/proveedores reales. La matriz completa Windows/Linux
corresponde a CI después del push; este registro no anticipa su resultado.

Evidencia local ignorada por Git: `artifacts/integration-main-20261028/`
(logs, XML y resumen de resultados). Sólo código, pruebas y documentación se
incorporan al commit; no binarios, bases de datos, fuentes o capturas.
