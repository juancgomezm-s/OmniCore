# Herramientas de frontera y conservación de archivos

La autorización explícita FullAgent recibe todo el catálogo elegible desde la primera
invocación. La ausencia de cualificación no se convierte en un límite artificial de seis
herramientas ni en una puntuación empírica inventada. Las categorías limitadas mantienen
su intersección con el harness; los límites y capacidades reducidos por el usuario,
AgentProfile, disponibilidad del runtime y Permission Engine continúan vigentes.

El preset FullAgent no impone un número fijo de herramientas visibles. Los perfiles
guardados anteriormente conservan sus límites hasta que el usuario los cambie. Conceder
acceso a herramientas no autoriza sus operaciones por sí mismo.

## Contrato de archivos

- `filesystem.write` sólo crea destinos ausentes, con publicación sin sobrescritura.
  Rechaza archivos existentes aunque el modelo sea FullAgent, presente un token correcto
  o haya leído previamente. Conserva la lectura de argumentos de journals históricos.
- `filesystem.patch` exige la versión vigente y conserva sus controles de lectura,
  ámbito, ambigüedad, codificación, presupuesto de mutaciones y validación posterior.
- El parche escribe los bytes diferentes mediante un handle del archivo existente.
  No ejecuta delete, rename-overwrite ni File.Replace. Si cambia la longitud, desplaza
  también el sufijo necesario; no pretende que el sistema de archivos inserte bytes.
- Las instrucciones del turno prohíben borrar/recrear, reemplazar mediante temporales
  o usar shell/process para eludir la edición localizada. No constituyen una sandbox
  para procesos arbitrarios: un proceso autorizado puede escribir archivos. Las
  operaciones destructivas y la integración de worktrees conservan su autorización
  propia; no se anuncia un bloqueo absoluto de cualquier proceso posible.

## Durabilidad y recuperación

Esta decisión actualiza la publicación atómica del parche descrita en ADR-0044/M3:
la sustitución atómica conserva contenido, pero cambia la identidad del archivo.
Se prioriza conservar el archivo original para editores, watchers y enlaces existentes.

Antes de modificarlo se guarda y sincroniza una preimagen temporal hermana. La
composición normal conserva además BeforeStateRef y hashes pre/post en el journal
durable antes del efecto. Se verifica la versión usando el mismo handle que se editará,
bajo el bloqueo por ruta. En Windows FileShare.Read impide escritura, borrado y rename
concurrentes durante esa apertura. En Unix los procesos externos no cooperantes
siguen siendo una limitación; no se afirma un compare-and-swap entre procesos arbitrarios.

La cancelación se comprueba antes de comenzar el commit. Un fallo ordinario durante
la escritura restaura los bytes originales por el mismo handle. Si también falla la
restauración, se conserva la preimagen temporal y se devuelve error. Una caída abrupta
puede dejar contenido parcial: el reconciliador lo clasifica Conflict, nunca Applied
ni NotApplied, y no repite la mutación automáticamente. Esta escritura no ofrece
visibilidad atómica para lectores concurrentes.

## Validación

Las pruebas de identidad mantienen un handle abierto antes del parche: tras editar,
ese handle debe leer exactamente el contenido actualizado. Una sustitución física
fallaría esta comprobación porque el handle seguiría apuntando al archivo anterior.
Se comprueban crecimiento, reducción, edición de igual longitud, BOM, finales CRLF,
fecha de creación en Windows y restauración después de un fallo inyectado durante la escritura.

La aceptación real Luna → DeepSeek conserva la misma sesión y Run, envía cada petición
una sola vez, exige crear un archivo real y modificar sólo una línea, recuerda una
clave exclusiva de la conversación y verifica también el handle original. Luna usa
FullAgent, sin limitar artificialmente su catálogo. Esta comprobación usa proveedores
reales y la TUI completa con el driver DOTNET de Terminal.Gui; no automatiza una ventana
de Windows Terminal.

Conservar identidad y emitir cambios del archivo evita la sustitución detectada.
No acredita por sí solo el historial o Undo de una extensión concreta de Visual Studio:
esa integración requiere comprobar su comportamiento dentro del editor.

## Resultados del 17 de octubre de 2026

| Comprobación en Windows, .NET 10.0.401 | Resultado |
|---|---|
| Compilación Release | Correcta, cero errores y advertencias |
| FilesystemPatch/Write, preimágenes, ModelPolicy, M3PolicyBoundary y procesos | 164 aprobadas |
| Arquitectura | 62 aprobadas |
| Flujo real de filesystem y verificador, incluidos módulo desconectado y mutación posterior | 3 aprobadas |
| Aceptación TUI con proveedores reales Luna → DeepSeek v4 Flash | 1 aprobada, 44,7 s |

La aceptación final conserva 19 de 20 líneas y cambia únicamente `estado: pendiente`
a `estado: editado por DeepSeek`. El handle original lee el resultado actualizado.
Luna ejecuta list/write/read con FullAgent; DeepSeek ejecuta read/patch/read. La sesión
y el Run no cambian, se autoriza una vez la ruta nueva y la petición no se reenvía.
Los datos de prueba quedan en `live-frontier-file-identity-final-20261017`, fuera del
repositorio y de la conversación del usuario. No se publican credenciales.

La revisión ampliada detectó un servidor de C# con todos sus hilos suspendidos después
de ejecutar el verificador temporal dentro de un job acotado. El proyecto temporal de
esa prueba ahora fija `UseSharedCompilation=false`: el compilador no sobrevive al job
ni bloquea compilaciones posteriores. Las tres variantes del verificador siguen activas
y aprobadas; sus aserciones incluyen ahora el diagnóstico de etapas incompletas.

No se inició WSL ni se acredita una nueva ejecución Linux o de GitHub Actions en esta
validación local. Las pruebas de identidad usan un handle original en ambas plataformas;
la aserción específica de fecha de creación sólo se exige en Windows.
