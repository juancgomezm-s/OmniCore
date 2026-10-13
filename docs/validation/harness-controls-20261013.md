# Controles del harness y cuotas opcionales — 13 de octubre de 2026

Este bloque amplía el plan de preferencias sin declarar cerrados los hitos principales.
La base es `main` en `29c2c26`; los cambios posteriores se verifican por separado.

## Contrato de cuotas

- `maxOutput` es opcional en el catálogo. El editor acepta un campo vacío y lo
  elimina del YAML; no convierte la ausencia en cero. Los modelos locales
  predeterminados tampoco solicitan un límite de salida de 2048 tokens.
  Descubrir modelos ChatGPT no convierte la capacidad reportada por el proveedor
  en una cuota de salida; una inscripción manual con límite explícito lo conserva.
- Sin `budget.session` o `budget.daily`, el runtime no impone los antiguos
  valores de 5/20 USD. La política inicial de sesión sólo incorpora una cuota
  cuando está declarada. La cualificación no inventa una cuota diaria, pero
  mantiene el presupuesto explícitamente autorizado para su propia prueba.
- Las cuotas de tokens/costo de Run y Task son opcionales. Una delegación sin
  cuota propia ni cuota del padre mantiene ambas dimensiones sin cota local.
  Una cuota declarada por el padre sigue heredándose: omitirla en el hijo no
  permite evadirla. Esto también se aplica al executor externo y a su recuperación.
- Un cero explícito no equivale a ausencia. Las cuotas declaradas conservan la
  admisión conservadora y el registro de consumo/reservas; si su exposición no
  puede comprobarse, se informa la limitación en lugar de excederlas.
- Omitir una cuota no autoriza otra ruta, gasto de una cuenta no autorizada,
  herramientas adicionales, mayor concurrencia ni permisos de escritura.
  Siguen aplicándose turnos, llamadas a herramientas, profundidad, vigencia,
  ownership y límites de contexto/capacidades del proveedor.
- La ausencia del límite en OmniCore no garantiza salida ilimitada del servicio.
  Algunos contratos de API exigen un parámetro de salida; sus restricciones
  permanecen independientes de las cuotas voluntarias del harness.

## Perfiles de modelo

Los controles persistidos se vinculan al proveedor/modelo exactos. Existen
perfiles de observación, parcheado, programación acotada y agente completo;
las opciones permiten restringir herramientas básicas/avanzadas, cambios de
archivos, reescrituras, borrado/movimiento, lectura/versionado/validación previa,
pasos del modelo, llamadas a herramientas y duración de cada generación.

La coordinación requiere una política explícita del modelo fuente y la
autoridad vigente del Run. No se concede por marca o por presumir que un modelo
es «frontera». El deadline cancela la invocación; no promete obligar al proveedor
a revelar su razonamiento ni a terminarlo con una respuesta útil.

El catálogo usa los IDs canónicos `search.text` y `artifact.read`. Las
herramientas básicas y los parches se priorizan antes de opciones avanzadas.
El límite visible efectivo sigue siendo la intersección de las políticas: un
perfil amplio no garantiza que todo quepa en el catálogo de un contexto pequeño.

## Aceptación real Luna → Luna

La prueba opt-in `Configured_Luna_native_managed_worker_waits_for_a_real_message_and_claims_one_root_turn`
pasó el `2026-10-13T17:40:53.5579467Z`, utilizando la suscripción ya configurada,
sin cambiar credenciales ni introducir un límite de salida.

- Worker: invocó `core.agents.mailbox.receive` y recibió un mensaje después de
  **60.0034365 segundos** medidos por reloj monotónico del Host.
- Hizo **dos invocaciones lógicas** del proveedor: espera de herramienta y saludo
  con el identificador de recepción. El resultado quedó publicado de forma durable.
- El principal hizo **una invocación lógica** adicional. El wake quedó
  aceptado/resuelto y no se volvió a reclamar.

La [evidencia publicada](assets/luna-managed-20261013.json) conserva los conteos y
tiempos; los logs locales están en `TestResults/luna-managed-evidence-20261013/`.
El primer intento devolvió un saludo correcto pero no repitió un identificador
que el fixture no había solicitado. Se conservó ese fallo y se corrigió la
instrucción; no se quitó la assertion del identificador.

