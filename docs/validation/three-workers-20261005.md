# Avance Luna / Ling / Qwen — 2026-10-05

Rama de trabajo `codex/omnicore-consolidation-20261004`, base `61d6109`.
Monitor anterior PAUSED; sin push, merge a main ni cualificación remota real.

## Entregas verificadas

- Luna nativo (`gpt-6-luna`) implementó `71e51ce`: contexto ambiental AsyncLocal Run/Task/Lane/Turn/ToolCall, restauración anidada e idempotente; EventStream rellena sólo IDs ausentes y conserva prioridad del payload v1. ExplorerTurn deriva Task de LaneCreated del Run/Lane actual, no del reloj ni de un Task inventado. La prueba usa filesystem.read real con provider scripted y comprueba envelopes tras reabrir SQLite. Incluye compatibilidad sin scope y aislamiento async.
- Ling (`inclusionai/ling-3.1-flash`) entregó borrador de seis Facts, aceptados tras correcciones del coordinador en `1da8373`: constructor InMemory, firma de DomainEvent.Create, codec real en vez de serializer alterno, literales JSON y assertion no tautológica. Se añadió comprobación explícita del otro Run y dos elementos restantes en orden. Pruebas rechazan queue duplicada (incluida cross-Lane), promoción huérfana/duplicada/cross-Lane y preservan colas independientes y prefijo del journal. No cambió producción FollowUp.
- Ling intento01 devolvió HTTP429; intento02 terminó stop con código visible. Uso reportado: 1594 input +1678 output =3272 tokens, reasoning0, coste0. Qwen gratuito `qwen/qwen3.8-27b:free` falló sin entrega por error de transporte/runtime no clasificado; no atribuirle código ni consumo confirmado.
- Qwen local fallback autorizado, mismo alias `qwen38-27b-abl-v9-best-q4kxl`, thinking LOW/no-tools. Preflight TLS health+slots libres; no cambio de servidor/credenciales/configuración. Paquete mínimo para dos Facts de CausationScope. Terminó por `length`, sólo bloque thinking, sin texto/código visible ni herramientas: ninguna entrega aceptada. Pi reportó 854 input, 8192 output, 1 cacheRead, total9047; reasoning aparece0 en usage pese bloque thinking, así que no se interpreta ese campo como prueba de ausencia de razonamiento. Duración aproximada 10m37s, proceso final ausente confirmado. No reintento del mismo paquete, no archivo CausationScopeIsolationTests creado.

## Verificación del coordinador

- Ling final reforzado: 6/6 PASS, 0 errores/omitidos.
- Batería combinada de atribución/replay/compatibilidad: 43/43 PASS.
- Batería ampliada ExplorerTurn/ExecutionScope/FollowUp/envelopes UTC/gasto/ModelStep/ArtifactRefs/lector/RunControl/cuestionarios/Barrier: 217/217 PASS, 0 errores/omitidos. Cifras solapadas; no sumar. Compilación sin errores/advertencias y diff-check limpio.
- Primera suite completa: 1250 casos, 1244 PASS /2 FAIL /4 SKIP. Los fallos fueron cleanup Windows SQLite en WorkspaceJournalReaderTests y smoke CLI que heredaba una Question pendiente de los escenarios M1; no se ocultaron.
- Luna reparó sólo el fixture lector en `86d5ef9`: además de Close y ClearPool exclusivo, dispone la conexión propia antes de borrar DB/WAL/SHM, sin catches que oculten fallos. Coordinador verificó 5/5 focales. El smoke CLI ahora usa workspace distinto para M2–M4, conservando la Question M1 sin resolverla ni bypass; verifica journal después de que ask lo crea, manteniendo assertions exit0 y respuesta-scripted. Luna auditó ese aislamiento read-only; coordinador reprodujo 1/1 PASS.
- Repetición final completa sobre cambios auditados: **1250 casos =1246 PASS /0 FAIL /4 SKIP**, 37s. Los cuatro SKIP corresponden a enlaces simbólicos no permitidos por el entorno Windows, no a pruebas nuevas omitidas. No equivale a TTY real ni cualificación remota.

## Límites

ExecutionScope es un subconjunto de ADR0046§4: ExecutionId/ModelStepId, Source y eliminación del fallback de causation siguen pendientes. No M6 scheduler ni atribución global de todos los paths Host. La Task queda nullable para Lane efímera/legacy sin LaneCreated; no se inventa. Pruebas de replay corrupto insertan envelopes raw deliberadamente, no pasan por validación canónica del escritor.

Los workers externos recibieron paquetes cerrados sin herramientas/escrituras ni historial. Runner nuevo fuera del repo: `C:\Users\juanc\.codex\omni-three-workers-20261005\closed-worker.cjs`; selección exacta, preflight catálogo coste0, provider max_price0 y fallbacks deshabilitados. Qwen local utiliza Pi sin context-files/skills/extensions/prompt-templates/approve y TLS validado. Los cambios preexistentes del checkout principal y el Pi ajeno no se tocaron.

M4 TTY/sesión larga/reinicio OS y M5 cualificación/acceso real continúan abiertos. Este bloque no los cierra.
