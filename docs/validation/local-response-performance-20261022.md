# Respuesta local y rendimiento — 2026-10-22

## Resultado real

Se reabrió la conversación existente `01a130e8-96a1-7509-956c-81e9c6c717d8`
del workspace `C:\WINDOWS\system32`. Se usó `TuiTurnHost.ExecuteAsync`, la misma
ruta de producción de la TUI, con SQLite, CAS, credenciales y proveedor reales.
No se automatizó Windows Terminal ni se sustituyó el proveedor por un fixture.

El modelo `local-qwen / qwen38-27b-abl-v9-best-q4kxl` respondió literalmente
«Prueba de conversación correcta.» en tres turnos consecutivos. Finalizaron con
`EndTurn`, código 0 y ninguna interacción `BudgetExceeded` pendiente:

| Prueba | Tiempo de ejecución | Resultado |
| --- | ---: | --- |
| Conversación original, sin forzar razonamiento | 9,48 s | Respuesta real |
| Misma conversación, YAML original restaurado | 5,39 s | Respuesta real |
| Instalación global actualizada, misma conversación | 4,94 s | Respuesta real |

La segunda medición incluye la autorización real de la ruta local exacta al
restaurar la configuración. La confirmación de la instalación global quedó
registrada en las secuencias 143–149. El proveedor no reportó usage: no se presenta el cero
del resumen legacy como una medición de tokens ni de coste.

Se compararon por SHA-256 los 101 eventos que existían antes de las pruebas:
todos permanecen idénticos. También se verificó el contenido de las 68 referencias
CAS de esos eventos contra sus hashes originales. Sólo se añadieron entradas de
prueba y eventos normales; no se borraron, reescribieron ni redactaron mensajes.
Los dos YAML del usuario se restauraron byte por byte desde sus respaldos,
comprobando antes que no hubiera cambios independientes.

## Diagnóstico del proveedor

Los intentos anteriores acababan a los 120 segundos sin respuesta visible. La
conversación migrada conserva un techo de generación de 120 segundos, separado
del perfil del modelo. Elevar únicamente el perfil a 600 segundos no sustituye
ese techo; no se ampliaron los permisos de la conversación para eludirlo.

La prueba directa al servidor, sin historial ni herramientas, también falló:
HTTP 200, `finish_reason=length`, 64 barras `/`. Por tanto, esta repetición no
dependía del texto de la conversación ni del renderizador de OmniCore.

Se verificó la identidad del proceso y se reinició limpiamente el mismo GGUF
mediante el lanzador existente, conservando TLS, credenciales, contexto y reparto
de GPU, con `specType=none`, `specDraftNMax=0`, `specAuto=false`. Después respondió
directamente en 1,74 s con `finish_reason=stop`, y pasaron los dos turnos reales
descritos arriba. MTP quedó desactivado. Antes del reinicio, tanto argv como el
estado del lanzador ya declaraban MTP desactivado: estos datos demuestran que el
reinicio eliminó el estado defectuoso, pero no permiten atribuir definitivamente
la corrupción a un draft MTP activo o demostrar su desincronización interna.

No se cambiaron los pesos, el binario del servidor ni su catálogo persistente.
El respaldo de estado, catálogo y logs anteriores está en
`C:\temp\OmniLlama\backups\omnicore-local-recovery-20261022-1810`.

La investigación además encontró un defecto independiente en el control explícito
de razonamiento: el adaptador admitía `off` para el perfil `llamaCpp`, pero el
runtime lo rechazaba antes de llegar a él. Se transmite ahora el perfil a todas
las resoluciones iniciales, posteriores y de aprobación. Los endpoints genéricos
siguen rechazando parámetros no representables.

Para ese modo explícito se envían `reasoning_effort: none` y
`chat_template_kwargs: {enable_thinking: false}`. En `/apply-template` del servidor
probado, el primero por sí solo conservaba la apertura de pensamiento; el segundo
la cerraba. Son parámetros nativos documentados por
[llama.cpp](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md),
sin añadir instrucciones ni cambiar mensajes. Los turnos exitosos finales no
necesitaron forzar ese modo ni conservar configuración experimental.

## Optimización del cliente

`RenderConversation` construía una firma concatenando todo el historial en cada
poll y notificación del viewport, antes de decidir si necesitaba dibujar. Esto
duplicaba grandes cantidades de texto aunque nada hubiera cambiado.

Se reemplazó esa firma por una comparación de los campos inmutables que se dibujan:
rol, herramienta y texto. Se conserva una instantánea de referencias para detectar
también reemplazos en listas mutables. Sólo se copia ese array al cambiar contenido;
el texto de los mensajes no se copia durante la comprobación. Se mantienen la
invalidación por ancho, tema/idioma, seguimiento del final y navegación manual.

Microbenchmark Release, .NET 10.0.12, Windows, 100 bloques y 1.638.400 caracteres,
calentamiento previo, `Stopwatch` y `GC.GetAllocatedBytesForCurrentThread`:

| Comprobación de contenido sin cambios | Antes | Después |
| --- | ---: | ---: |
| Tiempo por comprobación, dos muestras | 2,65–3,29 ms | 0,006–0,008 ms |
| Bytes asignados por comprobación | 12,65–12,92 MB | 0 B |

