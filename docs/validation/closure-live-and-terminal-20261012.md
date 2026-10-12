# Cierre de regresión, terminal y modelos reales — 12 de octubre de 2026

Registro de la continuación sobre `4223eb5`, rama `codex/tui-omega-animations`.
Corte final: 12 de octubre de 2026, 14:06 UTC / 08:06 Ciudad de México.
Este documento tiene precedencia sobre las cifras históricas de los informes de preferencias
y aceptación de herramientas. No se modificó ni fusionó `main`; tampoco se modificaron
cuentas, credenciales, TLS, configuración del servidor Qwen o archivos de otras ramas.

## Estado de la campaña

La campaña terminó y sus procesos propios se cerraron. La regresión final pasó sin fallos.
Se probaron los 14 contratos de herramientas con ambos modelos; Qwen conserva fallos.
Ambos reconocieron los 45 comandos y completaron 14 acciones Cliente, con la corrección de
catálogo descrita más abajo. Esto no equivale a certificar todas las ejecuciones CLI.

- Primera regresión integrada de esta continuación: **4194 casos, 4186 PASS, 0 FAIL, 8 SKIP**,
  8 min 56,609 s. Incluye la ampliación de timeout; precede las cinco nuevas pruebas de layout.
- Segunda pasada: **4199 casos, 4190 PASS, 1 FAIL, 8 SKIP**, 9 min 02,196 s. El único fallo
  fue una fixture offline que consultaba la cuota real al probar una sesión privada ausente.
  Se aisló la consulta con una cuota sintética desconocida; la excepción esperada, cero HTTP
  de generación y ausencia de perfil persistido se mantienen.
- **Regresión final: 4199 casos, 4191 PASS, 0 FAIL, 8 SKIP**, 8 min 54,595 s,
  completada el 12 de octubre a las 14:00 UTC / 08:00 Ciudad de México.
- Arquitectura: **62/62 PASS**, 0 omisiones, 5,417 s.
- Lote focal de timeout y layout: **10/10 PASS**, 3,269 s; solapado con la suite posterior.
- Compilación final de tests tras aislar la cuota: **0 errores y 0 advertencias**, 38,01 s.
- Compilación aislada del ejecutor real final: **0 errores y 0 advertencias**, 39,94 s.
- Recompilación posterior de la ayuda `resolve`: **0 errores y 0 advertencias**, 30,27 s.
- Self-test del ejecutor: 15 escenarios, 0 fallos; nueve éxitos de pipeline, cuatro bloqueos
  esperados sin binding/consentimiento y dos controles informativos. No son 15 tools certificadas.
- Control offline de CLI: las 16 formas canónicas son aceptadas; la inyección es rechazada;
  tres consultas reales de ayuda completaron sin modelo.

Las ocho omisiones de la suite son cuatro casos de symlink sin permiso y cuatro pruebas
opt-in de proveedor. Las pruebas reales se ejecutan aparte y sus resultados no se suman a la
suite como casos únicos. Los intentos interrumpidos y builds bloqueados por DLL en uso se
conservan, pero no cuentan como pruebas aprobadas.

## Correcciones de esta continuación

1. Qwen dispone de **35 minutos por generación** en el ejecutor de aceptación y su transporte
   HTTP; los casos de dos generaciones tienen 70 minutos. El número de generaciones permanece
   acotado. El valor de producción predeterminado sigue siendo 300 segundos: no se cambia el
   servidor ni la configuración global para realizar esta campaña.
2. El filtro de seguridad de la fixture `user.ask` admite `description`, `helpText` y `other`
   nulos, como permite el contrato canónico. Conserva los rechazos a preguntas sobre secretos.
   No se relajó `QuestionnaireValidator` ni se sustituyeron herramientas del producto.
3. El botón francés del motor local tenía una traducción que no cabía. Ahora usa «Vérifier
   l’adresse»; la descripción mantiene la explicación de la comprobación TCP.
4. Las filas de acciones y avisos de motores locales ya no se superponen a 80 columnas.
   Se declara el contenido desplazable y se separa el aviso de los botones.
