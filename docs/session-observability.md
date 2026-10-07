# Observabilidad de sesión para OmniCoder

## Superficie y propiedad

`IOmniClient.Query("sessionObservability:<activitySequence>")` devuelve JSON de
`SessionObservabilitySnapshot` de la sesión activa, o `null` si no hay sesión.
La consulta es de solo lectura: no consulta proveedores, no inicia inferencias y
no incrementa consumo ni secuencias. El cliente debe activar explícitamente su
`SessionId`; `SessionObservabilityProjection` rechaza snapshots de otra sesión,
incluidas identidades anidadas, y limpia indicadores al cambiar de sesión.

`BasedOnJournalSequence` identifica la evidencia canónica leída. `ActivitySequence`
es independiente, creciente dentro de la instancia del host y la sesión. Tras
reconectar/reiniciar el host se debe reiniciar el cursor (activar una nueva
proyección); no es un cursor durable del journal. Los últimos estados de cada
turno sobreviven a la poda del historial transitorio de 1024 transiciones.

## Contexto actual versus consumo

`Context` describe la última petición completada del modelo, **no** el gasto
acumulado ni un contador del estado oculto del proveedor. Mientras se genera,
se conserva esta medición con su fecha; `PendingContext` describe la petición
en curso y `Updating` distingue actualización de medición válida. Al completar
una invocación se sustituye por el input reportado cuando el proveedor publica
ese campo; en su ausencia se estima la forma de la petición con caracteres/4.
La capacidad es la configuración declarada del modelo (estimada), no el budget
efectivo seleccionado. El porcentaje utiliza input/capacidad, sin fingir un
tokenizador exacto. `UsableBudget` expone el límite efectivo por separado.

Las componentes estimadas separan instrucciones, historial, argumentos y
resultados de herramientas, definiciones de herramientas y texto de razonamiento
visible. Los datos opacos y base64 no se tokenizan como texto. Archivos embebidos
en instrucciones/resultados no tienen atribución independiente fiable en la
forma actual de `ModelRequest`: `files` queda Unknown, no cero. Adjuntos opacos
quedan Unknown; sin ellos se declara NotApplicable. El razonamiento de usage
es un subconjunto de salida, **no** una medición de ocupación del contexto.

Tras reiniciar el host se recuperan input y razonamiento de `ModelStepCompleted`,
capacidad y budget de `ModelStepStarted`, y composición estimada del artifact
de contexto cuando existe. Los eventos antiguos sin capacidad no se reinterpretan
usando el budget. La composición recuperada refleja categorías del materializador;
no pretende reconstruir las partes ocultas o definiciones del proveedor.

Cada `Metric<T>` conserva disponibilidad, valor nullable, fuente y `AsOf`.
Reported = valor publicado; Estimated = cálculo o configuración declarada;
Unknown = sin evidencia suficiente; Stale = última lectura válida con error de
actualización; NotApplicable/NotSupported no equivalen a cero.

## Contabilidad idempotente

El journal se reduce por identidad única `(TurnId, StepIndex)` y por
`MetaModelInvocation.InvocationId`. Releerlo no vuelve a cobrar. Un resumen
`ModelCompleted` no se añade si existen pasos del mismo turno. Journals antiguos
usan su artifact de usage solo como fallback. Duplicados idénticos no añaden
gasto; duplicados contradictorios marcan el total como Unknown.

Input incluye caché leída/escrita; output incluye razonamiento. Total = input +
output, sin volver a sumar los desgloses. Anthropic se normaliza añadiendo su
input no cacheado y sus contadores de caché. `Breakdown` indica disponibilidad
por contador: caché ausente es null/Unknown, no un cero reportado. `Tokens`
conserva sumas parciales conocidas, pero no deben presentarse como total completo
si su disponibilidad es Unknown. Un paso iniciado sin usage confirmado hace
desconocidos el total y coste completos, conservando evidencia conocida.

Reintentos que producen una invocación distinta cuentan como pasos distintos;
reintentos internos de transporte sin usage publicado no son consumo observable.
Las herramientas no suman tokens por sí mismas: sus argumentos/resultados
forman parte de la siguiente entrada del modelo. Las compactaciones mediante
MetaModelService registran su propia invocación y usage, incluso si luego fallan.
Todos los Runs/Lanes de la misma sesión se acumulan; otra sesión no se incluye.
Copiar historial a una rama no cobra el ancestro de nuevo; las nuevas peticiones
de esa rama sí cuentan en su propia sesión.

