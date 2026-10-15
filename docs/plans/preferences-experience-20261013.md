# Experiencia de preferencias e inferencia local de OmniCore

Plan aprobado el 13 de octubre de 2026. Mejorar la experiencia del centro de preferencias tomando la organización de OmniLlama como referencia: categorías que muestran ajustes, formularios explicativos y valores con procedencia. Este plan continúa el trabajo funcional y de traducción de [Preferencias e idiomas](preferences-and-localization.md); no sustituye sus contratos ni declara terminados otros hitos.

## Alcance e integración

Base confirmada de implementación: `2bd2fe448aac33084a9152bfb6416bca0c52c78b`. Trabajo aislado en `preferences-experience-20261013`, sin importar cambios no confirmados de memoria y M8 presentes en el checkout principal. Antes de integrar se revisarán las modificaciones concurrentes y se conservarán sus funciones. El alcance inicial excluía push, cambios de cuentas o credenciales, reinicios de servidores reales e inferencias pagadas.

**Autorización de integración y publicación (2026-10-15):** el usuario indica «Ya integra esta versión de la interfaz con Main y súbela al remoto». Queda autorizada la publicación de la rama y su integración en main, conservando los cambios concurrentes. Siguen fuera de alcance los cambios de cuentas/credenciales, reinicios de servidores reales e inferencias pagadas.

Referencia de producto: `omni_llama/examples/server/webui/src/components/SettingDialog.tsx`, `RuntimeSettings.tsx` y `utils/server-config.ts`, revisados en el checkout local de OmniLlama. Se reinterpreta su experiencia en Terminal.Gui; no se copia React ni se cambia el estilo de la conversación.

## Organización de los ajustes

| Categoría | Contenido |
| --- | --- |
| General y apariencia | Idioma, panel lateral, widgets y animaciones. |
| Cuentas y conexiones | Suscripciones y proveedores API, con estado y acciones distintas. |
| Modelos y perfiles | Catálogo, modelo predeterminado, rutas, capacidades y restricciones por provider y modelo. |
| Inferencia local | Motores, generación, muestreo, penalizaciones y estado del servidor. |
| Herramientas y seguridad | Disponibilidad, reglas de permisos y confianza. |
| Contexto y sesiones | Memoria, contexto, consumo medido y cuotas opcionales. |
| Extensiones y skills | Catálogo, consentimiento y activación. |
| Diagnóstico y mantenimiento | Errores, configuración efectiva y operaciones de mantenimiento. |

Las identidades internas existentes se preservan donde sea posible. La búsqueda debe encontrar ajustes y acciones, no solo categorías. Las funciones todavía sin contrato efectivo no se presentan como editables.

## Reglas de experiencia

La ventana amplia conserva la navegación por categorías y muestra directamente el contenido; no exige abrir una pantalla descriptiva adicional. En una terminal estrecha la navegación sigue siendo accesible sin comprimir los formularios. Tab, Shift+Tab, flechas y Escape tienen un comportamiento predecible. Se mantiene el foco, la selección y el borrador durante búsquedas y cambios de tamaño.

Cada campo muestra nombre comprensible, valor, unidad cuando corresponda, ayuda breve y origen o alcance. Booleanos usan controles de selección; enumeraciones y referencias a modelos usan selectores; listas y mapas tienen filas editables. El texto libre se reserva para valores realmente libres. Los detalles técnicos se despliegan bajo demanda.

Guardar valida y utiliza los contratos Host y revisiones existentes. Cancelar no persiste el borrador. Los errores aparecen junto al formulario sin reemplazarlo; los conflictos concurrentes no sobrescriben cambios externos. Iniciar sesión, probar conexiones y arrancar o detener motores son acciones explícitas separadas del guardado. Cambiar de categoría con un borrador pendiente debe conservarlo o solicitar una decisión, nunca descartarlo silenciosamente.

Toda copia propia se ofrece en español, inglés, francés e italiano. Se conserva el modo ASCII y NO_COLOR y no se modifica la fuente global del terminal para hacer caber campos.

## Fases de ejecución

### P1 Navegación integrada

