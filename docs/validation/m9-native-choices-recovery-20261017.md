# M9: elecciones nativas y recuperación explícita

Estado: construido y validado localmente en Core y OmniCoder para la Lane raíz.
La CI de integración del cambio sigue pendiente. M9 y la aceptación manual de
M8 permanecen abiertos.

## Alcance

Permission y PlanApproval viajan desde el diálogo existente de OmniCoder a Main,
SDK y Host stdio de OmniCore. La respuesta es una opción canónica del servidor,
ligada a Session/Run/revisión y protegida por la prueba de acción humana. No
habilita una herramienta directa del renderer. La continuación de Permission
conserva el Turn y la llamada originales; PlanApproval aplica sus cuatro opciones
con los efectos definidos en ADR-0035. Esta entrega cubre la Lane raíz.

La recuperación explícita cubre un ACK Accepted persistido antes de iniciar la
continuación. Consultar/reabrir no ejecuta nada; un command duplicado tampoco
relanza. Un grant, autorización o efecto ya iniciado impiden volver a ejecutar.
El aviso bloqueado permite cerrar y no ofrece un botón de reintento. No es
reconciliación de efectos inciertos ni una garantía general de exactly-once.

## Fallos corregidos y comprobaciones de aceptación

- Validar el fingerprint, perfil, estado del Run y transcript antes del efecto
  aprobado; un cambio de configuración debe detenerse antes de Started.
- Mantener NoClient→Deny por defecto incluso si existe callback de interacción;
  aplazar la Permission exige opt-in explícito del cliente nativo.
- Revalidar el intent efectivo tras resolver el ejecutable; argumentos iguales
  no autorizan cambiar de proceso.
- Anunciar choice/continue/recovery sólo si el cliente negocia ese soporte;
  conservar el cuestionario y catálogo legacy compatibles.
- Distinguir respuestas nativas de PlanApproval de las respuestas legacy y
  evitar avisos de recuperación para continuaciones históricas concluidas.
- Recuperar la admisión original de PlanApproval desde la propuesta aceptada:
  la solicitud puede carecer de TurnId porque se publica después del cierre
  del turno. No tomar una herramienta exitosa posterior como si fuera el plan.
- Mantener visible la incertidumbre real, con UI localizada y sin reejecución.
- Permitir registrar la reevaluación de política desde AwaitingPermission sin
  autorizar la llamada: el estado sigue pendiente hasta PermissionGranted o
  PermissionDenied. El flujo aprobado no repite Requested/Prepared.
- Enlazar el ACK de transporte con el identificador del comando interior antes
  de arrancar su continuación. Ambos identificadores pueden ser distintos;
  ni una consulta ni el duplicado de otro comando deben despertarla.
- Conservar el Turn de Permission y renovar el binding desde la admisión
  canónica actual. Un binding inicial en memoria no puede ocultar el Turn recién
  derivado ni impedir decisiones sucesivas dentro del mismo Run.
- Leer InteractionResolved.OptionId para elecciones. State pertenece al contrato
  de cuestionarios y sigue siendo null para Permission/PlanApproval; rellenarlo
  para ocultar la confusión cambiaría incorrectamente IsQuestionnaire.
- Publicar optionsJson en el evento wire de Permission/PlanApproval, además
  del CSV legacy. El diálogo esperaba JSON pero el mapper no lo enviaba; el
  defecto sólo apareció al probar Electron contra el Host real. La proyección
  nueva contiene únicamente objetos con id canónico y preserva cuestionarios.
- Entregar al modelo el resultado de una denegación. LoadConversation ya
  convertía PermissionDenied en un resultado de error, pero CanRead lo filtraba
  antes. La reconstrucción debe incluirlo sólo para su Run/Lane original, sin
  perder el resultado ni abrir el historial de otra Lane.

## Evidencia

Una compilación aprobada no acredita estos recorridos. La CI de Core `91b07fb` y de
OmniCoder `2b4c0d2` corresponde a la reparación previa y no valida este cambio.

Primer foco sobre `8f7619e`: 12 casos, 3 aprobados, 9 fallidos, cero omisiones.
Pasaron NoClient, cambio de ejecutable efectivo y negociación HELLO. Las pruebas
de interacción agotaron su espera antes de responder; no se contabilizan como
validación de choices/recovery. Se detectó además un error de fixture que exigía
TurnId en PlanApproval y un fallo de correlación equivalente en su recuperación,
corregido en producción en `b4b25b3`. Una reproducción aislada confirmó que
Permission no llegaba a Ask: la fixture enviaba `filesystem.write` sin descubrirla
bajo FullAgent/Discovered, obtenía `UNKNOWN_TOOL` y cerraba el turno. Se corrige
el proveedor de prueba para recorrer `tool.search`; no se amplían los defaults
ni se evita la frontera de herramientas. Ese intento fallido tampoco acredita
el caso de fingerprint. Los resultados nuevos se registrarán antes de aceptar
este bloque.