Coste = estimación USD con precios declarados por invocación; no es factura ni
saldo de cuenta. El contrato de precios actual solo tiene input/output, no tarifas
diferenciales de caché. Si falta usage completo o precio de alguna invocación,
coste total = null/Unknown, no cero. No se registra consumo real mediante fixtures.

Los eventos model_step.started/completed y meta_model.invocation_completed/failed
son v2 aditivos, con upcasters de v1: campos nuevos ausentes quedan null.

## Cuotas autenticadas fuera del renderer

`SubscriptionQuotaService` reutiliza el mecanismo de OmniCoder C#
`SubscriptionUsageService`: Codex app-server (`account/read`,
`account/rateLimits/read`) y Claude Code `/usage`, usando el login de sus CLIs.
No extrae credenciales ni realiza prompts de modelo. Cada proceso nuevo tiene
ownership local y límites de tiempo/salida; no se mata ningún proceso existente.
`OmniCliRuntime.RefreshProviderQuotaAsync` hace la consulta host-side y solo la
aplica si la sesión capturada sigue activa. Una consulta del renderer nunca hace
autenticación. La actualización es explícita; polling de snapshots no consulta cuotas.

Se prefiere `rateLimitsByLimitId`; el formato legacy es fallback. Solo se publican
ventanas efectivamente recibidas y duraciones/reinicios numéricos recibidos.
No se inventa una ventana de 5 horas ni semanal. Cuenta CLI se identifica mediante
fingerprint sin email en el wire. Es el origen de la medición: no prueba que sea
la misma cuenta que otro adaptador OAuth seleccionado en Core.

Claude conserva el reset textual cuando no entrega una fecha inequívoca y no
infiere duración ni identidad. Si no hay ventanas utilizables se devuelve Unknown
con explicación. Un error de actualización conserva la lectura previa como Stale,
su `AsOf` original y `LastQueryAttemptAt` de la consulta fallida.

`CreditScope.AccountBalance` y `KeyLimit` son distintos. Codex credits.balance
solo es saldo de cuenta en unidades credits; no equivale a límite de una clave ni
a dinero en una moneda no publicada. No hay adapter implementado para cuotas de
claves API; ese dato no se inventa.

## Actividad para animaciones

### Validez del contexto y del razonamiento reportado

La lectura en vivo y la recuperación del journal reutilizan
`TokenUsageValidation`: slots negativos, caché reportado mayor que input o
razonamiento reportado mayor que output no se presentan como mediciones válidas.
El recibo original no se modifica. En vivo se conserva la estimación del pedido
con disponibilidad `Estimated`; al recuperar sin ese pedido los tokens y el
porcentaje quedan `Unknown`/null. El razonamiento inválido permanece desconocido;
la capacidad declarada del modelo conserva su propia disponibilidad y fuente.

Regresión reproducida: seis fallos en 27 casos antes de la corrección, en lectura
en vivo y recuperación de journal en memoria/JSON (fixtures, no autenticación).
Después: focal conjunta 71 PASS, arquitectura 56 PASS y FULL
`context-validity-full-1117.log`: **2825 casos, 2821 PASS, 0 FAIL, 4 SKIP**,
122.743 s. Build sin errores ni advertencias. Logs en
`C:/Users/juanc/.codex/omni-m5-m55-workers-20261007-0649/`.

```powershell
dotnet build OmniCore.slnx --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noColor -class '*SessionObservabilityTests' -class '*ConversationUsageSafetyTests' -class '*UsagePresentationTests'
```

Los conteos se solapan. La primera ejecución focal de esta ronda arrancó antes
del terminal de build y sólo descubrió los 21 casos anteriores: se conserva
`context-validity-red-tests-1117.log`, pero no se acredita como prueba del cambio.
El RED válido posterior es `context-validity-red-tests-1116.log`, 27 casos/6 FAIL.