5. La etiqueta italiana del idioma usa el ancho disponible en lugar de 22 columnas fijas.
6. La evidencia de tools separa efecto verificado, fin de respuesta, texto final y recibo
   exacto. El diagnóstico CLI compara campos de la llamada sin volcar valores desconocidos.
   Los códigos de timeout reflejan el plazo efectivo, no el antiguo límite de 90 segundos.
7. La prueba M5 de sesión privada ausente ya no depende de la cuota de la cuenta personal.
   Sólo se inyecta la consulta de cuota de la fixture, sin cambiar la protección del producto.
8. El catálogo suministrado al clasificador omitía la ayuda de `resolve`; ahora incluye el
   recurso existente `interaction.resolve.usage`. No se añade un comando ni una herramienta.

## Aceptación interactiva

Se lanzó la TUI real en una pseudoterminal de 80×24 mediante el ejecutor de terminal y se
enviaron teclas reales. No se automatizó Windows Terminal mediante APIs del escritorio.
Se usaron datos y preferencias temporales independientes del perfil del usuario.

Comprobado: F4, nueve categorías, apertura de General, vista previa es/en/fr/it, Cancelar,
Guardar francés, cierre y reapertura con francés persistido, motores locales, navegación
por teclado y etiqueta francesa corregida. Abrir motores locales no inició servidores ni
probó conexiones. La TUI final se cerró normalmente con `/exit`.

La evidencia incluye salida ANSI/VT de la terminal y frames de celdas del driver. **Los PNG
del driver no son capturas del escritorio**: no acreditan una fuente particular ni la ventana
de Windows Terminal del usuario. Las primeras exportaciones con `NO_COLOR=1` eran ilegibles
por representar Color.None como blanco en SVG; se preservan, pero no se usan como evidencia
visual aprobada. Se generaron después frames con colores explícitos y se inspeccionaron.

- [Motor local en francés, 80×24](assets/preferences-20261012/closure-local-engines-fr-80x24.png).
- [Idioma en italiano, 80×24](assets/preferences-20261012/closure-general-it-80x24.png).

Correcciones visuales y cinco pruebas en el commit `23e284e`.
Timeouts, diagnóstico y aislamiento de pruebas en `6f24819`.
Ayuda del catálogo de aceptación en `918acf7`.

## Herramientas con modelo real

Luna usa la suscripción configurada y Qwen el endpoint local ya existente, sin fallback ni
refresh implícito. Tras el restablecimiento indicado por el usuario, Qwen completó el probe
en 8,75 s, con primer evento a 0,48 s y deadline de 2100 s. Disponibilidad no equivale a
capacidad de usar herramientas.

| Herramienta existente | Luna, última prueba terminada | Qwen, última prueba terminada |
|---|---|---|
| filesystem.read | PASS: efecto y recibo exacto | Efecto correcto; recibo alterado en la continuación |
| filesystem.list | PASS | Efecto correcto; recibo exacto ausente |
| search.text | PASS | Efecto correcto; recibo exacto ausente |
| reference.resolve | PASS | Efecto correcto; recibo exacto ausente |
| filesystem.write | PASS | Argumentos rechazados antes de ejecutar; expectedVersion incorrecto |
| filesystem.patch | PASS | Argumentos fuera de la fixture exacta; no ejecutada |
| artifact.read | PASS | Argumentos fuera de la fixture exacta; no ejecutada |
| plan.propose | PASS, propuesta, no aplicación | Propuesta correcta; recibo exacto ausente |
| mode.propose | PASS, propuesta, no cambio de autoridad | Propuesta correcta; recibo exacto ausente |
| process.exec | PASS, proceso y consentimiento once auditado | Proceso y efecto correctos; recibo exacto ausente |
| shell.exec | PASS, proceso y consentimiento once auditado | Proceso y efecto correctos; recibo exacto ausente |
| user.ask | PASS: tres tipos, Other, publicación/reapertura/respuesta durable y retorno | Primer esquema rechazado; segundo válido, publicado y respondido; falla devolución exacta del marcador |
| core.agents.mailbox.receive | PASS: binding real, entrega Host, ACK único y retorno | Efecto de buzón real completado; marcador exacto ausente en la respuesta |
| worktree.integrate | PASS: repo temporal, bytes, manifest CAS, HEAD/índice preservados y retorno | Tool correcta, argumentos rechazados antes del pipeline |

