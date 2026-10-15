# Primer bloque de experiencia de preferencias

Implementación del [plan de experiencia e inferencia local](../plans/preferences-experience-20261013.md), iniciada el 13 de octubre de 2026, hora de Ciudad de México. Validación del bloque durante la madrugada del 14 de octubre UTC. Base `2bd2fe4`; rama aislada `codex/preferences-experience-20261013`. No se modificó el checkout principal, sus cambios concurrentes de memoria y M8 ni la configuración personal.

## Cambios

En terminales de al menos 100 columnas, F4 abre directamente los controles de la categoría y mantiene la navegación lateral. Cambiar de categoría reemplaza solo el contenido y actualiza el encabezado visible; no abre otra ventana descriptiva. La búsqueda y Ctrl+F conservan el formulario mientras se filtra. Un idioma sin guardar permanece como borrador al cambiar de categoría y se revierte al cerrar sin guardar.

Contexto y sesiones tiene categoría propia con contexto, memoria y consumo medido. Diagnóstico conserva sus operaciones sin duplicar esas entradas. Los títulos, ayudas y la nueva categoría tienen recursos completos es/en/fr/it. La búsqueda reconoce términos de campos ya construidos, como ejecutable y límite de salida; no anuncia temperatura ni otros ajustes de inferencia todavía sin contrato.

General separa procedencia y preferencia guardada, dispone verticalmente sus accesos de interfaz en la presentación amplia y desplaza campos cuando la altura es insuficiente. Una terminal de 80 columnas conserva el flujo estrecho anterior. No se cambió la fuente global ni el diseño de la conversación.

## Pruebas

| Evidencia local en TestResults | Resultado | Alcance |
| --- | --- | --- |
| `preferences-experience-client-final.trx` | 21 PASS, 0 FAIL | Búsqueda, separación de categorías y cobertura de recursos. |
| `preferences-experience-tui-final.trx` | 196 PASS, 0 FAIL | Regresión de TUI anterior al último ajuste de altura y separación de dos botones. No equivale a una nueva regresión completa del repositorio. |
| `preferences-experience-render-final.trx` | 16 PASS, 0 FAIL | Código final: apertura directa en cuatro idiomas, categoría persistente, borrador/cancelación, revisión concurrente, reflujo, teclado, scroll en 100x25 y texto efectivamente dibujado. |

Las 16 pruebas focales están incluidas en el conjunto de TUI; las cifras no se suman. La compilación final de `OmniCore.Tests` terminó con cero errores y cero advertencias. Los logs, TRX y SVG originales permanecen en TestResults del worktree.

Las pruebas iniciales detectaron encabezado desactualizado y superposición de los accesos a panel y animaciones. La comparación del snapshot de idioma también se corrigió para comparar por valor la lista de diagnósticos; la revisión, idioma y estado guardado siguen comprobándose. El helper de navegación ahora toma el idioma efectivo cuando no se declara uno, para verificar correctamente el override de proceso. La revisión visual detectó que una altura explícita de una fila ocultaba texto de botones; el código final restaura su altura natural, los separa y exige ese texto en el buffer dibujado antes de capturar.

## Capturas

PNG derivados de celdas reales del driver Terminal.Gui, con conversación y almacenes fixture. No son capturas de una ventana del escritorio ni mockups. La rasterización conserva contenido y paleta. Se inspeccionaron las versiones española y francesa y se comprobó el dibujo de los accesos en los cuatro idiomas.

- [Español](assets/preferences-experience-20261013/settings-integrated-general-es-120x35.png)
- [English](assets/preferences-experience-20261013/settings-integrated-general-en-120x35.png)
- [Français](assets/preferences-experience-20261013/settings-integrated-general-fr-120x35.png)
- [Italiano](assets/preferences-experience-20261013/settings-integrated-general-it-120x35.png)

## Pendientes

La búsqueda todavía abre categorías, no enfoca cada campo exacto. Los editores especializados de modelos, cuentas y permisos se mantienen y necesitan la integración de formularios de P2. La generación y el muestreo local de P3 no se han implementado. La lectura de defaults publicados, personalizaciones por ruta y distinción entre parámetros de solicitud y arranque requieren contratos y payloads verificables.

El bloque queda en la rama aislada, sin merge ni push. Antes de integrarlo se revisarán los nuevos cambios de main y memoria; no se reemplazarán archivos desde esta base. No hubo inferencias pagadas, cambios de credenciales ni arranques o reinicios de servidores reales.