La fixture también debe declarar un perfil capaz: sin tamaño/contexto declarados,
el harness provisional es Direct con pocas herramientas visibles y tampoco
expone `tool.search`. El proveedor local de estas pruebas declara 100B,
contexto 32768 y contexto recomendado 24000; son hechos de una fixture, no una
cualificación real de un modelo ni un cambio a la configuración del usuario.
Con descubrimiento real se alcanzó Permission y se aceptó su respuesta, pero
la continuación no completó. Se identificó la transición inválida de
PermissionEvaluated desde AwaitingPermission y se corrigió en `5af770a`.
La reevaluación conserva AwaitingPermission; no concede autorización por sí sola.

Sobre producción `95fee74`, el foco final de 13 casos aprobó 12 y falló sólo
por una expectativa incorrecta de cierre del Run en la recuperación. La fixture
mantiene FullAgent con validación post-edición obligatoria y no configura
Build/Test: se debe conservar la deuda y rechazar el cierre, aunque la escritura
ya haya tenido éxito. Corregida esa aserción, el caso aislado aprobó 1/1 con
`PostEditValidationPending`, ausencia de consumo y ausencia de `RunCompleted`.
Son **13 casos únicos validados entre dos ejecuciones**, no una tanda única
13/13. Incluyen permisos allow_once/deny, firma/scope/revisión, duplicado,
reinicio antes de responder con receipt deferred/input_required ya durable,
fingerprint cambiado, ejecutable cambiado, NoClient, máquina de estados,
negociación y las cuatro opciones de PlanApproval.

Logs del foco: `%TEMP%/m9-native-choice-final-focal-95fee-20261017.log` y
`%TEMP%/m9-native-choice-recovery-focused-95fee-20261017.log`.
El reinicio antes de responder se prueba después de confirmar la espera durable;
no acredita la ventana de caída anterior a persistir el resultado de ejecución
del command fuente.

Dos casos adicionales sobre `95fee74` aprobaron 2/2:
`Accepted_plan_approval_choice_recovers_from_new_runtime_and_store` y
`Legacy_plan_approval_input_is_not_offered_as_native_choice_recovery`.
El primero usa continue_planning, cancelación antes del executor y Runtime/Store
nuevos; la continuación explícita crea el Turn siguiente del mismo Run. El
segundo usa `OmniServer.RespondToInteraction` real con reject y comprueba el input
legacy antes de obtener recovery none. La primera ejecución de esos dos casos
falló por una referencia de item de plan inválida en la fixture; se alineó con
P1 canónico y con el modo no interactivo del Host de producción.
Log aprobado: `%TEMP%/m9-native-plan-recovery-legacy-95fee-20261017-r3.log`.
El caso `Two_successive_permission_choices_resume_the_same_run_and_turn_once_each`
aprobó 1/1: dos decisiones y dos efectos, conservando Run/Turn y sin repeticiones.
Build incremental del proyecto de tests: cero advertencias/errores, 6.09 s.
Prueba serial: 4.756 s; log
`%TEMP%/m9-native-sequential-permission-test-final-95fee-20261017.log`.
La cobertura local reúne **16 casos distintos**, validados en focos separados
(13 + 2 + 1). Las repeticiones de allow/deny/restart no se suman otra vez.
Fuentes Core `95fee74`, pruebas `724019f`. Esta cifra no equivale a una suite
completa ni a CI del nuevo cambio.

OmniCoder: `vitest run test/omnicore-native-api.test.ts
test/omnicore-chat-backend-adapter.test.ts` aprobó **18/18** (5 SDK + 13 adapter),
625 ms. Incluye catálogo legacy y cerrar el aviso bloqueado sin enviar un command.
El primer intento tuvo dos respuestas de query mal nombradas en las fixtures;
se corrigieron sólo sus nombres para respetar el wire real y se validaron los
dos archivos. Log: `%TEMP%/m9-coder-native-api-adapter-corrected-20261017.log`.
El cierre de las pruebas Electron se registra más abajo.

El primer recorrido Electron llegó a la interacción durable pero falló antes de
mostrar opciones: el adapter exigía optionsJson y ProtocolMapper sólo enviaba
options CSV. La corrección aditiva se compone en `64fa950`/`d6bac81`; el primer
commit no compiló por una asignación nullable al diccionario de strings y no se
considera validado. El helper final agrega la propiedad sólo para las elecciones.
Sobre `d6bac81`, el build del proyecto de tests y sus referencias Host/CLI aprobó
con cero advertencias/errores (50.26 s). El test
`Native_choices_add_canonical_option_objects_while_legacy_csv_and_questionnaire_projection_remain`
aprobó 1/1 serial, comprobando la proyección real. Logs:
`%TEMP%/m9-native-choice-protocol-mapper-build-d6bac8-20261017.log` y
`%TEMP%/m9-native-choice-protocol-mapper-test-d6bac8-20261017.log`.
La cobertura Core suma así **17 casos distintos**; los 16 del motor conservan la
fuente anterior sin cambios y el caso adicional valida la nueva proyección wire.