En el diagnóstico focal Qwen de lectura, la continuación terminó en `EndTurn`, sin nuevas
tools, y describió el contenido real, pero cambió caracteres del recibo. Esto demuestra que
recibió información del resultado; no acredita reproducción fiable de identificadores.
No se redujo la longitud del recibo ni se alteraron hashes, permisos o assertions para aprobarlo.

Los tres casos Host Qwen finalizaron con **0/3 PASS**, sin omisiones: cuestionario e
integración rechazados antes de su efecto, mailbox fallido en el retorno. Un diagnóstico
posterior de `user.ask` produjo un esquema válido (sin errores semánticos, tres tipos de
pregunta), completó publicación/reapertura/respuesta del Host y falló al reproducir su marcador.
Es una segunda generación evaluada, no una corrección ficticia del primer resultado.
En total se observaron efectos correctos de **10 de 14 tools** con Qwen, pero ninguna
completó todavía el criterio entero de su escenario. Un efecto correcto no se renombra como PASS.
El booleano histórico `toolResultReturnedToSameModelCall` de esa prueba sólo se activa tras
comprobar el marcador: `false` no significa que el harness omitió enviar el ToolResult.

El catálogo general ofrece 13 herramientas reales; `worktree.integrate` se prueba con su
coordinador Host y catálogo correspondiente. Mailbox usa selección exacta y el resto de los
casos dirigidos selección automática. Mailbox acredita contrato e integración, **no** una
generación gestionada del hijo ni el despertar automático del principal.

## Comandos con modelo real

Luna: **45/45 nombres interpretados y 14/14 ClientAction ejecutadas por el driver** en una
misma pasada. Los otros 31 resultados son interpretación, no ejecución. En la conversación
CLI, el modelo pidió y ejecutó las **16 variantes permitidas**, con efectos y recibos
verificados; hubo un rechazo corregido. El JSON conserva `incomplete`: no se oculta ese error
ni se presenta una pasada perfecta.

Variantes CLI: help, /tools, /commands, /skills, /memory session, permissions list, trust,
extensions, model list, model policy set/show, sim, verify-journal, explain, gc --dry-run y
audit purge --dry-run. Todos los cambios son sobre datos y repositorios temporales.

Qwen terminó la conversación CLI al alcanzar el techo de **32 generaciones**: dos efectos
CLI correctos (`help`, `/tools`), seis comandos rechazados sin ejecución y ocho no alcanzados.
No produjo la devolución final de recibos. Hubo 30 rechazos de llamadas; **no son 30 comandos
distintos**. Se preserva `Status=failed`, sin prolongar ese ciclo.

| Qwen CLI | Comandos |
|---|---|
| Efecto verificado, sin recibo final | help, /tools |
| Rechazo de argumentos; no ejecutados | /commands, /skills, /memory session, permissions list, trust, extensions |
| No alcanzados por el techo de llamadas | model list, model policy set/show, sim, verify-journal, explain, gc --dry-run, audit purge --dry-run |

La interpretación independiente produjo **44/45 nombres correctos y 14/14 acciones Cliente
ejecutadas**. La única confusión fue `resolve` → `ask`. La revisión mostró que el catálogo
de esta prueba no incluía la descripción de `resolve`; se agregó su recurso real
`interaction.resolve.usage` sin inventar un comando. La repetición produjo **45/45 nombres
correctos y 14/14 acciones Cliente ejecutadas**, en tres generaciones. `resolve` quedó bien
interpretado con sus dos argumentos de ejemplo, pero no se resolvió una interacción real
mediante ese comando. No sustituye los resultados fallidos de ejecución CLI.

Autenticación, purgas reales y operaciones sobre datos personales no se activan por pedir
«certificar todos los comandos». Sus alias y rechazos no prueban sus caminos exitosos.
El [inventario histórico](live-command-tool-acceptance-20261012.md) enumera los 45 nombres;
este registro distingue qué se volvió a ejecutar y qué no.