La medición anterior ejecuta 40 comprobaciones del algoritmo original y 10.000 del
actual, comprobando igualdad en todas. Es una medición del detector de cambios,
no una aceleración equivalente de toda la aplicación. El test de regresión hace
1.000 comprobaciones sobre el mismo volumen y exige cero asignaciones; no usa
umbrales temporales dependientes de la carga del equipo.

Mediciones calientes sobre la conversación real, 500 consultas por caso:

| Operación | Tiempo medio | Asignación por consulta |
| --- | ---: | ---: |
| Suscripción sin eventos nuevos | 0,010 ms | 1.521 B |
| Identidad de sesión | 0,001–0,002 ms | 736 B |
| Observabilidad | 0,10–0,14 ms | 38–46 KB |

El runtime sin renderer arrancó en aproximadamente 0,8–1,5 s; su working set fue
77–83 MiB después de las consultas. No son cifras de memoria total del modelo ni
de una TUI completa. Se conservaron las mejoras ya integradas en Main para no
releer journals y consumo en reposo, autocomplete y redibujado de píxeles.

También se dejó abierta la TUI completa en Windows Terminal, con la conversación
original. En una muestra posterior al calentamiento de 15,04 s consumió 1,25 %
de un núcleo (0,078 % de la capacidad total de CPU del equipo), con working set
112,6 MiB y memoria privada 40,1 MiB. La primera muestra, todavía cerca del arranque,
consumió 11,45 % de un núcleo; no se usa esa transición como estado estable.
Son dos muestras locales, no un límite garantizado para todas las cargas.

## Pruebas y reproducción

226 casos únicos aprobados, sin omisiones, combinando las suites de razonamiento,
serialización compatible, plazos de generación, observabilidad, presupuesto
obsoleto, journal canónico, Markdown, protocolo y las regresiones de TUI de scroll,
resize, composer, sidebar y rendimiento. Las pruebas de TUI usan el renderer real
con datos controlados; las tres inferencias locales anteriores usan el modelo real.
También pasaron las 16 pruebas del launcher e instalador npm.

La compilación Release y la publicación pasan sin advertencias. Se actualizaron
también los binarios Debug usados explícitamente por las pruebas stdio: dos casos
fallaban contra el Debug antiguo y ambos pasan después de recompilarlo. No se
eliminaron ni debilitaron sus comprobaciones.

El proyecto usa xUnit v3/v4 con su runner ejecutable. Por ejemplo:

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -c Release
dotnet tests/OmniCore.Tests/bin/Release/net10.0/OmniCore.Tests.dll `
  -class OmniCore.Tests.ConversationRenderPerformanceTests `
  -class OmniCore.Tests.LlamaCppRuntimeReasoningTests `
  -class OmniCore.Tests.OpenAiChatCompatibleMediaSerializationTests `
  -parallelMode none -noColor
```

Evidencia local en `C:\Users\juanc\.codex\omnicore-linux-20261014\`:
`performance-final-20261022.xml`, `performance-tui-focused-20261022.xml`,
`performance-native-debug-updated-20261022.xml`, `performance-core-final-20261022.xml`,
`performance-history-budget-20261022.xml`, `performance-render-20261022.log`,
`performance-candidate-20261022.log` y
`budget-live-probe-20261022/local-original-config-confirmation.log`.
Las pruebas no acreditan una ejecución nueva completa del CI ni Linux. No se
encendió WSL para esta revisión.

## Instalación entregada

Actualizada mediante `npm run install:cli` desde el worktree aislado, sin incluir
los cambios ajenos sin commit del checkout principal. Versión instalada:
`0.1.0+f72f3f86ab72f520a3c6768c7660fb189667e424`. Las 55 DLL del paquete global
coinciden por SHA-256 con las publicadas. La prueba de respuesta de 4,94 s se
ejecutó con esas DLL instaladas; después se retiró el helper de diagnóstico y se
abrió `omni` normalmente en una ventana con toda la TUI.

El paquete anterior se conserva en
`C:\Users\juanc\.codex\omnicore-linux-20261014\installed-before-performance-20261022\omnicore-cli-0.1.0.tgz`.
Evidencia adicional: `performance-install-20261022.log`,
`performance-installer-tests-20261022.log`, `performance-full-tui-steady-20261022.json`
y `budget-live-probe-20261022/installed-local-confirmation.log` en el mismo directorio
de evidencias. MTP y n-gramas automáticos permanecen desactivados en el servidor.

## Decisión sobre Rust y alcance de la medición

No se recomienda reescribir OmniCore en Rust con esta evidencia. La espera fallida
ocurría incluso mediante HTTP directo al motor de inferencia, que ya es C/C++.
Cambiar el lenguaje del cliente no corrige el estado de ese servidor. El coste
evitable del cliente identificado se eliminó en C# sin sustituir la TUI.

Rust podría reducir ciertos costes de memoria o arranque; no se ha realizado una
comparación equivalente que permita prometer una ganancia. Antes de una migración,
correspondería medir escenarios objetivo con herramientas de
[profiling de .NET](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/profilers).
Esta revisión acredita los escenarios y cifras descritos, no una garantía de
optimización universal del 100 % ni de estabilidad indefinida del servidor.