Electron Permission/allow_once aprobó 1/1, 5.63 s, con 16 escenarios fuera del
filtro: selección en el diálogo, escritura real y una respuesta de herramienta
por cada llamada canónica de discovery y write. El arnés inicial intentaba
usar un botón dentro de una sección cerrada, no seleccionaba ACT y esperaba un
solo resultado de herramienta pese al descubrimiento real. Se corrigieron esas
expectativas de prueba; no se usó IPC directo para sustituir la UI.
Log: `%TEMP%/m9-coder-native-choice-permission-allow-settled-20261017.log`.

La auditoría del transcript denegado encontró que la primera aserción de Electron
aceptaba sólo el resultado de discovery. Eso ocultaba un defecto: el resultado
PermissionDenied era descartado por LaneConversationScope antes de llegar a la
rama que ya lo convertía a error del modelo. `fbed016` permite esa lectura sólo
en su Run/Lane y deriva el propietario desde el ToolCallId canónico.
Build del proyecto de tests sobre esa fuente: cero advertencias/errores. La
unidad `Permission_denial_is_visible_to_owning_lane_but_not_a_sibling_lane`
aprobó 1/1, incluyendo envelopes sin metadata de ToolCallId; el primer intento
falló por buscar el ID en esa metadata intencionadamente ausente y se corrigió
para leer el payload. Logs finales:
`%TEMP%/m9-lane-permission-denied-test-build-fbed016-r2-20261017.log` y
`%TEMP%/m9-lane-permission-denied-scope-fbed016-r2-20261017.log`.
Core reúne **18 casos distintos** en los focos descritos. La nueva E2E deny
aprobó 1/1 sobre `fbed016`, exigiendo los resultados de search y write, el error
`denied`, el evento toolcall.permission_denied y ausencia de escritura.
Log: `%TEMP%/m9-coder-native-choice-permission-deny-fbed016-20261017.log`.

### Resultado integrado local

- Core: 18 casos únicos en los focos descritos; fuentes finales `fbed016`, tests
  `724019f`, `44c3b1c`, `f44c0a9`. Último build del proyecto de tests y referencias
  Host/CLI: cero advertencias y errores. No se repitió toda la suite local.
- OmniCoder: fuentes `2924e38`; tests `a2c11da` y `91a82f7`. Unidades SDK/adapter
  18/18. Seis opciones Electron únicas validadas: allow_once, deny y las cuatro
  de PlanApproval. La tanda de cinco tuvo dos éxitos y tres expectativas de
  transcript incorrectas; se corrigieron y repitieron sólo esos tres casos.
  Deny se volvió a validar después de corregir su feedback real, sin sumarlo
  como un séptimo escenario nuevo.
- Smoke de primer mensaje/cuestionario: 1/1. Son **siete E2E únicos** entre los
  focos, con Electron, Host y proveedor HTTP local reales; no es una tanda
  única siete de siete ni acredita una cuenta de proveedor.
- Scanner Coder: 146/146/144, sin desajustes. NO_ORPHANS aprobado, también tras
  el último deny. El bundle probado se conserva en packages/app/out; únicamente
  se normalizaron finales de línea/espacios de su HTML antes del E2E.

Logs adicionales: `%TEMP%/m9-coder-native-choice-five-remaining-20261017.log`
(2 PASS/3 FAIL de expectativas),
`%TEMP%/m9-coder-native-choice-three-fixture-assertions-20261017.log` (3 PASS),
`%TEMP%/m9-coder-first-send-questionnaire-smoke-20261017.log` (1 PASS),
`%TEMP%/m9-coder-architecture-final-20261017.log` y
`%TEMP%/m9-coder-no-orphans-final-20261017.log`.

Comandos reproducibles desde Core (el segundo es el rerun efectivamente aprobado
del caso de recuperación; no se presenta como rerun de los 13):

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --parallel none --filter-method '*Accepted_permission_choice_recovers_from_new_runtime_and_store_after_orderly_cancellation*'
```

Desde packages/app de OmniCoder, los focos registrados usan npm run test para
los dos archivos de unidades y npm run test:e2e con el archivo
test/e2e/omnicore-host.e2e.test.ts y filtros por nombre de cada opción. Requieren
OMNICORE_E2E_ENTRY apuntando al omni.dll construido; sin esa variable las
omisiones no acreditan el recorrido. Los filtros y resultados individuales se
conservan en los logs anteriores.

La integración de recuperación validada controla el scheduler antes del
executor, cancela y espera la instancia inicial y abre Runtime/Store nuevos
sobre el mismo SQLite. Acredita ese reinicio ordenado; no equivale a
matar un proceso del sistema operativo después del ACK. Las E2E con proveedor
HTTP local acreditan el cableado, no inferencia con una cuenta real.

## Fuera de este cierre parcial

Permanecen los demás consumidores nativos de M9-L, la aceptación SEC-CONV-01–13
completa, procesos persistentes/ConPTY, paridad de adjuntos en TUI y aceptación
de medios/proveedores/plataformas. El cierre global de M9 no se deduce de este
bloque. La aceptación manual de M8 queda exclusivamente pendiente del usuario.