Cada transición contiene SessionId, TurnId, RunId/LaneId cuando existen,
secuencia, fecha, fuente y `FirstAnswerTextReceived`; no transporta texto.
WaitingForResponse nace del comienzo de turno/paso; Reasoning de deltas de
razonamiento; AnswerText de deltas de respuesta o texto final no vacío; Tools
del journal de herramientas; WaitingForApproval de interacción. Completed,
Cancelled y Failed se derivan del lifecycle canónico. Fallo del stream es
provisional hasta conocer la terminación durable. Cancelar Run/Lane termina
solo sus actividades. Eventos tardíos no reactivan un turno terminado.

OmniCoder dispone de flags separados para dibujar la O de puntos, segmentos de
razonamiento y cursor de texto; la animación gráfica no se implementa en Core.
La consulta devuelve estados terminales para retirar indicadores aunque la
respuesta haya sido vacía. Cambiar de sesión borra inmediatamente las flags.

## Integración y evidencia reproducible

`OmniCoder.OmniCore` es un módulo WPF net8 real, con ViewModel, panel esencial y
Expander Mostrar más. `MainViewModel.AttachOmniCoreMetrics(client, sessionId)`
conecta el módulo y el ContentControl del panel lateral; el adapter debe llamar
Refresh en el dispatcher y SwitchOmniCoreMetricsSession al cambiar de sesión.
La referencia se habilita compilando OmniCoder con `-p:OmniCoreRoot=<worktree>`.
No se modifica la sección Git. El backend de conversación Pi existente no se
sustituye: este hook debe conectarse al adapter interactivo de Core cuando se
migre esa conversación. No se afirma que la app Pi ya use este nuevo host.

Desde este worktree, ejecutar:

```powershell
./tools/test-session-observability.ps1
./tools/test-session-observability.ps1 -FullSuite -Accounts
```

El runner conserva logs separados y falla si fallan builds/pruebas. El probe
ejecuta Host, SQLite, ExplorerTurn, IOmniClient, serialización Protocol y el
ViewModel/panel WPF compilado del repo OmniCoder. El stream es fixture y así se
identifica; verifica integración real de componentes, no inferencia real.
`-Accounts` consulta las CLIs reales con su login existente, sin inferencia:
valida cuotas disponibles, no consumo de modelos. Los resultados Unknown se
registran sin simular éxito del proveedor.

Lectura real realizada 2026-10-06 05:43:56 UTC: Codex publicó una ventana de 10080
minutos, 18% usado, 82% restante, reset 2026-10-12 16:46:02 UTC, y saldo credits 0
publicado. No publicó una ventana de 5 horas. Claude no publicó ventanas
utilizables en la salida consultada. Es evidencia puntual, no una promesa de
disponibilidad futura ni cualificación de gasto Sol/Luna.

### Verificación ejecutada

- Suite completa: 1517 casos, 1513 PASS, 0 FAIL, 4 SKIP (permisos symlink).
- Suite visual separada con TERM=xterm-256color: 116 PASS.
- Arquitectura: 56 PASS.
- Después de corregir orden de mediciones concurrentes: 21 pruebas de
  SessionObservabilityTests PASS; no se suman a las cifras anteriores por solapamiento.
- OmniCoder.App compilado con el módulo opt-in: 0 errores, 0 warnings.
- La primera ejecución completa sin declarar terminal de color falló en la
  assertion de paleta de una prueba TUI. Se conservó el log; se corrigió el entorno
  del runner, no las assertions, y la suite completa posterior pasó.

Logs de esta ejecución (en la máquina de desarrollo):

- Suite completa e integración WPF: `C:\Users\juanc\AppData\Local\Temp\omni-session-observability-cc9df8cf28f7439fb697a2b624e74eeb`.
- Cuotas autenticadas de solo lectura: `C:\Users\juanc\AppData\Local\Temp\omni-session-observability-d871cc468848403b9310f0052749df08\accounts-read-only.log`.
- QA visual: `C:\Users\juanc\AppData\Local\Temp\omni-conversation-qa-9d875c2a14f34b5e858fc6a1508d4be2`.

Estado: implementación de Core y consumidor WPF verificadas; conexión del hook
al backend principal de conversaciones de OmniCoder pendiente (actualmente Pi).
No se declara cerrada esa migración ni se modifican las secciones Git.