Esta prueba ejercita el Host y `ExplorerTurn` con un proveedor real. No acredita
que Luna haya creado la delegación mediante una herramienta de spawn ni que la
prueba real recorra todo `RunTurnCoreAsync`. La ruta CLI se valida aparte con
HTTP y procesos controlados. Una continuación bloqueada por capacidad conserva
el wake pendiente y no inicia otra llamada del proveedor; una reclamación
incierta no se reenvía automáticamente.

## Qwen: más tiempo y sin cota de salida

La prueba focal `filesystem.read` ofreció el catálogo real de 13 herramientas y
limitó sus permisos al fixture de lectura, sin añadir herramientas inventadas.
La invocación y el HTTP de Qwen disponen ahora de **60 minutos**, y el caso
completo de dos generaciones admite 120 minutos.

El primer ensayo, con el límite explícito existente de 8192 tokens, terminó sin
llamada de herramienta con `MaxOutputTokens` tras aproximadamente 11 minutos.
Se repitió una sola vez con `--no-output-limit`, conforme a la petición del usuario.
Ese segundo ensayo empezó `2026-10-13T17:17:49.3811015Z` y terminó
`2026-10-13T18:16:27.8149286Z` (**58 minutos y 38 segundos**) con
`provider_kind:ProviderUnavailable;status_code:none`.

La [evidencia del segundo ensayo](assets/qwen-read-no-output-limit-20261013.json)
registra que no hubo llamada de herramienta ni ejecución. No prueba que Qwen
carezca de capacidad, ni certifica que pueda usar el catálogo. No se reinició el
servicio, no se cambiaron modelos/TLS/credenciales y no se encadenaron reintentos
con el mismo prompt después de ese fallo.

## Contexto inicial

La [medición reproducible](initial-context-20261013.md) captura la primera
request de OmniCore, OpenCode `1.18.35`, Pi `1.1.0`, Claude Code `2.1.296` y
Codex CLI `0.153.4` en un proyecto aislado. El contexto
inyectado más los esquemas, excluyendo el mensaje original, suma **1161–1641
tokens de referencia** en los cuatro perfiles de OmniCore, frente a **7213**
en OpenCode Plan, **6916** en Build, **1385** en Pi Base, **14805** en Claude Code
y **10245** en Codex CLI. ChatGPT web no se midió; no se equipara al CLI. Se normalizan los
bloques de texto sin contar sus wrappers JSON como instrucciones. Son conteos con una codificación de
referencia, no uso nativo facturado por Luna/Qwen ni una cifra universal.

## Validación

La regresión general (`TestResults/harness-optional-quotas-final-20261013.trx`)
terminó con **4273 casos / 4262 PASS / 0 FAIL / 11 SKIP**, incluyendo 185 casos
TUI. Siete skips requieren opt-in para proveedores reales; cuatro requieren
permisos de symlink. La aceptación Luna indicada arriba se ejecutó por separado.
No se suman suites solapadas ni se cuentan los skips como aprobación.

La corrección final del descubrimiento de modelos se incorporó después de iniciar
esa suite. El build posterior terminó con cero warnings/errores y el grupo
`ModelCatalogRegistrationTests` + `ModelCatalogPreferencesHostTests` dio
**25/25 PASS** (`TestResults/optional-discovery-final-20261013.trx`), incluyendo
ausencia de cuota al descubrir, eliminación del límite y conservación del valor
explícito existente. No se suma este grupo al total general solapado.
Las capturas del README proceden del driver real de Terminal.Gui y no
son mockups ni capturas del escritorio. Los ocho casos del formulario de controles
verifican cuatro idiomas y dos tamaños; las capturas españolas de la regresión se
revisaron sin recolorear la salida.

El cierre integrado de menús posterior incluye la corrección cultural del ratio y
las pruebas de discovery añadidas: **4276 casos / 4265 PASS / 0 FAIL / 11 SKIP**,
más arquitectura **62/62 PASS**. Véase [la auditoría final](preferences-main-closure-20261013.md);
no se suman sus resultados a los grupos históricos de arriba.
