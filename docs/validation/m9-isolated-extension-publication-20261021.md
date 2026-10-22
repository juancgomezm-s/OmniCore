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
SHA-256 del artefacto original validado antes del commit `1faeaab`:
`28C98E509F7BB1B28FE83545F5520BAC06F51902FA115885E82DA85EAD46C7AF`.
La corrección de CI siguiente recompiló esa ruta; su nuevo hash se registra aparte.

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

## Corrección detectada por CI

La [primera matriz del PR #4](https://github.com/juancgomezm-s/OmniCore/actions/runs/39734417286)
detectó en Linux una fuga de la clave `tool.filesystem.patch.effect_unknown`:
5054 pruebas aprobadas, 35 omitidas y una fallida de 5090. El estado incierto
usaba por error una clave específica de herramienta que no existía.

Luna alto corrigió la proyección con una clave común traducida en es/en/fr/it.
Conserva herramienta, argumentos y tipo de efecto, también en extensiones con
IDs dinámicos y acciones padre, sin convertir incertidumbre en éxito o fallo.
El build incremental terminó sin errores ni advertencias; aprobaron el E2E
afectado (1/1) y los focos de proyección y catálogo de locales (31/31), sin omisiones.

Después de finalizar Luna alto, Luna bajo revisó el diff y ejecutó de forma
independiente el E2E y la teoría de cuatro idiomas: **5/5, sin omisiones ni
hallazgos**. No repitió la suite global ni los 86 focos del lote anterior.

~~~powershell
dotnet C:/Users/juanc/AppData/Local/Temp/omnicore-m9-extension-batch-build/bin/OmniCore.Tests/debug/OmniCore.Tests.dll -method '*ClientTests.Projection_localizes_unknown_effect_for_core_and_dynamic_parent_tools' -method '*CliEndToEndTests.Resolve_lists_and_answers_live_effect_recovery_interaction_then_allows_new_run' -parallelMode none
~~~

SHA-256 de la DLL con la corrección:
`A1D5713D2B23018511D41F726514986AB9357A3CB8EEE0FEC9B7EA004980B1A3`.
La aceptación remota exige una nueva matriz completa sobre la revisión corregida.

Windows completó la misma matriz con 5076 aprobadas, 12 omitidas y dos fallidas
de 5090. Una era la fuga anterior; la otra era una carrera en la fixture del
comando de extensión: el contador HTTP notificaba la segunda solicitud antes
de que su callback encolara el cuerpo, y la prueba accedía al índice 1.

Luna alto añadió una espera de cuerpos capturados, señalada después de encolar,
sin cambiar las aserciones de selección exacta de herramienta, estado terminal
o ausencia del efecto requerido. El build incremental pasó sin advertencias
ni errores y el foco afectado aprobó 1/1. Después, Luna bajo revisó la carrera,
el cierre y la espera acotada, y aprobó **2/2, sin omisiones ni hallazgos**:

~~~powershell
dotnet C:/Users/juanc/AppData/Local/Temp/omnicore-m9-extension-batch-build/bin/OmniCore.Tests/debug/OmniCore.Tests.dll -method '*StdioTransportTests.Native_forced_extension_without_required_tool_effect_is_terminal_failed' -method '*StdioTransportTests.Native_command_ack_cut_recovers_ready_invocation_only_after_explicit_continue' -parallelMode none
~~~

SHA-256 de la DLL final con ambas correcciones:
`C706B47E9AD691A43C74F7192A44CDFA92DE8870268A73F330033136EE2CF017`.
Los workers trabajaron de forma secuencial. No se amplió el timeout de CI ni se
suprimió ninguna de las pruebas fallidas; la nueva matriz vuelve a ejecutarlas.

## Integración con la revisión final de plataforma

La [matriz corregida de `9bef507`](https://github.com/juancgomezm-s/OmniCore/actions/runs/39775150062)
aprobó sus cinco jobs: suites Windows/Linux, instalación y actualización npm en
ambos sistemas y paquete Linux. Windows registró 5082 correctas, 12 omitidas y
cero fallos de 5094; los siete escenarios de publicación Strong se ejecutaron y
aprobaron. Esta evidencia corresponde a ese commit, no a revisiones posteriores.

Antes del merge a main apareció la revisión comprometida `78808c1` de
`extensions-platform`: autorización explícita de contexto, avisos y atribución
v4 de herramientas, cliente headless y contratos de SDK/esquema. Se combina en
el worktree aislado con `9bef507`, conservando las dos líneas de historia. La
aceptación de ese árbol combinado requiere validación focal independiente y una
nueva CI completa; la matriz de `9bef507` no la sustituye.

Luna alto resolvió el conflicto en `OmniServer.WorkspacePanel.cs` conservando
el filtro causal de subacciones M9 y la atribución de herramientas v4. La
proyección conserva también la identidad cuando el efecto queda incierto; la
fixture comprueba su rastro y lo reconcilia antes de iniciar otra operación.
Se corrigió la localización del repositorio en las pruebas de plataforma para
ejecutarlas desde artifacts-path, sin cambiar sus comprobaciones funcionales.

En el árbol combinado: SDK **44/44**, build aislado de solución sin errores ni
advertencias y **29/29** focos de Strong, v4/replay, autorización de contexto y
proyección, sin omisiones. El árbol no incorpora cambios sin commit del checkout
compartido. Los focos de Luna bajo se registran a continuación; no se suman como
pruebas distintas a los resultados de construcción.

La validación independiente aprobó 17 focos de publicación, permisos, contexto,
atribución y replay, y detectó un fallo adicional de la fixture headless: otro
helper todavía buscaba el repositorio desde AppContext y suponía el CLI en el bin
convencional. Ese resultado inicial fue **17 correctas y una fallida**, no una
aceptación completa. El binario de esa pasada tenía hash
`A042C7AE943DB189CADB500EC32413E6C83B364AE0397A7C99A491C9FEAEC07F`.

Finalizado Luna bajo, Luna alto corrigió la búsqueda desde CallerFilePath y el
uso del CLI fresco copiado al output de pruebas. El build incremental aislado
terminó sin errores ni advertencias; el caso headless aprobó 1/1. Después Luna
bajo revisó el ajuste y repitió sólo ese cierre: **1/1, sin omisiones**. Confirmó
los tres pilotos, la alternativa reabierta, contexto autorizado y original intacto.

~~~powershell
dotnet exec C:/Users/juanc/AppData/Local/Temp/omnicore-m9-extension-batch-build/bin/OmniCore.Tests/debug/OmniCore.Tests.dll -method '*StdioTransportTests.Independent_Node_headless_client_accepts_all_three_pilots_and_reopens_the_alternative' -parallelMode none -reporter verbose
~~~

Hash de la DLL tras el cierre headless:
`6A24F4269EE3BDA92915BE5BB4A4FD4D1603BF3565B5AAF2EECE28903178F157`.
Hash del CLI del mismo output:
`DE63903963A0A760EFE872952869B698EDB26A439C7EE317908531810EFCCDA0`.
No se repitieron los 17 focos por cambios exclusivos de fixture; la CI final
verifica la suite completa sobre el árbol que se publica.

La [CI del árbol combinado `5a872d0`](https://github.com/juancgomezm-s/OmniCore/actions/runs/39782689962)
detectó cuatro expectativas antiguas en Linux: checkpoint aún esperado en
Instructions, dos variantes de chat que no separaban las nuevas referencias
de la conversación canónica y schema de escritura esperado en v3. Registró
5075 correctas, 35 omitidas y cuatro fallidas de 5114. No se contó como aceptación.

Luna alto corrigió únicamente tres pruebas y aprobó 11/11 focos, sin omisiones;
el build incremental terminó sin errores ni advertencias. Finalizado alto, Luna
bajo auditó las nuevas aserciones y aprobó los cuatro casos originales, 4/4 sin
omisiones. Exigen el checkpoint íntegro como referencia de usuario, fuera de Core
Instructions y replay crudo, WorkingState fresco, journal append-only y mensajes
canónicos exactos sin duplicación. La prueba de escritura reabre SQLite y exige
atribución BuiltIn, schema v4, metadatos pre/post y replay limpio.

~~~powershell
dotnet C:/Users/juanc/AppData/Local/Temp/omnicore-m9-extension-batch-build/bin/OmniCore.Tests/debug/OmniCore.Tests.dll -method '*CheckpointContinuationRegressionTests.Checkpoint_restored_after_store_reopen_reaches_new_turn_request_with_fresh_working_state' -method '*CliEndToEndTests.Normal_chat_followup_preserves_ordered_messages_once_after_runtime_reopen' -method '*FilesystemWriteToolTests.Write_pipeline_persists_canonical_reconciliation_metadata_and_replays_clean' -parallelMode none
~~~

SHA-256 de la DLL tras corregir las expectativas de contrato:
`6EAD450B1A2FC13DCBAF5EE79E1CA2AD178B0FFF4C0C66C93514E36F0F8574E7`.
Los cambios no alteran producción ni eliminan pruebas. La publicación sigue
condicionada a una nueva matriz íntegra sobre el commit corregido.

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
