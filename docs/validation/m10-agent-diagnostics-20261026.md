# M10-F — inspección de ejecución y validación (2026-10-26)

## Entrega

La acción cliente `agent.inspect` abre un diagnóstico de agentes desde la paleta
global Ctrl+P. Puede asignarse en `keybindings.yaml` User; no añade una tecla por
defecto ni sustituye `/agents`, `/agent inspect` o los controles existentes de M6.

La vista consulta exclusivamente el read model `agents` ya publicado por el Host.
Incluye lista alineada, búsqueda literal por objetivo/modelo/IDs, selección con
flechas, Enter para leer el detalle desplazable, actualización manual y retorno
al borrador con Esc. La información lleva una secuencia de captura explícita;
no se presenta como monitor en vivo ni deduce actividad de un worker por su estado.

El detalle presenta primero validación/deudas y separa:

- Resultado declarado por el agente y evaluación registrada del resultado.
- Integración/validación informadas por el Host y cantidad de recibos vinculados.
- Deudas pendientes con su ID, nivel requerido y comprobaciones faltantes literales.
- Identidad Task/Lane/Execution, ambigüedad y ausencia de Execution.
- Capacidad activa/máxima, espera y escritora; ausencia de datos permanece desconocida.
- Supervisión, actividad observada, fuente y último progreso informado.
- Procedencia Session/Run/secuencia y referencias Event/Execution/ToolCall del Host.

Un resultado Succeeded o Accepted no se convierte en verificación. Para presentar
integración verificada se requieren Execution identificada, ausencia conocida de
deudas, ambos indicadores canónicos y recibos vinculados positivos. Identidad
ambigua, datos legacy desconocidos o deuda pendiente impiden esa presentación.
La Lane principal conserva una indicación propia; no se le inventa una evaluación
de integración delegada. El cliente no valida recibos por sí mismo ni resuelve deuda.

Los snapshots se vinculan a Session y Run y exigen una secuencia no anterior a la
captura aceptada o al cursor actual. Datos no disponibles, antiguos, inconsistentes,
IDs duplicados o referencias con ownership contradictorio retiran el detalle
anterior. Un cambio real de Session/Run desactiva actualizar y pide reabrir la vista.
Un fallo de consulta muestra un estado localizado recuperable sin texto interno.

Se muestran hasta 512 Lanes; consultas de hasta 256 caracteres. Detalle acotado a
32 768 caracteres aproximadamente, campos de hasta 2048 caracteres, 64 deudas,
32 comprobaciones por deuda y 12 referencias recientes. Un límite muestra aviso,
sin modificar los datos fuente. Las strings externas, IDs, modelos y checks se
conservan; textos propios y estados conocidos se localizan en es/en/fr/it.

Consultar, filtrar y actualizar no envían commands, no activan proveedores ni
cambian el journal, borrador o adjuntos. Una aprobación pendiente conserva sus
opciones y foco. Los atajos protegidos de interrupción/cancelación siguen usando
el motor y la ruta existentes.

## Coordinación con M9

Implementación en nuevos `OmniCore.Client/AgentInspector*` y
`OmniCore.Cli/Tui/TuiApp.AgentInspector.cs`; conexiones pequeñas en catálogo,
recursos, localización y handler de acciones del teclado. No hay API nueva de
Host/Protocol ni cambios de permisos, admisión, recibos, shell o transporte.

La sesión M9 sigue activa en `m9-completion/OmniCore`: se observaron cambios suyos
en HumanShellProcessTool, OmniServer.SessionSecurity, ToolRuntime y pruebas de
handoff. No se escriben ese checkout, Documents/JayCoder, sus builds o instalaciones.
Este corte usa su propio worktree `openrouter-catalog-grid/OmniCore` y temporales
`omni-m10f-*`. Los cambios preexistentes de main se conservan.

## Validación

Build Release de `OmniCore.slnx`, incluidos los targets net8/net10: 0 advertencias,
0 errores. Ejecución serial de las suites focales sobre sus assemblies:

| Grupo | Correctas | Fallos | Omisiones |
|---|---:|---:|---:|
| Inspector, admisión/proyección de agentes, búsqueda, paleta y motor/configuración/dispatcher de teclado | 119 | 0 | 0 |
| TUI inspector/búsqueda/paleta/keybindings/comandos Host | 54 | 0 | 0 |
| Arquitectura | 62 | 0 | 0 |
| Total focal | 235 | 0 | 0 |

Se verifican captura real del Host en cuatro idiomas, consulta sin commands ni
cambios del journal, foco/scroll/retorno al borrador, atajo User, adjuntos, aprobación
pendiente, actualización conservando selección, vacíos y límites. Las pruebas de
proyección/presentación cubren captura antigua, Session/Run ajenos, referencias de
otra Execution, IDs duplicados, datos incompletos y recuperación de errores; éxito
declarado, deuda/ambigüedad/legacy desconocido y verificación con recibos se separan.

Logs `%TEMP%/omni-m10f-final-{build,client,tui,architecture}.log`. El primer build
intermedio coincidió con una edición dependiente; el siguiente detectó uso del tipo
TextView obsoleto en un helper de test, sustituido por inspección de View. La pasada
final indicada arriba es limpia. No se declara una suite full ni aceptación manual.

`git apply --check` del diff acumulado desde `ffa96af` en los archivos compartidos
con M9 pasa en su checkout actual, sin aplicar cambios. Acredita compatibilidad de
hunks, no integración ni aceptación de sus contratos/pruebas.

La instalación versionada se registra al terminar su verificación.

Capturas RGB del driver real en `%TEMP%/omni-m10f-frames`, escenas
`agent-inspector-{es,en,fr,it}`, 80×25 y 130×38. Rasterización con
`tools/render-tui-frames.cjs`; revisión visual separada de aceptación interactiva
manual del usuario. Las pruebas con Host real cubren consulta/cableado; los estados
adicionales de deuda, supervisión y receipts usan DTOs controlados y no acreditan
una nueva ejecución o verificación real del proveedor.

## Pendientes

Este corte no cierra M10. Memoria multiscoped, descubrimiento de bindings de
extensiones, búsquedas más amplias con lectura explícita, undo, evaluación full,
LSP/parches múltiples y canal de actualización GitHub continúan según el plan.
No modifica los gates ni la resolución de deudas canónicas existentes, ni acepta
los pendientes de M9 en nombre de su sesión.
