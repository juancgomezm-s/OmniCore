# M9: publicación de propuestas de procesos de extensión

Revisión: 2026-10-22. Estado: **primer vertical construido y aprobado localmente
por Luna bajo**. La publicación se condiciona a los cinco jobs de CI del PR
asociado. No acredita el cierre global de M9 ni la aceptación manual de M8.

Base estable: main `b81b3c2`, con
[CI posterior al merge aprobada](https://github.com/juancgomezm-s/OmniCore/actions/runs/39550735753).
La plataforma se preservó en `6433efd`; `67e7fba` combina esa base con el contrato
consolidado. El trabajo se aisló en `codex/m9-batch-publication`, sin incorporar
cambios sin commit de otra sesión. La CI del checkpoint anterior no sustituye
la aceptación de esta entrega.

## Recorrido construido

Las extensiones escritoras v1 ejecutan su proceso Strong sobre una copia privada
de los archivos Git autorizados para lectura o escritura. No reciben escritura
al checkout ni al common-dir originales. No se copian secretos, entradas
ignoradas ni archivos no rastreados del usuario. La salida incluye archivos nuevos
ignorados, con límites de archivos, bytes, entradas y profundidad. Si un destino
omitido de la copia ya existe físicamente, no se lo acepta como creación nueva.
La ruta v1 de sólo lectura conserva sus permisos exactos y no recibe escritura
a toda la raíz. No se añadió fallback Weak.

El productor conserva una propuesta CAS protegida e inmutable. Su identidad
incluye workspace, llamada/fuente, Run/Lane, Turn de origen y hashes de paquete y
manifiesto. `extension.batch.apply` resuelve las operaciones reales por archivo
antes del permiso y revalida consentimiento, paquete y alcance actuales. No se
fabrican metadatos de ownership Git ni se encadenan escrituras independientes.

El lote completo atraviesa permisos nativos por destino, fronteras de modelo y
Session, lectura previa/versiones, presupuesto agregado y fence de workspace.
Se comprueban preimágenes y topología antes de publicar y junto a cada efecto;
se verifican postimágenes y se registran recibos e historia durable. La recreación
de un tombstone usa la preimagen histórica para lectura, operación y cargo
lógicos, conservando la ausencia física en el recibo de reconciliación. Leer
internamente no concede lectura al modelo.

| Perfil User vigente | Comportamiento |
|---|---|
| Normal | Ejecutar produce una propuesta; aplicar el lote es una segunda acción. Los Ask necesarios muestran los destinos y se resuelven por lote, con aprobación de una llamada. |
| Autonomous en Act | Ejecutar, validar y publicar es una operación continua. No requiere otro paso de inferencia ni abre diálogos ante Ask, Deny, conflicto o límite. La aplicación explícita usa las mismas reglas. |

Se reutiliza únicamente el perfil de permisos existente. La configuración User
vigente se vuelve a leer para este recorrido; una copia anterior de la política
no conserva autonomía o reglas retiradas. Cambiar el perfil no inicia trabajo.
Los techos y reglas siguen restringiendo con permisos autónomos. El aplicador
Core autoriza el intent real Reconcilable; no simula un efecto None para elevarlo.

Proceso y publicación mantienen dos barreras durables y un vínculo causal Host.
La conversación, el proveedor y la proyección del cliente muestran la acción
original. El resultado compacto informa `publication=applied`, `blocked` o
`unknown`; no presenta una propuesta pendiente cuando ya se publicó. La
reconstrucción del historial valida el vínculo interno antes de excluirlo.
Los efectos inciertos conservan su estado y no relanzan el proceso. El lote no
promete atomicidad global entre archivos.

## Construcción — Luna alto

Un único worker construyó el bloque; terminó antes de iniciar Luna bajo. Sin
llamadas de inferencia live o pagadas.

~~~powershell
dotnet build OmniCore.slnx --artifacts-path C:/Users/juanc/AppData/Local/Temp/omnicore-m9-extension-batch-build -p:UseSharedCompilation=false -m:1 -nr:false
~~~

Resultado: exit 0, cero errores y advertencias. El auxiliar de proceso se copia
desde GetTargetPath, incluyendo el apphost sin extensión en Linux; no se toma
del bin convencional al usar artifacts aislados.

Luna alto aprobó 13/13 focos y 2/2 casos de snapshot, sin fallos ni omisiones.
Los siete escenarios Strong ejecutaron AppContainer real. La prueba autónoma
inspecciona la siguiente solicitud al proveedor: un resultado de la llamada
original con `publication=applied` y rutas seguras, sin llamada ni resultado de
la subacción interna; el journal conserva ambas barreras. La fixture de modelo
acredita cableado y serialización, no inferencia live de un proveedor.

La prueba completa detectó y corrigió un rechazo permanente por formato del
ToolCallId. También se corrigieron acceso RW excesivo en la ruta lectora,
reentrada del bloqueo de workspace, cargos físicos en vez de lógicos al recrear,
estado ausente en Preview y llamadas internas visibles tras replay. Un build
aislado no se contó como aceptación funcional.

## Validación independiente — Luna bajo

Aprobación local: **85/85 más 1/1, sin fallos ni omisiones**. Son 86 pruebas;
los resultados de construcción de Luna alto están solapados y no se suman.

DLL: `C:/Users/juanc/AppData/Local/Temp/omnicore-m9-extension-batch-build/bin/OmniCore.Tests/debug/OmniCore.Tests.dll`.
SHA-256: `28C98E509F7BB1B28FE83545F5520BAC06F51902FA115885E82DA85EAD46C7AF`.

Desde el worktree aislado:

~~~powershell
dotnet C:/Users/juanc/AppData/Local/Temp/omnicore-m9-extension-batch-build/bin/OmniCore.Tests/debug/OmniCore.Tests.dll -method '*WindowsAppContainerSandboxTests.Strong_extension_batch_is_published_as_one_protected_host_action' -method '*WindowsAppContainerSandboxTests.Strong_extension_writer_runs_in_private_copy_and_captures_ignored_output' -method '*ExtensionWriteWorkspaceTests.*' -method '*UserPermissionsTests.*' -method '*ClientTests.*' -parallelMode none
dotnet C:/Users/juanc/AppData/Local/Temp/omnicore-m9-extension-batch-build/bin/OmniCore.Tests/debug/OmniCore.Tests.dll -method '*WindowsAppContainerSandboxTests.Strong_read_only_v1_extension_receives_no_checkout_wide_write_access' -parallelMode none
~~~

Los siete caminos del lote cubren normal, autónomo, Ask/Deny silenciosos,
destino obsoleto, presupuesto y tombstone con lectura lógica, cargo ReplaceFile
y recibo durable. Los otros focos acreditan copia privada/scope, permisos User
frescos desde permissions.yaml aislado, clasificación Core y agrupación del
cliente. Luna bajo revisó resolución bajo lease/fence, fingerprint, revalidación
por destino y envelope protegido. No editó ni recompiló; usó la DLL final.
`git diff --check` no encontró errores; sólo avisos de normalización LF/CRLF.

## Publicación y límites

La integración en main exige la CI del PR asociado para su revisión exacta:
pruebas Windows/Linux, instalación npm Windows/Linux y paquete Linux. La suite
completa se ejecuta allí; estos focos locales no la sustituyen.

- Primer vertical para manifiestos v1, Strong disponible y workspace Git.
  Snapshot: hasta 4096 archivos, 64 MiB, 8192 entradas y profundidad 64; cada
  archivo tiene un máximo de 2 MiB. Propuesta: hasta 64 destinos y 8 MiB de
  postimágenes, con límites agregados de imágenes y sobre protegido de 16 MiB.
- Publica archivos de texto completos mediante reemplazo atómico; requiere
  capacidad de reemplazo completo cuando corresponde. No anuncia normalización
  localizada PatchOnly para este canal. Los directorios padre deben existir.
- Los archivos no rastreados o ignorados existentes no se usan como entradas.
  No se aceptan enlaces/reparse points ni contenido que el aplicador no pueda
  conservar fielmente.
- El supervisor v2 trusted conserva el acceso OS declarado en ADR-0061. No se
  atribuye a sus escrituras OS arbitrarias el confinamiento ni la contabilidad
  de este vertical Strong.
- No acredita paridad general de process.exec, shell.exec, wrappers, procesos
  persistentes, ConPTY, inferencia live ni todos los sistemas operativos. M9
  global y la aceptación manual de M8 permanecen abiertos.

## Historia preservada

El prototipo anterior `53ef5a2` permanece en el historial de
`codex/m9-isolated-process-mutations` como trabajo incompleto. No fue una entrega
aceptada ni se reutilizó el atajo de publicar archivos por separado. El contrato
de dos acciones o recorrido autónomo continuo se consolidó en ADR-0060 el
2026-10-21; esta revisión registra su implementación acotada.
