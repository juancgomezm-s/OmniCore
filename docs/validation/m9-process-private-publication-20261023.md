# M9-M: procesos y publicación privada (2026-10-23)

Estado: construido y cableado localmente, con aprobación focal condicionada.
ConPTY no está aceptado; no hay CI del candidato ni publicación de este corte
en main. M9 global sigue abierto. Bases: Core de73da7 y OmniCoder 2c31071.

## Recorrido operativo

process.start/read/send_input/stop usan un manager real del Host: streams dúplex,
entrada/EOF, UTF-8 secuenciado y acotado, timeout y control del árbol. Los handles
opacos están ligados a Session/Run/Lane y lease. El journal conserva estados y
propuestas; recuperar un Host no relanza procesos. Una propuesta terminal puede
recuperarse con el writer lease nuevo sin reanimar un proceso vivo anterior.
Los terminales sin propuesta pendiente se reciclan para no agotar una Session.

process.exec y shell.exec comparten ese manager, la copia Strong privada, el
origen Process protegido en CAS y el aplicador de lotes existente. Las escrituras
requieren paths declarados. El modo normal prepara y aplica; con autonomía User
vigente, el finalizador aplica internamente sin otra inferencia para solicitar
apply. Leer salida, consultar y reconectar no publican. Se conservan permisos,
preimagen, prelectura, límites e historial del aplicador compartido. No se deducen
permisos interpretando shell ni se acepta una propuesta por venir en stdout.

Los ejecutables locales salen de la copia declarada. El shell resuelto por Host
puede conservar su ruta del sistema, con cwd y escritura sobre staging. La salida
se sanea antes de obtener secuencia/cursor: prefijos sensibles y VT incompleto
tienen buffers acotados y marcadores explícitos; los prompts sin newline siguen
visibles. No se promete detectar secretos arbitrariamente codificados.

OmniCoder consume eventos y presenta herramientas/salida mediante el adapter
existente. Los E2E recorren ventana Electron, Host .NET y proceso Strong reales.
No entregan todavía una API/panel de control manual de procesos ni los controles
restantes de plan/lanes/worktrees; permanecen en M9-L.

## Correcciones verificadas

- Igualdad de valor de ToolId, antes comparado por referencia en tres predicados.
  El resolver genérico conserva su restricción sobre ejecutables del workspace.
- Decoder Coder acepta el sentinel maxVisibleTools=2147483647 exclusivamente para
  FullAgent, conforme a la validación Core; mantiene rechazos de otros valores.
- Timeout terminal tras confirmar cierre; recuperación segura de propuesta con
  lease nuevo; saneamiento entre lecturas y sincronización mediante ReadAsync.
- Orden de salida: capturar, disponer control/revocar ACL con staging existente y
  después eliminarlo. Una revocación incierta conserva evidencia y Unknown.
- Omitir un grant RX redundante sólo si un directorio de lectura autorizado cubre
  físicamente el directorio del ejecutable. La lease de ACL no se relajó.
- RX preexistente de ALL APPLICATION PACKAGES en directorios del sistema se
  reconoce sólo fuera de LPAC, sin intentar modificar la ACL de System32.
- Fixtures con Electron/Playwright reales, workspace creado antes de usarlo,
  extracción del handle real desde summary y binario del shell declarado para
  ejecutarse desde staging. No se sustituyeron efectos por respuestas de éxito.

## Evidencia conjunta final

Luna alto construyó y probó los focos reparados. Después Luna bajo revisó y
agregó fronteras independientes sobre los mismos binarios, sin reconstruir ni
repetir la suite completa. Son 15 casos conjuntos, no 15 adicionales del revisor.

| Foco | Aprobados |
|---|---:|
| Manager: salida/cursor, saneamiento y propuesta recuperada | 3 |
| Strong: lectura permitida y rechazo de archivo externo | 1 |
| Ask/Deny sin launch, fingerprint, política shell y aislamiento | 5 |
| Consentimiento Weak: preflight y denegación exacta sin launch | 2 |
| API nativa: sentinel FullAgent y negativos de categoría/valores | 1 |
| Electron: sesión Strong, process.exec y shell.exec con markers reales | 3 |

Sin omisiones entre los seleccionados. Los 24 E2E y 14 tests de API fuera de sus
filtros no se cuentan como ejecutados. El proveedor HTTP local acredita cableado,
no inferencia live. No se repitieron los 37 verdes de la matriz anterior ni se
ejecutó una suite integral local. La rama de ACE preexistente de System32 tiene
revisión estática y cobertura indirecta del shell, sin test dedicado.

SHA-256 finales:

- Host: 6065790936568F687ACB5EA49566FEE04AEA37F36A5F25364483A8C227115EF3,
  coincidente en los runtimes CLI y Tests.
- Tests: 497881DEF74CEF287984E660DA7E9D77743172AD5F1131C321532E12E9131F71.
- CLI: 428AB6A1B3B7316AB5E616480C6FA4C1C9B283FAEBB41A15C0D54DE0FDB02381.

Artefactos: %TEMP%/omnicore-m9-completion-build-20261023/bin.
Logs: omnicore-m9-final-focused.trx, omnicore-m9-boundary-final.log,
omnicore-m9-process-exec-consent-final.log, omnicore-m9-native-api-sentinel.log y
omnicore-m9-e2e-final-after-build.log, en %TEMP%. El último E2E conjunto aprobó
3/3 en 24.78 s. git diff --check pasó en la revisión.
El check de huérfanos del bundle pasó; la inspección posterior no encontró
Electron, helpers ni Hosts dotnet de estos workspaces/bundles aún activos.

## Pendientes y límites

ConPTY ejecuta el helper, pero GetConsoleMode devuelve ERROR_INVALID_HANDLE (6)
para stdin/stdout y tamaño 0x0. No se sustituye por pipes ni se elimina la aserción.
LowBoxConsoleEnabled está ausente en este equipo.
[La herramienta oficial de Microsoft](https://github.com/microsoft/SandboxSecurityTools/blob/main/LaunchAppContainer/LaunchAppContainer/LaunchAppContainer.cpp)
habilita ese valor para consolas LowBox normales; su relación causal con este
fallo de ConPTY sigue sin demostrar. La autorización de una prueba temporal con
restauración exacta está pendiente y el registro no se ha cambiado.

Weak persistente se rechaza antes del spawn: falta backend con ownership OS del
árbol para esa ruta. El positivo local corresponde a Windows Strong con pipes.
Linux espera CI del candidato; no se extrapola a Linux/macOS ni a ConPTY.
Continúan M9-L, medios/procedencia, idioma efectivo, SEC-CONV y sus aceptaciones.
M8 permanece abierto para pruebas manuales del usuario.