## Límites y pendientes que no quedan cerrados

- No se certifica todo uso libre posible de cada herramienta ni todos los subcomandos/flags.
- La cadena Luna → worker Qwen → espera de 60 segundos → despertar de Luna sigue sin
  implementación/aceptación de extremo a extremo. Tiene decisiones de autoridad, enrutado
  de worker y continuación descritas en el informe anterior; esta campaña no las inventa.
- No se declara M4 cerrado, ni aceptación desktop, login/logout, purgas reales o GC aplicado.
- Los errores de argumentos y recibos del modelo son hallazgos, no pretexto de sandbox.
  Hubo procesos reales con consentimiento acotado y binding de buzón real donde correspondía.

## Evidencia y reproducción

Rutas relativas al worktree, bajo `TestResults/` ignorado por Git:

- `closure-20261012-resume-full.log` y `closure-20261012-resume-full/*.trx`.
- `closure-20261012-final-full.log` y `closure-20261012-final-full/`: segunda pasada, un fallo.
- `closure-20261012-isolation-full.log` y `closure-20261012-isolation-full/`: pasada final, sin fallos.
- `closure-20261012-final-architecture.log` y su TRX.
- `closure-20261012-layout-timeout3.xml`: diez focales, sin omisiones.
- `closure-20261012-final-build2.log`, `closure-20261012-runner-final-build2.log`.
- `closure-20261012-runner-final-selftest.json` y `closure-20261012-runner-final-cli-selftest.json`.
- `closure-20261012-pty-final-events.json`: acciones y salida de terminal con timestamps.
- `closure-20261012-final-color-frames/`: TXT/SVG/PNG; no capturas desktop.
- `closure-20261012-luna-core.json`, `luna-process.exec.json`, `luna-shell.exec.json`,
  `luna-commands.json`, `luna-native.json` (todos con prefijo `closure-20261012-`).
- `closure-20261012-luna-host.xml` y directorio: mailbox + worktree, **2/2 PASS**.
- `closure-20261012-luna-userask.xml` y directorio: cuestionario durable, **1/1 PASS**.
- `closure-20261012-qwen-35min-probe.json`, `qwen-core.json`, `qwen-read-diagnostic.json`,
  `qwen-process.exec.json`, `qwen-shell.exec.json`, `qwen-native.json`, `qwen-commands.json`
  (mismo prefijo), y `closure-20261012-qwen-host/`.
- `closure-20261012-qwen-questionnaire-diagnostic/` y XML: segundo cuestionario válido,
  efecto durable comprobado y fallo exclusivamente en devolución exacta.
- `closure-20261012-qwen-commands-with-resolve-help.json`: clasificación final 45/45 y
  14/14 ClientAction; `closure-20261012-resolve-help-build.log`: compilación de su ejecutor.

Las rutas de fixtures preservadas y el inicio/fin se registran dentro de cada JSON. No se
guardan credenciales, cabeceras Authorization ni bloques de razonamiento. Los contadores son
invocaciones lógicas; no se infiere un número de solicitudes HTTP ni consumo cuando el
proveedor no informa uso.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore
dotnet test tests/OmniCore.Tests/OmniCore.Tests.csproj --no-build --no-restore --report-xunit-trx --results-directory TestResults/closure-check
dotnet build tools/OmniCoreLiveAcceptance/OmniCoreLiveAcceptance.csproj --no-restore
dotnet tools/OmniCoreLiveAcceptance/bin/Debug/net10.0/OmniCoreLiveAcceptance.dll --self-test
```

Las pruebas reales requieren autorización y opt-in explícito. El runner soporta `--model
luna|qwen --allow-live`; no elegir otro modelo como sustituto. Los tres casos xUnit reales
usan `OMNICORE_LIVE_ACCEPTANCE_USER_ASK`, `OMNICORE_LIVE_ACCEPTANCE_MAILBOX_MODEL` y
`OMNICORE_LIVE_ACCEPTANCE_WORKTREE_MODEL`, además de rutas explícitas de conexión y evidencia.
No construir sobre directorios de salida que esté utilizando una prueba o una TUI activa.
