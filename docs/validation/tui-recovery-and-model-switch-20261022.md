# Recuperación, cambio de modelo y refresco de TUI — 2026-10-22

## Diagnóstico y cambios

- El comando global `omni` apuntaba a un paquete compilado desde `fa16299` (15 de octubre),
  no al `main` integrado `d92e1f0`.
- El seguimiento del final estaba desactivado al construir la TUI. Ahora arranca activado:
  carga el historial completo y muestra su final; la navegación manual hacia arriba conserva
  su posición. Retomar explícitamente una sesión vuelve al final, también tras un resize.
- Se reprodujo una salida del bucle con «hola → seleccionar modelo → Esc»: sin overlay,
  Esc llegaba al `Quit` predeterminado de Terminal.Gui. Ahora cierra el overlay o interrumpe
  la generación; en reposo no sale de la aplicación. `/exit` conserva la salida explícita.
  Esto acredita esa ruta reproducida, no demuestra qué tecla produjo el incidente original.
- La selección de modelos registrados no espera al descubrimiento de red y los resultados
  tardíos no reabren el selector ni sustituyen la selección. Esta protección ya estaba en
  el `main` reciente y no estaba disponible de la misma forma en el paquete antiguo.
- Cada poll releía el historial completo para consumo y para buscar contexto inexistente,
  incluso sin eventos nuevos. El Hub reutiliza esas proyecciones por Session/secuencia,
  sin cachear la actividad ni las cuotas vivas. La suscripción sin novedades comprueba la
  secuencia antes de cargar el historial; un append posterior sigue entregándose.

## Evidencia

Antes de corregir: tres fallos de las cuatro nuevas pruebas de TUI (arranque a 80×25,
arranque a 120×40 y cierre tras Esc). Las dos pruebas de lecturas repetidas también fallaban:
20 polls agregaban 40 lecturas completas en observabilidad y 20 en la suscripción inactiva.

Después de corregir:

- 11 pruebas de TUI aprobadas: recuperación, resize, ocho cambios de modelo, Esc en generación,
  salida explícita, catálogo deliberadamente pendiente/tardío, selección y scroll existentes.
- 104 pruebas de observabilidad, consumo, historial, lifecycle y protocolo aprobadas.
- 16 pruebas del instalador/launcher npm aprobadas (131 comprobaciones en total).
- Build de los proyectos afectados sin advertencias ni errores.
- Frames RGB del driver, con datos sintéticos, en
  `%TEMP%/omni-tui-recovery-frames-20261022/`: final visible a 80×25 y 120×40, y chat vivo
  tras cambiar modelo. No son capturas de una ventana Windows automatizada.
- Resultados XML en `%TEMP%/omni-tui-focal-20261022.xml` y
  `%TEMP%/omni-observation-focal-20261022.xml`.

Medición de lectura de la sesión afectada, sin abrirla como escritor ni enviar inferencias:
71 eventos, 13 bloques, 1129 caracteres de conversación. Treinta consultas calientes por caso:

| Consulta de observabilidad en reposo | Antes | Después |
|---|---:|---:|
| Media por consulta | 2,09 ms | 0,06 ms |
| Asignaciones por consulta | 769266 B | 14164 B |

La reducción es de esa consulta, no una medida de latencia completa del terminal ni del modelo.
Los tests de conteo verifican ausencia de replay completo en reposo, invalidación por append
y separación entre sesiones; no dependen de un umbral temporal frágil.

## Instalación y límites

Se publica desde el worktree aislado `codex/tui-session-recovery`, basado en `d92e1f0`.
No se incluyen los cambios de Companion que otro trabajo está editando en el checkout principal.
El instalador oficial `npm run install:cli` compila, empaqueta y reemplaza el paquete global;
la comprobación compara los hashes de las DLL instaladas con las recién publicadas.
Configuración, credenciales y journals del usuario no forman parte del paquete npm.

Instalación completada y verificada: `omni` conserva su ruta global y carga
`0.1.0+b6ac1f91b429a83af586cf8ce4e8ef25a7120c55`. Las 55 DLL publicadas coinciden
por SHA-256 con las instaladas; `omni --help`, simulación y verificación de un journal
real temporal pasan mediante el launcher instalado. SHA-256 de `omni.dll`:
`a1dfd41210c888a9749e71356347bc7988ff0ba558f4c180fcd225bfa6cef99f`.
Evidencia del smoke en `%TEMP%/omni-installed-tui-smoke-20261022/`.
Respaldo del paquete anterior en
`%TEMP%/omni-installed-backup-20261022/omnicore-cli-0.1.0.tgz`.
El checkout principal, con cambios ajenos en curso, no se sobrescribió ni se fusionó a la fuerza.

La reproducción interactiva usa Terminal.Gui real con driver DOTNET, journal SQLite y CAS
temporales, eventos/teclas reales y proveedor determinista. No acredita una inferencia real
de Luna ni automatización de Windows Terminal; la skill de Windows prohíbe esta última.
No se presenta el CI completo como aprobado: en `d92e1f0`, Linux y los instaladores pasaron,
pero el job Windows terminó por hebras foreground remanentes tras sus nueve regresiones
aprobadas, antes de ejecutar la suite completa. Es un pendiente independiente de esta reparación.

## Corrección de la superposición del panel estrecho

La captura posterior del usuario mostró el panel centrado encima de chat/composer. La primera
instalación no incluía `b1b7090`, que estaba en `codex/tui-openrouter-usability` y no en `main`.
Se integró ese cambio concreto como `8d8c91a`, sin copiar ni fusionar trabajo ajeno sin commit.
El renderer ahora reserva la columna derecha para cualquier panel visible, incluso cuando la
preferencia legacy tiene `mode=overlay`. El ancho sigue limitado a `min(ancho/3, 40)`.

Una prueba adicional con borrador largo detectó que reducir la altura de conversación sin
cambiar su ancho dejaba el último mensaje fuera de la vista. La firma de contenido era idéntica
y se omitía el seguimiento. Ahora una variación de viewport mantiene el final visible cuando
el seguimiento está activo, sin recargar texto ni alterar la posición del lector si está inactivo.

Resultado: 14 pruebas aprobadas, sin omisiones, incluyendo arranque con panel visible persistido
a 64×30, 80×25, 80×40, 89×35, 120×40 y 240×40; modo legacy explícito; F2; maximizar/reducir;
borrador multilínea; reapertura al final; ocho cambios de modelo; Esc y selección de historial.
Los bounds de conversación, composer, header y status se comprueban separados del panel.
La captura RGB de 80×40 se inspeccionó visualmente tras rasterizar las celdas SVG del driver.

Evidencia: `%TEMP%/omni-sidebar-docked-20261022.xml` y
`%TEMP%/omni-sidebar-docked-frames-20261022/`, incluido `docked-sidebar-80x40.png`.
Estos frames usan datos de prueba; no se presentan como capturas de la terminal del usuario.