Eliminar la pantalla descriptiva intermedia en el diseño amplio, conservar navegación lateral y abrir directamente las secciones existentes. Separar contexto y sesiones de diagnóstico. Incorporar términos de ajustes existentes a la búsqueda y garantizar que ninguna búsqueda inicie una inferencia, descubra modelos ni cambie configuración. Añadir pruebas de navegación, cancelación y reflujo con capturas reales del driver.

### P2 Formularios y controles tipados

Reorganizar modelos y políticas en secciones comprensibles, sustituir booleanos y mapas técnicos por controles guiados, unificar ayudas, errores y pies de formulario. Integrar cuentas, herramientas, extensiones y memoria sin sustituir sus fachadas ni interferir con el trabajo concurrente. Las cuotas y límites de salida siguen siendo opcionales. Los perfiles de capacidades no conceden permisos.

### P3 Contrato de inferencia local

Añadir un contrato explícito y persistente de personalizaciones de generación por ruta y modelo, compatible con journals y perfiles existentes. Representar herencia separadamente del valor: no deducirla porque un número coincida con el predeterminado. Enviar únicamente personalizaciones declaradas y soportadas por el adaptador. Consultar defaults publicados solo mediante una operación acotada y compatible; un endpoint OpenAI-compatible no implica compatibilidad con `/props` ni con extensiones de llama.

Primero: temperatura, semilla, Top-K, Top-P, Min-P y penalizaciones compatibles. Después: controles avanzados como DRY, Mirostat, temperatura dinámica u orden de muestreo, únicamente para adaptadores que puedan validarlos y aplicarlos. No se ofrece JSON arbitrario como atajo para eludir contratos, permisos o serialización.

Separar ajustes por solicitud de parámetros de arranque: contexto servido, concurrencia, GPU y caché no son campos de generación. Indicar lectura, configuración guardada, valor efectivo y necesidad de reinicio; motores externos no se reinician desde OmniCore. No se copia la exclusión de razonamiento de OmniLlama si rompe la continuidad del proveedor.

### P4 Verificación e integración

Pruebas focales por bloque y regresión amplia al cerrar cambios coherentes. Matriz es/en/fr/it en 80x25 y 120x35, teclado, errores, conflictos y guardar/reabrir/cancelar. Capturas del driver real, sin simulaciones gráficas. Para inferencia: inspeccionar payloads con fixtures, confirmar omisión de valores heredados y rechazo de parámetros no soportados; pruebas con modelos reales requieren disponibilidad y autorización vigentes y timeouts adecuados al prefill.

Integrar posteriormente sobre main mediante merge normal, revisando cambios concurrentes de M8 y memoria. No reconstruir archivos desde versiones antiguas. Commit por bloque coherente; no sumar suites solapadas como pruebas independientes.

## Criterios de aceptación

- Una categoría amplia muestra su contenido sin una segunda pantalla de presentación.
- Contexto y memoria tienen una categoría propia; diagnóstico no los duplica.
- La búsqueda encuentra ajustes existentes por términos traducidos y conserva navegación usable cuando no hay coincidencias.
- Un borrador no desaparece al navegar, fallar una validación o cambiar el tamaño.
- Campos nuevos de inferencia llegan al adaptador y tienen efecto verificable; los no soportados no se guardan como si fueran funcionales.
- Heredado, personalizado, guardado y efectivo son estados distintos; vacío no introduce cuotas ni cotas ocultas.
- Se conserva selección provider/model exacta, seguridad y propiedad de procesos.

## Estado

Primer bloque de P1 implementado en `codex/preferences-experience-20261013`: apertura directa en diseño amplio, navegación lateral persistente, separación de contexto y diagnóstico, búsqueda por términos de campos existentes y conservación del borrador de idioma. La terminal estrecha mantiene la navegación de índice y formulario para no comprimir los campos. Registro y capturas en [la validación del primer bloque](../validation/preferences-experience-20261013.md).

P1 todavía debe ampliar la búsqueda a resultados de ajustes identificables que permitan enfocar el campo exacto, no únicamente abrir su categoría. P2, P3 y la integración de P4 siguen pendientes. El formulario de Modelos aún abre sus editores existentes; este checkpoint no declara todos los formularios integrados ni incorpora muestreo local.
