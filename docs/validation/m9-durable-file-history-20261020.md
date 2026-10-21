# M9-L-MUT-01: historial de borrado y recreación entre procesos

Fecha del corte: 2026-10-20. Actualización de publicación: 2026-10-21.
Estado: **implementado, validado e integrado en main `b81b3c2`**.
La [CI de la revisión `3f5610b`](https://github.com/juancgomezm-s/OmniCore/actions/runs/39546445202)
aprobó sus cinco trabajos antes del merge; la
[CI posterior de main](https://github.com/juancgomezm-s/OmniCore/actions/runs/39550735753)
también aprobó los cinco. El árbol integrado es el mismo que el de la revisión.
Este resultado cierra la entrega nativa descrita aquí, sin acreditar los procesos
escritores generales de M9-M ni cerrar M9 global o la aceptación manual de M8.

## Alcance construido y conectado

El contrato rector es [ADR-0044 §5.1](../adr/0044-politica-de-modelos-y-mutaciones.md#write-edit-normalization)
y su [aceptación M9](../plans/m8-m10-user-localization.md#m9-l-mut-01).

La historia usa tablas versionadas en el journal del workspace, independientes
de los eventos de Session. Guarda identidad de raíz física/ruta, versiones,
tombstones y recibos por llamada/destino. Un bloqueo OS cerca recuperación,
revalidación, barrera, efecto y confirmación durable. La aprobación Ask no mantiene
ese bloqueo; su fingerprint incluye revisión de historia, operación efectiva y
necesidad de creación física, y se vuelve a comprobar al continuar.

Write/patch, la aplicación autorizada de borrados y movimientos mediante
`worktree.integrate` y la acción humana Deshacer consumen esta historia.
CLI/TUI y OmniCoder llegan a la composición común del Host, incluidas sus rutas
de continuación. No se añadieron herramientas independientes
`filesystem.delete`/`filesystem.move`. Cada worktree aislado conserva la historia
de su propia raíz física hasta integrar el lote en el checkout principal.

La recreación se evalúa como ReplaceFile contra la preimagen histórica y exige
también capacidad de creación física, token, permisos y límites, y lectura efectiva
cuando la política la exige. Se conserva el opt-out explícito de lectura previa.
La lectura ordinaria registra hash/cobertura sin obligar a archivar el archivo.
La lectura autorizada de un tombstone devuelve su imagen CAS con marcador de
procedencia y token; no afirma que ese archivo exista físicamente. El I/O interno
no concede lectura al modelo. El replay de cobertura requiere el read solicitado
y completado en la misma Session/Run/Lane.

Los cargos se deduplican por llamada y destino, conservando sus ámbitos Turn/Run,
los cargos legacy del journal y el estado incierto. La consulta de cargos incluye
Applied/Conflict/Unknown; excluye Prepared y NotApplied. Los Prepared permanecen
en la cola de recuperación y se clasifican bajo el bloqueo antes de gastar
presupuesto. No se reejecuta el efecto para recuperarlo.

Las preimágenes de mutación deben ser fieles y verificables. La recuperación no
fabrica un tombstone restaurable a partir de un recibo incompleto. La postimagen
CAS puede omitirse conservando el hash del archivo presente. GC usa las referencias
de historia como raíces aun tras purgar la Session y aborta el barrido ante raíces
requeridas ausentes/corruptas o declaraciones de esquema inconsistentes. Deshacer
participa del bloqueo, publicación CAS y recibos; los propietarios cierran las
conexiones de historia al cerrar el Host. SQLite se abre y cierra por operación,
también durante inicialización y ante excepciones; un objeto de historial inactivo
no mantiene ocupado el archivo del journal.

## Evidencia y límites por recorrido

La tabla distingue efectos probados y revisión de composición. No acredita
todas las combinaciones posibles de carreras, cortes o clientes interactivos.

| Recorrido | Evidencia local |
|---|---|
| Proceso A mueve/borra, termina; proceso B lee/recrea | E2E OS con SQLite/CAS reales; también pasa en Linux WSL |
| Purga de Session y GC sin gracia entre procesos | E2E conserva la imagen necesaria y elimina un orphan envejecido |
| Recuperación antes/después del efecto | Dos focos Prepared→NotApplied/Applied tras reapertura, sin repetir el efecto |
| Recibo/preimagen corruptos | Recuperación queda incierta sin tombstone restaurable; GC aborta |
| Exclusión entre procesos | Fence OS comprobado en Windows y Linux WSL |
| Presupuesto durable incierto | Unknown de otro Turn visible; runtime nuevo rechaza creación sin modificar archivo |
| Prepared y presupuesto | No aparece como cargo antes de reconciliar; NotApplied no gasta; Unknown permanece visible |
| Permisos y límites de recreación | Siete negativos: sin lectura, token viejo, cobertura parcial, techo ReplaceFile, techo CreateFile, DenyPatch y Ask |
| Lote que supera presupuesto | Integración rechazada sin borrar ni crear archivos |
| Alias y worktree hijo | Identidad normalizada y raíces físicas separadas |
| Lectura histórica | Marcador presente en tombstone y ausente tras recreación física |
| Write, patch y Deshacer existentes | Focos de regresión del constructor aprobados |
| Revisión de Host y permisos | Composición y continuaciones revisadas; fingerprint/revalidación de revisión comprobados en fuente |
| Lanes/Run y lectura | Scope de replay y conservación de cargos revisados; no se afirma una prueba OS de todas las combinaciones de Lanes |
| Ausencia externa de ruta conocida | Comportamiento conservador revisado en fuente; no se atribuye actor ni se ofrece creación libre |

## Validación

Un constructor Luna alto y un validador Luna bajo trabajaron **de forma
secuencial**, sin dos workers activos a la vez.

- Build fresco de `OmniCore.slnx`, serial: cero advertencias y cero errores.
- Constructor: `MutationLedgerLifetimeTests` 5/5, `WorkspaceUndoTests` 6/6,
  `WorktreeIntegrationArtifactLimitTests` 1/1, `FilesystemPatchToolTests` 31/31
  y `FilesystemWriteToolTests` 51/51, además de focos nuevos para depurar.
- Auditor independiente: 14/14 focos Windows sobre las DLL actuales. Incluyen
  recuperación, GC, presupuesto, negativas de tombstone, alias/raíces y E2E.
- Linux WSL Debian: los recorridos de fence OS y E2E entre procesos pasan 2/2.
  Son comprobaciones de plataforma de los mismos recorridos, no casos nuevos
  que deban sumarse al total único.
- Ask/Deny/lease de integración: tres variantes aprobadas durante la auditoría.
- `git diff --check` limpio.

La auditoría encontró y reprodujo tres defectos, todos corregidos antes de aprobar
localmente: recuperación sin preimagen fiel, ocultación de recibos Unknown y
bloqueo indebido por mezclar Prepared con cargos antes de su recuperación.
No se cuentan las repeticiones de pruebas como casos nuevos. La aprobación local
no reemplaza la suite completa Windows/Linux, instalación npm y paquetes Linux
que exige CI antes de integrar.

### Corrección de ciclo de vida detectada por CI

La [primera CI del corte `39c87a9`](https://github.com/juancgomezm-s/OmniCore/actions/runs/39541800966)
aprobó la suite Linux, npm en ambos sistemas y el paquete Linux/AOT. Windows
detectó 41 fallos, todos al liberar/eliminar `journal.db`: el nuevo almacén retenía
una conexión que los cierres anteriores de clientes no conocían. La corrección
abre/cierra SQLite por operación sin cambiar el bloqueo OS ni fragmentar
transacciones; no se alteraron esas pruebas para ocultar la fuga.

Un Host del usuario mantenía ocupadas dependencias en la carpeta habitual. Se
conservó su proceso y se compiló la solución en una salida temporal aislada:
cero advertencias/errores. El constructor comprobó los 44 casos de los grupos
afectados y una nueva regresión de acceso exclusivo/eliminación del journal con
el objeto de historia todavía vivo. También aprobó focos de recuperación, CAS
corrupto y exclusión/E2E entre procesos con esa salida. Luna en bajo aprobó
independientemente 18/18 casos seleccionados sobre la DLL aislada y revisó las
rutas SQLite y el fence sin encontrar bloqueos pendientes. La nueva CI completa
debe aprobar el corte corregido antes de integrar; el SHA rojo no habilita el merge.

## Límites conservados

La garantía cubre rutas observadas y escritores conectados. No vigila
universalmente rutas nunca observadas ni atribuye operaciones de procesos
externos arbitrarios. La desaparición no autorizada de una ruta conocida
conserva conflicto por ruta; el arnés no inventa una preimagen.

Las llamadas independientes de borrado y recreación no constituyen una transacción
global: denegar una recreación posterior no deshace automáticamente el borrado
autorizado anterior. El lote de worktree valida su conjunto antes de publicar;
una publicación parcial exige reconciliación, sin prometer atomicidad global.

El control de publicación de procesos escritores generales y futuros wrappers
dinámicos permanece pendiente en M9-M. No hubo inferencia live ni llamadas de
pago. Este corte no cierra M9 global ni la aceptación manual de M8.
